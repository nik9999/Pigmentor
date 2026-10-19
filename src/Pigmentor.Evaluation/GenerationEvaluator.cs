using Microsoft.Extensions.AI;
using Pigmentor.Core.Documents;

namespace Pigmentor.Evaluation;

/// <summary>Per-query faithfulness assessment.</summary>
public sealed record FaithfulnessAssessment(
    string Query,
    string Answer,
    int SupportedClaims,
    int PartiallySupportedClaims,
    int NotSupportedClaims,
    double FaithfulnessScore);

/// <summary>
/// LLM-as-judge faithfulness scorer. Asks the chat client to grade each
/// sentence in the answer against the retrieved context as
/// SUPPORTED / PARTIALLY_SUPPORTED / NOT_SUPPORTED, then aggregates.
/// </summary>
public sealed class GenerationEvaluator
{
    private readonly IChatClient _judge;

    public GenerationEvaluator(IChatClient judge)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _judge = judge;
    }

    public async Task<FaithfulnessAssessment> AssessAsync(
        string query,
        string answer,
        IReadOnlyList<RetrievalResult> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(sources);

        var sentences = SplitSentences(answer);
        if (sentences.Count == 0)
        {
            return new FaithfulnessAssessment(query, answer, 0, 0, 0, 1.0);
        }

        var contextBlock = string.Join("\n",
            sources.Select((s, i) => $"[Source {i + 1}] {s.Chunk.Text}"));

        int supported = 0, partial = 0, notSupported = 0;
        foreach (var sentence in sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prompt =
                $"Grade the claim against the context. Reply EXACTLY one token: " +
                $"SUPPORTED, PARTIALLY_SUPPORTED, or NOT_SUPPORTED.\n\n" +
                $"Context:\n{contextBlock}\n\nClaim: {sentence}";
            var response = await _judge.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
            var verdict = (response.Text ?? "PARTIALLY_SUPPORTED").Trim().ToUpperInvariant();
            if (verdict.StartsWith("SUPPORTED", StringComparison.Ordinal))
            {
                supported++;
            }
            else if (verdict.StartsWith("NOT_SUPPORTED", StringComparison.Ordinal))
            {
                notSupported++;
            }
            else
            {
                partial++;
            }
        }
        var total = sentences.Count;
        var score = (supported + 0.5 * partial) / total;
        return new FaithfulnessAssessment(query, answer, supported, partial, notSupported, score);
    }

    private static IReadOnlyList<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        return [.. text
            .Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 3)];
    }
}
