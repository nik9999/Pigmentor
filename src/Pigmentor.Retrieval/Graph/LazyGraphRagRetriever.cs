using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Graph;

/// <summary>
/// LazyGraphRAG (Microsoft Research, November 2024) — defers all summarization
/// to query time. At index time we only build the bare graph (entities +
/// relations + chunk-text mapping). At query time we:
///   1. extract query entities
///   2. traverse the graph to gather a candidate subgraph
///   3. ask the chat model to summarize just that subgraph
///
/// Result: ~0.1% of full-GraphRAG indexing cost; query cost rises a bit
/// because summarization is now per-query. Supplying an
/// <see cref="ISummaryCache"/> makes that per-query cost real but bounded:
/// queries that resolve to a subgraph already summarized reuse the cached
/// summary and skip the LLM call entirely.
/// </summary>
public sealed class LazyGraphRagRetriever : IRetriever
{
    private readonly EntityExtractor _extractor;
    private readonly IGraphStore _graph;
    private readonly IChatClient _chat;
    private readonly ISummaryCache? _cache;
    public int MaxHops { get; }

    /// <summary>Create the retriever.</summary>
    /// <param name="extractor">Extracts the query's seed entities.</param>
    /// <param name="graph">The graph store traversed to assemble the subgraph.</param>
    /// <param name="chat">The chat client used to summarize the subgraph at query time.</param>
    /// <param name="maxHops">Traversal radius from the seed entities (default 2).</param>
    /// <param name="cache">
    /// Optional per-subgraph summary cache. When supplied, an identical subgraph
    /// served a second time reuses the cached summary and skips the LLM call;
    /// when <see langword="null"/>, every query is summarized afresh.
    /// </param>
    public LazyGraphRagRetriever(
        EntityExtractor extractor,
        IGraphStore graph,
        IChatClient chat,
        int maxHops = 2,
        ISummaryCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHops);
        _extractor = extractor;
        _graph = graph;
        _chat = chat;
        MaxHops = maxHops;
        _cache = cache;
    }

    public string Strategy => "lazy-graph-rag";

    public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Filter is not null)
        {
            throw new NotSupportedException($"Retriever '{Strategy}' does not support metadata filtering.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TopK);

        var extraction = await _extractor.ExtractAsync(request.Query, cancellationToken).ConfigureAwait(false);
        var names = extraction.Entities.Select(e => e.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            return Array.Empty<RetrievalResult>();
        }

        var subgraph = await _graph.TraverseAsync(names, MaxHops, cancellationToken).ConfigureAwait(false);
        if (subgraph.Count == 0)
        {
            return Array.Empty<RetrievalResult>();
        }

        // The cache key is the set of subgraph entity ids — two different
        // natural-language queries that traverse to the same entities share one
        // summary. On a hit we skip the (expensive) summarization LLM call.
        var subgraphIds = subgraph.Select(e => e.Id).ToList();
        if (_cache is not null)
        {
            var cached = await _cache.TryGetAsync(subgraphIds, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return [BuildResult(cached)];
            }
        }

        // Per-query summarization (the "Lazy" part — no precomputed summaries).
        var subgraphText = string.Join("\n",
            subgraph.Select(e => $"- {e.Type}: {e.Name}" +
                (e.Properties.Count == 0 ? "" : " (" + string.Join("; ", e.Properties.Select(p => $"{p.Key}={p.Value}")) + ")")));

        var prompt =
            $"На основе следующего подграфа составь один краткий абзац на русском языке, " +
            $"содержащий ключевую информацию для ответа на вопрос. Сохраняй исходные технические термины и идентификаторы без перевода. " +
            $"Ответь ТОЛЬКО текстом сводки, без вводных фраз.\n\n" +
            $"Вопрос: {request.Query}\n\nПодграф:\n{subgraphText}";

        var response = await _chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var summary = (response.Text ?? string.Empty).Trim();

        if (_cache is not null)
        {
            await _cache.SetAsync(subgraphIds, summary, cancellationToken).ConfigureAwait(false);
        }

        return [BuildResult(summary)];
    }

    private static RetrievalResult BuildResult(string summary)
    {
        var meta = new DocumentMetadata("lazy-graph", "graph", "Graph", "All",
            "Internal", "GraphSummary", 2026, "lazy-graph-rag",
            new DateOnly(2026, 1, 1), "LazyGraphRAG subgraph summary");
        var chunk = new DocumentChunk("lazy-graph#0", "lazy-graph", 0, summary, 0, summary.Length, meta);
        return new RetrievalResult(chunk, 1.0);
    }
}
