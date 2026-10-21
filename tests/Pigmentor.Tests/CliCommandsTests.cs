using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Paths;
using Pigmentor.App.Security;
using Pigmentor.RAG.Cli.Commands;
using Pigmentor.RAG.Cli.Console;
using Xunit;

namespace Pigmentor.Tests;

public sealed class CliCommandsTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly PigmentorAppPaths _paths;
    private readonly PigmentorConfigManager _configManager;
    private readonly InMemorySecretStore _secretStore;
    private readonly GigaChatCredentialResolver _credentialResolver;

    public CliCommandsTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _paths = new PigmentorAppPaths(_tempDirectory, _tempDirectory);
        _configManager = new PigmentorConfigManager(_paths);
        _secretStore = new InMemorySecretStore();
        _credentialResolver = new GigaChatCredentialResolver(_secretStore);
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

    [Fact]
    public async Task InitCommand_IsIdempotent_DoesNotOverwriteExisting()
    {
        var console1 = new TestConsoleIO();
        var handler1 = new InitCommandHandler(_paths, _configManager, console1);

        // 1. First init
        var exitCode1 = await handler1.ExecuteAsync();
        Assert.Equal(0, exitCode1);
        Assert.Contains("workspace initialized", console1.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(_paths.ConfigFilePath));

        // Modify config to prove it's not overwritten
        await _configManager.SetSettingAsync("chat.model", "CustomModel-v1");

        // 2. Second init
        var console2 = new TestConsoleIO();
        var handler2 = new InitCommandHandler(_paths, _configManager, console2);
        var exitCode2 = await handler2.ExecuteAsync();

        Assert.Equal(0, exitCode2);
        Assert.Contains("already exists", console2.Output, StringComparison.Ordinal);

        var config = await _configManager.LoadAsync();
        Assert.Equal("CustomModel-v1", config.Chat?.Model);
    }

    [Fact]
    public async Task AuthCommand_SetGigaChat_SecurelySavesSecret_WithoutEcho()
    {
        var console = new TestConsoleIO();
        console.EnqueueSecretInput("my-secret-key-12345");

        var handler = new AuthCommandHandler(_secretStore, _credentialResolver, _configManager, console);

        var exitCode = await handler.SetGigaChatAsync();
        Assert.Equal(0, exitCode);
        Assert.Contains("Credentials saved securely.", console.Output, StringComparison.Ordinal);

        // Verify stored in secret store
        var stored = await _secretStore.GetAsync(GigaChatCredentialResolver.SecretStoreKeyName);
        Assert.Equal("my-secret-key-12345", stored);

        // Verify console output has prompt and NO echo of the key
        Assert.Contains("GigaChat Authorization Key:", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("my-secret-key-12345", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthCommand_Logout_RemovesSecretFromStore()
    {
        await _secretStore.SetAsync(GigaChatCredentialResolver.SecretStoreKeyName, "old-key");
        Assert.NotNull(await _secretStore.GetAsync(GigaChatCredentialResolver.SecretStoreKeyName));

        var console = new TestConsoleIO();
        var handler = new AuthCommandHandler(_secretStore, _credentialResolver, _configManager, console);

        var exitCode = await handler.LogoutGigaChatAsync();
        Assert.Equal(0, exitCode);
        Assert.Contains("removed from secure storage", console.Output, StringComparison.Ordinal);

        Assert.Null(await _secretStore.GetAsync(GigaChatCredentialResolver.SecretStoreKeyName));
    }

    [Fact]
    public async Task ConfigCommand_SetAndShow_UpdatesAndDisplaysSettings()
    {
        var console = new TestConsoleIO();
        var handler = new ConfigCommandHandler(_paths, _configManager, console);

        var setCode = await handler.SetAsync("chat.provider", "Ollama");
        Assert.Equal(0, setCode);

        var setDims = await handler.SetAsync("embeddings.dimensions", "1024");
        Assert.Equal(0, setDims);

        var showConsole = new TestConsoleIO();
        var showHandler = new ConfigCommandHandler(_paths, _configManager, showConsole);
        var showCode = await showHandler.ShowAsync();

        Assert.Equal(0, showCode);
        Assert.Contains("Provider:            Ollama", showConsole.Output, StringComparison.Ordinal);
        Assert.Contains("Dimensions:          1024", showConsole.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusCommand_DisplaysReadiness()
    {
        var console = new TestConsoleIO();
        var handler = new StatusCommandHandler(_paths, _configManager, _credentialResolver, console);

        var exitCode = await handler.ExecuteAsync(check: false);
        Assert.Equal(0, exitCode);

        var output = console.Output;
        Assert.Contains("Pigmentor Operational Status", output, StringComparison.Ordinal);
        Assert.Contains("Chat:", output, StringComparison.Ordinal);
        Assert.Contains("Embeddings:", output, StringComparison.Ordinal);
        Assert.Contains("Workspace:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRouter_RoutesHelpAndUnknownCommands()
    {
        var console = new TestConsoleIO();
        var router = new CliRouter(
            _paths,
            _secretStore,
            _credentialResolver,
            _configManager,
            _ => Task.FromResult<Pigmentor.App.Runtime.PigmentorAppRuntime>(null!),
            console);

        // Help
        var helpCode = await router.RouteAsync(["--help"]);
        Assert.Equal(0, helpCode);
        Assert.Contains("Pigmentor RAG Command Line Interface", console.Output, StringComparison.Ordinal);

        // Unknown
        var unknownCode = await router.RouteAsync(["nonexistent"]);
        Assert.Equal(2, unknownCode);
        Assert.Contains("Unknown command", console.Error, StringComparison.Ordinal);
    }
}

