using Microsoft.Extensions.AI;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Graph;

/// <summary>
/// Microsoft GraphRAG 1.0-style indexer (the eager variant).
/// At index time:
///   1. extract entities + relations per chunk (EntityExtractor)
///   2. detect communities (Phase-5 stub: connected components only)
///   3. summarize each community via IChatClient
///
/// At query time the consumer searches over the community summaries
/// (typically as additional documents in a vector store).
///
/// This implementation is intentionally compact — the full Microsoft
/// GraphRAG (Python) is best invoked via the JsonExchange adapter when
/// the production deployment can accept a Python sidecar.
/// </summary>
public sealed class GraphRagPipeline
{
    private readonly EntityExtractor _extractor;
    private readonly IChatClient _chat;

    public GraphRagPipeline(EntityExtractor extractor, IChatClient chat)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(chat);
        _extractor = extractor;
        _chat = chat;
    }

    /// <summary>One community summary keyed by an arbitrary community id.</summary>
    public sealed record CommunitySummary(string CommunityId, IReadOnlyList<GraphEntity> Members, string Summary);

    public async Task<IReadOnlyList<CommunitySummary>> IndexAsync(
        IEnumerable<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        // 1. Extract.
        var entitiesById = new Dictionary<string, GraphEntity>(StringComparer.OrdinalIgnoreCase);
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ext = await _extractor.ExtractAsync(chunk.Text, cancellationToken).ConfigureAwait(false);
            foreach (var e in ext.Entities)
            {
                if (!string.IsNullOrEmpty(e.Id))
                {
                    entitiesById[e.Id] = e;
                    if (!adjacency.ContainsKey(e.Id))
                    {
                        adjacency[e.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            foreach (var r in ext.Relations)
            {
                if (string.IsNullOrEmpty(r.FromId) || string.IsNullOrEmpty(r.ToId))
                {
                    continue;
                }
                if (!adjacency.TryGetValue(r.FromId, out var fromSet))
                {
                    fromSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    adjacency[r.FromId] = fromSet;
                }
                fromSet.Add(r.ToId);
                if (!adjacency.TryGetValue(r.ToId, out var toSet))
                {
                    toSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    adjacency[r.ToId] = toSet;
                }
                toSet.Add(r.FromId);
            }
        }

        // 2. Communities = connected components (phase-5 stub for Leiden).
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var communities = new List<List<string>>();
        foreach (var node in adjacency.Keys)
        {
            if (visited.Contains(node))
            {
                continue;
            }
            var component = new List<string>();
            var stack = new Stack<string>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!visited.Add(cur))
                {
                    continue;
                }
                component.Add(cur);
                if (adjacency.TryGetValue(cur, out var nbrs))
                {
                    foreach (var n in nbrs)
                    {
                        stack.Push(n);
                    }
                }
            }
            communities.Add(component);
        }

        // 3. Summarize each community.
        var summaries = new List<CommunitySummary>();
        for (int i = 0; i < communities.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = communities[i].Where(id => entitiesById.ContainsKey(id))
                .Select(id => entitiesById[id]).ToList();
            if (members.Count == 0)
            {
                continue;
            }
            var memberLine = string.Join(", ",
                members.Select(m => $"{m.Type}: {m.Name}"));
            var prompt =
                "Summarize this community of related entities in two or three sentences.\n\n" +
                "Members: " + memberLine;
            var response = await _chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
            summaries.Add(new CommunitySummary(
                CommunityId: $"community-{i}",
                Members: members,
                Summary: (response.Text ?? string.Empty).Trim()));
        }
        return summaries;
    }
}
