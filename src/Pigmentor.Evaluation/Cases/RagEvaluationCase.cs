using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;

namespace Pigmentor.Evaluation.Cases;

/// <summary>
/// A deterministic evaluation case for the Pigmentor RAG pipeline.
/// Specifies expected retrieval relevance, factual answers, and technical terminology.
/// </summary>
public sealed record RagEvaluationCase(
    string Id,
    string Question,
    IReadOnlyList<string> RelevantDocumentIds,
    IReadOnlyList<string>? RelevantChunkIds = null,
    IReadOnlyList<string>? ExpectedFacts = null,
    IReadOnlyList<string>? ExpectedTechnicalTerms = null,
    MetadataFilter? MetadataFilter = null,
    bool ExpectInsufficientContext = false,
    string? Category = null);
