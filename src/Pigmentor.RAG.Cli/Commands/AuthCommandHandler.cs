using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Security;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

public sealed class AuthCommandHandler
{
    private readonly ISecretStore _secretStore;
    private readonly IGigaChatCredentialProvider _credentialProvider;
    private readonly PigmentorConfigManager _configManager;
    private readonly IConsoleIO _console;

    public AuthCommandHandler(
        ISecretStore secretStore,
        IGigaChatCredentialProvider credentialProvider,
        PigmentorConfigManager configManager,
        IConsoleIO console)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public async Task<int> SetGigaChatAsync(CancellationToken cancellationToken = default)
    {
        var key = _console.ReadSecret("GigaChat Authorization Key: ");

        if (string.IsNullOrWhiteSpace(key))
        {
            _console.WriteError("Error: Authorization key cannot be empty.");
            return 2;
        }

        try
        {
            await _secretStore.SetAsync(GigaChatCredentialResolver.SecretStoreKeyName, key.Trim(), cancellationToken).ConfigureAwait(false);
            _console.WriteLine("Credentials saved securely.");
            return 0;
        }
        catch (PlatformNotSupportedException ex)
        {
            _console.WriteError($"Error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            _console.WriteError($"Failed to save credentials: {SecretRedactor.Redact(ex.Message, key)}");
            return 1;
        }
    }

    public async Task<int> ShowStatusAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configManager.LoadAsync(cancellationToken).ConfigureAwait(false);
        var resolution = await _credentialProvider.ResolveCredentialAsync(cancellationToken).ConfigureAwait(false);

        _console.WriteLine("GigaChat");
        _console.WriteLine($"  Credentials: {(resolution.IsConfigured ? "configured" : "not configured")}");
        _console.WriteLine($"  Source:      {resolution.SourceDescription}");
        _console.WriteLine($"  Scope:       {config.GigaChat.Scope}");

        return 0;
    }

    public async Task<int> LogoutGigaChatAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _secretStore.DeleteAsync(GigaChatCredentialResolver.SecretStoreKeyName, cancellationToken).ConfigureAwait(false);
            _console.WriteLine("GigaChat credentials removed from secure storage.");

            var envValue = Environment.GetEnvironmentVariable(GigaChatCredentialResolver.EnvironmentVariableName);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                _console.WriteLine("Note: Credentials are also provided by environment variable 'PIGMENTOR_GIGACHAT_AUTH_KEY' and cannot be removed by Pigmentor.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            _console.WriteError($"Error removing credentials: {ex.Message}");
            return 1;
        }
    }
}

