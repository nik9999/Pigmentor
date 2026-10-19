using System.Text.Json.Serialization;

namespace Pigmentor.GigaChat.Model;

/// <summary>
/// Token consumption details returned by GigaChat.
/// </summary>
public sealed class GigaChatUsageDto
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}
