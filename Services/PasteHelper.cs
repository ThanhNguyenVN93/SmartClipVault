using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using SmartClipVault.Models;

namespace SmartClipVault.Services;

public static class PasteHelper
{
    private const long MaxImageFileBytes = 64L * 1024 * 1024;
    private const int MaxDecodedImageDimension = 16384;
    private const long MaxDecodedImagePixels = 64_000_000L;
    private const int SwRestore = 9;
    private const int PasteFocusDelayMs = 50;

    private const uint INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_V = 0x56;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    // MOUSEINPUT and HARDWAREINPUT are required so Marshal.SizeOf<INPUT>() matches native sizeof(INPUT) (40 bytes on x64).
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private static IntPtr _previousForeground = IntPtr.Zero;

    public static IntPtr CaptureForegroundWindow(IntPtr excludeHwnd = default)
    {
        var current = GetForegroundWindow();
        if (current != IntPtr.Zero && current != excludeHwnd && IsWindow(current))
            _previousForeground = current;
        return _previousForeground;
    }

    public static string FormatClipPreview(ClipItem clip, int maxLength = 250)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

        if (clip.IsSensitive)
            return "***";

        var raw = clip.ContentType == ClipContentType.Image
            ? $"[Image] {clip.OcrText ?? Path.GetFileName(clip.ImagePath) ?? string.Empty}"
            : clip.TextContent ?? string.Empty;

        var singleLine = raw.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (singleLine.Length <= maxLength)
            return singleLine;

        return string.Concat(singleLine.AsSpan(0, maxLength), "…");
    }

    public static bool TryValidateImagePath(string? imagePath, out string fullPath, string? allowedDirectory = null)
    {
        if (!TryNormalizeContainedImagePath(imagePath, out var candidate, allowedDirectory))
        {
            fullPath = string.Empty;
            return false;
        }

        try
        {
            var baseDir = string.IsNullOrWhiteSpace(allowedDirectory)
                ? ClipboardMonitorService.ImageDirectory
                : allowedDirectory;
            var normalizedBaseNoSlash = Path.GetFullPath(baseDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (HasReparsePointSubdirectory(candidate, normalizedBaseNoSlash))
            {
                fullPath = string.Empty;
                return false;
            }

            var fileInfo = new FileInfo(candidate);
            if (!fileInfo.Exists ||
                (fileInfo.Attributes & FileAttributes.Directory) != 0 ||
                (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                fileInfo.Length <= 0 ||
                fileInfo.Length > MaxImageFileBytes)
            {
                fullPath = string.Empty;
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    internal static bool TryNormalizeContainedImagePath(string? imagePath, out string fullPath, string? allowedDirectory = null)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(imagePath) || !Path.IsPathFullyQualified(imagePath))
            return false;

        if (HasTraversalPathSegment(imagePath))
            return false;

        try
        {
            var candidate = Path.GetFullPath(imagePath);
            if (candidate.StartsWith(@"\\", StringComparison.Ordinal))
                return false;

            // Reject NTFS Alternate Data Streams (e.g. "file.png:stream") while permitting the drive colon at index 1.
            if (candidate.IndexOf(':', 2) >= 0)
                return false;

            var baseDir = string.IsNullOrWhiteSpace(allowedDirectory)
                ? ClipboardMonitorService.ImageDirectory
                : allowedDirectory;
            var normalizedDir = Path.GetFullPath(baseDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                return false;

            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasTraversalPathSegment(string path)
    {
        foreach (var segment in path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                return true;
        }
        return false;
    }

    internal static bool HasReparsePointSubdirectory(string candidateFile, string normalizedBaseNoSlash)
    {
        var current = Path.GetDirectoryName(candidateFile);
        while (!string.IsNullOrEmpty(current) &&
               current.Length > normalizedBaseNoSlash.Length &&
               !string.Equals(current, normalizedBaseNoSlash, StringComparison.OrdinalIgnoreCase))
        {
            var dirInfo = new DirectoryInfo(current);
            if (!dirInfo.Exists || (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    public static Task<BitmapImage?> TryLoadClipboardImageAsync(string? imagePath, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeContainedImagePath(imagePath, out var normalizedPath))
            return Task.FromResult<BitmapImage?>(null);

        return Task.Run<BitmapImage?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateImagePath(normalizedPath, out var validatedPath))
                return null;

            return LoadBitmapFromValidatedPath(validatedPath, targetPixelSize: null, maxDecodedDimension: null);
        }, cancellationToken);
    }

    public static Task<BitmapImage?> TryLoadThumbnailAsync(
        string? imagePath,
        int targetPixelSize = 96,
        int maxDecodedDimension = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetPixelSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDecodedDimension, 0);

        if (!TryNormalizeContainedImagePath(imagePath, out var normalizedPath))
            return Task.FromResult<BitmapImage?>(null);

        return Task.Run<BitmapImage?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateImagePath(normalizedPath, out var validatedPath))
                return null;

            return LoadBitmapFromValidatedPath(validatedPath, targetPixelSize, maxDecodedDimension);
        }, cancellationToken);
    }

    internal static bool TryGetValidatedImageDimensions(string validatedPath, out int pixelWidth, out int pixelHeight)
    {
        pixelWidth = 0;
        pixelHeight = 0;

        try
        {
            using var stream = new FileStream(
                validatedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return TryReadValidatedFrameDimensions(stream, validatedPath, out pixelWidth, out pixelHeight);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException or ExternalException or OutOfMemoryException or ThreadStateException)
        {
            Trace.TraceWarning($"Failed to inspect clipboard image '{validatedPath}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static BitmapImage? LoadBitmapFromValidatedPath(
        string validatedPath,
        int? targetPixelSize,
        int? maxDecodedDimension)
    {
        try
        {
            using var stream = new FileStream(
                validatedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (!TryReadValidatedFrameDimensions(stream, validatedPath, out var pw, out var ph))
                return null;

            int? decodeWidth = null;
            int? decodeHeight = null;
            if (targetPixelSize is int targetPx && maxDecodedDimension is int maxDim)
                ComputeThumbnailDecodeDimensions(pw, ph, targetPx, maxDim, out decodeWidth, out decodeHeight);

            stream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth is int dw)
                bitmap.DecodePixelWidth = Math.Max(1, dw);
            else if (decodeHeight is int dh)
                bitmap.DecodePixelHeight = Math.Max(1, dh);
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException or ExternalException or OutOfMemoryException or ThreadStateException)
        {
            Trace.TraceWarning($"Failed to load clipboard image '{validatedPath}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool TryReadValidatedFrameDimensions(
        Stream stream,
        string validatedPath,
        out int pixelWidth,
        out int pixelHeight)
    {
        pixelWidth = 0;
        pixelHeight = 0;

        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
            return false;

        var frame = decoder.Frames[0];
        var pw = frame.PixelWidth;
        var ph = frame.PixelHeight;
        if (pw <= 0 || ph <= 0 ||
            pw > MaxDecodedImageDimension || ph > MaxDecodedImageDimension ||
            (long)pw * ph > MaxDecodedImagePixels)
        {
            Trace.TraceWarning($"Skipping oversized or invalid image '{validatedPath}' ({pw}x{ph}).");
            return false;
        }

        pixelWidth = pw;
        pixelHeight = ph;
        return true;
    }

    private static void ComputeThumbnailDecodeDimensions(
        int pw,
        int ph,
        int targetPixelSize,
        int maxDecodedDimension,
        out int? decodeWidth,
        out int? decodeHeight)
    {
        decodeWidth = null;
        decodeHeight = null;

        var effectiveTarget = Math.Min(targetPixelSize, maxDecodedDimension);
        if (pw <= 0 || ph <= 0)
        {
            decodeWidth = effectiveTarget;
            return;
        }

        if (pw >= ph)
        {
            var targetH = Math.Min(effectiveTarget, ph);
            var scaledW = (int)Math.Round((double)pw * targetH / ph);
            if (scaledW > maxDecodedDimension)
                decodeWidth = maxDecodedDimension;
            else
                decodeHeight = targetH;
        }
        else
        {
            var targetW = Math.Min(effectiveTarget, pw);
            var scaledH = (int)Math.Round((double)ph * targetW / pw);
            if (scaledH > maxDecodedDimension)
                decodeHeight = maxDecodedDimension;
            else
                decodeWidth = targetW;
        }
    }

    public static bool TrySetClipboardText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            using var suppression = ClipboardMonitorService.SuppressActiveSelfCopy();
            Clipboard.SetText(text);
            suppression.MarkSucceeded();
            return true;
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            Trace.TraceWarning($"Failed to copy text to clipboard: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static bool TrySetClipboardImage(BitmapSource? image)
    {
        if (image is null)
            return false;

        try
        {
            using var suppression = ClipboardMonitorService.SuppressActiveSelfCopy();
            Clipboard.SetImage(image);
            suppression.MarkSucceeded();
            return true;
        }
        catch (ExternalException ex)
        {
            Trace.TraceWarning($"Failed to copy image to clipboard: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> PasteTextAsync(string? text, Action? hidePopup = null, CancellationToken cancellationToken = default)
    {
        if (!TrySetClipboardText(text))
        {
            hidePopup?.Invoke();
            return false;
        }

        await RestoreAndSendPasteAsync(hidePopup, cancellationToken).ConfigureAwait(true);
        return true;
    }

    public static async Task<bool> PasteImageAsync(string? imagePath, Action? hidePopup = null, CancellationToken cancellationToken = default)
    {
        var image = await TryLoadClipboardImageAsync(imagePath, cancellationToken).ConfigureAwait(true);
        if (!TrySetClipboardImage(image))
        {
            hidePopup?.Invoke();
            return false;
        }

        await RestoreAndSendPasteAsync(hidePopup, cancellationToken).ConfigureAwait(true);
        return true;
    }

    private static async Task RestoreAndSendPasteAsync(Action? hidePopup, CancellationToken cancellationToken)
    {
        hidePopup?.Invoke();
        RestorePreviousForegroundWindow();

        if (_previousForeground == IntPtr.Zero || !IsWindow(_previousForeground))
            return;

        // Allow the target window on Windows 11 to process activation and keyboard focus before Ctrl+V.
        await Task.Delay(PasteFocusDelayMs, cancellationToken).ConfigureAwait(true);
        if (_previousForeground != IntPtr.Zero && IsWindow(_previousForeground))
            SendCtrlV();
    }

    private static void RestorePreviousForegroundWindow()
    {
        var target = _previousForeground;
        if (target == IntPtr.Zero || !IsWindow(target))
            return;

        if (IsIconic(target))
            ShowWindow(target, SwRestore);

        if (SetForegroundWindow(target))
            return;

        var currentThread = GetCurrentThreadId();
        var foregroundWindow = GetForegroundWindow();
        var foregroundThread = foregroundWindow != IntPtr.Zero
            ? GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero)
            : 0;
        var targetThread = GetWindowThreadProcessId(target, IntPtr.Zero);

        var attachedForeground = false;
        var attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);

            BringWindowToTop(target);
            SetForegroundWindow(target);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new List<INPUT>(9);

        // Release any physically held Shift/Alt/Right-Ctrl keys from the hotkey chord so Ctrl+V isn't altered.
        ReleaseKeyIfPressed(inputs, VK_LSHIFT, isExtended: false);
        ReleaseKeyIfPressed(inputs, VK_RSHIFT, isExtended: false);
        ReleaseKeyIfPressed(inputs, VK_LMENU, isExtended: false);
        ReleaseKeyIfPressed(inputs, VK_RMENU, isExtended: true);
        ReleaseKeyIfPressed(inputs, VK_RCONTROL, isExtended: true);

        inputs.Add(CreateKeyboardInput(VK_CONTROL, keyUp: false));
        inputs.Add(CreateKeyboardInput(VK_V, keyUp: false));
        inputs.Add(CreateKeyboardInput(VK_V, keyUp: true));
        inputs.Add(CreateKeyboardInput(VK_CONTROL, keyUp: true));

        var payload = inputs.ToArray();
        var sent = SendInput((uint)payload.Length, payload, Marshal.SizeOf<INPUT>());
        if (sent != payload.Length)
        {
            var error = Marshal.GetLastWin32Error();
            Trace.TraceWarning($"SendInput sent {sent}/{payload.Length} events. Win32 error: {error}");
        }
    }

    private static void ReleaseKeyIfPressed(List<INPUT> inputs, ushort vk, bool isExtended)
    {
        if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            inputs.Add(CreateKeyboardInput(vk, keyUp: true, isExtended: isExtended));
    }

    private static INPUT CreateKeyboardInput(ushort vk, bool keyUp, bool isExtended = false)
    {
        var flags = (keyUp ? KEYEVENTF_KEYUP : 0u) | (isExtended ? KEYEVENTF_EXTENDEDKEY : 0u);
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC),
                    dwFlags = flags
                }
            }
        };
    }
}
