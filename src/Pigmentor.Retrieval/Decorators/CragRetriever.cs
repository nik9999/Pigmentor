using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Decorators;

/// <summary>
/// CRAG (Corrective RAG) — runs the inner retriever, asks an
/// <see cref="IChatClient"/> to grade each result as
/// <c>CORRECT</c> / <c>AMBIGUOUS</c> / <c>INCORRECT</c>, drops
/// <c>INCORRECT</c> hits, and (if there are no <c>CORRECT</c> hits left)
/// invokes an optional <see cref="WebFallback"/> retriever. Original Self-RAG's
/// reflection tokens are replaced by structured-outputs prompting.
/// </summary>
public sealed class CragRetriever : IRetriever
{
    private readonly IRetriever _inner;
    private readonly IChatClient _chat;

    /// <summary>Optional web-search fallback when all inner results grade INCORRECT.</summary>
    public IRetriever? WebFallback { get; init; }

    public string Strategy => $"crag({_inner.Strategy})";

    public CragRetriever(IRetriever inner, IChatClient chat)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(chat);
        _inner = inner;
        _chat = chat;
    }

    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        var candidates = await _inner.RetrieveAsync(query, topK * 2, cancellationToken).ConfigureAwait(false);
        var graded = new List<RetrievalResult>();
        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var grade = await GradeAsync(query, c.Chunk.Text, cancellationToken).ConfigureAwait(false);
            if (grade != Grade.Incorrect)
            {
                graded.Add(c);
            }
        }

        if (graded.Count == 0 && WebFallback is not null)
        {
            return await WebFallback.RetrieveAsync(query, topK, cancellationToken).ConfigureAwait(false);
        }
        return [.. graded.Take(topK)];
    }

    private enum Grade { Correct, Ambiguous, Incorrect }

    private async Task<Grade> GradeAsync(string query, string passage, CancellationToken cancellationToken)
    {
        var prompt =
            $"Grade how well the passage answers the question. Reply with EXACTLY one token: " +
            $"CORRECT, AMBIGUOUS, or INCORRECT.\n\nQuestion: {query}\n\nPassage: {passage}";
        var response = await _chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = (response.Text ?? "AMBIGUOUS").Trim().ToUpperInvariant();
        if (text.StartsWith("CORRECT", StringComparison.Ordinal))
        {
            return Grade.Correct;
        }

        if (text.StartsWith("INCORRECT", StringComparison.Ordinal))
        {
            return Grade.Incorrect;
        }

        return Grade.Ambiguous;
    }
}
