using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;

namespace Pigmentor.Retrieval.Vectorless;

/// <summary>
/// The typed query a structural retriever extracts from free text: either an
/// <see cref="ExplicitId"/> the user named (e.g. <c>GDPR/Art17</c>) or a
/// <see cref="Topic"/> when no identifier is present.
/// </summary>
/// <param name="ExplicitId">A structural identifier the user named, or <see langword="null"/>.</param>
/// <param name="Topic">A short topic summary when no identifier is present, or <see langword="null"/>.</param>
public sealed record StructuralQuery(
    [property: JsonPropertyName("explicitId")] string? ExplicitId,
    [property: JsonPropertyName("topic")] string? Topic);

/// <summary>
/// Deterministic structural retriever. Where vector retrieval is approximate,
/// structural retrieval is <em>exact</em>: an explicit identifier resolves to a
/// node by O(1) dictionary lookup, and explicit cross-references are followed by
/// a bounded breadth-first walk. It exposes the chapter's three low-level shapes —
/// <see cref="Lookup(string)"/>, tree traversal
/// (<see cref="Ancestors(string)"/> / <see cref="Descendants(string)"/> /
/// <see cref="Siblings(string)"/>), and cross-reference following
/// (<see cref="WithCrossReferences(string, int)"/>) — and adapts to the repo's
/// <see cref="IRetriever"/> contract. An <see cref="IChatClient"/> is used only to
/// extract the identifier from a question; identifier lookups never call the model.
/// Topic queries (no identifier) delegate to an optional <c>topicFallback</c>
/// retriever (vector, keyword, or the LLM section router).
/// </summary>
public sealed class StructuralRetriever : IRetriever
{
    private readonly StructuralIndex _index;
    private readonly IChatClient _chat;
    private readonly IRetriever? _topicFallback;

    /// <summary>Create the retriever.</summary>
    /// <param name="index">The structural index to resolve ids against.</param>
    /// <param name="chat">Chat client used only for identifier extraction.</param>
    /// <param name="topicFallback">
    /// Optional retriever for topic queries that name no identifier (e.g. a vector
    /// or keyword retriever, or the LLM section router). When <see langword="null"/>,
    /// topic queries return an empty result.
    /// </param>
    public StructuralRetriever(StructuralIndex index, IChatClient chat, IRetriever? topicFallback = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(chat);
        _index = index;
        _chat = chat;
        _topicFallback = topicFallback;
    }

    /// <inheritdoc />
    public string Strategy => "structural";

    // --- (1) direct lookup -------------------------------------------------

    /// <summary>Resolve a node by its stable id in O(1), or <see langword="null"/>.</summary>
    public StructuralNode? Lookup(string id) => _index.Lookup(id);

    // --- (2) tree traversal ------------------------------------------------

    /// <summary>The node's descendants in breadth-first order (walking <see cref="StructuralNode.ChildIds"/>).</summary>
    public IReadOnlyList<StructuralNode> Descendants(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var results = new List<StructuralNode>();
        var queue = new Queue<string>();
        if (Lookup(id) is { } start)
        {
            foreach (var childId in start.ChildIds)
            {
                queue.Enqueue(childId);
            }
        }
        while (queue.TryDequeue(out var currentId))
        {
            if (Lookup(currentId) is not { } node)
            {
                continue;
            }
            results.Add(node);
            foreach (var childId in node.ChildIds)
            {
                queue.Enqueue(childId);
            }
        }
        return results;
    }

    /// <summary>The node's ancestors, nearest first (walking <see cref="StructuralNode.ParentId"/> to the root).</summary>
    public IReadOnlyList<StructuralNode> Ancestors(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var results = new List<StructuralNode>();
        var current = Lookup(id)?.ParentId;
        while (current is not null && Lookup(current) is { } node)
        {
            results.Add(node);
            current = node.ParentId;
        }
        return results;
    }

    /// <summary>The node's siblings (its parent's children, minus the node itself).</summary>
    public IReadOnlyList<StructuralNode> Siblings(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (Lookup(id) is not { ParentId: { } parentId } || Lookup(parentId) is not { } parent)
        {
            return [];
        }
        return [.. parent.ChildIds
            .Where(childId => !string.Equals(childId, id, StringComparison.Ordinal))
            .Select(Lookup)
            .Where(n => n is not null)
            .Select(n => n!)];
    }

    // --- (3) cross-reference following (BFS, bounded depth) ----------------

    /// <summary>
    /// The closure of <paramref name="id"/> and the nodes it cites, expanded
    /// breadth-first up to <paramref name="depth"/> hops. The seed node is always
    /// returned first; cycles are pruned by a visited set.
    /// </summary>
    public IReadOnlyList<StructuralNode> WithCrossReferences(string id, int depth = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegative(depth);

        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((id, 0));
        var results = new List<StructuralNode>();
        while (queue.TryDequeue(out var item))
        {
            var (currentId, currentDepth) = item;
            if (Lookup(currentId) is not { } node)
            {
                continue;
            }
            results.Add(node);
            if (currentDepth < depth)
            {
                foreach (var refId in node.CrossReferences)
                {
                    if (seen.Add(refId))
                    {
                        queue.Enqueue((refId, currentDepth + 1));
                    }
                }
            }
        }
        return results;
    }

    // --- IRetriever adapter: identifier extraction → lookup, else fallback --

    /// <inheritdoc />
    public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TopK);

        var explicitId = await ExtractIdentifierAsync(request.Query, cancellationToken).ConfigureAwait(false);
        if (explicitId is not null && WithCrossReferences(explicitId, depth: 1) is { Count: > 0 } nodes)
        {
            return ToResults(nodes, request.TopK, _index.Root.Title, request.Filter);
        }

        if (_topicFallback is not null)
        {
            return await _topicFallback.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return [];
    }

    private async Task<string?> ExtractIdentifierAsync(string query, CancellationToken cancellationToken)
    {
        var prompt =
            "Extract a structural identifier (e.g. \"GDPR/Art17\", \"Art17\", \"§17\") if the user named " +
            "one; otherwise summarize the topic. Reply as JSON: " +
            "{ \"explicitId\": string|null, \"topic\": string|null }.\n\n" +
            $"Question: {query}";

        try
        {
            // useJsonSchemaResponseFormat: false keeps this working with local /
            // Ollama models that lack native JSON-schema response support; M.E.AI
            // then steers the model with a prompt-appended schema. Mirrors Ch 15's
            // SelfRagDecider.
            var typed = await _chat.GetResponseAsync<StructuralQuery>(
                prompt,
                useJsonSchemaResponseFormat: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // TryGetResult (NOT .Result, which throws on a failed parse).
            if (typed.TryGetResult(out var sq) &&
                sq?.ExplicitId is { } id &&
                !string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // Structured output unavailable or unparseable — treat as a topic query.
        }
        return null;
    }

    private static List<RetrievalResult> ToResults(
        IReadOnlyList<StructuralNode> nodes,
        int topK,
        string documentTitle,
        MetadataFilter? filter = null)
    {
        var results = new List<RetrievalResult>(nodes.Count);
        int rank = 0;
        foreach (var n in nodes)
        {
            var meta = new DocumentMetadata(
                n.Id, "structural", "Structural", "All",
                "Internal", "Section", 2026, "structural",
                new DateOnly(2026, 1, 1), documentTitle);

            if (filter is not null && !filter.Matches(meta))
            {
                continue;
            }

            var text = $"{n.Path}\n\n{n.Text}";
            var chunk = new DocumentChunk(n.Id, "structural", rank, text, 0, text.Length, meta);
            results.Add(new RetrievalResult(chunk, 1.0 / (rank + 1)));
            rank++;
            if (results.Count >= topK)
            {
                break;
            }
        }
        return results;
    }
}
