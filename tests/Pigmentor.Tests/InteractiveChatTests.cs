using Microsoft.Data.Sqlite;
using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Paths;
using Pigmentor.App.Runtime;
using Pigmentor.App.Security;
using Pigmentor.RAG.Cli.Commands;
using Pigmentor.RAG.Cli.Console;
using Pigmentor.Tests.Fixtures;

namespace Pigmentor.Tests;

public sealed class InteractiveChatTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly PigmentorAppPaths _paths;
    private readonly PigmentorConfigManager _configManager;
    private readonly InMemorySecretStore _secretStore;
    private readonly GigaChatCredentialResolver _credentialResolver;
    private readonly DeterministicSemanticEmbeddingService _embedder;
    private readonly DeterministicRagChatClient _chatClient;

    public InteractiveChatTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_chat_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        _paths = new PigmentorAppPaths(_tempDirectory, _tempDirectory);
        _configManager = new PigmentorConfigManager(_paths);
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

    private Task<PigmentorAppRuntime> CreateTestRuntimeAsync(CancellationToken ct)
    {
        var config = new PigmentorConfig
        {
            RootPath = _tempDirectory,
            Chat = new() { Provider = "Ollama", Model = "llama3" },
            Embeddings = new() { Provider = "Ollama", Model = "nomic-embed-text", Dimensions = 64 }
        };

        return PigmentorAppRuntime.CreateAsync(
            config: config,
            credentialProvider: _credentialResolver,
            chatClientOverride: _chatClient,
            embeddingServiceOverride: _embedder,
            cancellationToken: ct);
    }

    [Fact]
    public async Task Chat_HelpAndClear_ExplainIndependentQueriesWithoutMemory()
    {
        var console = new TestConsoleIO();
        console.EnqueueInput("/help");
        console.EnqueueInput("/clear");
        console.EnqueueInput("/exit");

        var handler = new ChatCommandHandler(CreateTestRuntimeAsync, console);
        var exitCode = await handler.ExecuteAsync();

        Assert.Equal(0, exitCode);
        var output = console.Output;

        Assert.Contains("Multi-turn conversational memory is not supported", output, StringComparison.Ordinal);
        Assert.Contains("does not maintain conversational history", output, StringComparison.Ordinal);
        Assert.Contains("Goodbye.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_PerTurnCancellation_CancelsTurnAndContinuesLoop()
    {
        var console = new TestConsoleIO();
        console.EnqueueInput("What is the baud rate?"); // First query - will be cancelled
        console.EnqueueInput("/exit");                  // Second query - exit cleanly

        Action? registeredCancelAction = null;

        var handler = new ChatCommandHandler(CreateTestRuntimeAsync, console)
        {
            TurnCancelRegistrar = (cancelAction) =>
            {
                registeredCancelAction = cancelAction;
                // Trigger turn cancellation immediately when generation turn starts
                cancelAction();
                return new DummyDisposable();
            }
        };

        var exitCode = await handler.ExecuteAsync();
        Assert.Equal(0, exitCode);

        var output = console.Output;
        Assert.Contains("Turn cancelled by user.", output, StringComparison.Ordinal);
        Assert.Contains("Goodbye.", output, StringComparison.Ordinal);
        Assert.NotNull(registeredCancelAction);
    }

    private sealed class DummyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
