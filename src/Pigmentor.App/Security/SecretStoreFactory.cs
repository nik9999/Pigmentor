using System.Runtime.InteropServices;
using Pigmentor.App.Paths;

namespace Pigmentor.App.Security;

/// <summary>
/// Factory for instantiating OS-protected credential stores.
/// Fails closed when no OS-protected credential store is supported.
/// </summary>
public static class SecretStoreFactory
{
    public static ISecretStore CreateDefault(IPigmentorAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsDataProtectionSecretStore(paths.SecretsFilePath);
        }

        return new FailClosedSecretStore(
            "Secure credential store is unavailable on this platform. Use environment variable PIGMENTOR_GIGACHAT_AUTH_KEY or configure a supported credential store.");
    }
}

