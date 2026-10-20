using System.Net.Http.Headers;
using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Paths;
using Pigmentor.App.Security;
using Pigmentor.GigaChat;
using Pigmentor.GigaChat.Auth;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

public sealed class StatusCommandHandler
{
    private readonly IPigmentorAppPaths _paths;
    private readonly PigmentorConfigManager _configManager;
    private readonly IGigaChatCredentialProvider _credentialProvider;
    private readonly IConsoleIO _console;

    public StatusCommandHandler(
        IPigmentorAppPaths paths,
        PigmentorConfigManager configManager,
        IGigaChatCredentialProvider credentialProvider,
        IConsoleIO console)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public async Task<int> ExecuteAsync(bool check = false, CancellationToken cancellationToken = default)
    {
        var config = await _configManager.LoadAsync(cancellationToken).ConfigureAwait(false);
        var credentialRes = await _credentialProvider.ResolveCredentialAsync(cancellationToken).ConfigureAwait(false);

        _console.WriteLine("Pigmentor Operational Status");
        _console.WriteLine("============================");
        _console.WriteLine();

        _console.WriteLine("Chat:");
        _console.WriteLine($"  Provider:    {config.Chat.Provider}");
        _console.WriteLine($"  Model:       {config.Chat.Model}");
        _console.WriteLine($"  Endpoint:    {config.Chat.Endpoint}");
        _console.WriteLine($"  Credentials: {(credentialRes.IsConfigured ? $"configured ({credentialRes.SourceDescription})" : "not configured")}");
        _console.WriteLine();

        _console.WriteLine("Embeddings:");
        _console.WriteLine($"  Provider:    {config.Embeddings.Provider}");
        _console.WriteLine($"  Model:       {config.Embeddings.Model}");
        _console.WriteLine($"  Dimensions:  {config.Embeddings.Dimensions}");
        _console.WriteLine();

        _console.WriteLine("Storage:");
        _console.WriteLine("  Backend:     SQLite + sqlite-vec");
        _console.WriteLine($"  Database:    {config.Storage.DatabasePath}");
        var dbExists = !string.IsNullOrWhiteSpace(config.Storage.DatabasePath) && File.Exists(config.Storage.DatabasePath);
        _console.WriteLine($"  Database on disk: {(dbExists ? "exists" : "will be created on first ingestion")}");

        if (check)
        {
            _console.WriteLine();
            _console.WriteLine("Connection Validation (--check):");

            await ValidateConnectionsAsync(config, credentialRes, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task ValidateConnectionsAsync(
        PigmentorConfig config,
        CredentialResolutionResult credentialRes,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // 1. Validate Chat
        if (string.Equals(config.Chat.Provider, "GigaChat", StringComparison.OrdinalIgnoreCase))
        {
            if (!credentialRes.IsConfigured)
            {
                _console.WriteLine("  Chat (GigaChat): credentials missing (cannot validate).");
            }
            else
            {
                try
                {
                    _console.Write("  Chat (GigaChat auth): ");
                    // Check auth endpoint with authorization key
                    var authClient = new GigaChatAuthClient(new GigaChatClientOptions
                    {
                        AuthorizationKey = credentialRes.Key,
                        Scope = config.GigaChat.Scope,
                        IgnoreSslValidation = !config.GigaChat.VerifySsl
                    }, http);

                    _ = await authClient.GetAccessTokenAsync(cts.Token).ConfigureAwait(false);
                    _console.WriteLine("OK (authenticated successfully)");
                }
                catch (Exception ex)
                {
                    _console.WriteLine($"Failed ({SecretRedactor.Redact(ex.Message, credentialRes.Key)})");
                }
            }
        }
        else if (string.Equals(config.Chat.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _console.Write("  Chat (Ollama): ");
                var resp = await http.GetAsync(new Uri(new Uri(config.Chat.Endpoint), "/api/tags"), cts.Token).ConfigureAwait(false);
                _console.WriteLine(resp.IsSuccessStatusCode ? "OK" : $"Response code {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                _console.WriteLine($"Failed ({ex.Message})");
            }
        }

        // 2. Validate Embeddings if Ollama
        if (string.Equals(config.Embeddings.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _console.Write("  Embeddings (Ollama): ");
                var resp = await http.GetAsync(new Uri(new Uri(config.Embeddings.Endpoint), "/api/tags"), cts.Token).ConfigureAwait(false);
                _console.WriteLine(resp.IsSuccessStatusCode ? "OK" : $"Response code {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                _console.WriteLine($"Failed ({ex.Message})");
            }
        }
    }
}
