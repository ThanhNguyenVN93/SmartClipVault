using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
    private readonly record struct ClipQueueWork(ClipItem Item, bool IsStarOnly);

    private TaskbarIcon? _trayIcon;
    private MenuItem? _showMenuItem;
    private MenuItem? _settingsMenuItem;
    private MenuItem? _exitMenuItem;
    private MenuItem? _backupMenuItem;
    private MenuItem? _restoreMenuItem;
    private MenuItem? _updateMenuItem;
    private MenuItem? _langMenuItem;
    private MenuItem? _langEnMenuItem;
    private MenuItem? _langViMenuItem;
    private DatabaseService? _databaseService;
    private ClipboardMonitorService? _clipboardMonitor;
    private OcrService? _ocrService;
    private Channel<ClipQueueWork>? _clipQueue;
    private Task? _clipWriterTask;
    private Timer? _sensitiveClipCleanupTimer;
    private int _sensitiveCleanupRunning;
    private int _cleanedUp;
    private GlobalHotkeyService? _hotkeyService;
    private QuickPasteWindow? _quickPasteWindow;
    private CancellationTokenSource? _quickPasteLoadCts;
    private BackupService? _backupService;
    private SettingsService? _settingsService;
    private Views.SettingsWindow? _settingsWindow;
    private readonly LocalizationService _localizationService = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _isBackupOrRestoreRunning;
    private int _isUpdateCheckRunning;

    internal LocalizationService Localization => _localizationService;
    internal ClipboardMonitorService? ClipboardMonitor => _clipboardMonitor;
    internal SettingsService? Settings => _settingsService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            UpdateService.CleanupOldUpdateFiles();
            _ = CleanupOldUpdateFilesDelayedAsync(_shutdownCts.Token);

            _settingsService = new SettingsService();
            var loadedSettings = _settingsService.Load();
            ApplyFontScale(loadedSettings.UiFontScale);
            _settingsService.SettingsChanged += OnSettingsChanged;

            string? customDbPath = null;
            if (!string.IsNullOrWhiteSpace(loadedSettings.DataDirectory))
            {
                Directory.CreateDirectory(loadedSettings.DataDirectory);
                customDbPath = Path.Combine(loadedSettings.DataDirectory, "smartclipvault.db");
                var defaultDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SmartClipVault");
                var defaultDb = Path.Combine(defaultDir, "smartclipvault.db");
                if (!File.Exists(customDbPath) && File.Exists(defaultDb))
                    File.Copy(defaultDb, customDbPath, overwrite: false);
            }

            _databaseService = customDbPath is not null
                ? new DatabaseService(customDbPath)
                : new DatabaseService();
            _databaseService.Initialize();
            _databaseService.ClipsDeleted += OnDatabaseClipsDeleted;
            _backupService = new BackupService(_databaseService);
            _localizationService.LanguageChanged += OnLanguageChanged;

            _clipQueue = Channel.CreateBounded<ClipQueueWork>(new BoundedChannelOptions(1024)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _clipWriterTask = Task.Run(ProcessClipQueueAsync);

            _ocrService = new OcrService(_databaseService, ClipboardMonitorService.ImageDirectory);

            _clipboardMonitor = new ClipboardMonitorService();
            _clipboardMonitor.Settings = _settingsService;
            _clipboardMonitor.ClipCaptured += OnClipCaptured;
            _clipboardMonitor.DoubleCopyStar += OnDoubleCopyStar;
            _clipboardMonitor.Start();

            _sensitiveClipCleanupTimer = new Timer(
                OnSensitiveClipCleanupTick,
                null,
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60));

            InitializeTrayIcon();
            InitializeHotkey();

            if (loadedSettings.AutoClearDays > 0)
                _ = DeleteExpiredUnpinnedClipsSafelyAsync(loadedSettings.AutoClearDays, _shutdownCts.Token);

            if (!loadedSettings.StartHiddenToTray)
                ShowMainWindow();
        }
        catch
        {
            CleanupResources();
            throw;
        }
    }

    private async Task DeleteExpiredUnpinnedClipsSafelyAsync(int days, CancellationToken ct)
    {
        try
        {
            if (_databaseService is null || ct.IsCancellationRequested)
                return;

            await _databaseService.DeleteExpiredUnpinnedClipsAsync(days, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Startup auto-clear of expired clips failed: {ex}");
        }
    }

    private void OnClipCaptured(object? sender, ClipItem item)
    {
        _clipQueue?.Writer.TryWrite(new ClipQueueWork(item, IsStarOnly: false));
    }

    private async Task ProcessClipQueueAsync()
    {
        if (_clipQueue is null || _databaseService is null)
            return;

        await foreach (var work in _clipQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var item = work.Item;
            try
            {
                if (work.IsStarOnly)
                {
                    item.IsStarred = true;
                    if (item.Id > 0)
                    {
                        _databaseService.SetClipStarred(item.Id, true);
                        NotifyMainWindowClipStarred(item.Id);
                    }
                    continue;
                }

                if (item.ContentType == ClipContentType.Text && SecretDetector.IsSensitive(item.TextContent))
                    item.IsSensitive = true;

                var id = _databaseService.InsertClip(item);
                item.Id = id;

                if (item.ContentType == ClipContentType.Image && (_settingsService?.Current.EnableOcr ?? true))
                    _ocrService?.Enqueue(item);

                if (item.IsStarred)
                    NotifyMainWindowClipStarred(id);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to persist captured clip: {ex}");
            }
        }
    }

    private void NotifyMainWindowClipStarred(long clipId)
    {
        if (clipId <= 0 || Volatile.Read(ref _cleanedUp) != 0)
            return;

        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _cleanedUp) == 0 && MainWindow is MainWindow mainWin && mainWin.IsLoaded)
                    mainWin.StarClipById(clipId);
            });
        }
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                return System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Failed to extract app icon for tray: {ex.Message}");
        }
        return SystemIcons.Application;
    }

    private void InitializeTrayIcon()
    {
        var contextMenu = new ContextMenu();

        _showMenuItem = new MenuItem();
        _showMenuItem.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Tray_Open");
        _showMenuItem.Click += OnShowMenuItemClick;

        _settingsMenuItem = new MenuItem();
        _settingsMenuItem.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Tray_Settings");
        _settingsMenuItem.Click += OnTraySettingsClick;

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

        _updateMenuItem = new MenuItem();
        _updateMenuItem.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Tray_CheckUpdates");
        _updateMenuItem.Click += OnTrayCheckUpdatesClick;

        contextMenu.Items.Add(_showMenuItem);
        contextMenu.Items.Add(_settingsMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(_backupMenuItem);
        contextMenu.Items.Add(_restoreMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(_langMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(_updateMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(_exitMenuItem);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "SmartClipVault",
            Icon = LoadTrayIcon(),
            ContextMenu = contextMenu
        };
        _trayIcon.TrayMouseDoubleClick += OnTrayMouseDoubleClick;
    }

    private readonly Queue<ClipItem> _sequentialQueue = new();

    public void EnqueueSequentialClip(ClipItem clip)
    {
        if (clip is null || clip.IsSensitive)
            return;
        _sequentialQueue.Enqueue(clip);
        if (!string.IsNullOrEmpty(clip.TextContent))
            PasteHelper.TrySetClipboardText(clip.TextContent);
    }

    public bool IsLocked { get; private set; }

    internal void SetLocked(bool locked)
    {
        IsLocked = locked;
    }

    private async void OnTrayBackupClick(object sender, RoutedEventArgs e)
    {
        if (IsLocked)
        {
            ShowMainWindow();
            return;
        }
        await RunBackupDialogAsync().ConfigureAwait(true);
    }

    private async void OnTrayRestoreClick(object sender, RoutedEventArgs e)
    {
        if (IsLocked)
        {
            ShowMainWindow();
            return;
        }
        await RunRestoreDialogAsync().ConfigureAwait(true);
    }

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

        var includeImages = _settingsService?.Current.IncludeImagesInBackup ?? true;
        return RunBackupTransferAsync(
            owner,
            cancellationToken,
            (backup, ct) => backup.ExportAsync(dlg.FileName, includeImages, ct),
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
                if (_settingsService?.Current.AutoBackupBeforeRestore == true)
                {
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var backupDir = Path.Combine(localAppData, "SmartClipVault");
                    Directory.CreateDirectory(backupDir);
                    var autoPath = Path.Combine(backupDir,
                        $"auto_before_restore_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
                    await backup.ExportAsync(autoPath, includeImages: true, ct).ConfigureAwait(true);
                    Trace.TraceInformation($"Auto-backup created before restore: {autoPath}");
                }

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

        if (owner is { IsLoaded: true, IsVisible: true } && ReferenceEquals(MainWindow, owner))
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

    internal void ShowSettingsWindow(Window? owner = null)
    {
        if (Volatile.Read(ref _cleanedUp) != 0 || _settingsService is null)
            return;

        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        var effectiveOwner = owner is { IsLoaded: true, IsVisible: true }
            ? owner
            : (MainWindow is { IsLoaded: true, IsVisible: true } mw ? mw : null);

        var dialog = new Views.SettingsWindow(_settingsService)
        {
            Owner = effectiveOwner,
            WindowStartupLocation = effectiveOwner is not null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen
        };

        _settingsWindow = dialog;
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            if (ReferenceEquals(_settingsWindow, dialog))
                _settingsWindow = null;
        }
    }

    private void OnTraySettingsClick(object sender, RoutedEventArgs e)
    {
        if (IsLocked)
        {
            ShowMainWindow();
            return;
        }
        ShowSettingsWindow();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (Volatile.Read(ref _cleanedUp) != 0)
            return;

        if (Dispatcher.CheckAccess())
        {
            ApplyFontScale(settings.UiFontScale);
            return;
        }

        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _cleanedUp) == 0)
                    ApplyFontScale(settings.UiFontScale);
            });
        }
    }

    private void ApplyFontScale(double scale)
    {
        var effectiveScale = scale is 0.9 or 1.0 or 1.1 or 1.25 ? scale : 1.0;
        Resources["FontSizeBase"] = 12.0 * effectiveScale;
        Resources["FontSize12"] = 12.0 * effectiveScale;
        Resources["FontSize11"] = 11.0 * effectiveScale;
        Resources["FontSize10"] = 10.0 * effectiveScale;
    }

    private void OnShowMenuItemClick(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void OnExitMenuItemClick(object sender, RoutedEventArgs e) => Shutdown();

    private void OnTrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _cleanedUp) != 0)
            return;

        if (_settingsService?.Current.DoubleClickTrayToToggle == false)
            return;

        if (MainWindow is SmartClipVault.MainWindow { IsVisible: true } win && win.WindowState != WindowState.Minimized)
            win.Hide();
        else
            ShowMainWindow();
    }

    private void OnDoubleCopyStar(object? sender, Models.ClipItem item)
    {
        if (Volatile.Read(ref _cleanedUp) != 0)
            return;

        _clipQueue?.Writer.TryWrite(new ClipQueueWork(item, IsStarOnly: true));
    }

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
        if (Volatile.Read(ref _cleanedUp) != 0 || _databaseService is null || IsLocked)
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

    internal void ShowMainWindow()
    {
        if (MainWindow is not SmartClipVault.MainWindow)
        {
            var window = new MainWindow();
            window.Closed += OnMainWindowClosed;
            if (_databaseService is not null)
                window.Initialize(_databaseService);
            window.SetBackupRestoreEnabled(!_isBackupOrRestoreRunning);
            MainWindow = window;
        }

        var wasHidden = !MainWindow.IsVisible;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }
        MainWindow.Activate();
        if (MainWindow is SmartClipVault.MainWindow mw)
        {
            if (IsLocked)
                mw.ApplyLockState(true);
            else if (wasHidden)
                mw.RefreshClips();
        }
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

    private void OnDatabaseClipsDeleted(object? sender, EventArgs e)
        => _clipboardMonitor?.ResetDeduplicationState();

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
        if (_settingsService is not null)
            _settingsService.SettingsChanged -= OnSettingsChanged;

        _quickPasteLoadCts?.Cancel();
        _quickPasteLoadCts?.Dispose();
        _quickPasteLoadCts = null;

        if (_settingsWindow is not null)
        {
            try { _settingsWindow.Close(); } catch (InvalidOperationException) { }
            _settingsWindow = null;
        }

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
            _clipboardMonitor.DoubleCopyStar -= OnDoubleCopyStar;
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

        if (_databaseService is not null)
            _databaseService.ClipsDeleted -= OnDatabaseClipsDeleted;

        _backupService?.Dispose();
        _backupService = null;

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

        if (_settingsMenuItem is not null)
        {
            _settingsMenuItem.Click -= OnTraySettingsClick;
            _settingsMenuItem = null;
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

        if (_updateMenuItem is not null)
        {
            _updateMenuItem.Click -= OnTrayCheckUpdatesClick;
            _updateMenuItem = null;
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

    private static async Task CleanupOldUpdateFilesDelayedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            UpdateService.CleanupOldUpdateFiles();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Deferred old-update cleanup failed: {ex}");
        }
    }

    private async void OnTrayCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await CheckForUpdatesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Unexpected error in update check handler: {ex}");
        }
    }

    internal async Task CheckForUpdatesAsync()
    {
        if (Volatile.Read(ref _cleanedUp) != 0)
            return;

        if (Interlocked.CompareExchange(ref _isUpdateCheckRunning, 1, 0) != 0)
            return;

        if (_updateMenuItem is not null)
            _updateMenuItem.IsEnabled = false;

        try
        {
            var ct = _shutdownCts.Token;
            if (ct.IsCancellationRequested)
                return;

            var asmVer = typeof(App).Assembly.GetName().Version;
            var currentVersion = asmVer is not null
                ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}"
                : "1.0.0";

            UpdateService.ReleaseInfo? info;
            try
            {
                info = await UpdateService.CheckForUpdateAsync(
                    currentVersion,
                    cancellationToken: ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Trace.TraceError($"Update check failed: {ex}");
                if (Volatile.Read(ref _cleanedUp) != 0) return;
                ShowMessageBox(MainWindow,
                    FormatResourceMessage(TryGetResourceString("Dlg_UpdateFailed", "Update failed: {0}"), ex.Message),
                    MessageBoxImage.Error);
                return;
            }

            if (Volatile.Read(ref _cleanedUp) != 0 || ct.IsCancellationRequested) return;

            if (info is null)
            {
                ShowMessageBox(MainWindow, TryGetResourceString("Dlg_UpToDate", "SmartClipVault is up to date."), MessageBoxImage.Information);
                return;
            }

            var title = TryGetResourceString("Dlg_UpdateTitle", "Update Available");
            var msg = FormatResourceMessage(
                TryGetResourceString("Dlg_UpdateAvailable", "Version {0} is available. Do you want to update now?"),
                info.Version);

            var confirm = MainWindow is { IsLoaded: true, IsVisible: true } mw
                ? MessageBox.Show(mw, msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            if (Volatile.Read(ref _cleanedUp) != 0 || ct.IsCancellationRequested)
                return;

            await DownloadAndApplyUpdateAsync(info, ct).ConfigureAwait(true);
        }
        finally
        {
            Volatile.Write(ref _isUpdateCheckRunning, 0);
            if (Volatile.Read(ref _cleanedUp) == 0 && _updateMenuItem is not null)
                _updateMenuItem.IsEnabled = true;
        }
    }

    private async Task DownloadAndApplyUpdateAsync(UpdateService.ReleaseInfo info, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"SmartClipVaultUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var stagedExePath = await UpdateService.DownloadAndStageUpdateAsync(
                info,
                tempDir,
                cancellationToken: ct).ConfigureAwait(true);

            if (Volatile.Read(ref _cleanedUp) != 0 || ct.IsCancellationRequested)
                return;

            UpdateService.ApplyStagedUpdate(stagedExePath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.TraceError($"Update download/apply failed: {ex}");
            if (Volatile.Read(ref _cleanedUp) != 0) return;
            ShowMessageBox(MainWindow,
                FormatResourceMessage(TryGetResourceString("Dlg_UpdateFailed", "Update failed: {0}"), ex.Message),
                MessageBoxImage.Error);
        }
        finally
        {
            UpdateService.TryDeleteDirectory(tempDir);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CleanupResources();
        base.OnExit(e);
    }
}
