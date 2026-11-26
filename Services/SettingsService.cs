using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SmartClipVault.Models;

namespace SmartClipVault.Services;

public sealed class SettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRunValueName = "SmartClipVault";
    private const string DpapiPrefix = "dpapi:";
    private const string LockedPinFallbackSentinel = "__PIN_LOCKED_UNREADABLE__";
    private const int MinMaxHistoryItems = 10;

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
            if (!File.Exists(_settingsPath))
            {
                _current = new AppSettings();
                Normalize(_current);
                return Clone(_current);
            }

            string? rawJson = null;
            try
            {
                rawJson = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(rawJson, JsonOptions);
                if (loaded is null)
                {
                    ApplyFailClosedFallbackLocked(rawJson);
                    return Clone(_current);
                }

                var needsMigration = false;
                var dpapiFailed = false;

                if (!string.IsNullOrEmpty(loaded.PinHash))
                {
                    if (TryUnprotectPinSecret(loaded.PinHash, out var plainHash, out var wasProtected))
                    {
                        loaded.PinHash = plainHash;
                        if (!wasProtected)
                            needsMigration = true;
                    }
                    else
                    {
                        dpapiFailed = true;
                    }
                }

                if (!string.IsNullOrEmpty(loaded.PinSalt))
                {
                    if (TryUnprotectPinSecret(loaded.PinSalt, out var plainSalt, out var wasProtected))
                    {
                        loaded.PinSalt = plainSalt;
                        if (!wasProtected)
                            needsMigration = true;
                    }
                    else
                    {
                        dpapiFailed = true;
                    }
                }

                Normalize(loaded);
                _current = loaded;

                if (needsMigration && !dpapiFailed)
                {
                    try
                    {
                        WriteSettingsAtomicallyLocked(loaded);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning($"Failed to migrate PIN settings to DPAPI: {ex.Message}");
                    }
                }

                return Clone(_current);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to load settings: {ex.Message}");
                ApplyFailClosedFallbackLocked(rawJson);
                return Clone(_current);
            }
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
            WriteSettingsAtomicallyLocked(normalized);

            _current = normalized;
            snapshot = Clone(_current);
            version = ++_saveVersion;
            ApplyAutoStart(snapshot.AutoStartWithWindows);
        }

        if (Volatile.Read(ref _saveVersion) == version)
            SettingsChanged?.Invoke(this, Clone(snapshot));
    }

    private void WriteSettingsAtomicallyLocked(AppSettings source)
    {
        var dir = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var diskCopy = Clone(source);
        diskCopy.PinHash = ProtectPinSecret(diskCopy.PinHash);
        diskCopy.PinSalt = ProtectPinSecret(diskCopy.PinSalt);

        var json = JsonSerializer.Serialize(diskCopy, JsonOptions);
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
    }

    private void ApplyFailClosedFallbackLocked(string? rawJson)
    {
        if (_current.PinHash is not null || _current.PinSalt is not null)
        {
            Normalize(_current);
            return;
        }

        if (!string.IsNullOrWhiteSpace(rawJson) && TryRecoverPinFieldsFromJson(rawJson, out var recoveredHash, out var recoveredSalt))
        {
            var recovered = new AppSettings
            {
                PinHash = recoveredHash,
                PinSalt = recoveredSalt
            };
            Normalize(recovered);
            _current = recovered;
            return;
        }

        _current = new AppSettings
        {
            PinHash = LockedPinFallbackSentinel,
            PinSalt = LockedPinFallbackSentinel
        };
        Normalize(_current);
    }

    private static bool TryRecoverPinFieldsFromJson(string rawJson, out string? pinHash, out string? pinSalt)
    {
        pinHash = null;
        pinSalt = null;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (doc.RootElement.TryGetProperty(nameof(AppSettings.PinHash), out var hashProp))
            {
                if (hashProp.ValueKind == JsonValueKind.String)
                {
                    var rawHash = hashProp.GetString();
                    pinHash = TryUnprotectPinSecret(rawHash, out var plainHash, out _)
                        ? plainHash
                        : rawHash;
                }
                else if (hashProp.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }

            if (doc.RootElement.TryGetProperty(nameof(AppSettings.PinSalt), out var saltProp))
            {
                if (saltProp.ValueKind == JsonValueKind.String)
                {
                    var rawSalt = saltProp.GetString();
                    pinSalt = TryUnprotectPinSecret(rawSalt, out var plainSalt, out _)
                        ? plainSalt
                        : rawSalt;
                }
                else if (saltProp.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ProtectPinSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        if (value.StartsWith(DpapiPrefix, StringComparison.Ordinal) ||
            string.Equals(value, LockedPinFallbackSentinel, StringComparison.Ordinal))
        {
            return value;
        }

        var plainBytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(encryptedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private static bool TryUnprotectPinSecret(string? value, out string? plainValue, out bool wasProtected)
    {
        if (string.IsNullOrEmpty(value))
        {
            plainValue = null;
            wasProtected = false;
            return true;
        }

        if (string.Equals(value, LockedPinFallbackSentinel, StringComparison.Ordinal))
        {
            plainValue = value;
            wasProtected = true;
            return false;
        }

        if (!value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            plainValue = value;
            wasProtected = false;
            return true;
        }

        wasProtected = true;
        try
        {
            var cipherBytes = Convert.FromBase64String(value[DpapiPrefix.Length..]);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            try
            {
                plainValue = Encoding.UTF8.GetString(plainBytes);
                return !string.IsNullOrEmpty(plainValue);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            plainValue = value;
            return false;
        }
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
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) &&
                string.Equals(Path.GetFileName(exePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (enable)
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key is null)
                    return;

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

        if (s.MaxHistoryItems < MinMaxHistoryItems)
            s.MaxHistoryItems = MinMaxHistoryItems;

        if (s.AutoLockMinutes < 0)
            s.AutoLockMinutes = 0;

        s.Language = string.IsNullOrWhiteSpace(s.Language) ? "en" : s.Language.Trim();
        s.Theme = string.IsNullOrWhiteSpace(s.Theme) ? "Dark" : s.Theme.Trim();
        s.QuickPasteHotkey = string.IsNullOrWhiteSpace(s.QuickPasteHotkey) ? "Ctrl+Shift+V" : s.QuickPasteHotkey.Trim();

        if (string.IsNullOrWhiteSpace(s.PinHash))
            s.PinHash = null;
        if (string.IsNullOrWhiteSpace(s.PinSalt))
            s.PinSalt = null;

        // If only one of PinHash/PinSalt is present, fail-closed so PIN lock is never silently disabled.
        if (s.PinHash is not null && s.PinSalt is null)
            s.PinSalt = LockedPinFallbackSentinel;
        else if (s.PinSalt is not null && s.PinHash is null)
            s.PinHash = LockedPinFallbackSentinel;

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
        UiFontScale = s.UiFontScale,
        IncludeImagesInBackup = s.IncludeImagesInBackup,
        AutoBackupBeforeRestore = s.AutoBackupBeforeRestore,
        PlayToastSound = s.PlayToastSound,
        Language = s.Language,
        MaxHistoryItems = s.MaxHistoryItems,
        EnableOcr = s.EnableOcr,
        AutoLockMinutes = s.AutoLockMinutes,
        Theme = s.Theme,
        DataDirectory = s.DataDirectory,
        PinHash = s.PinHash,
        PinSalt = s.PinSalt,
        QuickPasteHotkey = s.QuickPasteHotkey,
    };

    private const int PinPbkdf2Iterations = 600_000;
    private const int PinSaltBytes = 16;
    private const int PinHashBytes = 32;

    public static bool IsValidPinFormat(string? pin)
        => !string.IsNullOrEmpty(pin) && pin.Length is >= 4 and <= 8 && pin.All(char.IsAsciiDigit);

    public static (string Hash, string Salt) CreatePinHash(string pin)
    {
        if (!IsValidPinFormat(pin))
            throw new ArgumentException("PIN must be 4 to 8 ASCII digits.", nameof(pin));

        var salt = RandomNumberGenerator.GetBytes(PinSaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, PinPbkdf2Iterations, HashAlgorithmName.SHA256, PinHashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool VerifyPin(string? pin, string? storedHash, string? storedSalt)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(storedSalt))
            return false;

        if (!TryUnprotectPinSecret(storedHash, out var plainHash, out _) ||
            !TryUnprotectPinSecret(storedSalt, out var plainSalt, out _) ||
            string.IsNullOrEmpty(plainHash) ||
            string.IsNullOrEmpty(plainSalt))
        {
            return false;
        }

        byte[]? salt = null;
        byte[]? actual = null;
        byte[]? expected = null;
        try
        {
            salt = Convert.FromBase64String(plainSalt);
            actual = Convert.FromBase64String(plainHash);
            if (salt.Length != PinSaltBytes || actual.Length != PinHashBytes)
                return false;

            expected = Rfc2898DeriveBytes.Pbkdf2(pin, salt, PinPbkdf2Iterations, HashAlgorithmName.SHA256, PinHashBytes);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
            if (actual is not null) CryptographicOperations.ZeroMemory(actual);
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
        }
    }
}
