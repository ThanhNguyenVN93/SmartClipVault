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
