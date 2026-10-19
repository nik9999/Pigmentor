namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Per-model task-instruction prefixes. Defaults to <see cref="None"/>, which is
/// correct for OpenAI / Azure OpenAI text-embedding-3 models (trained without
/// task prefixes). Models hosted via Ollama do NOT add prefixes for you — you
/// must supply them here.
/// </summary>
public sealed record EmbeddingPrompt(string DocumentPrefix = "", string QueryPrefix = "")
{
    /// <summary>No prefix. Use for OpenAI / Azure OpenAI text-embedding-3-*.</summary>
    public static readonly EmbeddingPrompt None = new();

    /// <summary>nomic-embed-text: both sides prefixed, different prefixes.</summary>
    public static readonly EmbeddingPrompt Nomic = new(
        DocumentPrefix: "search_document: ",
        QueryPrefix: "search_query: ");

    /// <summary>mxbai-embed-large: query gets a prompt, documents do not (asymmetric).</summary>
    public static readonly EmbeddingPrompt Mxbai = new(
        DocumentPrefix: "",
        QueryPrefix: "Represent this sentence for searching relevant passages: ");

    /// <summary>Prepend the correct prefix for <paramref name="task"/>.</summary>
    public string Apply(string text, EmbeddingTaskType task) => task switch
    {
        EmbeddingTaskType.Document => DocumentPrefix + text,
        EmbeddingTaskType.Query => QueryPrefix + text,
        _ => text,
    };
}
