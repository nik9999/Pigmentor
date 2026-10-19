# Pigmentor

Pigmentor is a .NET 10 RAG platform built from the reusable SmartDocs ideas,
with a project-specific `Pigmentor.*` namespace.

## First slice

- `Pigmentor.Core` contains domain primitives, ports, abstractions and configuration contracts.
- `Pigmentor.Ingestion` contains document loading, chunking and embedding pipelines.
- `Pigmentor.Retrieval` contains dense, sparse, hybrid, graph and vectorless retrieval.
- `Pigmentor.Generation` contains the retrieve-augment-generate pipeline.
- `Pigmentor.GigaChat` contains the Sber GigaChat REST adapter and Microsoft.Extensions.AI implementations.
- `Pigmentor.Hosting` contains the composition root, `PigmentorBuilder`, and top-level DI registrations (`UseOllama`, `UseGigaChat`, `UseConfiguredProviders`).
- `Pigmentor.VectorStore.SqliteVec` provides embedded, portable vector storage powered by SQLite and native sqlite-vec (`vec0`), supporting transactional relational chunk storage and storage-side metadata pre-filtering.
- `Pigmentor.Tests` contains unit and integration tests.

The default provider is local Ollama (`nomic-embed-text` and `llama3.2`).
The sqlite-vec native extension (`vec0`) is automatically resolved and loaded across platforms (Windows, Linux, macOS) by `SqliteVecExtensionLoader`, with support for an explicit path override if needed.

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

```csharp
// Programmatic provider configuration:
services.AddPigmentor()
    .UseOllama(new Uri("http://localhost:11434"), chatModel: "llama3.2", embeddingModel: "nomic-embed-text");

// Or with Sber GigaChat:
services.AddPigmentor()
    .UseGigaChat(authorizationKey: "...", chatModel: "GigaChat-2-Lite");

// Or dynamically wired from configuration (Pigmentor:Llm and Pigmentor:Embedding sections):
services.AddPigmentor(configuration)
    .UseConfiguredProviders();
```

Use `Embedding:Provider = Onnx` for a local BGE-M3 backend. Set both
`Embedding:ModelPath` and `Embedding:TokenizerPath`; BGE-M3 requires the
converted ONNX graph and its compatible tokenizer bundle.

## Run

```powershell
dotnet build Pigmentor.slnx
dotnet test Pigmentor.slnx
```
