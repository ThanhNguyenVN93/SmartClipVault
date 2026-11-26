using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Windows.Graphics.Imaging;
using SmartClipVault.Models;

namespace SmartClipVault.Services;

public sealed class ClipboardMonitorService : IDisposable
{
    private static readonly string ImageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartClipVault", "images");

    internal static string ImageDirectory => ImageDir;

    private const int WmClipboardUpdate = 0x031D;
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint CfHDrop = 15;
    private const int ErrorAccessDenied = 5;
    private const int MaxRetries = 5;
    private const int MaxClipboardChars = 4_000_000;
    private const int MaxClipboardImageBytes = 64 * 1024 * 1024;
    private const uint MaxDecodedImageDimension = 16384;
    private const ulong MaxDecodedImagePixels = 64_000_000;
    private static readonly IntPtr HwndMessage = new(-3);

    private static readonly Lock ActiveMonitorsLock = new();
    private static readonly HashSet<ClipboardMonitorService> ActiveMonitors = [];

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private volatile SettingsService? _settings;
    public SettingsService? Settings
    {
        get => _settings;
        set => _settings = value;
    }

    private readonly Lock _stateLock = new();
    private HwndSource? _hwndSource;
    private CancellationTokenSource? _retryCts;
    private bool _isListening;
    private volatile bool _disposed;
    private byte[]? _lastTextHash;
    private byte[]? _lastImageHash;
    private int _selfWriteDepth;
    private uint _selfWriteInitialSequence;
    private bool _selfWriteSucceeded;
    private uint _suppressedSequenceNumber;
    private long _suppressedUntilTickMs;
    private string? _lastCapturedText;
    private long _lastCapturedTextTickMs;
    private ClipItem? _lastCapturedItem;
    private bool _lastCapturedStarred;

    public sealed class SelfCopySuppressionScope : IDisposable
    {
        private readonly ClipboardMonitorService[] _monitors;
        private int _succeeded;
        private int _disposed;

        internal SelfCopySuppressionScope(ClipboardMonitorService[] monitors)
        {
            _monitors = monitors;
            foreach (var monitor in _monitors)
                monitor.EnterSelfCopySuppression();
        }

        public void MarkSucceeded() => Interlocked.Exchange(ref _succeeded, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            var succeeded = Volatile.Read(ref _succeeded) != 0;
            foreach (var monitor in _monitors)
                monitor.ExitSelfCopySuppression(succeeded);
        }
    }

    public event EventHandler<ClipItem>? ClipCaptured;
    public event EventHandler<ClipItem>? DoubleCopyStar;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, [Out] char[]? lpszFile, uint cch);

    public void Start()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isListening)
                return;
        }

        var parameters = new HwndSourceParameters("SmartClipVaultClipboardMonitor")
        {
            ParentWindow = HwndMessage,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };

        var hwndSource = new HwndSource(parameters);
        var listenerRegistered = false;
        try
        {
            hwndSource.AddHook(WndProc);

            if (!AddClipboardFormatListener(hwndSource.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Failed to register clipboard listener. Win32 error: {error}");
            }

            listenerRegistered = true;

            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_isListening)
                    throw new InvalidOperationException("ClipboardMonitorService is already started.");

                _hwndSource = hwndSource;
                _isListening = true;
            }

            lock (ActiveMonitorsLock)
            {
                if (!_disposed)
                    ActiveMonitors.Add(this);
            }
        }
        catch
        {
            if (listenerRegistered)
            {
                RemoveClipboardFormatListener(hwndSource.Handle);
            }
            hwndSource.RemoveHook(WndProc);
            hwndSource.Dispose();
            throw;
        }
    }

    public SelfCopySuppressionScope SuppressSelfCopy() => new([this]);

    internal static SelfCopySuppressionScope SuppressActiveSelfCopy()
    {
        ClipboardMonitorService[] snapshot;
        lock (ActiveMonitorsLock)
        {
            snapshot = ActiveMonitors.Count == 0 ? [] : [.. ActiveMonitors];
        }
        return new SelfCopySuppressionScope(snapshot);
    }

    private void EnterSelfCopySuppression()
    {
        CancellationTokenSource? previousCts;

        lock (_stateLock)
        {
            if (_disposed)
                return;

            if (_selfWriteDepth == 0)
            {
                _selfWriteInitialSequence = GetClipboardSequenceNumber();
                _selfWriteSucceeded = false;
            }

            _selfWriteDepth++;
            previousCts = _retryCts;
            _retryCts = null;
        }

        if (previousCts is not null)
        {
            try
            {
                previousCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void ExitSelfCopySuppression(bool succeeded)
    {
        lock (_stateLock)
        {
            if (_selfWriteDepth <= 0)
                return;

            if (succeeded)
                _selfWriteSucceeded = true;

            _selfWriteDepth--;
            if (_selfWriteDepth > 0 || _disposed || !_selfWriteSucceeded)
                return;

            var seqAfter = GetClipboardSequenceNumber();
            if (seqAfter != 0 && seqAfter != _selfWriteInitialSequence)
            {
                _suppressedSequenceNumber = seqAfter;
                _suppressedUntilTickMs = 0;
            }
            else if (seqAfter == 0)
            {
                _suppressedSequenceNumber = 0;
                _suppressedUntilTickMs = Environment.TickCount64 + 500;
            }
        }
    }

    private bool ShouldSuppressClipboardUpdate()
    {
        var currentSeq = GetClipboardSequenceNumber();

        lock (_stateLock)
        {
            if (_disposed || _selfWriteDepth > 0)
                return true;

            if (_suppressedSequenceNumber != 0)
            {
                if (currentSeq == _suppressedSequenceNumber)
                    return true;

                _suppressedSequenceNumber = 0;
            }

            if (_suppressedUntilTickMs != 0)
            {
                if (Environment.TickCount64 <= _suppressedUntilTickMs)
                    return true;

                _suppressedUntilTickMs = 0;
            }

            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && !_disposed)
        {
            if (ShouldSuppressClipboardUpdate())
            {
                handled = true;
                return IntPtr.Zero;
            }

            StartClipboardProcessing();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void StartClipboardProcessing()
    {
        CancellationTokenSource? previousCts;
        var nextCts = new CancellationTokenSource();

        lock (_stateLock)
        {
            if (_disposed)
            {
                nextCts.Dispose();
                return;
            }

            previousCts = _retryCts;
            _retryCts = nextCts;
        }

        if (previousCts is not null)
        {
            try
            {
                previousCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _ = ProcessClipboardWithRetryAsync(nextCts);
    }

    private async Task ProcessClipboardWithRetryAsync(CancellationTokenSource cts)
    {
        try
        {
            var cancellationToken = cts.Token;
            var sourceApp = GetForegroundProcessName();

            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                if (_disposed || cancellationToken.IsCancellationRequested || ShouldSuppressClipboardUpdate())
                    return;

                var settings = _settings?.GetSnapshot();
                if (settings is not null && !settings.CaptureHistory)
                {
                    ResetDeduplicationState();
                    return;
                }

                if (settings is not null && IsForegroundAppExcluded(settings.ExcludedApps))
                {
                    ResetDeduplicationState();
                    return;
                }

                IntPtr hwnd;
                lock (_stateLock)
                {
                    if (_disposed || _hwndSource is null || _hwndSource.IsDisposed)
                        return;
                    hwnd = _hwndSource.Handle;
                }
                if (hwnd == IntPtr.Zero)
                    return;

                if (TryReadClipboardData(hwnd, out var text, out var dibBytes, out var filePaths, out var shouldRetry))
                {
                    if (ShouldSuppressClipboardUpdate())
                        return;

                    if (filePaths is { Length: > 0 })
                    {
                        ResetDeduplicationState();
                        var fileItem = new ClipItem
                        {
                            ContentType = ClipContentType.Files,
                            TextContent = string.Join("\n", filePaths),
                            SourceApp = sourceApp,
                            CreatedAt = DateTime.UtcNow
                        };
                        if (!_disposed && !cancellationToken.IsCancellationRequested)
                        {
                            try { ClipCaptured?.Invoke(this, fileItem); }
                            catch (Exception ex) { Trace.TraceError($"ClipCaptured handler threw an exception: {ex}"); }
                        }
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var trimmed = text.Trim();
                        var isDuplicate = IsDuplicateText(trimmed);
                        if (!isDuplicate)
                        {
                            var isSensitive = SecretDetector.IsSensitive(trimmed);
                            var item = new ClipItem
                            {
                                ContentType = ClipContentType.Text,
                                TextContent = trimmed,
                                IsSensitive = isSensitive,
                                SourceApp = sourceApp,
                                CreatedAt = DateTime.UtcNow
                            };

                            lock (_stateLock)
                            {
                                if (!isSensitive && settings is { AutoStarOnDoubleCopy: true })
                                {
                                    _lastCapturedText = trimmed;
                                    _lastCapturedTextTickMs = Environment.TickCount64;
                                    _lastCapturedItem = item;
                                    _lastCapturedStarred = false;
                                }
                                else
                                {
                                    _lastCapturedText = null;
                                    _lastCapturedTextTickMs = 0;
                                    _lastCapturedItem = null;
                                    _lastCapturedStarred = false;
                                }
                            }

                            if (!_disposed && !cancellationToken.IsCancellationRequested)
                            {
                                try
                                {
                                    ClipCaptured?.Invoke(this, item);
                                }
                                catch (Exception ex)
                                {
                                    Trace.TraceError($"ClipCaptured handler threw an exception: {ex}");
                                }
                            }
                        }
                        else if (settings is { AutoStarOnDoubleCopy: true })
                        {
                            ClipItem? itemToStar = null;
                            var nowMs = Environment.TickCount64;
                            lock (_stateLock)
                            {
                                if (_lastCapturedItem is not null &&
                                    !_lastCapturedItem.IsSensitive &&
                                    !_lastCapturedStarred &&
                                    _lastCapturedText is not null &&
                                    string.Equals(_lastCapturedText, trimmed, StringComparison.Ordinal))
                                {
                                    if ((nowMs - _lastCapturedTextTickMs) <= 1000)
                                    {
                                        _lastCapturedStarred = true;
                                        _lastCapturedItem.IsStarred = true;
                                        itemToStar = _lastCapturedItem;
                                    }
                                    else
                                    {
                                        _lastCapturedTextTickMs = nowMs;
                                    }
                                }
                            }

                            if (itemToStar is not null && !_disposed && !cancellationToken.IsCancellationRequested)
                            {
                                try
                                {
                                    DoubleCopyStar?.Invoke(this, itemToStar);
                                }
                                catch (Exception ex)
                                {
                                    Trace.TraceError($"DoubleCopyStar handler threw an exception: {ex}");
                                }
                            }
                        }
                    }
                    else if (dibBytes is not null && !IsDuplicateImage(dibBytes))
                    {
                        ClearLastCapturedTextState();
                        await SaveAndEmitImageAsync(dibBytes, sourceApp, cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }

                if (!shouldRetry || attempt == MaxRetries - 1)
                    return;

                var delayMs = 40 * (attempt + 1);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Unexpected clipboard monitoring error: {ex}");
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_retryCts, cts))
                {
                    _retryCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private bool IsDuplicateText(string text)
        => IsDuplicateData(MemoryMarshal.AsBytes(text.AsSpan()), ref _lastTextHash, ref _lastImageHash);

    private bool IsDuplicateImage(byte[] dibBytes)
        => IsDuplicateData(dibBytes, ref _lastImageHash, ref _lastTextHash);

    private bool IsDuplicateData(ReadOnlySpan<byte> data, ref byte[]? currentHash, ref byte[]? otherHash)
    {
        var hash = SHA256.HashData(data);

        lock (_stateLock)
        {
            if (currentHash is not null && CryptographicOperations.FixedTimeEquals(currentHash, hash))
            {
                return true;
            }

            ZeroAndClear(ref currentHash);
            ZeroAndClear(ref otherHash);
            currentHash = hash;
            return false;
        }
    }

    private static void ZeroAndClear(ref byte[]? buffer)
    {
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
            buffer = null;
        }
    }

    private static bool TryReadClipboardData(IntPtr hwnd, out string? text, out byte[]? dibBytes,
        out string[]? filePaths, out bool shouldRetry)
    {
        text = null;
        dibBytes = null;
        filePaths = null;
        shouldRetry = false;

        if (!OpenClipboard(hwnd))
        {
            var error = Marshal.GetLastWin32Error();
            shouldRetry = error == ErrorAccessDenied;
            return false;
        }

        try
        {
            // CF_HDROP takes priority over text representation of paths
            if (IsClipboardFormatAvailable(CfHDrop))
            {
                var dropHandle = GetClipboardData(CfHDrop);
                if (dropHandle != IntPtr.Zero)
                {
                    var count = DragQueryFile(dropHandle, 0xFFFFFFFF, null, 0);
                    if (count > 0 && count <= 1024)
                    {
                        var paths = new List<string>((int)count);
                        var buffer = new char[4096];
                        for (uint i = 0; i < count; i++)
                        {
                            var len = DragQueryFile(dropHandle, i, buffer, (uint)buffer.Length);
                            if (len > 0 && len < buffer.Length)
                                paths.Add(new string(buffer, 0, (int)len));
                        }
                        if (paths.Count > 0)
                        {
                            filePaths = [.. paths];
                            return true;
                        }
                    }
                }
            }

            if (IsClipboardFormatAvailable(CfUnicodeText))
            {
                var textHandle = GetClipboardData(CfUnicodeText);
                if (textHandle == IntPtr.Zero)
                    return false;

                var byteSize = GlobalSize(textHandle);
                if (byteSize >= sizeof(char))
                {
                    var ptr = GlobalLock(textHandle);
                    if (ptr == IntPtr.Zero)
                        return false;

                    try
                    {
                        text = ReadBoundedUnicodeString(ptr, byteSize);
                    }
                    finally
                    {
                        GlobalUnlock(textHandle);
                    }
                }

                if (!string.IsNullOrWhiteSpace(text))
                    return true;
            }

            if (IsClipboardFormatAvailable(CfDib))
            {
                var dibHandle = GetClipboardData(CfDib);
                if (dibHandle == IntPtr.Zero)
                    return false;

                var byteSize = GlobalSize(dibHandle);
                if (byteSize >= 12 && byteSize <= (nuint)MaxClipboardImageBytes)
                {
                    var ptr = GlobalLock(dibHandle);
                    if (ptr == IntPtr.Zero)
                        return false;

                    try
                    {
                        var buffer = new byte[(int)byteSize];
                        Marshal.Copy(ptr, buffer, 0, buffer.Length);
                        dibBytes = buffer;
                    }
                    finally
                    {
                        GlobalUnlock(dibHandle);
                    }
                }
            }

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static string ReadBoundedUnicodeString(IntPtr ptr, nuint byteSize)
    {
        var maxChars = (int)Math.Min((ulong)byteSize / sizeof(char), (ulong)MaxClipboardChars);
        if (maxChars <= 0)
            return string.Empty;

        var chunkSize = Math.Min(maxChars, 4096);
        var chunk = ArrayPool<char>.Shared.Rent(chunkSize);
        try
        {
            var actualLength = maxChars;
            var offset = 0;
            var isFirstChunk = true;

            while (offset < maxChars)
            {
                var count = Math.Min(chunkSize, maxChars - offset);
                Marshal.Copy(IntPtr.Add(ptr, offset * sizeof(char)), chunk, 0, count);

                var nullIndex = chunk.AsSpan(0, count).IndexOf('\0');
                if (nullIndex >= 0)
                {
                    actualLength = offset + nullIndex;
                    break;
                }

                offset += count;
                if (offset < maxChars)
                {
                    isFirstChunk = false;
                }
            }

            if (actualLength == 0)
                return string.Empty;

            if (isFirstChunk)
                return new string(chunk, 0, actualLength);

            var fullBuffer = ArrayPool<char>.Shared.Rent(actualLength);
            try
            {
                Marshal.Copy(ptr, fullBuffer, 0, actualLength);
                return new string(fullBuffer, 0, actualLength);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(fullBuffer, clearArray: true);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chunk, clearArray: true);
        }
    }

    private static string? GetForegroundProcessName()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        if (GetWindowThreadProcessId(fg, out var pid) == 0 || pid == 0 || pid > int.MaxValue) return null;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveAndEmitImageAsync(byte[] dibBytes, string? sourceApp, CancellationToken cancellationToken)
    {
        string? createdFilePath = null;
        try
        {
            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed)
                    return;

                using var bmpMemory = TryBuildBmpStream(dibBytes);
                if (bmpMemory is null)
                    return;

                using var bmpStream = bmpMemory.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(bmpStream).AsTask(cancellationToken).ConfigureAwait(false);

                var pixelWidth = decoder.PixelWidth;
                var pixelHeight = decoder.PixelHeight;
                if (pixelWidth == 0 || pixelHeight == 0 ||
                    pixelWidth > MaxDecodedImageDimension || pixelHeight > MaxDecodedImageDimension ||
                    (ulong)pixelWidth * pixelHeight > MaxDecodedImagePixels)
                {
                    Trace.TraceWarning($"Skipping oversized clipboard image ({pixelWidth}x{pixelHeight}).");
                    return;
                }

                // CF_DIB (40-byte BITMAPINFOHEADER) uses the 4th byte as padding, not alpha.
                var biSize = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(0, 4));
                var alphaMode = biSize >= 108 ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore;

                using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    alphaMode,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);

                using var pngMemory = new MemoryStream();
                using var pngStream = pngMemory.AsRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, pngStream)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                encoder.SetSoftwareBitmap(softwareBitmap);
                await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

                if (_disposed || cancellationToken.IsCancellationRequested)
                    return;

                Directory.CreateDirectory(ImageDir);
                var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.png";
                var filePath = Path.Combine(ImageDir, fileName);
                createdFilePath = filePath;

                using (var fileStream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    pngMemory.Position = 0;
                    await pngMemory.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                    await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    TryDeleteFile(filePath);
                    createdFilePath = null;
                    return;
                }

                var item = new ClipItem
                {
                    ContentType = ClipContentType.Image,
                    ImagePath = filePath,
                    SourceApp = sourceApp,
                    CreatedAt = DateTime.UtcNow
                };

                try
                {
                    ClipCaptured?.Invoke(this, item);
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"ClipCaptured handler threw an exception: {ex}");
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (createdFilePath is not null)
                TryDeleteFile(createdFilePath);
            throw;
        }
        catch (Exception ex)
        {
            if (createdFilePath is not null)
                TryDeleteFile(createdFilePath);
            Trace.TraceError($"Failed to capture clipboard image: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static MemoryStream? TryBuildBmpStream(byte[] dibBytes)
    {
        if (dibBytes.Length < 12)
            return null;

        var biSize = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(0, 4));
        ulong offBits;

        if (biSize == 12)
        {
            var bpp = BinaryPrimitives.ReadUInt16LittleEndian(dibBytes.AsSpan(10, 2));
            var colorEntries = bpp <= 8 ? (1ul << bpp) : 0ul;
            offBits = 14ul + 12ul + colorEntries * 3ul;
        }
        else if (biSize >= 40 && biSize <= (uint)dibBytes.Length)
        {
            var bpp = BinaryPrimitives.ReadUInt16LittleEndian(dibBytes.AsSpan(14, 2));
            var compression = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(16, 4));
            var clrUsed = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(32, 4));
            if (clrUsed > 65536)
                return null;

            var colorEntries = clrUsed > 0 ? clrUsed : (bpp <= 8 ? (1ul << bpp) : 0ul);
            var masksBytes = (biSize == 40 && compression == 3) ? 12ul
                : (biSize == 40 && compression == 6) ? 16ul
                : 0ul;
            offBits = 14ul + biSize + masksBytes + colorEntries * 4ul;
        }
        else
        {
            return null;
        }

        var totalSize = 14ul + (ulong)dibBytes.Length;
        if (offBits > totalSize || totalSize > uint.MaxValue)
            return null;

        var stream = new MemoryStream((int)totalSize);
        Span<byte> header = stackalloc byte[14];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..6], (uint)totalSize);
        header[6..10].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header[10..14], (uint)offBits);

        stream.Write(header);
        stream.Write(dibBytes);
        stream.Position = 0;
        return stream;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    public void ResetDeduplicationState()
    {
        lock (_stateLock)
        {
            ZeroAndClear(ref _lastTextHash);
            ZeroAndClear(ref _lastImageHash);
            _lastCapturedText = null;
            _lastCapturedTextTickMs = 0;
            _lastCapturedItem = null;
            _lastCapturedStarred = false;
        }
    }

    private void ClearLastCapturedTextState()
    {
        lock (_stateLock)
        {
            _lastCapturedText = null;
            _lastCapturedTextTickMs = 0;
            _lastCapturedItem = null;
            _lastCapturedStarred = false;
        }
    }

    private static bool IsForegroundAppExcluded(IReadOnlyList<string> excludedApps)
    {
        if (excludedApps.Count == 0)
            return false;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return false;

        if (GetWindowThreadProcessId(fg, out var pid) == 0 || pid == 0 || pid > int.MaxValue)
            return false;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var pname = proc.ProcessName;
            foreach (var excluded in excludedApps)
            {
                if (string.IsNullOrWhiteSpace(excluded))
                    continue;

                var candidate = excluded.Trim();
                if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    candidate = candidate[..^4];

                if (candidate.Length == 0)
                    continue;

                if (string.Equals(pname, candidate, StringComparison.OrdinalIgnoreCase)
                    || pname.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Could not check foreground process: {ex.Message}");
        }

        return false;
    }

    private void DisposeHwndSource(HwndSource hwndSource, bool wasListening)
    {
        if (wasListening && !hwndSource.IsDisposed && hwndSource.Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(hwndSource.Handle);
        }

        hwndSource.RemoveHook(WndProc);
        if (!hwndSource.IsDisposed)
            hwndSource.Dispose();
    }

    public void Dispose()
    {
        HwndSource? hwndSource;
        CancellationTokenSource? retryCts;
        bool wasListening;

        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            hwndSource = _hwndSource;
            _hwndSource = null;
            wasListening = _isListening;
            _isListening = false;
            retryCts = _retryCts;
            _retryCts = null;
            _selfWriteDepth = 0;
            _selfWriteSucceeded = false;
            _suppressedSequenceNumber = 0;
            _suppressedUntilTickMs = 0;
            _lastCapturedText = null;
            _lastCapturedTextTickMs = 0;
            _lastCapturedItem = null;
            _lastCapturedStarred = false;

            if (_lastTextHash is not null)
            {
                CryptographicOperations.ZeroMemory(_lastTextHash);
                _lastTextHash = null;
            }

            if (_lastImageHash is not null)
            {
                CryptographicOperations.ZeroMemory(_lastImageHash);
                _lastImageHash = null;
            }
        }

        lock (ActiveMonitorsLock)
        {
            ActiveMonitors.Remove(this);
        }

        if (retryCts is not null)
        {
            try
            {
                retryCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (hwndSource is not null)
        {
            if (hwndSource.CheckAccess())
            {
                DisposeHwndSource(hwndSource, wasListening);
                return;
            }

            if (wasListening)
            {
                try
                {
                    if (!hwndSource.IsDisposed && hwndSource.Handle != IntPtr.Zero)
                        RemoveClipboardFormatListener(hwndSource.Handle);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (!hwndSource.Dispatcher.HasShutdownStarted && !hwndSource.Dispatcher.HasShutdownFinished)
            {
                try
                {
                    hwndSource.Dispatcher.BeginInvoke(() => DisposeHwndSource(hwndSource, wasListening: false));
                }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }
    }
}
