using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pigmentor.GigaChat.Auth;

/// <summary>
/// Manages OAuth 2.0 token acquisition, caching, and automatic renewal for Sber GigaChat.
/// </summary>
public sealed class GigaChatAuthClient : IDisposable
{
    private readonly GigaChatClientOptions _options;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public GigaChatAuthClient(GigaChatClientOptions options, HttpClient httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Returns a valid Bearer access token, acquiring or refreshing it as necessary.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // 1. Direct access token check
        if (!string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            return _options.AccessToken.Trim();
        }

        if (!string.IsNullOrEmpty(_options.AuthorizationKey) && _options.AuthorizationKey.StartsWith("eyJ", StringComparison.Ordinal))
        {
            return _options.AuthorizationKey.Trim();
        }

        // 2. Return cached token if valid (with 60-second safety margin)
        if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiresAt.AddSeconds(-60))
        {
            return _cachedToken;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock
            if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiresAt.AddSeconds(-60))
            {
                return _cachedToken;
            }

            var authKey = _options.ResolveAuthorizationKey();
            if (string.IsNullOrWhiteSpace(authKey))
            {
                throw new InvalidOperationException(
                    "GigaChat credentials not specified. Provide either AuthorizationKey (Client ID:Client Secret in Base64 from Sber Developers Studio) or explicit ClientId and ClientSecret.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.AuthUrl);
            request.Headers.Add("RqUID", Guid.NewGuid().ToString());
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            request.Content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("scope", _options.Scope)
            ]);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Failed to acquire Sber GigaChat OAuth token. Status: {(int)response.StatusCode} {response.ReasonPhrase}. Details: {errorBody}",
                    null,
                    response.StatusCode);
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<GigaChatTokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                throw new InvalidOperationException("Sber GigaChat OAuth endpoint returned an empty or invalid token response.");
            }

            _cachedToken = tokenResponse.AccessToken;
            _tokenExpiresAt = tokenResponse.ExpiresAt > 0
                ? DateTimeOffset.FromUnixTimeSeconds(tokenResponse.ExpiresAt)
                : DateTimeOffset.UtcNow.AddMinutes(28); // Default 30 min lifetime fallback

            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Invalidates the currently cached access token, forcing a refresh on the next request.
    /// </summary>
    public void InvalidateToken()
    {
        _cachedToken = null;
        _tokenExpiresAt = DateTimeOffset.MinValue;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _lock.Dispose();
            _disposed = true;
        }
    }
}
