using System.Text.Json.Serialization;

namespace Pigmentor.App.Configuration;

/// <summary>
/// Root user-level configuration model for Pigmentor applications.
/// STRICTLY NON-SECRET: Credentials must NEVER be stored here.
/// </summary>
public sealed class PigmentorConfig
{
    [JsonPropertyName("rootPath")]
    public string? RootPath { get; set; }

    [JsonPropertyName("chat")]
    public ChatConfig? Chat { get; set; } = new();

    [JsonPropertyName("embeddings")]
    public EmbeddingsConfig? Embeddings { get; set; } = new();

    [JsonPropertyName("gigachat")]
    public GigaChatConfig? GigaChat { get; set; } = new();

    [JsonPropertyName("reranker")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RerankerConfig? Reranker { get; set; } = new();

    [JsonPropertyName("retrieval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RetrievalConfig? Retrieval { get; set; } = new();
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelPath { get; set; }

    [JsonPropertyName("tokenizerPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenizerPath { get; set; }

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

public sealed class RerankerConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "Onnx";

    [JsonPropertyName("modelPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelPath { get; set; } = "models/bge-reranker-v2-m3/model.onnx";

    [JsonPropertyName("tokenizerPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenizerPath { get; set; } = "models/bge-reranker-v2-m3/tokenizer.json";

    [JsonPropertyName("maxPairsPerBatch")]
    public int MaxPairsPerBatch { get; set; } = 16;

    [JsonPropertyName("maxSequenceLength")]
    public int MaxSequenceLength { get; set; } = 512;

    [JsonPropertyName("minScore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinScore { get; set; }
}

public sealed class RetrievalConfig
{
    [JsonPropertyName("candidatePoolPerLeg")]
    public int CandidatePoolPerLeg { get; set; } = 50;

    [JsonPropertyName("rrfK")]
    public int RrfK { get; set; } = 60;

    [JsonPropertyName("denseWeight")]
    public double DenseWeight { get; set; } = 1.0;

    [JsonPropertyName("sparseWeight")]
    public double SparseWeight { get; set; } = 1.0;

    [JsonPropertyName("legTimeoutSeconds")]
    public double LegTimeoutSeconds { get; set; } = 10.0;
}
