using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using SmartClipVault.Models;

namespace SmartClipVault.Services;

public sealed class ClipboardMonitorService : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private const uint CfUnicodeText = 13;
    private const int ErrorAccessDenied = 5;
    private const int MaxRetries = 5;
    private const int MaxClipboardChars = 4_000_000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Lock _stateLock = new();
    private HwndSource? _hwndSource;
    private CancellationTokenSource? _retryCts;
    private bool _isListening;
    private bool _disposed;
    private byte[]? _lastTextHash;

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

                if (TryReadClipboardText(hwnd, out var text, out var shouldRetry))
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
    {
        var hash = SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan()));

        lock (_stateLock)
        {
            if (_lastTextHash is not null && CryptographicOperations.FixedTimeEquals(_lastTextHash, hash))
            {
                return true;
            }

            if (_lastTextHash is not null)
            {
                CryptographicOperations.ZeroMemory(_lastTextHash);
            }

            _lastTextHash = hash;
            return false;
        }
    }

    private static bool TryReadClipboardText(IntPtr hwnd, out string? text, out bool shouldRetry)
    {
        text = null;
        shouldRetry = false;

        if (!OpenClipboard(hwnd))
        {
            var error = Marshal.GetLastWin32Error();
            shouldRetry = error == ErrorAccessDenied;
            return false;
        }

        try
        {
            if (!IsClipboardFormatAvailable(CfUnicodeText))
                return true;

            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
                return false;

            var byteSize = GlobalSize(handle);
            if (byteSize < sizeof(char))
                return true;

            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero)
                return false;

            try
            {
                text = ReadBoundedUnicodeString(ptr, byteSize);
                return true;
            }
            finally
            {
                GlobalUnlock(handle);
            }
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
