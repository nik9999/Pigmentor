using System.Security.Cryptography;
using System.Text;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Fingerprints;

/// <summary>
/// Provides deterministic hashing helpers for document content and metadata fingerprinting.
/// </summary>
public static class IngestionFingerprint
{
    /// <summary>
    /// Computes deterministic SHA-256 hash of raw document content as a lowercase hex string.
    /// </summary>
    public static string ComputeContentHash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes deterministic SHA-256 hash of canonical document metadata as a lowercase hex string.
    /// </summary>
    public static string ComputeMetadataHash(DocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var canonical = FormattableString.Invariant(
            $"{metadata.Id}|{metadata.Silo}|{metadata.Department}|{metadata.Office}|{metadata.ConfidentialityLevel}|{metadata.DocumentType}|{metadata.FiscalYear}|{metadata.Author}|{metadata.LastModified:yyyy-MM-dd}|{metadata.Title}");
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

