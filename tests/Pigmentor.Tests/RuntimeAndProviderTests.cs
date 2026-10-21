using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Paths;
using Pigmentor.App.Runtime;
using Pigmentor.App.Security;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Generation;
using Pigmentor.Tests.Fixtures;

namespace Pigmentor.Tests;

public sealed class RuntimeAndProviderTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly PigmentorAppPaths _paths;
    private readonly InMemorySecretStore _secretStore;
    private readonly GigaChatCredentialResolver _credentialResolver;
    private readonly DeterministicSemanticEmbeddingService _embedder;
    private readonly DeterministicRagChatClient _chatClient;

    public RuntimeAndProviderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_runtime_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        _paths = new PigmentorAppPaths(_tempDirectory, _tempDirectory);
        _secretStore = new InMemorySecretStore();
        _credentialResolver = new GigaChatCredentialResolver(_secretStore);
        _embedder = new DeterministicSemanticEmbeddingService(64);
        _chatClient = new DeterministicRagChatClient();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Theory]
    [InlineData("Ollama", "Ollama")]
    [InlineData("GigaChat", "Ollama")]
    [InlineData("Ollama", "Onnx")]
    [InlineData("GigaChat", "Onnx")]
    public async Task Runtime_SupportsAllFourProviderCombinations(string chatProvider, string embeddingsProvider)
    {
        var config = new PigmentorConfig
        {
            RootPath = _tempDirectory,
            Chat = new()
            {
                Provider = chatProvider,
                Model = chatProvider == "GigaChat" ? "GigaChat-Pro" : "llama3.2",
                Endpoint = chatProvider == "GigaChat" ? "https://api.giga.chat/v1" : "http://localhost:11434"
            },
            Embeddings = new()
            {
                Provider = embeddingsProvider,
                Model = "nomic-embed-text",
                Endpoint = "http://localhost:11434",
                Dimensions = 64
            }
        };

        // If GigaChat is used, provide credentials
        if (chatProvider == "GigaChat")
        {
            await _secretStore.SetAsync(GigaChatCredentialResolver.SecretStoreKeyName, "dGVzdC1hdXRoLWtleQ==");
        }

        if (embeddingsProvider == "Onnx")
        {
            var fakeOnnx = Path.Combine(_tempDirectory, "model.onnx");
            if (!File.Exists(fakeOnnx))
            {
                await File.WriteAllBytesAsync(fakeOnnx, [0x08, 0x01]);
            }
            config.Embeddings!.ModelPath = fakeOnnx;
        }

        // Test runtime creation with mock clients (avoiding external network calls in unit tests)
        await using var runtime = await PigmentorAppRuntime.CreateAsync(
            config: config,
            credentialProvider: _credentialResolver,
            chatClientOverride: _chatClient,
            embeddingServiceOverride: _embedder);

        Assert.NotNull(runtime.Pipeline);
        Assert.NotNull(runtime.IngestionService);
        Assert.NotNull(runtime.VectorStore);
    }

    [Theory]
    [InlineData("not-a-valid-uri")]
    [InlineData("ftp://localhost:11434")]
    [InlineData("file:///etc/hosts")]
    [InlineData("/relative/path")]
    public async Task Runtime_StrictEndpointValidation_RejectsInvalidEndpointsWithoutSilentFallback(string invalidEndpoint)
    {
        var config = new PigmentorConfig
        {
            RootPath = _tempDirectory,
            Chat = new()
            {
                Provider = "Ollama",
                Endpoint = invalidEndpoint
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PigmentorAppRuntime.CreateAsync(
                config: config,
                credentialProvider: _credentialResolver,
                chatClientOverride: null,
                embeddingServiceOverride: _embedder));

        Assert.Contains("is not a valid absolute HTTP or HTTPS URI", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_DoesNotDisposeExternalOverrides_WhenDisposed()
    {
        var config = new PigmentorConfig
        {
            RootPath = _tempDirectory,
            Chat = new() { Provider = "Ollama" },
            Embeddings = new() { Provider = "Ollama", Dimensions = 64 }
        };

        var disposableChatClient = new TrackingDisposableChatClient();
        var disposableEmbedder = new TrackingDisposableEmbeddingService();

        var runtime = await PigmentorAppRuntime.CreateAsync(
            config: config,
            credentialProvider: _credentialResolver,
            chatClientOverride: disposableChatClient,
            embeddingServiceOverride: disposableEmbedder);

        // Dispose runtime
        await runtime.DisposeAsync();

        // The external instances must NOT have been disposed
        Assert.False(disposableChatClient.IsDisposed);
        Assert.False(disposableEmbedder.IsDisposed);
    }

    private sealed class TrackingDisposableChatClient : IChatClient, IDisposable
    {
        private readonly DeterministicRagChatClient _inner = new();
        public bool IsDisposed { get; private set; }

        public ChatClientMetadata Metadata => _inner.Metadata;

        public void Dispose() => IsDisposed = true;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _inner.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);
    }

    private sealed class TrackingDisposableEmbeddingService : IEmbeddingService, IDisposable
    {
        private readonly DeterministicSemanticEmbeddingService _inner = new(64);
        public bool IsDisposed { get; private set; }

        public string EmbeddingModel => _inner.EmbeddingModel;
        public int Dimensions => _inner.Dimensions;

        public void Dispose() => IsDisposed = true;

        public Task<EmbeddedChunk> EmbedAsync(DocumentChunk chunk, CancellationToken cancellationToken = default)
            => _inner.EmbedAsync(chunk, cancellationToken);

        public Task<ReadOnlyMemory<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
            => _inner.EmbedQueryAsync(query, cancellationToken);

        public IAsyncEnumerable<EmbeddedChunk> EmbedAsync(IAsyncEnumerable<DocumentChunk> chunks, CancellationToken cancellationToken = default)
            => _inner.EmbedAsync(chunks, cancellationToken);
    }
}
