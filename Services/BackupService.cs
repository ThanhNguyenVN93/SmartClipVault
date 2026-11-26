using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using SmartClipVault.Data;

namespace SmartClipVault.Services;

public class BackupService
{
    private const long MaxExtractBytes = 512L * 1024 * 1024; // 512 MB
    private const int MaxEntryCount = 100_000;
    private const int CopyBufferSize = 81920;

    private readonly DatabaseService _db;
    private readonly string _imageDirectory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

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

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => Export(zipPath, includeImages, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
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
                        foreach (var file in Directory.EnumerateFiles(imgDir, "*", SearchOption.TopDirectoryOnly))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (string.Equals(Path.GetFullPath(file), fullZipPath, StringComparison.OrdinalIgnoreCase))
                                continue;

                            var entryName = "images/" + Path.GetFileName(file);
                            AddFileToArchive(archive, file, entryName, ct);
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

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => Restore(zipPath, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void Restore(string zipPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var fullZipPath = Path.GetFullPath(zipPath);
        if (!File.Exists(fullZipPath))
            throw new FileNotFoundException("Backup archive was not found.", fullZipPath);

        var stagingDir = Path.Combine(Path.GetTempPath(), $"scv_restore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        var stagingBase = NormalizeDirectoryWithSeparator(stagingDir);
        var imgDir = _imageDirectory;
        var imgBase = NormalizeDirectoryWithSeparator(imgDir);

        try
        {
            using var archive = ZipFile.OpenRead(fullZipPath);

            if (archive.Entries.Count > MaxEntryCount)
                throw new InvalidDataException($"Archive contains too many entries ({archive.Entries.Count} > {MaxEntryCount}).");

            long declaredTotalBytes = 0;
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? dbEntry = null;
            string? stagedDbPath = null;
            var imageEntries = new List<(ZipArchiveEntry Entry, string StagedPath, string FinalPath)>();

            // Validate paths, counts, declared lengths, and DB presence before extracting anything
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var normalizedName = ValidateAndNormalizeEntryPath(entry.FullName);
                var stagedPath = ResolveContainedPath(stagingBase, normalizedName.TrimEnd('/'), entry.FullName);

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
                    imageEntries.Add((entry, stagedPath, finalImagePath));
                }
            }

            if (dbEntry is null || stagedDbPath is null)
                throw new InvalidDataException("Backup archive does not contain smartclipvault.db.");

            // Extract into isolated staging directory while counting actual decompressed bytes
            long totalBytesCopied = 0;
            CopyEntryWithLimit(dbEntry, stagedDbPath, ref totalBytesCopied, ct);

            foreach (var (entry, stagedPath, _) in imageEntries)
            {
                ct.ThrowIfCancellationRequested();
                CopyEntryWithLimit(entry, stagedPath, ref totalBytesCopied, ct);
            }

            // Validate and restore database before touching live images
            _db.RestoreFrom(stagedDbPath);

            Directory.CreateDirectory(imgDir);
            foreach (var (_, stagedPath, finalPath) in imageEntries)
            {
                ct.ThrowIfCancellationRequested();
                var parentDir = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);

                File.Copy(stagedPath, finalPath, overwrite: true);
            }
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    private static void AddFileToArchive(ZipArchive archive, string sourceFile, string entryName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = File.GetLastWriteTime(sourceFile);

        using var src = new FileStream(
            sourceFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var dest = entry.Open();
        src.CopyTo(dest);
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

    private static string ValidateAndNormalizeEntryPath(string entryFullName)
    {
        if (string.IsNullOrWhiteSpace(entryFullName))
            throw new InvalidDataException("Archive contains an entry with an empty name.");

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
                segment.IndexOfAny(invalidFileChars) >= 0)
            {
                throw new InvalidDataException($"Unsafe entry path detected: {entryFullName}");
            }
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
}
