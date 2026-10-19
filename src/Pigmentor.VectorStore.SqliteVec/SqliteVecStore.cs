using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Core.Numerics;

namespace Pigmentor.VectorStore.SqliteVec;

/// <summary>
/// SQLite persistence seam for sqlite-vec. The extension is loaded explicitly
/// by the host so native deployment remains a host concern.
/// </summary>
public sealed class SqliteVecStore : IVectorStore, IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteVecStore(string connectionString)
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_dynamic_cdecl());
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        Initialize();
    }

    public string CollectionName => "pigmentor";

    public Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void LoadVectorExtension(string extensionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionPath);
        _connection.EnableExtensions(true);
        _connection.LoadExtension(extensionPath);
    }

    public async Task UpsertAsync(IEnumerable<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var item in chunks)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO chunks(id, document_id, content, metadata, embedding) VALUES ($id, $document, $content, $metadata, $embedding)";
            command.Parameters.AddWithValue("$id", item.Chunk.ChunkId);
            command.Parameters.AddWithValue("$document", item.Chunk.DocumentId);
            command.Parameters.AddWithValue("$content", item.Chunk.Text);
            command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(item.Chunk.Metadata));
            command.Parameters.AddWithValue("$embedding", JsonSerializer.Serialize(item.Vector.ToArray()));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(ReadOnlyMemory<float> queryVector, int topK, MetadataFilter? filter = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        var results = new List<RetrievalResult>();
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, document_id, content, metadata, embedding FROM chunks";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var embedding = JsonSerializer.Deserialize<float[]>(reader.GetString(4)) ?? [];
            var metadata = JsonSerializer.Deserialize<DocumentMetadata>(reader.GetString(3)) ??
                new DocumentMetadata(reader.GetString(1), "", "", "", "", "Reference", 0, "", DateOnly.MinValue, reader.GetString(1));
            results.Add(new RetrievalResult(
                new DocumentChunk(reader.GetString(0), reader.GetString(1), 0, reader.GetString(2), 0, reader.GetString(2).Length, metadata),
                CosineKernel.Cosine(queryVector.Span, embedding)));
        }

        return results.OrderByDescending(x => x.Score).Take(topK).ToArray();
    }

    public void Dispose() => _connection.Dispose();

    public async Task DeleteAsync(IEnumerable<string> chunkIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        await using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM chunks WHERE id = $id";
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in chunkIds)
        {
            parameter.Value = id;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private void Initialize()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS chunks (id TEXT PRIMARY KEY, document_id TEXT NOT NULL, content TEXT NOT NULL, metadata TEXT NOT NULL, embedding TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }
}
