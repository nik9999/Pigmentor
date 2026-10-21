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
        PigmentorConfig config;
        try
        {
            config = await _configManager.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _console.WriteError($"Configuration Error: {ex.Message}");
            return 2;
        }

        var validationErrors = PigmentorConfigManager.Validate(config, _paths, validateFilesExist: false);
        if (validationErrors.Count > 0)
        {
            _console.WriteError("Configuration Validation Errors:");
            foreach (var err in validationErrors)
            {
                _console.WriteError($"  - {err}");
            }
            return 2;
        }

        var effectiveRoot = _paths.ResolveEffectiveRootPath(config.RootPath);
        var dbPath = _paths.GetDatabasePath(effectiveRoot);
        var credentialRes = await _credentialProvider.ResolveCredentialAsync(cancellationToken).ConfigureAwait(false);

        _console.WriteLine("Pigmentor Operational Status");
        _console.WriteLine("============================");
        _console.WriteLine();

        _console.WriteLine("Workspace:");
        _console.WriteLine($"  Root Path:        {effectiveRoot}");
        _console.WriteLine($"  Database:         {dbPath}");
        var dbExists = File.Exists(dbPath);
        _console.WriteLine($"  Database on disk: {(dbExists ? "exists" : "will be created on first sync")}");
        _console.WriteLine();

        _console.WriteLine("Chat:");
        _console.WriteLine($"  Provider:         {config.Chat!.Provider}");
        _console.WriteLine($"  Model:            {config.Chat.Model}");
        _console.WriteLine($"  Endpoint:         {config.Chat.Endpoint}");
        _console.WriteLine($"  Credentials:      {(credentialRes.IsConfigured ? $"configured ({credentialRes.SourceDescription})" : "not configured")}");
        _console.WriteLine();

        _console.WriteLine("Embeddings:");
        _console.WriteLine($"  Provider:         {config.Embeddings!.Provider}");
        if (string.Equals(config.Embeddings.Provider, "Onnx", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedModel = !string.IsNullOrWhiteSpace(config.Embeddings.ModelPath)
                ? _paths.ResolveApplicationPath(config.Embeddings.ModelPath)
                : "(none)";
            var resolvedTok = !string.IsNullOrWhiteSpace(config.Embeddings.TokenizerPath)
                ? _paths.ResolveApplicationPath(config.Embeddings.TokenizerPath)
                : "(none)";
            _console.WriteLine($"  Model Path:       {config.Embeddings.ModelPath} (resolved: {resolvedModel})");
            _console.WriteLine($"  Tokenizer Path:   {config.Embeddings.TokenizerPath ?? "(none)"} (resolved: {resolvedTok})");
        }
        else
        {
            _console.WriteLine($"  Model:            {config.Embeddings.Model}");
            _console.WriteLine($"  Endpoint:         {config.Embeddings.Endpoint}");
        }
        _console.WriteLine($"  Dimensions:       {config.Embeddings.Dimensions}");

        if (check)
        {
            _console.WriteLine();
            _console.WriteLine("Connection & Health Validation (--check):");
            var healthy = await ValidateConnectionsAsync(config, credentialRes, cancellationToken).ConfigureAwait(false);
            return healthy ? 0 : 1;
        }

        return 0;
    }

    private async Task<bool> ValidateConnectionsAsync(
        PigmentorConfig config,
        CredentialResolutionResult credentialRes,
        CancellationToken cancellationToken)
    {
        var allHealthy = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // 1. Validate Chat Provider
        if (string.Equals(config.Chat!.Provider, "GigaChat", StringComparison.OrdinalIgnoreCase))
        {
            if (!credentialRes.IsConfigured)
            {
                _console.WriteLine("  Chat (GigaChat): FAILED (credentials are not configured).");
                allHealthy = false;
            }
            else
            {
                try
                {
                    _console.Write("  Chat (GigaChat auth): ");
                    var authClient = new GigaChatAuthClient(new GigaChatClientOptions
                    {
                        AuthorizationKey = credentialRes.Key,
                        Scope = config.GigaChat?.Scope ?? "GIGACHAT_API_PERS",
                        IgnoreSslValidation = config.GigaChat != null && !config.GigaChat.VerifySsl
                    }, http);

                    _ = await authClient.GetAccessTokenAsync(cts.Token).ConfigureAwait(false);
                    _console.WriteLine("OK (authenticated successfully)");

                    // Also check chat endpoint reachability
                    _console.Write("  Chat (GigaChat endpoint reachability): ");
                    var chatBaseUri = new Uri(config.Chat.Endpoint);
                    var checkResp = await http.GetAsync(chatBaseUri, cts.Token).ConfigureAwait(false);
                    _console.WriteLine("OK (endpoint reachable)");
                }
                catch (Exception ex)
                {
                    _console.WriteLine($"FAILED ({SecretRedactor.Redact(ex.Message, credentialRes.Key)})");
                    allHealthy = false;
                }
            }
        }
        else if (string.Equals(config.Chat.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _console.Write("  Chat (Ollama): ");
                var resp = await http.GetAsync(new Uri(new Uri(config.Chat.Endpoint), "/api/tags"), cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _console.WriteLine("OK");
                }
                else
                {
                    _console.WriteLine($"FAILED (HTTP {(int)resp.StatusCode})");
                    allHealthy = false;
                }
            }
            catch (Exception ex)
            {
                _console.WriteLine($"FAILED ({ex.Message})");
                allHealthy = false;
            }
        }

        // 2. Validate Embeddings Provider
        if (string.Equals(config.Embeddings!.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _console.Write("  Embeddings (Ollama): ");
                var resp = await http.GetAsync(new Uri(new Uri(config.Embeddings.Endpoint), "/api/tags"), cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    _console.WriteLine("OK");
                }
                else
                {
                    _console.WriteLine($"FAILED (HTTP {(int)resp.StatusCode})");
                    allHealthy = false;
                }
            }
            catch (Exception ex)
            {
                _console.WriteLine($"FAILED ({ex.Message})");
                allHealthy = false;
            }
        }
        else if (string.Equals(config.Embeddings.Provider, "Onnx", StringComparison.OrdinalIgnoreCase))
        {
            _console.Write("  Embeddings (ONNX file validation): ");
            var modelResolved = !string.IsNullOrWhiteSpace(config.Embeddings.ModelPath)
                ? _paths.ResolveApplicationPath(config.Embeddings.ModelPath)
                : null;
            var tokResolved = !string.IsNullOrWhiteSpace(config.Embeddings.TokenizerPath)
                ? _paths.ResolveApplicationPath(config.Embeddings.TokenizerPath)
                : null;

            var modelExists = modelResolved != null && File.Exists(modelResolved);
            var tokExists = tokResolved != null && File.Exists(tokResolved);

            if (modelExists && tokExists)
            {
                _console.WriteLine("OK (model and tokenizer files exist)");
            }
            else
            {
                var missing = new List<string>();
                if (!modelExists)
                {
                    missing.Add($"model file '{config.Embeddings.ModelPath}' (resolved: '{modelResolved}') not found");
                }
                if (!tokExists)
                {
                    missing.Add($"tokenizer file '{config.Embeddings.TokenizerPath}' (resolved: '{tokResolved}') not found");
                }
                _console.WriteLine($"FAILED ({string.Join(", ", missing)})");
                allHealthy = false;
            }
        }

        return allHealthy;
    }
}
