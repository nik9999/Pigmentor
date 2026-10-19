using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Numerics;

namespace Pigmentor.Ingestion.Chunking;

/// <summary>
/// Semantic chunker — splits sentences, then merges adjacent sentences whose
/// pairwise embedding cosine drops below <see cref="BreakpointThreshold"/>.
/// Captures topic shifts (the "natural breakpoint" pattern from Ch 4).
/// Slow: needs an embedding call per sentence. Use only when chunk
/// boundaries materially affect retrieval quality.
/// </summary>
public sealed class SemanticChunker : IChunker
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    public double BreakpointThreshold { get; }

    public SemanticChunker(
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        double breakpointThreshold = 0.5)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        _embeddings = embeddings;
        BreakpointThreshold = breakpointThreshold;
    }

    public string Strategy => "semantic";

    public async IAsyncEnumerable<DocumentChunk> ChunkAsync(
        Document document,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = document.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        // Reuse the SentenceChunker to produce sentence-level granularity.
        var sentenceChunker = new SentenceChunker(maxSentencesPerChunk: 1);
        var sentences = new List<DocumentChunk>();
        await foreach (var s in sentenceChunker.ChunkAsync(document, cancellationToken).ConfigureAwait(false))
        {
            sentences.Add(s);
        }
        if (sentences.Count == 0)
        {
            yield break;
        }

        // Embed all sentences in one batch.
        var embeddings = await _embeddings.GenerateAsync(
            sentences.Select(s => s.Text), cancellationToken: cancellationToken).ConfigureAwait(false);

        int chunkIndex = 0;
        int groupStart = 0;
        for (int i = 1; i <= sentences.Count; i++)
        {
            bool atBreak = i == sentences.Count;
            if (!atBreak)
            {
                var sim = CosineKernel.Cosine(embeddings[i - 1].Vector.Span, embeddings[i].Vector.Span);
                atBreak = sim < BreakpointThreshold;
            }

            if (atBreak)
            {
                var first = sentences[groupStart];
                var last = sentences[i - 1];
                var slice = text[first.StartCharOffset..last.EndCharOffset].Trim();
                yield return ChunkBuilder.Build(document, chunkIndex++, first.StartCharOffset, last.EndCharOffset, slice);
                groupStart = i;
            }
        }
    }

}
