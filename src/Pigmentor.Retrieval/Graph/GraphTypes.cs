namespace Pigmentor.Retrieval.Graph;

/// <summary>An entity in the knowledge graph (e.g. Employee, Department, Document, Contract).</summary>
public sealed record GraphEntity(string Id, string Type, string Name, IReadOnlyDictionary<string, string> Properties);

/// <summary>A directed relationship between two <see cref="GraphEntity"/>s.</summary>
public sealed record GraphRelation(string FromId, string ToId, string Type, IReadOnlyDictionary<string, string> Properties);

/// <summary>Result of an entity-extraction pass over a chunk of text.</summary>
public sealed record EntityExtraction(IReadOnlyList<GraphEntity> Entities, IReadOnlyList<GraphRelation> Relations);
