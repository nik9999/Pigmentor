using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Pigmentor.GigaChat.Model;

/// <summary>
/// Request payload sent to GigaChat /embeddings endpoint.
/// </summary>
public sealed class GigaChatEmbeddingsRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = GigaChatModels.Embeddings;

    [JsonPropertyName("input")]
    public IList<string> Input { get; set; } = [];
}
