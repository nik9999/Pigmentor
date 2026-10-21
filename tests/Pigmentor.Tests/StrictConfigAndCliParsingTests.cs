using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Paths;
using Pigmentor.App.Runtime;
using Pigmentor.App.Security;
using Pigmentor.RAG.Cli.Commands;
using Pigmentor.RAG.Cli.Console;
using Pigmentor.Tests.Fixtures;

namespace Pigmentor.Tests;

public sealed class StrictConfigAndCliParsingTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly PigmentorAppPaths _paths;
    private readonly PigmentorConfigManager _configManager;
    private readonly InMemorySecretStore _secretStore;
    private readonly GigaChatCredentialResolver _credentialResolver;
    private readonly DeterministicSemanticEmbeddingService _embedder;
    private readonly DeterministicRagChatClient _chatClient;

    public StrictConfigAndCliParsingTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_strict_test_{Guid.NewGuid():N}");
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

    private CliRouter CreateRouter(IConsoleIO console)
    {
        return new CliRouter(
            _paths,
            _secretStore,
            _credentialResolver,
            _configManager,
            CreateTestRuntimeAsync,
            console);
    }

    [Theory]
    [InlineData("{\"rootPath\": \"D:\\\\data\", \"authorizationKey\": \"secret\"}")]
    [InlineData("{\"rootPath\": \"D:\\\\data\", \"key\": \"secret\"}")]
    [InlineData("{\"rootPath\": \"D:\\\\data\", \"password\": \"secret\"}")]
    [InlineData("{\"rootPath\": \"D:\\\\data\", \"token\": \"secret\"}")]
    [InlineData("{\"rootPath\": \"D:\\\\data\", \"credential\": \"secret\"}")]
    [InlineData("{\"rootPath\": \"D:\\\\data\", \"chat\": {\"provider\": \"Ollama\", \"model\": \"llama3\", \"secret\": \"x\"}}")]
    [InlineData("{\"rootPath\": \"D:\\\\data\", \"gigachat\": {\"scope\": \"GIGACHAT_API_PERS\", \"apiKey\": \"x\"}}")]
    public async Task ConfigManager_LoadAsync_RejectsForbiddenSecretsInJson(string invalidJson)
    {
        _paths.EnsureConfigDirectoryCreated();
        await File.WriteAllTextAsync(_paths.ConfigFilePath, invalidJson);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _configManager.LoadAsync());
        Assert.Contains("contains forbidden secret property", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Use 'pigmentor auth gigachat'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"rootPath\": \"D:\\\\data\", \"unknownTopLevel\": 123}")]
    [InlineData("{\"rootPath\": \"D:\\\\data\", \"chat\": {\"provider\": \"Ollama\", \"nonExistentProperty\": true}}")]
    public async Task ConfigManager_LoadAsync_RejectsUnknownPropertiesInJson(string jsonWithUnknown)
    {
        _paths.EnsureConfigDirectoryCreated();
        await File.WriteAllTextAsync(_paths.ConfigFilePath, jsonWithUnknown);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _configManager.LoadAsync());
        Assert.Contains("contains unknown property", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigManager_LoadAsync_GracefullyHandlesNullSectionsWithoutNpe()
    {
        var jsonWithNullSections = """
        {
            "rootPath": "D:\\PigmentorData",
            "chat": null,
            "embeddings": null,
            "gigachat": null
        }
        """;

        _paths.EnsureConfigDirectoryCreated();
        await File.WriteAllTextAsync(_paths.ConfigFilePath, jsonWithNullSections);

        // Does not throw NPE on load
        var config = await _configManager.LoadAsync();
        Assert.NotNull(config);
        Assert.Equal("D:\\PigmentorData", config.RootPath);
        Assert.Null(config.Chat);
        Assert.Null(config.Embeddings);
        Assert.Null(config.GigaChat);

        // Validate catches the null sections gracefully
        var errors = PigmentorConfigManager.Validate(config);
        Assert.Contains(errors, e => e.Contains("'chat' configuration section cannot be null", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'embeddings' configuration section cannot be null", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'gigachat' configuration section cannot be null", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CliRouter_UnknownCommand_ReturnsExitCode2()
    {
        var console = new TestConsoleIO();
        var router = CreateRouter(console);

        var exitCode = await router.RouteAsync(["nonexistent"]);
        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown command 'nonexistent'", console.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRouter_UnknownFlagForAsk_ReturnsExitCode2()
    {
        var console = new TestConsoleIO();
        var router = CreateRouter(console);

        var exitCode = await router.RouteAsync(["ask", "What is RS-485?", "--strem"]);
        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown option '--strem'", console.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRouter_UnknownFlagForStatus_ReturnsExitCode2()
    {
        var console = new TestConsoleIO();
        var router = CreateRouter(console);

        var exitCode = await router.RouteAsync(["status", "--invalid-flag"]);
        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown option '--invalid-flag'", console.AllOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("init extra")]
    [InlineData("sync extra")]
    [InlineData("chat extra")]
    [InlineData("ingest file.txt extra")]
    [InlineData("auth gigachat extra")]
    [InlineData("auth logout invalid_target")]
    [InlineData("config show extra")]
    [InlineData("config set chat.model m extra")]
    public async Task CliRouter_ExtraArguments_ReturnExitCode2(string commandLine)
    {
        var console = new TestConsoleIO();
        var router = CreateRouter(console);

        var args = commandLine.Split(' ');
        var exitCode = await router.RouteAsync(args);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task CliRouter_Ask_JoinsMultiWordQuestionWithoutQuotes()
    {
        await _configManager.InitializeAsync(_tempDirectory);
        var console = new TestConsoleIO();
        var router = CreateRouter(console);

        // Multiple separate words passed on CLI
        var exitCode = await router.RouteAsync(["ask", "What", "is", "the", "operating", "voltage?"]);
        Assert.Equal(0, exitCode);
        Assert.Contains("контекст", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CliRouter_StatusCheck_ExitsWithCode1_WhenProviderUnreachable()
    {
        await _configManager.InitializeAsync(_tempDirectory);
        // Configure an unreachable endpoint for check
        await _configManager.SetSettingAsync("chat.endpoint", "http://127.0.0.1:59999");
        await _configManager.SetSettingAsync("embeddings.endpoint", "http://127.0.0.1:59999");

        var console = new TestConsoleIO();
        var router = CreateRouter(console);

        var exitCode = await router.RouteAsync(["status", "--check"]);
        Assert.Equal(1, exitCode);
        Assert.Contains("FAILED", console.Output, StringComparison.Ordinal);
    }
}
