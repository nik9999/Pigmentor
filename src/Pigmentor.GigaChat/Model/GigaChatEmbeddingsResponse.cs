using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Pigmentor.GigaChat.Model;

/// <summary>
/// Response payload returned by GigaChat /embeddings endpoint.
/// </summary>
public sealed class GigaChatEmbeddingsResponse
{
    [JsonPropertyName("data")]
    public List<GigaChatEmbeddingItemDto> Data { get; set; } = [];

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("usage")]
    public GigaChatUsageDto? Usage { get; set; }

    [JsonPropertyName("object")]
    public string? ObjectType { get; set; }
}
