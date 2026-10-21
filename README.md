# Pigmentor

Pigmentor is an enterprise-grade .NET 10 Retrieval-Augmented Generation (RAG) platform with embedded vector storage, structural document extraction, deterministic evaluation, and a reference command-line interface.

## Supported Operating Systems

Pigmentor applications target **Windows** and **Linux** only:

| Capability | Windows | Linux Desktop | Linux Headless |
| :--- | :---: | :---: | :---: |
| **CLI Runtime** | YES | YES | YES |
| **OS Secret Store** | YES (DPAPI) | YES (Secret Service / `secret-tool`) | — |
| **Environment Credential** | YES | YES | YES (`PIGMENTOR_GIGACHAT_AUTH_KEY`) |
| **SQLite + sqlite-vec** | YES | YES | YES |
| **Future Avalonia Desktop** | YES | YES | — |

> [!NOTE]
> macOS is not supported.

## Architectural Layers

- `Pigmentor.Core`: Domain primitives, ports, abstractions, chunking structures, and configuration contracts.
- `Pigmentor.Ingestion`: Document loading (PDF, DOCX, XLSX, TXT, images), structural normalization, quality gating, and embedding pipelines.
- `Pigmentor.Retrieval`: Dense vector retrieval, sparse lexical retrieval, hybrid fusion (RRF), structural retrieval, and reranking.
- `Pigmentor.Generation`: Retrieve-augment-generate pipeline, prompt construction, grounding enforcement, and citation mapping.
- `Pigmentor.GigaChat`: Sber GigaChat REST adapter and Microsoft.Extensions.AI implementations.
- `Pigmentor.Hosting`: Composition root, `PigmentorBuilder`, and DI registrations (`UseOllama`, `UseGigaChat`, `UseConfiguredProviders`).
- `Pigmentor.VectorStore.SqliteVec`: Embedded, portable vector storage powered by SQLite and native sqlite-vec (`vec0`), supporting relational chunk storage and storage-side metadata pre-filtering.
- `Pigmentor.Evaluation`: Deterministic synthetic test suites, regression benchmarks, retrieval metrics (Hit@K, MRR@K, NDCG@K), and generation metrics (grounding fidelity, citation precision/recall).
- `Pigmentor.App`: Application workspace management, configuration serialization, credential resolution, and runtime composition.
- `Pigmentor.RAG.Cli`: Reference executable command-line interface (`pigmentor`).

## Provider Combinations

Pigmentor supports 4 core provider combinations for chat and embeddings:

1. **Ollama Chat + Ollama Embeddings** (Default fully local setup)
2. **GigaChat + Ollama Embeddings**
3. **Ollama Chat + Local ONNX Embeddings** (e.g. BGE-M3 ONNX runtime)
4. **GigaChat + Local ONNX Embeddings**

### Configuration (`config.json`)

Configuration is stored in `<appData>/config.json` without secrets:

```json
{
  "rootPath": "D:\\PigmentorData",
  "chat": {
    "provider": "Ollama",
    "model": "llama3.2",
    "endpoint": "http://localhost:11434"
  },
  "embeddings": {
    "provider": "Ollama",
    "model": "nomic-embed-text",
    "endpoint": "http://localhost:11434",
    "dimensions": 768
  },
  "gigachat": {
    "scope": "GIGACHAT_API_PERS",
    "verifySsl": true
  }
}
```

For local ONNX embeddings:

```json
{
  "embeddings": {
    "provider": "Onnx",
    "modelPath": "/models/bge-m3.onnx",
    "tokenizerPath": "/models/tokenizer.json",
    "dimensions": 1024
  }
}
```

## Workspace & Document Silos

Pigmentor operates against a single workspace specified by `rootPath`.
The vector database is always derived strictly as:

```text
<rootPath>/pigmentor.db
```

The workspace organizes enterprise documents into **6 fixed standard silos**:

1. `hr-policies`
2. `technical-docs`
3. `financial-reports`
4. `legal-contracts`
5. `product-catalog`
6. `release-notes-tickets`

Executing `pigmentor init` idempotently provisions `rootPath` and all 6 silo directories.

## Security & Credential Management

> [!WARNING]
> **Security Mandate:**
> Never place the GigaChat Authorization Key in `config.json` or plain text files.
> - **Interactive Desktop/CLI**: Use `pigmentor auth gigachat` to securely store credentials in OS-protected credential storage (Windows DPAPI or Linux FreeDesktop Secret Service via `secret-tool`).
> - **Headless / CI / Containers**: Set the `PIGMENTOR_GIGACHAT_AUTH_KEY` environment variable. Plaintext fallback is strictly rejected (fail closed).

## Pigmentor CLI (`pigmentor`)

### Quick Start Workflow

```powershell
# 1. Initialize user workspace and document silos
pigmentor init

# 2. Store GigaChat credentials securely (no terminal echo)
pigmentor auth gigachat

# 3. Configure providers
pigmentor config set chat.provider GigaChat
pigmentor config set chat.model GigaChat-2-Pro

# 4. Synchronize all documents across workspace silos
pigmentor sync

# 5. Ask a question with sources and diagnostics
pigmentor ask "Какая скорость передачи данных установлена для RS-485?" --stream --diagnostics

# 6. Start an interactive RAG query session
pigmentor chat
```

### CLI Command Reference

- **`pigmentor init`**: Idempotently creates the workspace directory structure (`rootPath`, `pigmentor.db`, and the 6 silo folders).
- **`pigmentor sync`**: Synchronizes all supported documents across the 6 workspace silos into SQLite + sqlite-vec. Features resilient recursive traversal, deterministic sorting, skipping of unsupported files, warning on unknown root folders, and structured summary reporting.
- **`pigmentor auth gigachat`**: Prompts securely (without console echo) for the GigaChat Authorization Key and encrypts it via the platform's secure secret store.
- **`pigmentor auth status`**: Displays credential readiness and resolution provenance (`OS protected secret store`, `environment variable`, or `not configured`) without exposing secrets.
- **`pigmentor auth logout [gigachat]`**: Deletes credentials from the secure OS secret store.
- **`pigmentor config show`**: Displays active non-secret configuration settings, workspace root path, and derived database path.
- **`pigmentor config set <setting> <value>`**: Updates a setting in `config.json`. Setting sensitive keywords (`key`, `secret`, `auth`, `token`, `password`, `credential`) or deprecated `databasePath` is strictly rejected.
- **`pigmentor ingest <path>`**: Ingests an ad-hoc single file or directory. Supports PDF, DOCX, XLSX, TXT, BMP, PNG, JPG. Automatically detects unchanged files and skips redundant reprocessing.
- **`pigmentor ask <question> [--stream] [--diagnostics]`**: Queries the RAG pipeline. Multi-word unquoted questions are automatically joined.
  - `--stream`: Streams text tokens in real time, followed by final citations and provenance.
  - `--diagnostics`: Displays retrieval candidate count, sources used, citation counts, prompt tokens, and execution latency.
- **`pigmentor chat`**: Launches an interactive query loop. Questions are answered independently against the index. Features per-turn Ctrl+C handling: interrupting a generation turn cancels the active query and returns to the `> ` prompt without exiting the chat session.
- **`pigmentor status [--check]`**: Displays operational readiness of the chat model, embedding service, and vector storage. With `--check`, executes an active connectivity and authentication probe (exits with code 1 if health check fails).

## Build & Test

```powershell
dotnet build Pigmentor.slnx
dotnet format --verify-no-changes Pigmentor.slnx
dotnet test Pigmentor.slnx
```
