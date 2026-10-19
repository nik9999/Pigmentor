using System.Text.Json.Serialization;

namespace Pigmentor.GigaChat.Model;

/// <summary>
/// Individual embedding vector entry in GigaChat embeddings response.
/// </summary>
public sealed class GigaChatEmbeddingItemDto
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = [];

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("object")]
    public string? ObjectType { get; set; }
}
