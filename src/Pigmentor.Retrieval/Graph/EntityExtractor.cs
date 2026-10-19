using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Pigmentor.Retrieval.Graph;

/// <summary>
/// LLM-driven entity + relationship extractor. The primary path asks an
/// <see cref="IChatClient"/> for a typed <see cref="EntityExtraction"/> via
/// Microsoft.Extensions.AI structured output
/// (<c>GetResponseAsync&lt;T&gt;</c>); if that yields nothing usable — or the
/// model lacks reliable structured-output support — we fall back to a manual
/// prompt + lenient JSON parse. Used by <see cref="DocumentToGraphPipeline"/>
/// in Ch 13 and the LazyGraphRAG indexer in Ch 17.
/// </summary>
public sealed class EntityExtractor
{
    private readonly IChatClient _chat;

    private const string Prompt =
        """
        Extract entities and relationships from the following passage. Return ONLY a JSON object:

          {
            "entities":  [ { "id": "...", "type": "...", "name": "...", "properties": {} }, ... ],
            "relations": [ { "from": "...", "to": "...", "type": "...", "properties": {} }, ... ]
          }

        Use these entity types when applicable: Employee, Department, Office, Document, Product, Client, Contract.
        Use these relation types when applicable: WORKS_IN, REPORTS_TO, AUTHORED, GOVERNS, REFERENCES, DEPENDS_ON, SIGNED_BY.
        Use kebab-case stable ids (e.g. "acme-corporation", "vacation-policy-2026").

        Passage:
        {0}
        """;

    public EntityExtractor(IChatClient chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
    }

    public async Task<EntityExtraction> ExtractAsync(string passage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passage);
        var prompt = Prompt.Replace("{0}", passage, StringComparison.Ordinal);

        // Primary path: typed structured output. `useJsonSchemaResponseFormat:
        // false` keeps this working with local/Ollama models that lack native
        // JSON-schema response support — Microsoft.Extensions.AI then steers the
        // model with a prompt-appended schema and parses the reply into T.
        try
        {
            var typed = await _chat.GetResponseAsync<EntityExtraction>(
                prompt,
                useJsonSchemaResponseFormat: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // TryGetResult (NOT .Result, which throws on a failed parse). Accept
            // the typed result only when it is coherent (non-null lists, and it
            // carries entities or relations). One extra guard: if the model
            // returned relations but every one of them lost its endpoints, it
            // almost certainly emitted the prompt's "from"/"to" shape — which the
            // typed schema (FromId/ToId) cannot bind. That is exactly what the
            // manual fallback below repairs, so we decline the typed result and
            // let the lenient parser take over rather than silently drop edges.
            if (typed.TryGetResult(out var extraction) && extraction is not null &&
                extraction.Entities is not null && extraction.Relations is not null &&
                (extraction.Entities.Count > 0 || extraction.Relations.Count > 0))
            {
                var relationsLostEndpoints = extraction.Relations.Count > 0 &&
                    extraction.Relations.All(r => string.IsNullOrEmpty(r.FromId) || string.IsNullOrEmpty(r.ToId));
                if (!relationsLostEndpoints)
                {
                    return extraction;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // Structured output unavailable or unparseable — drop to the manual path.
        }

        // Fallback path: manual prompt + lenient extraction. Robust for models
        // that wrap JSON in prose or emit the "from"/"to" relation shape.
        var response = await _chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var raw = (response.Text ?? "{}").Trim();
        var json = ExtractJson(raw);
        try
        {
            var parsed = JsonSerializer.Deserialize<RawExtraction>(json) ?? new RawExtraction();
            return Convert(parsed);
        }
        catch (JsonException)
        {
            return new EntityExtraction(Array.Empty<GraphEntity>(), Array.Empty<GraphRelation>());
        }
    }

    private static EntityExtraction Convert(RawExtraction raw)
    {
        var entities = (raw.Entities ?? []).Select(e =>
            new GraphEntity(
                Id: e.Id ?? "",
                Type: e.Type ?? "Unknown",
                Name: e.Name ?? "",
                Properties: e.Properties ?? new Dictionary<string, string>())).ToList();
        var relations = (raw.Relations ?? []).Select(r =>
            new GraphRelation(
                FromId: r.From ?? "",
                ToId: r.To ?? "",
                Type: r.Type ?? "RELATED_TO",
                Properties: r.Properties ?? new Dictionary<string, string>())).ToList();
        return new EntityExtraction(entities, relations);
    }

    private static string ExtractJson(string raw)
    {
        var s = raw.IndexOf('{', StringComparison.Ordinal);
        var e = raw.LastIndexOf('}');
        return s < 0 || e < s ? "{}" : raw[s..(e + 1)];
    }

    private sealed record RawExtraction
    {
        [JsonPropertyName("entities")] public List<RawEntity>? Entities { get; init; }
        [JsonPropertyName("relations")] public List<RawRelation>? Relations { get; init; }
    }
    private sealed record RawEntity
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("type")] public string? Type { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("properties")] public Dictionary<string, string>? Properties { get; init; }
    }
    private sealed record RawRelation
    {
        [JsonPropertyName("from")] public string? From { get; init; }
        [JsonPropertyName("to")] public string? To { get; init; }
        [JsonPropertyName("type")] public string? Type { get; init; }
        [JsonPropertyName("properties")] public Dictionary<string, string>? Properties { get; init; }
    }
}
