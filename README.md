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

## RAG Pipeline Execution

The generation pipeline (`IRagPipeline`) orchestrates retrieval with metadata filtering, reranking, token budgeting, prompt injection defense, generation, and citation mapping. By default, Pigmentor v1 uses Russian instructions and preserves technical terms (`Modbus`, `RS-485`, registers, commands) verbatim:

```csharp
// Fluent RAG configuration
services.AddPigmentor(configuration)
    .UseOllama(new Uri("http://localhost:11434"), chatModel: "llama3.2")
    .ConfigureRagPipeline(settings =>
    {
        settings.DefaultTopK = 5;
        settings.Budget = new ContextBudget(MaxTotalTokens: 4096, ReservedOutputTokens: 1024);
    });

// Complete response
var response = await pipeline.AskAsync(new RagRequest("Какие регистры Modbus поддерживаются?", topK: 5));
Console.WriteLine(response.Answer);

// Streaming response
await foreach (var evt in pipeline.AskStreamingAsync(new RagRequest("Покажи конфигурацию RS-485")))
{
    if (evt is RagTextDelta delta) Console.Write(delta.Text);
    else if (evt is RagCompleted completed) Console.WriteLine($"\nCitations: {completed.Response.Citations.Count}");
}
```

## Run

```powershell
dotnet build Pigmentor.slnx
dotnet test Pigmentor.slnx
```

## Pigmentor CLI (`pigmentor`)

`Pigmentor.RAG.Cli` provides a reference command-line interface for configuration, credential management, ingestion, and interactive RAG query execution against technical documentation.

> [!WARNING]
> **Security Warning:**
> Never place the GigaChat Authorization Key in `config.json` or other plain text configuration files.
> - For **interactive local usage**, use `pigmentor auth gigachat` to securely store the key in OS-protected credential storage (DPAPI on Windows).
> - For **CI/CD, container, or headless environments**, set the `PIGMENTOR_GIGACHAT_AUTH_KEY` environment variable.

### Quick Start Workflow

```powershell
# 1. Initialize user directories and configuration
dotnet run --project src/Pigmentor.RAG.Cli -- init

# 2. Store GigaChat credentials securely (no terminal echo)
dotnet run --project src/Pigmentor.RAG.Cli -- auth gigachat

# 3. Configure providers
dotnet run --project src/Pigmentor.RAG.Cli -- config set chat.provider gigachat
dotnet run --project src/Pigmentor.RAG.Cli -- config set embeddings.provider ollama

# 4. Ingest a document file or entire directory
dotnet run --project src/Pigmentor.RAG.Cli -- ingest ./docs

# 5. Ask a question with sources and citations
dotnet run --project src/Pigmentor.RAG.Cli -- ask "Какая скорость интерфейса RS-485?" --stream --diagnostics

# 6. Start an interactive conversational RAG session
dotnet run --project src/Pigmentor.RAG.Cli -- chat
```

### CLI Command Reference

- **`pigmentor init`**: Idempotently creates user application data and configuration directories.
- **`pigmentor auth gigachat`**: Prompts securely (without console echo) for the GigaChat Authorization Key and encrypts it via the platform's secure secret store.
- **`pigmentor auth status`**: Displays credential readiness and resolution source (`OS protected secret store`, `environment variable`, or `not configured`) without revealing secrets.
- **`pigmentor auth logout [gigachat]`**: Deletes credentials from the secure OS secret store.
- **`pigmentor config show`**: Displays all active non-secret configuration settings.
- **`pigmentor config set <setting> <value>`**: Updates a setting in `config.json`. Any attempt to set sensitive keywords (`key`, `secret`, `auth`, `token`, `password`, `credential`) is strictly rejected.
- **`pigmentor ingest <path>`**: Ingests a single file or recursively scans a directory. Supports PDF, DOCX, XLSX, TXT, BMP, PNG, JPG. Automatically detects unchanged files and skips redundant reprocessing.
- **`pigmentor ask "<question>" [--stream] [--diagnostics]`**: Queries the RAG pipeline.
  - `--stream`: Streams text tokens in real time, followed by final citations and provenance.
  - `--diagnostics`: Displays retrieval candidate count, sources used, citation counts, prompt tokens, and execution latency.
- **`pigmentor chat`**: Launches an interactive chat loop with `/exit`, `/clear` (new conversation context), and `/help`.
- **`pigmentor status [--check]`**: Displays operational readiness of the chat model, embedding service, and vector storage. With `--check`, executes an active connectivity and authentication probe.

