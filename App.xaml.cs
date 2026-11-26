using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using SmartClipVault.Data;
using SmartClipVault.Models;
using SmartClipVault.Services;

namespace SmartClipVault;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MenuItem? _showMenuItem;
    private MenuItem? _exitMenuItem;
    private DatabaseService? _databaseService;
    private ClipboardMonitorService? _clipboardMonitor;
    private OcrService? _ocrService;
    private Channel<ClipItem>? _clipQueue;
    private Task? _clipWriterTask;
    private Timer? _sensitiveClipCleanupTimer;
    private int _sensitiveCleanupRunning;
    private int _cleanedUp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _databaseService = new DatabaseService();
            _databaseService.Initialize();

            _clipQueue = Channel.CreateBounded<ClipItem>(new BoundedChannelOptions(1024)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _clipWriterTask = Task.Run(ProcessClipQueueAsync);

            _ocrService = new OcrService(_databaseService, ClipboardMonitorService.ImageDirectory);

            _clipboardMonitor = new ClipboardMonitorService();
            _clipboardMonitor.ClipCaptured += OnClipCaptured;
            _clipboardMonitor.Start();

            _sensitiveClipCleanupTimer = new Timer(
                OnSensitiveClipCleanupTick,
                null,
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60));

            InitializeTrayIcon();
        }
        catch
        {
            CleanupResources();
            throw;
        }
    }

    private void OnClipCaptured(object? sender, ClipItem item)
    {
        _clipQueue?.Writer.TryWrite(item);
    }

    private async Task ProcessClipQueueAsync()
    {
        if (_clipQueue is null || _databaseService is null)
            return;

        await foreach (var item in _clipQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (item.ContentType == ClipContentType.Text && SecretDetector.IsSensitive(item.TextContent))
                    item.IsSensitive = true;

                var id = _databaseService.InsertClip(item);
                item.Id = id;

                if (item.ContentType == ClipContentType.Image)
                    _ocrService?.Enqueue(item);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to persist captured clip: {ex}");
            }
        }
    }

    private void InitializeTrayIcon()
    {
        var contextMenu = new ContextMenu();

        _showMenuItem = new MenuItem { Header = "Open SmartClipVault" };
        _showMenuItem.Click += OnShowMenuItemClick;

        _exitMenuItem = new MenuItem { Header = "Exit" };
        _exitMenuItem.Click += OnExitMenuItemClick;

        contextMenu.Items.Add(_showMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(_exitMenuItem);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "SmartClipVault",
            Icon = SystemIcons.Application,
            ContextMenu = contextMenu
        };
        _trayIcon.TrayMouseDoubleClick += OnTrayMouseDoubleClick;
    }

    private void OnShowMenuItemClick(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void OnExitMenuItemClick(object sender, RoutedEventArgs e) => Shutdown();

    private void OnTrayMouseDoubleClick(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            var window = new MainWindow();
            window.Closed += OnMainWindowClosed;
            MainWindow = window;
        }

        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }
        MainWindow.Activate();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Closed -= OnMainWindowClosed;
            if (ReferenceEquals(MainWindow, window))
            {
                MainWindow = null;
            }
        }
    }

    private void OnSensitiveClipCleanupTick(object? state)
    {
        if (Volatile.Read(ref _cleanedUp) != 0)
            return;

        if (Interlocked.CompareExchange(ref _sensitiveCleanupRunning, 1, 0) != 0)
            return;

        try
        {
            if (Volatile.Read(ref _cleanedUp) == 0)
            {
                _databaseService?.DeleteExpiredSensitiveClips(TimeSpan.FromSeconds(60));
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to delete expired sensitive clips: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _sensitiveCleanupRunning, 0);
        }
    }

    private void CleanupResources()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) != 0)
            return;

        if (_clipboardMonitor is not null)
        {
            _clipboardMonitor.ClipCaptured -= OnClipCaptured;
            _clipboardMonitor.Dispose();
            _clipboardMonitor = null;
        }

        _clipQueue?.Writer.TryComplete();
        if (_clipWriterTask is not null)
        {
            try
            {
                _clipWriterTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Error flushing clip persistence queue on exit: {ex}");
            }

            _clipWriterTask = null;
        }

        _clipQueue = null;

        _ocrService?.Dispose();
        _ocrService = null;

        if (_sensitiveClipCleanupTimer is not null)
        {
            try
            {
                _sensitiveClipCleanupTimer.Change(Timeout.Infinite, Timeout.Infinite);
                using var timerStopped = new ManualResetEvent(false);
                if (_sensitiveClipCleanupTimer.Dispose(timerStopped))
                {
                    timerStopped.WaitOne(TimeSpan.FromSeconds(2));
                }
            }
            catch (ObjectDisposedException)
            {
            }

            _sensitiveClipCleanupTimer = null;
        }

        if (_showMenuItem is not null)
        {
            _showMenuItem.Click -= OnShowMenuItemClick;
            _showMenuItem = null;
        }

        if (_exitMenuItem is not null)
        {
            _exitMenuItem.Click -= OnExitMenuItemClick;
            _exitMenuItem = null;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.TrayMouseDoubleClick -= OnTrayMouseDoubleClick;
            _trayIcon.ContextMenu = null;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CleanupResources();
        base.OnExit(e);
    }
}
