using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Pigmentor.GigaChat.Model;

/// <summary>
/// Information about a model returned by the GigaChat Models API.
/// </summary>
public sealed class GigaChatModelDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string? ObjectType { get; set; }

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>
/// Response payload for GET /models.
/// </summary>
public sealed class GigaChatModelsResponse
{
    [JsonPropertyName("object")]
    public string? ObjectType { get; set; }

    [JsonPropertyName("data")]
    public List<GigaChatModelDto> Data { get; set; } = [];
}
