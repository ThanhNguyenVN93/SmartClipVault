using System.Text.RegularExpressions;

namespace SmartClipVault.Services;

public static partial class SecretDetector
{
    private const RegexOptions SafeOptions = RegexOptions.NonBacktracking;
    private const RegexOptions SafeIgnoreCaseOptions = RegexOptions.IgnoreCase | RegexOptions.NonBacktracking;

    [GeneratedRegex(@"\bsk-(?:proj-|svcacct-|admin-)?[A-Za-z0-9\-_]{20,}", SafeOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{20,})", SafeOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GithubTokenRegex();

    [GeneratedRegex(@"\bAIza[A-Za-z0-9\-_]{35}", SafeOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GoogleApiKeyRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9\-_]+\.eyJ[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+", SafeOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\b(server|data\s*source|initial\s*catalog|user\s*id|password|pwd)\s*=\s*[^;\s][^;]{2,}", SafeIgnoreCaseOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex(@"[""']?\b(password|passwd|pwd|secret|token|api[-_]?key)\b[""']?\s*[=:]\s*(?:""[^""\r\n]{4,}""|'[^'\r\n]{4,}'|\S{4,})", SafeIgnoreCaseOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 _-]{0,40}PRIVATE KEY(?: BLOCK)?-----", SafeOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PemPrivateKeyRegex();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b", SafeOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"[""']?\bAuthorization\b[""']?\s*:\s*[""']?Bearer\s+[^\s""']{4,}", SafeIgnoreCaseOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BearerAuthRegex();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]*://[^\s/:@#?]*:[^\s/@#?]+@[^\s/@#?]+", SafeIgnoreCaseOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UrlCredentialsRegex();

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
                || KeyValueSecretRegex().IsMatch(text)
                || PemPrivateKeyRegex().IsMatch(text)
                || AwsAccessKeyRegex().IsMatch(text)
                || BearerAuthRegex().IsMatch(text)
                || UrlCredentialsRegex().IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    public static string Mask(string? text) => "***";
}
