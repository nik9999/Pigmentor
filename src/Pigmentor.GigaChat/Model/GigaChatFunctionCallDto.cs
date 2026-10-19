using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pigmentor.GigaChat.Model;

/// <summary>
/// Function call payload emitted by GigaChat.
/// </summary>
public sealed class GigaChatFunctionCallDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; }
}
