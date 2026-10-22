using System.Data;
using System.Globalization;
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
    public const int CurrentSchemaVersion = 2;

    private readonly SqliteVecStoreOptions _options;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly int _dimensions;
    private readonly string _embeddingModel;
    private bool _disposed;

    public SqliteVecStore(SqliteVecStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (_options.Dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.Dimensions,
                "Vector dimensions must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(_options.EmbeddingModel))
        {
            throw new ArgumentException(
                "EmbeddingModel must be specified and non-empty in SqliteVecStoreOptions.",
                nameof(options));
        }

        _dimensions = _options.Dimensions;
        _embeddingModel = _options.EmbeddingModel;

        // 1. Ensure SQLite native provider runtime is initialized BEFORE opening connection
        SqliteVecExtensionLoader.EnsureSqliteProvider();

        // 2. Open connection
        _connection = new SqliteConnection(_options.ConnectionString);
        _connection.Open();

        // 3. Load & verify native sqlite-vec extension
        SqliteVecExtensionLoader.Load(_connection, _options.ExtensionPath);

        // 4. Initialize and validate database schema and metadata
        InitializeSchema();
    }

    public SqliteVecStore(
        string connectionString,
        int dimensions,
        string embeddingModel,
        string? extensionPath = null)
        : this(new SqliteVecStoreOptions
        {
            ConnectionString = connectionString,
            Dimensions = dimensions,
            EmbeddingModel = embeddingModel,
            ExtensionPath = extensionPath
        })
    {
    }

    public string CollectionName => _options.CollectionName;

    public int Dimensions => _dimensions;

    public string EmbeddingModel => _embeddingModel;

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

        // Enforce invariants on the entire batch BEFORE acquiring transaction lock or executing writes
        foreach (var item in chunkList)
        {
            if (item.Vector.Length != _dimensions)
            {
                throw new ArgumentException(
                    $"Vector dimensionality mismatch for chunk '{item.Chunk.ChunkId}': expected {_dimensions}, but got {item.Vector.Length}.",
                    nameof(chunks));
            }

            if (!string.Equals(item.EmbeddingModel, _embeddingModel, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Embedding model mismatch for chunk '{item.Chunk.ChunkId}': expected '{_embeddingModel}', but got '{item.EmbeddingModel}'.",
                    nameof(chunks));
            }
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
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
                pLastMod.Value = m.LastModified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                pTitle.Value = m.Title;
                pEmbeddingModel.Value = item.EmbeddingModel;

                var rowIdObj = await upsertChunkCmd.ExecuteScalarAsync(cancellationToken);
                var rowId = Convert.ToInt64(rowIdObj, CultureInfo.InvariantCulture);

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

        if (queryVector.Length != _dimensions)
        {
            throw new ArgumentException(
                $"Query vector dimensionality mismatch: expected {_dimensions}, but got {queryVector.Length}.",
                nameof(queryVector));
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var (filterSql, filterParams) = SqliteFilterTranslator.Translate(filter?.Root);

            using var cmd = _connection.CreateCommand();

            if (!string.IsNullOrWhiteSpace(filterSql))
            {
                cmd.CommandText = $@"
                    WITH matches AS (
                        SELECT rowid, distance, embedding
                        FROM vec_chunks
                        WHERE rowid IN (SELECT row_id FROM chunks WHERE {filterSql})
                          AND embedding MATCH $query_vector
                          AND k = $topK
                    )
                    SELECT c.chunk_id, c.document_id, c.chunk_index, c.text, c.start_char_offset, c.end_char_offset,
                           c.silo, c.department, c.office, c.confidentiality_level, c.document_type, c.fiscal_year,
                           c.author, c.last_modified, c.title,
                           m.distance, m.embedding
                    FROM matches m
                    JOIN chunks c ON c.row_id = m.rowid
                    ORDER BY m.distance;";
            }
            else
            {
                cmd.CommandText = @"
                    WITH matches AS (
                        SELECT rowid, distance, embedding
                        FROM vec_chunks
                        WHERE embedding MATCH $query_vector
                          AND k = $topK
                    )
                    SELECT c.chunk_id, c.document_id, c.chunk_index, c.text, c.start_char_offset, c.end_char_offset,
                           c.silo, c.department, c.office, c.confidentiality_level, c.document_type, c.fiscal_year,
                           c.author, c.last_modified, c.title,
                           m.distance, m.embedding
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
                ReadOnlyMemory<float>? vector = null;
                if (!reader.IsDBNull(16))
                {
                    var bytes = (byte[])reader.GetValue(16);
                    if (bytes != null && bytes.Length > 0)
                    {
                        var floats = new float[bytes.Length / sizeof(float)];
                        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
                        vector = floats;
                    }
                }

                var lastModified = DateOnly.TryParse(lastModifiedStr, CultureInfo.InvariantCulture, out var d) ? d : DateOnly.MinValue;

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

                results.Add(new RetrievalResult(chunk, score)
                {
                    DenseScore = score,
                    Vector = vector
                });
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
            ThrowIfDisposed();
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
                    var rowId = Convert.ToInt64(rowIdObj, CultureInfo.InvariantCulture);

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

    public async Task<IReadOnlyList<DocumentChunk>> GetAllChunksAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT chunk_id, document_id, chunk_index, text, start_char_offset, end_char_offset,
                                       silo, department, office, confidentiality_level, document_type, fiscal_year,
                                       author, last_modified, title
                                FROM chunks ORDER BY row_id ASC;";

            var list = new List<DocumentChunk>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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

                var lastModified = DateOnly.TryParse(lastModifiedStr, CultureInfo.InvariantCulture, out var d) ? d : DateOnly.MinValue;

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

                list.Add(new DocumentChunk(
                    ChunkId: chunkId,
                    DocumentId: docId,
                    ChunkIndex: chunkIndex,
                    Text: text,
                    StartCharOffset: startOffset,
                    EndCharOffset: endOffset,
                    Metadata: metadata));
            }

            return list;
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

        _lock.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
        finally
        {
            _lock.Release();
        }
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
            ";
            pragmaCmd.ExecuteNonQuery();
        }

        PigmentorSqliteSchema.EnsureInitializedForVectorStore(_connection, _dimensions, _embeddingModel);
        ValidateExistingMetadata();
    }

    private void ValidateExistingMetadata()
    {

        // 2. Validate dimensions
        using (var metaCmd = _connection.CreateCommand())
        {
            metaCmd.CommandText = "SELECT value FROM pigmentor_metadata WHERE key = 'dimensions';";
            var existingDimObj = metaCmd.ExecuteScalar();
            if (existingDimObj == null || !int.TryParse(existingDimObj.ToString(), out var existingDim))
            {
                throw new InvalidOperationException("Incompatible SQLite schema: missing or invalid 'dimensions' metadata.");
            }

            if (_dimensions != existingDim)
            {
                throw new InvalidOperationException(
                    $"Database dimension mismatch: database is configured for {existingDim} dimensions, but store requested {_dimensions}.");
            }
        }

        // 3. Validate embedding model
        using (var modelCmd = _connection.CreateCommand())
        {
            modelCmd.CommandText = "SELECT value FROM pigmentor_metadata WHERE key = 'embedding_model';";
            var existingModelObj = modelCmd.ExecuteScalar();
            var existingModel = existingModelObj?.ToString();
            if (string.IsNullOrWhiteSpace(existingModel))
            {
                throw new InvalidOperationException("Incompatible SQLite schema: missing or invalid 'embedding_model' metadata.");
            }

            if (!string.Equals(existingModel, _embeddingModel, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Database embedding model mismatch: database is configured for model '{existingModel}', but store requested '{_embeddingModel}'.");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
