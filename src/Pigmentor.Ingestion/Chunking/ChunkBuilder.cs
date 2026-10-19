using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Chunking;

/// <summary>Internal helper that constructs <see cref="DocumentChunk"/>s with consistent IDs and offsets.</summary>
internal static class ChunkBuilder
{
    public static DocumentChunk Build(Document document, int index, int startOffset, int endOffset, string text)
    {
        return new DocumentChunk(
            ChunkId: $"{document.Metadata.Id}#{index}",
            DocumentId: document.Metadata.Id,
            ChunkIndex: index,
            Text: text,
            StartCharOffset: startOffset,
            EndCharOffset: endOffset,
            Metadata: document.Metadata);
    }
}
