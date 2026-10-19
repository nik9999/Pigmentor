using System.Globalization;
using System.Text;
using Npgsql;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Hybrid;

/// <summary>
/// Single-store hybrid retriever over PostgreSQL with the <c>pgvector</c> and
/// full-text-search extensions. Unlike <see cref="Pigmentor.Retrieval.HybridRetriever"/>
/// (which fans out to two separate <see cref="IRetriever"/>s and fuses the
/// results in process), this adapter performs dense ranking, sparse ranking,
/// and Reciprocal Rank Fusion <em>inside a single SQL round-trip</em> — the
/// "the database is the hybrid" pattern Chapter 14 teaches.
/// </summary>
/// <remarks>
/// <para>
/// The query embedding is produced by the injected <see cref="IEmbeddingService"/>
/// (mirroring <see cref="Pigmentor.Retrieval.DenseRetriever"/>, so the query gets the
/// model's query-task prefix) and passed to Postgres as a <c>::vector</c> text
/// literal — no separate pgvector binding package is required.
/// </para>
/// <para>
/// This is an integration adapter: it is build-verified and correct-by-construction
/// but not exercised in CI, because it needs a live PostgreSQL with the
/// <c>vector</c> extension, a <c>tsvector</c> column, and a populated table. It
/// follows the same "verified, not CI-run" policy as
/// <see cref="VectorStores.QdrantVectorStore"/> and
/// <see cref="VectorStores.AzureAiSearchVectorStore"/>.
/// </para>
/// </remarks>
public sealed class PostgresHybridRetriever : IRetriever, IAsyncDisposable
{
    /// <summary>The Reciprocal Rank Fusion constant (c = 60), matching <see cref="Pigmentor.Retrieval.RrfMerger"/>.</summary>
    public const int RrfConstant = 60;

    private readonly IEmbeddingService _embeddings;
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly PostgresHybridOptions _options;
    private readonly string _sql;

    /// <summary>
    /// Creates the retriever over a connection string. The retriever builds and
    /// owns an <see cref="NpgsqlDataSource"/>, so it is responsible for disposing
    /// it — see <see cref="DisposeAsync"/>.
    /// </summary>
    /// <param name="embeddings">Embeds the query (query-task prefix applied).</param>
    /// <param name="connectionString">A standard Npgsql connection string.</param>
    /// <param name="options">Table/column configuration. Defaults are sensible for the book's schema.</param>
    public PostgresHybridRetriever(
        IEmbeddingService embeddings,
        string connectionString,
        PostgresHybridOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _embeddings = embeddings;
        _options = options ?? new PostgresHybridOptions();
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _ownsDataSource = true;
        _sql = BuildHybridSql(_options);
    }

    /// <summary>
    /// Creates the retriever over a caller-owned <see cref="NpgsqlDataSource"/>.
    /// The caller retains ownership; <see cref="DisposeAsync"/> will not dispose
    /// the supplied data source (it is typically pooled and DI-managed).
    /// </summary>
    /// <param name="embeddings">Embeds the query (query-task prefix applied).</param>
    /// <param name="dataSource">A configured, caller-owned Npgsql data source.</param>
    /// <param name="options">Table/column configuration. Defaults are sensible for the book's schema.</param>
    public PostgresHybridRetriever(
        IEmbeddingService embeddings,
        NpgsqlDataSource dataSource,
        PostgresHybridOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(dataSource);

        _embeddings = embeddings;
        _options = options ?? new PostgresHybridOptions();
        _dataSource = dataSource;
        _ownsDataSource = false;
        _sql = BuildHybridSql(_options);
    }

    public string Strategy => "hybrid-postgres";

    public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Filter is not null)
        {
            throw new NotSupportedException($"Retriever '{Strategy}' does not support metadata filtering.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TopK);

        var queryVec = await _embeddings.EmbedQueryAsync(request.Query, cancellationToken).ConfigureAwait(false);

        await using var command = _dataSource.CreateCommand(_sql);
        // Values are bound as parameters — never string-interpolated — so the
        // query text and the embedding cannot be used for SQL injection.
        command.Parameters.AddWithValue("embedding", FormatVectorLiteral(queryVec.Span));
        command.Parameters.AddWithValue("query", request.Query);
        command.Parameters.AddWithValue("topk", request.TopK);

        var results = new List<RetrievalResult>(request.TopK);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        // Column order matches the SELECT list in BuildHybridSql.
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var meta = new DocumentMetadata(
                Id: reader.GetString(1),
                Silo: reader.GetString(5),
                Department: reader.GetString(6),
                Office: reader.GetString(7),
                ConfidentialityLevel: reader.GetString(8),
                DocumentType: reader.GetString(9),
                FiscalYear: reader.GetInt32(10),
                Author: reader.GetString(11),
                LastModified: DateOnly.FromDateTime(reader.GetDateTime(12)),
                Title: reader.GetString(13));

            var chunk = new DocumentChunk(
                ChunkId: reader.GetString(0),
                DocumentId: reader.GetString(1),
                ChunkIndex: reader.GetInt32(2),
                Text: reader.GetString(3),
                StartCharOffset: reader.GetInt32(14),
                EndCharOffset: reader.GetInt32(15),
                Metadata: meta);

            // Column 4 is the fused RRF score.
            results.Add(new RetrievalResult(chunk, reader.GetDouble(4)));
        }

        return results;
    }

    /// <summary>
    /// Builds the single RRF-in-SQL statement. Extracted and made
    /// <see langword="internal"/> so the SQL is unit-testable without a live
    /// PostgreSQL (the unit test asserts on the structural invariants — the RRF
    /// constant, the cosine operator, and the FTS functions).
    /// </summary>
    /// <remarks>
    /// The CTE unions two ranked legs and fuses them by summing
    /// <c>1.0 / (60 + rank)</c> (c = 60). The dense leg ranks by the pgvector
    /// cosine-distance operator <c>&lt;=&gt;</c>; the sparse leg ranks by
    /// <c>ts_rank_cd</c> over a <c>websearch_to_tsquery</c>.
    /// <para>
    /// HONEST NOTE: <c>ts_rank_cd</c> is PostgreSQL's <em>cover-density</em>
    /// ranking. It is NOT BM25 — there is no IDF term, no TF saturation, and no
    /// document-length normalization. It rewards proximity and density of the
    /// query lexemes within the document, which is a different (and generally
    /// weaker) relevance signal than the Okapi BM25 used by
    /// <see cref="Pigmentor.Retrieval.SparseRetriever"/>. For a true BM25 sparse leg in
    /// Postgres you need an extension such as <c>pg_search</c> / ParadeDB; the
    /// built-in FTS shown here is the no-extra-extension baseline.
    /// </para>
    /// </remarks>
    internal static string BuildHybridSql(PostgresHybridOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);

        // Identifiers come from configuration, not user input, and are quoted.
        var table = Quote(o.TableName);
        var id = Quote(o.ChunkIdColumn);
        var docId = Quote(o.DocumentIdColumn);
        var idx = Quote(o.ChunkIndexColumn);
        var text = Quote(o.TextColumn);
        var emb = Quote(o.EmbeddingColumn);
        var ts = Quote(o.TsVectorColumn);
        var silo = Quote(o.SiloColumn);
        var dept = Quote(o.DepartmentColumn);
        var office = Quote(o.OfficeColumn);
        var conf = Quote(o.ConfidentialityColumn);
        var docType = Quote(o.DocumentTypeColumn);
        var fy = Quote(o.FiscalYearColumn);
        var author = Quote(o.AuthorColumn);
        var modified = Quote(o.LastModifiedColumn);
        var title = Quote(o.TitleColumn);
        var start = Quote(o.StartOffsetColumn);
        var end = Quote(o.EndOffsetColumn);
        var cfg = o.TextSearchConfig; // a regconfig name, e.g. 'english'

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $@"
WITH dense AS (
    SELECT {id} AS chunk_id,
           ROW_NUMBER() OVER (ORDER BY {emb} <=> @embedding::vector) AS rank
    FROM {table}
    ORDER BY {emb} <=> @embedding::vector
    LIMIT (@topk * {o.CandidateMultiplier})
),
sparse AS (
    SELECT {id} AS chunk_id,
           ROW_NUMBER() OVER (
               ORDER BY ts_rank_cd({ts}, websearch_to_tsquery('{cfg}', @query)) DESC
           ) AS rank
    FROM {table}
    WHERE {ts} @@ websearch_to_tsquery('{cfg}', @query)
    LIMIT (@topk * {o.CandidateMultiplier})
),
fused AS (
    SELECT COALESCE(d.chunk_id, s.chunk_id) AS chunk_id,
           COALESCE(1.0 / ({RrfConstant} + d.rank), 0.0)
         + COALESCE(1.0 / ({RrfConstant} + s.rank), 0.0) AS score
    FROM dense d
    FULL OUTER JOIN sparse s ON d.chunk_id = s.chunk_id
)
SELECT t.{id}, t.{docId}, t.{idx}, t.{text}, f.score,
       t.{silo}, t.{dept}, t.{office}, t.{conf}, t.{docType},
       t.{fy}, t.{author}, t.{modified}, t.{title}, t.{start}, t.{end}
FROM fused f
JOIN {table} t ON t.{id} = f.chunk_id
ORDER BY f.score DESC
LIMIT @topk;");
        return sb.ToString();
    }

    /// <summary>
    /// Renders a dense vector as a pgvector text literal (<c>[v0,v1,...]</c>),
    /// bound as a parameter and cast to <c>::vector</c> in SQL. Extracted and
    /// <see langword="internal"/> for unit testing without a live database.
    /// </summary>
    internal static string FormatVectorLiteral(ReadOnlySpan<float> vector)
    {
        var sb = new StringBuilder(vector.Length * 8 + 2);
        sb.Append('[');
        for (int i = 0; i < vector.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Quotes a SQL identifier by wrapping it in double quotes and doubling any
    /// embedded double quotes. Identifiers originate from configuration, not from
    /// user input; this is defense-in-depth, not a user-input boundary.
    /// </summary>
    private static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Disposes the owned <see cref="NpgsqlDataSource"/>. When the data source was
    /// supplied by the caller (the <see cref="NpgsqlDataSource"/> ctor overload),
    /// it is left untouched.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Table and column configuration for <see cref="PostgresHybridRetriever"/>.
/// Defaults match the book's <c>chunks</c> schema (a <c>vector</c> column for
/// dense embeddings and a generated <c>tsvector</c> column for full-text search).
/// </summary>
public sealed class PostgresHybridOptions
{
    /// <summary>The table holding chunk rows. Default <c>chunks</c>.</summary>
    public string TableName { get; init; } = "chunks";

    /// <summary>The text-search configuration (regconfig) for <c>websearch_to_tsquery</c>. Default <c>english</c>.</summary>
    public string TextSearchConfig { get; init; } = "english";

    /// <summary>
    /// How many candidates each leg fetches, as a multiple of <c>topK</c>, before
    /// fusion (so fusion has headroom to reorder). Default 4.
    /// </summary>
    public int CandidateMultiplier { get; init; } = 4;

    /// <summary>The <c>chunk_id</c> column (primary key, text). Default <c>chunk_id</c>.</summary>
    public string ChunkIdColumn { get; init; } = "chunk_id";

    /// <summary>The <c>document_id</c> column. Default <c>document_id</c>.</summary>
    public string DocumentIdColumn { get; init; } = "document_id";

    /// <summary>The zero-based chunk-index column. Default <c>chunk_index</c>.</summary>
    public string ChunkIndexColumn { get; init; } = "chunk_index";

    /// <summary>The chunk-text column. Default <c>text</c>.</summary>
    public string TextColumn { get; init; } = "text";

    /// <summary>The dense-embedding <c>vector</c> column. Default <c>embedding</c>.</summary>
    public string EmbeddingColumn { get; init; } = "embedding";

    /// <summary>The full-text <c>tsvector</c> column. Default <c>text_tsv</c>.</summary>
    public string TsVectorColumn { get; init; } = "text_tsv";

    /// <summary>The start-offset column. Default <c>start_offset</c>.</summary>
    public string StartOffsetColumn { get; init; } = "start_offset";

    /// <summary>The end-offset column. Default <c>end_offset</c>.</summary>
    public string EndOffsetColumn { get; init; } = "end_offset";

    /// <summary>The silo metadata column. Default <c>silo</c>.</summary>
    public string SiloColumn { get; init; } = "silo";

    /// <summary>The department metadata column. Default <c>department</c>.</summary>
    public string DepartmentColumn { get; init; } = "department";

    /// <summary>The office metadata column. Default <c>office</c>.</summary>
    public string OfficeColumn { get; init; } = "office";

    /// <summary>The confidentiality metadata column. Default <c>confidentiality</c>.</summary>
    public string ConfidentialityColumn { get; init; } = "confidentiality";

    /// <summary>The document-type metadata column. Default <c>document_type</c>.</summary>
    public string DocumentTypeColumn { get; init; } = "document_type";

    /// <summary>The fiscal-year metadata column. Default <c>fiscal_year</c>.</summary>
    public string FiscalYearColumn { get; init; } = "fiscal_year";

    /// <summary>The author metadata column. Default <c>author</c>.</summary>
    public string AuthorColumn { get; init; } = "author";

    /// <summary>The last-modified metadata column (a <c>date</c>). Default <c>last_modified</c>.</summary>
    public string LastModifiedColumn { get; init; } = "last_modified";

    /// <summary>The title metadata column. Default <c>title</c>.</summary>
    public string TitleColumn { get; init; } = "title";
}
