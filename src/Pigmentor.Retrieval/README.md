# Pigmentor.Retrieval

Retrieval engine for the **Pigmentor** RAG stack — the companion library to
*Production-Grade RAG with C# and .NET*.

Dense, sparse, and hybrid retrievers; Reciprocal Rank Fusion; MMR and context
ordering; query-enhancement decorators (HyDE, RAG-Fusion, CRAG, step-back);
graph and vectorless retrieval; and the in-memory vector store for tests and demos.

Vector storage is abstracted through `IVectorStore`, with `Pigmentor.VectorStore.SqliteVec`
providing an embedded, portable vector store powered by SQLite and native sqlite-vec.

## Install

```bash
dotnet add package Pigmentor.Retrieval
```

## License

MIT © Rachid Dahir
