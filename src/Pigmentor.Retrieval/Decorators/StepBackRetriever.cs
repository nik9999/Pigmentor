using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Decorators;

/// <summary>
/// Step-Back Prompting (Zheng et al., 2023) — before retrieving, ask the chat
/// model for ONE more-general "step-back" question that abstracts away the
/// specifics of the user's query. We then retrieve on BOTH the original query
/// and the broader step-back query and fuse the two ranked lists with RRF.
/// The step-back leg surfaces high-level / principle-level passages that a
/// narrow literal query often misses, while the original leg keeps the
/// specifics — the fusion gets the best of both.
/// </summary>
public sealed class StepBackRetriever : IRetriever
{
    private readonly IRetriever _inner;
    private readonly IChatClient _chat;
    private readonly RrfMerger _merger;

    internal const string Prompt =
        "Сформулируй ОДИН более общий концептуальный вопрос (step-back вопрос) к следующему вопросу на русском языке, " +
        "охватывающий базовые принципы и концепции предметной области. " +
        "Сохраняй исходные технические термины, названия протоколов, стандартов, моделей и ключевые сущности без перевода. " +
        "Ответь только текстом вопроса, без вводных слов и пояснений.\n\n" +
        "Вопрос: {0}";

    public StepBackRetriever(IRetriever inner, IChatClient chat, RrfMerger? merger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(chat);
        _inner = inner;
        _chat = chat;
        _merger = merger ?? new RrfMerger();
    }

    public string Strategy => $"stepback({_inner.Strategy})";

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
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TopK);

        var prompt = Prompt.Replace("{0}", request.Query, StringComparison.Ordinal);
        var response = await _chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var stepBack = (response.Text ?? string.Empty).Trim();

        // Pull a deeper candidate pool per leg so the fusion has room to work.
        var candidateK = Math.Max(50, request.TopK);
        var original = await _inner.RetrieveAsync(request with { TopK = candidateK }, cancellationToken).ConfigureAwait(false);

        // If the model returned nothing usable, fall back to the original leg
        // alone rather than re-issuing the same query as a degenerate "step-back".
        if (string.IsNullOrEmpty(stepBack) ||
            string.Equals(stepBack, request.Query, StringComparison.OrdinalIgnoreCase))
        {
            return [.. original.Take(request.TopK)];
        }

        var broader = await _inner.RetrieveAsync(request with { Query = stepBack, TopK = candidateK }, cancellationToken).ConfigureAwait(false);
        return _merger.Merge(original, broader, request.TopK);
    }
}
