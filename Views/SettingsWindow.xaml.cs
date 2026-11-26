using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SmartClipVault.Models;
using SmartClipVault.Services;

namespace SmartClipVault.Views;

public partial class SettingsWindow : Window
{
    private const int MinMaxHistoryItems = 10;
    // SettingsService.Normalize clamps values < 10 to 10; represent UI "Unlimited" (Tag="0") as int.MaxValue.
    private const int UnlimitedMaxHistoryItems = int.MaxValue;

    private readonly SettingsService _settingsService;

    public SettingsWindow(SettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;
        InitializeComponent();
        LoadFromSettings(_settingsService.GetSnapshot());
    }

    private void LoadFromSettings(AppSettings s)
    {
        AutoStartCheck.IsChecked = s.AutoStartWithWindows;
        StartHiddenCheck.IsChecked = s.StartHiddenToTray;
        DoubleClickTrayCheck.IsChecked = s.DoubleClickTrayToToggle;
        CaptureHistoryCheck.IsChecked = s.CaptureHistory;
        AutoStarCheck.IsChecked = s.AutoStarOnDoubleCopy;
        ExcludedAppsBox.Text = string.Join(Environment.NewLine, s.ExcludedApps);
        AutoClearDaysBox.Text = s.AutoClearDays.ToString(CultureInfo.InvariantCulture);
        IncludeImagesCheck.IsChecked = s.IncludeImagesInBackup;
        AutoBackupBeforeRestoreCheck.IsChecked = s.AutoBackupBeforeRestore;
        PlayToastSoundCheck.IsChecked = s.PlayToastSound;
        EnableOcrCheck.IsChecked = s.EnableOcr;
        SelectComboByTag(LanguageCombo, s.Language, 0);
        SelectComboByTag(ThemeCombo, s.Theme, 0);
        DataDirectoryBox.Text = s.DataDirectory ?? string.Empty;
        var maxHistoryTag = s.MaxHistoryItems >= UnlimitedMaxHistoryItems
            ? "0"
            : s.MaxHistoryItems.ToString(CultureInfo.InvariantCulture);
        SelectComboByTag(MaxHistoryCombo, maxHistoryTag, 1);
        SelectComboByTag(AutoLockCombo, s.AutoLockMinutes.ToString(CultureInfo.InvariantCulture), 0);
        UpdatePinStatus(s.PinHash != null);

        foreach (ComboBoxItem item in FontScaleCombo.Items)
        {
            if (item.Tag is string tag &&
                double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) &&
                Math.Abs(val - s.UiFontScale) < 0.01)
            {
                FontScaleCombo.SelectedItem = item;
                break;
            }
        }

        if (FontScaleCombo.SelectedItem is null && FontScaleCombo.Items.Count > 1)
            FontScaleCombo.SelectedIndex = 1;
    }

    private void UpdatePinStatus(bool pinIsSet)
    {
        PinStatusText.Text = pinIsSet
            ? Application.Current?.TryFindResource("Settings_PinStatus_Set") as string ?? "PIN is set"
            : Application.Current?.TryFindResource("Settings_PinStatus_NotSet") as string ?? "No PIN configured";
        RemovePinButton.IsEnabled = pinIsSet;
    }

    private static bool IsValidPin(string pin, out string error)
    {
        if (pin.Length < 4 || pin.Length > 8)
        {
            error = Application.Current?.TryFindResource("Settings_PinInvalidLength") as string
                ?? "PIN must be 4–8 digits.";
            return false;
        }
        foreach (var c in pin)
        {
            if (!char.IsAsciiDigit(c))
            {
                error = Application.Current?.TryFindResource("Settings_PinDigitsOnly") as string
                    ?? "PIN must contain only digits (0–9).";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private void OnSetPinClick(object sender, RoutedEventArgs e)
    {
        var newPin = NewPinBox.Password;
        var confirmPin = ConfirmPinBox.Password;

        if (!IsValidPin(newPin, out var pinError))
        {
            MessageBox.Show(this, pinError, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            NewPinBox.Focus();
            return;
        }

        if (newPin != confirmPin)
        {
            var msg = Application.Current?.TryFindResource("Settings_PinMismatch") as string
                ?? "PINs do not match.";
            MessageBox.Show(this, msg, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            ConfirmPinBox.Focus();
            return;
        }

        var updated = _settingsService.GetSnapshot();
        var (hash, salt) = SettingsService.CreatePinHash(newPin);
        updated.PinHash = hash;
        updated.PinSalt = salt;

        try
        {
            _settingsService.Save(updated);
        }
        catch (Exception ex)
        {
            var fmt = Application.Current?.TryFindResource("Settings_SaveFailed") as string
                ?? "Failed to save settings: {0}";
            MessageBox.Show(this, string.Format(CultureInfo.CurrentCulture, fmt, ex.Message),
                Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        NewPinBox.Clear();
        ConfirmPinBox.Clear();
        UpdatePinStatus(pinIsSet: true);

        var successMsg = Application.Current?.TryFindResource("Settings_PinSaved") as string
            ?? "PIN saved successfully.";
        MessageBox.Show(this, successMsg, Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnRemovePinClick(object sender, RoutedEventArgs e)
    {
        var updated = _settingsService.GetSnapshot();
        updated.PinHash = null;
        updated.PinSalt = null;

        try
        {
            _settingsService.Save(updated);
        }
        catch (Exception ex)
        {
            var fmt = Application.Current?.TryFindResource("Settings_SaveFailed") as string
                ?? "Failed to save settings: {0}";
            MessageBox.Show(this, string.Format(CultureInfo.CurrentCulture, fmt, ex.Message),
                Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        UpdatePinStatus(pinIsSet: false);

        var msg = Application.Current?.TryFindResource("Settings_PinRemoved") as string ?? "PIN removed.";
        MessageBox.Show(this, msg, Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var daysText = AutoClearDaysBox.Text.Trim();
        if (!int.TryParse(daysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ||
            days < 0 ||
            days > 36500)
        {
            var msg = Application.Current?.TryFindResource("Settings_InvalidAutoClearDays") as string
                ?? "Please enter a whole number of days between 0 and 36500.";
            MessageBox.Show(this, msg, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            AutoClearDaysBox.Focus();
            AutoClearDaysBox.SelectAll();
            return;
        }

        var scale = 1.0;
        if (FontScaleCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedScale))
        {
            scale = parsedScale;
        }

        var excluded = ExcludedAppsBox.Text
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = _settingsService.GetSnapshot();

        var language = current.Language;
        if (LanguageCombo.SelectedItem is ComboBoxItem langItem && langItem.Tag is string lang && !string.IsNullOrWhiteSpace(lang))
            language = lang;

        var theme = current.Theme;
        if (ThemeCombo.SelectedItem is ComboBoxItem themeItem && themeItem.Tag is string selectedTheme && !string.IsNullOrWhiteSpace(selectedTheme))
            theme = selectedTheme;

        var maxHistoryItems = current.MaxHistoryItems;
        if (MaxHistoryCombo.SelectedItem is ComboBoxItem maxItem && maxItem.Tag is string maxTag &&
            int.TryParse(maxTag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxHist))
        {
            maxHistoryItems = maxHist <= 0
                ? UnlimitedMaxHistoryItems
                : Math.Max(MinMaxHistoryItems, maxHist);
        }

        var autoLockMinutes = current.AutoLockMinutes;
        if (AutoLockCombo.SelectedItem is ComboBoxItem lockItem && lockItem.Tag is string lockTag &&
            int.TryParse(lockTag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var autoLock))
        {
            autoLockMinutes = Math.Max(0, autoLock);
        }

        var updated = new AppSettings
        {
            AutoStartWithWindows = AutoStartCheck.IsChecked == true,
            StartHiddenToTray = StartHiddenCheck.IsChecked == true,
            DoubleClickTrayToToggle = DoubleClickTrayCheck.IsChecked == true,
            CaptureHistory = CaptureHistoryCheck.IsChecked == true,
            AutoStarOnDoubleCopy = AutoStarCheck.IsChecked == true,
            ExcludedApps = excluded,
            AutoClearDays = days,
            UiFontScale = scale,
            IncludeImagesInBackup = IncludeImagesCheck.IsChecked == true,
            AutoBackupBeforeRestore = AutoBackupBeforeRestoreCheck.IsChecked == true,
            PlayToastSound = PlayToastSoundCheck.IsChecked == true,
            EnableOcr = EnableOcrCheck.IsChecked == true,
            Language = language,
            Theme = theme,
            DataDirectory = string.IsNullOrWhiteSpace(DataDirectoryBox.Text) ? null : DataDirectoryBox.Text.Trim(),
            MaxHistoryItems = maxHistoryItems,
            AutoLockMinutes = autoLockMinutes,
            PinHash = current.PinHash,
            PinSalt = current.PinSalt,
            QuickPasteHotkey = current.QuickPasteHotkey,
        };

        try
        {
            _settingsService.Save(updated);
        }
        catch (Exception ex)
        {
            var fmt = Application.Current?.TryFindResource("Settings_SaveFailed") as string
                ?? "Failed to save settings: {0}";
            string message;
            try
            {
                message = string.Format(CultureInfo.CurrentCulture, fmt, ex.Message);
            }
            catch (FormatException)
            {
                message = $"{fmt} {ex.Message}";
            }
            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        (Application.Current as App)?.SetLanguage(updated.Language);
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static void SelectComboByTag(ComboBox combo, string tagValue, int fallbackIndex)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag as string, tagValue, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > fallbackIndex)
            combo.SelectedIndex = fallbackIndex;
    }

    private void OnBrowseDataDirClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Data & Sync Folder"
        };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            DataDirectoryBox.Text = dlg.FolderName;
        }
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Delete all unpinned, unstarred clipboard history? This cannot be undone.",
            "SmartClipVault", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        if (Application.Current is App app && app.MainWindow is MainWindow mw)
        {
            mw.ClearUnpinnedHistoryFromSettings();
        }
        ToastWindow.ShowToast("History cleared!");
    }

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        var current = _settingsService.GetSnapshot();
        defaults.PinHash = current.PinHash;
        defaults.PinSalt = current.PinSalt;
        LoadFromSettings(defaults);
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            await app.CheckForUpdatesAsync().ConfigureAwait(true);
    }
}
