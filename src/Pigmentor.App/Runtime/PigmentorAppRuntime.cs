using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Paths;
using Pigmentor.Core.Abstractions;
using Pigmentor.Generation;
using Pigmentor.GigaChat;
using Pigmentor.Hosting;
using Pigmentor.Ingestion.Chunking;
using Pigmentor.Ingestion.Extraction;
using Pigmentor.Ingestion.Normalization;
using Pigmentor.Ingestion.Services;
using Pigmentor.Retrieval;
using Pigmentor.VectorStore.SqliteVec;

namespace Pigmentor.App.Runtime;

/// <summary>
/// Composition root for Pigmentor applications.
/// Assembles public abstractions, services, and storage without duplicating kernel logic.
/// Clear single-owner resource lifecycle: DI container (ServiceProvider) owns and disposes all runtime-created services.
/// External overrides are caller-owned and never disposed by the runtime.
/// </summary>
public sealed class PigmentorAppRuntime : IDisposable, IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private bool _disposed;

    public IServiceProvider Services => _serviceProvider;
    public IRagPipeline Pipeline => _serviceProvider.GetRequiredService<IRagPipeline>();
    public IDocumentIngestionService IngestionService => _serviceProvider.GetRequiredService<IDocumentIngestionService>();
    public IVectorStore VectorStore => _serviceProvider.GetRequiredService<IVectorStore>();
    public IIngestionStateStore StateStore => _serviceProvider.GetRequiredService<IIngestionStateStore>();

    private PigmentorAppRuntime(ServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Creates a new runtime instance based on configuration and resolved credentials.
    /// Supports dependency injection overrides for unit/integration testing.
    /// </summary>
    public static async Task<PigmentorAppRuntime> CreateAsync(
        PigmentorConfig config,
        IGigaChatCredentialProvider credentialProvider,
        IPigmentorAppPaths? appPaths = null,
        IChatClient? chatClientOverride = null,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGeneratorOverride = null,
        IEmbeddingService? embeddingServiceOverride = null,
        IVectorStore? vectorStoreOverride = null,
        IIngestionStateStore? stateStoreOverride = null,
        IOcrEngine? ocrEngineOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(credentialProvider);
        cancellationToken.ThrowIfCancellationRequested();

        var paths = appPaths ?? new PigmentorAppPaths();

        // 1. Validate configuration against effective paths
        var errors = PigmentorConfigManager.Validate(config, paths);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid Pigmentor configuration:{Environment.NewLine}- " + string.Join(Environment.NewLine + "- ", errors));
        }

        var normalizedRoot = paths.ResolveEffectiveRootPath(config.RootPath);
        paths.EnsureWorkspaceDirectories(normalizedRoot);
        var dbPath = paths.GetDatabasePath(normalizedRoot);

        var services = new ServiceCollection();

        // Add logging (NullLogger by default)
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Use Hosting baseline
        var builder = services.AddPigmentor();

        // 1. Reranker setup: Cross-encoder (bge-reranker-v2-m3) if enabled, otherwise TableAwareLexicalReranker
        if (config.Reranker?.Enabled == true)
        {
            var rerankerModelPath = paths.ResolveApplicationPath(config.Reranker.ModelPath ?? "models/bge-reranker-v2-m3/model.onnx");
            var rerankerTokenizerPath = paths.ResolveApplicationPath(
                config.Reranker.TokenizerPath ?? config.Embeddings?.TokenizerPath ?? "models/bge-reranker-v2-m3/tokenizer.json");

            services.Replace(ServiceDescriptor.Singleton<IReranker>(sp =>
            {
                return new OnnxCrossEncoderReranker(
                    rerankerModelPath,
                    rerankerTokenizerPath,
                    maxPairsPerBatch: config.Reranker.MaxPairsPerBatch > 0 ? config.Reranker.MaxPairsPerBatch : 16,
                    maxSequenceLength: config.Reranker.MaxSequenceLength > 0 ? config.Reranker.MaxSequenceLength : 512,
                    applySigmoid: true);
            }));
        }
        else
        {
            services.Replace(ServiceDescriptor.Singleton<IReranker, TableAwareLexicalReranker>());
        }

        services.Configure<RagPipelineSettings>(options =>
        {
            options.Budget = new ContextBudget(MaxTotalTokens: 8192, ReservedOutputTokens: 1024);
            options.DefaultTopK = 8;
            if (config.Reranker?.Enabled == true)
            {
                options.RetrievalPoolSize = Math.Max(50, options.DefaultTopK * 4);
                if (config.Reranker.MinScore.HasValue)
                {
                    options.MinRerankerScore = config.Reranker.MinScore.Value;
                }
            }
        });

        // 2. Chat Client setup
        if (chatClientOverride != null)
        {
            services.AddSingleton(chatClientOverride);
        }
        else if (string.Equals(config.Chat!.Provider, "GigaChat", StringComparison.OrdinalIgnoreCase))
        {
            var authKey = await credentialProvider.GetAuthorizationKeyAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(authKey))
            {
                throw new InvalidOperationException(
                    "GigaChat credentials are not configured. Run 'pigmentor auth gigachat' or set the PIGMENTOR_GIGACHAT_AUTH_KEY environment variable.");
            }

            if (string.IsNullOrWhiteSpace(config.Chat.Endpoint) ||
                !Uri.TryCreate(config.Chat.Endpoint, UriKind.Absolute, out var endpointUri) ||
                (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException($"Chat endpoint '{config.Chat.Endpoint}' is not a valid absolute HTTP or HTTPS URI.");
            }

            var gigaChatOptions = new GigaChatClientOptions
            {
                AuthorizationKey = authKey,
                Scope = config.GigaChat?.Scope ?? "GIGACHAT_API_PERS",
                DefaultModel = config.Chat.Model,
                BaseUrl = endpointUri,
                IgnoreSslValidation = config.GigaChat != null && !config.GigaChat.VerifySsl
            };

            services.AddSingleton(_ => new GigaChatClient(gigaChatOptions));
            services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<GigaChatClient>().GetChatClient(config.Chat.Model));
        }
        else if (string.Equals(config.Chat.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(config.Chat.Endpoint) ||
                !Uri.TryCreate(config.Chat.Endpoint, UriKind.Absolute, out var endpointUri) ||
                (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException($"Chat endpoint '{config.Chat.Endpoint}' is not a valid absolute HTTP or HTTPS URI.");
            }

            services.AddSingleton(_ => new OllamaApiClient(endpointUri, config.Chat.Model));
            services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<OllamaApiClient>());
        }
        else
        {
            throw new InvalidOperationException($"Unsupported chat provider: '{config.Chat.Provider}'.");
        }

        // 3. Embedding Generator & Embedding Service setup
        if (embeddingServiceOverride != null)
        {
            services.AddSingleton(embeddingServiceOverride);
        }
        else
        {
            if (embeddingGeneratorOverride != null)
            {
                services.AddSingleton(embeddingGeneratorOverride);
            }
            else if (string.Equals(config.Embeddings!.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(config.Embeddings.Endpoint) ||
                    !Uri.TryCreate(config.Embeddings.Endpoint, UriKind.Absolute, out var embUri) ||
                    (embUri.Scheme != Uri.UriSchemeHttp && embUri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException($"Embeddings endpoint '{config.Embeddings.Endpoint}' is not a valid absolute HTTP or HTTPS URI.");
                }

                services.AddSingleton(_ => new OllamaApiClient(embUri, config.Embeddings.Model));
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => sp.GetRequiredService<OllamaApiClient>());
            }
            else if (string.Equals(config.Embeddings.Provider, "Onnx", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(config.Embeddings.ModelPath))
                {
                    throw new InvalidOperationException("ONNX embedding ModelPath is required when Provider is 'Onnx'.");
                }

                if (string.IsNullOrWhiteSpace(config.Embeddings.TokenizerPath))
                {
                    throw new InvalidOperationException("ONNX embedding TokenizerPath is required when Provider is 'Onnx'.");
                }

                var resolvedModel = paths.ResolveApplicationPath(config.Embeddings.ModelPath);
                var resolvedTokenizer = paths.ResolveApplicationPath(config.Embeddings.TokenizerPath);

                services.AddSingleton(_ => new OnnxEmbeddingGenerator(
                    resolvedModel,
                    resolvedTokenizer,
                    config.Embeddings.Dimensions));
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => sp.GetRequiredService<OnnxEmbeddingGenerator>());
            }
            else
            {
                throw new NotSupportedException($"Unsupported embedding provider: '{config.Embeddings.Provider}'.");
            }

            var embeddingModelName = config.Embeddings!.Model ?? Path.GetFileNameWithoutExtension(config.Embeddings.ModelPath) ?? "default";
            services.AddSingleton<IEmbeddingService>(sp =>
            {
                var generator = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
                return new GeneratorEmbeddingService(generator, embeddingModelName, config.Embeddings.Dimensions);
            });
        }

        // 4. Storage setup (SQLite + sqlite-vec)
        var connectionString = $"Data Source={dbPath}";

        if (vectorStoreOverride != null)
        {
            services.AddSingleton(vectorStoreOverride);
        }
        else
        {
            services.AddSingleton<IVectorStore>(sp =>
            {
                var emb = sp.GetRequiredService<IEmbeddingService>();
                var opts = new SqliteVecStoreOptions
                {
                    ConnectionString = connectionString,
                    Dimensions = emb.Dimensions,
                    EmbeddingModel = emb.EmbeddingModel
                };
                return new SqliteVecStore(opts);
            });
        }

        if (stateStoreOverride != null)
        {
            services.AddSingleton(stateStoreOverride);
        }
        else
        {
            services.AddSingleton<IIngestionStateStore>(_ => new SqliteIngestionStateStore(connectionString));
        }

        // 5. Retrieval setup (Hybrid: Dense vector search + Sparse BM25 fused with RRF)
        services.AddSingleton<SparseRetriever>();
        services.AddSingleton<DenseRetriever>(sp =>
        {
            var emb = sp.GetRequiredService<IEmbeddingService>();
            var vec = sp.GetRequiredService<IVectorStore>();
            return new DenseRetriever(emb, vec);
        });
        services.AddSingleton<IRetriever>(sp =>
        {
            var dense = sp.GetRequiredService<DenseRetriever>();
            var sparse = sp.GetRequiredService<SparseRetriever>();
            var hybridOptions = new HybridRetrievalOptions
            {
                CandidatePoolPerLeg = config.Retrieval?.CandidatePoolPerLeg ?? 50,
                RrfK = config.Retrieval?.RrfK ?? 60,
                DenseWeight = config.Retrieval?.DenseWeight ?? 1.0,
                SparseWeight = config.Retrieval?.SparseWeight ?? 1.0,
                LegTimeout = TimeSpan.FromSeconds(config.Retrieval?.LegTimeoutSeconds ?? 10.0)
            };
            return new HybridRetriever(dense, sparse, hybridOptions);
        });

        // 6. Extraction, Chunker, and Ingestion setup
        builder.AddDocumentExtraction();
        if (ocrEngineOverride != null)
        {
            builder.UseOcrEngine(ocrEngineOverride);
        }

        services.AddSingleton<IChunker, StructuralDocumentChunker>();
        services.AddSingleton<IDocumentIngestionService, DocumentIngestionService>();

        var provider = services.BuildServiceProvider();

        // 7. Warm up SparseRetriever with all persisted chunks
        var vectorStore = provider.GetRequiredService<IVectorStore>();
        var sparseRetriever = provider.GetRequiredService<SparseRetriever>();
        var allChunks = await vectorStore.GetAllChunksAsync(cancellationToken).ConfigureAwait(false);
        if (allChunks.Count > 0)
        {
            sparseRetriever.Index(allChunks);
        }

        return new PigmentorAppRuntime(provider);
    }

    /// <summary>
    /// Reloads all chunks from persistent storage and updates the in-memory sparse (BM25) index.
    /// </summary>
    public async Task RefreshSparseIndexAsync(CancellationToken cancellationToken = default)
    {
        var vectorStore = _serviceProvider.GetRequiredService<IVectorStore>();
        var sparse = _serviceProvider.GetRequiredService<SparseRetriever>();
        var allChunks = await vectorStore.GetAllChunksAsync(cancellationToken).ConfigureAwait(false);
        sparse.Index(allChunks);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _serviceProvider.Dispose();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _serviceProvider.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
