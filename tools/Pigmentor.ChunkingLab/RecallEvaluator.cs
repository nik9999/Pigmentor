using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record RecallEvalQuery(
    string Id,
    string Query,
    IReadOnlyList<string> RelevantDocuments,
    IReadOnlyList<string>? RelevantChunkIds = null);

internal static partial class RecallEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<IReadOnlyList<RecallEvalQuery>> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var queries = await JsonSerializer.DeserializeAsync<List<RecallEvalQuery>>(
            stream,
            JsonOptions);
        return queries ?? [];
    }

    public static void PrintReport(
        IReadOnlyList<RecallEvalQuery> queries,
        IReadOnlyDictionary<string, List<ChunkRecord>> chunksByStrategy,
        IReadOnlyList<int> ks,
        string inputDirectory)
    {
        Console.WriteLine($"\nRecall@K evaluation: queries={queries.Count}, k=[{string.Join(",", ks)}]");
        Console.WriteLine("CR@K = Context Relevance@K = relevant chunks in top K divided by K.");
        Console.WriteLine("Strategy                     MRR      " + string.Join("  ", ks.Select(k => $"R@{k,5}")) + "    " + string.Join("  ", ks.Select(k => $"CR@{k,4}")));

        foreach (var (strategy, chunks) in chunksByStrategy)
        {
            var ranked = queries.Select(query => Rank(query, chunks, inputDirectory)).ToArray();
            var mrr = queries.Count == 0
                ? 0
                : ranked.Average(result =>
                {
                    var firstRelevant = Array.FindIndex(result, item => item.IsRelevant);
                    return firstRelevant < 0 ? 0 : 1d / (firstRelevant + 1);
                });
            var values = ks.Select(k =>
            {
                var hits = ranked.Count(result => result.Take(k).Any(result => result.IsRelevant));
                return queries.Count == 0 ? 0 : (double)hits / queries.Count;
            }).ToArray();
            var contextRelevance = ks.Select(k =>
            {
                if (queries.Count == 0)
                {
                    return 0d;
                }

                return ranked.Average(result =>
                {
                    var window = result.Take(k).ToArray();
                    return window.Length == 0 ? 0 : window.Count(item => item.IsRelevant) / (double)k;
                });
            });
            Console.WriteLine($"{strategy,-28} {mrr,6:P1}  "
                + string.Join("  ", values.Select(value => $"{value,6:P1}"))
                + "    "
                + string.Join("  ", contextRelevance.Select(value => $"{value,6:P1}")));
        }
    }

    private static RankedChunk[] Rank(
        RecallEvalQuery query,
        IReadOnlyList<ChunkRecord> chunks,
        string inputDirectory)
    {
        var queryTerms = Terms(query.Query);
        return chunks
            .Select(chunk => new RankedChunk(
                chunk,
                Score(queryTerms, chunk.Text),
                IsRelevant(chunk, query, inputDirectory)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Chunk.ChunkId, StringComparer.Ordinal)
            .ToArray();
    }

    private static double Score(HashSet<string> queryTerms, string text)
    {
        if (queryTerms.Count == 0)
        {
            return 0;
        }

        var chunkTerms = Terms(text);
        return queryTerms.Count(term => chunkTerms.Contains(term)) / (double)queryTerms.Count;
    }

    private static bool IsRelevant(ChunkRecord chunk, RecallEvalQuery query, string inputDirectory)
    {
        if (query.RelevantChunkIds is { Count: > 0 })
        {
            return query.RelevantChunkIds.Any(expected =>
                chunk.ChunkId.Equals(expected, StringComparison.OrdinalIgnoreCase));
        }

        var source = Normalize(Path.GetRelativePath(inputDirectory, chunk.SourcePath));
        var sourceName = Normalize(Path.GetFileName(chunk.SourcePath));
        return query.RelevantDocuments.Any(expected =>
        {
            var normalized = Normalize(expected);
            return source.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || sourceName.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || source.EndsWith('/' + normalized, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static HashSet<string> Terms(string value) =>
        TermRegex().Matches(value.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(term => term.Length > 2)
            .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('.', '/');

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex TermRegex();

    private sealed record RankedChunk(ChunkRecord Chunk, double Score, bool IsRelevant);
}
