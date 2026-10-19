namespace Pigmentor.VectorStore.SqliteVec;

/// <summary>
/// Configuration options for <see cref="SqliteVecStore"/>.
/// </summary>
public sealed class SqliteVecStoreOptions
{
    /// <summary>
    /// The SQLite connection string (e.g. <c>Data Source=pigmentor.db</c>).
    /// </summary>
    public string ConnectionString { get; set; } = "Data Source=pigmentor.db";

    /// <summary>
    /// Vector dimensions (e.g. 1536 for OpenAI small, 768 for nomic-embed-text).
    /// If 0, dimension is inferred upon first insertion or read from existing database metadata.
    /// </summary>
    public int Dimensions { get; set; }

    /// <summary>
    /// Optional explicit path to the native sqlite-vec extension file (vec0.dll / vec0.so / vec0.dylib).
    /// If null, automatically resolved from runtime directories.
    /// </summary>
    public string? ExtensionPath { get; set; }

    /// <summary>
    /// Logical collection name (default is "pigmentor").
    /// </summary>
    public string CollectionName { get; set; } = "pigmentor";

    /// <summary>
    /// Name of the embedding model stored in database metadata.
    /// </summary>
    public string? EmbeddingModel { get; set; }
}

