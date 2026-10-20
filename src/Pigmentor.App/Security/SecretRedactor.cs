namespace Pigmentor.App.Security;

/// <summary>
/// Defensive secret redaction utility to prevent sensitive credentials from appearing in
/// exception messages, diagnostic output, or console logs.
/// </summary>
public static class SecretRedactor
{
    public const string RedactedReplacement = "[REDACTED]";

    /// <summary>
    /// Replaces all occurrences of known sensitive strings in <paramref name="text"/> with "[REDACTED]".
    /// </summary>
    public static string Redact(string? text, params string?[] knownSecrets)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var secret in knownSecrets)
        {
            if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 4)
            {
                result = result.Replace(secret, RedactedReplacement, StringComparison.Ordinal);
            }
        }

        return result;
    }
}

