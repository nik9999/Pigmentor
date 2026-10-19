namespace Pigmentor.Generation;

/// <summary>
/// Primary application-level contract for the Pigmentor RAG pipeline.
/// </summary>
public interface IRagPipeline
{
    /// <summary>
    /// Executes a retrieval-augmented generation request.
    /// </summary>
    /// <param name="request">The input request containing question, optional filter, conversation id, and top-k.</param>
    /// <param name="cancellationToken">Cancellation token to abort pipeline execution.</param>
    /// <returns>A <see cref="RagResponse"/> containing answer, citations, sources, and diagnostics.</returns>
    Task<RagResponse> AskAsync(
        RagRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience overload executing a RAG request with raw question string and optional top-k limit.
    /// </summary>
    Task<RagResponse> AskAsync(
        string question,
        int? topK = null,
        CancellationToken cancellationToken = default) =>
        AskAsync(new RagRequest(question, topK: topK), cancellationToken);

    /// <summary>
    /// Executes a streaming retrieval-augmented generation request, yielding <see cref="RagTextDelta"/>
    /// events incrementally as generated, terminated by a final <see cref="RagCompleted"/> event.
    /// </summary>
    /// <param name="request">The input request containing question, optional filter, conversation id, and top-k.</param>
    /// <param name="cancellationToken">Cancellation token to abort pipeline execution.</param>
    /// <returns>An async stream of <see cref="RagStreamEvent"/> instances.</returns>
    IAsyncEnumerable<RagStreamEvent> AskStreamingAsync(
        RagRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience overload executing a streaming RAG request with raw question string and optional top-k limit.
    /// </summary>
    IAsyncEnumerable<RagStreamEvent> AskStreamingAsync(
        string question,
        int? topK = null,
        CancellationToken cancellationToken = default) =>
        AskStreamingAsync(new RagRequest(question, topK: topK), cancellationToken);
}
