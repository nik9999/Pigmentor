using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pigmentor.App.Security;

/// <summary>
/// Windows DPAPI implementation of <see cref="ISecretStore"/> using <see cref="ProtectedData"/>.
/// Secrets are encrypted with the current user's credentials and can only be decrypted
/// by the same Windows user account on the same machine.
/// </summary>
public sealed class WindowsDataProtectionSecretStore : ISecretStore, IDisposable
{
    private static readonly byte[] Entropy = "Pigmentor.CredentialStore.v1"u8.ToArray();
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public WindowsDataProtectionSecretStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("WindowsDataProtectionSecretStore is only supported on Windows.");
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var secrets = LoadSecretsCore();
            return secrets.TryGetValue(name, out var value) ? value : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("WindowsDataProtectionSecretStore is only supported on Windows.");
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var secrets = LoadSecretsCore();
            secrets[name] = value;
            SaveSecretsCore(secrets);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("WindowsDataProtectionSecretStore is only supported on Windows.");
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var secrets = LoadSecretsCore();
            if (secrets.Remove(name))
            {
                if (secrets.Count == 0 && File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                }
                else
                {
                    SaveSecretsCore(secrets);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private Dictionary<string, string> LoadSecretsCore()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var encryptedBytes = File.ReadAllBytes(_filePath);
            if (encryptedBytes.Length == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(decryptedBytes);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (CryptographicException)
        {
            // Corrupted or unreadable by current user
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    [SupportedOSPlatform("windows")]
    private void SaveSecretsCore(Dictionary<string, string> secrets)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(secrets);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);

        var tempPath = _filePath + $".tmp.{Guid.NewGuid():N}";
        File.WriteAllBytes(tempPath, encryptedBytes);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lock.Dispose();
        _disposed = true;
    }
}

