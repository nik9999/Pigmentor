using Pigmentor.App.Security;

namespace Pigmentor.Tests;

public sealed class LinuxSecretStoreTests
{
    private sealed class MockLinuxSecretToolRunner : ILinuxSecretToolRunner
    {
        private readonly Dictionary<(string Service, string Key), string> _store = new();
        public bool Available { get; set; } = true;
        public bool ThrowOnStore { get; set; }
        public bool ThrowOnLookup { get; set; }

        public bool IsAvailable() => Available;

        public Task StoreSecretAsync(string label, string service, string key, string secret, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnStore)
            {
                throw new InvalidOperationException("Simulated D-Bus daemon communication error");
            }

            _store[(service, key)] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> LookupSecretAsync(string service, string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnLookup)
            {
                throw new InvalidOperationException("Simulated D-Bus daemon communication error");
            }

            _store.TryGetValue((service, key), out var value);
            return Task.FromResult(value);
        }

        public Task ClearSecretAsync(string service, string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _store.Remove((service, key));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LinuxSecretService_StoreAndRetrieve_Succeeds()
    {
        var runner = new MockLinuxSecretToolRunner();
        var store = new LinuxSecretServiceSecretStore(runner);

        await store.SetAsync("gigachat_auth_key", "sec-secret-token-12345");
        var retrieved = await store.GetAsync("gigachat_auth_key");

        Assert.Equal("sec-secret-token-12345", retrieved);
    }

    [Fact]
    public async Task LinuxSecretService_Delete_ClearsSecret()
    {
        var runner = new MockLinuxSecretToolRunner();
        var store = new LinuxSecretServiceSecretStore(runner);

        await store.SetAsync("gigachat_auth_key", "sec-secret-token-12345");
        await store.DeleteAsync("gigachat_auth_key");
        var retrieved = await store.GetAsync("gigachat_auth_key");

        Assert.Null(retrieved);
    }

    [Fact]
    public async Task LinuxSecretService_NonExistentKey_ReturnsNull()
    {
        var runner = new MockLinuxSecretToolRunner();
        var store = new LinuxSecretServiceSecretStore(runner);

        var retrieved = await store.GetAsync("unknown_key");
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task LinuxSecretService_WhenUnavailable_SetThrowsPlatformNotSupportedException_FailClosed()
    {
        var runner = new MockLinuxSecretToolRunner { Available = false };
        var store = new LinuxSecretServiceSecretStore(runner);

        var ex = await Assert.ThrowsAsync<PlatformNotSupportedException>(
            async () => await store.SetAsync("gigachat_auth_key", "secret"));

        Assert.Contains("Secure Linux Secret Service storage is unavailable", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PIGMENTOR_GIGACHAT_AUTH_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxSecretService_WhenUnavailable_GetReturnsNull_ForEnvFallback()
    {
        var runner = new MockLinuxSecretToolRunner { Available = false };
        var store = new LinuxSecretServiceSecretStore(runner);

        var retrieved = await store.GetAsync("gigachat_auth_key");
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task LinuxSecretService_HonorsCancellation()
    {
        var runner = new MockLinuxSecretToolRunner();
        var store = new LinuxSecretServiceSecretStore(runner);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.SetAsync("key", "val", cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.GetAsync("key", cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.DeleteAsync("key", cts.Token));
    }
}

