using System.ComponentModel.DataAnnotations;

namespace Pigmentor.Core.Configuration;

/// <summary>The set of chat providers Pigmentor is wired to talk to.</summary>
public enum LlmProvider
{
    /// <summary>
    /// Local Ollama via <c>OllamaSharp.OllamaApiClient</c>. Default for
    /// development; works offline and incurs no API cost.
    /// </summary>
    Ollama,

    /// <summary>
    /// Sber GigaChat through the Pigmentor.GigaChat REST adapter.
    /// </summary>
    GigaChat,
}

/// <summary>The set of embedding backends Pigmentor can use.</summary>
public enum EmbeddingProvider
{
    /// <summary>Embedding model served by a local Ollama instance.</summary>
    Ollama,

    /// <summary>Local ONNX embedding model loaded from disk.</summary>
    Onnx
}

/// <summary>
/// Configuration for the chat client Pigmentor registers
/// in DI. Bound from the <c>Pigmentor:Llm</c> section of
/// <c>appsettings.json</c> by
/// <see cref="DependencyInjection.ServiceCollectionExtensions.AddPigmentorCore"/>.
/// </summary>
public sealed class LlmClientOptions
{
    /// <summary>Configuration section path bound to this class.</summary>
    public const string SectionName = "Pigmentor:Llm";

    /// <summary>Which provider to wire up.</summary>
    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    /// <summary>
    /// Provider endpoint. For Ollama use <c>http://localhost:11434</c>; for
    /// GigaChat use <c>https://api.giga.chat/v1</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// GigaChat authorization key (Base64 Client ID:Client Secret). It is
    /// ignored for Ollama. Read from environment variables or user-secrets,
    /// never committed to source control. This is not an API key.
    /// </summary>
    public string? AuthorizationKey { get; set; }

    /// <summary>
    /// Chat model identifier. Ollama uses a local tag such as <c>llama3.2</c>;
    /// GigaChat uses an API model ID such as <c>GigaChat-2-Lite</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ChatModel { get; set; } = "llama3.2";

    /// <summary>
}

/// <summary>
/// Configuration for the independent embedding backend. Bound from the
/// <c>Pigmentor:Embedding</c> section.
/// </summary>
public sealed class EmbeddingClientOptions
{
    public const string SectionName = "Pigmentor:Embedding";

    /// <summary>Embedding backend to register.</summary>
    public EmbeddingProvider Provider { get; set; } = EmbeddingProvider.Ollama;

    /// <summary>Ollama endpoint. Ignored for ONNX.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Ollama model name. Ignored for ONNX.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Model { get; set; } = "nomic-embed-text";

    /// <summary>
    /// Path to the local ONNX embedding model. For BGE-M3 this is typically a
    /// converted <c>model.onnx</c> from the BAAI/bge-m3 model bundle.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// Path to the tokenizer vocabulary/configuration used by the ONNX model.
    /// BGE-M3 requires the tokenizer bundle in addition to model.onnx.
    /// </summary>
    public string? TokenizerPath { get; set; }

    /// <summary>Expected vector dimensions for the ONNX model, when known.</summary>
    public int Dimensions { get; set; }
}
