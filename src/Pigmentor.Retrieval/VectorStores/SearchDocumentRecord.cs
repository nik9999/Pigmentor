using System.Text.Json.Serialization;

namespace Pigmentor.Retrieval.VectorStores;

/// <summary>
/// The flat document shape persisted to Azure AI Search by
/// <see cref="AzureAiSearchVectorStore"/>. Field names are carried by
/// <see cref="JsonPropertyNameAttribute"/> so they line up with the index
/// schema built in <see cref="AzureAiSearchVectorStore.BuildIndex"/>; the
/// index fields are defined explicitly (not via <c>FieldBuilder</c>), so no
/// SDK field attributes are needed here.
/// </summary>
public sealed class SearchDocumentRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("vector")]
    public IReadOnlyList<float> Vector { get; set; } = [];

    [JsonPropertyName("chunk_id")]
    public string ChunkId { get; set; } = string.Empty;

    [JsonPropertyName("document_id")]
    public string DocumentId { get; set; } = string.Empty;

    [JsonPropertyName("chunk_index")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("start_offset")]
    public int StartOffset { get; set; }

    [JsonPropertyName("end_offset")]
    public int EndOffset { get; set; }

    [JsonPropertyName("silo")]
    public string Silo { get; set; } = string.Empty;

    [JsonPropertyName("department")]
    public string Department { get; set; } = string.Empty;

    [JsonPropertyName("office")]
    public string Office { get; set; } = string.Empty;

    [JsonPropertyName("confidentiality")]
    public string Confidentiality { get; set; } = string.Empty;

    [JsonPropertyName("document_type")]
    public string DocumentType { get; set; } = string.Empty;

    [JsonPropertyName("fiscal_year")]
    public int FiscalYear { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("last_modified")]
    public string LastModified { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}
