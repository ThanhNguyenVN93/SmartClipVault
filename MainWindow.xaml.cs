using System;
using System.Collections.Generic;
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

    private record ClipRow(string Preview, string TypeLabel, string PreviewColor, ClipItem Clip);

    private static readonly Regex ClipPlaceholderRegex =
        new(@"\{\{\s*clip\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private DatabaseService? _db;
    private List<ClipItem> _clips = [];
    private CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _pinnedCts;

    public MainWindow()
    {
        InitializeComponent();
    }

    public void Initialize(DatabaseService db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _ = LoadInitialDataAsync();
    }

    public Task RefreshClipsAsync(string? search = null, CancellationToken cancellationToken = default)
        => LoadClipsInternalAsync(search, cancellationToken);

    protected override void OnClosed(EventArgs e)
    {
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        _pinnedCts?.Cancel();
        _pinnedCts?.Dispose();
        _pinnedCts = null;

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
            var token = _lifetimeCts.Token;
            await LoadClipsInternalAsync(SearchBox.Text, token).ConfigureAwait(true);
            await RefreshBoardsAsync(selectBoardId: null, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"MainWindow failed to load initial data: {ex}");
        }
    }

    private async Task LoadClipsInternalAsync(string? search, CancellationToken cancellationToken)
    {
        if (_db is null)
            return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
        var clips = await _db.GetRecentClipsAsync(search, 100, linkedCts.Token).ConfigureAwait(true);
        if (linkedCts.Token.IsCancellationRequested)
            return;

        _clips = clips;
        ClipListBox.ItemsSource = _clips.Select(ToRow).ToList();
    }

    private static ClipRow ToRow(ClipItem clip)
    {
        var preview = PasteHelper.FormatClipPreview(clip);
        var color = clip.IsSensitive ? "#CE9178" : "#D4D4D4";
        var typeLabel = clip.ContentType switch
        {
            ClipContentType.Image => "Image",
            ClipContentType.Files => "Files",
            _ => clip.IsSensitive ? "Sensitive" : "Text"
        };
        return new ClipRow(preview, typeLabel, color, clip);
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

            PinnedClipListBox.ItemsSource = clips.Select(ToRow).ToList();
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
            MessageBox.Show(this, "Select or create a board first.", "SmartClipVault",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (BoardListBox.SelectedItem is BoardItem board)
        {
            PinnedLabel.Text = $"Pinned — {board.Name}";
            await RefreshPinnedClipsAsync(board.Id).ConfigureAwait(true);
        }
        else
        {
            PinnedLabel.Text = "Pinned Clips";
            PinnedClipListBox.ItemsSource = null;
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

        var name = PromptInput("Board name:", "New Board", this);
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
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 6, 0)
        };
        var okBtn = new Button
        {
            Content = "OK",
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
            MessageBox.Show(this, "Template is empty.", "SmartClipVault",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    MessageBox.Show(this, "No non-sensitive text clip is available to substitute for {{clip}}.",
                        "SmartClipVault", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            var result = latestText is null
                ? template
                : ClipPlaceholderRegex.Replace(template, _ => latestText);

            if (string.IsNullOrEmpty(result))
            {
                MessageBox.Show(this, "Template produced empty output.", "SmartClipVault",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (PasteHelper.TrySetClipboardText(result))
            {
                MessageBox.Show(this, "Template applied and copied to clipboard.", "SmartClipVault",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this, "Unable to access the clipboard right now. Please try again.", "SmartClipVault",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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

        if (clip.ContentType == ClipContentType.Image && !string.IsNullOrWhiteSpace(clip.ImagePath))
        {
            var img = await PasteHelper.TryLoadClipboardImageAsync(clip.ImagePath, _lifetimeCts.Token).ConfigureAwait(true);
            PasteHelper.TrySetClipboardImage(img);
        }
        else if (!string.IsNullOrEmpty(clip.TextContent))
        {
            PasteHelper.TrySetClipboardText(clip.TextContent);
        }
    }
}
