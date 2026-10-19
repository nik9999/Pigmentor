using Microsoft.ML.Tokenizers;

namespace Pigmentor.Core.Tokens;

/// <summary>
/// <see cref="ITokenCounter"/> implementation backed by
/// <c>Microsoft.ML.Tokenizers.TiktokenTokenizer</c>. The default encoding is
/// <c>cl100k_base</c>, which matches the GPT-3.5 / GPT-4 / text-embedding-3
/// family — the same family that powers every chapter's default Azure OpenAI
/// configuration.
/// </summary>
/// <remarks>
/// The tokenizer is created once at construction time and reused for the
/// lifetime of the instance; <see cref="CountTokens(string)"/> is safe to
/// call concurrently. Register as a singleton in DI.
/// </remarks>
public sealed class TokenCounter : ITokenCounter
{
    /// <summary>
    /// The default encoding name. Matches the GPT-3.5 / GPT-4 /
    /// text-embedding-3-{small,large} family that the book defaults to.
    /// </summary>
    public const string DefaultEncoding = "cl100k_base";

    private readonly TiktokenTokenizer _tokenizer;

    /// <summary>Create a counter using the <see cref="DefaultEncoding"/> (cl100k_base).</summary>
    public TokenCounter() : this(DefaultEncoding) { }

    /// <summary>Create a counter for a specific tiktoken encoding.</summary>
    /// <param name="encodingName">A tiktoken encoding name — for example <c>cl100k_base</c> or <c>o200k_base</c>.</param>
    public TokenCounter(string encodingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodingName);
        EncodingName = encodingName;
        _tokenizer = TiktokenTokenizer.CreateForEncoding(encodingName);
    }

    /// <inheritdoc />
    public string EncodingName { get; }

    /// <inheritdoc />
    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _tokenizer.CountTokens(text);
    }

    /// <inheritdoc />
    public int CountTokens(ReadOnlySpan<char> text)
    {
        // Microsoft.ML.Tokenizers 2.0 surfaces span-based overloads on the
        // base Tokenizer class; we forward to it directly to avoid the
        // string allocation in hot paths (Ch 21 SpanEmbeddingPipeline).
        return _tokenizer.CountTokens(text);
    }
}
