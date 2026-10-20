using Pigmentor.App.Credentials;
using Pigmentor.App.Security;
using Xunit;

namespace Pigmentor.Tests;

public sealed class CredentialResolutionTests
{
    [Fact]
    public async Task ResolveCredential_Precedence_Explicit_WinsOverEnvAndStore()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("gigachat_auth_key", "store-key");

        var resolver = new GigaChatCredentialResolver(
            secretStore: secretStore,
            explicitKey: "explicit-key",
            envResolver: key => "env-key");

        var result = await resolver.ResolveCredentialAsync();

        Assert.Equal("explicit-key", result.Key);
        Assert.Equal(CredentialSource.Explicit, result.Source);
        Assert.True(result.IsConfigured);
        Assert.Equal("explicit-key", await resolver.GetAuthorizationKeyAsync());
    }

    [Fact]
    public async Task ResolveCredential_Precedence_Environment_WinsOverStore()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("gigachat_auth_key", "store-key");

        var resolver = new GigaChatCredentialResolver(
            secretStore: secretStore,
            explicitKey: null,
            envResolver: key => key == GigaChatCredentialResolver.EnvironmentVariableName ? "env-key" : null);

        var result = await resolver.ResolveCredentialAsync();

        Assert.Equal("env-key", result.Key);
        Assert.Equal(CredentialSource.Environment, result.Source);
        Assert.True(result.IsConfigured);
    }

    [Fact]
    public async Task ResolveCredential_Precedence_SecretStore_UsedWhenNoExplicitOrEnv()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("gigachat_auth_key", "store-key");

        var resolver = new GigaChatCredentialResolver(
            secretStore: secretStore,
            explicitKey: null,
            envResolver: key => null);

        var result = await resolver.ResolveCredentialAsync();

        Assert.Equal("store-key", result.Key);
        Assert.Equal(CredentialSource.SecretStore, result.Source);
        Assert.True(result.IsConfigured);
    }

    [Fact]
    public async Task ResolveCredential_ReturnsNone_WhenAllSourcesMissing()
    {
        var secretStore = new InMemorySecretStore();

        var resolver = new GigaChatCredentialResolver(
            secretStore: secretStore,
            explicitKey: null,
            envResolver: key => null);

        var result = await resolver.ResolveCredentialAsync();

        Assert.Null(result.Key);
        Assert.Equal(CredentialSource.None, result.Source);
        Assert.False(result.IsConfigured);
        Assert.Null(await resolver.GetAuthorizationKeyAsync());
    }
}

