# Pigmentor.Core

Core abstractions and primitives for the **Pigmentor** Retrieval-Augmented Generation
stack — the companion library to *Production-Grade RAG with C# and .NET*.

This is the foundation every other `Pigmentor.*` package builds on. It defines the
domain ports (`IRetriever`, `IVectorStore`, `IEmbeddingService`), the document model
(`DocumentChunk`, `RetrievalResult`, `DocumentMetadata`), configuration options, and
token counting — with no dependency on any specific vector database, LLM provider, or
hosting model.

The abstractions and document model are reflection-free; the reflection-based
configuration binding lives behind the `AddPigmentorCore` DI helper, so trimmed/AOT
hosts can register options another way and keep the rest of the surface lean.

## Install

```bash
dotnet add package Pigmentor.Core
```

## License

MIT © Rachid Dahir
