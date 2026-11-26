using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using SmartClipVault.Models;

namespace SmartClipVault.Services;

public sealed class SettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRunValueName = "SmartClipVault";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly Lock _gate = new();
    private readonly string _settingsPath;
    private AppSettings _current = new();
    private long _saveVersion;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
                return Clone(_current);
        }
    }

    public event EventHandler<AppSettings>? SettingsChanged;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartClipVault",
            "settings.json");
        Normalize(_current);
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (loaded is not null)
                    {
                        Normalize(loaded);
                        _current = loaded;
                        return Clone(_current);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to load settings: {ex.Message}");
            }

            _current = new AppSettings();
            Normalize(_current);
            return Clone(_current);
        }
    }

    public void Save(AppSettings updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        var normalized = Clone(updated);
        Normalize(normalized);

        AppSettings snapshot;
        long version;
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            var tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }

            _current = normalized;
            snapshot = Clone(_current);
            version = ++_saveVersion;
            ApplyAutoStart(snapshot.AutoStartWithWindows);
        }

        if (Volatile.Read(ref _saveVersion) == version)
            SettingsChanged?.Invoke(this, Clone(snapshot));
    }

    public AppSettings GetSnapshot()
    {
        lock (_gate)
            return Clone(_current);
    }

    public static void ApplyAutoStart(bool enable)
    {
        try
        {
            if (enable)
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key is null)
                    return;

                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                    key.SetValue(AppRunValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(AppRunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Failed to update Windows auto-start registry key: {ex.Message}");
        }
    }

    private static void Normalize(AppSettings s)
    {
        var sourceApps = s.ExcludedApps ?? ["KeePass", "Bitwarden", "1Password"];
        s.ExcludedApps = sourceApps
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (s.AutoClearDays < 0)
            s.AutoClearDays = 0;
        else if (s.AutoClearDays > 36500)
            s.AutoClearDays = 36500;

        s.UiFontScale = NormalizeFontScale(s.UiFontScale);
    }

    private static double NormalizeFontScale(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 1.0;

        foreach (var candidate in new[] { 0.9, 1.0, 1.1, 1.25 })
        {
            if (Math.Abs(value - candidate) < 0.001)
                return candidate;
        }

        return 1.0;
    }

    private static AppSettings Clone(AppSettings s) => new()
    {
        AutoStartWithWindows = s.AutoStartWithWindows,
        StartHiddenToTray = s.StartHiddenToTray,
        DoubleClickTrayToToggle = s.DoubleClickTrayToToggle,
        CaptureHistory = s.CaptureHistory,
        ExcludedApps = s.ExcludedApps is null ? ["KeePass", "Bitwarden", "1Password"] : new List<string>(s.ExcludedApps),
        AutoClearDays = s.AutoClearDays,
        AutoStarOnDoubleCopy = s.AutoStarOnDoubleCopy,
        UiFontScale = s.UiFontScale
    };
}
