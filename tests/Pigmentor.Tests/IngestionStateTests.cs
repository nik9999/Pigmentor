#pragma warning disable CA1861 // Prefer 'static readonly' fields over constant array arguments
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Ingestion.Chunking;
using Pigmentor.Ingestion.DependencyInjection;
using Pigmentor.Ingestion.Events;
using Pigmentor.Ingestion.Fingerprints;
using Pigmentor.Ingestion.Lifecycle;
using Pigmentor.Ingestion.Services;
using Pigmentor.VectorStore.SqliteVec;
using Xunit;

namespace Pigmentor.Tests;

public sealed class IngestionStateTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly string _connectionString;

    public IngestionStateTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"pigmentor_ingest_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_tempDbPath}";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }
            var walFile = _tempDbPath + "-wal";
            if (File.Exists(walFile))
            {
                File.Delete(walFile);
            }
            var shmFile = _tempDbPath + "-shm";
            if (File.Exists(shmFile))
            {
                File.Delete(shmFile);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static Document CreateDocument(
        string id,
        string content,
        string department = "Engineering",
        string title = "Title",
        int fiscalYear = 2024)
    {
        var meta = new DocumentMetadata(
            Id: id,
            Silo: "technical-docs",
            Department: department,
            Office: "Montreal",
            ConfidentialityLevel: "Internal",
            DocumentType: "Reference",
            FiscalYear: fiscalYear,
            Author: "Author",
            LastModified: new DateOnly(2024, 6, 1),
            Title: title);

        return new Document(meta, content, $"{id}.md");
    }

    // --- 1. Fingerprint Unit Tests ---

    [Fact]
    public void Fingerprint_DeterministicAndDistinct()
    {
        var doc1 = CreateDocument("doc1", "Hello World");
        var doc2 = CreateDocument("doc1", "Hello World");
        var doc3 = CreateDocument("doc1", "Different Content");

        var h1 = IngestionFingerprint.ComputeContentHash(doc1.Content);
        var h2 = IngestionFingerprint.ComputeContentHash(doc2.Content);
        var h3 = IngestionFingerprint.ComputeContentHash(doc3.Content);

        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, h3);
        Assert.Equal(64, h1.Length);

        var m1 = IngestionFingerprint.ComputeMetadataHash(doc1.Metadata);
        var m2 = IngestionFingerprint.ComputeMetadataHash(doc2.Metadata);
        Assert.Equal(m1, m2);

        var docModifiedMeta = doc1 with { Metadata = doc1.Metadata with { Department = "Legal" } };
        var m3 = IngestionFingerprint.ComputeMetadataHash(docModifiedMeta.Metadata);
        Assert.NotEqual(m1, m3);
    }

    // --- 2. DocumentChangeClassifier Unit Tests ---

    [Fact]
    public void DocumentChangeClassifier_ClassifiesAccurately()
    {
        var cHash1 = "content-hash-1";
        var cHash2 = "content-hash-2";
        var mHash1 = "meta-hash-1";
        var mHash2 = "meta-hash-2";

        // Null state => New
        Assert.Equal(DocumentChangeKind.New, DocumentChangeClassifier.Classify(null, cHash1, mHash1));

        // Interrupted/Failed state => New (repair invariant)
        var processingState = new IngestionDocumentState("default", "d1", cHash1, mHash1, 1, IngestionStatus.Processing, null, []);
        Assert.Equal(DocumentChangeKind.New, DocumentChangeClassifier.Classify(processingState, cHash1, mHash1));

        var failedState = new IngestionDocumentState("default", "d1", cHash1, mHash1, 1, IngestionStatus.Failed, null, []);
        Assert.Equal(DocumentChangeKind.New, DocumentChangeClassifier.Classify(failedState, cHash1, mHash1));

        // Succeeded state
        var succeededState = new IngestionDocumentState("default", "d1", cHash1, mHash1, 1, IngestionStatus.Succeeded, DateTimeOffset.UtcNow, ["c1"]);

        // Same content and metadata => Unchanged
        Assert.Equal(DocumentChangeKind.Unchanged, DocumentChangeClassifier.Classify(succeededState, cHash1, mHash1));

        // Content changed => ContentChanged
        Assert.Equal(DocumentChangeKind.ContentChanged, DocumentChangeClassifier.Classify(succeededState, cHash2, mHash1));

        // Metadata changed => MetadataChanged
        Assert.Equal(DocumentChangeKind.MetadataChanged, DocumentChangeClassifier.Classify(succeededState, cHash1, mHash2));

        // Both changed => ContentAndMetadataChanged
        Assert.Equal(DocumentChangeKind.ContentAndMetadataChanged, DocumentChangeClassifier.Classify(succeededState, cHash2, mHash2));
    }

    // --- 3. SqliteIngestionStateStore CRUD & Concurrency ---

    [Fact]
    public async Task SqliteIngestionStateStore_CrudAndChunks_PersistAccurately()
    {
        using var store = new SqliteIngestionStateStore(_connectionString);

        var state = new IngestionDocumentState(
            SourceId: "tenant-a",
            DocumentId: "doc-100",
            ContentHash: "hash-c",
            MetadataHash: "hash-m",
            Version: 1,
            Status: IngestionStatus.Succeeded,
            LastSuccessfulIngestion: DateTimeOffset.UtcNow,
            ChunkIds: ["doc-100#0", "doc-100#1", "doc-100#2"]);

        await store.SaveAsync(state);

        var retrieved = await store.GetAsync("doc-100", "tenant-a");
        Assert.NotNull(retrieved);
        Assert.Equal("tenant-a", retrieved.SourceId);
        Assert.Equal("doc-100", retrieved.DocumentId);
        Assert.Equal("hash-c", retrieved.ContentHash);
        Assert.Equal("hash-m", retrieved.MetadataHash);
        Assert.Equal(1, retrieved.Version);
        Assert.Equal(IngestionStatus.Succeeded, retrieved.Status);
        Assert.Equal(3, retrieved.ChunkIds.Count);
        Assert.Equal("doc-100#0", retrieved.ChunkIds[0]);
        Assert.Equal("doc-100#1", retrieved.ChunkIds[1]);
        Assert.Equal("doc-100#2", retrieved.ChunkIds[2]);

        // List
        var list = await store.ListAsync("tenant-a");
        Assert.Single(list);
        Assert.Equal("doc-100", list[0].DocumentId);
        Assert.Equal(3, list[0].ChunkIds.Count);

        // Delete
        await store.DeleteAsync("doc-100", "tenant-a");
        var afterDelete = await store.GetAsync("doc-100", "tenant-a");
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task SqliteIngestionStateStore_OptimisticConcurrency_ThrowsOnViolation()
    {
        using var store = new SqliteIngestionStateStore(_connectionString);

        var state1 = new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-concurrency",
            ContentHash: "h1",
            MetadataHash: "m1",
            Version: 1,
            Status: IngestionStatus.Succeeded,
            LastSuccessfulIngestion: DateTimeOffset.UtcNow,
            ChunkIds: ["c1"]);

        // expectedVersion = 0 on new doc -> succeeds
        await store.SaveAsync(state1, expectedVersion: 0);

        // expectedVersion = 0 when already exists -> throws
        await Assert.ThrowsAsync<IngestionConcurrencyException>(() => store.SaveAsync(state1, expectedVersion: 0));

        // expectedVersion = 99 (mismatch) -> throws
        await Assert.ThrowsAsync<IngestionConcurrencyException>(() => store.SaveAsync(state1 with { Version = 2 }, expectedVersion: 99));

        // expectedVersion = 1 (matching) -> succeeds
        await store.SaveAsync(state1 with { Version = 2 }, expectedVersion: 1);

        var final = await store.GetAsync("doc-concurrency", "default");
        Assert.NotNull(final);
        Assert.Equal(2, final.Version);
    }

    // --- 4. Schema Migration v1 -> v2 ---

    [Fact]
    public async Task SchemaMigration_V1ToV2_PreservesExistingVectorsAndAddsIngestionTables()
    {
        // 1. Manually bootstrap a v1 schema database with an indexed vector
        SqliteVecExtensionLoader.EnsureSqliteProvider();
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            SqliteVecExtensionLoader.Load(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE pigmentor_metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                INSERT INTO pigmentor_metadata (key, value) VALUES ('dimensions', '2'), ('embedding_model', 'test-model');

                CREATE TABLE chunks (
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

                CREATE VIRTUAL TABLE vec_chunks USING vec0(
                    embedding float[2] distance_metric=cosine
                );
                PRAGMA user_version = 1;

                INSERT INTO chunks (
                    row_id, chunk_id, document_id, chunk_index, text, start_char_offset, end_char_offset,
                    silo, department, office, confidentiality_level, document_type, fiscal_year, author,
                    last_modified, title, embedding_model
                ) VALUES (
                    1, 'v1-chunk', 'v1-doc', 0, 'V1 content', 0, 10,
                    'technical-docs', 'Engineering', 'Montreal', 'Internal', 'Reference', 2024, 'Author',
                    '2024-06-01', 'V1 Title', 'test-model'
                );
            ";
            cmd.ExecuteNonQuery();

            // Insert vector into vec_chunks
            using var vecCmd = conn.CreateCommand();
            vecCmd.CommandText = "INSERT INTO vec_chunks (rowid, embedding) VALUES (1, $vec);";
            var vecBytes = new byte[sizeof(float) * 2];
            Buffer.BlockCopy(new float[] { 1f, 0f }, 0, vecBytes, 0, vecBytes.Length);
            vecCmd.Parameters.AddWithValue("$vec", vecBytes);
            vecCmd.ExecuteNonQuery();
        }

        // 2. Open with SqliteVecStore (which will detect v1 and migrate to v2)
        using (var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-model"
        }))
        {
            // Verify search works on the pre-existing v1 data
            var results = await store.SearchAsync(new float[] { 1f, 0f }, topK: 1);
            Assert.Single(results);
            Assert.Equal("v1-chunk", results[0].Chunk.ChunkId);
            Assert.Equal("V1 content", results[0].Chunk.Text);
        }

        // 3. Verify user_version is now 2
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt64(checkCmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(2, version);
        }

        // 4. Verify SqliteIngestionStateStore operates on the migrated database
        using (var stateStore = new SqliteIngestionStateStore(_connectionString))
        {
            var st = new IngestionDocumentState("default", "v1-doc", "chash", "mhash", 1, IngestionStatus.Succeeded, DateTimeOffset.UtcNow, ["v1-chunk"]);
            await stateStore.SaveAsync(st);
            var readBack = await stateStore.GetAsync("v1-doc");
            Assert.NotNull(readBack);
            Assert.Equal("v1-chunk", readBack.ChunkIds[0]);
        }
    }

    // --- 5. High-Level DocumentIngestionService Lifecycle Tests ---

    [Fact]
    public async Task DocumentIngestionService_NewDocument_IngestedAndPersisted()
    {
        var options = new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" };
        using var vectorStore = new SqliteVecStore(options);
        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        var chunker = new FixedSizeChunker(50, 0);
        var embedder = new TestEmbeddingService(dimensions: 2, model: "test-model");
        var service = new DocumentIngestionService(stateStore, vectorStore, chunker, embedder);

        var doc = CreateDocument("doc-1", "This is a paragraph of content that fits in a single chunk.");
        var result = await service.IngestAsync(doc);

        Assert.Equal("doc-1", result.DocumentId);
        Assert.Equal(DocumentChangeKind.New, result.ChangeKind);
        Assert.Equal(IngestionStatus.Succeeded, result.Status);
        Assert.Equal(1, result.Version);
        Assert.False(result.Skipped);
        Assert.True(result.ChunksIndexed > 0);
        Assert.Equal(0, result.ChunksDeleted);

        // Verify state is persisted
        var persisted = await stateStore.GetAsync("doc-1");
        Assert.NotNull(persisted);
        Assert.Equal(1, persisted.Version);
        Assert.Equal(IngestionStatus.Succeeded, persisted.Status);
        Assert.NotEmpty(persisted.ChunkIds);

        // Verify vector store can search
        var search = await vectorStore.SearchAsync(new float[] { 1f, 0f }, topK: 5);
        Assert.NotEmpty(search);
        Assert.Equal("doc-1", search[0].Chunk.DocumentId);
    }

    [Fact]
    public async Task DocumentIngestionService_RestartSafe_UnchangedDocument_SkippedWithoutEmbedding()
    {
        var doc = CreateDocument("doc-restart", "Deterministic content for restart testing.");

        // First run: ingest
        {
            using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
            using var stateStore = new SqliteIngestionStateStore(_connectionString);
            var embedder = new TestEmbeddingService(2, "test-model");
            var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), embedder);

            var res1 = await service.IngestAsync(doc);
            Assert.False(res1.Skipped);
            Assert.True(embedder.CallCount > 0);
        }

        // Restart simulation: fresh store instances, empty process memory
        {
            using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
            using var stateStore = new SqliteIngestionStateStore(_connectionString);
            var embedder = new TestEmbeddingService(2, "test-model");
            var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), embedder);

            var res2 = await service.IngestAsync(doc);

            // Verified: classified as Unchanged, skipped, 0 new embedding calls
            Assert.True(res2.Skipped);
            Assert.Equal(DocumentChangeKind.Unchanged, res2.ChangeKind);
            Assert.Equal(0, embedder.CallCount);
            Assert.Equal(1, res2.Version);

            // Vectors still exist and searchable
            var search = await vectorStore.SearchAsync(new float[] { 1f, 0f }, topK: 1);
            Assert.Single(search);
            Assert.Equal("doc-restart", search[0].Chunk.DocumentId);
        }
    }

    [Fact]
    public async Task DocumentIngestionService_ContentChanged_ReplacesChunksAndIncrementsVersion()
    {
        var docV1 = CreateDocument("doc-edit", "Initial content v1.");
        var docV2 = CreateDocument("doc-edit", "Completely rewritten content v2.");

        // Run v1
        {
            using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
            using var stateStore = new SqliteIngestionStateStore(_connectionString);
            var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), new TestEmbeddingService(2, "test-model"));
            var res1 = await service.IngestAsync(docV1);
            Assert.Equal(1, res1.Version);
        }

        // Run v2 after restart
        {
            using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
            using var stateStore = new SqliteIngestionStateStore(_connectionString);
            var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), new TestEmbeddingService(2, "test-model"));

            var res2 = await service.IngestAsync(docV2);
            Assert.False(res2.Skipped);
            Assert.Equal(DocumentChangeKind.ContentChanged, res2.ChangeKind);
            Assert.Equal(2, res2.Version);

            var search = await vectorStore.SearchAsync(new float[] { 1f, 0f }, topK: 5);
            Assert.Single(search);
            Assert.Equal("Completely rewritten content v2.", search[0].Chunk.Text);
        }
    }

    [Fact]
    public async Task DocumentIngestionService_ShrinkingDocument_EliminatesOrphanChunks()
    {
        // 3 chunks when split by 10 chars
        var docBig = CreateDocument("doc-shrink", "1234567890abcdefghijABCDEFGHIJ");
        // 1 chunk
        var docSmall = CreateDocument("doc-shrink", "short");

        // Index large document
        {
            using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
            using var stateStore = new SqliteIngestionStateStore(_connectionString);
            var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(10, 0), new TestEmbeddingService(2, "test-model"));
            var res1 = await service.IngestAsync(docBig);
            Assert.Equal(3, res1.ChunksIndexed);
        }

        // Restart and update with shrinking document
        {
            using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
            using var stateStore = new SqliteIngestionStateStore(_connectionString);
            var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(10, 0), new TestEmbeddingService(2, "test-model"));

            var res2 = await service.IngestAsync(docSmall);
            Assert.Equal(1, res2.ChunksIndexed);
            Assert.Equal(3, res2.ChunksDeleted);

            // Vector store should now only contain exactly 1 chunk
            var search = await vectorStore.SearchAsync(new float[] { 1f, 0f }, topK: 10);
            Assert.Single(search);
            Assert.Equal("short", search[0].Chunk.Text);

            // State store chunk list matches
            var st = await stateStore.GetAsync("doc-shrink");
            Assert.NotNull(st);
            Assert.Single(st.ChunkIds);
        }
    }

    [Fact]
    public async Task DocumentIngestionService_MetadataChanged_UpdatesChunksInVectorStore()
    {
        var docMeta1 = CreateDocument("doc-meta", "Stable content", department: "Engineering");
        var docMeta2 = CreateDocument("doc-meta", "Stable content", department: "Finance");

        using (var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" }))
        using (var stateStore = new SqliteIngestionStateStore(_connectionString))
        {
            var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), new TestEmbeddingService(2, "test-model"));
            await service.IngestAsync(docMeta1);

            // Search with Engineering filter -> finds 1
            var filterEng = MetadataFilter.Where(m => m.Department == "Engineering");
            var resEng = await vectorStore.SearchAsync(new float[] { 1f, 0f }, topK: 5, filter: filterEng);
            Assert.Single(resEng);

            // Update metadata to Finance
            var updateResult = await service.IngestAsync(docMeta2);
            Assert.Equal(DocumentChangeKind.MetadataChanged, updateResult.ChangeKind);

            // Search with Engineering filter -> 0
            var resEngAfter = await vectorStore.SearchAsync(new float[] { 1f, 0f }, topK: 5, filter: filterEng);
            Assert.Empty(resEngAfter);

            // Search with Finance filter -> 1
            var filterFin = MetadataFilter.Where(m => m.Department == "Finance");
            var resFinAfter = await vectorStore.SearchAsync(new float[] { 1f, 0f }, topK: 5, filter: filterFin);
            Assert.Single(resFinAfter);
        }
    }

    [Fact]
    public async Task DocumentIngestionService_SyncAsync_ReconcilesSourceAndDeletesStale()
    {
        var docA = CreateDocument("doc-A", "Doc A content");
        var docB = CreateDocument("doc-B", "Doc B content");
        var docC = CreateDocument("doc-C", "Doc C content");

        using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
        using var stateStore = new SqliteIngestionStateStore(_connectionString);
        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), new TestEmbeddingService(2, "test-model"));

        // Initial sync with docA and docB
        var sync1 = await service.SyncAsync([docA, docB]);
        Assert.Equal(2, sync1.Ingested);
        Assert.Equal(0, sync1.Deleted);

        // Next sync with docB (unchanged) and docC (new). docA is missing from source!
        var sync2 = await service.SyncAsync([docB, docC]);
        Assert.Equal(1, sync2.Ingested); // docC
        Assert.Equal(1, sync2.Skipped);  // docB
        Assert.Equal(1, sync2.Deleted);  // docA was deleted

        // docA is gone from state store and vector store
        var stateA = await stateStore.GetAsync("doc-A");
        Assert.Null(stateA);

        var searchA = await vectorStore.SearchAsync(new float[] { 1f, 0f }, topK: 10, filter: MetadataFilter.Where(m => m.Id == "doc-A"));
        Assert.Empty(searchA);

        // docB and docC exist
        var stateB = await stateStore.GetAsync("doc-B");
        var stateC = await stateStore.GetAsync("doc-C");
        Assert.NotNull(stateB);
        Assert.NotNull(stateC);
    }

    [Fact]
    public async Task DocumentIngestionService_InterruptedIngestion_RecoversSafelyOnNextRun()
    {
        using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        // Simulate an interrupted ingestion state in DB
        var interrupted = new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-interrupted",
            ContentHash: "hash-c",
            MetadataHash: "hash-m",
            Version: 1,
            Status: IngestionStatus.Processing, // Left in Processing
            LastSuccessfulIngestion: null,
            ChunkIds: []);
        await stateStore.SaveAsync(interrupted);

        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), new TestEmbeddingService(2, "test-model"));

        var doc = CreateDocument("doc-interrupted", "Recovered content");
        var res = await service.IngestAsync(doc);

        Assert.Equal(DocumentChangeKind.New, res.ChangeKind); // Correctly treated as New to ensure complete repair
        Assert.Equal(IngestionStatus.Succeeded, res.Status);

        var state = await stateStore.GetAsync("doc-interrupted");
        Assert.NotNull(state);
        Assert.Equal(IngestionStatus.Succeeded, state.Status);
    }

    // --- 6. DocumentReingestService and IngestEventConsumer Restart Safety ---

    [Fact]
    public async Task DocumentReingestService_RestartSafe_CleansOrphansUsingPersistentState()
    {
        var docId = "reingest-doc";
        using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        // Step 1: Pre-populate persistent state with 3 chunks
        var meta = new DocumentMetadata(docId, "silo", "dept", "off", "conf", "type", 2024, "auth", new DateOnly(2024, 1, 1), "title");
        var c0 = new EmbeddedChunk(new DocumentChunk($"{docId}#0", docId, 0, "c0", 0, 2, meta), new float[] { 1f, 0f }, "test-model");
        var c1 = new EmbeddedChunk(new DocumentChunk($"{docId}#1", docId, 1, "c1", 2, 4, meta), new float[] { 1f, 0f }, "test-model");
        var c2 = new EmbeddedChunk(new DocumentChunk($"{docId}#2", docId, 2, "c2", 4, 6, meta), new float[] { 1f, 0f }, "test-model");
        await vectorStore.UpsertAsync([c0, c1, c2]);

        await stateStore.SaveAsync(new IngestionDocumentState("default", docId, "h", "m", 1, IngestionStatus.Succeeded, DateTimeOffset.UtcNow, [$"{docId}#0", $"{docId}#1", $"{docId}#2"]));

        // Step 2: Fresh DocumentReingestService instance (no in-memory cache seeded)
        var newChunks = new List<DocumentChunk>
        {
            new($"{docId}#0", docId, 0, "new single chunk", 0, 16, meta)
        };

        var reingestService = new DocumentReingestService(
            rechunk: (_, _) => Task.FromResult<IReadOnlyList<DocumentChunk>>(newChunks),
            embedder: new TestEmbeddingService(2, "test-model"),
            store: vectorStore,
            stateStore: stateStore);

        await reingestService.ReingestAsync(docId);

        // Verified: orphan chunks #1 and #2 were deleted via state store lookup!
        var allResults = await vectorStore.SearchAsync(new float[] { 1f, 0f }, topK: 10);
        Assert.Single(allResults);
        Assert.Equal($"{docId}#0", allResults[0].Chunk.ChunkId);

        var updatedState = await stateStore.GetAsync(docId);
        Assert.NotNull(updatedState);
        Assert.Single(updatedState.ChunkIds);
        Assert.Equal(2, updatedState.Version);
    }

    [Fact]
    public async Task IngestEventConsumer_RestartSafe_DropsStaleVersionUsingPersistentState()
    {
        var docId = "consumer-doc";
        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        // Persist document at version 3
        await stateStore.SaveAsync(new IngestionDocumentState(
            SourceId: "default",
            DocumentId: docId,
            ContentHash: "h",
            MetadataHash: "m",
            Version: 3,
            Status: IngestionStatus.Succeeded,
            LastSuccessfulIngestion: DateTimeOffset.UtcNow,
            ChunkIds: ["c1"]));

        var reingestCalled = false;
        var reingestMock = new ActionReingestService(() => reingestCalled = true);

        // Fresh IngestEventConsumer instance (empty in-process _lastVersion dictionary)
        var feed = new ChannelChangeFeed();
        var consumer = new IngestEventConsumer(
            feed: feed,
            reingest: reingestMock,
            invalidateCache: (_, _) => Task.CompletedTask,
            logger: NullLogger<IngestEventConsumer>.Instance,
            tenantId: "default",
            stateStore: stateStore);

        // Event arrives with version 2 (stale because version 3 was already processed before restart)
        var staleHandled = await consumer.ProcessAsync(new DocumentChanged(docId, "default", Version: 2));
        Assert.False(staleHandled);
        Assert.False(reingestCalled);

        // Event arrives with version 3 (duplicate delivery)
        var dupHandled = await consumer.ProcessAsync(new DocumentChanged(docId, "default", Version: 3));
        Assert.False(dupHandled);
        Assert.False(reingestCalled);

        // Event arrives with version 4 (new)
        var newHandled = await consumer.ProcessAsync(new DocumentChanged(docId, "default", Version: 4));
        Assert.True(newHandled);
        Assert.True(reingestCalled);
    }

    // --- 7. Dependency Injection Registration Test ---

    [Fact]
    public void DependencyInjection_RegistersAndResolvesIngestionServices()
    {
        var services = new ServiceCollection();
        services.AddSqliteVecStore(opts =>
        {
            opts.ConnectionString = _connectionString;
            opts.Dimensions = 2;
            opts.EmbeddingModel = "test-model";
        });
        services.AddSqliteIngestionStateStore();
        services.AddSingleton<IChunker>(new FixedSizeChunker(100, 0));
        services.AddSingleton<IEmbeddingService>(new TestEmbeddingService(2, "test-model"));
        services.AddDocumentIngestionService();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IVectorStore>());
        Assert.NotNull(provider.GetService<IIngestionStateStore>());
        Assert.NotNull(provider.GetService<IDocumentIngestionService>());
    }

    // --- 8. Concurrency & CAS Integrity Tests ---

    [Fact]
    public async Task ConcurrentUpdate_OnlyOneWriterClaimsNextVersion()
    {
        using var store = new SqliteIngestionStateStore(_connectionString);

        // Pre-populate document at Version 1, Succeeded
        var initial = new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-race-update",
            ContentHash: "h1",
            MetadataHash: "m1",
            Version: 1,
            Status: IngestionStatus.Succeeded,
            LastSuccessfulIngestion: DateTimeOffset.UtcNow,
            ChunkIds: ["c1"]);
        await store.SaveAsync(initial);

        // Worker A and Worker B both read Version 1 and attempt CAS claim to v2 Processing
        var claimA = initial with { Version = 2, Status = IngestionStatus.Processing, ContentHash = "h2" };
        var claimB = initial with { Version = 2, Status = IngestionStatus.Processing, ContentHash = "h3" };

        // Worker A claims v2
        await store.SaveAsync(claimA, expectedVersion: 1, expectedStatus: IngestionStatus.Succeeded);

        // Worker B tries to claim v2 based on v1 Succeeded -> fails with IngestionConcurrencyException
        await Assert.ThrowsAsync<IngestionConcurrencyException>(() =>
            store.SaveAsync(claimB, expectedVersion: 1, expectedStatus: IngestionStatus.Succeeded));

        var current = await store.GetAsync("doc-race-update");
        Assert.NotNull(current);
        Assert.Equal(2, current.Version);
        Assert.Equal(IngestionStatus.Processing, current.Status);
        Assert.Equal("h2", current.ContentHash);
    }

    [Fact]
    public async Task ConcurrentNewDocument_OnlyOneWriterClaimsVersionOne()
    {
        using var store = new SqliteIngestionStateStore(_connectionString);

        var docStateA = new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-race-new",
            ContentHash: "h1",
            MetadataHash: "m1",
            Version: 1,
            Status: IngestionStatus.Processing,
            LastSuccessfulIngestion: null,
            ChunkIds: []);

        var docStateB = new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-race-new",
            ContentHash: "h2",
            MetadataHash: "m2",
            Version: 1,
            Status: IngestionStatus.Processing,
            LastSuccessfulIngestion: null,
            ChunkIds: []);

        // Worker A claims new doc
        await store.SaveAsync(docStateA, expectedVersion: 0);

        // Worker B tries to claim new doc with same PK -> throws IngestionConcurrencyException
        await Assert.ThrowsAsync<IngestionConcurrencyException>(() =>
            store.SaveAsync(docStateB, expectedVersion: 0));

        var current = await store.GetAsync("doc-race-new");
        Assert.NotNull(current);
        Assert.Equal(1, current.Version);
        Assert.Equal("h1", current.ContentHash);
    }

    [Fact]
    public async Task StaleWriter_CannotMarkSucceeded()
    {
        using var store = new SqliteIngestionStateStore(_connectionString);

        // Worker A claims v2 Processing
        var stateV2Processing = new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-stale-success",
            ContentHash: "h2",
            MetadataHash: "m2",
            Version: 2,
            Status: IngestionStatus.Processing,
            LastSuccessfulIngestion: null,
            ChunkIds: []);
        await store.SaveAsync(stateV2Processing, expectedVersion: 0);

        // Worker B concurrently advances document to v3 Succeeded
        var stateV3Succeeded = new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-stale-success",
            ContentHash: "h3",
            MetadataHash: "m3",
            Version: 3,
            Status: IngestionStatus.Succeeded,
            LastSuccessfulIngestion: DateTimeOffset.UtcNow,
            ChunkIds: ["c3"]);
        await store.SaveAsync(stateV3Succeeded);

        // Stale Worker A finishes and tries to mark v2 as Succeeded
        var stateV2Succeeded = stateV2Processing with { Status = IngestionStatus.Succeeded };
        await Assert.ThrowsAsync<IngestionConcurrencyException>(() =>
            store.SaveAsync(stateV2Succeeded, expectedVersion: 2, expectedStatus: IngestionStatus.Processing));

        // State remains v3 Succeeded
        var final = await store.GetAsync("doc-stale-success");
        Assert.NotNull(final);
        Assert.Equal(3, final.Version);
        Assert.Equal(IngestionStatus.Succeeded, final.Status);
    }

    [Fact]
    public async Task StaleWriter_CannotMarkFailed()
    {
        using var store = new SqliteIngestionStateStore(_connectionString);

        // Worker A claims v2 Processing
        var stateV2Processing = new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-stale-fail",
            ContentHash: "h2",
            MetadataHash: "m2",
            Version: 2,
            Status: IngestionStatus.Processing,
            LastSuccessfulIngestion: null,
            ChunkIds: []);
        await store.SaveAsync(stateV2Processing, expectedVersion: 0);

        // Newer run succeeds at v3
        var stateV3Succeeded = new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-stale-fail",
            ContentHash: "h3",
            MetadataHash: "m3",
            Version: 3,
            Status: IngestionStatus.Succeeded,
            LastSuccessfulIngestion: DateTimeOffset.UtcNow,
            ChunkIds: ["c3"]);
        await store.SaveAsync(stateV3Succeeded);

        // Stale Worker A tries to record Failure for v2
        var stateV2Failed = stateV2Processing with { Status = IngestionStatus.Failed };
        await Assert.ThrowsAsync<IngestionConcurrencyException>(() =>
            store.SaveAsync(stateV2Failed, expectedVersion: 2, expectedStatus: IngestionStatus.Processing));

        // State remains v3 Succeeded (not corrupted by stale failure)
        var final = await store.GetAsync("doc-stale-fail");
        Assert.NotNull(final);
        Assert.Equal(3, final.Version);
        Assert.Equal(IngestionStatus.Succeeded, final.Status);
    }

    // --- 9. Schema Authority Tests ---

    [Fact]
    public async Task StateStoreFirst_InitializesValidV2Schema()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"schema_statestore_first_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={tempDb}";
        try
        {
            // 1. Open State Store first on fresh DB
            using (var stateStore = new SqliteIngestionStateStore(connStr))
            {
                var doc = new IngestionDocumentState("default", "d1", "h1", "m1", 1, IngestionStatus.Succeeded, DateTimeOffset.UtcNow, ["c1"]);
                await stateStore.SaveAsync(doc);
            }

            // Verify user_version is 2
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version;";
                Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            }

            // 2. Open Vector Store subsequently on the same DB
            using (var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions
            {
                ConnectionString = connStr,
                Dimensions = 2,
                EmbeddingModel = "test-model"
            }))
            {
                var meta = new DocumentMetadata("d1", "silo", "dept", "off", "conf", "type", 2024, "auth", new DateOnly(2024, 1, 1), "title");
                var chunk = new EmbeddedChunk(new DocumentChunk("c1", "d1", 0, "text", 0, 4, meta), new float[] { 1f, 0f }, "test-model");
                await vectorStore.UpsertAsync([chunk]);

                var res = await vectorStore.SearchAsync(new float[] { 1f, 0f }, topK: 1);
                Assert.Single(res);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) { File.Delete(tempDb); } } catch { }
        }
    }

    [Fact]
    public async Task VectorStoreFirst_StateStoreUsesSameSchema()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"schema_vectorstore_first_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={tempDb}";
        try
        {
            // 1. Open Vector Store first on fresh DB
            using (var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions
            {
                ConnectionString = connStr,
                Dimensions = 2,
                EmbeddingModel = "test-model"
            }))
            {
                var meta = new DocumentMetadata("d1", "silo", "dept", "off", "conf", "type", 2024, "auth", new DateOnly(2024, 1, 1), "title");
                var chunk = new EmbeddedChunk(new DocumentChunk("c1", "d1", 0, "text", 0, 4, meta), new float[] { 1f, 0f }, "test-model");
                await vectorStore.UpsertAsync([chunk]);
            }

            // Verify user_version is 2
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version;";
                Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            }

            // 2. Open State Store subsequently on the same DB
            using (var stateStore = new SqliteIngestionStateStore(connStr))
            {
                var doc = new IngestionDocumentState("default", "d1", "h1", "m1", 1, IngestionStatus.Succeeded, DateTimeOffset.UtcNow, ["c1"]);
                await stateStore.SaveAsync(doc);

                var retrieved = await stateStore.GetAsync("d1");
                Assert.NotNull(retrieved);
                Assert.Equal(1, retrieved.Version);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) { File.Delete(tempDb); } } catch { }
        }
    }

    [Fact]
    public void UnsupportedSchemaVersion_FailsFromStateStore()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"schema_unsupported_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={tempDb}";
        try
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 99;";
                cmd.ExecuteNonQuery();
            }

            var ex = Assert.Throws<InvalidOperationException>(() => new SqliteIngestionStateStore(connStr));
            Assert.Contains("Unsupported Pigmentor SQLite schema version 99", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) { File.Delete(tempDb); } } catch { }
        }
    }

    [Fact]
    public void UnsupportedSchemaVersion_FailsFromVectorStore()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"schema_unsupported_vec_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={tempDb}";
        try
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 99;";
                cmd.ExecuteNonQuery();
            }

            var ex = Assert.Throws<InvalidOperationException>(() => new SqliteVecStore(new SqliteVecStoreOptions
            {
                ConnectionString = connStr,
                Dimensions = 2,
                EmbeddingModel = "test-model"
            }));
            Assert.Contains("Unsupported Pigmentor SQLite schema version 99", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) { File.Delete(tempDb); } } catch { }
        }
    }

    [Fact]
    public void UnversionedDatabaseWithTables_FailsFromStateStore()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"schema_unversioned_{Guid.NewGuid():N}.db");
        var connStr = $"Data Source={tempDb}";
        try
        {
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE foreign_table (id INTEGER PRIMARY KEY, data TEXT);";
                cmd.ExecuteNonQuery();
            }

            var ex = Assert.Throws<InvalidOperationException>(() => new SqliteIngestionStateStore(connStr));
            Assert.Contains("Cannot open unversioned SQLite database with existing tables", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) { File.Delete(tempDb); } } catch { }
        }
    }

    // --- 10. Retry, Version Guarding & Scope Collision Tests ---

    [Fact]
    public async Task FailedAttempt_RetriesSameVersion_SuccessfullyCompletes()
    {
        using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        var flakyEmbedder = new FlakyEmbeddingService { ShouldFail = true };
        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), flakyEmbedder);

        var doc = CreateDocument("doc-retry", "Content to be ingested.");

        // First attempt fails during embedding
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.IngestAsync(doc));
        Assert.Equal("Simulated embedding transient failure.", ex.Message);

        // State in DB is Failed, Version 1
        var failedState = await stateStore.GetAsync("doc-retry");
        Assert.NotNull(failedState);
        Assert.Equal(1, failedState.Version);
        Assert.Equal(IngestionStatus.Failed, failedState.Status);

        // Second attempt retries: embedder succeeds
        flakyEmbedder.ShouldFail = false;
        var result = await service.IngestAsync(doc);

        // Result has Version 1 (version was NOT inflated to 2 on retry)
        Assert.Equal(1, result.Version);
        Assert.Equal(IngestionStatus.Succeeded, result.Status);
        Assert.False(result.Skipped);

        // State in DB is now Succeeded at Version 1
        var succeededState = await stateStore.GetAsync("doc-retry");
        Assert.NotNull(succeededState);
        Assert.Equal(1, succeededState.Version);
        Assert.Equal(IngestionStatus.Succeeded, succeededState.Status);
        Assert.NotNull(succeededState.LastSuccessfulIngestion);
    }

    [Fact]
    public async Task StaleExplicitVersion_Throws()
    {
        using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        // Document at version 5
        await stateStore.SaveAsync(new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-explicit-stale",
            ContentHash: "h5",
            MetadataHash: "m5",
            Version: 5,
            Status: IngestionStatus.Succeeded,
            LastSuccessfulIngestion: DateTimeOffset.UtcNow,
            ChunkIds: ["c5"]));

        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), new TestEmbeddingService(2, "test-model"));
        var doc = CreateDocument("doc-explicit-stale", "Content v3");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.IngestAsync(doc, explicitVersion: 3));
        Assert.Contains("Downgrading version is rejected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateExplicitVersion_SucceededSkips_ChangedThrows()
    {
        using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        var doc = CreateDocument("doc-explicit-dup", "Content v5");
        var cHash = IngestionFingerprint.ComputeContentHash(doc.Content);
        var mHash = IngestionFingerprint.ComputeMetadataHash(doc.Metadata);

        // Document at version 5 Succeeded
        await stateStore.SaveAsync(new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-explicit-dup",
            ContentHash: cHash,
            MetadataHash: mHash,
            Version: 5,
            Status: IngestionStatus.Succeeded,
            LastSuccessfulIngestion: DateTimeOffset.UtcNow,
            ChunkIds: ["c5"]));

        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), new TestEmbeddingService(2, "test-model"));

        // Same content with same explicitVersion = 5 -> skips cleanly
        var skipRes = await service.IngestAsync(doc, explicitVersion: 5);
        Assert.True(skipRes.Skipped);
        Assert.Equal(5, skipRes.Version);

        // Modified content with duplicate explicitVersion = 5 -> throws
        var changedDoc = CreateDocument("doc-explicit-dup", "Changed content v5");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.IngestAsync(changedDoc, explicitVersion: 5));
        Assert.Contains("duplicate explicit version 5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewerExplicitVersion_Accepted()
    {
        using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        // Document at version 2
        await stateStore.SaveAsync(new IngestionDocumentState(
            SourceId: "default",
            DocumentId: "doc-explicit-newer",
            ContentHash: "h2",
            MetadataHash: "m2",
            Version: 2,
            Status: IngestionStatus.Succeeded,
            LastSuccessfulIngestion: DateTimeOffset.UtcNow,
            ChunkIds: ["c2"]));

        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), new TestEmbeddingService(2, "test-model"));
        var doc = CreateDocument("doc-explicit-newer", "Content updated to v5");

        var result = await service.IngestAsync(doc, explicitVersion: 5);
        Assert.False(result.Skipped);
        Assert.Equal(5, result.Version);
        Assert.Equal(IngestionStatus.Succeeded, result.Status);

        var saved = await stateStore.GetAsync("doc-explicit-newer");
        Assert.NotNull(saved);
        Assert.Equal(5, saved.Version);
    }

    [Fact]
    public async Task DocumentId_CrossSourceScopeCollision_Throws()
    {
        using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = _connectionString, Dimensions = 2, EmbeddingModel = "test-model" });
        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), new TestEmbeddingService(2, "test-model"));

        var doc = CreateDocument("doc-scope-collision", "Content");

        // Ingest under source scope 'source-A'
        var resA = await service.IngestAsync(doc, sourceId: "source-A");
        Assert.Equal(IngestionStatus.Succeeded, resA.Status);

        // Attempting to ingest same doc under source scope 'source-B' throws collision exception
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.IngestAsync(doc, sourceId: "source-B"));
        Assert.Contains("already registered in synchronization scope 'source-A'", ex.Message, StringComparison.Ordinal);
    }
    private sealed class ActionReingestService : IDocumentReingestService
    {
        private readonly Action _onReingest;
        public ActionReingestService(Action onReingest) => _onReingest = onReingest;
        public Task ReingestAsync(string documentId, CancellationToken cancellationToken = default)
        {
            _onReingest();
            return Task.CompletedTask;
        }
    }

    private sealed class TestEmbeddingService : IEmbeddingService
    {
        public int CallCount { get; private set; }

        public TestEmbeddingService(int dimensions = 2, string model = "test-model")
        {
            Dimensions = dimensions;
            EmbeddingModel = model;
        }

        public string EmbeddingModel { get; }
        public int Dimensions { get; }

        public Task<EmbeddedChunk> EmbedAsync(DocumentChunk chunk, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var vec = new float[Dimensions];
            vec[0] = 1f;
            return Task.FromResult(new EmbeddedChunk(chunk, vec, EmbeddingModel));
        }

        public Task<ReadOnlyMemory<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
        {
            var vec = new float[Dimensions];
            vec[0] = 1f;
            return Task.FromResult<ReadOnlyMemory<float>>(vec);
        }

        public async IAsyncEnumerable<EmbeddedChunk> EmbedAsync(
            IAsyncEnumerable<DocumentChunk> chunks,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                yield return await EmbedAsync(chunk, cancellationToken);
            }
        }
    }

    private sealed class FlakyEmbeddingService : IEmbeddingService
    {
        public bool ShouldFail { get; set; } = true;
        public int Dimensions => 2;
        public string EmbeddingModel => "test-model";

        public Task<EmbeddedChunk> EmbedAsync(DocumentChunk chunk, CancellationToken cancellationToken = default)
        {
            if (ShouldFail)
            {
                throw new InvalidOperationException("Simulated embedding transient failure.");
            }
            var vec = new float[Dimensions];
            vec[0] = 1f;
            return Task.FromResult(new EmbeddedChunk(chunk, vec, EmbeddingModel));
        }

        public Task<ReadOnlyMemory<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
        {
            var vec = new float[Dimensions];
            vec[0] = 1f;
            return Task.FromResult<ReadOnlyMemory<float>>(vec);
        }

        public async IAsyncEnumerable<EmbeddedChunk> EmbedAsync(
            IAsyncEnumerable<DocumentChunk> chunks,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                yield return await EmbedAsync(chunk, cancellationToken);
            }
        }
    }
}
