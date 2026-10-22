using System.Text.Json.Serialization;

namespace Pigmentor.Retrieval;

/// <summary>
/// Rule mapping explicit user triggers, keywords, and entity patterns to a document silo.
/// </summary>
public sealed class ScopeRule
{
    [JsonPropertyName("silo")]
    public string Silo { get; set; } = string.Empty;

    [JsonPropertyName("triggerPhrases")]
    public List<string> TriggerPhrases { get; set; } = [];

    [JsonPropertyName("triggerTerms")]
    public List<string> TriggerTerms { get; set; } = [];

    [JsonPropertyName("entityPatterns")]
    public List<string> EntityPatterns { get; set; } = [];
}

/// <summary>
/// Root configuration containing a list of scope rules.
/// </summary>
public sealed class ScopeRulesConfig
{
    [JsonPropertyName("scopes")]
    public List<ScopeRule> Scopes { get; set; } = [];
}
