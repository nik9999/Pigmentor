using System.Security.Cryptography;
using System.Text;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Fingerprints;

/// <summary>
/// Provides deterministic hashing helpers for document content, pipeline versioning, and metadata fingerprinting.
/// </summary>
public static class IngestionFingerprint
{
    /// <summary>
    /// Central default normalization and extraction pipeline version.
    /// Incrementing this constant triggers deterministic reingestion across all stored documents.
    /// </summary>
    public const int DefaultNormalizationVersion = 1;

    /// <summary>
    /// Computes deterministic SHA-256 hash of raw source document content.
    /// </summary>
    public static string ComputeSourceContentHash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes effective content fingerprint incorporating the normalization pipeline version.
    /// When normalization version changes, the effective hash changes, triggering reingestion.
    /// </summary>
    public static string ComputeContentHash(string content, int normalizationVersion = DefaultNormalizationVersion)
    {
        ArgumentNullException.ThrowIfNull(content);
        var canonical = FormattableString.Invariant($"v{normalizationVersion}|{content}");
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes effective content fingerprint from pre-computed source content hash and normalization version.
    /// </summary>
    public static string ComputeEffectiveContentHash(string sourceContentHash, int normalizationVersion = DefaultNormalizationVersion)
    {
        ArgumentNullException.ThrowIfNull(sourceContentHash);
        var canonical = FormattableString.Invariant($"v{normalizationVersion}:{sourceContentHash}");
        var bytes = Encoding.UTF8.GetBytes(canonical);
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
