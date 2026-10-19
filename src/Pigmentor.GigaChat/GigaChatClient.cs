using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Pigmentor.GigaChat.Auth;
using Pigmentor.GigaChat.Model;

namespace Pigmentor.GigaChat;

/// <summary>
/// Primary service client for interacting with the Sber GigaChat REST API.
/// Provides access to Chat Completions, Text Embeddings, and token utilities.
/// </summary>
public sealed class GigaChatClient : IDisposable
{
    private readonly GigaChatClientOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly GigaChatAuthClient _authClient;
    private bool _disposed;

    /// <summary>
    /// Gets the configuration options used by this client.
    /// </summary>
    public GigaChatClientOptions Options => _options;

    public GigaChatClient(GigaChatClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.HttpClient != null)
        {
            _httpClient = options.HttpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(1),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            };

            if (options.IgnoreSslValidation)
            {
#pragma warning disable CA5359 // Sberbank endpoints use Russian Trusted Root CA which is not trusted by default OS certificate stores.
                handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
            }

            _httpClient = new HttpClient(handler)
            {
                Timeout = options.Timeout
            };
            _ownsHttpClient = true;
        }

        _authClient = new GigaChatAuthClient(_options, _httpClient);
    }

    /// <summary>
    /// Initializes a new instance of <see cref="GigaChatClient"/> with a Base64-encoded Authorization Key.
    /// </summary>
    /// <param name="authorizationKey">Base64-encoded Client ID:Client Secret key from Sber Developers Studio.</param>
    /// <param name="scope">OAuth scope (e.g. <see cref="GigaChatScope.Personal"/>, <see cref="GigaChatScope.Corporate"/>).</param>
    public GigaChatClient(string authorizationKey, string? scope = null)
        : this(new GigaChatClientOptions
        {
            AuthorizationKey = authorizationKey,
            Scope = scope ?? GigaChatScope.Personal
        })
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="GigaChatClient"/> with explicit Client ID and Client Secret credentials.
    /// </summary>
    /// <param name="clientId">Client ID from Sber Developers Studio.</param>
    /// <param name="clientSecret">Client Secret from Sber Developers Studio.</param>
    /// <param name="scope">OAuth scope (e.g. <see cref="GigaChatScope.Personal"/>, <see cref="GigaChatScope.Corporate"/>).</param>
    public GigaChatClient(string clientId, string clientSecret, string? scope = null)
        : this(new GigaChatClientOptions
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            Scope = scope ?? GigaChatScope.Personal
        })
    {
    }

    /// <summary>
    /// Creates a Microsoft.Extensions.AI <see cref="IChatClient"/> adapter for GigaChat.
    /// </summary>
    /// <param name="modelId">Optional target model ID. If null, defaults to <see cref="GigaChatClientOptions.DefaultModel"/>.</param>
    public IChatClient GetChatClient(string? modelId = null) =>
        new GigaChatChatClient(this, modelId ?? _options.DefaultModel);

    /// <summary>
    /// Creates a Microsoft.Extensions.AI <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> adapter for GigaChat.
    /// </summary>
    /// <param name="modelId">Optional target embedding model ID. If null, defaults to <see cref="GigaChatClientOptions.DefaultEmbeddingModel"/>.</param>
    public IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(string? modelId = null) =>
        new GigaChatEmbeddingGenerator(this, modelId ?? _options.DefaultEmbeddingModel);

    /// <summary>
    /// Convenience adapter alias matching Microsoft.Extensions.AI conventions.
    /// </summary>
    public IChatClient AsChatClient(string? modelId = null) => GetChatClient(modelId);

    /// <summary>
    /// Convenience adapter alias matching Microsoft.Extensions.AI conventions.
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>> AsEmbeddingGenerator(string? modelId = null) => GetEmbeddingGenerator(modelId);

    /// <summary>
    /// Executes a non-streaming chat completion call.
    /// </summary>
    public async Task<GigaChatCompletionsResponse> CompleteChatAsync(
        GigaChatCompletionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        request.Stream = false;
        var endpoint = new Uri(_options.BaseUrl.ToString().TrimEnd('/') + "/chat/completions");

        using var response = await SendWithRetryAsync(token =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = JsonContent.Create(request);
            return req;
        }, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"GigaChat chat completion failed: {(int)response.StatusCode} {response.ReasonPhrase}. Details: {errorBody}",
                null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<GigaChatCompletionsResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("GigaChat returned an empty completions response.");
    }

    /// <summary>
    /// Executes a streaming Server-Sent Events (SSE) chat completion call.
    /// </summary>
    public async IAsyncEnumerable<GigaChatChoiceDto> CompleteChatStreamingAsync(
        GigaChatCompletionsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        request.Stream = true;
        var token = await _authClient.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        var endpoint = new Uri(_options.BaseUrl.ToString().TrimEnd('/') + "/chat/completions");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = JsonContent.Create(request);

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"GigaChat streaming completion failed: {(int)response.StatusCode} {response.ReasonPhrase}. Details: {errorBody}",
                null,
                response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                break;
            }

            line = line.Trim();
            if (line.Length == 0 || line.StartsWith(':'))
            {
                continue; // Empty line or SSE comment
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var json = line["data:".Length..].Trim();
                if (string.Equals(json, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                GigaChatCompletionsResponse? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<GigaChatCompletionsResponse>(json);
                }
                catch
                {
                    continue;
                }

                if (chunk?.Choices != null)
                {
                    foreach (var choice in chunk.Choices)
                    {
                        yield return choice;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Generates vector embeddings for a batch of input texts.
    /// </summary>
    public async Task<GigaChatEmbeddingsResponse> GenerateEmbeddingsAsync(
        GigaChatEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var endpoint = new Uri(_options.BaseUrl.ToString().TrimEnd('/') + "/embeddings");

        using var response = await SendWithRetryAsync(token =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = JsonContent.Create(request);
            return req;
        }, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"GigaChat embeddings call failed: {(int)response.StatusCode} {response.ReasonPhrase}. Details: {errorBody}",
                null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<GigaChatEmbeddingsResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("GigaChat returned an empty embeddings response.");
    }

    /// <summary>
    /// Uploads a file (e.g. image) to GigaChat Files API for multimodal processing.
    /// </summary>
    /// <param name="fileBytes">Binary contents of the file.</param>
    /// <param name="fileName">File name with extension (e.g. image.png).</param>
    /// <param name="mimeType">MIME type (e.g. image/png, image/jpeg).</param>
    /// <param name="purpose">Purpose of the file upload (defaults to "general").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The unique file ID returned by GigaChat.</returns>
    public async Task<string> UploadFileAsync(
        byte[] fileBytes,
        string fileName = "image.png",
        string mimeType = "image/png",
        string purpose = "general",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        ThrowIfDisposed();

        var endpoint = new Uri(_options.BaseUrl.ToString().TrimEnd('/') + "/files");

        using var response = await SendWithRetryAsync(token =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var multipart = new MultipartFormDataContent();
            var byteContent = new ByteArrayContent(fileBytes);
            byteContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            multipart.Add(byteContent, "file", fileName);
            multipart.Add(new StringContent(purpose), "purpose");

            req.Content = multipart;
            return req;
        }, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"GigaChat file upload failed: {(int)response.StatusCode} {response.ReasonPhrase}. Details: {errorBody}",
                null,
                response.StatusCode);
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (doc.RootElement.TryGetProperty("id", out var idProp))
        {
            return idProp.GetString() ?? throw new InvalidOperationException("GigaChat returned an empty file id.");
        }

        throw new InvalidOperationException("GigaChat file upload response missing 'id' property.");
    }

    /// <summary>
    /// Retrieves the list of available models from the GigaChat API via GET /models.
    /// </summary>
    public async Task<IReadOnlyList<GigaChatModelDto>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var endpoint = new Uri(_options.BaseUrl.ToString().TrimEnd('/') + "/models");

        using var response = await SendWithRetryAsync(token =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return req;
        }, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"GigaChat get models failed: {(int)response.StatusCode} {response.ReasonPhrase}. Details: {errorBody}",
                null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<GigaChatModelsResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return result?.Data ?? [];
    }

    /// <summary>
    /// Retrieves information about a specific model via GET /models/{modelId}.
    /// </summary>
    public async Task<GigaChatModelDto?> GetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ThrowIfDisposed();

        var endpoint = new Uri(_options.BaseUrl.ToString().TrimEnd('/') + $"/models/{Uri.EscapeDataString(modelId)}");

        using var response = await SendWithRetryAsync(token =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return req;
        }, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<GigaChatModelDto>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<string, HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        int maxAttempts = Math.Max(1, _options.MaxRetries + 1);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var token = await _authClient.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var request = requestFactory(token);

            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt < maxAttempts)
                {
                    _authClient.InvalidateToken();
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (attempt < maxAttempts && IsTransientStatusCode(response.StatusCode))
                {
                    var delay = CalculateDelay(attempt, response);
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientException(ex, cancellationToken))
            {
                response?.Dispose();
                var delay = CalculateDelay(attempt, null);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("GigaChat retry policy exhausted without returning a response.");
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.TooManyRequests => true, // 429
            HttpStatusCode.InternalServerError => true, // 500
            HttpStatusCode.BadGateway => true, // 502
            HttpStatusCode.ServiceUnavailable => true, // 503
            HttpStatusCode.GatewayTimeout => true, // 504
            HttpStatusCode.RequestTimeout => true, // 408
            _ => false,
        };

    private static bool IsTransientException(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (ex is OperationCanceledException)
        {
            // If caller token was not requested, this is an internal HttpClient.Timeout (transient)
            return true;
        }

        if (ex is TimeoutException)
        {
            return true;
        }

        if (ex is HttpRequestException)
        {
            return true;
        }

        if (ex is IOException)
        {
            return true;
        }

        if (ex is SocketException)
        {
            return true;
        }

        return ex.InnerException != null && IsTransientException(ex.InnerException, cancellationToken);
    }

    private TimeSpan CalculateDelay(int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero && delta < TimeSpan.FromSeconds(30))
        {
            return delta;
        }

        var baseMs = _options.RetryBaseDelay.TotalMilliseconds;
        var expMs = baseMs * Math.Pow(2, attempt - 1);
        var clampedMs = Math.Min(expMs, 10000);
        return TimeSpan.FromMilliseconds(clampedMs);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _authClient.Dispose();
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
            _disposed = true;
        }
    }
}
