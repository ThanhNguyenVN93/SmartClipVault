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

        var updated = new AppSettings
        {
            AutoStartWithWindows = AutoStartCheck.IsChecked == true,
            StartHiddenToTray = StartHiddenCheck.IsChecked == true,
            DoubleClickTrayToToggle = DoubleClickTrayCheck.IsChecked == true,
            CaptureHistory = CaptureHistoryCheck.IsChecked == true,
            AutoStarOnDoubleCopy = AutoStarCheck.IsChecked == true,
            ExcludedApps = excluded,
            AutoClearDays = days,
            UiFontScale = scale
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

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
