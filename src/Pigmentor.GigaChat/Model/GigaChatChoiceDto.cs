using System.Text.Json.Serialization;

namespace Pigmentor.GigaChat.Model;

/// <summary>
/// Choice item in GigaChat completions response.
/// </summary>
public sealed class GigaChatChoiceDto
{
    [JsonPropertyName("message")]
    public GigaChatMessageDto? Message { get; set; }

    /// <summary>
    /// Populated during streaming responses.
    /// </summary>
    [JsonPropertyName("delta")]
    public GigaChatMessageDto? Delta { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}
