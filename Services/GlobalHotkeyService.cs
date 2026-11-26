using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SmartClipVault.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_V = 0x56;
    private const int HotkeyId = 9001;
    private static readonly IntPtr HwndMessage = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event EventHandler? HotkeyPressed;

    private HwndSource? _hwndSource;
    private bool _ownsHwndSource;
    private bool _isRegistered;
    private bool _disposed;

    public bool IsRegistered => _isRegistered;

    public static bool TryParseHotkey(string? hotkey, out uint modifiers, out uint vk)
    {
        modifiers = MOD_NOREPEAT;
        vk = 0;

        if (string.IsNullOrWhiteSpace(hotkey))
            return false;

        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= MOD_CONTROL;
                    break;
                case "SHIFT":
                    modifiers |= MOD_SHIFT;
                    break;
                case "ALT":
                    modifiers |= MOD_ALT;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    if (part.Length == 1)
                        vk = (uint)char.ToUpperInvariant(part[0]);
                    else
                        return false;
                    break;
            }
        }

        return vk != 0 && (modifiers & ~MOD_NOREPEAT) != 0;
    }

    public bool Register(Window? owner = null, string? hotkey = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        UnregisterCurrent();

        uint fsModifiers;
        uint vk;
        string hotkeyLabel;
        if (!string.IsNullOrWhiteSpace(hotkey) && TryParseHotkey(hotkey, out var parsedMods, out var parsedVk))
        {
            fsModifiers = parsedMods;
            vk = parsedVk;
            hotkeyLabel = hotkey;
        }
        else
        {
            fsModifiers = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
            vk = VK_V;
            hotkeyLabel = "Ctrl+Shift+V";
        }

        HwndSource? hwndSource = null;
        var ownsSource = false;

        try
        {
            if (owner is null)
            {
                var parameters = new HwndSourceParameters("SmartClipVaultGlobalHotkey")
                {
                    ParentWindow = HwndMessage,
                    Width = 0,
                    Height = 0,
                    PositionX = 0,
                    PositionY = 0,
                    WindowStyle = 0
                };
                hwndSource = new HwndSource(parameters);
                ownsSource = true;
            }
            else
            {
                var helper = new WindowInteropHelper(owner);
                var handle = helper.EnsureHandle();
                hwndSource = HwndSource.FromHwnd(handle);
                if (hwndSource is null)
                    return false;
            }

            hwndSource.AddHook(WndProc);

            if (!RegisterHotKey(hwndSource.Handle, HotkeyId, fsModifiers, vk))
            {
                var error = Marshal.GetLastWin32Error();
                Trace.TraceWarning($"Failed to register global hotkey {hotkeyLabel}. Win32 error: {error}");
                CleanupHwndSource(hwndSource, ownsSource, unregisterHotkey: false);
                return false;
            }

            _hwndSource = hwndSource;
            _ownsHwndSource = ownsSource;
            _isRegistered = true;
            return true;
        }
        catch
        {
            if (hwndSource is not null)
                CleanupHwndSource(hwndSource, ownsSource, unregisterHotkey: false);
            throw;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed || !_isRegistered)
            return IntPtr.Zero;

        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"GlobalHotkeyService HotkeyPressed handler failed: {ex}");
            }
        }

        return IntPtr.Zero;
    }

    private void UnregisterCurrent()
    {
        var hwndSource = _hwndSource;
        var ownsSource = _ownsHwndSource;
        var wasRegistered = _isRegistered;

        _hwndSource = null;
        _ownsHwndSource = false;
        _isRegistered = false;

        if (hwndSource is not null)
            DisposeOnDispatcher(hwndSource, ownsSource, wasRegistered);
    }

    private void DisposeOnDispatcher(HwndSource hwndSource, bool ownsSource, bool wasRegistered)
    {
        if (hwndSource.CheckAccess())
        {
            CleanupHwndSource(hwndSource, ownsSource, wasRegistered);
            return;
        }

        if (!hwndSource.Dispatcher.HasShutdownStarted && !hwndSource.Dispatcher.HasShutdownFinished)
        {
            try
            {
                hwndSource.Dispatcher.Invoke(() => CleanupHwndSource(hwndSource, ownsSource, wasRegistered));
                return;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (wasRegistered && !hwndSource.IsDisposed && hwndSource.Handle != IntPtr.Zero)
            UnregisterHotKey(hwndSource.Handle, HotkeyId);
    }

    private void CleanupHwndSource(HwndSource hwndSource, bool ownsSource, bool unregisterHotkey)
    {
        if (unregisterHotkey && !hwndSource.IsDisposed && hwndSource.Handle != IntPtr.Zero)
        {
            if (!UnregisterHotKey(hwndSource.Handle, HotkeyId))
            {
                var error = Marshal.GetLastWin32Error();
                Trace.TraceWarning($"UnregisterHotKey failed with Win32 error: {error}");
            }
        }

        hwndSource.RemoveHook(WndProc);
        if (ownsSource && !hwndSource.IsDisposed)
            hwndSource.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        UnregisterCurrent();
    }
}
