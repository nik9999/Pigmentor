using System.Runtime.InteropServices;
using Pigmentor.App.Paths;
using Pigmentor.App.Security;
using Xunit;

namespace Pigmentor.Tests;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _secretsFilePath;

    public SecretStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_sec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _secretsFilePath = Path.Combine(_tempDirectory, "secrets.dat");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task InMemorySecretStore_SupportsSetGetOverwriteAndDelete()
    {
        var store = new InMemorySecretStore();

        // 1. Missing value returns null
        Assert.Null(await store.GetAsync("test_key"));

        // 2. Set and Get
        await store.SetAsync("test_key", "secret-value-123");
        Assert.Equal("secret-value-123", await store.GetAsync("test_key"));

        // 3. Overwrite
        await store.SetAsync("test_key", "secret-value-updated");
        Assert.Equal("secret-value-updated", await store.GetAsync("test_key"));

        // 4. Delete
        await store.DeleteAsync("test_key");
        Assert.Null(await store.GetAsync("test_key"));

        // 5. Delete non-existent is a safe no-op
        await store.DeleteAsync("test_key");
    }

    [Fact]
    public async Task WindowsDataProtectionSecretStore_SupportsSetGetOverwriteAndDelete_OnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var store = new WindowsDataProtectionSecretStore(_secretsFilePath);

        // 1. Missing value returns null
        Assert.Null(await store.GetAsync("gigachat_auth_key"));

        // 2. Set and Get
        await store.SetAsync("gigachat_auth_key", "sec-12345-abcdef");
        Assert.Equal("sec-12345-abcdef", await store.GetAsync("gigachat_auth_key"));

        // Verify underlying file is NOT plain text!
        var rawBytes = await File.ReadAllBytesAsync(_secretsFilePath);
        var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);
        Assert.DoesNotContain("sec-12345-abcdef", rawText, StringComparison.Ordinal);

        // 3. Re-open store from disk and verify persistence
        using var store2 = new WindowsDataProtectionSecretStore(_secretsFilePath);
        Assert.Equal("sec-12345-abcdef", await store2.GetAsync("gigachat_auth_key"));

        // 4. Overwrite
        await store2.SetAsync("gigachat_auth_key", "sec-updated-67890");
        Assert.Equal("sec-updated-67890", await store2.GetAsync("gigachat_auth_key"));

        // 5. Delete
        await store2.DeleteAsync("gigachat_auth_key");
        Assert.Null(await store2.GetAsync("gigachat_auth_key"));
    }

    [Fact]
    public async Task FailClosedSecretStore_ThrowsPlatformNotSupported_OnSet()
    {
        var store = new FailClosedSecretStore("Custom fail reason");

        // Get returns null
        Assert.Null(await store.GetAsync("key"));

        // Set fails closed
        var ex = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.SetAsync("key", "val").AsTask());
        Assert.Contains("Custom fail reason", ex.Message);

        // Delete is no-op
        await store.DeleteAsync("key");
    }

    [Fact]
    public void SecretStoreFactory_ReturnsAppropriateStoreForCurrentPlatform()
    {
        var paths = new PigmentorAppPaths(_tempDirectory, _tempDirectory);
        var store = SecretStoreFactory.CreateDefault(paths);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.IsType<WindowsDataProtectionSecretStore>(store);
        }
        else
        {
            Assert.IsType<FailClosedSecretStore>(store);
        }
    }
}

