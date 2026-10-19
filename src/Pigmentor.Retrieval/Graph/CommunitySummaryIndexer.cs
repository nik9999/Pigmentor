using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Graph;

/// <summary>
/// Persists eager-GraphRAG community summaries into an <see cref="IVectorStore"/>
/// so they become first-class retrieval targets. Each
/// <see cref="GraphRagPipeline.CommunitySummary"/> is embedded and upserted as a
/// synthetic <see cref="DocumentChunk"/>; a normal vector query then ranks the
/// community summaries alongside (or instead of) raw chunks — this is how
/// GraphRAG answers global "what are the themes?" questions that no single chunk
/// contains.
/// </summary>
public sealed class CommunitySummaryIndexer
{
    private const string CommunityDocumentId = "graph-community";

    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _store;

    /// <summary>Create the indexer over an embedding service and a vector store.</summary>
    /// <param name="embeddings">Embeds each community summary for vector search.</param>
    /// <param name="store">The destination vector store the summaries are upserted into.</param>
    public CommunitySummaryIndexer(IEmbeddingService embeddings, IVectorStore store)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(store);
        _embeddings = embeddings;
        _store = store;
    }

    /// <summary>
    /// Embed and upsert each summary as a synthetic chunk
    /// (<c>ChunkId = community/{CommunityId}#0</c>,
    /// <c>DocumentId = graph-community</c>, <c>DocumentType = "GraphSummary"</c>).
    /// The summary text is augmented with the member entity types and names so
    /// the embedding — and any later citation — carries the community's roster.
    /// </summary>
    public async Task IndexSummariesAsync(
        IReadOnlyList<GraphRagPipeline.CommunitySummary> summaries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        foreach (var summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var memberLine = string.Join(", ", summary.Members.Select(m => $"{m.Type}: {m.Name}"));
            var text = string.IsNullOrEmpty(memberLine)
                ? summary.Summary
                : $"{summary.Summary}\n\nMembers: {memberLine}";

            var meta = new DocumentMetadata(
                Id: summary.CommunityId,
                Silo: "graph",
                Department: "All",
                Office: "All",
                ConfidentialityLevel: "Internal",
                DocumentType: "GraphSummary",
                FiscalYear: 2026,
                Author: "graph-rag",
                LastModified: new DateOnly(2026, 1, 1),
                Title: $"Community {summary.CommunityId}");

            var chunk = new DocumentChunk(
                ChunkId: $"community/{summary.CommunityId}#0",
                DocumentId: CommunityDocumentId,
                ChunkIndex: 0,
                Text: text,
                StartCharOffset: 0,
                EndCharOffset: text.Length,
                Metadata: meta);

            var embedded = await _embeddings.EmbedAsync(chunk, cancellationToken).ConfigureAwait(false);
            await _store.UpsertAsync([embedded], cancellationToken).ConfigureAwait(false);
        }
    }
}
