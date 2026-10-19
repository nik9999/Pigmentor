namespace Pigmentor.Hosting;

/// <summary>
/// Options for configuring high-level RAG pipeline components.
/// </summary>
public sealed class RagPipelineOptions
{
    public string? QdrantConnectionString { get; private set; }
    public string? Neo4jConnectionString { get; private set; }
    public string? RedisConnectionString { get; private set; }
    public bool RerankingEnabled { get; private set; }
    public bool AuditingEnabled { get; private set; }

    public RagPipelineOptions UseQdrant(string connectionString)
    {
        QdrantConnectionString = connectionString;
        return this;
    }

    public RagPipelineOptions UseNeo4j(string connectionString)
    {
        Neo4jConnectionString = connectionString;
        return this;
    }

    public RagPipelineOptions EnableCaching(string redisConnectionString)
    {
        RedisConnectionString = redisConnectionString;
        return this;
    }

    public RagPipelineOptions EnableReranking()
    {
        RerankingEnabled = true;
        return this;
    }

    public RagPipelineOptions EnableAuditing()
    {
        AuditingEnabled = true;
        return this;
    }
}

