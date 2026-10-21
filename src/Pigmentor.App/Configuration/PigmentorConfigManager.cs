using System.Text.Json;
using Pigmentor.App.Paths;

namespace Pigmentor.App.Configuration;

/// <summary>
/// Manages loading, saving, setting, and validating non-secret Pigmentor configuration.
/// Enforces zero-credential tolerance and strict schema validation.
/// </summary>
public sealed class PigmentorConfigManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string[] ForbiddenSecretKeywords =
    [
        "key",
        "secret",
        "auth",
        "token",
        "password",
        "credential",
        "authorizationkey",
        "clientsecret",
        "apikey"
    ];

    private static readonly HashSet<string> AllowedTopLevelProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "rootpath",
        "chat",
        "embeddings",
        "gigachat",
        "storage" // Supported for graceful upgrade from older configurations
    };

    private static readonly HashSet<string> AllowedStorageProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "databasepath"
    };

    private static readonly HashSet<string> AllowedChatProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "provider",
        "model",
        "endpoint"
    };

    private static readonly HashSet<string> AllowedEmbeddingsProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "provider",
        "model",
        "endpoint",
        "modelpath",
        "tokenizerpath",
        "dimensions"
    };

    private static readonly HashSet<string> AllowedGigaChatProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "scope",
        "verifyssl"
    };

    private readonly IPigmentorAppPaths _paths;

    public PigmentorConfigManager(IPigmentorAppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public static PigmentorConfig CreateDefaultConfig() => new()
    {
        RootPath = "data",
        Chat = new()
        {
            Provider = "Ollama",
            Model = "gigachat3-32k:latest",
            Endpoint = "http://192.168.215.17:11434"
        },
        Embeddings = new()
        {
            Provider = "Onnx",
            ModelPath = "models/bge-m3/model.onnx",
            TokenizerPath = "models/bge-m3/tokenizer.json",
            Dimensions = 1024
        },
        GigaChat = new()
        {
            Scope = "GIGACHAT_API_PERS",
            VerifySsl = true
        }
    };

    /// <summary>
    /// Loads configuration from disk, validating strict schema and zero-secrets policy.
    /// </summary>
    public async Task<PigmentorConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ConfigFilePath))
        {
            return CreateDefaultConfig();
        }

        var jsonBytes = await File.ReadAllBytesAsync(_paths.ConfigFilePath, cancellationToken).ConfigureAwait(false);
        if (jsonBytes.Length == 0)
        {
            throw new InvalidOperationException("Configuration file is empty.");
        }

        using var doc = JsonDocument.Parse(jsonBytes);
        InspectJsonElementForSecretsAndSchema(doc.RootElement);

        var config = JsonSerializer.Deserialize<PigmentorConfig>(jsonBytes, SerializerOptions)
            ?? throw new InvalidOperationException("Configuration file deserialized to null.");

        // Migration: If loading an older config with 'storage.databasePath', derive rootPath from it and upgrade config file
        if (doc.RootElement.TryGetProperty("storage", out var storageElem) &&
            storageElem.ValueKind == JsonValueKind.Object)
        {
            string? legacyDbPath = null;
            if (storageElem.TryGetProperty("databasePath", out var dbElem) ||
                storageElem.TryGetProperty("databasepath", out dbElem))
            {
                legacyDbPath = dbElem.GetString();
            }

            if (!string.IsNullOrWhiteSpace(legacyDbPath))
            {
                var derivedRoot = Path.GetDirectoryName(legacyDbPath);
                if (!string.IsNullOrWhiteSpace(derivedRoot))
                {
                    config.RootPath = derivedRoot;
                }
            }

            // Save clean config without 'storage'
            await SaveAsync(config, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(config.RootPath))
        {
            config.RootPath = _paths.DefaultRootPath;
        }

        return config;
    }

    /// <summary>
    /// Saves configuration to disk with formatting.
    /// </summary>
    public async Task SaveAsync(PigmentorConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        _paths.EnsureConfigDirectoryCreated();

        var tempPath = _paths.ConfigFilePath + $".tmp.{Guid.NewGuid():N}";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _paths.ConfigFilePath, overwrite: true);
    }

    /// <summary>
    /// Initializes configuration idempotently. If configuration already exists, returns false without overwriting.
    /// </summary>
    public async Task<bool> InitializeAsync(string? explicitRootPath = null, CancellationToken cancellationToken = default)
    {
        _paths.EnsureConfigDirectoryCreated();

        var rootToConfigure = !string.IsNullOrWhiteSpace(explicitRootPath)
            ? explicitRootPath.Trim()
            : (_paths.DefaultRootPath.Equals(Path.Combine(_paths.ApplicationBaseDirectory, "data"), StringComparison.OrdinalIgnoreCase) ? "data" : _paths.DefaultRootPath);

        var effectiveRoot = _paths.ResolveEffectiveRootPath(rootToConfigure);
        _paths.EnsureWorkspaceDirectories(effectiveRoot);

        if (File.Exists(_paths.ConfigFilePath))
        {
            return false;
        }

        var config = CreateDefaultConfig();
        config.RootPath = rootToConfigure;

        await SaveAsync(config, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Checks if a setting key name represents a forbidden secret property.
    /// </summary>
    public static bool IsSecretSettingKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Trim();
        if (normalized.Contains("tokenizer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ForbiddenSecretKeywords.Any(k => normalized.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sets a configuration value using dot notation (e.g. "rootpath", "chat.provider", "gigachat.scope").
    /// Rejects any secret keys.
    /// </summary>
    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var normalizedKey = key.Trim();

        if (IsSecretSettingKey(normalizedKey))
        {
            throw new InvalidOperationException(
                $"Secret settings cannot be stored in configuration: '{normalizedKey}'. Use 'pigmentor auth gigachat' or the 'PIGMENTOR_GIGACHAT_AUTH_KEY' environment variable.");
        }

        if (normalizedKey.Equals("storage.databasepath", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.Equals("databasepath", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "'storage.databasePath' is no longer supported. Pigmentor uses a single workspace 'rootPath' from which the database is automatically derived. Use 'pigmentor config set rootPath <path>'.");
        }

        var config = await LoadAsync(cancellationToken).ConfigureAwait(false);

        switch (normalizedKey.ToLowerInvariant())
        {
            case "rootpath":
                config.RootPath = value.Trim();
                break;

            case "chat.provider":
                config.Chat ??= new ChatConfig();
                config.Chat.Provider = value;
                break;
            case "chat.model":
                config.Chat ??= new ChatConfig();
                config.Chat.Model = value;
                break;
            case "chat.endpoint":
                config.Chat ??= new ChatConfig();
                config.Chat.Endpoint = value;
                break;

            case "embeddings.provider":
                config.Embeddings ??= new EmbeddingsConfig();
                config.Embeddings.Provider = value;
                break;
            case "embeddings.model":
                config.Embeddings ??= new EmbeddingsConfig();
                config.Embeddings.Model = value;
                break;
            case "embeddings.endpoint":
                config.Embeddings ??= new EmbeddingsConfig();
                config.Embeddings.Endpoint = value;
                break;
            case "embeddings.modelpath":
                config.Embeddings ??= new EmbeddingsConfig();
                config.Embeddings.ModelPath = value;
                break;
            case "embeddings.tokenizerpath":
                config.Embeddings ??= new EmbeddingsConfig();
                config.Embeddings.TokenizerPath = value;
                break;
            case "embeddings.dimensions":
                if (!int.TryParse(value, out var dims) || dims <= 0)
                {
                    throw new ArgumentException("embeddings.dimensions must be a positive integer.", nameof(value));
                }
                config.Embeddings ??= new EmbeddingsConfig();
                config.Embeddings.Dimensions = dims;
                break;

            case "gigachat.scope":
                config.GigaChat ??= new GigaChatConfig();
                config.GigaChat.Scope = value;
                break;
            case "gigachat.verifyssl":
                if (!bool.TryParse(value, out var verifySsl))
                {
                    throw new ArgumentException("gigachat.verifyssl must be 'true' or 'false'.", nameof(value));
                }
                config.GigaChat ??= new GigaChatConfig();
                config.GigaChat.VerifySsl = verifySsl;
                break;

            default:
                throw new ArgumentException($"Unknown configuration setting: '{key}'.", nameof(key));
        }

        await SaveAsync(config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the configuration model and returns actionable error messages.
    /// Application-relative paths are resolved via <paramref name="paths"/> relative to AppContext.BaseDirectory.
    /// </summary>
    public static List<string> Validate(PigmentorConfig config, IPigmentorAppPaths? paths = null, bool validateFilesExist = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        var resolver = paths ?? new PigmentorAppPaths();
        var errors = new List<string>();

        // Workspace RootPath
        if (string.IsNullOrWhiteSpace(config.RootPath))
        {
            errors.Add("rootPath cannot be empty. Specify a valid workspace directory path.");
        }
        else
        {
            try
            {
                var fullRoot = resolver.ResolveEffectiveRootPath(config.RootPath);
                if (File.Exists(fullRoot))
                {
                    errors.Add($"rootPath '{config.RootPath}' points to an existing file, but a workspace directory is required.");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"rootPath '{config.RootPath}' is an invalid directory path: {ex.Message}");
            }
        }

        // Chat
        if (config.Chat == null)
        {
            errors.Add("The 'chat' configuration section cannot be null.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(config.Chat.Provider))
            {
                errors.Add("Chat provider cannot be empty (supported: 'GigaChat', 'Ollama').");
            }
            else if (!string.Equals(config.Chat.Provider, "GigaChat", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(config.Chat.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Unsupported chat provider '{config.Chat.Provider}'. Supported providers: 'GigaChat', 'Ollama'.");
            }

            if (string.IsNullOrWhiteSpace(config.Chat.Model))
            {
                errors.Add("Chat model identifier cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(config.Chat.Endpoint) ||
                !Uri.TryCreate(config.Chat.Endpoint, UriKind.Absolute, out var chatUri) ||
                (chatUri.Scheme != Uri.UriSchemeHttp && chatUri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add($"Chat endpoint '{config.Chat.Endpoint}' is not a valid absolute HTTP or HTTPS URI.");
            }
        }

        // Embeddings
        if (config.Embeddings == null)
        {
            errors.Add("The 'embeddings' configuration section cannot be null.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(config.Embeddings.Provider))
            {
                errors.Add("Embeddings provider cannot be empty (supported: 'Ollama', 'Onnx').");
            }
            else if (!string.Equals(config.Embeddings.Provider, "Ollama", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(config.Embeddings.Provider, "Onnx", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Unsupported embeddings provider '{config.Embeddings.Provider}'. Supported providers: 'Ollama', 'Onnx'.");
            }

            if (string.Equals(config.Embeddings.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(config.Embeddings.Model))
                {
                    errors.Add("Embedding model cannot be empty when provider is 'Ollama'.");
                }

                if (string.IsNullOrWhiteSpace(config.Embeddings.Endpoint) ||
                    !Uri.TryCreate(config.Embeddings.Endpoint, UriKind.Absolute, out var embUri) ||
                    (embUri.Scheme != Uri.UriSchemeHttp && embUri.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add($"Embeddings endpoint '{config.Embeddings.Endpoint}' is not a valid absolute HTTP or HTTPS URI.");
                }
            }

            if (string.Equals(config.Embeddings.Provider, "Onnx", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(config.Embeddings.ModelPath))
                {
                    errors.Add("Embedding modelPath must be specified when provider is 'Onnx'.");
                }
                else if (validateFilesExist)
                {
                    var resolvedModel = resolver.ResolveApplicationPath(config.Embeddings.ModelPath);
                    if (!File.Exists(resolvedModel))
                    {
                        errors.Add($"ONNX model not found:\n  configured: {config.Embeddings.ModelPath}\n  resolved:   {resolvedModel}");
                    }
                }

                if (string.IsNullOrWhiteSpace(config.Embeddings.TokenizerPath))
                {
                    errors.Add("Embedding tokenizerPath must be specified when provider is 'Onnx'.");
                }
                else if (validateFilesExist)
                {
                    var resolvedTokenizer = resolver.ResolveApplicationPath(config.Embeddings.TokenizerPath);
                    if (!File.Exists(resolvedTokenizer))
                    {
                        errors.Add($"ONNX tokenizer not found:\n  configured: {config.Embeddings.TokenizerPath}\n  resolved:   {resolvedTokenizer}");
                    }
                }
            }

            if (config.Embeddings.Dimensions <= 0)
            {
                errors.Add("Embedding dimensions must be a positive integer.");
            }
        }

        // GigaChat
        if (config.GigaChat == null)
        {
            errors.Add("The 'gigachat' configuration section cannot be null.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(config.GigaChat.Scope))
            {
                errors.Add("GigaChat scope cannot be empty (e.g. 'GIGACHAT_API_PERS' or 'GIGACHAT_API_CORP').");
            }
        }

        return errors;
    }

    private static void InspectJsonElementForSecretsAndSchema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Configuration root must be a JSON object.");
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (IsSecretSettingKey(prop.Name))
            {
                throw new InvalidOperationException(
                    $"Configuration file contains forbidden secret property '{prop.Name}'. Secret settings must NEVER be saved in configuration files. Use 'pigmentor auth gigachat' or the 'PIGMENTOR_GIGACHAT_AUTH_KEY' environment variable.");
            }

            if (!AllowedTopLevelProperties.Contains(prop.Name))
            {
                throw new InvalidOperationException($"Configuration file contains unknown property '{prop.Name}'.");
            }

            // Inspect nested sections
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var allowedNested = prop.Name.ToLowerInvariant() switch
                {
                    "chat" => AllowedChatProperties,
                    "embeddings" => AllowedEmbeddingsProperties,
                    "gigachat" => AllowedGigaChatProperties,
                    "storage" => AllowedStorageProperties,
                    _ => null
                };

                foreach (var child in prop.Value.EnumerateObject())
                {
                    if (IsSecretSettingKey(child.Name))
                    {
                        throw new InvalidOperationException(
                            $"Configuration file contains forbidden secret property '{prop.Name}.{child.Name}'. Secret settings must NEVER be saved in configuration files. Use 'pigmentor auth gigachat' or the 'PIGMENTOR_GIGACHAT_AUTH_KEY' environment variable.");
                    }

                    if (allowedNested != null && !allowedNested.Contains(child.Name))
                    {
                        throw new InvalidOperationException($"Configuration file contains unknown property '{prop.Name}.{child.Name}'.");
                    }
                }
            }
        }
    }
}
