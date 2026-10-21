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

## Quick Start & Model Setup

### 1. Clone & Download BGE-M3 Production Model Assets

```bash
git clone https://github.com/nik9999/Pigmentor.git
cd Pigmentor
```

Download the official BAAI BGE-M3 ONNX runtime assets:

**Windows (PowerShell):**
```powershell
./scripts/download-bge-m3.ps1
```
*(Or use `./scripts/download-bge-m3.ps1 -Quantized` for the single-file int8 ~568 MB model).*

**Linux (Bash):**
```bash
chmod +x ./scripts/download-bge-m3.sh
./scripts/download-bge-m3.sh
```

### 2. Model Asset Storage & Deployment Architecture

```text
Models/bge-m3/               <- Canonical repository source assets (ignored by Git)
    ├── model.onnx
    ├── model.onnx_data       (if using official FP32 graph)
    └── tokenizer.json
          │
    [dotnet build / publish]
          ↓
bin/.../Models/bge-m3/       <- Deployed runtime copy (relative to AppContext.BaseDirectory)
```

> [!NOTE]
> **Git Policy:** Multi-GB model weights (`*.onnx`, `*.onnx_data`, `*.bin`, `*.bpe.model`) and large tokenizer definitions (`tokenizer.json`) are deliberately excluded from Git via `.gitignore`.
> Only `Models/bge-m3/README.md` and `Models/bge-m3/.gitkeep` are tracked in version control.
> Unit and integration test suites run entirely on lightweight, deterministic fixtures in `tests/Pigmentor.Tests/Fixtures/` and never depend on downloading multi-GB production models.

### 3. Build & Verify

```powershell
dotnet build Pigmentor.slnx
dotnet test Pigmentor.slnx
```

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

### Target Runtime Configuration (`config.json`)

Configuration is stored in user application data (`%APPDATA%/Pigmentor/config.json` on Windows, `~/.config/pigmentor/config.json` on Linux) strictly without secrets:

```json
{
  "rootPath": "data",
  "chat": {
    "provider": "Ollama",
    "model": "gigachat3-32k:latest",
    "endpoint": "http://192.168.215.17:11434"
  },
  "embeddings": {
    "provider": "Onnx",
    "modelPath": "models/bge-m3/model.onnx",
    "tokenizerPath": "models/bge-m3/tokenizer.json",
    "dimensions": 1024
  }
}
```

> [!NOTE]
> **Deterministic Application Path Policy:**
> All relative application paths (`rootPath`, `embeddings.modelPath`, `embeddings.tokenizerPath`) are resolved against `AppContext.BaseDirectory` (the application / executable directory), **NOT** the shell's current working directory (`Environment.CurrentDirectory`).
> For instance, `rootPath = "data"` reliably resolves to `<AppContext.BaseDirectory>/data` and SQLite database to `<AppContext.BaseDirectory>/data/pigmentor.db` regardless of the shell directory from which `pigmentor` is launched.

### BGE-M3 ONNX Artifact Contract

Pigmentor's ONNX embedding pipeline natively integrates BGE-M3 dense embeddings:
- **Tokenizer**: Real Hugging Face tokenizer loaded directly from `tokenizerPath` (`tokenizer.json` format) via `Tokenizers.DotNet` (SentencePiece / XLM-RoBERTa vocabulary, NFKC normalization, whitespace / byte-level pre-tokenization). There is **no hash-tokenizer fallback**.
- **Graph Inputs**: `input_ids` and `attention_mask` (INT64 or INT32 tensors, `[batch, sequence_length]`), with optional `token_type_ids` if required by the exported graph.
- **Graph Outputs & Pooling**: Evaluates `sentence_embedding` / `dense_vecs` (2D tensor `[batch, 1024]`) or `last_hidden_state` (3D tensor `[batch, sequence_length, 1024]`). For 3D representations, dense embeddings use CLS token pooling (first token `[0, 0, :]`).
- **Normalization**: Strict L2 normalization ($||v||_2 = 1.0$).
- **Dimensionality**: Strictly 1024 dimensions. Runtime checks enforce exact dimensionality match against configured dimensions and reject mismatches immediately without silent truncation or padding.

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
