namespace Pigmentor.Retrieval.Vectorless;

/// <summary>
/// Holds the structural tree plus a by-id dictionary that backs O(1)
/// <see cref="Lookup(string)"/>. The index is the resolver for the id-linked
/// <see cref="StructuralNode"/> graph: parent / child / cross-reference ids are
/// turned back into nodes here. Built by <see cref="DocumentStructureParser"/>.
/// </summary>
public sealed class StructuralIndex
{
    private readonly IReadOnlyDictionary<string, StructuralNode> _byId;

    /// <summary>The root node of the structural tree.</summary>
    public StructuralNode Root { get; }

    /// <summary>
    /// Build the index from its root and the full node set. The node set is
    /// projected into a by-id dictionary, so every parent / child / cross-reference
    /// id resolves in O(1).
    /// </summary>
    /// <param name="root">The tree root (also expected to appear in <paramref name="allNodes"/>).</param>
    /// <param name="allNodes">Every node in the tree, including the root.</param>
    public StructuralIndex(StructuralNode root, IEnumerable<StructuralNode> allNodes)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(allNodes);
        Root = root;
        _byId = allNodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
    }

    /// <summary>Resolve a node by its stable id in O(1), or <see langword="null"/> if unknown.</summary>
    public StructuralNode? Lookup(string id) =>
        id is not null && _byId.TryGetValue(id, out var node) ? node : null;

    /// <summary>Enumerate every node in the index.</summary>
    public IEnumerable<StructuralNode> AllNodes() => _byId.Values;
}
