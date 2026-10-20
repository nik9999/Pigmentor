using System.Text.Json;
using Pigmentor.App.Paths;

namespace Pigmentor.App.Configuration;

/// <summary>
/// Manages loading, saving, setting, and validating non-secret Pigmentor configuration.
/// Enforces zero-credential tolerance in configuration files.
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
        "credential"
    ];

    private readonly IPigmentorAppPaths _paths;

    public PigmentorConfigManager(IPigmentorAppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// Loads configuration from disk, creating default configuration if missing.
    /// </summary>
    public async Task<PigmentorConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ConfigFilePath))
        {
            var defaultConfig = new PigmentorConfig
            {
                Storage = { DatabasePath = _paths.DefaultDatabasePath }
            };
            return defaultConfig;
        }

        using var stream = File.OpenRead(_paths.ConfigFilePath);
        var config = await JsonSerializer.DeserializeAsync<PigmentorConfig>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        config ??= new PigmentorConfig();

        if (string.IsNullOrWhiteSpace(config.Storage.DatabasePath))
        {
            config.Storage.DatabasePath = _paths.DefaultDatabasePath;
        }

        return config;
    }

    /// <summary>
    /// Saves configuration to disk.
    /// </summary>
    public async Task SaveAsync(PigmentorConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        _paths.EnsureDirectoriesCreated();

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
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectoriesCreated();

        if (File.Exists(_paths.ConfigFilePath))
        {
            return false;
        }

        var config = new PigmentorConfig
        {
            Storage = { DatabasePath = _paths.DefaultDatabasePath }
        };

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

        return ForbiddenSecretKeywords.Any(k => key.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sets a configuration value using dot notation (e.g. "chat.provider", "gigachat.scope").
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

        var config = await LoadAsync(cancellationToken).ConfigureAwait(false);

        switch (normalizedKey.ToLowerInvariant())
        {
            case "chat.provider":
                config.Chat.Provider = value;
                break;
            case "chat.model":
                config.Chat.Model = value;
                break;
            case "chat.endpoint":
                config.Chat.Endpoint = value;
                break;

            case "embeddings.provider":
                config.Embeddings.Provider = value;
                break;
            case "embeddings.model":
                config.Embeddings.Model = value;
                break;
            case "embeddings.endpoint":
                config.Embeddings.Endpoint = value;
                break;
            case "embeddings.modelpath":
                config.Embeddings.ModelPath = value;
                break;
            case "embeddings.dimensions":
                if (!int.TryParse(value, out var dims) || dims <= 0)
                {
                    throw new ArgumentException("embeddings.dimensions must be a positive integer.", nameof(value));
                }
                config.Embeddings.Dimensions = dims;
                break;

            case "gigachat.scope":
                config.GigaChat.Scope = value;
                break;
            case "gigachat.verifyssl":
                if (!bool.TryParse(value, out var verifySsl))
                {
                    throw new ArgumentException("gigachat.verifyssl must be 'true' or 'false'.", nameof(value));
                }
                config.GigaChat.VerifySsl = verifySsl;
                break;

            case "storage.databasepath":
                config.Storage.DatabasePath = value;
                break;

            default:
                throw new ArgumentException($"Unknown configuration setting: '{key}'.", nameof(key));
        }

        await SaveAsync(config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the configuration model and returns actionable error messages if invalid.
    /// </summary>
    public static List<string> Validate(PigmentorConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();

        // Chat
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

        // Embeddings
        if (string.IsNullOrWhiteSpace(config.Embeddings.Provider))
        {
            errors.Add("Embeddings provider cannot be empty (supported: 'Ollama', 'Onnx').");
        }
        else if (!string.Equals(config.Embeddings.Provider, "Ollama", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(config.Embeddings.Provider, "Onnx", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Unsupported embeddings provider '{config.Embeddings.Provider}'. Supported providers: 'Ollama', 'Onnx'.");
        }

        if (string.Equals(config.Embeddings.Provider, "Ollama", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(config.Embeddings.Model))
        {
            errors.Add("Embedding model cannot be empty when provider is 'Ollama'.");
        }

        if (string.Equals(config.Embeddings.Provider, "Onnx", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(config.Embeddings.ModelPath))
        {
            errors.Add("Embedding modelPath must be specified when provider is 'Onnx'.");
        }

        if (config.Embeddings.Dimensions <= 0)
        {
            errors.Add("Embedding dimensions must be a positive integer.");
        }

        // GigaChat scope
        if (string.IsNullOrWhiteSpace(config.GigaChat.Scope))
        {
            errors.Add("GigaChat scope cannot be empty (e.g. 'GIGACHAT_API_PERS' or 'GIGACHAT_API_CORP').");
        }

        // Storage
        if (string.IsNullOrWhiteSpace(config.Storage.DatabasePath))
        {
            errors.Add("Storage databasePath cannot be empty.");
        }

        return errors;
    }
}

