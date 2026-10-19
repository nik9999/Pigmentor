using Pigmentor.Core.Documents;

namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Loads a single source document — Markdown, PDF, DOCX, etc. — from disk
/// (or any other source) into a <see cref="Document"/>.
/// Implementations are introduced incrementally: Ch 4 ships Markdown,
/// Ch 5 adds PDF and image-bearing formats.
/// </summary>
public interface IDocumentLoader
{
    /// <summary>The set of file extensions this loader handles (e.g. <c>.md</c>, <c>.pdf</c>).</summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>Load a document from <paramref name="path"/>.</summary>
    Task<Document> LoadAsync(string path, CancellationToken cancellationToken = default);
}
