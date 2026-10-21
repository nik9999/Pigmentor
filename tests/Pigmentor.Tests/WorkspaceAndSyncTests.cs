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

public sealed class WorkspaceAndSyncTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly PigmentorAppPaths _paths;
    private readonly PigmentorConfigManager _configManager;
    private readonly InMemorySecretStore _secretStore;
    private readonly GigaChatCredentialResolver _credentialResolver;
    private readonly DeterministicSemanticEmbeddingService _embedder;
    private readonly DeterministicRagChatClient _chatClient;

    public WorkspaceAndSyncTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_ws_test_{Guid.NewGuid():N}");
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
    public void PigmentorSilos_ContainsExactlySixStandardSilos()
    {
        var expectedSilos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hr-policies",
            "technical-docs",
            "financial-reports",
            "legal-contracts",
            "product-catalog",
            "release-notes-tickets"
        };

        Assert.Equal(6, PigmentorSilos.All.Count);
        foreach (var silo in expectedSilos)
        {
            Assert.True(PigmentorSilos.IsKnownSilo(silo));
        }

        Assert.False(PigmentorSilos.IsKnownSilo("custom-silo"));
        Assert.False(PigmentorSilos.IsKnownSilo("random-folder"));
    }

    [Fact]
    public void DatabasePath_IsStrictlyDerivedFromRootPath_IndependentOfCwd()
    {
        var customRoot = Path.Combine(_tempDirectory, "custom_workspace");
        var effectiveRoot = _paths.ResolveEffectiveRootPath(customRoot);
        var dbPath = _paths.GetDatabasePath(effectiveRoot);

        Assert.True(Path.IsPathRooted(effectiveRoot));
        Assert.True(Path.IsPathRooted(dbPath));
        Assert.Equal(Path.Combine(effectiveRoot, "pigmentor.db"), dbPath);
    }

    [Fact]
    public async Task InitCommand_CreatesRootPathAndAllSixDocumentSilos()
    {
        var console = new TestConsoleIO();
        var handler = new InitCommandHandler(_paths, _configManager, console);

        var exitCode = await handler.ExecuteAsync();
        Assert.Equal(0, exitCode);

        Assert.True(Directory.Exists(_tempDirectory));
        var dbPath = _paths.GetDatabasePath(_tempDirectory);
        Assert.Equal(Path.Combine(_tempDirectory, "pigmentor.db"), dbPath);

        foreach (var silo in PigmentorSilos.All)
        {
            var siloDir = Path.Combine(_tempDirectory, silo);
            Assert.True(Directory.Exists(siloDir), $"Expected silo directory {silo} to exist.");
        }
    }

    [Fact]
    public async Task ConfigSet_RejectsStorageDatabasePath_WithActionableGuidance()
    {
        await _configManager.InitializeAsync(_tempDirectory);

        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _configManager.SetSettingAsync("storage.databasePath", "D:\\custom\\path.db"));
        Assert.Contains("is no longer supported", ex1.Message, StringComparison.Ordinal);
        Assert.Contains("rootPath", ex1.Message, StringComparison.Ordinal);

        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _configManager.SetSettingAsync("databasePath", "D:\\custom\\path.db"));
        Assert.Contains("is no longer supported", ex2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncCommand_SynchronizesSilos_WithResilienceAndDeterministicOrder()
    {
        // 1. Initialize workspace
        var initConsole = new TestConsoleIO();
        var initHandler = new InitCommandHandler(_paths, _configManager, initConsole);
        await initHandler.ExecuteAsync();

        // 2. Populate silos with files (including nested subdirectories and unsupported files)
        var techSilo = Path.Combine(_tempDirectory, "technical-docs");
        var hrSilo = Path.Combine(_tempDirectory, "hr-policies");
        var legalSilo = Path.Combine(_tempDirectory, "legal-contracts");

        // Supported files
        MultiFormatFixtureFiles.CreateTxt(techSilo, "b_protocol.txt", "Протокол Modbus RTU использует контрольную сумму CRC-16.");
        var techSubDir = Path.Combine(techSilo, "hardware");
        Directory.CreateDirectory(techSubDir);
        MultiFormatFixtureFiles.CreateDocx(techSubDir, "a_specs.docx", "Спецификация", "Скорость интерфейса RS-485 составляет 115200 бод.");

        MultiFormatFixtureFiles.CreateTxt(hrSilo, "vacation_policy.txt", "Порядок предоставления ежегодного оплачиваемого отпуска.");

        // Unsupported files (should be skipped)
        File.WriteAllText(Path.Combine(legalSilo, "contract.tmp"), "Temporary file");
        File.WriteAllBytes(Path.Combine(legalSilo, "signed.bin"), [0x00, 0x01, 0x02]);

        // Unknown top-level directory (should be ignored with warning)
        var unknownDir = Path.Combine(_tempDirectory, "unknown_external_folder");
        Directory.CreateDirectory(unknownDir);
        File.WriteAllText(Path.Combine(unknownDir, "ignored.txt"), "This file should not participate in sync.");

        // 3. Execute sync
        var syncConsole = new TestConsoleIO();
        var syncHandler = new SyncCommandHandler(_paths, _configManager, CreateTestRuntimeAsync, syncConsole);

        var exitCode = await syncHandler.ExecuteAsync();
        Assert.Equal(0, exitCode);

        var output = syncConsole.Output;

        // Verify warning for unknown directory
        Assert.Contains("Warning: Ignoring unknown top-level directory 'unknown_external_folder'", output, StringComparison.Ordinal);

        // Verify documents processed across silos
        Assert.Contains("Syncing: [hr-policies] hr-policies", output, StringComparison.Ordinal);
        Assert.Contains("Syncing: [technical-docs] technical-docs", output, StringComparison.Ordinal);
        Assert.Contains("succeeded (1 chunks)", output, StringComparison.Ordinal);

        // Verify summary
        Assert.Contains("Workspace synchronization summary:", output, StringComparison.Ordinal);
        Assert.Contains("Processed:            3", output, StringComparison.Ordinal);
        Assert.Contains("Succeeded:            3", output, StringComparison.Ordinal);
        Assert.Contains("Skipped unsupported:  2", output, StringComparison.Ordinal);

        // 4. Repeat sync - verify unchanged skipping
        var repeatConsole = new TestConsoleIO();
        var repeatHandler = new SyncCommandHandler(_paths, _configManager, CreateTestRuntimeAsync, repeatConsole);

        var repeatCode = await repeatHandler.ExecuteAsync();
        Assert.Equal(0, repeatCode);

        var repeatOutput = repeatConsole.Output;
        Assert.Contains("Unchanged:            3", repeatOutput, StringComparison.Ordinal);
        Assert.Contains("Succeeded:            0", repeatOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncCommand_HonorsCancellation()
    {
        await new InitCommandHandler(_paths, _configManager, new TestConsoleIO()).ExecuteAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var syncHandler = new SyncCommandHandler(_paths, _configManager, CreateTestRuntimeAsync, new TestConsoleIO());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => syncHandler.ExecuteAsync(cts.Token));
    }
}
