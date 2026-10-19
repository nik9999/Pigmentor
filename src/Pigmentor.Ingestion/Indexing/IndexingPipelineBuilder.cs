using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Indexing;

/// <summary>
/// Fluent builder for picking an <see cref="IIndexingStrategy"/> per
/// document type (file extension or silo name). Used in Ch 7 and Ch 25.
///
/// <code>
/// var pipeline = new IndexingPipelineBuilder()
///     .ForDocumentType(".md", new QueryIndexingStrategy(chat))
///     .ForDocumentType(".pdf", new SummaryIndexingStrategy(chat))
///     .Default(new ChunkIndexingStrategy())
///     .Build();
///
/// await foreach (var item in pipeline.IndexAsync(chunk)) { ... }
/// </code>
/// </summary>
public sealed class IndexingPipelineBuilder
{
    private readonly Dictionary<string, IIndexingStrategy> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private IIndexingStrategy _default = new ChunkIndexingStrategy();

    public IndexingPipelineBuilder ForDocumentType(string extension, IIndexingStrategy strategy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentNullException.ThrowIfNull(strategy);
        _byExtension[extension] = strategy;
        return this;
    }

    public IndexingPipelineBuilder Default(IIndexingStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _default = strategy;
        return this;
    }

    public IndexingPipeline Build() => new(_byExtension, _default);
}

/// <summary>Routes a <see cref="DocumentChunk"/> to the right strategy by its source file extension.</summary>
public sealed class IndexingPipeline
{
    private readonly Dictionary<string, IIndexingStrategy> _byExtension;
    private readonly IIndexingStrategy _default;

    internal IndexingPipeline(Dictionary<string, IIndexingStrategy> byExtension, IIndexingStrategy fallback)
    {
        _byExtension = byExtension;
        _default = fallback;
    }

    /// <summary>Pick a strategy based on the parent document's extension and emit its items.</summary>
    public IAsyncEnumerable<IndexedItem> IndexAsync(
        DocumentChunk chunk,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var ext = Path.GetExtension(sourcePath);
        var strategy = !string.IsNullOrEmpty(ext) && _byExtension.TryGetValue(ext, out var s) ? s : _default;
        return strategy.IndexAsync(chunk, cancellationToken);
    }
}
