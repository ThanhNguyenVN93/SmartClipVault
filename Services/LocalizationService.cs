using System;
using System.Collections.ObjectModel;
using System.Windows;

namespace SmartClipVault.Services;

public class LocalizationService
{
    private string _currentLang = "en";

    public string CurrentLanguage => _currentLang;

    public event EventHandler<string>? LanguageChanged;

    public void SetLanguage(string langCode)
    {
        if (string.IsNullOrWhiteSpace(langCode))
            throw new ArgumentException("Language code must not be empty.", nameof(langCode));

        var normalized = langCode.Trim().ToLowerInvariant() switch
        {
            "vi" or "vi-vn" => "vi",
            _ => "en"
        };

        var app = Application.Current
            ?? throw new InvalidOperationException("Application.Current is not available.");

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => SetLanguage(langCode));
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        if (normalized == _currentLang && ContainsLanguageDictionary(merged, normalized))
            return;

        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/Strings.{normalized}.xaml", UriKind.Absolute)
        };

        // Replace existing Strings.*.xaml dictionary in-place to avoid transient missing keys
        var replacedIndex = -1;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            if (IsStringsDictionary(merged[i]))
            {
                if (replacedIndex < 0)
                {
                    merged[i] = dict;
                    replacedIndex = i;
                }
                else
                {
                    merged.RemoveAt(i);
                }
            }
        }

        if (replacedIndex < 0)
        {
            merged.Add(dict);
        }

        _currentLang = normalized;
        LanguageChanged?.Invoke(this, normalized);
    }

    private static bool ContainsLanguageDictionary(Collection<ResourceDictionary> merged, string langCode)
    {
        var expectedSuffix = $"/Strings.{langCode}.xaml";
        foreach (var dict in merged)
        {
            var src = (dict.Source?.OriginalString ?? string.Empty).Replace('\\', '/');
            if (src.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsStringsDictionary(ResourceDictionary dict)
    {
        var src = (dict.Source?.OriginalString ?? string.Empty).Replace('\\', '/');
        return src.Contains("Resources/Strings.", StringComparison.OrdinalIgnoreCase) &&
               src.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
    }
}
