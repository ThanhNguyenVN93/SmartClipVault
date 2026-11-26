using System;
using System.Buffers;
using System.Buffers.Binary;
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
    private const int ErrorAccessDenied = 5;
    private const int MaxRetries = 5;
    private const int MaxClipboardChars = 4_000_000;
    private const int MaxClipboardImageBytes = 64 * 1024 * 1024;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Lock _stateLock = new();
    private HwndSource? _hwndSource;
    private CancellationTokenSource? _retryCts;
    private bool _isListening;
    private volatile bool _disposed;
    private byte[]? _lastTextHash;
    private byte[]? _lastImageHash;

    public event EventHandler<ClipItem>? ClipCaptured;

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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && !_disposed)
        {
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

            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                    return;

                var hwnd = _hwndSource?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero)
                    return;

                if (TryReadClipboardData(hwnd, out var text, out var dibBytes, out var shouldRetry))
                {
                    if (!string.IsNullOrWhiteSpace(text) && !IsDuplicateText(text))
                    {
                        var item = new ClipItem
                        {
                            ContentType = ClipContentType.Text,
                            TextContent = text.Trim(),
                            CreatedAt = DateTime.UtcNow
                        };

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
                    else if (string.IsNullOrWhiteSpace(text) && dibBytes is not null && !IsDuplicateImage(dibBytes))
                    {
                        await SaveAndEmitImageAsync(dibBytes, cancellationToken).ConfigureAwait(false);
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

    private static bool TryReadClipboardData(IntPtr hwnd, out string? text, out byte[]? dibBytes, out bool shouldRetry)
    {
        text = null;
        dibBytes = null;
        shouldRetry = false;

        if (!OpenClipboard(hwnd))
        {
            var error = Marshal.GetLastWin32Error();
            shouldRetry = error == ErrorAccessDenied;
            return false;
        }

        try
        {
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

    private async Task SaveAndEmitImageAsync(byte[] dibBytes, CancellationToken cancellationToken)
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

    private void DisposeHwndSource(HwndSource hwndSource, bool wasListening)
    {
        if (wasListening && hwndSource.Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(hwndSource.Handle);
        }

        hwndSource.RemoveHook(WndProc);
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
            }
            else if (!hwndSource.Dispatcher.HasShutdownStarted && !hwndSource.Dispatcher.HasShutdownFinished)
            {
                try
                {
                    hwndSource.Dispatcher.Invoke(() => DisposeHwndSource(hwndSource, wasListening));
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }
}
