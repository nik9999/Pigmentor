using System.Text.RegularExpressions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval;

/// <summary>
/// Deterministic second-stage reranker that combines lexical coverage with
/// structured-table and requested-category signals.
/// </summary>
public sealed partial class TableAwareLexicalReranker : IReranker
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "в", "во", "и", "из", "к", "как", "какие", "какой", "на", "по", "у", "для",
        "все", "всех", "назови", "перечисли", "таблица", "таблицы", "the", "a", "an", "of", "for"
    };

    public Task<IReadOnlyList<RetrievalResult>> RerankAsync(
        string query,
        IReadOnlyList<RetrievalResult> candidates,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(candidates);
        if (topK <= 0 || candidates.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<RetrievalResult>>([]);
        }

        var queryTerms = ExtractTerms(query);
        var lowerQuery = query.ToLowerInvariant();
        var asksForTableEnumeration = Regex.IsMatch(lowerQuery, @"\b(?:все|перечисли\w*|назови\w*)\b") &&
                                      Regex.IsMatch(lowerQuery, @"\b(?:диапазон\w*|строк\w*|значени\w*)\b");
        var asksForCurrent = Regex.IsMatch(lowerQuery, @"\bток\w*\b|\bтоков\w*\b");
        var asksForVoltage = Regex.IsMatch(lowerQuery, @"\bнапряжени\w*\b");
        var asksForRange = Regex.IsMatch(lowerQuery, @"\bдиапазон\w*\b");
        var asksForTerminals = Regex.IsMatch(lowerQuery, @"\bклемм\w*\b");
        var asksForDcon = lowerQuery.Contains("dcon", StringComparison.Ordinal);

        var ranked = candidates
            .Select((candidate, index) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = candidate.Chunk.Text;
                var terms = ExtractTerms(text);
                var overlap = queryTerms.Count == 0
                    ? 0.0
                    : (double)queryTerms.Count(terms.Contains) / queryTerms.Count;
                var score = overlap * 8.0;

                // Preserve the fused order as a weak prior, not as the final decision.
                score += 1.0 / (index + 1);
                score += candidate.Score;

                var tableRowCount = TableRowRegex().Count(text);
                if (tableRowCount >= 2 && (asksForTableEnumeration || asksForRange || asksForTerminals || asksForDcon))
                {
                    score += 2.0;
                }
                if (asksForCurrent && CurrentRowRegex().IsMatch(text))
                {
                    score += 6.0;
                }
                if (asksForVoltage && VoltageRowRegex().IsMatch(text))
                {
                    score += 6.0;
                }
                if (asksForRange && RangeRegex().IsMatch(text))
                {
                    score += 3.0;
                }
                if (asksForTerminals && TerminalRowRegex().IsMatch(text))
                {
                    score += 6.0;
                }
                if (asksForDcon && DconCommandRegex().IsMatch(text))
                {
                    score += 4.0;
                }

                return new RetrievalResult(candidate.Chunk, score);
            })
            .OrderByDescending(result => result.Score)
            .Take(topK)
            .ToList();

        return Task.FromResult<IReadOnlyList<RetrievalResult>>(ranked);
    }

    private static HashSet<string> ExtractTerms(string text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in TokenRegex().Matches(text.ToLowerInvariant()))
        {
            var raw = match.Value;
            if (!StopWords.Contains(raw))
            {
                terms.Add(Canonicalize(raw));
            }
        }
        return terms;
    }

    private static string Canonicalize(string token)
    {
        if (token.StartsWith("ток", StringComparison.Ordinal)) return "ток";
        if (token.StartsWith("диапаз", StringComparison.Ordinal)) return "диапазон";
        if (token.StartsWith("напряжен", StringComparison.Ordinal)) return "напряжение";
        if (token.StartsWith("клемм", StringComparison.Ordinal)) return "клемма";
        if (token.StartsWith("характерист", StringComparison.Ordinal)) return "характеристика";
        if (token.StartsWith("команд", StringComparison.Ordinal)) return "команда";
        if (token.StartsWith("датчик", StringComparison.Ordinal)) return "датчик";
        if (token.StartsWith("канал", StringComparison.Ordinal)) return "канал";
        return token;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-_/][\p{L}\p{N}]+)*")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?m)^\s*\|.+\|\s*$")]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"(?im)^\s*\|\s*Ток\s*\|")]
    private static partial Regex CurrentRowRegex();

    [GeneratedRegex(@"(?im)^\s*\|\s*Напряжение\s*\|")]
    private static partial Regex VoltageRowRegex();

    [GeneratedRegex(@"[+\-]?\d+(?:[\.,]\d+)?\s*(?:…|\.\.\.|–|—|-)\s*[+\-]?\d+(?:[\.,]\d+)?")]
    private static partial Regex RangeRegex();

    [GeneratedRegex(@"(?im)^\s*\|\s*X1:\d+\s*\|")]
    private static partial Regex TerminalRowRegex();

    [GeneratedRegex(@"[$~%][A-Z]{2,}[A-Z0-9]*", RegexOptions.IgnoreCase)]
    private static partial Regex DconCommandRegex();
}
