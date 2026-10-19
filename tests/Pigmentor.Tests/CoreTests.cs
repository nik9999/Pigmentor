using Pigmentor.Core.Documents;
using Pigmentor.Core.Numerics;
using Pigmentor.Ingestion;
using Pigmentor.Ingestion.Chunking;

namespace Pigmentor.Tests;

public sealed class CoreTests
{
    [Fact]
    public void CosineSimilarityIsOneForIdenticalVectors()
    {
        Assert.Equal(1d, CosineKernel.Cosine([1, 2], [1, 2]), precision: 10);
    }

    [Fact]
    public async Task FixedChunkerPreservesDocumentIdentity()
    {
        var metadata = new DocumentMetadata("doc-1", "", "", "", "", "Reference", 0, "", DateOnly.MinValue, "doc-1");
        var document = new Document(metadata, "First paragraph.\n\nSecond paragraph.", "doc-1.md");
        var chunks = new List<DocumentChunk>();
        await foreach (var chunk in new FixedSizeChunker(20, 0).ChunkAsync(document))
        {
            chunks.Add(chunk);
        }
        Assert.All(chunks, chunk => Assert.Equal("doc-1", chunk.DocumentId));
    }
}
