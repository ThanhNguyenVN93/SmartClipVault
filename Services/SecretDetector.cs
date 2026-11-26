using System.Text.RegularExpressions;

namespace SmartClipVault.Services;

public static partial class SecretDetector
{
    private const RegexOptions SafeOptions = RegexOptions.NonBacktracking;
    private const RegexOptions SafeIgnoreCaseOptions = RegexOptions.IgnoreCase | RegexOptions.NonBacktracking;

    [GeneratedRegex(@"\bsk-(?:proj-|svcacct-|admin-)?[A-Za-z0-9\-_]{20,}", SafeOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{36,}", SafeOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GithubTokenRegex();

    [GeneratedRegex(@"\bAIza[A-Za-z0-9\-_]{35}", SafeOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GoogleApiKeyRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9\-_]+\.eyJ[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+", SafeOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\b(server|data\s*source|initial\s*catalog|user\s*id|password|pwd)\s*=\s*[^;\s][^;]{2,}", SafeIgnoreCaseOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex(@"\b(password|passwd|pwd|secret|token|api[-_]?key)\s*[=:]\s*\S{4,}", SafeIgnoreCaseOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyValueSecretRegex();

    public static bool IsSensitive(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            return OpenAiKeyRegex().IsMatch(text)
                || GithubTokenRegex().IsMatch(text)
                || GoogleApiKeyRegex().IsMatch(text)
                || JwtRegex().IsMatch(text)
                || ConnectionStringRegex().IsMatch(text)
                || KeyValueSecretRegex().IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static string Mask(string? text) => "***";
}
