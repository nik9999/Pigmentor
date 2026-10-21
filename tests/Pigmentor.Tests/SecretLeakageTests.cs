using System.Text.Json;
using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Paths;
using Pigmentor.App.Security;
using Pigmentor.Generation;
using Pigmentor.RAG.Cli.Commands;
using Pigmentor.RAG.Cli.Console;
using Xunit;

namespace Pigmentor.Tests;

public sealed class SecretLeakageTests : IDisposable
{
    private const string SyntheticSecret = "PIGMENTOR_TEST_SECRET_DO_NOT_LEAK";
    private readonly string _tempDirectory;
    private readonly PigmentorAppPaths _paths;
    private readonly PigmentorConfigManager _configManager;
    private readonly InMemorySecretStore _secretStore;
    private readonly GigaChatCredentialResolver _credentialResolver;

    public SecretLeakageTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_leak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _paths = new PigmentorAppPaths(_tempDirectory, _tempDirectory);
        _configManager = new PigmentorConfigManager(_paths);
        _secretStore = new InMemorySecretStore();
        _credentialResolver = new GigaChatCredentialResolver(_secretStore, explicitKey: SyntheticSecret);
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
    public async Task ConfigSerialization_NeverContainsSecret()
    {
        var config = await _configManager.LoadAsync();
        await _configManager.SaveAsync(config);

        var json = await File.ReadAllTextAsync(_paths.ConfigFilePath);

        Assert.DoesNotContain(SyntheticSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        var jsonWithoutTokenizer = json.Replace("tokenizer", "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", jsonWithoutTokenizer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigSet_RejectsSecretSettings_WithActionableGuidance()
    {
        string[] forbiddenKeys =
        [
            "gigachat.key",
            "gigachat.secret",
            "chat.token",
            "auth.password",
            "storage.credential",
            "API_KEY"
        ];

        foreach (var key in forbiddenKeys)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _configManager.SetSettingAsync(key, SyntheticSecret));

            Assert.Contains("Secret settings cannot be stored in configuration", ex.Message, StringComparison.Ordinal);
            Assert.Contains("pigmentor auth gigachat", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticSecret, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AuthStatusOutput_NeverPrintsSecret_EvenWhenConfigured()
    {
        var console = new TestConsoleIO();
        var handler = new AuthCommandHandler(_secretStore, _credentialResolver, _configManager, console);

        var exitCode = await handler.ShowStatusAsync();

        Assert.Equal(0, exitCode);
        var output = console.Output;

        Assert.Contains("Credentials: configured", output, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecret, output, StringComparison.Ordinal);
        Assert.DoesNotContain("***", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigShowOutput_NeverPrintsSecret()
    {
        var console = new TestConsoleIO();
        var handler = new ConfigCommandHandler(_paths, _configManager, console);

        var exitCode = await handler.ShowAsync();

        Assert.Equal(0, exitCode);
        var output = console.Output;

        Assert.DoesNotContain(SyntheticSecret, output, StringComparison.Ordinal);
        Assert.Contains("Chat:", output, StringComparison.Ordinal);
        Assert.Contains("Embeddings:", output, StringComparison.Ordinal);
        Assert.Contains("Workspace:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_NeverContainSecret()
    {
        var diag = new RagDiagnostics(
            TotalLatency: TimeSpan.FromMilliseconds(250),
            RetrievedCandidateCount: 5,
            ContextSourceCount: 3,
            PromptTokenCount: 120,
            ValidCitationCount: 2);

        var console = new TestConsoleIO();
        var handler = new AskCommandHandler(_ => Task.FromResult<Pigmentor.App.Runtime.PigmentorAppRuntime>(null!), console);

        handler.PrintDiagnostics(diag);

        var output = console.Output;
        Assert.DoesNotContain(SyntheticSecret, output, StringComparison.Ordinal);
        Assert.Contains("Retrieval candidates: 5", output, StringComparison.Ordinal);
        Assert.Contains("Valid citations:      2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretRedactor_DefensivelyReplacesSecretOccurrences()
    {
        var message = $"Failed to authenticate against provider with key: {SyntheticSecret} for request.";
        var redacted = SecretRedactor.Redact(message, SyntheticSecret);

        Assert.DoesNotContain(SyntheticSecret, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.RedactedReplacement, redacted, StringComparison.Ordinal);
    }
}

