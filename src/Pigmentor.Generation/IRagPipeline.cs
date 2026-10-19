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
}

