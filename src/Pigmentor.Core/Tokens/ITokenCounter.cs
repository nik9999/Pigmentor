namespace Pigmentor.Core.Tokens;

/// <summary>
/// Estimates the number of tokens an LLM will charge for a given piece of
/// text. Used everywhere context-window budget management matters: the
/// prompt-template engine (Ch 10), the conversation summarizer (Ch 12),
/// the cost-tracking decorators (Ch 21), the eval harness (Ch 20).
/// </summary>
/// <remarks>
/// <para>Implementations target ≤2% delta vs the corresponding
/// provider-side billing for the model family they were configured for.
/// If you need the count for a different model family, register a separate
/// <see cref="ITokenCounter"/> bound to that encoding.</para>
/// <para>Counts are pure functions of the input string; implementations
/// must be safe to call from any thread.</para>
/// </remarks>
public interface ITokenCounter
{
    /// <summary>
    /// The encoding name the counter was configured with (for example
    /// <c>cl100k_base</c> for the GPT-3.5 / GPT-4 / text-embedding-3 family,
    /// or <c>o200k_base</c> for GPT-4o / GPT-5).
    /// </summary>
    string EncodingName { get; }

    /// <summary>Count the tokens in <paramref name="text"/>.</summary>
    int CountTokens(string text);

    /// <summary>Count the tokens in a span of text without allocating a string.</summary>
    int CountTokens(ReadOnlySpan<char> text);
}
