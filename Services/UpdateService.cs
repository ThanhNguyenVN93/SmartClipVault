using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SmartClipVault.Services;

public sealed class UpdateService
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/ThanhNguyenVN93/SmartClipVault/releases/latest";

    private const string AllowedApiPrefix =
        "https://api.github.com/repos/ThanhNguyenVN93/SmartClipVault/";

    public const string AllowedAssetPrefix =
        "https://github.com/ThanhNguyenVN93/SmartClipVault/releases/download/";

    private static readonly string[] AllowedAssetRedirectHosts =
    [
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    ];

    private const long MaxUncompressedBytes = 100L * 1024 * 1024; // 100 MB
    private const long MaxZipDownloadBytes = 128L * 1024 * 1024;  // 128 MB
    private const long MaxSha256DownloadBytes = 1L * 1024 * 1024; // 1 MB
    private const long MaxApiResponseBytes = 5L * 1024 * 1024;    // 5 MB
    private const int MaxEntryCount = 10_000;
    private const int MaxRedirects = 5;
    private const int CopyBufferSize = 81920;

    // Set to true only in test/override context to allow http://127.0.0.1 and http://localhost.
    private static volatile bool _allowLocalUrls;
    internal static bool AllowLocalUrls
    {
        get => _allowLocalUrls;
        set => _allowLocalUrls = value;
    }

    public sealed record ReleaseInfo(string Version, string ZipUrl, string Sha256Url);

    // ── URL & redirect validation ───────────────────────────────────────────────

    private static bool IsLoopbackHttpUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && (string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static bool HasTraversalSegments(Uri uri)
    {
        foreach (var rawSegment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var decoded = Uri.UnescapeDataString(rawSegment);
            if (decoded is "." or ".."
                || decoded.Contains('/')
                || decoded.Contains('\\'))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsStrictGitHubAssetUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.Equals(uri.Host, "github.com", StringComparison.Ordinal)
        && uri.AbsoluteUri.StartsWith(AllowedAssetPrefix, StringComparison.Ordinal)
        && !HasTraversalSegments(uri);

    private static bool IsUrlAllowed(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains('\\'))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return IsStrictGitHubAssetUri(uri)
               || (_allowLocalUrls && IsLoopbackHttpUri(uri) && !HasTraversalSegments(uri));
    }

    private static bool IsApiUrlAllowed(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains('\\'))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var isGitHubApi =
            uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment)
            && string.Equals(uri.Host, "api.github.com", StringComparison.Ordinal)
            && uri.AbsoluteUri.StartsWith(AllowedApiPrefix, StringComparison.Ordinal)
            && !HasTraversalSegments(uri);

        return isGitHubApi
               || (_allowLocalUrls && IsLoopbackHttpUri(uri) && !HasTraversalSegments(uri));
    }

    private static bool IsAssetRedirectTargetAllowed(Uri originUri, Uri targetUri)
    {
        if (!string.IsNullOrEmpty(targetUri.UserInfo)
            || !string.IsNullOrEmpty(targetUri.Fragment)
            || HasTraversalSegments(targetUri))
        {
            return false;
        }

        if (_allowLocalUrls && IsLoopbackHttpUri(originUri))
            return IsLoopbackHttpUri(targetUri) || IsStrictGitHubAssetUri(targetUri);

        if (targetUri.Scheme != Uri.UriSchemeHttps || !targetUri.IsDefaultPort)
            return false;

        if (IsStrictGitHubAssetUri(targetUri))
            return true;

        foreach (var host in AllowedAssetRedirectHosts)
        {
            if (string.Equals(targetUri.Host, host, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsApiRedirectTargetAllowed(Uri originUri, Uri targetUri)
    {
        if (!string.IsNullOrEmpty(targetUri.UserInfo)
            || !string.IsNullOrEmpty(targetUri.Fragment)
            || HasTraversalSegments(targetUri))
        {
            return false;
        }

        if (_allowLocalUrls && IsLoopbackHttpUri(originUri))
            return IsLoopbackHttpUri(targetUri);

        return targetUri.Scheme == Uri.UriSchemeHttps
               && targetUri.IsDefaultPort
               && string.Equals(targetUri.Host, "api.github.com", StringComparison.Ordinal);
    }

    internal static HttpClient CreateSafeHttpClient(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartClipVault/1.0");
        return client;
    }

    private static async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        HttpClient client,
        string initialUrl,
        Func<string, bool> isInitialUrlAllowed,
        Func<Uri, Uri, bool> isRedirectAllowed,
        string? acceptHeader,
        CancellationToken cancellationToken)
    {
        if (!isInitialUrlAllowed(initialUrl))
            throw new InvalidOperationException($"Blocked disallowed update URL: '{initialUrl}'.");

        var currentUri = new Uri(initialUrl, UriKind.Absolute);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            if (!string.IsNullOrEmpty(acceptHeader))
                request.Headers.Accept.ParseAdd(acceptHeader);

            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();

                if (hop >= MaxRedirects)
                    throw new InvalidOperationException("Too many HTTP redirects while fetching update.");

                if (location is null)
                    throw new InvalidOperationException("HTTP redirect response did not include a Location header.");

                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!isRedirectAllowed(currentUri, nextUri))
                    throw new InvalidOperationException($"Blocked unsafe HTTP redirect target: '{nextUri}'.");

                currentUri = nextUri;
                continue;
            }

            try
            {
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException("Too many HTTP redirects while fetching update.");
    }

    private static async Task<string> ReadResponseStringBoundedAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            throw new InvalidOperationException($"HTTP response exceeds maximum allowed size ({maxBytes} bytes).");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bytesRead > maxBytes - totalRead)
                    throw new InvalidOperationException($"HTTP response exceeds maximum allowed size ({maxBytes} bytes).");

                totalRead += bytesRead;
                ms.Write(buffer, 0, bytesRead);
            }

            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task DownloadResponseToFileBoundedAsync(
        HttpResponseMessage response,
        string destPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            throw new InvalidOperationException($"Download exceeds maximum allowed size ({maxBytes} bytes).");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            await using (var fileStream = new FileStream(
                destPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                useAsync: true))
            {
                long totalRead = 0;
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (bytesRead > maxBytes - totalRead)
                        throw new InvalidOperationException($"Download exceeds maximum allowed size ({maxBytes} bytes).");

                    totalRead += bytesRead;
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryDeleteFile(destPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    // ── Version helpers ─────────────────────────────────────────────────────────

    public static (int Major, int Minor, int Patch) ParseVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!TryParseVersion(version, out var parsed))
            throw new FormatException($"Invalid version string: '{version}'.");

        return parsed;
    }

    private static bool TryParseVersion(string version, out (int Major, int Minor, int Patch) parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var v = version.TrimStart('v');

        // Strip pre-release suffix (e.g. "1.2.3-rc1" → "1.2.3")
        var dash = v.IndexOf('-');
        if (dash >= 0) v = v[..dash];

        var parts = v.Split('.');
        if (parts.Length < 3
            || !int.TryParse(parts[0], out var major) || major < 0
            || !int.TryParse(parts[1], out var minor) || minor < 0
            || !int.TryParse(parts[2], out var patch) || patch < 0)
        {
            return false;
        }

        parsed = (major, minor, patch);
        return true;
    }

    public static bool IsNewer(string current, string latest)
    {
        var c = ParseVersion(current);
        var l = ParseVersion(latest);

        if (l.Major != c.Major) return l.Major > c.Major;
        if (l.Minor != c.Minor) return l.Minor > c.Minor;
        return l.Patch > c.Patch;
    }

    // ── GitHub release JSON parsing ─────────────────────────────────────────────

    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (root.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.True)
            return null;
        if (root.TryGetProperty("prerelease", out var preEl) && preEl.ValueKind == JsonValueKind.True)
            return null;

        if (!root.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String)
            return null;

        var version = (tagEl.GetString() ?? "").TrimStart('v', 'V');
        if (string.IsNullOrEmpty(version)
            || version.Contains("..")
            || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !TryParseVersion(version, out _))
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assetsEl) || assetsEl.ValueKind != JsonValueKind.Array)
            return null;

        var expectedZip = $"SmartClipVault-v{version}-win-x64.zip";
        string? zipUrl = null;
        string? sha256Url = null;

        foreach (var asset in assetsEl.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
                continue;
            if (!asset.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String
                || !asset.TryGetProperty("browser_download_url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var name = nameEl.GetString() ?? "";
            var url  = urlEl.GetString()  ?? "";

            if (string.Equals(name, expectedZip, StringComparison.Ordinal))
                zipUrl ??= url;
            else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.Ordinal))
                sha256Url ??= url;
        }

        if (zipUrl is null || sha256Url is null)
            return null;
        if (!IsUrlAllowed(zipUrl) || !IsUrlAllowed(sha256Url))
            return null;

        return new ReleaseInfo(version, zipUrl, sha256Url);
    }

    // ── SHA-256 helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a SHA256SUMS.txt file and returns the hex hash for the given asset name,
    /// or null if not found. Handles both "  " (text mode) and " *" (binary mode) separators.
    /// </summary>
    public static string? ParseSha256(string sumsText, string assetName)
    {
        ArgumentNullException.ThrowIfNull(sumsText);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

        foreach (var rawLine in sumsText.Split('\n'))
        {
            var line = rawLine.TrimStart('\uFEFF').TrimEnd('\r', ' ');
            if (line.Length < 66) continue; // 64 hex + 2 separator chars

            var hash = line[..64];
            if (!IsHex64(hash)) continue;

            var sep = line[64..66];
            if (sep != "  " && sep != " *") continue;

            var name = line[66..].Trim();
            if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }

        return null;
    }

    private static bool IsHex64(ReadOnlySpan<char> s)
    {
        if (s.Length != 64) return false;
        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    public static bool VerifySha256(string filePath, string expectedHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHex);

        using var stream = File.OpenRead(filePath);
        var hashBytes = SHA256.HashData(stream);
        var actual = Convert.ToHexString(hashBytes);
        return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    // ── Zip extraction ──────────────────────────────────────────────────────────

    private static string NormalizeDirWithSeparator(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    /// <summary>
    /// Extracts all .exe entries from zipPath into destDir with Zip-Slip protection
    /// and a 100 MB total uncompressed-size limit (enforced on both header declarations
    /// and actual decompressed stream bytes). Cleans up extracted files on failure.
    /// Returns the path of the first .exe extracted.
    /// </summary>
    public static string ExtractExe(string zipPath, string destDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destDir);

        var fullDest = NormalizeDirWithSeparator(destDir);
        using var archive = ZipFile.OpenRead(zipPath);

        if (archive.Entries.Count > MaxEntryCount)
            throw new InvalidOperationException($"Archive exceeds the maximum entry count ({MaxEntryCount}).");

        // First pass: validate every entry path, attributes, and declared sizes before extracting.
        long declaredTotal = 0;
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exeEntries = new List<(ZipArchiveEntry Entry, string DestPath)>();

        foreach (var entry in archive.Entries)
        {
            var candidatePath = ValidateEntryPath(entry.FullName, fullDest, out var isDirectory);

            if (isDirectory)
            {
                if (entry.Length != 0)
                    throw new InvalidOperationException($"Directory entry has non-zero length: '{entry.FullName}'.");
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaxUncompressedBytes - declaredTotal)
                throw new InvalidOperationException(
                    "Archive exceeds the 100 MB maximum uncompressed size limit.");

            declaredTotal += entry.Length;

            // Reject Unix symlink entries (S_IFLNK = 0xA000 in upper 16 bits).
            const int unixSymlinkMask = 0xF000;
            const int unixSymlinkType = 0xA000;
            if (((entry.ExternalAttributes >> 16) & unixSymlinkMask) == unixSymlinkType)
                throw new InvalidOperationException($"Unsafe symlink entry detected: '{entry.FullName}'.");

            if (!entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seenFiles.Add(candidatePath))
                throw new InvalidOperationException($"Duplicate entry path in archive: '{entry.FullName}'.");

            exeEntries.Add((entry, candidatePath));
        }

        if (exeEntries.Count == 0)
            throw new InvalidOperationException("No .exe file found in the zip archive.");

        // Second pass: stream-extract .exe files while counting actual decompressed bytes.
        var destExisted = Directory.Exists(fullDest);
        if (!destExisted)
            Directory.CreateDirectory(fullDest);

        var extractedFiles = new List<string>();
        var createdDirs = new List<string>();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long actualTotalBytes = 0;

            foreach (var (entry, destPath) in exeEntries)
            {
                var parentDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    TrackAndCreateDirectory(parentDir, fullDest, createdDirs);
                }

                extractedFiles.Add(destPath);
                using var src = entry.Open();
                using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                CopyStreamWithLimit(src, dest, MaxUncompressedBytes, ref actualTotalBytes, buffer);
            }

            return exeEntries[0].DestPath;
        }
        catch
        {
            foreach (var file in extractedFiles)
                TryDeleteFile(file);

            for (var i = createdDirs.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (Directory.Exists(createdDirs[i]))
                        Directory.Delete(createdDirs[i], recursive: false);
                }
                catch
                {
                    // best-effort cleanup of empty directories
                }
            }

            if (!destExisted)
                TryDeleteDirectory(fullDest);

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal static void CopyStreamWithLimit(
        Stream src,
        Stream dest,
        long maxTotalBytes,
        ref long actualTotalBytes,
        byte[]? sharedBuffer = null)
    {
        var rented = sharedBuffer is null ? ArrayPool<byte>.Shared.Rent(CopyBufferSize) : null;
        var buffer = sharedBuffer ?? rented!;
        try
        {
            int bytesRead;
            while ((bytesRead = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytesRead > maxTotalBytes - actualTotalBytes)
                    throw new InvalidOperationException(
                        "Archive exceeds the 100 MB maximum uncompressed size limit.");

                actualTotalBytes += bytesRead;
                dest.Write(buffer, 0, bytesRead);
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static void TrackAndCreateDirectory(string targetDir, string rootWithSep, List<string> createdDirs)
    {
        var pending = new Stack<string>();
        var current = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = rootWithSep.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        while (!string.IsNullOrEmpty(current)
               && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase)
               && !Directory.Exists(current))
        {
            pending.Push(current);
            current = Path.GetDirectoryName(current);
        }

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            Directory.CreateDirectory(dir);
            createdDirs.Add(dir);
        }
    }

    private static string ValidateEntryPath(string entryFullName, string fullDestWithSep, out bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(entryFullName)
            || entryFullName.Contains('\0')
            || entryFullName.Contains(':')
            || Path.IsPathRooted(entryFullName))
        {
            throw new InvalidOperationException($"Unsafe entry path: '{entryFullName}'.");
        }

        var normalized = entryFullName.Replace('\\', '/');
        if (normalized.StartsWith('/'))
            throw new InvalidOperationException($"Unsafe entry path: '{entryFullName}'.");

        isDirectory = normalized.EndsWith('/');
        var trimmed = isDirectory ? normalized.TrimEnd('/') : normalized;
        if (string.IsNullOrEmpty(trimmed))
            throw new InvalidOperationException($"Unsafe entry path: '{entryFullName}'.");

        var invalidFileChars = Path.GetInvalidFileNameChars();
        foreach (var segment in trimmed.Split('/'))
        {
            if (string.IsNullOrEmpty(segment)
                || segment == "."
                || segment == ".."
                || segment.IndexOfAny(invalidFileChars) >= 0)
            {
                throw new InvalidOperationException(
                    $"Zip-Slip detected: entry '{entryFullName}' contains an unsafe path segment.");
            }
        }

        // Canonical path containment check with trailing separator as final safety net.
        var candidate = Path.GetFullPath(
            Path.Combine(fullDestWithSep, trimmed.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullDestWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Zip-Slip detected: entry '{entryFullName}' would escape destination directory.");

        return candidate;
    }

    // ── Staged-update application ───────────────────────────────────────────────

    /// <summary>
    /// Renames the running executable to .old, copies stagedExePath over it, then restarts.
    /// Must be called on the UI thread so the WPF shutdown is dispatched correctly.
    /// </summary>
    public static void ApplyStagedUpdate(string stagedExePath)
    {
        var currentExe = GetCurrentExePath();
        if (string.Equals(Path.GetFileName(currentExe), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Self-update is only supported when running from the application executable.");

        ApplyStagedUpdateCore(
            stagedExePath,
            currentExe,
            static psi => Process.Start(psi),
            ShutdownApplication);
    }

    internal static void ApplyStagedUpdateCore(
        string stagedExePath,
        string currentExe,
        Func<ProcessStartInfo, Process?> processStarter,
        Action shutdownAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedExePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentExe);
        ArgumentNullException.ThrowIfNull(processStarter);
        ArgumentNullException.ThrowIfNull(shutdownAction);

        if (!File.Exists(stagedExePath))
            throw new FileNotFoundException("Staged update executable was not found.", stagedExePath);

        if (!File.Exists(currentExe))
            throw new FileNotFoundException("Current executable was not found.", currentExe);

        var backupPath = currentExe + ".old";
        if (File.Exists(backupPath))
        {
            try
            {
                File.Delete(backupPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Previous .old may still be locked by AV or a dying instance; fall back to unique backup name.
                backupPath = $"{currentExe}.{Guid.NewGuid():N}.old";
            }
        }

        File.Move(currentExe, backupPath, overwrite: false);
        try
        {
            File.Copy(stagedExePath, currentExe, overwrite: false);

            var psi = new ProcessStartInfo
            {
                FileName = currentExe,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(currentExe) ?? string.Empty
            };

            using var started = processStarter(psi)
                ?? throw new InvalidOperationException("Failed to launch updated executable.");
        }
        catch
        {
            // Rollback: restore the previous exe so the application remains usable.
            try
            {
                if (File.Exists(backupPath))
                    File.Move(backupPath, currentExe, overwrite: true);
            }
            catch (Exception rollbackEx)
            {
                Trace.TraceError($"Update rollback failed: {rollbackEx}");
            }
            throw;
        }

        shutdownAction();
    }

    private static string GetCurrentExePath()
    {
        if (!string.IsNullOrEmpty(Environment.ProcessPath))
            return Environment.ProcessPath;

        using var currentProcess = Process.GetCurrentProcess();
        return currentProcess.MainModule?.FileName
               ?? throw new InvalidOperationException("Cannot determine running executable path.");
    }

    private static void ShutdownApplication()
    {
        var app = Application.Current;
        if (app is null)
            return;

        if (app.Dispatcher.CheckAccess())
            app.Shutdown();
        else
            app.Dispatcher.Invoke(app.Shutdown);
    }

    internal static void CleanupOldUpdateFiles(string? currentExePath = null)
    {
        try
        {
            var exePath = currentExePath ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
                return;

            var dir = Path.GetDirectoryName(exePath);
            var exeName = Path.GetFileName(exePath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(exeName) || !Directory.Exists(dir))
                return;

            var exactOld = Path.Combine(dir, exeName + ".old");
            TryDeleteFile(exactOld);

            foreach (var file in Directory.EnumerateFiles(dir, exeName + ".*.old", SearchOption.TopDirectoryOnly))
                TryDeleteFile(file);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    internal static void TryDeleteDirectory(string path)
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

    // ── Full async check & stage flows ──────────────────────────────────────────

    public static async Task<ReleaseInfo?> CheckForUpdateAsync(
        string currentVersion,
        HttpClient? httpClient = null,
        string? releasesApiUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        HttpClient? owned = null;
        if (httpClient is null)
            owned = CreateSafeHttpClient(TimeSpan.FromSeconds(30));

        try
        {
            var client = httpClient ?? owned!;
            var url = releasesApiUrl ?? ReleasesApiUrl;

            using var response = await SendWithValidatedRedirectsAsync(
                client,
                url,
                IsApiUrlAllowed,
                IsApiRedirectTargetAllowed,
                "application/vnd.github+json",
                cancellationToken).ConfigureAwait(false);

            var json = await ReadResponseStringBoundedAsync(
                response,
                MaxApiResponseBytes,
                cancellationToken).ConfigureAwait(false);

            var info = ParseRelease(json);
            if (info is null) return null;
            return IsNewer(currentVersion, info.Version) ? info : null;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    public static async Task<string> DownloadAndStageUpdateAsync(
        ReleaseInfo info,
        string tempDir,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDir);

        if (!IsUrlAllowed(info.ZipUrl) || !IsUrlAllowed(info.Sha256Url))
            throw new InvalidOperationException("Blocked disallowed update asset URL.");

        var fullTempDir = NormalizeDirWithSeparator(tempDir);
        Directory.CreateDirectory(fullTempDir);

        var zipUri = new Uri(info.ZipUrl, UriKind.Absolute);
        var zipName = Path.GetFileName(zipUri.LocalPath);
        if (string.IsNullOrWhiteSpace(zipName)
            || zipName is "." or ".."
            || zipName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"Invalid asset filename in URL: '{info.ZipUrl}'.");
        }

        var zipPath = Path.GetFullPath(Path.Combine(fullTempDir, zipName));
        if (!zipPath.StartsWith(fullTempDir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsafe staging file path: '{zipName}'.");

        HttpClient? owned = null;
        if (httpClient is null)
            owned = CreateSafeHttpClient(TimeSpan.FromMinutes(5));

        try
        {
            var client = httpClient ?? owned!;

            using (var zipResponse = await SendWithValidatedRedirectsAsync(
                client,
                info.ZipUrl,
                IsUrlAllowed,
                IsAssetRedirectTargetAllowed,
                null,
                cancellationToken).ConfigureAwait(false))
            {
                await DownloadResponseToFileBoundedAsync(
                    zipResponse,
                    zipPath,
                    MaxZipDownloadBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            string sumsText;
            using (var sumsResponse = await SendWithValidatedRedirectsAsync(
                client,
                info.Sha256Url,
                IsUrlAllowed,
                IsAssetRedirectTargetAllowed,
                null,
                cancellationToken).ConfigureAwait(false))
            {
                sumsText = await ReadResponseStringBoundedAsync(
                    sumsResponse,
                    MaxSha256DownloadBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            var expectedHash = ParseSha256(sumsText, zipName)
                ?? throw new InvalidOperationException($"Hash for '{zipName}' not found in SHA256SUMS.txt.");

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!VerifySha256(zipPath, expectedHash))
                    throw new InvalidOperationException("SHA-256 verification failed — the downloaded file may be corrupted.");

                return ExtractExe(zipPath, fullTempDir);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(zipPath);
            owned?.Dispose();
        }
    }
}
