using System.Text;
using System.Text.RegularExpressions;

namespace Pigmentor.Retrieval.Vectorless;

/// <summary>
/// Parses Markdown headings (<c>#</c>, <c>##</c>, <c>###</c>, …) into an
/// id-linked <see cref="StructuralIndex"/>. Each node gets a stable
/// <see cref="StructuralNode.Id"/> (via <see cref="DefaultIdFor"/> or a
/// caller-supplied derivation), parent / child links are emitted as ids, and
/// after the tree is built each node's <see cref="StructuralNode.CrossReferences"/>
/// are populated by scanning its text for formal identifiers
/// (<c>§17</c>, <c>Article 17</c>, <c>RFC 7807</c>) and resolving them to known ids.
/// DOCX/PDF parsers (OpenXML / PdfPig structure layer) follow the same shape —
/// emit the same node set, then hand it to <see cref="StructuralIndex"/>.
/// </summary>
public sealed partial class DocumentStructureParser
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    // Formal cross-reference identifiers: §17, §17.2a, Article 17, RFC 7807.
    [GeneratedRegex(@"§\s*\d+(?:\.\d+)*[a-z]?|Article\s+\d+|RFC\s+\d+", RegexOptions.CultureInvariant)]
    private static partial Regex CrossReferenceRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    /// <summary>
    /// Parse <paramref name="markdown"/> into a <see cref="StructuralIndex"/>.
    /// </summary>
    /// <param name="title">The document title; becomes the root node.</param>
    /// <param name="markdown">The Markdown body.</param>
    /// <param name="idFor">
    /// Optional id-derivation strategy. Receives the node's path and title and
    /// returns its stable id. Defaults to <see cref="DefaultIdFor"/>, which prefers
    /// a detected formal identifier (e.g. <c>Article 17</c> → <c>Art17</c>) and
    /// otherwise slugs the path. Supply your own to plug a legal / RFC / code
    /// numbering convention.
    /// </param>
    public static StructuralIndex Parse(
        string title,
        string markdown,
        Func<string, string, string>? idFor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(markdown);
        idFor ??= DefaultIdFor;

        // Mutable scaffolding used only inside the parser; the public surface is
        // the immutable StructuralNode record produced at the end.
        var builders = new List<NodeBuilder>();
        var rootPath = "/" + title;
        var root = new NodeBuilder(idFor(rootPath, title), rootPath, title, 0, parentId: null);
        builders.Add(root);

        var stack = new Stack<NodeBuilder>();
        stack.Push(root);

        var bodyBuf = new StringBuilder();
        foreach (var line in markdown.Split('\n'))
        {
            var match = HeadingRegex().Match(line);
            if (match.Success)
            {
                FlushBody(stack.Peek(), bodyBuf);

                var level = match.Groups[1].Length;
                var name = match.Groups[2].Value.Trim();

                while (stack.Count > 0 && stack.Peek().Level >= level)
                {
                    stack.Pop();
                }
                if (stack.Count == 0)
                {
                    stack.Push(root);
                }

                var parent = stack.Peek();
                var path = parent.Path + "/" + name;
                var node = new NodeBuilder(idFor(path, name), path, name, level, parent.Id);
                parent.ChildIds.Add(node.Id);
                builders.Add(node);
                stack.Push(node);
            }
            else
            {
                bodyBuf.AppendLine(line);
            }
        }
        FlushBody(stack.Peek(), bodyBuf);

        // Resolve cross-references now that every id exists. We match a node's
        // citations against the known-id set and the formal-id index so that
        // "Article 17" in prose resolves to the node whose id is "Art17".
        var knownIds = builders.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var formalIndex = BuildFormalIndex(builders);
        var nodes = new List<StructuralNode>(builders.Count);
        foreach (var b in builders)
        {
            var crossRefs = ResolveCrossReferences(b, knownIds, formalIndex);
            nodes.Add(new StructuralNode(
                b.Id, b.Path, b.Title, b.Body, b.ParentId, [.. b.ChildIds], crossRefs));
        }

        var rootNode = nodes[0];
        return new StructuralIndex(rootNode, nodes);

        static void FlushBody(NodeBuilder target, StringBuilder buf)
        {
            if (buf.Length > 0)
            {
                var text = buf.ToString().Trim();
                target.Body = string.IsNullOrEmpty(target.Body) ? text : target.Body + "\n" + text;
                buf.Clear();
            }
        }
    }

    /// <summary>
    /// Default id-derivation: a detected formal identifier wins
    /// (<c>Article 17</c> → <c>Art17</c>, <c>§17</c> → <c>S17</c>,
    /// <c>RFC 7807</c> → <c>RFC7807</c>); otherwise the path is slugged.
    /// </summary>
    public static string DefaultIdFor(string path, string title)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(title);

        var formal = ToFormalId(title);
        return formal ?? Slug(path);
    }

    private static string Slug(string value)
    {
        var slug = SlugRegex().Replace(value.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "node" : slug;
    }

    // Canonical short id for a formal reference token, e.g. "Article 17" -> "Art17".
    private static string? ToFormalId(string text)
    {
        var m = CrossReferenceRegex().Match(text);
        if (!m.Success)
        {
            return null;
        }
        return Canonicalize(m.Value);
    }

    private static string Canonicalize(string token)
    {
        var t = token.Trim();
        if (t.StartsWith('§'))
        {
            return "S" + t[1..].Trim().Replace(" ", "", StringComparison.Ordinal);
        }
        if (t.StartsWith("Article", StringComparison.OrdinalIgnoreCase))
        {
            return "Art" + t["Article".Length..].Trim();
        }
        if (t.StartsWith("RFC", StringComparison.OrdinalIgnoreCase))
        {
            return "RFC" + t["RFC".Length..].Trim();
        }
        return Slug(t);
    }

    // Map a canonical formal id (e.g. "Art17") to the node id that owns it, so a
    // prose citation can resolve to a node even when ids are path slugs.
    private static Dictionary<string, string> BuildFormalIndex(IEnumerable<NodeBuilder> builders)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in builders)
        {
            var formal = ToFormalId(b.Title);
            if (formal is not null)
            {
                index[formal] = b.Id;
            }
        }
        return index;
    }

    private static List<string> ResolveCrossReferences(
        NodeBuilder node,
        HashSet<string> knownIds,
        Dictionary<string, string> formalIndex)
    {
        if (string.IsNullOrEmpty(node.Body))
        {
            return [];
        }

        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in CrossReferenceRegex().Matches(node.Body))
        {
            var canonical = Canonicalize(m.Value);
            // Prefer a node whose own title carries this formal id; otherwise the
            // canonical id may itself be a node id (e.g. when ids are formal).
            string? target = formalIndex.TryGetValue(canonical, out var byTitle)
                ? byTitle
                : knownIds.Contains(canonical) ? canonical : null;

            if (target is not null && target != node.Id && seen.Add(target))
            {
                resolved.Add(target);
            }
        }
        return resolved;
    }

    // Mutable per-node scaffolding. Never escapes the parser.
    private sealed class NodeBuilder(string id, string path, string title, int level, string? parentId)
    {
        public string Id { get; } = id;
        public string Path { get; } = path;
        public string Title { get; } = title;
        public int Level { get; } = level;
        public string? ParentId { get; } = parentId;
        public string Body { get; set; } = string.Empty;
        public List<string> ChildIds { get; } = [];
    }
}
