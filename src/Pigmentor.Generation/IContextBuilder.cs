using Pigmentor.Core.Documents;

namespace Pigmentor.Generation;

/// <summary>
/// Builds a bounded, ordered, and formatted context package from retrieval candidates.
/// </summary>
public interface IContextBuilder
{
    /// <summary>
    /// Builds a <see cref="RagContext"/> respecting the specified token budget.
    /// </summary>
    RagContext BuildContext(
        IReadOnlyList<RetrievalResult> candidates,
        ContextBudget budget);

    /// <summary>
    /// Builds a <see cref="RagContext"/> respecting a maximum available token limit.
    /// </summary>
    RagContext BuildContext(
        IReadOnlyList<RetrievalResult> candidates,
        int maxTokens) => BuildContext(candidates, new ContextBudget(maxTokens, 0));
}

