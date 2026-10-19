using System.Data;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;

namespace Pigmentor.VectorStore.SqliteVec;

/// <summary>
/// Persistent vector storage implementation using SQLite and native sqlite-vec (vec0).
/// </summary>
public sealed class SqliteVecStore : IVectorStore, IDisposable, IAsyncDisposable
{
    private readonly SqliteVecStoreOptions _options;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _dimensions;
    private bool _schemaInitialized;
    private bool _disposed;

    public SqliteVecStore(SqliteVecStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _dimensions = options.Dimensions;

        _connection = new SqliteConnection(_options.ConnectionString);
        _connection.Open();

        SqliteVecExtensionLoader.Load(_connection, _options.ExtensionPath);

        InitializeSchema();
    }

    public SqliteVecStore(
        string connectionString,
        int dimensions = 0,
        string? extensionPath = null,
        string? embeddingModel = null)
        : this(new SqliteVecStoreOptions
        {
            ConnectionString = connectionString,
            Dimensions = dimensions,
            ExtensionPath = extensionPath,
            EmbeddingModel = embeddingModel
        })
    {
    }

    public string CollectionName => _options.CollectionName;

    public int Dimensions => _dimensions;

    public Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // Schema is already created and validated in InitializeSchema
        return Task.CompletedTask;
    }

    public void LoadVectorExtension(string extensionPath)
    {
        ThrowIfDisposed();
        SqliteVecExtensionLoader.Load(_connection, extensionPath);
    }

    public async Task UpsertAsync(IEnumerable<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(chunks);

        var chunkList = chunks as IReadOnlyList<EmbeddedChunk> ?? chunks.ToList();
        if (chunkList.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_dimensions == 0)
            {
                EnsureVirtualTable(chunkList[0].Vector.Length, chunkList[0].EmbeddingModel);
            }

            foreach (var item in chunkList)
            {
                if (item.Vector.Length != _dimensions)
                {
                    throw new ArgumentException(
                        $"Vector dimensionality mismatch for chunk '{item.Chunk.ChunkId}': expected {_dimensions}, but got {item.Vector.Length}.",
                        nameof(chunks));
                }
            }

            using var transaction = _connection.BeginTransaction();

            using var upsertChunkCmd = _connection.CreateCommand();
            upsertChunkCmd.Transaction = transaction;
            upsertChunkCmd.CommandText = @"
                INSERT INTO chunks (
                    chunk_id, document_id, chunk_index, text, start_char_offset, end_char_offset,
                    silo, department, office, confidentiality_level, document_type, fiscal_year,
                    author, last_modified, title, embedding_model
                ) VALUES (
                    $chunk_id, $document_id, $chunk_index, $text, $start_char_offset, $end_char_offset,
                    $silo, $department, $office, $confidentiality_level, $document_type, $fiscal_year,
                    $author, $last_modified, $title, $embedding_model
                )
                ON CONFLICT(chunk_id) DO UPDATE SET
                    document_id = excluded.document_id,
                    chunk_index = excluded.chunk_index,
                    text = excluded.text,
                    start_char_offset = excluded.start_char_offset,
                    end_char_offset = excluded.end_char_offset,
                    silo = excluded.silo,
                    department = excluded.department,
                    office = excluded.office,
                    confidentiality_level = excluded.confidentiality_level,
                    document_type = excluded.document_type,
                    fiscal_year = excluded.fiscal_year,
                    author = excluded.author,
                    last_modified = excluded.last_modified,
                    title = excluded.title,
                    embedding_model = excluded.embedding_model
                RETURNING row_id;";

            var pChunkId = upsertChunkCmd.Parameters.Add("$chunk_id", SqliteType.Text);
            var pDocId = upsertChunkCmd.Parameters.Add("$document_id", SqliteType.Text);
            var pChunkIndex = upsertChunkCmd.Parameters.Add("$chunk_index", SqliteType.Integer);
            var pText = upsertChunkCmd.Parameters.Add("$text", SqliteType.Text);
            var pStartOffset = upsertChunkCmd.Parameters.Add("$start_char_offset", SqliteType.Integer);
            var pEndOffset = upsertChunkCmd.Parameters.Add("$end_char_offset", SqliteType.Integer);
            var pSilo = upsertChunkCmd.Parameters.Add("$silo", SqliteType.Text);
            var pDept = upsertChunkCmd.Parameters.Add("$department", SqliteType.Text);
            var pOffice = upsertChunkCmd.Parameters.Add("$office", SqliteType.Text);
            var pConf = upsertChunkCmd.Parameters.Add("$confidentiality_level", SqliteType.Text);
            var pDocType = upsertChunkCmd.Parameters.Add("$document_type", SqliteType.Text);
            var pFiscalYear = upsertChunkCmd.Parameters.Add("$fiscal_year", SqliteType.Integer);
            var pAuthor = upsertChunkCmd.Parameters.Add("$author", SqliteType.Text);
            var pLastMod = upsertChunkCmd.Parameters.Add("$last_modified", SqliteType.Text);
            var pTitle = upsertChunkCmd.Parameters.Add("$title", SqliteType.Text);
            var pEmbeddingModel = upsertChunkCmd.Parameters.Add("$embedding_model", SqliteType.Text);

            using var deleteVecCmd = _connection.CreateCommand();
            deleteVecCmd.Transaction = transaction;
            deleteVecCmd.CommandText = "DELETE FROM vec_chunks WHERE rowid = $row_id;";
            var pDeleteRowId = deleteVecCmd.Parameters.Add("$row_id", SqliteType.Integer);

            using var insertVecCmd = _connection.CreateCommand();
            insertVecCmd.Transaction = transaction;
            insertVecCmd.CommandText = "INSERT INTO vec_chunks(rowid, embedding) VALUES ($row_id, $embedding);";
            var pInsertRowId = insertVecCmd.Parameters.Add("$row_id", SqliteType.Integer);
            var pInsertEmbedding = insertVecCmd.Parameters.Add("$embedding", SqliteType.Blob);

            foreach (var item in chunkList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var c = item.Chunk;
                var m = c.Metadata;

                pChunkId.Value = c.ChunkId;
                pDocId.Value = c.DocumentId;
                pChunkIndex.Value = c.ChunkIndex;
                pText.Value = c.Text;
                pStartOffset.Value = c.StartCharOffset;
                pEndOffset.Value = c.EndCharOffset;
                pSilo.Value = m.Silo;
                pDept.Value = m.Department;
                pOffice.Value = m.Office;
                pConf.Value = m.ConfidentialityLevel;
                pDocType.Value = m.DocumentType;
                pFiscalYear.Value = m.FiscalYear;
                pAuthor.Value = m.Author;
                pLastMod.Value = m.LastModified.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                pTitle.Value = m.Title;
                pEmbeddingModel.Value = item.EmbeddingModel;

                var rowIdObj = await upsertChunkCmd.ExecuteScalarAsync(cancellationToken);
                var rowId = Convert.ToInt64(rowIdObj, System.Globalization.CultureInfo.InvariantCulture);

                // Update vec_chunks
                pDeleteRowId.Value = rowId;
                await deleteVecCmd.ExecuteNonQueryAsync(cancellationToken);

                pInsertRowId.Value = rowId;
                pInsertEmbedding.Value = MemoryMarshal.AsBytes(item.Vector.Span).ToArray();
                await insertVecCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        MetadataFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_dimensions > 0 && queryVector.Length != _dimensions)
            {
                throw new ArgumentException(
                    $"Query vector dimensionality mismatch: expected {_dimensions}, but got {queryVector.Length}.",
                    nameof(queryVector));
            }

            if (!_schemaInitialized || _dimensions == 0)
            {
                return Array.Empty<RetrievalResult>();
            }

            var (filterSql, filterParams) = SqliteFilterTranslator.Translate(filter?.Root);

            using var cmd = _connection.CreateCommand();

            if (!string.IsNullOrWhiteSpace(filterSql))
            {
                cmd.CommandText = $@"
                    WITH matches AS (
                        SELECT rowid, distance
                        FROM vec_chunks
                        WHERE rowid IN (SELECT row_id FROM chunks WHERE {filterSql})
                          AND embedding MATCH $query_vector
                          AND k = $topK
                    )
                    SELECT c.chunk_id, c.document_id, c.chunk_index, c.text, c.start_char_offset, c.end_char_offset,
                           c.silo, c.department, c.office, c.confidentiality_level, c.document_type, c.fiscal_year,
                           c.author, c.last_modified, c.title,
                           m.distance
                    FROM matches m
                    JOIN chunks c ON c.row_id = m.rowid
                    ORDER BY m.distance;";
            }
            else
            {
                cmd.CommandText = @"
                    WITH matches AS (
                        SELECT rowid, distance
                        FROM vec_chunks
                        WHERE embedding MATCH $query_vector
                          AND k = $topK
                    )
                    SELECT c.chunk_id, c.document_id, c.chunk_index, c.text, c.start_char_offset, c.end_char_offset,
                           c.silo, c.department, c.office, c.confidentiality_level, c.document_type, c.fiscal_year,
                           c.author, c.last_modified, c.title,
                           m.distance
                    FROM matches m
                    JOIN chunks c ON c.row_id = m.rowid
                    ORDER BY m.distance;";
            }

            cmd.Parameters.AddWithValue("$query_vector", MemoryMarshal.AsBytes(queryVector.Span).ToArray());
            cmd.Parameters.AddWithValue("$topK", topK);

            foreach (var p in filterParams)
            {
                cmd.Parameters.Add(p);
            }

            var results = new List<RetrievalResult>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var chunkId = reader.GetString(0);
                var docId = reader.GetString(1);
                var chunkIndex = reader.GetInt32(2);
                var text = reader.GetString(3);
                var startOffset = reader.GetInt32(4);
                var endOffset = reader.GetInt32(5);
                var silo = reader.GetString(6);
                var department = reader.GetString(7);
                var office = reader.GetString(8);
                var confLevel = reader.GetString(9);
                var docType = reader.GetString(10);
                var fiscalYear = reader.GetInt32(11);
                var author = reader.GetString(12);
                var lastModifiedStr = reader.GetString(13);
                var title = reader.GetString(14);
                var distance = reader.GetDouble(15);

                var lastModified = DateOnly.TryParse(lastModifiedStr, out var d) ? d : DateOnly.MinValue;

                var metadata = new DocumentMetadata(
                    Id: docId,
                    Silo: silo,
                    Department: department,
                    Office: office,
                    ConfidentialityLevel: confLevel,
                    DocumentType: docType,
                    FiscalYear: fiscalYear,
                    Author: author,
                    LastModified: lastModified,
                    Title: title);

                var chunk = new DocumentChunk(
                    ChunkId: chunkId,
                    DocumentId: docId,
                    ChunkIndex: chunkIndex,
                    Text: text,
                    StartCharOffset: startOffset,
                    EndCharOffset: endOffset,
                    Metadata: metadata);

                // sqlite-vec with cosine metric returns cosine distance (1.0 - cosine_similarity).
                // Higher score = better match in Pigmentor.
                var score = 1.0 - distance;

                results.Add(new RetrievalResult(chunk, score));
            }

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(IEnumerable<string> chunkIds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(chunkIds);

        var idList = chunkIds as IReadOnlyList<string> ?? chunkIds.ToList();
        if (idList.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();

            using var findRowIdCmd = _connection.CreateCommand();
            findRowIdCmd.Transaction = transaction;
            findRowIdCmd.CommandText = "SELECT row_id FROM chunks WHERE chunk_id = $chunk_id;";
            var pFindChunkId = findRowIdCmd.Parameters.Add("$chunk_id", SqliteType.Text);

            using var deleteVecCmd = _connection.CreateCommand();
            deleteVecCmd.Transaction = transaction;
            deleteVecCmd.CommandText = "DELETE FROM vec_chunks WHERE rowid = $row_id;";
            var pDeleteRowId = deleteVecCmd.Parameters.Add("$row_id", SqliteType.Integer);

            using var deleteChunkCmd = _connection.CreateCommand();
            deleteChunkCmd.Transaction = transaction;
            deleteChunkCmd.CommandText = "DELETE FROM chunks WHERE row_id = $row_id;";
            var pDeleteChunkRowId = deleteChunkCmd.Parameters.Add("$row_id", SqliteType.Integer);

            foreach (var chunkId in idList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                pFindChunkId.Value = chunkId;
                var rowIdObj = await findRowIdCmd.ExecuteScalarAsync(cancellationToken);
                if (rowIdObj != null && rowIdObj != DBNull.Value)
                {
                    var rowId = Convert.ToInt64(rowIdObj, System.Globalization.CultureInfo.InvariantCulture);

                    pDeleteRowId.Value = rowId;
                    await deleteVecCmd.ExecuteNonQueryAsync(cancellationToken);

                    pDeleteChunkRowId.Value = rowId;
                    await deleteChunkCmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            transaction.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _connection.Dispose();
        _lock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void InitializeSchema()
    {
        using (var pragmaCmd = _connection.CreateCommand())
        {
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                PRAGMA user_version = 1;
            ";
            pragmaCmd.ExecuteNonQuery();
        }

        using (var createCmd = _connection.CreateCommand())
        {
            createCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS pigmentor_metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS chunks (
                    row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    chunk_id TEXT UNIQUE NOT NULL,
                    document_id TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    start_char_offset INTEGER NOT NULL,
                    end_char_offset INTEGER NOT NULL,
                    silo TEXT NOT NULL,
                    department TEXT NOT NULL,
                    office TEXT NOT NULL,
                    confidentiality_level TEXT NOT NULL,
                    document_type TEXT NOT NULL,
                    fiscal_year INTEGER NOT NULL,
                    author TEXT NOT NULL,
                    last_modified TEXT NOT NULL,
                    title TEXT NOT NULL,
                    embedding_model TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_chunks_document_id ON chunks(document_id);
                CREATE INDEX IF NOT EXISTS idx_chunks_department ON chunks(department);
                CREATE INDEX IF NOT EXISTS idx_chunks_silo ON chunks(silo);
                CREATE INDEX IF NOT EXISTS idx_chunks_office ON chunks(office);
                CREATE INDEX IF NOT EXISTS idx_chunks_confidentiality ON chunks(confidentiality_level);
                CREATE INDEX IF NOT EXISTS idx_chunks_document_type ON chunks(document_type);
                CREATE INDEX IF NOT EXISTS idx_chunks_fiscal_year ON chunks(fiscal_year);
                CREATE INDEX IF NOT EXISTS idx_chunks_author ON chunks(author);
                CREATE INDEX IF NOT EXISTS idx_chunks_last_modified ON chunks(last_modified);
            ";
            createCmd.ExecuteNonQuery();
        }

        // Check if dimensions are recorded in metadata
        using (var metaCmd = _connection.CreateCommand())
        {
            metaCmd.CommandText = "SELECT value FROM pigmentor_metadata WHERE key = 'dimensions';";
            var existingDimObj = metaCmd.ExecuteScalar();
            if (existingDimObj != null && int.TryParse(existingDimObj.ToString(), out var existingDim))
            {
                if (_dimensions > 0 && _dimensions != existingDim)
                {
                    throw new InvalidOperationException(
                        $"Database dimension mismatch: database is configured for {existingDim} dimensions, but store requested {_dimensions}.");
                }

                _dimensions = existingDim;
                _schemaInitialized = true;
            }
        }

        if (_dimensions > 0)
        {
            EnsureVirtualTable(_dimensions, _options.EmbeddingModel);
        }
    }

    private void EnsureVirtualTable(int dimensions, string? embeddingModel)
    {
        if (_schemaInitialized && _dimensions == dimensions)
        {
            return;
        }

        _dimensions = dimensions;

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = $@"
                CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(
                    embedding float[{_dimensions}] distance_metric=cosine
                );

                INSERT INTO pigmentor_metadata(key, value) VALUES ('dimensions', '{_dimensions}')
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            ";

            if (!string.IsNullOrWhiteSpace(embeddingModel))
            {
                cmd.CommandText += $@"
                    INSERT INTO pigmentor_metadata(key, value) VALUES ('embedding_model', '{embeddingModel}')
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                ";
            }

            cmd.ExecuteNonQuery();
        }

        _schemaInitialized = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
