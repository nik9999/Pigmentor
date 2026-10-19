using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Pigmentor.GigaChat.Model;

/// <summary>
/// Response payload returned by GigaChat /chat/completions endpoint.
/// </summary>
public sealed class GigaChatCompletionsResponse
{
    [JsonPropertyName("choices")]
    public List<GigaChatChoiceDto> Choices { get; set; } = [];

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("usage")]
    public GigaChatUsageDto? Usage { get; set; }

    [JsonPropertyName("object")]
    public string? ObjectType { get; set; }
}
