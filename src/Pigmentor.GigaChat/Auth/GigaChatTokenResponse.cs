using System.Text.Json.Serialization;

namespace Pigmentor.GigaChat.Auth;

/// <summary>
/// OAuth 2.0 token response returned by Sber OAuth endpoint (/api/v2/oauth).
/// </summary>
public sealed class GigaChatTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// UNIX timestamp in seconds when the token expires.
    /// </summary>
    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }
}
