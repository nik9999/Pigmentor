using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Pigmentor.GigaChat.Model;

/// <summary>
/// Request payload sent to GigaChat /chat/completions endpoint.
/// </summary>
public sealed class GigaChatCompletionsRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = GigaChatModels.GigaChat;

    [JsonPropertyName("messages")]
    public List<GigaChatMessageDto> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; set; }

    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? N { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("repetition_penalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? RepetitionPenalty { get; set; }

    [JsonPropertyName("update_interval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UpdateInterval { get; set; }

    [JsonPropertyName("functions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GigaChatFunctionDto>? Functions { get; set; }

    [JsonPropertyName("function_call")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? FunctionCall { get; set; }
}
