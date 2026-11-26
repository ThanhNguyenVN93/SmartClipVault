using System;

namespace SmartClipVault.Services;

public static class PlaceholderService
{
    public static string Expand(string text, string? currentClipboard = null)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
            return text ?? string.Empty;

        var now = DateTime.Now;
        return text
            .Replace("{{date}}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{{time}}", now.ToString("HH:mm"), StringComparison.OrdinalIgnoreCase)
            .Replace("{{datetime}}", now.ToString("yyyy-MM-dd HH:mm"), StringComparison.OrdinalIgnoreCase)
            .Replace("{{clipboard}}", currentClipboard ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
