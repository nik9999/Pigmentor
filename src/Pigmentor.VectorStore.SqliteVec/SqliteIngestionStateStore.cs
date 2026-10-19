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

        PigmentorSqliteSchema.EnsureInitializedForStateStore(_connection);
    }

    public SqliteIngestionStateStore(SqliteVecStoreOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).ConnectionString)
    {
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

    public async Task<string?> GetSourceIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT source_id
                FROM ingestion_documents
                WHERE document_id = $document_id
                LIMIT 1;
            ";
            cmd.Parameters.AddWithValue("$document_id", documentId);

            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is null or DBNull ? null : result.ToString();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(
        IngestionDocumentState state,
        long? expectedVersion = null,
        IngestionStatus? expectedStatus = null,
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

            if (expectedVersion.HasValue && expectedVersion.Value == 0)
            {
                // Optimistic creation of a new document: INSERT without ON CONFLICT.
                // If a record with (source_id, document_id) already exists, Primary Key constraint violation occurs.
                try
                {
                    using var insertCmd = _connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = @"
                        INSERT INTO ingestion_documents (
                            source_id, document_id, content_hash, metadata_hash, version, status, last_successful_ingestion
                        ) VALUES (
                            $source_id, $document_id, $content_hash, $metadata_hash, $version, $status, $last_successful_ingestion
                        );
                    ";
                    insertCmd.Parameters.AddWithValue("$source_id", state.SourceId);
                    insertCmd.Parameters.AddWithValue("$document_id", state.DocumentId);
                    insertCmd.Parameters.AddWithValue("$content_hash", state.ContentHash);
                    insertCmd.Parameters.AddWithValue("$metadata_hash", state.MetadataHash);
                    insertCmd.Parameters.AddWithValue("$version", state.Version);
                    insertCmd.Parameters.AddWithValue("$status", state.Status.ToString());
                    insertCmd.Parameters.AddWithValue(
                        "$last_successful_ingestion",
                        state.LastSuccessfulIngestion.HasValue
                            ? state.LastSuccessfulIngestion.Value.ToString("O", CultureInfo.InvariantCulture)
                            : (object)DBNull.Value);

                    await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
                {
                    throw new IngestionConcurrencyException(
                        $"Optimistic concurrency violation: document '{state.DocumentId}' in scope '{state.SourceId}' already exists.",
                        ex);
                }
            }
            else if (expectedVersion.HasValue)
            {
                // Atomic CAS update of an existing document
                using var updateCmd = _connection.CreateCommand();
                updateCmd.Transaction = transaction;

                var sql = @"
                    UPDATE ingestion_documents
                    SET content_hash = $content_hash,
                        metadata_hash = $metadata_hash,
                        version = $version,
                        status = $status,
                        last_successful_ingestion = $last_successful_ingestion
                    WHERE source_id = $source_id
                      AND document_id = $document_id
                      AND version = $expected_version";

                if (expectedStatus.HasValue)
                {
                    sql += " AND status = $expected_status";
                    updateCmd.Parameters.AddWithValue("$expected_status", expectedStatus.Value.ToString());
                }

                sql += ";";

                updateCmd.CommandText = sql;
                updateCmd.Parameters.AddWithValue("$source_id", state.SourceId);
                updateCmd.Parameters.AddWithValue("$document_id", state.DocumentId);
                updateCmd.Parameters.AddWithValue("$content_hash", state.ContentHash);
                updateCmd.Parameters.AddWithValue("$metadata_hash", state.MetadataHash);
                updateCmd.Parameters.AddWithValue("$version", state.Version);
                updateCmd.Parameters.AddWithValue("$status", state.Status.ToString());
                updateCmd.Parameters.AddWithValue(
                    "$last_successful_ingestion",
                    state.LastSuccessfulIngestion.HasValue
                        ? state.LastSuccessfulIngestion.Value.ToString("O", CultureInfo.InvariantCulture)
                        : (object)DBNull.Value);
                updateCmd.Parameters.AddWithValue("$expected_version", expectedVersion.Value);

                var rows = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (rows == 0)
                {
                    throw new IngestionConcurrencyException(
                        $"Optimistic concurrency violation: document '{state.DocumentId}' in scope '{state.SourceId}' was modified or claimed concurrently (expected version {expectedVersion.Value}, status {expectedStatus?.ToString() ?? "any"}).");
                }
            }
            else
            {
                // Unconditional upsert (when expectedVersion is null)
                using var docCmd = _connection.CreateCommand();
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
