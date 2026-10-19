# Pigmentor

Pigmentor is a .NET 10 RAG platform built from the reusable SmartDocs ideas,
with a project-specific `Pigmentor.*` namespace.

## First slice

- `Pigmentor.Core` contains the domain and ports.
- `Pigmentor.Ingestion` contains document loading, chunking and embedding pipelines.
- `Pigmentor.Retrieval` contains dense, sparse, hybrid, graph and vectorless retrieval.
- `Pigmentor.Generation` contains the retrieve-augment-generate pipeline.
- `Pigmentor.VectorStore.SqliteVec` persists chunks and embeddings in SQLite and exposes an explicit sqlite-vec loading seam.
- `Pigmentor.Tests` contains offline tests.

The default provider is local Ollama (`nomic-embed-text` and `llama3.2`).
The sqlite-vec native extension is intentionally loaded by the host through
`SqliteVecStore.LoadVectorExtension`; deployment packaging will be added after
the target native binaries are selected for Windows/Linux.

Chat and embeddings are configured independently:

```json
{
  "Pigmentor": {
    "Llm": {
      "Provider": "Ollama",
      "Endpoint": "http://localhost:11434",
      "ChatModel": "llama3.2"
    },
    "Embedding": {
      "Provider": "Ollama",
      "Endpoint": "http://localhost:11434",
      "Model": "nomic-embed-text"
    }
  }
}
```

Use `Embedding:Provider = Onnx` for a local BGE-M3 backend. Set both
`Embedding:ModelPath` and `Embedding:TokenizerPath`; BGE-M3 requires the
converted ONNX graph and its compatible tokenizer bundle.

## Run

```powershell
dotnet build Pigmentor.slnx
dotnet test Pigmentor.slnx
```
