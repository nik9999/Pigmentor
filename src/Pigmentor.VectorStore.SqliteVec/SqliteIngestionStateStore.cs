using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Pigmentor.Core.Abstractions;

namespace Pigmentor.VectorStore.SqliteVec;

/// <summary>
/// SQLite implementation of <see cref="IIngestionStateStore"/> that stores lifecycle
/// state and chunk associations in standard relational SQLite tables.
/// </summary>
public sealed class SqliteIngestionStateStore : IIngestionStateStore, IDisposable, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public SqliteIngestionStateStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;

        SqliteVecExtensionLoader.EnsureSqliteProvider();

        _connection = new SqliteConnection(_connectionString);
        _connection.Open();

        InitializeTables();
    }

    public SqliteIngestionStateStore(SqliteVecStoreOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).ConnectionString)
    {
    }

    private void InitializeTables()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS ingestion_documents (
                source_id TEXT NOT NULL,
                document_id TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                metadata_hash TEXT NOT NULL,
                version INTEGER NOT NULL,
                status TEXT NOT NULL,
                last_successful_ingestion TEXT NULL,
                PRIMARY KEY (source_id, document_id)
            );

            CREATE TABLE IF NOT EXISTS ingestion_document_chunks (
                source_id TEXT NOT NULL,
                document_id TEXT NOT NULL,
                chunk_id TEXT NOT NULL,
                PRIMARY KEY (source_id, document_id, chunk_id),
                FOREIGN KEY (source_id, document_id)
                    REFERENCES ingestion_documents(source_id, document_id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_ingestion_docs_status
                ON ingestion_documents(status);
        ";
        cmd.ExecuteNonQuery();
    }

    public async Task<IngestionDocumentState?> GetAsync(
        string documentId,
        string sourceId = "default",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT content_hash, metadata_hash, version, status, last_successful_ingestion
                FROM ingestion_documents
                WHERE source_id = $source_id AND document_id = $document_id;
            ";
            cmd.Parameters.AddWithValue("$source_id", sourceId);
            cmd.Parameters.AddWithValue("$document_id", documentId);

            string contentHash;
            string metadataHash;
            long version;
            IngestionStatus status;
            DateTimeOffset? lastSuccess = null;

            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                contentHash = reader.GetString(0);
                metadataHash = reader.GetString(1);
                version = reader.GetInt64(2);
                var statusStr = reader.GetString(3);
                status = Enum.TryParse<IngestionStatus>(statusStr, true, out var parsedStatus)
                    ? parsedStatus
                    : IngestionStatus.Failed;

                if (!reader.IsDBNull(4))
                {
                    var dtStr = reader.GetString(4);
                    if (DateTimeOffset.TryParse(dtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                    {
                        lastSuccess = dt;
                    }
                }
            }

            // Read chunk IDs
            using var chunkCmd = _connection.CreateCommand();
            chunkCmd.CommandText = @"
                SELECT chunk_id
                FROM ingestion_document_chunks
                WHERE source_id = $source_id AND document_id = $document_id
                ORDER BY chunk_id;
            ";
            chunkCmd.Parameters.AddWithValue("$source_id", sourceId);
            chunkCmd.Parameters.AddWithValue("$document_id", documentId);

            var chunkIds = new List<string>();
            await using (var chunkReader = await chunkCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await chunkReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    chunkIds.Add(chunkReader.GetString(0));
                }
            }

            return new IngestionDocumentState(
                SourceId: sourceId,
                DocumentId: documentId,
                ContentHash: contentHash,
                MetadataHash: metadataHash,
                Version: version,
                Status: status,
                LastSuccessfulIngestion: lastSuccess,
                ChunkIds: chunkIds);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<IngestionDocumentState>> ListAsync(
        string sourceId = "default",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<IngestionDocumentState>();

            // Read all document records
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT document_id, content_hash, metadata_hash, version, status, last_successful_ingestion
                    FROM ingestion_documents
                    WHERE source_id = $source_id
                    ORDER BY document_id;
                ";
                cmd.Parameters.AddWithValue("$source_id", sourceId);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var docId = reader.GetString(0);
                    var contentHash = reader.GetString(1);
                    var metadataHash = reader.GetString(2);
                    var version = reader.GetInt64(3);
                    var statusStr = reader.GetString(4);
                    var status = Enum.TryParse<IngestionStatus>(statusStr, true, out var parsedStatus)
                        ? parsedStatus
                        : IngestionStatus.Failed;

                    DateTimeOffset? lastSuccess = null;
                    if (!reader.IsDBNull(5))
                    {
                        var dtStr = reader.GetString(5);
                        if (DateTimeOffset.TryParse(dtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                        {
                            lastSuccess = dt;
                        }
                    }

                    results.Add(new IngestionDocumentState(
                        SourceId: sourceId,
                        DocumentId: docId,
                        ContentHash: contentHash,
                        MetadataHash: metadataHash,
                        Version: version,
                        Status: status,
                        LastSuccessfulIngestion: lastSuccess,
                        ChunkIds: []));
                }
            }

            // Read all chunk associations for this source in one batch
            using (var chunkCmd = _connection.CreateCommand())
            {
                chunkCmd.CommandText = @"
                    SELECT document_id, chunk_id
                    FROM ingestion_document_chunks
                    WHERE source_id = $source_id
                    ORDER BY document_id, chunk_id;
                ";
                chunkCmd.Parameters.AddWithValue("$source_id", sourceId);

                var chunksByDoc = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                await using var chunkReader = await chunkCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await chunkReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var docId = chunkReader.GetString(0);
                    var chunkId = chunkReader.GetString(1);

                    if (!chunksByDoc.TryGetValue(docId, out var list))
                    {
                        list = new List<string>();
                        chunksByDoc[docId] = list;
                    }
                    list.Add(chunkId);
                }

                // Associate chunk IDs with results
                for (var i = 0; i < results.Count; i++)
                {
                    var doc = results[i];
                    if (chunksByDoc.TryGetValue(doc.DocumentId, out var list))
                    {
                        results[i] = doc with { ChunkIds = list };
                    }
                }
            }

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(
        IngestionDocumentState state,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.DocumentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.SourceId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction();

            // Optimistic concurrency check if expectedVersion is specified
            if (expectedVersion.HasValue)
            {
                using var checkCmd = _connection.CreateCommand();
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = @"
                    SELECT version FROM ingestion_documents
                    WHERE source_id = $source_id AND document_id = $document_id;
                ";
                checkCmd.Parameters.AddWithValue("$source_id", state.SourceId);
                checkCmd.Parameters.AddWithValue("$document_id", state.DocumentId);

                var existingVerObj = await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                if (expectedVersion.Value == 0)
                {
                    // Expecting document to not exist yet
                    if (existingVerObj != null && existingVerObj != DBNull.Value)
                    {
                        throw new InvalidOperationException(
                            $"Optimistic concurrency violation: document '{state.DocumentId}' in source '{state.SourceId}' already exists (version {existingVerObj}), expected new document.");
                    }
                }
                else
                {
                    if (existingVerObj == null || existingVerObj == DBNull.Value)
                    {
                        throw new InvalidOperationException(
                            $"Optimistic concurrency violation: document '{state.DocumentId}' in source '{state.SourceId}' does not exist, expected version {expectedVersion.Value}.");
                    }

                    var actualVersion = Convert.ToInt64(existingVerObj, CultureInfo.InvariantCulture);
                    if (actualVersion != expectedVersion.Value)
                    {
                        throw new InvalidOperationException(
                            $"Optimistic concurrency violation: document '{state.DocumentId}' in source '{state.SourceId}' has version {actualVersion}, expected version {expectedVersion.Value}.");
                    }
                }
            }

            // Upsert document record
            using (var docCmd = _connection.CreateCommand())
            {
                docCmd.Transaction = transaction;
                docCmd.CommandText = @"
                    INSERT INTO ingestion_documents (
                        source_id, document_id, content_hash, metadata_hash, version, status, last_successful_ingestion
                    ) VALUES (
                        $source_id, $document_id, $content_hash, $metadata_hash, $version, $status, $last_successful_ingestion
                    )
                    ON CONFLICT(source_id, document_id) DO UPDATE SET
                        content_hash = excluded.content_hash,
                        metadata_hash = excluded.metadata_hash,
                        version = excluded.version,
                        status = excluded.status,
                        last_successful_ingestion = excluded.last_successful_ingestion;
                ";
                docCmd.Parameters.AddWithValue("$source_id", state.SourceId);
                docCmd.Parameters.AddWithValue("$document_id", state.DocumentId);
                docCmd.Parameters.AddWithValue("$content_hash", state.ContentHash);
                docCmd.Parameters.AddWithValue("$metadata_hash", state.MetadataHash);
                docCmd.Parameters.AddWithValue("$version", state.Version);
                docCmd.Parameters.AddWithValue("$status", state.Status.ToString());
                docCmd.Parameters.AddWithValue(
                    "$last_successful_ingestion",
                    state.LastSuccessfulIngestion.HasValue
                        ? state.LastSuccessfulIngestion.Value.ToString("O", CultureInfo.InvariantCulture)
                        : (object)DBNull.Value);

                await docCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Replace chunk associations
            using (var deleteChunksCmd = _connection.CreateCommand())
            {
                deleteChunksCmd.Transaction = transaction;
                deleteChunksCmd.CommandText = @"
                    DELETE FROM ingestion_document_chunks
                    WHERE source_id = $source_id AND document_id = $document_id;
                ";
                deleteChunksCmd.Parameters.AddWithValue("$source_id", state.SourceId);
                deleteChunksCmd.Parameters.AddWithValue("$document_id", state.DocumentId);

                await deleteChunksCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (state.ChunkIds.Count > 0)
            {
                using var insertChunkCmd = _connection.CreateCommand();
                insertChunkCmd.Transaction = transaction;
                insertChunkCmd.CommandText = @"
                    INSERT INTO ingestion_document_chunks (source_id, document_id, chunk_id)
                    VALUES ($source_id, $document_id, $chunk_id);
                ";
                insertChunkCmd.Parameters.AddWithValue("$source_id", state.SourceId);
                insertChunkCmd.Parameters.AddWithValue("$document_id", state.DocumentId);
                var pChunkId = insertChunkCmd.Parameters.Add("$chunk_id", SqliteType.Text);

                foreach (var chunkId in state.ChunkIds)
                {
                    pChunkId.Value = chunkId;
                    await insertChunkCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            transaction.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(
        string documentId,
        string sourceId = "default",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction();

            using var deleteChunksCmd = _connection.CreateCommand();
            deleteChunksCmd.Transaction = transaction;
            deleteChunksCmd.CommandText = @"
                DELETE FROM ingestion_document_chunks
                WHERE source_id = $source_id AND document_id = $document_id;
            ";
            deleteChunksCmd.Parameters.AddWithValue("$source_id", sourceId);
            deleteChunksCmd.Parameters.AddWithValue("$document_id", documentId);
            await deleteChunksCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            using var deleteDocCmd = _connection.CreateCommand();
            deleteDocCmd.Transaction = transaction;
            deleteDocCmd.CommandText = @"
                DELETE FROM ingestion_documents
                WHERE source_id = $source_id AND document_id = $document_id;
            ";
            deleteDocCmd.Parameters.AddWithValue("$source_id", sourceId);
            deleteDocCmd.Parameters.AddWithValue("$document_id", documentId);
            await deleteDocCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            transaction.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _connection.Dispose();
        _lock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
