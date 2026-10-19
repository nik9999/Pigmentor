namespace Pigmentor.Hosting;

/// <summary>
/// Legacy options surface from earlier chapters.
/// </summary>
[Obsolete("RagPipelineOptions is deprecated because it did not actually wire up underlying services. Use services.AddPigmentor() and fluent provider/retriever extensions directly.")]
public sealed class RagPipelineOptions
{
    [Obsolete("Use services.AddPigmentor() with retrieval provider extensions instead.")]
    public RagPipelineOptions UseNeo4j(string connectionString) => this;

    [Obsolete("Use services.AddPigmentor() with retrieval provider extensions instead.")]
    public RagPipelineOptions EnableCaching(string redisConnectionString) => this;

    [Obsolete("Use services.AddPigmentor() with retrieval provider extensions instead.")]
    public RagPipelineOptions EnableReranking() => this;

    [Obsolete("Use services.AddPigmentor() with retrieval provider extensions instead.")]
    public RagPipelineOptions EnableAuditing() => this;
}

