using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SmartClipVault.Models;
using SmartClipVault.Services;

namespace SmartClipVault.Views;

public partial class QuickPasteWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private record ClipRow(int Index, string Preview, ClipItem Clip);

    private List<ClipItem> _clips = [];
    private POINT _lastCursorPos;
    private bool _hasCursorPos;
    private bool _isOpening;
    private bool _isPasting;
    private bool _allowClose;

    public QuickPasteWindow()
    {
        InitializeComponent();
    }

    public void BeginLoading()
    {
        _clips = [];
        ClipList.ItemsSource = null;
        EmptyText.Visibility = Visibility.Collapsed;
        ClipList.Visibility = Visibility.Visible;
    }

    public void LoadClips(IEnumerable<ClipItem> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        _clips = [.. clips];
        var rows = new List<ClipRow>();
        var count = Math.Min(_clips.Count, 9);
        for (int i = 0; i < count; i++)
        {
            var clip = _clips[i];
            rows.Add(new ClipRow(i + 1, PasteHelper.FormatClipPreview(clip), clip));
        }

        ClipList.ItemsSource = rows;
        if (rows.Count > 0)
        {
            EmptyText.Visibility = Visibility.Collapsed;
            ClipList.Visibility = Visibility.Visible;
            ClipList.SelectedIndex = 0;
        }
        else
        {
            ClipList.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
        }

        if (IsVisible)
        {
            UpdateLayout();
            ClampToMonitorWorkArea();
        }
    }

    public void ShowNearCursor()
    {
        var ownHwnd = new WindowInteropHelper(this).EnsureHandle();
        PasteHelper.CaptureForegroundWindow(ownHwnd);

        if (GetCursorPos(out var pos))
        {
            _lastCursorPos = pos;
            _hasCursorPos = true;
            PositionAtCursor(pos);
        }

        _isOpening = true;
        try
        {
            Show();
            Activate();
            ClipList.Focus();
        }
        finally
        {
            _isOpening = false;
        }

        UpdateLayout();
        ClampToMonitorWorkArea();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_isOpening || _isPasting || _allowClose || !IsVisible)
            return;

        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            _ = PasteAtIndexAsync(e.Key - Key.D1);
            e.Handled = true;
            return;
        }

        if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9)
        {
            _ = PasteAtIndexAsync(e.Key - Key.NumPad1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            _ = PasteAtIndexAsync(ClipList.SelectedIndex);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement(ClipList, source) is not ListBoxItem item)
            return;

        var idx = ClipList.ItemContainerGenerator.IndexFromContainer(item);
        if (idx >= 0)
        {
            e.Handled = true;
            _ = PasteAtIndexAsync(idx);
        }
    }

    private async System.Threading.Tasks.Task PasteAtIndexAsync(int idx)
    {
        if (_isPasting || idx < 0 || idx >= _clips.Count)
            return;

        var clip = _clips[idx];
        if (clip.IsSensitive)
            return;

        _isPasting = true;
        try
        {
            if (clip.ContentType == ClipContentType.Image && !string.IsNullOrWhiteSpace(clip.ImagePath))
                await PasteHelper.PasteImageAsync(clip.ImagePath, Hide).ConfigureAwait(true);
            else if (!string.IsNullOrEmpty(clip.TextContent))
                await PasteHelper.PasteTextAsync(clip.TextContent, Hide).ConfigureAwait(true);
            else
                Hide();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"QuickPaste failed: {ex}");
            if (IsVisible)
                Hide();
        }
        finally
        {
            _isPasting = false;
        }
    }

    private void PositionAtCursor(POINT pos)
    {
        var dipPoint = DeviceToDip(pos);
        Left = dipPoint.X;
        Top = dipPoint.Y;
    }

    private void ClampToMonitorWorkArea()
    {
        if (!_hasCursorPos)
            return;

        var hMonitor = MonitorFromPoint(_lastCursorPos, MonitorDefaultToNearest);
        if (hMonitor == IntPtr.Zero)
            return;

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref info))
            return;

        var topLeft = DeviceToDip(new POINT { X = info.rcWork.Left, Y = info.rcWork.Top });
        var bottomRight = DeviceToDip(new POINT { X = info.rcWork.Right, Y = info.rcWork.Bottom });

        var popupWidth = ActualWidth > 0 ? ActualWidth : Width;
        var popupHeight = ActualHeight > 0 ? ActualHeight : 120;

        var maxLeft = Math.Max(topLeft.X, bottomRight.X - popupWidth);
        var maxTop = Math.Max(topLeft.Y, bottomRight.Y - popupHeight);

        Left = Math.Clamp(Left, topLeft.X, maxLeft);
        Top = Math.Clamp(Top, topLeft.Y, maxTop);
    }

    private Point DeviceToDip(POINT devicePoint)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
            return source.CompositionTarget.TransformFromDevice.Transform(new Point(devicePoint.X, devicePoint.Y));

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        return new Point(devicePoint.X / scaleX, devicePoint.Y / scaleY);
    }
}
