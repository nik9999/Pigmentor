using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Generation;
using Pigmentor.GigaChat;
using Pigmentor.Hosting;
using Pigmentor.Ingestion.Chunking;
using Pigmentor.Ingestion.Extraction;
using Pigmentor.Ingestion.Lifecycle;
using Pigmentor.Ingestion.Normalization;
using Pigmentor.Ingestion.Services;
using Pigmentor.Retrieval;
using Pigmentor.VectorStore.SqliteVec;

namespace Pigmentor.App.Runtime;

/// <summary>
/// Composition root for Pigmentor applications.
/// Assembles public abstractions, services, and storage without duplicating kernel logic.
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

        // 1. Validate configuration
        var errors = PigmentorConfigManager.Validate(config);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid Pigmentor configuration:{Environment.NewLine}- " + string.Join(Environment.NewLine + "- ", errors));
        }

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
        else if (string.Equals(config.Chat.Provider, "GigaChat", StringComparison.OrdinalIgnoreCase))
        {
            var authKey = await credentialProvider.GetAuthorizationKeyAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(authKey))
            {
                throw new InvalidOperationException(
                    "GigaChat credentials are not configured. Run 'pigmentor auth gigachat' or set the PIGMENTOR_GIGACHAT_AUTH_KEY environment variable.");
            }

            var gigaChatOptions = new GigaChatClientOptions
            {
                AuthorizationKey = authKey,
                Scope = config.GigaChat.Scope,
                DefaultModel = config.Chat.Model,
                IgnoreSslValidation = !config.GigaChat.VerifySsl
            };

            if (!string.IsNullOrWhiteSpace(config.Chat.Endpoint) &&
                Uri.TryCreate(config.Chat.Endpoint, UriKind.Absolute, out var endpointUri))
            {
                gigaChatOptions.BaseUrl = endpointUri;
            }

            var gigaChatClient = new GigaChatClient(gigaChatOptions);
            services.AddSingleton(gigaChatClient);
            services.AddSingleton<IChatClient>(_ => gigaChatClient.GetChatClient(config.Chat.Model));
        }
        else if (string.Equals(config.Chat.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = new Uri(config.Chat.Endpoint);
            services.AddSingleton<IChatClient>(_ => new OllamaApiClient(endpoint, config.Chat.Model));
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
                if (string.Equals(config.Embeddings.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
                {
                    var endpoint = new Uri(config.Embeddings.Endpoint);
                    generator = new OllamaApiClient(endpoint, config.Embeddings.Model);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported embedding provider: '{config.Embeddings.Provider}'.");
                }
            }

            services.AddSingleton(generator);
            services.AddSingleton<IEmbeddingService>(_ =>
                new GeneratorEmbeddingService(generator, config.Embeddings.Model, config.Embeddings.Dimensions));
        }

        // 4. Storage setup (SQLite + sqlite-vec)
        var dbPath = Path.GetFullPath(config.Storage.DatabasePath!);
        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dbDir))
        {
            Directory.CreateDirectory(dbDir);
        }

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
        return new PigmentorAppRuntime(provider);
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

