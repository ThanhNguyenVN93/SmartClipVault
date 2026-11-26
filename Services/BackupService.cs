using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using SmartClipVault.Data;

namespace SmartClipVault.Services;

public class BackupService : IDisposable
{
    private const long MaxExtractBytes = 512L * 1024 * 1024; // 512 MB
    private const int MaxEntryCount = 100_000;
    private const int CopyBufferSize = 81920;

    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly DatabaseService _db;
    private readonly string _imageDirectory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _disposed;

    public BackupService(DatabaseService db, string? imageDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _imageDirectory = string.IsNullOrWhiteSpace(imageDirectory)
            ? ClipboardMonitorService.ImageDirectory
            : imageDirectory;
    }

    public async Task ExportAsync(string zipPath, bool includeImages = true, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await Task.Run(() => Export(zipPath, includeImages, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _operationGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void Export(string zipPath, bool includeImages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var fullZipPath = Path.GetFullPath(zipPath);
        var tempDir = Path.Combine(Path.GetTempPath(), $"scv_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var tempDb = Path.Combine(tempDir, "smartclipvault.db");
        var tempZip = Path.Combine(tempDir, "backup.zip");
        try
        {
            _db.BackupTo(tempDb);
            ct.ThrowIfCancellationRequested();

            using (var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                AddFileToArchive(archive, tempDb, "smartclipvault.db", ct);

                if (includeImages)
                {
                    var imgDir = _imageDirectory;
                    if (Directory.Exists(imgDir))
                    {
                        try
                        {
                            foreach (var file in Directory.EnumerateFiles(imgDir, "*", SearchOption.TopDirectoryOnly))
                            {
                                ct.ThrowIfCancellationRequested();
                                if (string.Equals(Path.GetFullPath(file), fullZipPath, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var entryName = "images/" + Path.GetFileName(file);
                                TryAddImageToArchive(archive, file, entryName, ct);
                            }
                        }
                        catch (DirectoryNotFoundException)
                        {
                        }
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            var destDir = Path.GetDirectoryName(fullZipPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            File.Move(tempZip, fullZipPath, overwrite: true);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public async Task RestoreAsync(string zipPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await Task.Run(() => Restore(zipPath, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _operationGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void Restore(string zipPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var fullZipPath = Path.GetFullPath(zipPath);
        if (!File.Exists(fullZipPath))
            throw new FileNotFoundException("Backup archive was not found.", fullZipPath);

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"scv_restore_{Guid.NewGuid():N}");
        var extractDir = Path.Combine(stagingRoot, "extract");
        var rollbackDir = Path.Combine(stagingRoot, "rollback");
        var rollbackDbPath = Path.Combine(rollbackDir, "smartclipvault.db");
        var rollbackImagesDir = Path.Combine(rollbackDir, "images");
        Directory.CreateDirectory(extractDir);

        var extractBase = NormalizeDirectoryWithSeparator(extractDir);
        var imgDir = Path.GetFullPath(_imageDirectory);
        var imgBase = NormalizeDirectoryWithSeparator(imgDir);
        var imgBaseNoSlash = imgDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        try
        {
            using var archive = ZipFile.OpenRead(fullZipPath);

            if (archive.Entries.Count > MaxEntryCount)
                throw new InvalidDataException($"Archive contains too many entries ({archive.Entries.Count} > {MaxEntryCount}).");

            long declaredTotalBytes = 0;
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? dbEntry = null;
            string? stagedDbPath = null;
            var imageEntries = new List<(ZipArchiveEntry Entry, string StagedPath, string FinalPath, string RelativePath)>();

            // Validate paths, symlinks, counts, declared lengths, and DB presence before extracting anything
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var normalizedName = ValidateAndNormalizeEntryPath(entry.FullName, entry.ExternalAttributes);
                var stagedPath = ResolveContainedPath(extractBase, normalizedName.TrimEnd('/'), entry.FullName);

                if (normalizedName.EndsWith('/'))
                {
                    if (entry.Length != 0)
                        throw new InvalidDataException($"Directory entry has non-zero length: {entry.FullName}");
                    continue;
                }

                if (!seenPaths.Add(normalizedName))
                    throw new InvalidDataException($"Archive contains duplicate entry: {entry.FullName}");

                if (entry.Length < 0 || entry.Length > MaxExtractBytes - declaredTotalBytes)
                    throw new InvalidDataException("Archive exceeds the maximum allowed restore size (512 MB).");
                declaredTotalBytes += entry.Length;

                if (string.Equals(normalizedName, "smartclipvault.db", StringComparison.OrdinalIgnoreCase))
                {
                    dbEntry = entry;
                    stagedDbPath = stagedPath;
                }
                else if (normalizedName.StartsWith("images/", StringComparison.OrdinalIgnoreCase))
                {
                    var relativeImagePath = normalizedName["images/".Length..];
                    if (string.IsNullOrEmpty(relativeImagePath))
                        continue;

                    var finalImagePath = ResolveContainedPath(imgBase, relativeImagePath, entry.FullName);
                    imageEntries.Add((entry, stagedPath, finalImagePath, relativeImagePath));
                }
            }

            if (dbEntry is null || stagedDbPath is null)
                throw new InvalidDataException("Backup archive does not contain smartclipvault.db.");

            // Extract into isolated staging directory while counting actual decompressed bytes
            long totalBytesCopied = 0;
            CopyEntryWithLimit(dbEntry, stagedDbPath, ref totalBytesCopied, ct);

            foreach (var (entry, stagedPath, _, _) in imageEntries)
            {
                ct.ThrowIfCancellationRequested();
                CopyEntryWithLimit(entry, stagedPath, ref totalBytesCopied, ct);
            }

            // Validate staged backup DB before backing up or modifying live state
            _db.ValidateBackupFile(stagedDbPath);

            // Verify target image directory is not a reparse point before touching live files
            var existingImgDirInfo = new DirectoryInfo(imgDir);
            var hadImageDir = existingImgDirInfo.Exists;
            if (hadImageDir && (existingImgDirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Image directory is a reparse point.");

            // Backup current DB & existing target images for atomic rollback
            Directory.CreateDirectory(rollbackDir);
            _db.BackupTo(rollbackDbPath);

            var existingImageBackups = new List<(string BackupPath, string LivePath)>();
            foreach (var (_, _, finalPath, relativePath) in imageEntries)
            {
                ct.ThrowIfCancellationRequested();
                if (hadImageDir && File.Exists(finalPath))
                {
                    if (PasteHelper.HasReparsePointSubdirectory(finalPath, imgBaseNoSlash))
                        throw new InvalidDataException($"Target image path traverses a reparse point: {finalPath}");

                    var liveInfo = new FileInfo(finalPath);
                    if ((liveInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException($"Target image file is a reparse point: {finalPath}");

                    var backupFile = Path.Combine(rollbackImagesDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    var backupParent = Path.GetDirectoryName(backupFile);
                    if (!string.IsNullOrEmpty(backupParent))
                        Directory.CreateDirectory(backupParent);
                    File.Copy(finalPath, backupFile, overwrite: true);
                    existingImageBackups.Add((backupFile, finalPath));
                }
            }

            // Apply DB + images atomically with rollback on failure
            var dbRestoreAttempted = false;
            var addedImageFiles = new List<string>();
            var overwrittenImageFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                ct.ThrowIfCancellationRequested();

                dbRestoreAttempted = true;
                _db.RestoreFrom(stagedDbPath);

                if (imageEntries.Count > 0)
                    Directory.CreateDirectory(imgDir);

                foreach (var (_, stagedPath, finalPath, _) in imageEntries)
                {
                    ct.ThrowIfCancellationRequested();

                    var parentDir = Path.GetDirectoryName(finalPath);
                    if (!string.IsNullOrEmpty(parentDir))
                        Directory.CreateDirectory(parentDir);

                    if (PasteHelper.HasReparsePointSubdirectory(finalPath, imgBaseNoSlash))
                        throw new InvalidDataException($"Target image path traverses a reparse point: {finalPath}");

                    if (File.Exists(finalPath))
                    {
                        var liveInfo = new FileInfo(finalPath);
                        if ((liveInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException($"Target image file is a reparse point: {finalPath}");
                        overwrittenImageFiles.Add(finalPath);
                    }
                    else
                    {
                        addedImageFiles.Add(finalPath);
                    }

                    File.Copy(stagedPath, finalPath, overwrite: true);
                }
            }
            catch
            {
                foreach (var addedFile in addedImageFiles)
                {
                    TryDeleteFile(addedFile);
                }

                foreach (var (backupFile, liveFile) in existingImageBackups)
                {
                    if (!overwrittenImageFiles.Contains(liveFile))
                        continue;

                    try
                    {
                        if (File.Exists(backupFile))
                        {
                            var parentDir = Path.GetDirectoryName(liveFile);
                            if (!string.IsNullOrEmpty(parentDir))
                                Directory.CreateDirectory(parentDir);
                            File.Copy(backupFile, liveFile, overwrite: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError($"BackupService: failed to rollback image '{liveFile}': {ex.Message}");
                    }
                }

                if (!hadImageDir)
                    TryDeleteEmptyDirectory(imgDir);

                if (dbRestoreAttempted && File.Exists(rollbackDbPath))
                {
                    try
                    {
                        _db.RestoreFrom(rollbackDbPath);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError($"BackupService: failed to rollback database: {ex.Message}");
                    }
                }

                throw;
            }
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static void AddFileToArchive(ZipArchive archive, string sourceFile, string entryName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var src = new FileStream(
            sourceFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var lastWriteTime = File.GetLastWriteTime(sourceFile);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = lastWriteTime;

        using var dest = entry.Open();
        src.CopyTo(dest);
    }

    private static void TryAddImageToArchive(ZipArchive archive, string sourceFile, string entryName, CancellationToken ct)
    {
        try
        {
            AddFileToArchive(archive, sourceFile, entryName, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Trace.TraceWarning($"BackupService: skipping missing image during export: {sourceFile}");
        }
    }

    private static void CopyEntryWithLimit(
        ZipArchiveEntry entry,
        string destPath,
        ref long totalBytesCopied,
        CancellationToken ct)
    {
        var parentDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            using var src = entry.Open();
            using var dest = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            int bytesRead;
            while ((bytesRead = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (bytesRead > MaxExtractBytes - totalBytesCopied)
                    throw new InvalidDataException("Archive exceeds the maximum allowed restore size (512 MB).");

                totalBytesCopied += bytesRead;
                dest.Write(buffer, 0, bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string NormalizeDirectoryWithSeparator(string path)
        => Path.GetFullPath(path)
               .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
           + Path.DirectorySeparatorChar;

    private static string ValidateAndNormalizeEntryPath(string entryFullName, int externalAttributes = 0)
    {
        if (string.IsNullOrWhiteSpace(entryFullName))
            throw new InvalidDataException("Archive contains an entry with an empty name.");

        const uint unixFileTypeMask = 0xF000u;
        const uint unixSymlinkType = 0xA000u;
        if ((((uint)externalAttributes >> 16) & unixFileTypeMask) == unixSymlinkType)
            throw new InvalidDataException($"Archive contains an unsafe symbolic link entry: {entryFullName}");

        if (entryFullName.Contains('\0') ||
            entryFullName.Contains(':') ||
            Path.IsPathRooted(entryFullName))
        {
            throw new InvalidDataException($"Unsafe entry path detected: {entryFullName}");
        }

        var normalized = entryFullName.Replace('\\', '/');
        if (normalized.StartsWith('/'))
            throw new InvalidDataException($"Unsafe entry path detected: {entryFullName}");

        var isDirectory = normalized.EndsWith('/');
        var trimmed = isDirectory ? normalized.TrimEnd('/') : normalized;
        if (string.IsNullOrEmpty(trimmed))
            throw new InvalidDataException($"Unsafe entry path detected: {entryFullName}");

        var invalidFileChars = Path.GetInvalidFileNameChars();
        foreach (var segment in trimmed.Split('/'))
        {
            if (string.IsNullOrEmpty(segment) ||
                segment == "." ||
                segment == ".." ||
                segment.EndsWith('.') ||
                segment.EndsWith(' ') ||
                segment.IndexOfAny(invalidFileChars) >= 0)
            {
                throw new InvalidDataException($"Unsafe entry path detected: {entryFullName}");
            }

            var firstDot = segment.IndexOf('.');
            var baseName = firstDot >= 0 ? segment[..firstDot] : segment;
            if (WindowsReservedDeviceNames.Contains(baseName))
                throw new InvalidDataException($"Archive entry uses a reserved Windows device name: {entryFullName}");
        }

        return normalized;
    }

    private static string ResolveContainedPath(string baseDirWithSep, string relativePath, string originalEntryName)
    {
        var osRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullTarget = Path.GetFullPath(Path.Combine(baseDirWithSep, osRelative));
        if (!fullTarget.StartsWith(baseDirWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe entry path detected: {originalEntryName}");

        return fullTarget;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
                Directory.Delete(path, recursive: false);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _operationGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
