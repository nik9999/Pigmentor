#pragma warning disable CA1861 // Prefer 'static readonly' fields over constant array arguments
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.VectorStore.SqliteVec;
using Xunit;

namespace Pigmentor.Tests;

public sealed class SqliteVecIntegrationTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly string _connectionString;

    public SqliteVecIntegrationTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"pigmentor_test_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_tempDbPath}";
    }

    public void Dispose()
    {
        // Give connection pool a moment to release handles if needed
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
            // Best effort cleanup in temp directory
        }
    }

    private static EmbeddedChunk CreateChunk(
        string chunkId,
        string docId,
        float[] vector,
        string department = "Engineering",
        int fiscalYear = 2024,
        string text = "Sample text",
        string confidentiality = "Internal",
        string title = "Sample Title",
        string embeddingModel = "test-embed-model")
    {
        var metadata = new DocumentMetadata(
            Id: docId,
            Silo: "technical-docs",
            Department: department,
            Office: "Montreal",
            ConfidentialityLevel: confidentiality,
            DocumentType: "Reference",
            FiscalYear: fiscalYear,
            Author: "Test Author",
            LastModified: new DateOnly(2024, 6, 1),
            Title: title);

        var docChunk = new DocumentChunk(
            ChunkId: chunkId,
            DocumentId: docId,
            ChunkIndex: 0,
            Text: text,
            StartCharOffset: 0,
            EndCharOffset: text.Length,
            Metadata: metadata);

        return new EmbeddedChunk(docChunk, vector, embeddingModel);
    }

    [Fact]
    public async Task NearestNeighborOrdering_ReturnsClosestVectorsFirst()
    {
        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        });

        // Vector pointing along X-axis
        var query = new float[] { 1f, 0f };

        // Candidates:
        // c1: angle 0 (exact match, distance 0)
        // c2: angle 45 deg (cos 0.707, distance ~0.293)
        // c3: angle 90 deg (cos 0.0, distance 1.0)
        var c1 = CreateChunk("c1", "doc1", [1f, 0f], text: "Exact");
        var c2 = CreateChunk("c2", "doc2", [0.7071f, 0.7071f], text: "Near");
        var c3 = CreateChunk("c3", "doc3", [0f, 1f], text: "Orthogonal");

        await store.UpsertAsync([c2, c3, c1]);

        var results = await store.SearchAsync(query, topK: 3);

        Assert.Equal(3, results.Count);
        Assert.Equal("c1", results[0].Chunk.ChunkId);
        Assert.Equal("c2", results[1].Chunk.ChunkId);
        Assert.Equal("c3", results[2].Chunk.ChunkId);

        // Score semantics: score = 1.0 - distance
        // For c1: distance ~ 0 => score ~ 1.0
        // For c3: distance ~ 1 => score ~ 0.0
        Assert.True(results[0].Score > results[1].Score);
        Assert.True(results[1].Score > results[2].Score);
        Assert.InRange(results[0].Score, 0.99, 1.01);
        Assert.InRange(results[2].Score, -0.01, 0.01);

        // Precomputed vectors carried directly from storage
        var v0 = results[0].Vector;
        Assert.True(v0.HasValue && v0.Value.Span.SequenceEqual(c1.Vector.Span));
        var v1 = results[1].Vector;
        Assert.True(v1.HasValue && v1.Value.Span.SequenceEqual(c2.Vector.Span));
        var v2 = results[2].Vector;
        Assert.True(v2.HasValue && v2.Value.Span.SequenceEqual(c3.Vector.Span));
    }

    [Fact]
    public async Task PersistenceAcrossDispose_ReopeningPreservesChunksAndVectors()
    {
        var c1 = CreateChunk("c1", "doc1", [0.6f, 0.8f], text: "Persisted Text", title: "Original Title");

        // Write with first store instance and dispose
        using (var store1 = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        }))
        {
            await store1.UpsertAsync([c1]);
        }

        // Reopen with second store instance (explicit dimensions and model)
        using (var store2 = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        }))
        {
            var results = await store2.SearchAsync(new float[] { 0.6f, 0.8f }, topK: 1);

            Assert.Single(results);
            Assert.Equal("c1", results[0].Chunk.ChunkId);
            Assert.Equal("Persisted Text", results[0].Chunk.Text);
            Assert.Equal("Original Title", results[0].Chunk.Metadata.Title);
            Assert.InRange(results[0].Score, 0.99, 1.01);
        }
    }

    [Fact]
    public async Task UpsertIdempotency_UpdatesExistingChunkWithoutDuplication()
    {
        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        });

        var initial = CreateChunk("c1", "doc1", [1f, 0f], text: "Initial Content", department: "Engineering");
        await store.UpsertAsync([initial]);

        // Verify initial state
        var res1 = await store.SearchAsync(new float[] { 1f, 0f }, topK: 5);
        Assert.Single(res1);
        Assert.Equal("Initial Content", res1[0].Chunk.Text);

        // Update with modified content and new vector
        var updated = CreateChunk("c1", "doc1", [0f, 1f], text: "Updated Content", department: "Finance");
        await store.UpsertAsync([updated]);

        // Verify still exactly 1 chunk exists
        var res2 = await store.SearchAsync(new float[] { 0f, 1f }, topK: 5);
        Assert.Single(res2);
        Assert.Equal("c1", res2[0].Chunk.ChunkId);
        Assert.Equal("Updated Content", res2[0].Chunk.Text);
        Assert.Equal("Finance", res2[0].Chunk.Metadata.Department);
        Assert.InRange(res2[0].Score, 0.99, 1.01);
    }

    [Fact]
    public async Task DeleteAsync_AtomicallyRemovesFromChunksAndVecChunks()
    {
        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        });

        var c1 = CreateChunk("c1", "doc1", [1f, 0f]);
        var c2 = CreateChunk("c2", "doc2", [0f, 1f]);
        await store.UpsertAsync([c1, c2]);

        var beforeDelete = await store.SearchAsync(new float[] { 1f, 0f }, topK: 5);
        Assert.Equal(2, beforeDelete.Count);

        await store.DeleteAsync(["c1"]);

        var afterDelete = await store.SearchAsync(new float[] { 1f, 0f }, topK: 5);
        Assert.Single(afterDelete);
        Assert.Equal("c2", afterDelete[0].Chunk.ChunkId);
    }

    [Fact]
    public async Task MetadataPreFilterInvariant_EnsuresTopKIsNotWastedOnExcludedVectors()
    {
        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        });

        var query = new float[] { 1f, 0f };

        // HR chunk is an exact vector match (distance 0), but wrong department
        var hrChunk = CreateChunk("hr-1", "doc-hr", [1f, 0f], department: "HR", text: "HR policy");

        // Engineering chunk has distance ~0.293 from query, but matches department
        var engChunk = CreateChunk("eng-1", "doc-eng", [0.7071f, 0.7071f], department: "Engineering", text: "Engineering docs");

        await store.UpsertAsync([hrChunk, engChunk]);

        // CRITICAL: Request topK = 1 with Engineering filter.
        // Pre-filtering in SQLite ensures eng-1 is selected and returned as the top result!
        var filter = MetadataFilter.Where(m => m.Department == "Engineering");
        var results = await store.SearchAsync(query, topK: 1, filter: filter);

        Assert.Single(results);
        Assert.Equal("eng-1", results[0].Chunk.ChunkId);
        Assert.Equal("Engineering", results[0].Chunk.Metadata.Department);
    }

    [Fact]
    public async Task CompoundMetadataFilters_AndOrCombinationsWorkAccurately()
    {
        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        });

        var c1 = CreateChunk("c1", "doc1", [1f, 0f], department: "Engineering", fiscalYear: 2024);
        var c2 = CreateChunk("c2", "doc2", [1f, 0f], department: "Engineering", fiscalYear: 2022);
        var c3 = CreateChunk("c3", "doc3", [1f, 0f], department: "Product", fiscalYear: 2024);
        var c4 = CreateChunk("c4", "doc4", [1f, 0f], department: "Legal", fiscalYear: 2024);

        await store.UpsertAsync([c1, c2, c3, c4]);

        var query = new float[] { 1f, 0f };

        // Test AND: Engineering AND FiscalYear >= 2023 -> only c1
        var andFilter = MetadataFilter.Where(m => m.Department == "Engineering" && m.FiscalYear >= 2023);
        var andResults = await store.SearchAsync(query, topK: 10, filter: andFilter);
        Assert.Single(andResults);
        Assert.Equal("c1", andResults[0].Chunk.ChunkId);

        // Test OR: Engineering OR Product -> c1, c2, c3
        var orFilter = MetadataFilter.Where(m => m.Department == "Engineering" || m.Department == "Product");
        var orResults = await store.SearchAsync(query, topK: 10, filter: orFilter);
        Assert.Equal(3, orResults.Count);
        Assert.Contains(orResults, r => r.Chunk.ChunkId == "c1");
        Assert.Contains(orResults, r => r.Chunk.ChunkId == "c2");
        Assert.Contains(orResults, r => r.Chunk.ChunkId == "c3");

        // Test filter.And(otherFilter)
        var f1 = MetadataFilter.Where(m => m.FiscalYear == 2024);
        var f2 = MetadataFilter.Where(m => m.Department == "Product");
        var combined = f1.And(f2);
        var combinedResults = await store.SearchAsync(query, topK: 10, filter: combined);
        Assert.Single(combinedResults);
        Assert.Equal("c3", combinedResults[0].Chunk.ChunkId);
    }

    [Fact]
    public void UnsupportedFilterExpression_FailsFastWithNotSupportedException()
    {
        // Calling methods like StartsWith or unmapped functions should fail fast
        Assert.Throws<NotSupportedException>(() =>
        {
            MetadataFilter.Where(m => m.Department.StartsWith("Eng"));
        });
    }

    [Fact]
    public async Task SpecialCharactersAndSqlSafety_HandledSafelyViaParameters()
    {
        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        });

        var trickyAuthor = "O'Connor; DROP TABLE chunks; --";
        var trickyTitle = "Quotes \" and ' and -- comments /* test */";

        var chunk = CreateChunk(
            "c_safe",
            "doc_safe",
            [1f, 0f],
            text: "Safe content",
            title: trickyTitle);

        // Override author to tricky author
        var metadata = chunk.Chunk.Metadata with { Author = trickyAuthor };
        var updatedChunk = new EmbeddedChunk(chunk.Chunk with { Metadata = metadata }, chunk.Vector, chunk.EmbeddingModel);

        await store.UpsertAsync([updatedChunk]);

        // Search with filter containing special characters
        var filter = MetadataFilter.Where(m => m.Author == trickyAuthor);
        var results = await store.SearchAsync(new float[] { 1f, 0f }, topK: 5, filter: filter);

        Assert.Single(results);
        Assert.Equal("c_safe", results[0].Chunk.ChunkId);
        Assert.Equal(trickyAuthor, results[0].Chunk.Metadata.Author);
        Assert.Equal(trickyTitle, results[0].Chunk.Metadata.Title);
    }

    [Fact]
    public async Task DimensionMismatch_ThrowsArgumentException()
    {
        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        });

        // Insert wrong dimensions
        var wrongChunk = CreateChunk("c_wrong", "doc1", [1f, 0f, 0.5f]); // dim 3 instead of 2
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync([wrongChunk]));

        // Search wrong dimensions
        await Assert.ThrowsAsync<ArgumentException>(() => store.SearchAsync(new float[] { 1f, 0f, 0.5f }, topK: 1));
    }

    [Fact]
    public async Task ReopeningWithMismatchedDimensions_ThrowsInvalidOperationException()
    {
        var chunk = CreateChunk("c1", "doc1", [1f, 0f]);

        using (var store1 = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        }))
        {
            await store1.UpsertAsync([chunk]);
        }

        // Reopen requesting 384 dimensions on a 2-dimensional database
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var store2 = new SqliteVecStore(new SqliteVecStoreOptions
            {
                ConnectionString = _connectionString,
                Dimensions = 384,
                EmbeddingModel = "test-embed-model"
            });
        });
    }

    [Fact]
    public async Task CancellationToken_PropagatesAndCancelsOperation()
    {
        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await store.SearchAsync(new float[] { 1f, 0f }, topK: 5, cancellationToken: cts.Token);
        });

        var chunk = CreateChunk("c1", "doc1", [1f, 0f]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await store.UpsertAsync([chunk], cancellationToken: cts.Token);
        });
    }

    [Fact]
    public async Task SearchWithMetadataFilterAll_ReturnsUnconditionalResults()
    {
        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        });

        var c1 = CreateChunk("c1", "doc1", [1f, 0f], department: "Engineering");
        var c2 = CreateChunk("c2", "doc2", [0f, 1f], department: "HR");

        await store.UpsertAsync([c1, c2]);

        var results = await store.SearchAsync(new float[] { 1f, 0f }, topK: 5, filter: MetadataFilter.All);

        Assert.Equal(2, results.Count);
    }

    // --- Targeted Hardening Tests ---

    [Fact]
    public void ProviderBootstrap_InitializesSQLiteRuntimeBeforeOpening()
    {
        SqliteVecExtensionLoader.EnsureSqliteProvider();
        Assert.True(SQLitePCL.raw.sqlite3_libversion_number() > 0);

        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-embed-model"
        });

        Assert.Equal(2, store.Dimensions);
        Assert.Equal("test-embed-model", store.EmbeddingModel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NewDatabase_WithZeroOrNegativeDimensions_ThrowsArgumentOutOfRangeException(int invalidDim)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var store = new SqliteVecStore(new SqliteVecStoreOptions
            {
                ConnectionString = _connectionString,
                Dimensions = invalidDim,
                EmbeddingModel = "test-embed-model"
            });
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NewDatabase_WithMissingOrEmptyEmbeddingModel_ThrowsArgumentException(string? invalidModel)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            using var store = new SqliteVecStore(new SqliteVecStoreOptions
            {
                ConnectionString = _connectionString,
                Dimensions = 2,
                EmbeddingModel = invalidModel!
            });
        });
    }

    [Fact]
    public void EmbeddingModel_PersistedAndReopenedSuccessfully()
    {
        using (var store1 = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "nomic-embed-text"
        }))
        {
            Assert.Equal("nomic-embed-text", store1.EmbeddingModel);
        }

        using (var store2 = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "nomic-embed-text"
        }))
        {
            Assert.Equal("nomic-embed-text", store2.EmbeddingModel);
        }
    }

    [Fact]
    public void ReopenWithMismatchedEmbeddingModel_ThrowsInvalidOperationException()
    {
        using (var store1 = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "model-A"
        }))
        {
        }

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var store2 = new SqliteVecStore(new SqliteVecStoreOptions
            {
                ConnectionString = _connectionString,
                Dimensions = 2,
                EmbeddingModel = "model-B"
            });
        });

        Assert.Contains("model-A", ex.Message);
        Assert.Contains("model-B", ex.Message);
    }

    [Fact]
    public async Task ChunkModelMismatch_ThrowsArgumentException()
    {
        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "model-A"
        });

        // Chunk has model-B instead of model-A
        var mismatchedChunk = new EmbeddedChunk(
            CreateChunk("c1", "doc1", [1f, 0f]).Chunk,
            new float[] { 1f, 0f },
            "model-B");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync([mismatchedChunk]));
        Assert.Contains("model-A", ex.Message);
        Assert.Contains("model-B", ex.Message);
    }

    [Fact]
    public async Task BatchUpsert_WithModelMismatchInSubsequentChunk_LeavesDatabaseEmpty()
    {
        using var store = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "model-A"
        });

        var chunk1 = new EmbeddedChunk(CreateChunk("c1", "doc1", [1f, 0f]).Chunk, new float[] { 1f, 0f }, "model-A");
        var chunk2 = new EmbeddedChunk(CreateChunk("c2", "doc2", [0f, 1f]).Chunk, new float[] { 0f, 1f }, "model-B");

        // Upsert batch containing valid chunk1 and invalid chunk2
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync([chunk1, chunk2]));

        // Verify chunk1 was NOT written to the database (atomic)
        var results = await store.SearchAsync(new float[] { 1f, 0f }, topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public void UnsupportedSchemaVersion_ThrowsInvalidOperationException()
    {
        // Create database manually with user_version = 999
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 999;";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var store = new SqliteVecStore(new SqliteVecStoreOptions
            {
                ConnectionString = _connectionString,
                Dimensions = 2,
                EmbeddingModel = "test-embed-model"
            });
        });

        Assert.Contains("999", ex.Message);
        Assert.Contains($"Expected version {SqliteVecStore.CurrentSchemaVersion}", ex.Message);
    }

    [Fact]
    public void UnversionedDatabase_WithExistingPigmentorTables_ThrowsInvalidOperationException()
    {
        // Create an unversioned table with pigmentor tables (user_version = 0 by default)
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE chunks (id INT);";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var store = new SqliteVecStore(new SqliteVecStoreOptions
            {
                ConnectionString = _connectionString,
                Dimensions = 2,
                EmbeddingModel = "test-embed-model"
            });
        });

        Assert.Contains("unversioned", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrickyEmbeddingModelName_RoundTripsSafelyViaParameters()
    {
        var trickyModelName = "model'oops\"; DROP TABLE chunks; --";

        using (var store1 = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = trickyModelName
        }))
        {
            var chunk = new EmbeddedChunk(CreateChunk("c1", "doc1", [1f, 0f]).Chunk, new float[] { 1f, 0f }, trickyModelName);
            await store1.UpsertAsync([chunk]);
        }

        // Reopen with exact tricky name
        using (var store2 = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = trickyModelName
        }))
        {
            var results = await store2.SearchAsync(new float[] { 1f, 0f }, topK: 1);
            Assert.Single(results);
            Assert.Equal("c1", results[0].Chunk.ChunkId);
            Assert.Equal(trickyModelName, store2.EmbeddingModel);
        }
    }

    [Fact]
    public void ExplicitInvalidExtensionPath_ThrowsFileNotFoundException()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.dll");

        Assert.Throws<FileNotFoundException>(() =>
        {
            using var store = new SqliteVecStore(new SqliteVecStoreOptions
            {
                ConnectionString = _connectionString,
                Dimensions = 2,
                EmbeddingModel = "test-embed-model",
                ExtensionPath = invalidPath
            });
        });
    }
}
