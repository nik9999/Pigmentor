namespace Pigmentor.Core.Abstractions;

/// <summary>
/// What an embedding will be used for. Many models (nomic-embed-text, BGE, E5,
/// mxbai-embed-large) require a task-specific instruction prefix; embedding a
/// query with the document scheme (or no scheme at all) degrades retrieval.
/// </summary>
public enum EmbeddingTaskType
{
    /// <summary>Text stored in the index (the corpus side of retrieval).</summary>
    Document,

    /// <summary>A user query embedded at search time.</summary>
    Query,
}
