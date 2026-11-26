using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartClipVault.Services;

public static partial class TextTransformService
{
    [GeneratedRegex(@"[^a-zA-Z0-9]+|(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordSplitterRegex();

    public static string ToUpperCase(string text) => text.ToUpperInvariant();

    public static string ToLowerCase(string text) => text.ToLowerInvariant();

    public static string ToCamelCase(string text)
    {
        var words = SplitWords(text);
        if (words.Count == 0)
            return text;

        var sb = new StringBuilder();
        for (int i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (string.IsNullOrEmpty(word))
                continue;
            if (i == 0)
                sb.Append(word.ToLowerInvariant());
            else
                sb.Append(char.ToUpperInvariant(word[0])).Append(word[1..].ToLowerInvariant());
        }
        return sb.ToString();
    }

    public static string ToSnakeCase(string text)
    {
        var words = SplitWords(text);
        return string.Join("_", words.Select(w => w.ToLowerInvariant()));
    }

    public static string JsonPretty(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public static string JsonMinify(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public static string Base64Encode(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    public static string Base64Decode(string text)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim()));
        }
        catch (FormatException)
        {
            return text;
        }
    }

    public static string UrlEncode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        try
        {
            return Uri.EscapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    public static string UrlDecode(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    public static string TrimAndRemoveDuplicateLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var result = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
            result.RemoveAt(result.Count - 1);

        return string.Join(Environment.NewLine, result);
    }

    private static List<string> SplitWords(string text)
        => WordSplitterRegex().Split(text).Where(p => !string.IsNullOrEmpty(p)).ToList();
}
