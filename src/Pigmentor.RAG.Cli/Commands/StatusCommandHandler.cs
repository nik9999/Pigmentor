using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Paths;
using Pigmentor.App.Runtime;
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
            if (!string.IsNullOrWhiteSpace(config.Embeddings.ModelPath) && resolvedModel != "(none)")
            {
                var dir = Path.GetDirectoryName(resolvedModel);
                var externalData = dir != null ? Path.Combine(dir, "model.onnx_data") : null;
                if (externalData != null && File.Exists(externalData))
                {
                    _console.WriteLine($"  External Data:    model.onnx_data (resolved: {externalData})");
                }
            }
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
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

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
            _console.Write("  Embeddings (ONNX inference): ");
            var modelPath = config.Embeddings.ModelPath;
            var tokenizerPath = config.Embeddings.TokenizerPath;

            if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(tokenizerPath))
            {
                _console.WriteLine("FAILED");
                _console.WriteLine("    ModelPath or TokenizerPath is not configured in settings.");
                allHealthy = false;
            }
            else
            {
                var modelResolved = _paths.ResolveApplicationPath(modelPath);
                var tokResolved = _paths.ResolveApplicationPath(tokenizerPath);

                var modelExists = File.Exists(modelResolved);
                var tokExists = File.Exists(tokResolved);

                if (!modelExists || !tokExists)
                {
                    _console.WriteLine("FAILED");
                    if (!modelExists)
                    {
                        _console.WriteLine($"    ONNX model file '{modelPath}' (resolved: '{modelResolved}') was not found. Run scripts/download-bge-m3.ps1 to download production model assets.");
                    }
                    if (!tokExists)
                    {
                        _console.WriteLine($"    Tokenizer file '{tokenizerPath}' (resolved: '{tokResolved}') was not found. Run scripts/download-bge-m3.ps1 to download production model assets.");
                    }
                    allHealthy = false;
                }
                else
                {
                    // Check if external model weights are required:
                    // If model file size is less than 50MB and named model.onnx (e.g. BAAI graph definition ~725KB),
                    // model.onnx_data must be present next to model.onnx.
                    var modelDir = Path.GetDirectoryName(modelResolved) ?? string.Empty;
                    var externalDataPath = Path.Combine(modelDir, "model.onnx_data");
                    var modelFileInfo = new FileInfo(modelResolved);

                    if (string.Equals(Path.GetFileName(modelResolved), "model.onnx", StringComparison.OrdinalIgnoreCase)
                        && modelFileInfo.Length < 50 * 1024 * 1024
                        && !File.Exists(externalDataPath))
                    {
                        _console.WriteLine("FAILED");
                        _console.WriteLine($"    ONNX model graph requires external weights file 'model.onnx_data', which is missing in '{modelDir}'. Run scripts/download-bge-m3.ps1.");
                        allHealthy = false;
                    }
                    else
                    {
                        var expectedDim = config.Embeddings.Dimensions > 0 ? config.Embeddings.Dimensions : 1024;
                        try
                        {
                            using var generator = new OnnxEmbeddingGenerator(
                                modelPath: modelResolved,
                                tokenizerPath: tokResolved,
                                dimensions: expectedDim);

                            var generated = await generator.GenerateAsync(["Pigmentor embedding health check"], null, cts.Token).ConfigureAwait(false);

                            if (generated.Count == 0 || generated[0].Vector.IsEmpty)
                            {
                                _console.WriteLine("FAILED");
                                _console.WriteLine("    ONNX inference produced an empty embedding vector.");
                                allHealthy = false;
                            }
                            else
                            {
                                var vector = generated[0].Vector.ToArray();
                                if (vector.Length != expectedDim)
                                {
                                    _console.WriteLine("FAILED");
                                    _console.WriteLine($"    Embedding dimension mismatch: expected {expectedDim}, got {vector.Length}.");
                                    allHealthy = false;
                                }
                                else
                                {
                                    var hasNonFinite = false;
                                    var sumSquares = 0.0;
                                    foreach (var val in vector)
                                    {
                                        if (float.IsNaN(val) || float.IsInfinity(val))
                                        {
                                            hasNonFinite = true;
                                            break;
                                        }
                                        sumSquares += val * val;
                                    }

                                    if (hasNonFinite)
                                    {
                                        _console.WriteLine("FAILED");
                                        _console.WriteLine("    Embedding vector contains non-finite values (NaN or Infinity).");
                                        allHealthy = false;
                                    }
                                    else if (sumSquares <= 1e-12)
                                    {
                                        _console.WriteLine("FAILED");
                                        _console.WriteLine("    Embedding vector is an all-zero vector.");
                                        allHealthy = false;
                                    }
                                    else
                                    {
                                        _console.WriteLine($"OK ({vector.Length} dimensions)");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _console.WriteLine("FAILED");
                            if (ex.Message.Contains("model.onnx_data", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("external data", StringComparison.OrdinalIgnoreCase))
                            {
                                _console.WriteLine($"    ONNX model graph requires external weights file 'model.onnx_data', which is missing in '{modelDir}'. Run scripts/download-bge-m3.ps1.");
                            }
                            else
                            {
                                var safeMsg = SecretRedactor.Redact(ex.Message, credentialRes.Key);
                                _console.WriteLine($"    {safeMsg}");
                            }
                            allHealthy = false;
                        }
                    }
                }
            }
        }

        return allHealthy;
    }
}
