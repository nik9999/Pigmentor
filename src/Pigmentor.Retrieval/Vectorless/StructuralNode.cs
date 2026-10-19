namespace Pigmentor.Retrieval.Vectorless;

/// <summary>
/// A node in the structural index. The tree is <em>id-linked</em>:
/// <see cref="ParentId"/> / <see cref="ChildIds"/> hold stable string ids that
/// resolve through <see cref="StructuralIndex"/> (no object references), which
/// keeps the type an immutable <see langword="record"/>.
/// <see cref="CrossReferences"/> are the identifiers this node cites in its own
/// <see cref="Text"/> — the basis for the deterministic cross-reference walk in
/// <see cref="StructuralRetriever.WithCrossReferences(string, int)"/>.
/// </summary>
/// <param name="Id">Stable identifier, e.g. <c>GDPR/Art17</c> or a path slug.</param>
/// <param name="Path">Human-readable breadcrumb, e.g. <c>GDPR &gt; Article 17 &gt; Paragraph 1</c>.</param>
/// <param name="Title">The heading text for this node.</param>
/// <param name="Text">The node's body text.</param>
/// <param name="ParentId">Id of the parent node, or <see langword="null"/> for the root.</param>
/// <param name="ChildIds">Ids of the direct children, in document order.</param>
/// <param name="CrossReferences">Ids this node cites in its <see cref="Text"/>.</param>
public sealed record StructuralNode(
    string Id,
    string Path,
    string Title,
    string Text,
    string? ParentId,
    IReadOnlyList<string> ChildIds,
    IReadOnlyList<string> CrossReferences);
