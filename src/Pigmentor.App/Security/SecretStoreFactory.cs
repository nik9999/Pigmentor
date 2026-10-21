using System.Runtime.InteropServices;
using Pigmentor.App.Paths;

namespace Pigmentor.App.Security;

/// <summary>
/// Factory for instantiating OS-protected credential stores.
/// Windows: DPAPI protected user file storage.
/// Linux: FreeDesktop Secret Service via secret-tool (GNOME Keyring / compatible).
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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxSecretServiceSecretStore();
        }

        return new FailClosedSecretStore(
            "Secure credential store is unavailable on this platform. Use environment variable PIGMENTOR_GIGACHAT_AUTH_KEY or configure a supported credential store.");
    }
}
