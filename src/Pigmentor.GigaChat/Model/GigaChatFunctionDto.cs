using System.Text.Json.Serialization;

namespace Pigmentor.GigaChat.Model;

/// <summary>
/// Function definition descriptor exposed to GigaChat for tool/function calling.
/// </summary>
public sealed class GigaChatFunctionDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Parameters { get; set; }

    [JsonPropertyName("return_parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ReturnParameters { get; set; }
}
