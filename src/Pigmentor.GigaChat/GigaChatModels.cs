namespace Pigmentor.GigaChat;

/// <summary>
/// Well-known model identifiers supported by the Sber GigaChat API.
/// </summary>
public static class GigaChatModels
{
    /// <summary>
    /// Default versatile chat model suitable for standard conversational tasks.
    /// </summary>
    public const string GigaChat = "GigaChat";

    /// <summary>
    /// Enhanced model with extended context window and reasoning capabilities.
    /// </summary>
    public const string GigaChatPlus = "GigaChat-Plus";

    /// <summary>
    /// High-performance professional model for complex analysis and multi-step reasoning.
    /// </summary>
    public const string GigaChatPro = "GigaChat-Pro";

    /// <summary>
    /// Flagship maximum capability model offering state-of-the-art Russian and English generation.
    /// </summary>
    public const string GigaChatMax = "GigaChat-Max";

    /// <summary>
    /// GigaChat 2 Lite generation model.
    /// </summary>
    public const string GigaChat2 = "GigaChat-2-Lite";

    /// <summary>
    /// Alias for <see cref="GigaChat2"/>.
    /// </summary>
    public const string GigaChat2Lite = "GigaChat-2-Lite";

    /// <summary>
    /// GigaChat 2.0 Pro generation model.
    /// </summary>
    public const string GigaChat2Pro = "GigaChat-2-Pro";

    /// <summary>
    /// GigaChat 2.0 Max flagship generation model.
    /// </summary>
    public const string GigaChat2Max = "GigaChat-2-Max";

    /// <summary>GigaChat 3 Ultra generation model.</summary>
    public const string GigaChat3Ultra = "GigaChat-3-Ultra";

    /// <summary>
    /// Standard dense text embeddings model.
    /// </summary>
    public const string Embeddings = "Embeddings";

    /// <summary>
    /// Second-generation dense text embeddings model.
    /// </summary>
    public const string Embeddings2 = "Embeddings-2";

    /// <summary>
    /// Retrieval-optimized dense embeddings model for RAG pipelines.
    /// </summary>
    public const string EmbeddingsGigaR = "EmbeddingsGigaR";

    /// <summary>Embeddings 3B model released in September 2025.</summary>
    public const string Embeddings3B202509 = "Embeddings-3B-2025-09";
}
