using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;

namespace Pigmentor.Retrieval;

/// <summary>
/// Sparse keyword retriever — in-process BM25 (Robertson / Spärck-Jones)
/// over an indexed corpus of <see cref="DocumentChunk"/>s. The math is the standard formulation:
///
/// <code>
///   bm25(q, d) = Σ_{t in q} idf(t) × ((k1 + 1) × tf(t,d))
///                                   / (tf(t,d) + k1 × (1 - b + b × |d| / avgdl))
/// </code>
/// </summary>
public sealed partial class SparseRetriever : IRetriever
{
    public double K1 { get; }
    public double B { get; }

    private readonly Dictionary<string, double> _idf = new(StringComparer.Ordinal);
    private readonly List<(DocumentChunk Chunk, Dictionary<string, int> TermFreq, int Length)> _docs = [];
    private double _avgDocLength;

    public SparseRetriever(double k1 = 1.5, double b = 0.75)
    {
        K1 = k1;
        B = b;
    }

    public string Strategy => "sparse-bm25";

    [GeneratedRegex(@"[\p{L}\d_]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    /// <summary>Build the inverted index from the supplied chunks. Replaces any prior index.</summary>
    public void Index(IEnumerable<DocumentChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        _docs.Clear();
        _idf.Clear();

        var df = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        long totalLen = 0;
        foreach (var c in chunks)
        {
            var tokens = Tokenize(c.Text);
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in tokens)
            {
                tf[t] = tf.TryGetValue(t, out var n) ? n + 1 : 1;
            }
            _docs.Add((c, tf, tokens.Length));
            totalLen += tokens.Length;
            foreach (var term in tf.Keys)
            {
                df.AddOrUpdate(term, 1, (_, v) => v + 1);
            }
        }
        _avgDocLength = _docs.Count == 0 ? 0 : (double)totalLen / _docs.Count;
        // Standard Okapi IDF.
        foreach (var (term, freq) in df)
        {
            _idf[term] = Math.Log((_docs.Count - freq + 0.5) / (freq + 0.5) + 1.0);
        }
    }

    public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

    public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TopK);

        var queryTokens = Tokenize(request.Query);
        var scored = new List<RetrievalResult>(_docs.Count);
        foreach (var (chunk, tf, len) in _docs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request.Filter is not null && !request.Filter.Matches(chunk.Metadata))
            {
                continue;
            }

            double score = 0;
            foreach (var qt in queryTokens)
            {
                if (!_idf.TryGetValue(qt, out var idf) || !tf.TryGetValue(qt, out var f))
                {
                    continue;
                }
                var denom = f + K1 * (1 - B + B * len / Math.Max(_avgDocLength, 1.0));
                score += idf * ((K1 + 1) * f) / denom;
            }
            if (score > 0)
            {
                scored.Add(new RetrievalResult(chunk, score));
            }
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return Task.FromResult<IReadOnlyList<RetrievalResult>>(scored.Take(request.TopK).ToList());
    }

    private static string[] Tokenize(string text)
    {
        var matches = TokenRegex().Matches(text);
        var tokens = new string[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            tokens[i] = matches[i].Value.ToLowerInvariant();
        }
        return tokens;
    }
}
