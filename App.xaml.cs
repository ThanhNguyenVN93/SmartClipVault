using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using SmartClipVault.Data;
using SmartClipVault.Models;
using SmartClipVault.Services;
using SmartClipVault.Views;

namespace SmartClipVault;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MenuItem? _showMenuItem;
    private MenuItem? _exitMenuItem;
    private MenuItem? _backupMenuItem;
    private MenuItem? _restoreMenuItem;
    private MenuItem? _langMenuItem;
    private MenuItem? _langEnMenuItem;
    private MenuItem? _langViMenuItem;
    private DatabaseService? _databaseService;
    private ClipboardMonitorService? _clipboardMonitor;
    private OcrService? _ocrService;
    private Channel<ClipItem>? _clipQueue;
    private Task? _clipWriterTask;
    private Timer? _sensitiveClipCleanupTimer;
    private int _sensitiveCleanupRunning;
    private int _cleanedUp;
    private GlobalHotkeyService? _hotkeyService;
    private QuickPasteWindow? _quickPasteWindow;
    private CancellationTokenSource? _quickPasteLoadCts;
    private BackupService? _backupService;
    private readonly LocalizationService _localizationService = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _isBackupOrRestoreRunning;

    internal LocalizationService Localization => _localizationService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _databaseService = new DatabaseService();
            _databaseService.Initialize();
            _backupService = new BackupService(_databaseService);
            _localizationService.LanguageChanged += OnLanguageChanged;

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
            InitializeHotkey();
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

        _showMenuItem = new MenuItem();
        _showMenuItem.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Tray_Open");
        _showMenuItem.Click += OnShowMenuItemClick;

        _backupMenuItem = new MenuItem();
        _backupMenuItem.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Tray_Backup");
        _backupMenuItem.Click += OnTrayBackupClick;

        _restoreMenuItem = new MenuItem();
        _restoreMenuItem.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Tray_Restore");
        _restoreMenuItem.Click += OnTrayRestoreClick;

        _langMenuItem = new MenuItem();
        _langMenuItem.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Tray_Language");
        _langEnMenuItem = new MenuItem { Header = "EN", IsCheckable = true };
        _langEnMenuItem.Click += OnLangEnMenuClick;
        _langViMenuItem = new MenuItem { Header = "VI", IsCheckable = true };
        _langViMenuItem.Click += OnLangViMenuClick;
        _langMenuItem.Items.Add(_langEnMenuItem);
        _langMenuItem.Items.Add(_langViMenuItem);
        UpdateLanguageMenuChecks(_localizationService.CurrentLanguage);

        _exitMenuItem = new MenuItem();
        _exitMenuItem.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Tray_Exit");
        _exitMenuItem.Click += OnExitMenuItemClick;

        contextMenu.Items.Add(_showMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(_backupMenuItem);
        contextMenu.Items.Add(_restoreMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(_langMenuItem);
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

    private async void OnTrayBackupClick(object sender, RoutedEventArgs e)
        => await RunBackupDialogAsync().ConfigureAwait(true);

    private async void OnTrayRestoreClick(object sender, RoutedEventArgs e)
        => await RunRestoreDialogAsync().ConfigureAwait(true);

    internal Task RunBackupDialogAsync(Window? owner = null, CancellationToken cancellationToken = default)
    {
        if (_backupService is null || Volatile.Read(ref _cleanedUp) != 0 || _isBackupOrRestoreRunning)
            return Task.CompletedTask;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = TryGetResourceString("Dlg_BackupTitle", "Backup SmartClipVault"),
            Filter = TryGetResourceString("Dlg_ZipFilter", "Zip archive|*.zip"),
            FileName = $"SmartClipVault_backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
        };

        if (!ShowFileDialog(dlg, owner) || Volatile.Read(ref _cleanedUp) != 0 || cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;

        return RunBackupTransferAsync(
            owner,
            cancellationToken,
            (backup, ct) => backup.ExportAsync(dlg.FileName, ct),
            "Dlg_BackupSuccess", "Backup created successfully.",
            "Dlg_BackupFailed", "Backup failed: {0}");
    }

    internal Task RunRestoreDialogAsync(Window? owner = null, CancellationToken cancellationToken = default)
    {
        if (_backupService is null || Volatile.Read(ref _cleanedUp) != 0 || _isBackupOrRestoreRunning)
            return Task.CompletedTask;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = TryGetResourceString("Dlg_RestoreTitle", "Restore SmartClipVault"),
            Filter = TryGetResourceString("Dlg_ZipFilter", "Zip archive|*.zip")
        };

        if (!ShowFileDialog(dlg, owner) || Volatile.Read(ref _cleanedUp) != 0 || cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;

        return RunBackupTransferAsync(
            owner,
            cancellationToken,
            async (backup, ct) =>
            {
                await backup.RestoreAsync(dlg.FileName, ct).ConfigureAwait(true);
                if (Volatile.Read(ref _cleanedUp) != 0 || cancellationToken.IsCancellationRequested)
                    return;

                if (MainWindow is MainWindow mainWin && mainWin.IsLoaded)
                    await mainWin.ReloadDataAsync().ConfigureAwait(true);

                if (_quickPasteWindow is { IsVisible: true })
                    await LoadQuickPasteClipsAsync().ConfigureAwait(true);
            },
            "Dlg_RestoreSuccess", "Restore completed successfully.",
            "Dlg_RestoreFailed", "Restore failed: {0}");
    }

    private async Task RunBackupTransferAsync(
        Window? owner,
        CancellationToken cancellationToken,
        Func<BackupService, CancellationToken, Task> operation,
        string successKey,
        string successFallback,
        string failedKey,
        string failedFallback)
    {
        if (_backupService is null || Volatile.Read(ref _cleanedUp) != 0 || _isBackupOrRestoreRunning)
            return;

        _isBackupOrRestoreRunning = true;
        SetBackupRestoreUiEnabled(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        try
        {
            await operation(_backupService, linkedCts.Token).ConfigureAwait(true);
            if (Volatile.Read(ref _cleanedUp) != 0 || cancellationToken.IsCancellationRequested)
                return;

            ShowMessageBox(owner, TryGetResourceString(successKey, successFallback), MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Backup/restore operation failed: {ex}");
            if (Volatile.Read(ref _cleanedUp) != 0 || cancellationToken.IsCancellationRequested)
                return;

            var fmt = TryGetResourceString(failedKey, failedFallback);
            ShowMessageBox(owner, FormatResourceMessage(fmt, ex.Message), MessageBoxImage.Error);
        }
        finally
        {
            _isBackupOrRestoreRunning = false;
            if (Volatile.Read(ref _cleanedUp) == 0)
                SetBackupRestoreUiEnabled(true);
        }
    }

    private bool ShowFileDialog(Microsoft.Win32.CommonDialog dialog, Window? owner)
        => (owner is { IsLoaded: true } && ReferenceEquals(MainWindow, owner)
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog()) == true;

    private void ShowMessageBox(Window? owner, string message, MessageBoxImage icon)
    {
        if (Volatile.Read(ref _cleanedUp) != 0)
            return;

        if (owner is { IsLoaded: true } && ReferenceEquals(MainWindow, owner))
            MessageBox.Show(owner, message, "SmartClipVault", MessageBoxButton.OK, icon);
        else
            MessageBox.Show(message, "SmartClipVault", MessageBoxButton.OK, icon);
    }

    private void SetBackupRestoreUiEnabled(bool enabled)
    {
        if (_backupMenuItem is not null)
            _backupMenuItem.IsEnabled = enabled;
        if (_restoreMenuItem is not null)
            _restoreMenuItem.IsEnabled = enabled;
        if (MainWindow is MainWindow mainWin && mainWin.IsLoaded)
            mainWin.SetBackupRestoreEnabled(enabled);
    }

    private string TryGetResourceString(string key, string fallback)
        => TryFindResource(key) as string ?? fallback;

    private static string FormatResourceMessage(string format, string arg)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, arg);
        }
        catch (FormatException)
        {
            return $"{format} {arg}";
        }
    }

    private void OnLangEnMenuClick(object sender, RoutedEventArgs e)
    {
        SetLanguage("en");
        UpdateLanguageMenuChecks(_localizationService.CurrentLanguage);
    }

    private void OnLangViMenuClick(object sender, RoutedEventArgs e)
    {
        SetLanguage("vi");
        UpdateLanguageMenuChecks(_localizationService.CurrentLanguage);
    }

    private void OnLanguageChanged(object? sender, string langCode)
        => UpdateLanguageMenuChecks(langCode);

    private void UpdateLanguageMenuChecks(string langCode)
    {
        if (_langEnMenuItem is not null)
            _langEnMenuItem.IsChecked = string.Equals(langCode, "en", StringComparison.OrdinalIgnoreCase);
        if (_langViMenuItem is not null)
            _langViMenuItem.IsChecked = string.Equals(langCode, "vi", StringComparison.OrdinalIgnoreCase);
    }

    internal void SetLanguage(string langCode)
    {
        try
        {
            _localizationService.SetLanguage(langCode);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to switch language to '{langCode}': {ex}");
        }
    }

    internal DatabaseService? GetDatabaseService() => _databaseService;

    private void OnShowMenuItemClick(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void OnExitMenuItemClick(object sender, RoutedEventArgs e) => Shutdown();

    private void OnTrayMouseDoubleClick(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void InitializeHotkey()
    {
        _hotkeyService = new GlobalHotkeyService();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        if (!_hotkeyService.Register())
        {
            Trace.TraceWarning("Global hotkey Ctrl+Shift+V could not be registered (may be in use by another app).");
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _cleanedUp) != 0 || _databaseService is null)
            return;

        if (_quickPasteWindow is null)
        {
            _quickPasteWindow = new QuickPasteWindow();
            _quickPasteWindow.Closed += OnQuickPasteWindowClosed;
        }

        _quickPasteWindow.BeginLoading();
        _quickPasteWindow.ShowNearCursor();
        _ = LoadQuickPasteClipsAsync();
    }

    private async Task LoadQuickPasteClipsAsync()
    {
        if (_databaseService is null)
            return;

        _quickPasteLoadCts?.Cancel();
        _quickPasteLoadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _quickPasteLoadCts = cts;

        try
        {
            var clips = await _databaseService.GetRecentClipsAsync(null, 9, cts.Token).ConfigureAwait(true);
            if (cts.Token.IsCancellationRequested || Volatile.Read(ref _cleanedUp) != 0)
                return;

            if (_quickPasteWindow is { IsVisible: true } popup)
                popup.LoadClips(clips);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to load quick paste clips: {ex}");
        }
    }

    private void OnQuickPasteWindowClosed(object? sender, EventArgs e)
    {
        if (sender is QuickPasteWindow popup)
        {
            popup.Closed -= OnQuickPasteWindowClosed;
            if (ReferenceEquals(_quickPasteWindow, popup))
                _quickPasteWindow = null;
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            var window = new MainWindow();
            window.Closed += OnMainWindowClosed;
            if (_databaseService is not null)
                window.Initialize(_databaseService);
            window.SetBackupRestoreEnabled(!_isBackupOrRestoreRunning);
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

        try { _shutdownCts.Cancel(); } catch (ObjectDisposedException) { }
        _localizationService.LanguageChanged -= OnLanguageChanged;

        _quickPasteLoadCts?.Cancel();
        _quickPasteLoadCts?.Dispose();
        _quickPasteLoadCts = null;

        if (_hotkeyService is not null)
        {
            _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
            _hotkeyService.Dispose();
            _hotkeyService = null;
        }

        if (_quickPasteWindow is not null)
        {
            _quickPasteWindow.Closed -= OnQuickPasteWindowClosed;
            _quickPasteWindow.ForceClose();
            _quickPasteWindow = null;
        }

        if (MainWindow is not null)
        {
            MainWindow.Closed -= OnMainWindowClosed;
            MainWindow.Close();
            MainWindow = null;
        }

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

        if (_backupMenuItem is not null)
        {
            _backupMenuItem.Click -= OnTrayBackupClick;
            _backupMenuItem = null;
        }

        if (_restoreMenuItem is not null)
        {
            _restoreMenuItem.Click -= OnTrayRestoreClick;
            _restoreMenuItem = null;
        }

        if (_langEnMenuItem is not null)
        {
            _langEnMenuItem.Click -= OnLangEnMenuClick;
            _langEnMenuItem = null;
        }

        if (_langViMenuItem is not null)
        {
            _langViMenuItem.Click -= OnLangViMenuClick;
            _langViMenuItem = null;
        }

        if (_langMenuItem is not null)
        {
            _langMenuItem.Items.Clear();
            _langMenuItem = null;
        }

        if (_exitMenuItem is not null)
        {
            _exitMenuItem.Click -= OnExitMenuItemClick;
            _exitMenuItem = null;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.TrayMouseDoubleClick -= OnTrayMouseDoubleClick;
            if (_trayIcon.ContextMenu is ContextMenu cm)
                cm.Items.Clear();
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
