using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SmartClipVault.Data;
using SmartClipVault.Models;
using SmartClipVault.Services;

// ── Minimal self-contained test runner ─────────────────────────────────────────

var tests = new List<(string Name, Action Run)>();

void Add(string name, Action run) => tests.Add((name, run));

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{label}: expected '{expected}', got '{actual}'.");
}

static void AssertThrows<TEx>(Action act, string? messageFragment = null) where TEx : Exception
{
    try { act(); }
    catch (TEx ex)
    {
        if (messageFragment is not null
            && !ex.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase))
            throw new Exception(
                $"Expected {typeof(TEx).Name} containing '{messageFragment}', got: {ex.Message}");
        return;
    }
    throw new Exception($"Expected {typeof(TEx).Name} but no exception was thrown.");
}

// ─── ParseVersion ──────────────────────────────────────────────────────────────

Add("ParseVersion_SimpleSemVer", () =>
{
    var (maj, min, pat) = UpdateService.ParseVersion("1.2.3");
    AssertEqual(1, maj, "major"); AssertEqual(2, min, "minor"); AssertEqual(3, pat, "patch");
});

Add("ParseVersion_WithVPrefix", () =>
{
    var (maj, min, pat) = UpdateService.ParseVersion("v2.10.0");
    AssertEqual(2, maj, "major"); AssertEqual(10, min, "minor"); AssertEqual(0, pat, "patch");
});

Add("ParseVersion_StripsPrereleaseTag", () =>
{
    var (maj, min, pat) = UpdateService.ParseVersion("1.3.0-rc1");
    AssertEqual(1, maj, "major"); AssertEqual(3, min, "minor"); AssertEqual(0, pat, "patch");
});

Add("ParseVersion_InvalidThrows", () =>
    AssertThrows<FormatException>(() => UpdateService.ParseVersion("not-a-version")));

Add("ParseVersion_EmptyThrows", () =>
    AssertThrows<ArgumentException>(() => UpdateService.ParseVersion("")));

// ─── IsNewer ───────────────────────────────────────────────────────────────────

Add("IsNewer_MajorBump", () =>
    Assert(UpdateService.IsNewer("1.0.0", "2.0.0"), "2.0.0 should be newer than 1.0.0"));

Add("IsNewer_MinorBump", () =>
    Assert(UpdateService.IsNewer("1.0.0", "1.1.0"), "1.1.0 should be newer than 1.0.0"));

Add("IsNewer_PatchBump", () =>
    Assert(UpdateService.IsNewer("1.0.0", "1.0.1"), "1.0.1 should be newer than 1.0.0"));

Add("IsNewer_SameVersion", () =>
    Assert(!UpdateService.IsNewer("1.2.3", "1.2.3"), "same version should not be newer"));

Add("IsNewer_OlderLatest", () =>
    Assert(!UpdateService.IsNewer("2.0.0", "1.9.9"), "1.9.9 should not be newer than 2.0.0"));

Add("IsNewer_MajorOverridesMinor", () =>
    Assert(!UpdateService.IsNewer("2.0.0", "1.99.99"), "1.99.99 should not be newer than 2.0.0"));

// ─── ParseRelease ──────────────────────────────────────────────────────────────

static string MakeReleaseJson(
    string tag = "v1.2.3",
    bool draft = false,
    bool prerelease = false,
    string? zipName = null,
    string? zipUrl = null,
    bool includeSha256 = true)
{
    var version = tag.TrimStart('v');
    var resolvedZipName = zipName ?? $"SmartClipVault-v{version}-win-x64.zip";
    var resolvedZipUrl  = zipUrl  ?? $"{UpdateService.AllowedAssetPrefix}{tag}/{resolvedZipName}";
    var sha256Url = $"{UpdateService.AllowedAssetPrefix}{tag}/SHA256SUMS.txt";

    var assets = new StringBuilder();
    assets.Append($"{{\"name\":\"{resolvedZipName}\",\"browser_download_url\":\"{resolvedZipUrl}\"}}");
    if (includeSha256)
        assets.Append($",{{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"{sha256Url}\"}}");

    return $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [{{assets}}]
        }
        """;
}

Add("ParseRelease_ValidRelease", () =>
{
    var json = MakeReleaseJson();
    var info = UpdateService.ParseRelease(json);
    Assert(info is not null, "should parse valid release");
    AssertEqual("1.2.3", info!.Version, "version");
    Assert(info.ZipUrl.StartsWith(UpdateService.AllowedAssetPrefix, StringComparison.Ordinal), "zip URL prefix");
    Assert(info.Sha256Url.Contains("SHA256SUMS.txt", StringComparison.Ordinal), "sha256 URL");
});

Add("ParseRelease_Draft_ReturnsNull", () =>
{
    var info = UpdateService.ParseRelease(MakeReleaseJson(draft: true));
    Assert(info is null, "draft release should return null");
});

Add("ParseRelease_Prerelease_ReturnsNull", () =>
{
    var info = UpdateService.ParseRelease(MakeReleaseJson(prerelease: true));
    Assert(info is null, "prerelease should return null");
});

Add("ParseRelease_MissingZipAsset_ReturnsNull", () =>
{
    var info = UpdateService.ParseRelease(MakeReleaseJson(zipName: "WrongName.zip"));
    Assert(info is null, "missing zip asset should return null");
});

Add("ParseRelease_MissingSha256Asset_ReturnsNull", () =>
{
    var info = UpdateService.ParseRelease(MakeReleaseJson(includeSha256: false));
    Assert(info is null, "missing SHA256SUMS.txt should return null");
});

Add("ParseRelease_DisallowedUrl_ReturnsNull", () =>
{
    UpdateService.AllowLocalUrls = false;
    var json = MakeReleaseJson(zipUrl: "https://evil.example.com/evil.zip");
    var info = UpdateService.ParseRelease(json);
    Assert(info is null, "disallowed URL should return null");
});

Add("ParseRelease_LocalhostAllowedInTestMode", () =>
{
    UpdateService.AllowLocalUrls = true;
    try
    {
        var json = $$"""
            {
              "tag_name": "v9.9.9",
              "draft": false,
              "prerelease": false,
              "assets": [
                {"name":"SmartClipVault-v9.9.9-win-x64.zip","browser_download_url":"http://127.0.0.1:8080/SmartClipVault-v9.9.9-win-x64.zip"},
                {"name":"SHA256SUMS.txt","browser_download_url":"http://localhost:8080/SHA256SUMS.txt"}
              ]
            }
            """;
        var info = UpdateService.ParseRelease(json);
        Assert(info is not null, "localhost URL should be allowed in test mode");
        AssertEqual("9.9.9", info!.Version, "version");
    }
    finally
    {
        UpdateService.AllowLocalUrls = false;
    }
});

// ─── ParseSha256 ───────────────────────────────────────────────────────────────

Add("ParseSha256_TwoSpaceSeparator", () =>
{
    var hash = new string('a', 64);
    var text = $"{hash}  SmartClipVault-v1.0.0-win-x64.zip";
    var result = UpdateService.ParseSha256(text, "SmartClipVault-v1.0.0-win-x64.zip");
    AssertEqual(hash, result, "hash");
});

Add("ParseSha256_AsteriskSeparator", () =>
{
    var hash = new string('b', 64);
    var text = $"{hash} *SmartClipVault-v1.0.0-win-x64.zip";
    var result = UpdateService.ParseSha256(text, "SmartClipVault-v1.0.0-win-x64.zip");
    AssertEqual(hash, result, "hash");
});

Add("ParseSha256_CrlfLineEndings", () =>
{
    var hash = new string('c', 64);
    var text = $"irrelevant  other.zip\r\n{hash}  target.zip\r\n";
    var result = UpdateService.ParseSha256(text, "target.zip");
    AssertEqual(hash, result, "hash");
});

Add("ParseSha256_CaseInsensitiveFilename", () =>
{
    var hash = new string('d', 64);
    var text = $"{hash}  MyFile.ZIP";
    var result = UpdateService.ParseSha256(text, "myfile.zip");
    AssertEqual(hash, result, "hash");
});

Add("ParseSha256_UppercaseHashNormalized", () =>
{
    var hash = new string('A', 64);
    var text = $"{hash}  asset.zip";
    var result = UpdateService.ParseSha256(text, "asset.zip");
    AssertEqual(hash.ToLowerInvariant(), result, "hash should be lowercased");
});

Add("ParseSha256_NotFound_ReturnsNull", () =>
{
    var text = $"{new string('e', 64)}  other.zip";
    var result = UpdateService.ParseSha256(text, "missing.zip");
    Assert(result is null, "should return null when not found");
});

// ─── ExtractExe ────────────────────────────────────────────────────────────────

static string MakeTempDir() =>
    Directory.CreateTempSubdirectory("SelfTest_").FullName;

static string CreateZip(string dir, params (string entryName, byte[] content)[] entries)
{
    var zipPath = Path.Combine(dir, $"test_{Guid.NewGuid():N}.zip");
    using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
    foreach (var (name, content) in entries)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }
    return zipPath;
}

static byte[] FakeExe() => [0x4D, 0x5A]; // MZ header

Add("ExtractExe_ValidZip_ReturnsExePath", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var zipPath = CreateZip(tmp, ("SmartClipVault.exe", FakeExe()));
        var dest = Path.Combine(tmp, "out");
        var result = UpdateService.ExtractExe(zipPath, dest);
        Assert(File.Exists(result), "extracted exe should exist");
        Assert(result.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), "result should be .exe path");
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

Add("ExtractExe_NoExeInZip_Throws", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var zipPath = CreateZip(tmp, ("readme.txt", Encoding.UTF8.GetBytes("hello")));
        var dest = Path.Combine(tmp, "out");
        AssertThrows<InvalidOperationException>(
            () => UpdateService.ExtractExe(zipPath, dest),
            "No .exe file found");
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

Add("ExtractExe_ZipSlip_DotDot_Throws", () =>
{
    var tmp = MakeTempDir();
    try
    {
        // ZipArchive.CreateEntry stores the name as-is; reading it back yields "../outside.exe"
        var zipPath = CreateZip(tmp, ("../outside.exe", FakeExe()));
        var dest = Path.Combine(tmp, "out");
        AssertThrows<InvalidOperationException>(
            () => UpdateService.ExtractExe(zipPath, dest),
            "Zip-Slip");
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

Add("ExtractExe_ZipSlip_AbsolutePath_Throws", () =>
{
    // Drive-letter path like "C:\evil.exe" contains ':' which is rejected.
    var tmp = MakeTempDir();
    try
    {
        var zipPath = CreateZip(tmp, ("C:\\evil.exe", FakeExe()));
        var dest = Path.Combine(tmp, "out");
        AssertThrows<InvalidOperationException>(
            () => UpdateService.ExtractExe(zipPath, dest),
            "Unsafe entry path");
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

Add("ExtractExe_OversizedEntry_Throws", () =>
{
    // Write a zip entry that declares 101 MB uncompressed size by crafting raw zip bytes.
    // We use a trick: create a valid zip with a small compressed payload that declares
    // a very large uncompressed length in the local file header to simulate a zip bomb.
    var tmp = MakeTempDir();
    try
    {
        var zipPath = Path.Combine(tmp, "bomb.zip");
        WriteFakeOversizedZip(zipPath);
        var dest = Path.Combine(tmp, "out");
        AssertThrows<InvalidOperationException>(
            () => UpdateService.ExtractExe(zipPath, dest),
            "100 MB");
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

static void WriteFakeOversizedZip(string path)
{
    // Build a syntactically valid ZIP with one entry "bomb.exe" whose uncompressed size
    // is reported as 101 MB in the central directory and local file header.
    // The actual payload is just 2 bytes, stored (method 0 = STORED).
    // ZipFile.OpenRead reads the central directory; entry.Length comes from that record.
    const long fakeSize = 101L * 1024 * 1024; // > 100 MB limit
    var fileName = Encoding.ASCII.GetBytes("bomb.exe");
    var data = new byte[] { 0x4D, 0x5A };

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

    // Local file header
    uint localHeaderOffset = 0;
    bw.Write(0x04034b50u);    // signature
    bw.Write((ushort)20);     // version needed
    bw.Write((ushort)0);      // flags
    bw.Write((ushort)0);      // compression: STORED
    bw.Write((ushort)0);      // mod time
    bw.Write((ushort)0);      // mod date
    bw.Write(0u);             // CRC-32 (irrelevant — won't be opened)
    bw.Write((uint)data.Length); // compressed size
    bw.Write((uint)fakeSize); // uncompressed size (FAKE — 101 MB)
    bw.Write((ushort)fileName.Length);
    bw.Write((ushort)0);      // extra len
    bw.Write(fileName);
    bw.Write(data);

    // Central directory record
    uint cdOffset = (uint)ms.Position;
    bw.Write(0x02014b50u);    // signature
    bw.Write((ushort)20);     // version made by
    bw.Write((ushort)20);     // version needed
    bw.Write((ushort)0);      // flags
    bw.Write((ushort)0);      // compression
    bw.Write((ushort)0);      // mod time
    bw.Write((ushort)0);      // mod date
    bw.Write(0u);             // CRC-32
    bw.Write((uint)data.Length); // compressed size
    bw.Write((uint)fakeSize); // uncompressed size (FAKE)
    bw.Write((ushort)fileName.Length);
    bw.Write((ushort)0);      // extra len
    bw.Write((ushort)0);      // comment len
    bw.Write((ushort)0);      // disk start
    bw.Write((ushort)0);      // internal attrs
    bw.Write(0u);             // external attrs
    bw.Write(localHeaderOffset);
    bw.Write(fileName);

    // End of central directory record
    uint cdSize = (uint)(ms.Position - cdOffset);
    bw.Write(0x06054b50u);    // signature
    bw.Write((ushort)0);      // disk
    bw.Write((ushort)0);      // disk with CD
    bw.Write((ushort)1);      // entries on this disk
    bw.Write((ushort)1);      // total entries
    bw.Write(cdSize);
    bw.Write(cdOffset);
    bw.Write((ushort)0);      // comment len

    File.WriteAllBytes(path, ms.ToArray());
}

// ─── Verify SHA-256 round-trip ─────────────────────────────────────────────────

Add("VerifySha256_CorrectHash_ReturnsTrue", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var filePath = Path.Combine(tmp, "data.bin");
        var content = Encoding.UTF8.GetBytes("hello SmartClipVault");
        File.WriteAllBytes(filePath, content);
        var expected = Convert.ToHexString(SHA256.HashData(content));
        Assert(UpdateService.VerifySha256(filePath, expected), "correct hash should verify");
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

Add("VerifySha256_WrongHash_ReturnsFalse", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var filePath = Path.Combine(tmp, "data.bin");
        File.WriteAllBytes(filePath, [0x01]);
        Assert(!UpdateService.VerifySha256(filePath, new string('0', 64)), "wrong hash should fail");
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

// ─── SSRF, Redirect Validation, Real Zip-Bomb, & ApplyStagedUpdate ─────────────

Add("ParseRelease_PathTraversalInUrl_ReturnsNull", () =>
{
    UpdateService.AllowLocalUrls = false;
    var json = MakeReleaseJson(
        zipUrl: $"{UpdateService.AllowedAssetPrefix}../../otheruser/otherrepo/releases/download/v1.2.3/SmartClipVault-v1.2.3-win-x64.zip");
    var info = UpdateService.ParseRelease(json);
    Assert(info is null, "URL with .. traversal should be rejected");
});

Add("ParseRelease_LocalhostSpoofDomain_ReturnsNull", () =>
{
    UpdateService.AllowLocalUrls = true;
    try
    {
        var json = MakeReleaseJson(zipUrl: "http://localhost.evil.com/SmartClipVault-v1.2.3-win-x64.zip");
        var info = UpdateService.ParseRelease(json);
        Assert(info is null, "spoofed localhost domain should be rejected");
    }
    finally
    {
        UpdateService.AllowLocalUrls = false;
    }
});

Add("CheckForUpdateAsync_BlocksUnsafeRedirect_WithLocalServer", () =>
{
    UpdateService.AllowLocalUrls = true;
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;

    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var buf = new byte[1024];
        _ = await stream.ReadAsync(buf);
        var resp = Encoding.ASCII.GetBytes(
            "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(resp);
    });

    try
    {
        AssertThrows<InvalidOperationException>(() =>
            UpdateService.CheckForUpdateAsync(
                "1.0.0",
                releasesApiUrl: $"http://127.0.0.1:{port}/latest").GetAwaiter().GetResult(),
            "Blocked unsafe HTTP redirect");
        serverTask.GetAwaiter().GetResult();
    }
    finally
    {
        UpdateService.AllowLocalUrls = false;
    }
});

Add("DownloadAndStageUpdateAsync_BlocksUnsafeRedirect_WithLocalServer", () =>
{
    UpdateService.AllowLocalUrls = true;
    var tmp = MakeTempDir();
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;

    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var buf = new byte[1024];
        _ = await stream.ReadAsync(buf);
        var resp = Encoding.ASCII.GetBytes(
            "HTTP/1.1 302 Found\r\nLocation: https://evil.example.com/payload.zip\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(resp);
    });

    try
    {
        var info = new UpdateService.ReleaseInfo(
            "1.2.3",
            $"http://127.0.0.1:{port}/SmartClipVault-v1.2.3-win-x64.zip",
            $"http://127.0.0.1:{port}/SHA256SUMS.txt");

        AssertThrows<InvalidOperationException>(() =>
            UpdateService.DownloadAndStageUpdateAsync(info, tmp).GetAwaiter().GetResult(),
            "Blocked unsafe HTTP redirect");
        serverTask.GetAwaiter().GetResult();
    }
    finally
    {
        UpdateService.AllowLocalUrls = false;
        Directory.Delete(tmp, recursive: true);
    }
});

Add("DownloadAndStageUpdateAsync_AllowsGitHubCdnRedirect_AndBlocksDowngrade", () =>
{
    UpdateService.AllowLocalUrls = false;
    var tmp = MakeTempDir();
    try
    {
        var zipBuildDir = MakeTempDir();
        try
        {
            var sourceZip = CreateZip(zipBuildDir, ("SmartClipVault.exe", FakeExe()));
            var zipBytes = File.ReadAllBytes(sourceZip);
            var zipHash = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
            var sumsText = $"{zipHash}  SmartClipVault-v1.2.3-win-x64.zip\n";

            var zipUrl = $"{UpdateService.AllowedAssetPrefix}v1.2.3/SmartClipVault-v1.2.3-win-x64.zip";
            var shaUrl = $"{UpdateService.AllowedAssetPrefix}v1.2.3/SHA256SUMS.txt";
            var cdnUrl = "https://objects.githubusercontent.com/github-production-release-asset/test.zip";

            using var handler = new StubHttpMessageHandler(req =>
            {
                var u = req.RequestUri!.AbsoluteUri;
                if (u == zipUrl)
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri(cdnUrl);
                    return redirect;
                }
                if (u == cdnUrl)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
                if (u == shaUrl)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sumsText) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            using var client = new HttpClient(handler);

            var info = new UpdateService.ReleaseInfo("1.2.3", zipUrl, shaUrl);
            var stagedExe = UpdateService.DownloadAndStageUpdateAsync(info, tmp, client).GetAwaiter().GetResult();
            Assert(File.Exists(stagedExe), "staged exe should exist after allowed GitHub CDN redirect");
            Assert(!File.Exists(Path.Combine(tmp, "SmartClipVault-v1.2.3-win-x64.zip")),
                "downloaded zip should be cleaned up after staging");

            // Also verify HTTP downgrade redirect from HTTPS GitHub asset URL is blocked.
            using var downgradeHandler = new StubHttpMessageHandler(_ =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("http://objects.githubusercontent.com/downgrade.zip");
                return redirect;
            });
            using var downgradeClient = new HttpClient(downgradeHandler);
            AssertThrows<InvalidOperationException>(() =>
                UpdateService.DownloadAndStageUpdateAsync(info, tmp, downgradeClient).GetAwaiter().GetResult(),
                "Blocked unsafe HTTP redirect");
        }
        finally
        {
            Directory.Delete(zipBuildDir, recursive: true);
        }
    }
    finally
    {
        Directory.Delete(tmp, recursive: true);
    }
});

Add("ExtractExe_PartialFailure_CleansUpExtractedFilesAndDirs", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var zipPath = Path.Combine(tmp, "partial_fail.zip");
        WriteCorruptSecondEntryZip(zipPath);
        var dest = Path.Combine(tmp, "out");
        Directory.CreateDirectory(dest);

        AssertThrows<InvalidDataException>(() => UpdateService.ExtractExe(zipPath, dest));

        var remainingFiles = Directory.GetFiles(dest, "*", SearchOption.AllDirectories);
        var remainingDirs = Directory.GetDirectories(dest, "*", SearchOption.AllDirectories);
        AssertEqual(0, remainingFiles.Length, "no partial extracted files should remain after failure");
        AssertEqual(0, remainingDirs.Length, "no newly created subdirectories should remain after failure");
    }
    finally
    {
        Directory.Delete(tmp, recursive: true);
    }
});

Add("CopyStreamWithLimit_Exceeds100MB_Throws", () =>
{
    const int totalBombBytes = 101 * 1024 * 1024;
    using var cms = new MemoryStream();
    using (var ds = new DeflateStream(cms, CompressionLevel.Fastest, leaveOpen: true))
    {
        var zeroChunk = new byte[1024 * 1024];
        for (var i = 0; i < totalBombBytes / zeroChunk.Length; i++)
            ds.Write(zeroChunk, 0, zeroChunk.Length);
    }
    cms.Position = 0;

    using var decompressor = new DeflateStream(cms, CompressionMode.Decompress);
    long actualTotal = 0;
    AssertThrows<InvalidOperationException>(
        () => UpdateService.CopyStreamWithLimit(decompressor, Stream.Null, 100L * 1024 * 1024, ref actualTotal),
        "100 MB");
});

static void WriteCorruptSecondEntryZip(string path)
{
    var helperName = Encoding.ASCII.GetBytes("sub/helper.exe");
    var helperData = new byte[] { 0x4D, 0x5A };
    var corruptName = Encoding.ASCII.GetBytes("corrupt.exe");
    var corruptPayload = new byte[] { 0xFF, 0xFF, 0x00, 0x01 };

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

    uint lfh1Offset = (uint)ms.Position;
    bw.Write(0x04034b50u);
    bw.Write((ushort)20);
    bw.Write((ushort)0);
    bw.Write((ushort)0); // STORED
    bw.Write((ushort)0);
    bw.Write((ushort)0);
    bw.Write(0u);
    bw.Write((uint)helperData.Length);
    bw.Write((uint)helperData.Length);
    bw.Write((ushort)helperName.Length);
    bw.Write((ushort)0);
    bw.Write(helperName);
    bw.Write(helperData);

    uint lfh2Offset = (uint)ms.Position;
    bw.Write(0x04034b50u);
    bw.Write((ushort)20);
    bw.Write((ushort)0);
    bw.Write((ushort)8); // DEFLATE (invalid stream triggers InvalidDataException on read)
    bw.Write((ushort)0);
    bw.Write((ushort)0);
    bw.Write(0u);
    bw.Write((uint)corruptPayload.Length);
    bw.Write(100u);
    bw.Write((ushort)corruptName.Length);
    bw.Write((ushort)0);
    bw.Write(corruptName);
    bw.Write(corruptPayload);

    uint cdOffset = (uint)ms.Position;

    void WriteCdEntry(byte[] name, ushort method, uint compSize, uint uncompSize, uint lfhOffset)
    {
        bw.Write(0x02014b50u);
        bw.Write((ushort)20);
        bw.Write((ushort)20);
        bw.Write((ushort)0);
        bw.Write(method);
        bw.Write((ushort)0);
        bw.Write((ushort)0);
        bw.Write(0u);
        bw.Write(compSize);
        bw.Write(uncompSize);
        bw.Write((ushort)name.Length);
        bw.Write((ushort)0);
        bw.Write((ushort)0);
        bw.Write((ushort)0);
        bw.Write((ushort)0);
        bw.Write(0u);
        bw.Write(lfhOffset);
        bw.Write(name);
    }

    WriteCdEntry(helperName, 0, (uint)helperData.Length, (uint)helperData.Length, lfh1Offset);
    WriteCdEntry(corruptName, 8, (uint)corruptPayload.Length, 100u, lfh2Offset);

    uint cdSize = (uint)(ms.Position - cdOffset);
    bw.Write(0x06054b50u);
    bw.Write((ushort)0);
    bw.Write((ushort)0);
    bw.Write((ushort)2);
    bw.Write((ushort)2);
    bw.Write(cdSize);
    bw.Write(cdOffset);
    bw.Write((ushort)0);

    File.WriteAllBytes(path, ms.ToArray());
}

Add("ApplyStagedUpdateCore_UsesUseShellExecuteFalseAndReplacesExe", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var currentExe = Path.Combine(tmp, "SmartClipVault.exe");
        var stagedExe = Path.Combine(tmp, "staged.exe");
        File.WriteAllBytes(currentExe, [0x01, 0x02]);
        File.WriteAllBytes(stagedExe, [0x03, 0x04]);

        ProcessStartInfo? capturedPsi = null;
        var shutdownCalled = false;

        UpdateService.ApplyStagedUpdateCore(
            stagedExe,
            currentExe,
            psi =>
            {
                capturedPsi = psi;
                return new Process();
            },
            () => shutdownCalled = true);

        Assert(capturedPsi is not null, "processStarter should have been called");
        Assert(!capturedPsi!.UseShellExecute, "UseShellExecute must be false");
        AssertEqual(currentExe, capturedPsi.FileName, "FileName");
        AssertEqual(tmp, capturedPsi.WorkingDirectory, "WorkingDirectory");
        AssertEqual(0, capturedPsi.ArgumentList.Count, "ArgumentList");
        Assert(string.IsNullOrEmpty(capturedPsi.Arguments), "Arguments should be empty");
        Assert(shutdownCalled, "shutdownAction should be invoked");
        Assert(File.Exists(currentExe + ".old"), ".old backup should exist");
        Assert(File.ReadAllBytes(currentExe).AsSpan().SequenceEqual(new byte[] { 0x03, 0x04 }), "currentExe should hold new bytes");
    }
    finally
    {
        Directory.Delete(tmp, recursive: true);
    }
});

Add("ApplyStagedUpdateCore_WhenProcessStartFails_RollsBack", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var currentExe = Path.Combine(tmp, "SmartClipVault.exe");
        var stagedExe = Path.Combine(tmp, "staged.exe");
        File.WriteAllBytes(currentExe, [0xAA, 0xBB]);
        File.WriteAllBytes(stagedExe, [0xCC, 0xDD]);

        var shutdownCalled = false;
        AssertThrows<InvalidOperationException>(() =>
            UpdateService.ApplyStagedUpdateCore(
                stagedExe,
                currentExe,
                _ => throw new InvalidOperationException("launch failed"),
                () => shutdownCalled = true),
            "launch failed");

        Assert(!shutdownCalled, "shutdown should not run on failure");
        Assert(File.ReadAllBytes(currentExe).AsSpan().SequenceEqual(new byte[] { 0xAA, 0xBB }), "currentExe should roll back to original bytes");
        Assert(!File.Exists(currentExe + ".old"), ".old should be moved back during rollback");
    }
    finally
    {
        Directory.Delete(tmp, recursive: true);
    }
});

Add("ApplyStagedUpdateCore_WhenOldExeLocked_UsesUniqueBackupAndSucceeds", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var currentExe = Path.Combine(tmp, "SmartClipVault.exe");
        var oldExe = currentExe + ".old";
        var stagedExe = Path.Combine(tmp, "staged.exe");
        File.WriteAllBytes(currentExe, [0x11]);
        File.WriteAllBytes(oldExe, [0x00]);
        File.WriteAllBytes(stagedExe, [0x22]);

        using (new FileStream(oldExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            UpdateService.ApplyStagedUpdateCore(
                stagedExe,
                currentExe,
                _ => new Process(),
                () => { });
        }

        Assert(File.ReadAllBytes(currentExe).AsSpan().SequenceEqual(new byte[] { 0x22 }), "currentExe should be updated");
        UpdateService.CleanupOldUpdateFiles(currentExe);
        Assert(!File.Exists(oldExe), ".old should be cleaned up once unlocked");
        AssertEqual(0, Directory.GetFiles(tmp, "*.old").Length, "all .old files should be cleaned up");
    }
    finally
    {
        Directory.Delete(tmp, recursive: true);
    }
});

// ─── SettingsService (DPAPI, fail-closed Load, Normalize) ──────────────────────

Add("SettingsService_PinDpapiProtectAndVerifyRoundTrip", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var settingsPath = Path.Combine(tmp, "settings.json");
        var svc = new SettingsService(settingsPath);
        var (hash, salt) = SettingsService.CreatePinHash("4829");
        var s = svc.Load();
        s.PinHash = hash;
        s.PinSalt = salt;
        svc.Save(s);

        var rawJson = File.ReadAllText(settingsPath);
        Assert(!rawJson.Contains(hash, StringComparison.Ordinal), "raw PinHash must not be stored in plaintext on disk");
        Assert(!rawJson.Contains(salt, StringComparison.Ordinal), "raw PinSalt must not be stored in plaintext on disk");
        Assert(rawJson.Contains("dpapi:", StringComparison.Ordinal), "settings.json should contain DPAPI-protected PIN fields");

        var svc2 = new SettingsService(settingsPath);
        var loaded = svc2.Load();
        Assert(SettingsService.VerifyPin("4829", loaded.PinHash, loaded.PinSalt), "correct PIN should verify after DPAPI round-trip");
        Assert(!SettingsService.VerifyPin("0000", loaded.PinHash, loaded.PinSalt), "wrong PIN should fail verification");
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

Add("SettingsService_LegacyPlaintextPinMigratesToDpapi", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var settingsPath = Path.Combine(tmp, "settings.json");
        var (hash, salt) = SettingsService.CreatePinHash("1357");
        File.WriteAllText(settingsPath, $$"""
            {
              "PinHash": "{{hash}}",
              "PinSalt": "{{salt}}"
            }
            """);

        var svc = new SettingsService(settingsPath);
        var loaded = svc.Load();
        Assert(SettingsService.VerifyPin("1357", loaded.PinHash, loaded.PinSalt), "legacy plaintext PIN should verify");

        var migratedJson = File.ReadAllText(settingsPath);
        Assert(!migratedJson.Contains(hash, StringComparison.Ordinal), "legacy PinHash should be migrated to DPAPI on disk");
        Assert(migratedJson.Contains("dpapi:", StringComparison.Ordinal), "migrated settings.json should contain dpapi: prefix");
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

Add("SettingsService_LoadFailClosed_PreservesOrLocksPinOnCorruptJson", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var settingsPath = Path.Combine(tmp, "settings.json");
        var svc = new SettingsService(settingsPath);
        var (hash, salt) = SettingsService.CreatePinHash("2468");
        var s = svc.Load();
        s.PinHash = hash;
        s.PinSalt = salt;
        svc.Save(s);

        // Reload after file corruption must preserve existing lock/PIN in memory.
        File.WriteAllText(settingsPath, "{ broken json");
        var reloaded = svc.Load();
        Assert(reloaded.PinHash is not null && reloaded.PinSalt is not null, "reload on corrupt JSON must not null out PIN");
        Assert(SettingsService.VerifyPin("2468", reloaded.PinHash, reloaded.PinSalt), "existing valid PIN should remain intact");

        // Cold load on corrupt existing file must fail-closed (non-null PIN sentinel, VerifyPin returns false).
        var coldSvc = new SettingsService(settingsPath);
        var coldLoaded = coldSvc.Load();
        Assert(coldLoaded.PinHash is not null && coldLoaded.PinSalt is not null, "cold load on corrupt settings.json must not fail-open to null PIN");
        Assert(!SettingsService.VerifyPin("2468", coldLoaded.PinHash, coldLoaded.PinSalt), "corrupt cold load must reject unlock attempts");

        // Corrupted DPAPI payload must also fail-closed.
        File.WriteAllText(settingsPath, """{"PinHash":"dpapi:AAAA","PinSalt":"dpapi:BBBB"}""");
        var dpapiFailSvc = new SettingsService(settingsPath);
        var dpapiFailLoaded = dpapiFailSvc.Load();
        Assert(dpapiFailLoaded.PinHash is not null && dpapiFailLoaded.PinSalt is not null, "corrupt DPAPI PIN must stay non-null");
        Assert(!SettingsService.VerifyPin("2468", dpapiFailLoaded.PinHash, dpapiFailLoaded.PinSalt), "corrupt DPAPI PIN must not verify");

        // Locked/unreadable existing settings.json must also fail-closed.
        using (new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedSvc = new SettingsService(settingsPath);
            var lockedLoaded = lockedSvc.Load();
            Assert(lockedLoaded.PinHash is not null && lockedLoaded.PinSalt is not null, "locked settings.json must not fail-open to null PIN");
            Assert(!SettingsService.VerifyPin("2468", lockedLoaded.PinHash, lockedLoaded.PinSalt), "locked cold load must reject unlock");
        }
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

Add("SettingsService_NormalizeClampsAndDefaults", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var settingsPath = Path.Combine(tmp, "settings.json");
        var svc = new SettingsService(settingsPath);
        var s = new AppSettings
        {
            MaxHistoryItems = 0,
            AutoLockMinutes = -10,
            Language = "  ",
            Theme = "",
            QuickPasteHotkey = "   "
        };
        svc.Save(s);

        var loaded = svc.Load();
        AssertEqual(10, loaded.MaxHistoryItems, "MaxHistoryItems clamped to >= 10");
        AssertEqual(0, loaded.AutoLockMinutes, "AutoLockMinutes clamped to >= 0");
        AssertEqual("en", loaded.Language, "Language defaulted");
        AssertEqual("Dark", loaded.Theme, "Theme defaulted");
        AssertEqual("Ctrl+Shift+V", loaded.QuickPasteHotkey, "QuickPasteHotkey defaulted");
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

// ─── SecretDetector ────────────────────────────────────────────────────────────

Add("SecretDetector_DetectsJsonKeysPemAwsPatBearerUrlCreds", () =>
{
    Assert(SecretDetector.IsSensitive("{\"api_key\": \"my_secret_value\"}"), "JSON double-quoted api_key");
    Assert(SecretDetector.IsSensitive("{'password': 'secret123'}"), "JSON single-quoted password");
    Assert(SecretDetector.IsSensitive("-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAK"), "PEM RSA private key");
    Assert(SecretDetector.IsSensitive("-----BEGIN PRIVATE KEY-----"), "PEM PKCS8 private key");
    Assert(SecretDetector.IsSensitive("aws_key=AKIAIOSFODNN7EXAMPLE"), "AWS AKIA key");
    Assert(SecretDetector.IsSensitive("ASIA1234567890ABCDEF"), "AWS ASIA key");
    Assert(SecretDetector.IsSensitive("github_pat_11ABCDEFG0123456789_0123456789"), "GitHub fine-grained PAT");
    Assert(SecretDetector.IsSensitive("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload"), "Authorization Bearer");
    Assert(SecretDetector.IsSensitive("https://alice:s3cr3t@example.com/api"), "URL credentials");

    Assert(!SecretDetector.IsSensitive("https://example.com:8443/path"), "URL with port only should not trigger");
    Assert(!SecretDetector.IsSensitive("{\"name\": \"hello\", \"password\": \"\"}"), "empty JSON password should not trigger");
    Assert(!SecretDetector.IsSensitive("Regular clipboard text without secrets"), "benign text");
});

// ─── DatabaseService (DPAPI encryption, bounds, TryParse) ─────────────────────

Add("DatabaseService_EncryptsSensitiveClipsWithDpapiAndRoundTrips", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var dbPath = Path.Combine(tmp, "test.db");
        var imgDir = Path.Combine(tmp, "images");
        var db = new DatabaseService(dbPath, imgDir);
        db.Initialize();

        var clipId = db.InsertClip(new ClipItem
        {
            ContentType = ClipContentType.Text,
            TextContent = "sk-proj-secretvalue1234567890",
            OcrText = "token=abcd1234",
            IsSensitive = true,
            CreatedAt = DateTime.UtcNow
        });

        var imgId = db.InsertClip(new ClipItem
        {
            ContentType = ClipContentType.Image,
            ImagePath = Path.Combine(imgDir, "img.png"),
            IsSensitive = false,
            CreatedAt = DateTime.UtcNow
        });
        db.UpdateOcrText(imgId, "AKIAIOSFODNN7EXAMPLE", isSensitive: true);

        // Verify raw SQLite rows are DPAPI-protected, not plaintext.
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TextContent, OcrText, IsSensitive FROM clips WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", clipId);
            using var reader = cmd.ExecuteReader();
            Assert(reader.Read(), "sensitive text row should exist");
            Assert(reader.GetString(0).StartsWith("dpapi:", StringComparison.Ordinal), "raw TextContent should be dpapi-encrypted");
            Assert(reader.GetString(1).StartsWith("dpapi:", StringComparison.Ordinal), "raw OcrText should be dpapi-encrypted");
            AssertEqual(1, reader.GetInt32(2), "IsSensitive");
        }

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT OcrText, IsSensitive FROM clips WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", imgId);
            using var reader = cmd.ExecuteReader();
            Assert(reader.Read(), "OCR image row should exist");
            Assert(reader.GetString(0).StartsWith("dpapi:", StringComparison.Ordinal), "OCR sensitive text should be dpapi-encrypted");
            AssertEqual(1, reader.GetInt32(1), "image clip marked sensitive");
        }

        var clips = db.GetRecentClips(null, 10);
        var textClip = clips.Find(c => c.Id == clipId)!;
        AssertEqual("sk-proj-secretvalue1234567890", textClip.TextContent, "decrypted TextContent");
        AssertEqual("token=abcd1234", textClip.OcrText, "decrypted OcrText");
        var imgClip = clips.Find(c => c.Id == imgId)!;
        Assert(imgClip.IsSensitive, "image clip should be sensitive");
        AssertEqual("AKIAIOSFODNN7EXAMPLE", imgClip.OcrText, "decrypted image OcrText");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(tmp, recursive: true);
    }
});

Add("DatabaseService_DeleteRecentUnpinnedClips_BoundsAndTryParse", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var dbPath = Path.Combine(tmp, "test.db");
        var imgDir = Path.Combine(tmp, "images");
        var db = new DatabaseService(dbPath, imgDir);
        db.Initialize();

        AssertThrows<ArgumentOutOfRangeException>(() => db.DeleteRecentUnpinnedClips(TimeSpan.FromSeconds(-1)));

        var unpinnedId = db.InsertClip(new ClipItem
        {
            ContentType = ClipContentType.Text,
            TextContent = "unpinned",
            CreatedAt = DateTime.UtcNow
        });
        var starredId = db.InsertClip(new ClipItem
        {
            ContentType = ClipContentType.Text,
            TextContent = "starred",
            IsStarred = true,
            CreatedAt = DateTime.UtcNow
        });

        db.DeleteRecentUnpinnedClips(TimeSpan.MaxValue);
        var remaining = db.GetRecentClips(null, 10);
        AssertEqual(1, remaining.Count, "only starred clip should remain");
        AssertEqual(starredId, remaining[0].Id, "remaining clip id");

        // Corrupt CreatedAt in DB should not throw DateTime.Parse.
        var boardId = db.CreateBoard("B1");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clips SET CreatedAt = 'invalid-date' WHERE Id = $id; UPDATE boards SET CreatedAt = 'bad-date' WHERE Id = $bid;";
            cmd.Parameters.AddWithValue("$id", starredId);
            cmd.Parameters.AddWithValue("$bid", boardId);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var parsedClips = db.GetRecentClips(null, 10);
        var parsedBoards = db.GetBoards();
        AssertEqual(1, parsedClips.Count, "GetRecentClips handles invalid CreatedAt");
        AssertEqual(1, parsedBoards.Count, "GetBoards handles invalid CreatedAt");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(tmp, recursive: true);
    }
});

// ─── BackupService (symlinks, reserved names, ImagePath remap, atomic rollback)

Add("BackupService_RejectsUnixSymlinkAndWindowsReservedNames", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var dbPath = Path.Combine(tmp, "db.db");
        var imgDir = Path.Combine(tmp, "images");
        var db = new DatabaseService(dbPath, imgDir);
        db.Initialize();

        var srcDb = Path.Combine(tmp, "src.db");
        db.BackupTo(srcDb);
        var dbBytes = File.ReadAllBytes(srcDb);

        // Symlink entry (Unix S_IFLNK = 0xA000 in high 16 bits of ExternalAttributes)
        var symlinkZip = Path.Combine(tmp, "symlink.zip");
        using (var archive = ZipFile.Open(symlinkZip, ZipArchiveMode.Create))
        {
            var e1 = archive.CreateEntry("smartclipvault.db");
            using (var s = e1.Open()) s.Write(dbBytes);

            var e2 = archive.CreateEntry("images/link.png");
            e2.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using (var s = e2.Open()) s.Write([0x01]);
        }

        using var backup = new BackupService(db, imgDir);
        AssertThrows<InvalidDataException>(() => backup.RestoreAsync(symlinkZip).GetAwaiter().GetResult(), "symbolic link");

        foreach (var badName in new[] { "images/CON.png", "images/nul.txt", "images/com1/a.png", "images/LPT9.png" })
        {
            var reservedZip = Path.Combine(tmp, $"reserved_{Guid.NewGuid():N}.zip");
            using (var archive = ZipFile.Open(reservedZip, ZipArchiveMode.Create))
            {
                var e1 = archive.CreateEntry("smartclipvault.db");
                using (var s = e1.Open()) s.Write(dbBytes);
                var e2 = archive.CreateEntry(badName);
                using (var s = e2.Open()) s.Write([0x01]);
            }
            AssertThrows<InvalidDataException>(() => backup.RestoreAsync(reservedZip).GetAwaiter().GetResult(), "reserved");
        }
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(tmp, recursive: true);
    }
});

Add("BackupService_RestoreRemapsImagePathAndAtomicRollbackOnFailure", () =>
{
    var tmp = MakeTempDir();
    try
    {
        // Build source backup with an image path from another location
        var srcDir = Path.Combine(tmp, "src");
        var srcImgDir = Path.Combine(srcDir, "images");
        Directory.CreateDirectory(srcImgDir);
        var srcDb = new DatabaseService(Path.Combine(srcDir, "src.db"), srcImgDir);
        srcDb.Initialize();

        var backupImgFile = Path.Combine(srcImgDir, "restored.png");
        File.WriteAllBytes(backupImgFile, [0x89, 0x50, 0x4E, 0x47]);
        srcDb.InsertClip(new ClipItem
        {
            ContentType = ClipContentType.Image,
            ImagePath = @"C:\AnotherUser\OldFolder\images\restored.png",
            CreatedAt = DateTime.UtcNow
        });
        srcDb.InsertClip(new ClipItem
        {
            ContentType = ClipContentType.Text,
            TextContent = "from-backup",
            CreatedAt = DateTime.UtcNow
        });

        var zipPath = Path.Combine(tmp, "backup.zip");
        using (var srcBackup = new BackupService(srcDb, srcImgDir))
        {
            srcBackup.ExportAsync(zipPath, includeImages: true).GetAwaiter().GetResult();
        }

        // Restore into target DB with a different image directory
        var dstDir = Path.Combine(tmp, "dst");
        var dstImgDir = Path.Combine(dstDir, "images");
        Directory.CreateDirectory(dstImgDir);
        var dstDb = new DatabaseService(Path.Combine(dstDir, "dst.db"), dstImgDir);
        dstDb.Initialize();
        dstDb.InsertClip(new ClipItem
        {
            ContentType = ClipContentType.Text,
            TextContent = "original-live",
            CreatedAt = DateTime.UtcNow
        });

        using var dstBackup = new BackupService(dstDb, dstImgDir);
        dstBackup.RestoreAsync(zipPath).GetAwaiter().GetResult();

        var restoredClips = dstDb.GetRecentClips(null, 10);
        var restoredImgClip = restoredClips.Find(c => c.ContentType == ClipContentType.Image)!;
        AssertEqual(Path.GetFullPath(Path.Combine(dstImgDir, "restored.png")), restoredImgClip.ImagePath, "remapped ImagePath");
        Assert(File.Exists(Path.Combine(dstImgDir, "restored.png")), "restored image file exists");

        // Now test atomic rollback when target image copy fails (locked target file)
        var lockedZip = Path.Combine(tmp, "rollback_test.zip");
        var rbSrcDir = Path.Combine(tmp, "rb_src");
        var rbSrcImgDir = Path.Combine(rbSrcDir, "images");
        Directory.CreateDirectory(rbSrcImgDir);
        var rbDb = new DatabaseService(Path.Combine(rbSrcDir, "rb.db"), rbSrcImgDir);
        rbDb.Initialize();
        File.WriteAllBytes(Path.Combine(rbSrcImgDir, "extra.png"), [0x01, 0x02]);
        File.WriteAllBytes(Path.Combine(rbSrcImgDir, "restored.png"), [0xAA, 0xBB]);
        rbDb.InsertClip(new ClipItem
        {
            ContentType = ClipContentType.Text,
            TextContent = "should-not-persist",
            CreatedAt = DateTime.UtcNow
        });
        using (var rbBackup = new BackupService(rbDb, rbSrcImgDir))
        {
            rbBackup.ExportAsync(lockedZip, includeImages: true).GetAwaiter().GetResult();
        }

        var lockedFilePath = Path.Combine(dstImgDir, "restored.png");
        using (new FileStream(lockedFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AssertThrows<IOException>(() => dstBackup.RestoreAsync(lockedZip).GetAwaiter().GetResult());
        }

        // Verify DB rolled back to "from-backup" and "extra.png" did not remain
        var postFailClips = dstDb.GetRecentClips(null, 10);
        Assert(postFailClips.Exists(c => c.TextContent == "from-backup"), "DB should roll back to pre-restore clip");
        Assert(!postFailClips.Exists(c => c.TextContent == "should-not-persist"), "failed restore DB state must be rolled back");
        Assert(!File.Exists(Path.Combine(dstImgDir, "extra.png")), "newly copied image should be deleted on rollback");
        Assert(File.ReadAllBytes(lockedFilePath).AsSpan().SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }), "existing image must retain original bytes");

        dstBackup.Dispose();
        AssertThrows<ObjectDisposedException>(() => dstBackup.ExportAsync(zipPath).GetAwaiter().GetResult());
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(tmp, recursive: true);
    }
});

// ─── ClipboardMonitorService, OcrService, PasteHelper ──────────────────────────

Add("ClipboardMonitorService_ResetDeduplicationAndCrossThreadDispose", () =>
{
    ClipboardMonitorService? monitor = null;
    Exception? staError = null;
    using var started = new ManualResetEventSlim(false);
    var staThread = new Thread(() =>
    {
        try
        {
            monitor = new ClipboardMonitorService();
            monitor.Start();
        }
        catch (Exception ex)
        {
            staError = ex;
        }
        finally
        {
            started.Set();
        }
    });
    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    Assert(started.Wait(TimeSpan.FromSeconds(5)), "STA thread should start monitor");
    if (staError is not null)
        throw staError;

    // Cross-thread Dispose must not deadlock when STA thread has already exited without a message loop.
    monitor!.ResetDeduplicationState();
    var disposeTask = Task.Run(() => monitor.Dispose());
    Assert(disposeTask.Wait(TimeSpan.FromSeconds(3)), "cross-thread Dispose must complete without deadlocking");
});

Add("OcrService_ValidatesContainedPathsAndDisposesCleanly", () =>
{
    var tmp = MakeTempDir();
    try
    {
        var db = new DatabaseService(Path.Combine(tmp, "ocr.db"), Path.Combine(tmp, "images"));
        db.Initialize();
        using var ocr = new OcrService(db, Path.Combine(tmp, "images"));
        ocr.Enqueue(new ClipItem
        {
            Id = 1,
            ContentType = ClipContentType.Image,
            ImagePath = Path.Combine(tmp, "images", "..", "outside.png")
        });
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(tmp, recursive: true);
    }
});

Add("PasteHelper_LoadBitmapRejectsOversizedHeaderAndLoadsValidPng", () =>
{
    var tmp = MakeTempDir();
    try
    {
        // Valid 1x1 transparent PNG
        byte[] valid1x1Png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89,
            0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54,
            0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01,
            0x0D, 0x0A, 0x2D, 0xB4,
            0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,
            0xAE, 0x42, 0x60, 0x82
        ];

        var validPath = Path.Combine(tmp, "valid.png");
        File.WriteAllBytes(validPath, valid1x1Png);
        var loaded = PasteHelper.LoadBitmapFromValidatedPath(validPath, null, null);
        Assert(loaded is not null, "valid 1x1 PNG should load");

        // Patch IHDR width/height to 20000 x 20000 with valid CRC
        var oversized = (byte[])valid1x1Png.Clone();
        oversized[16] = 0x00; oversized[17] = 0x00; oversized[18] = 0x4E; oversized[19] = 0x20; // 20000
        oversized[20] = 0x00; oversized[21] = 0x00; oversized[22] = 0x4E; oversized[23] = 0x20; // 20000
        var crc = ComputeCrc32(oversized.AsSpan(12, 17));
        oversized[29] = (byte)(crc >> 24);
        oversized[30] = (byte)(crc >> 16);
        oversized[31] = (byte)(crc >> 8);
        oversized[32] = (byte)crc;

        var oversizedPath = Path.Combine(tmp, "oversized.png");
        File.WriteAllBytes(oversizedPath, oversized);
        var rejected = PasteHelper.LoadBitmapFromValidatedPath(oversizedPath, null, null);
        Assert(rejected is null, "oversized 20000x20000 PNG must be rejected before decoding");
    }
    finally { Directory.Delete(tmp, recursive: true); }
});

static uint ComputeCrc32(ReadOnlySpan<byte> data)
{
    uint crc = 0xFFFFFFFFu;
    foreach (var b in data)
    {
        crc ^= b;
        for (var i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    }
    return ~crc;
}

// ─── Runner ────────────────────────────────────────────────────────────────────

int passed = 0, failed = 0;

foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"[PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        Console.ResetColor();
        failed++;
    }
}

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed.");

return failed == 0 ? 0 : 1;

file sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => Task.FromResult(responder(request));
}
