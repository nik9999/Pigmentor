using Pigmentor.Core.Filtering;

namespace Pigmentor.Generation;

/// <summary>
/// Parameters for a query to <see cref="IRagPipeline"/>.
/// </summary>
public sealed record RagRequest
{
    /// <summary>
    /// The user question to answer. Must not be empty or whitespace.
    /// </summary>
    public string Question { get; init; }

    /// <summary>
    /// Optional conversation identifier for multi-turn session tracking.
    /// <para>
    /// <b>Note:</b> In the current pipeline implementation, conversational memory and query rewriting
    /// are not yet active; this property is part of the contract for caller session correlation
    /// and subsequent pipeline stages.
    /// </para>
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Optional metadata filter to restrict retrieval candidates.
    /// </summary>
    public MetadataFilter? MetadataFilter { get; init; }

    /// <summary>
    /// Optional limit on the number of candidate chunks to retrieve.
    /// </summary>
    public int? TopK { get; init; }

    public RagRequest(
        string question,
        string? conversationId = null,
        MetadataFilter? metadataFilter = null,
        int? topK = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        if (topK.HasValue && topK.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topK), topK, "TopK must be positive.");
        }

        Question = question;
        ConversationId = conversationId;
        MetadataFilter = metadataFilter;
        TopK = topK;
    }
}

