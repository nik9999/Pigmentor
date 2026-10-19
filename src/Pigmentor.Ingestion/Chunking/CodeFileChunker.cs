using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Chunking;

/// <summary>
/// Splits C# source files into chunks aligned to class and method
/// boundaries using Roslyn. Each emitted chunk is made self-describing by
/// prepending the surrounding context — the file's <c>using</c> directives,
/// the containing namespace, and the enclosing type chain — so a method chunk
/// retrieved in isolation still names the class and namespace it belongs to.
/// Documents whose <c>SourcePath</c> doesn't end in <c>.cs</c> fall back to
/// whole-document chunks.
/// </summary>
/// <remarks>
/// Like <see cref="ContextualChunker"/>, only <see cref="DocumentChunk.Text"/>
/// is augmented: <see cref="DocumentChunk.StartCharOffset"/> and
/// <see cref="DocumentChunk.EndCharOffset"/> still reference the original member
/// span, so citations keep pointing at the right slice of the source file.
/// </remarks>
public sealed class CodeFileChunker : IChunker
{
    public string Strategy => "code-cs";

    public async IAsyncEnumerable<DocumentChunk> ChunkAsync(
        Document document,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await Task.Yield();

        var text = document.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        if (!document.SourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            yield return ChunkBuilder.Build(document, 0, 0, text.Length, text);
            yield break;
        }

        var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);

        // File-level context that every chunk shares: the using directives.
        var usings = BuildUsingContext(root);

        var members = root.DescendantNodes()
            .Where(static n => n is BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax)
            .OrderBy(static n => n.FullSpan.Start)
            .ToList();

        if (members.Count == 0)
        {
            yield return ChunkBuilder.Build(document, 0, 0, text.Length, text);
            yield break;
        }

        int index = 0;
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var span = member.FullSpan;
            int start = Math.Min(span.Start, text.Length);
            int end = Math.Min(span.End, text.Length);
            if (end <= start)
            {
                continue;
            }

            var body = text[start..end];
            var augmented = PrependContext(usings, member, body);
            yield return ChunkBuilder.Build(document, index++, start, end, augmented);
        }
    }

    /// <summary>
    /// Builds the shared file-level context line: the namespace plus the
    /// (possibly nested) type chain that encloses <paramref name="member"/>, then
    /// the member body itself. The result reads like a compressed view of the
    /// file so the chunk is self-describing even when retrieved alone.
    /// </summary>
    private static string PrependContext(string usings, SyntaxNode member, string body)
    {
        var header = new StringBuilder();
        if (usings.Length > 0)
        {
            header.Append(usings).Append('\n');
        }

        var ns = member.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns is not null)
        {
            header.Append("namespace ").Append(ns.Name.ToString()).Append(";\n");
        }

        // Walk the enclosing type declarations from outermost to innermost so a
        // method inside a nested type names every level above it.
        var enclosingTypes = member.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Reverse()
            .ToList();
        foreach (var type in enclosingTypes)
        {
            header.Append(DescribeType(type)).Append('\n');
        }

        if (header.Length == 0)
        {
            return body;
        }

        // A blank line separates the synthesized context header from the original
        // source body, matching the ContextualChunker augmentation convention.
        return $"{header.ToString().TrimEnd('\n')}\n\n{body}";
    }

    private static string BuildUsingContext(SyntaxNode root)
    {
        var usings = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(static u => u.ToString().Trim())
            .Where(static s => s.Length > 0)
            .ToList();
        return usings.Count == 0 ? string.Empty : string.Join('\n', usings);
    }

    /// <summary>
    /// Renders a single-line signature for an enclosing type — for example
    /// <c>public class Calculator</c> — without its body, so the header stays
    /// compact.
    /// </summary>
    private static string DescribeType(BaseTypeDeclarationSyntax type)
    {
        var modifiers = string.Join(' ', type.Modifiers.Select(static m => m.Text));
        var keyword = type switch
        {
            ClassDeclarationSyntax => "class",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            RecordDeclarationSyntax r => r.ClassOrStructKeyword.Text.Length > 0 ? $"record {r.ClassOrStructKeyword.Text}" : "record",
            EnumDeclarationSyntax => "enum",
            _ => "type",
        };
        var name = type.Identifier.Text;
        return modifiers.Length > 0 ? $"{modifiers} {keyword} {name}" : $"{keyword} {name}";
    }
}
