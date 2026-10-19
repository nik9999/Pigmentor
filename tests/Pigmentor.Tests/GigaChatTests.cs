using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Pigmentor.GigaChat;
using Pigmentor.GigaChat.Auth;

namespace Pigmentor.Tests;

public sealed class GigaChatTests
{
    [Fact]
    public async Task AuthClientCachesTokenUsingExpiryInUnixSeconds()
    {
        var requests = 0;
        var handler = new DelegateHandler(_ =>
        {
            requests++;
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"access_token\":\"token\",\"expires_at\":{expiresAt}}}", Encoding.UTF8, "application/json")
            };
        });
        var httpClient = new HttpClient(handler);
        var options = new GigaChatClientOptions
        {
            AuthorizationKey = "client-secret",
            AuthUrl = new Uri("https://auth.test/api/v2/oauth")
        };
        using var client = new GigaChatAuthClient(options, httpClient);

        Assert.Equal("token", await client.GetAccessTokenAsync());
        Assert.Equal("token", await client.GetAccessTokenAsync());
        Assert.Equal(1, requests);
    }

    [Fact]
    public void DefaultsTargetCurrentApiAndValidateTls()
    {
        var options = new GigaChatClientOptions();

        Assert.Equal("https://api.giga.chat/v1", options.BaseUrl.ToString());
        Assert.False(options.IgnoreSslValidation);
        Assert.Equal("GigaChat-2-Lite", options.DefaultModel);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
