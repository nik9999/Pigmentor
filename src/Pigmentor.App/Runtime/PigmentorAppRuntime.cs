using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
/// Explicitly manages the lifecycle of resources it creates.
/// </summary>
public sealed class PigmentorAppRuntime : IDisposable, IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly List<IDisposable> _ownedDisposables;
    private readonly List<IAsyncDisposable> _ownedAsyncDisposables;
    private bool _disposed;

    public IServiceProvider Services => _serviceProvider;
    public IRagPipeline Pipeline => _serviceProvider.GetRequiredService<IRagPipeline>();
    public IDocumentIngestionService IngestionService => _serviceProvider.GetRequiredService<IDocumentIngestionService>();
    public IVectorStore VectorStore => _serviceProvider.GetRequiredService<IVectorStore>();
    public IIngestionStateStore StateStore => _serviceProvider.GetRequiredService<IIngestionStateStore>();

    private PigmentorAppRuntime(
        ServiceProvider serviceProvider,
        List<IDisposable> ownedDisposables,
        List<IAsyncDisposable> ownedAsyncDisposables)
    {
        _serviceProvider = serviceProvider;
        _ownedDisposables = ownedDisposables;
        _ownedAsyncDisposables = ownedAsyncDisposables;
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

        // 1. Validate configuration
        var errors = PigmentorConfigManager.Validate(config);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid Pigmentor configuration:{Environment.NewLine}- " + string.Join(Environment.NewLine + "- ", errors));
        }

        var paths = appPaths ?? new PigmentorAppPaths();
        var normalizedRoot = paths.ResolveEffectiveRootPath(config.RootPath);
        paths.EnsureWorkspaceDirectories(normalizedRoot);
        var dbPath = paths.GetDatabasePath(normalizedRoot);

        var ownedDisposables = new List<IDisposable>();
        var ownedAsyncDisposables = new List<IAsyncDisposable>();
        var services = new ServiceCollection();

        // Add logging (NullLogger by default)
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Use Hosting baseline
        var builder = services.AddPigmentor();

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

            var gigaChatClient = new GigaChatClient(gigaChatOptions);
            ownedDisposables.Add(gigaChatClient);
            services.AddSingleton(gigaChatClient);
            services.AddSingleton<IChatClient>(_ => gigaChatClient.GetChatClient(config.Chat.Model));
        }
        else if (string.Equals(config.Chat.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(config.Chat.Endpoint) ||
                !Uri.TryCreate(config.Chat.Endpoint, UriKind.Absolute, out var endpointUri) ||
                (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException($"Chat endpoint '{config.Chat.Endpoint}' is not a valid absolute HTTP or HTTPS URI.");
            }

            var ollamaClient = new OllamaApiClient(endpointUri, config.Chat.Model);
            ownedDisposables.Add(ollamaClient);
            services.AddSingleton<IChatClient>(ollamaClient);
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
            IEmbeddingGenerator<string, Embedding<float>>? generator = embeddingGeneratorOverride;

            if (generator == null)
            {
                if (string.Equals(config.Embeddings!.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(config.Embeddings.Endpoint) ||
                        !Uri.TryCreate(config.Embeddings.Endpoint, UriKind.Absolute, out var embUri) ||
                        (embUri.Scheme != Uri.UriSchemeHttp && embUri.Scheme != Uri.UriSchemeHttps))
                    {
                        throw new InvalidOperationException($"Embeddings endpoint '{config.Embeddings.Endpoint}' is not a valid absolute HTTP or HTTPS URI.");
                    }

                    var ollamaEmbClient = new OllamaApiClient(embUri, config.Embeddings.Model);
                    generator = ollamaEmbClient;
                    ownedDisposables.Add(ollamaEmbClient);
                }
                else if (string.Equals(config.Embeddings.Provider, "Onnx", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(config.Embeddings.ModelPath))
                    {
                        throw new InvalidOperationException("ONNX embedding ModelPath is required when Provider is 'Onnx'.");
                    }

                    var onnxGen = new OnnxEmbeddingGenerator(
                        config.Embeddings.ModelPath,
                        config.Embeddings.TokenizerPath,
                        config.Embeddings.Dimensions);

                    generator = onnxGen;
                    ownedDisposables.Add(onnxGen);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported embedding provider: '{config.Embeddings.Provider}'.");
                }
            }

            var embeddingModelName = config.Embeddings!.Model ?? Path.GetFileNameWithoutExtension(config.Embeddings.ModelPath) ?? "default";
            var embeddingService = new GeneratorEmbeddingService(generator, embeddingModelName, config.Embeddings.Dimensions);

            services.AddSingleton(generator);
            services.AddSingleton<IEmbeddingService>(embeddingService);
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
                var store = new SqliteVecStore(opts);
                lock (ownedDisposables)
                {
                    ownedDisposables.Add(store);
                    ownedAsyncDisposables.Add(store);
                }
                return store;
            });
        }

        if (stateStoreOverride != null)
        {
            services.AddSingleton(stateStoreOverride);
        }
        else
        {
            services.AddSingleton<IIngestionStateStore>(_ =>
            {
                var store = new SqliteIngestionStateStore(connectionString);
                lock (ownedDisposables)
                {
                    ownedDisposables.Add(store);
                    ownedAsyncDisposables.Add(store);
                }
                return store;
            });
        }

        // 5. Retrieval setup
        services.AddSingleton<IRetriever>(sp =>
        {
            var emb = sp.GetRequiredService<IEmbeddingService>();
            var vec = sp.GetRequiredService<IVectorStore>();
            return new DenseRetriever(emb, vec);
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
        ownedDisposables.Add(provider);
        ownedAsyncDisposables.Add(provider);

        return new PigmentorAppRuntime(provider, ownedDisposables, ownedAsyncDisposables);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        for (var i = _ownedDisposables.Count - 1; i >= 0; i--)
        {
            try
            {
                _ownedDisposables[i].Dispose();
            }
            catch
            {
                // Best effort cleanup
            }
        }

        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        for (var i = _ownedAsyncDisposables.Count - 1; i >= 0; i--)
        {
            try
            {
                await _ownedAsyncDisposables[i].DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort cleanup
            }
        }

        Dispose();
    }
}
