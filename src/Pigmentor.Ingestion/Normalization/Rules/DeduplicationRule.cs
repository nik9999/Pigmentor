using System.Security.Cryptography;
using System.Text;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Normalization.Rules;

/// <summary>
/// Detects and eliminates exact duplicate blocks and pages before chunking and embedding.
/// </summary>
public static class DeduplicationRule
{
    public static IReadOnlyList<ExtractedBlock> Deduplicate(
        IReadOnlyList<ExtractedBlock> blocks,
        NormalizationDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (blocks.Count <= 1)
        {
            return blocks;
        }

        var seenBlockHashes = new HashSet<string>(StringComparer.Ordinal);
        var uniqueBlocks = new List<ExtractedBlock>(blocks.Count);

        foreach (var block in blocks)
        {
            var text = block.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var hash = ComputeHash(FormattableString.Invariant($"{block.Kind}:{text}"));
            if (seenBlockHashes.Add(hash))
            {
                uniqueBlocks.Add(block);
            }
            else
            {
                diagnostics.DuplicateBlocksRemoved++;
            }
        }

        return uniqueBlocks;
    }

    private static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

