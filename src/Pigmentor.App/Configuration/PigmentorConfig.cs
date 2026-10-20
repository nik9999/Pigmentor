using System.Text.Json.Serialization;

namespace Pigmentor.App.Configuration;

/// <summary>
/// Root user-level configuration model for Pigmentor applications.
/// STRICTLY NON-SECRET: Credentials must NEVER be stored here.
/// </summary>
public sealed class PigmentorConfig
{
    [JsonPropertyName("chat")]
    public ChatConfig Chat { get; set; } = new();

    [JsonPropertyName("embeddings")]
    public EmbeddingsConfig Embeddings { get; set; } = new();

    [JsonPropertyName("gigachat")]
    public GigaChatConfig GigaChat { get; set; } = new();

    [JsonPropertyName("storage")]
    public StorageConfig Storage { get; set; } = new();
}

public sealed class ChatConfig
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "GigaChat";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "GigaChat";

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "https://gigachat.devices.sberbank.ru/api/v1";
}

public sealed class EmbeddingsConfig
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "Ollama";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "nomic-embed-text";

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "http://localhost:11434";

    [JsonPropertyName("modelPath")]
    public string? ModelPath { get; set; }

    [JsonPropertyName("dimensions")]
    public int Dimensions { get; set; } = 768;
}

public sealed class GigaChatConfig
{
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "GIGACHAT_API_PERS";

    [JsonPropertyName("verifySsl")]
    public bool VerifySsl { get; set; } = true;
}

public sealed class StorageConfig
{
    [JsonPropertyName("databasePath")]
    public string? DatabasePath { get; set; }
}

