using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SmartClipVault.Data;
using SmartClipVault.Models;
using SmartClipVault.Services;

namespace SmartClipVault;

public partial class MainWindow : Window
{
    private const int SearchDebounceMs = 150;
    private const int MaxThumbnailCacheEntries = 150;
    private const int MaxConcurrentThumbnailLoads = 4;

    private enum ClipFilterMode { All, Text, Images }

    private sealed class ClipRow : INotifyPropertyChanged
    {
        private ImageSource? _thumbnail;

        public ClipRow(string preview, string typeLabel, string previewColor, ClipItem clip, ImageSource? thumbnail = null)
        {
            Preview = preview;
            TypeLabel = typeLabel;
            PreviewColor = previewColor;
            Clip = clip;
            _thumbnail = thumbnail;
        }

        public string Preview { get; }
        public string TypeLabel { get; }
        public string PreviewColor { get; }
        public ClipItem Clip { get; }

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value))
                    return;

                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailVisibility)));
            }
        }

        public Visibility ThumbnailVisibility => _thumbnail is not null ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static readonly SolidColorBrush ActiveFilterBrush = CreateFrozenBrush(0x26, 0x4F, 0x78);
    private static readonly SolidColorBrush InactiveFilterBrush = CreateFrozenBrush(0x2D, 0x2D, 0x30);

    private static readonly Regex ClipPlaceholderRegex =
        new(@"\{\{\s*clip\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private DatabaseService? _db;
    private List<ClipItem> _clips = [];
    private readonly Dictionary<string, ImageSource> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private ClipFilterMode _currentFilter = ClipFilterMode.All;
    private CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _pinnedCts;
    private CancellationTokenSource? _thumbnailCts;
    private bool _isClosed;
    private bool _isLocked;

    internal bool IsLocked => _isLocked;

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public MainWindow()
    {
        InitializeComponent();
    }

    public void Initialize(DatabaseService db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        if (Application.Current is App app)
        {
            app.Localization.LanguageChanged -= OnLanguageChanged;
            app.Localization.LanguageChanged += OnLanguageChanged;
        }
        _ = LoadInitialDataAsync();
    }

    public Task RefreshClipsAsync(string? search = null, CancellationToken cancellationToken = default)
        => LoadClipsInternalAsync(search, cancellationToken);

    public Task ReloadDataAsync()
        => LoadInitialDataAsync();

    public void SetBackupRestoreEnabled(bool enabled)
    {
        BackupButton.IsEnabled = enabled;
        RestoreButton.IsEnabled = enabled;
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;

        if (Application.Current is App app)
            app.Localization.LanguageChanged -= OnLanguageChanged;

        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        _pinnedCts?.Cancel();
        _pinnedCts?.Dispose();
        _pinnedCts = null;

        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;

        _thumbnailCache.Clear();
        ClipListBox.ItemsSource = null;
        BoardListBox.ItemsSource = null;
        PinnedClipListBox.ItemsSource = null;
        _clips = [];
        _db = null;

        base.OnClosed(e);
    }

    private async Task LoadInitialDataAsync()
    {
        try
        {
            _thumbnailCache.Clear();
            var token = _lifetimeCts.Token;
            await LoadClipsInternalAsync(SearchBox.Text, token).ConfigureAwait(true);
            await RefreshBoardsAsync(selectBoardId: null, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                PinnedClipListBox.ItemsSource = null;
                UpdatePinnedLabel();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"MainWindow failed to load initial data: {ex}");
        }
    }

    public void RefreshClips()
    {
        if (!_isClosed)
            _ = LoadClipsInternalAsync(SearchBox?.Text, CancellationToken.None);
    }

    private async Task LoadClipsInternalAsync(string? search, CancellationToken cancellationToken)
    {
        if (_isClosed || _db is null)
            return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
        var clips = await _db.GetRecentClipsAsync(search, 100, linkedCts.Token).ConfigureAwait(true);
        if (linkedCts.Token.IsCancellationRequested)
            return;

        _clips = clips;
        PruneThumbnailCache();
        ApplyClipFilter();
    }

    private void OnFilterAllClick(object sender, RoutedEventArgs e) => SetFilter(ClipFilterMode.All);
    private void OnFilterTextClick(object sender, RoutedEventArgs e) => SetFilter(ClipFilterMode.Text);
    private void OnFilterImagesClick(object sender, RoutedEventArgs e) => SetFilter(ClipFilterMode.Images);

    private void SetFilter(ClipFilterMode mode)
    {
        if (_currentFilter == mode)
            return;

        _currentFilter = mode;
        FilterAllButton.Background = mode == ClipFilterMode.All ? ActiveFilterBrush : InactiveFilterBrush;
        FilterTextButton.Background = mode == ClipFilterMode.Text ? ActiveFilterBrush : InactiveFilterBrush;
        FilterImagesButton.Background = mode == ClipFilterMode.Images ? ActiveFilterBrush : InactiveFilterBrush;
        ApplyClipFilter();
    }

    private void ApplyClipFilter()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;

        IEnumerable<ClipItem> filtered = _currentFilter switch
        {
            ClipFilterMode.Text => _clips.Where(c => c.ContentType == ClipContentType.Text),
            ClipFilterMode.Images => _clips.Where(c => c.ContentType == ClipContentType.Image),
            _ => _clips
        };

        var rows = new List<ClipRow>();
        var pendingThumbnails = new List<(ClipRow Row, string Path)>();

        foreach (var clip in filtered)
        {
            ImageSource? cachedThumbnail = null;
            string? normalizedImagePath = null;
            if (clip.ContentType == ClipContentType.Image &&
                PasteHelper.TryNormalizeContainedImagePath(clip.ImagePath, out normalizedImagePath))
            {
                _thumbnailCache.TryGetValue(normalizedImagePath, out cachedThumbnail);
            }

            var row = ToRow(clip, cachedThumbnail);
            rows.Add(row);

            if (cachedThumbnail is null && normalizedImagePath is not null)
                pendingThumbnails.Add((row, normalizedImagePath));
        }

        ClipListBox.ItemsSource = rows;

        if (pendingThumbnails.Count > 0 && !_lifetimeCts.IsCancellationRequested)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _thumbnailCts = cts;
            _ = LoadThumbnailsAsync(pendingThumbnails, cts.Token);
        }
    }

    private async Task LoadThumbnailsAsync(List<(ClipRow Row, string Path)> pending, CancellationToken ct)
    {
        using var throttler = new SemaphoreSlim(MaxConcurrentThumbnailLoads, MaxConcurrentThumbnailLoads);
        var tasks = new List<Task>(pending.Count);

        foreach (var (row, path) in pending)
        {
            if (ct.IsCancellationRequested)
                break;

            tasks.Add(LoadSingleThumbnailAsync(row, path, throttler, ct));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Unexpected error loading thumbnails: {ex}");
        }
    }

    private async Task LoadSingleThumbnailAsync(
        ClipRow row,
        string normalizedPath,
        SemaphoreSlim throttler,
        CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await throttler.WaitAsync(ct).ConfigureAwait(true);
            acquired = true;

            if (ct.IsCancellationRequested)
                return;

            if (_thumbnailCache.TryGetValue(normalizedPath, out var cached))
            {
                row.Thumbnail = cached;
                return;
            }

            var thumbnail = await PasteHelper.TryLoadThumbnailAsync(normalizedPath, cancellationToken: ct).ConfigureAwait(true);
            if (!ct.IsCancellationRequested && thumbnail is not null)
            {
                _thumbnailCache[normalizedPath] = thumbnail;
                row.Thumbnail = thumbnail;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Failed to load thumbnail for '{normalizedPath}': {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (acquired)
                throttler.Release();
        }
    }

    private void PruneThumbnailCache()
    {
        if (_thumbnailCache.Count <= MaxThumbnailCacheEntries)
            return;

        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in _clips)
        {
            if (clip.ContentType == ClipContentType.Image &&
                PasteHelper.TryNormalizeContainedImagePath(clip.ImagePath, out var path))
            {
                activePaths.Add(path);
            }
        }

        foreach (var key in _thumbnailCache.Keys.ToList())
        {
            if (!activePaths.Contains(key))
                _thumbnailCache.Remove(key);
        }
    }

    private static ClipRow ToRow(ClipItem clip, ImageSource? thumbnail = null)
    {
        var preview = PasteHelper.FormatClipPreview(clip);
        var color = clip.IsSensitive ? "#CE9178" : "#D4D4D4";
        var typeLabel = clip.ContentType switch
        {
            ClipContentType.Image => TryGetResource("Type_Image") ?? "Image",
            ClipContentType.Files => TryGetResource("Type_Files") ?? "Files",
            _ => clip.IsSensitive
                ? (TryGetResource("Type_Sensitive") ?? "Sensitive")
                : (TryGetResource("Type_Text") ?? "Text")
        };
        if (clip.IsStarred)
            typeLabel = $"★ {typeLabel}";
        return new ClipRow(preview, typeLabel, color, clip, thumbnail);
    }

    private async Task RefreshBoardsAsync(long? selectBoardId = null, CancellationToken cancellationToken = default)
    {
        if (_db is null)
            return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
        var boards = await _db.GetBoardsAsync(linkedCts.Token).ConfigureAwait(true);
        if (linkedCts.Token.IsCancellationRequested)
            return;

        BoardListBox.ItemsSource = boards;
        if (selectBoardId is long id)
        {
            var match = boards.FirstOrDefault(b => b.Id == id);
            if (match is not null)
                BoardListBox.SelectedItem = match;
        }
    }

    private async Task RefreshPinnedClipsAsync(long boardId)
    {
        if (_db is null)
            return;

        _pinnedCts?.Cancel();
        _pinnedCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _pinnedCts = cts;

        try
        {
            var clips = await _db.GetBoardClipsAsync(boardId, cts.Token).ConfigureAwait(true);
            if (cts.Token.IsCancellationRequested)
                return;

            PinnedClipListBox.ItemsSource = clips.Select(c => ToRow(c)).ToList();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to load pinned clips: {ex}");
        }
    }

    private async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _searchCts = cts;

        var search = SearchBox.Text;
        try
        {
            await Task.Delay(SearchDebounceMs, cts.Token).ConfigureAwait(true);
            await LoadClipsInternalAsync(search, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Search failed: {ex}");
        }
    }

    private void OnClipSelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private async void OnClipDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsClickOnListBoxItem(ClipListBox, e.OriginalSource))
            return;

        if (ClipListBox.SelectedItem is ClipRow row)
            await SafeCopyToClipboardAsync(row.Clip).ConfigureAwait(true);
    }

    private async void OnCopyClipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipRow row)
            await SafeCopyToClipboardAsync(row.Clip).ConfigureAwait(true);
    }

    private async void OnPinClipClick(object sender, RoutedEventArgs e)
    {
        if (_db is null)
            return;
        if (sender is not Button btn || btn.Tag is not ClipRow row)
            return;
        if (BoardListBox.SelectedItem is not BoardItem board)
        {
            MessageBox.Show(this,
                TryGetResource("Dlg_SelectBoardFirst") ?? "Select or create a board first.",
                "SmartClipVault", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _db.PinClipToBoardAsync(row.Clip.Id, board.Id, _lifetimeCts.Token).ConfigureAwait(true);
            await RefreshPinnedClipsAsync(board.Id).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to pin clip: {ex}");
        }
    }

    private async void OnBoardSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePinnedLabel();
        if (BoardListBox.SelectedItem is BoardItem board)
        {
            await RefreshPinnedClipsAsync(board.Id).ConfigureAwait(true);
        }
        else
        {
            _pinnedCts?.Cancel();
            _pinnedCts?.Dispose();
            _pinnedCts = null;
            PinnedClipListBox.ItemsSource = null;
        }
    }

    private void OnLanguageChanged(object? sender, string langCode)
    {
        UpdatePinnedLabel();
        ApplyClipFilter();
    }

    private void UpdatePinnedLabel()
    {
        if (BoardListBox.SelectedItem is BoardItem board)
        {
            var pinnedBase = TryGetResource("Main_PinnedClips") ?? "Pinned Clips";
            PinnedLabel.Text = $"{pinnedBase} — {board.Name}";
        }
        else
        {
            PinnedLabel.SetResourceReference(TextBlock.TextProperty, "Main_PinnedClips");
        }
    }

    private async void OnPinnedClipDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsClickOnListBoxItem(PinnedClipListBox, e.OriginalSource))
            return;

        if (PinnedClipListBox.SelectedItem is ClipRow row)
            await SafeCopyToClipboardAsync(row.Clip).ConfigureAwait(true);
    }

    private async void OnCreateBoardClick(object sender, RoutedEventArgs e)
    {
        if (_db is null)
            return;

        var name = PromptInput(
            TryGetResource("Dlg_BoardNameLabel") ?? "Board name:",
            TryGetResource("Dlg_NewBoardTitle") ?? "New Board",
            this);
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            var id = await _db.CreateBoardAsync(name.Trim(), _lifetimeCts.Token).ConfigureAwait(true);
            await RefreshBoardsAsync(id, _lifetimeCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to create board: {ex}");
        }
    }

    private static bool IsClickOnListBoxItem(ListBox listBox, object? originalSource)
        => originalSource is DependencyObject dep &&
           ItemsControl.ContainerFromElement(listBox, dep) is ListBoxItem;

    private static string? PromptInput(string label, string title, Window owner)
    {
        var dlg = new Window
        {
            Title = title,
            Owner = owner,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26))
        };

        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var tb = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = Brushes.LightGray,
            CaretBrush = Brushes.LightGray,
            Padding = new Thickness(4)
        };
        sp.Children.Add(tb);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var cancelBtn = new Button
        {
            Content = TryGetResource("Dlg_Cancel") ?? "Cancel",
            IsCancel = true,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 6, 0)
        };
        var okBtn = new Button
        {
            Content = TryGetResource("Dlg_OK") ?? "OK",
            IsDefault = true,
            Padding = new Thickness(16, 4, 16, 4)
        };

        void OnOkClick(object s, RoutedEventArgs args) => dlg.DialogResult = true;
        void OnLoaded(object s, RoutedEventArgs args) => tb.Focus();

        okBtn.Click += OnOkClick;
        dlg.Loaded += OnLoaded;

        buttonPanel.Children.Add(cancelBtn);
        buttonPanel.Children.Add(okBtn);
        sp.Children.Add(buttonPanel);
        dlg.Content = sp;

        try
        {
            return dlg.ShowDialog() == true ? tb.Text : null;
        }
        finally
        {
            okBtn.Click -= OnOkClick;
            dlg.Loaded -= OnLoaded;
        }
    }

    private async void OnRunTemplateClick(object sender, RoutedEventArgs e)
    {
        var template = TemplateBox.Text;
        if (string.IsNullOrWhiteSpace(template))
        {
            MessageBox.Show(this,
                TryGetResource("Dlg_TemplateEmpty") ?? "Template is empty.",
                "SmartClipVault", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            string? latestText = null;
            if (ClipPlaceholderRegex.IsMatch(template))
            {
                var recent = _db is not null
                    ? await _db.GetRecentClipsAsync(null, 50, _lifetimeCts.Token).ConfigureAwait(true)
                    : _clips;

                latestText = recent
                    .FirstOrDefault(c => c.ContentType == ClipContentType.Text &&
                                         !c.IsSensitive &&
                                         !string.IsNullOrEmpty(c.TextContent))
                    ?.TextContent;

                if (latestText is null)
                {
                    MessageBox.Show(this,
                        TryGetResource("Dlg_TemplateNoClip") ?? "No non-sensitive text clip is available to substitute for {{clip}}.",
                        "SmartClipVault", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            var result = latestText is null
                ? template
                : ClipPlaceholderRegex.Replace(template, _ => latestText);

            if (string.IsNullOrEmpty(result))
            {
                MessageBox.Show(this,
                    TryGetResource("Dlg_TemplateEmptyOutput") ?? "Template produced empty output.",
                    "SmartClipVault", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (PasteHelper.TrySetClipboardText(result))
            {
                MessageBox.Show(this,
                    TryGetResource("Dlg_TemplateCopied") ?? "Template applied and copied to clipboard.",
                    "SmartClipVault", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this,
                    TryGetResource("Dlg_ClipboardUnavailable") ?? "Unable to access the clipboard right now. Please try again.",
                    "SmartClipVault", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to run template: {ex}");
        }
    }

    private static string? TryGetResource(string key)
        => Application.Current?.TryFindResource(key) as string;

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            app.ShowSettingsWindow(this);
    }

    internal void StarClipById(long clipId)
    {
        if (_isClosed || clipId <= 0 || _lifetimeCts.IsCancellationRequested)
            return;

        _ = SafeRefreshClipsAsync();
    }

    private async Task SafeRefreshClipsAsync()
    {
        try
        {
            await RefreshClipsAsync(SearchBox.Text, _lifetimeCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to refresh clips after star: {ex}");
        }
    }

    private async void OnBackupClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            await app.RunBackupDialogAsync(this, _lifetimeCts.Token).ConfigureAwait(true);
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            await app.RunRestoreDialogAsync(this, _lifetimeCts.Token).ConfigureAwait(true);
    }

    private void OnLangEnClick(object sender, RoutedEventArgs e)
        => (Application.Current as App)?.SetLanguage("en");

    private void OnLangViClick(object sender, RoutedEventArgs e)
        => (Application.Current as App)?.SetLanguage("vi");

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.L && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            HandleCtrlL();
            e.Handled = true;
        }
    }

    private void HandleCtrlL()
    {
        if (_isLocked)
            return;

        var settings = (Application.Current as App)?.Settings?.Current;
        if (settings?.PinHash is null || settings.PinSalt is null)
        {
            MessageBox.Show(this,
                TryGetResource("Lock_NoPinWarning") ?? "No PIN configured. Set a PIN in Settings to enable locking.",
                "SmartClipVault", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ApplyLockState(true);
    }

    private int _failedPinAttempts;
    private DateTime _pinCooldownUntilUtc = DateTime.MinValue;

    public void ApplyLockState(bool locked)
    {
        _isLocked = locked;
        (Application.Current as App)?.SetLocked(locked);
        if (locked)
        {
            LockPinBox.Clear();
            LockErrorText.Visibility = Visibility.Collapsed;
            LockOverlay.Visibility = Visibility.Visible;
            LockPinBox.Focus();
        }
        else
        {
            _failedPinAttempts = 0;
            _pinCooldownUntilUtc = DateTime.MinValue;
            LockOverlay.Visibility = Visibility.Collapsed;
            LockPinBox.Clear();
            LockErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnLockPinKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            TryUnlock();
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e) => TryUnlock();

    private void TryUnlock()
    {
        if (!_isLocked)
            return;

        var now = DateTime.UtcNow;
        if (now < _pinCooldownUntilUtc)
        {
            var secs = Math.Max(1, (int)Math.Ceiling((_pinCooldownUntilUtc - now).TotalSeconds));
            LockErrorText.Text = $"Too many failed attempts. Try again in {secs}s.";
            LockErrorText.Visibility = Visibility.Visible;
            LockPinBox.Clear();
            LockPinBox.Focus();
            return;
        }

        var settings = (Application.Current as App)?.Settings?.Current;
        var pin = LockPinBox.Password;

        if (settings?.PinHash is not null && settings.PinSalt is not null &&
            SettingsService.VerifyPin(pin, settings.PinHash, settings.PinSalt))
        {
            ApplyLockState(false);
            RefreshClips();
        }
        else
        {
            _failedPinAttempts++;
            if (_failedPinAttempts >= 5)
            {
                _pinCooldownUntilUtc = DateTime.UtcNow.AddSeconds(30);
                _failedPinAttempts = 0;
            }
            LockErrorText.Text = TryGetResource("Lock_IncorrectPin") ?? "Incorrect PIN. Please try again.";
            LockErrorText.Visibility = Visibility.Visible;
            LockPinBox.Clear();
            LockPinBox.Focus();
        }
    }

    private void OnClipContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var isText = ClipListBox.SelectedItem is ClipRow r
            && r.Clip.ContentType == ClipContentType.Text
            && !r.Clip.IsSensitive
            && !string.IsNullOrEmpty(r.Clip.TextContent);

        if (sender is ContextMenu cm && cm.Items.Count > 0 && cm.Items[0] is MenuItem specialOps)
            specialOps.IsEnabled = isText;
    }

    private void OnTransformClick(object sender, RoutedEventArgs e)
    {
        if (ClipListBox.SelectedItem is not ClipRow row)
            return;
        if (row.Clip.ContentType != ClipContentType.Text
            || row.Clip.IsSensitive
            || string.IsNullOrEmpty(row.Clip.TextContent))
            return;

        var text = row.Clip.TextContent;
        var operation = (sender as MenuItem)?.Tag as string;

        var result = operation switch
        {
            "UpperCase"   => TextTransformService.ToUpperCase(text),
            "LowerCase"   => TextTransformService.ToLowerCase(text),
            "CamelCase"   => TextTransformService.ToCamelCase(text),
            "SnakeCase"   => TextTransformService.ToSnakeCase(text),
            "JsonPretty"  => TextTransformService.JsonPretty(text),
            "JsonMinify"  => TextTransformService.JsonMinify(text),
            "Base64Encode" => TextTransformService.Base64Encode(text),
            "Base64Decode" => TextTransformService.Base64Decode(text),
            "UrlEncode"   => TextTransformService.UrlEncode(text),
            "UrlDecode"   => TextTransformService.UrlDecode(text),
            "TrimDedupe"  => TextTransformService.TrimAndRemoveDuplicateLines(text),
            _             => null
        };

        if (result is not null)
            PasteHelper.TrySetClipboardText(result);
    }

    private async Task SafeCopyToClipboardAsync(ClipItem clip)
    {
        try
        {
            await CopyToClipboardAsync(clip).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to copy clip to clipboard: {ex}");
        }
    }

    private async Task CopyToClipboardAsync(ClipItem clip)
    {
        if (clip.IsSensitive)
            return;

        if (clip.ContentType == ClipContentType.Image &&
            PasteHelper.TryNormalizeContainedImagePath(clip.ImagePath, out var normalizedPath))
        {
            var img = await PasteHelper.TryLoadClipboardImageAsync(normalizedPath, _lifetimeCts.Token).ConfigureAwait(true);
            if (img is not null && !_lifetimeCts.IsCancellationRequested)
            {
                var monitor = (Application.Current as App)?.ClipboardMonitor;
                using var suppression = monitor?.SuppressSelfCopy();
                if (PasteHelper.TrySetClipboardImage(img))
                    suppression?.MarkSucceeded();
            }
        }
        else if (!string.IsNullOrEmpty(clip.TextContent))
        {
            PasteHelper.TrySetClipboardText(clip.TextContent);
        }
    }
}
