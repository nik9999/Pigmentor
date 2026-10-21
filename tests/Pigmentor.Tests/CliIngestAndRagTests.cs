using Microsoft.Data.Sqlite;
using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Paths;
using Pigmentor.App.Runtime;
using Pigmentor.App.Security;
using Pigmentor.RAG.Cli.Commands;
using Pigmentor.RAG.Cli.Console;
using Pigmentor.Tests.Fixtures;
using Xunit;

namespace Pigmentor.Tests;

public sealed class CliIngestAndRagTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _dbPath;
    private readonly PigmentorAppPaths _paths;
    private readonly PigmentorConfig _config;
    private readonly InMemorySecretStore _secretStore;
    private readonly GigaChatCredentialResolver _credentialResolver;
    private readonly DeterministicSemanticEmbeddingService _embedder;
    private readonly DeterministicRagChatClient _chatClient;

    public CliIngestAndRagTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_e2e_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _dbPath = Path.Combine(_tempDirectory, "test_pigmentor.db");

        _paths = new PigmentorAppPaths(_tempDirectory, _tempDirectory);
        _config = new PigmentorConfig
        {
            RootPath = _tempDirectory,
            Chat = new() { Provider = "Ollama", Model = "llama3" },
            Embeddings = new() { Provider = "Ollama", Model = "nomic-embed-text", Dimensions = 64 }
        };

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
            try
            {
                SqliteConnection.ClearAllPools();
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
    }

    private Task<PigmentorAppRuntime> CreateTestRuntimeAsync(CancellationToken ct)
    {
        return PigmentorAppRuntime.CreateAsync(
            config: _config,
            credentialProvider: _credentialResolver,
            chatClientOverride: _chatClient,
            embeddingServiceOverride: _embedder,
            cancellationToken: ct);
    }

    [Fact]
    public async Task IngestCommand_SingleFile_IngestsFileAndSkipsOnRepeat()
    {
        var txtPath = MultiFormatFixtureFiles.CreateTxt(
            _tempDirectory,
            "specs.txt",
            "Для электропитания контроллера требуется стабилизированное напряжение 24 В постоянного тока.");

        var console1 = new TestConsoleIO();
        var handler1 = new IngestCommandHandler(CreateTestRuntimeAsync, console1);

        // 1. Initial Ingest
        var exitCode1 = await handler1.ExecuteAsync(txtPath);
        Assert.Equal(0, exitCode1);
        Assert.Contains("Status:         succeeded", console1.Output, StringComparison.Ordinal);
        Assert.Contains("Chunks indexed: 1", console1.Output, StringComparison.Ordinal);

        // 2. Repeat Ingest (unchanged)
        var console2 = new TestConsoleIO();
        var handler2 = new IngestCommandHandler(CreateTestRuntimeAsync, console2);

        var exitCode2 = await handler2.ExecuteAsync(txtPath);
        Assert.Equal(0, exitCode2);
        Assert.Contains("Status:  unchanged", console2.Output, StringComparison.Ordinal);
        Assert.Contains("Skipped: yes", console2.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IngestCommand_Directory_IngestsSupportedAndSkipsUnsupported()
    {
        var docsDir = Path.Combine(_tempDirectory, "docs");
        Directory.CreateDirectory(docsDir);

        MultiFormatFixtureFiles.CreateTxt(docsDir, "doc1.txt", "Текст спецификации номер 1.");
        MultiFormatFixtureFiles.CreateDocx(docsDir, "doc2.docx", "Заголовок", "Текст спецификации номер 2.");
        // Create unsupported files (.bin and .zip)
        File.WriteAllText(Path.Combine(docsDir, "unsupported.bin"), "Dummy binary data");
        File.WriteAllBytes(Path.Combine(docsDir, "archive.zip"), [1, 2, 3, 4]);

        var console = new TestConsoleIO();
        var handler = new IngestCommandHandler(CreateTestRuntimeAsync, console);

        var exitCode = await handler.ExecuteAsync(docsDir);
        Assert.Equal(0, exitCode);

        var output = console.Output;
        Assert.Contains("Directory ingestion summary:", output, StringComparison.Ordinal);
        Assert.Contains("Processed:             2", output, StringComparison.Ordinal);
        Assert.Contains("Succeeded:             2", output, StringComparison.Ordinal);
        Assert.Contains("Skipped (unsupported): 2", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskCommand_NonStreaming_AnswersQuestionWithCitationsAndDiagnostics()
    {
        // 1. Ingest a test document
        var docxPath = MultiFormatFixtureFiles.CreateDocx(
            _tempDirectory,
            "comm.docx",
            "Спецификация",
            "Для интерфейса RS-485 установлена скорость передачи данных 115200 бит/с.");

        var ingestConsole = new TestConsoleIO();
        var ingestHandler = new IngestCommandHandler(CreateTestRuntimeAsync, ingestConsole);
        await ingestHandler.ExecuteAsync(docxPath);

        // 2. Ask question
        var askConsole = new TestConsoleIO();
        var askHandler = new AskCommandHandler(CreateTestRuntimeAsync, askConsole);

        var exitCode = await askHandler.ExecuteAsync(
            "Какая скорость передачи данных установлена для интерфейса RS-485?",
            stream: false,
            diagnostics: true);

        Assert.Equal(0, exitCode);
        var output = askConsole.Output;

        Assert.Contains("115200", output, StringComparison.Ordinal);
        Assert.Contains("[S1]", output, StringComparison.Ordinal);
        Assert.Contains("Sources:", output, StringComparison.Ordinal);
        Assert.Contains("comm", output, StringComparison.Ordinal);
        Assert.Contains("Diagnostics:", output, StringComparison.Ordinal);
        Assert.Contains("Valid citations:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskCommand_Streaming_StreamsTokensAndEmitsFinalCitations()
    {
        // 1. Ingest a test document
        var txtPath = MultiFormatFixtureFiles.CreateTxt(
            _tempDirectory,
            "power.txt",
            "Для электропитания контроллера требуется стабилизированное напряжение 24 В постоянного тока.");

        var ingestConsole = new TestConsoleIO();
        var ingestHandler = new IngestCommandHandler(CreateTestRuntimeAsync, ingestConsole);
        await ingestHandler.ExecuteAsync(txtPath);

        // 2. Ask streaming
        var askConsole = new TestConsoleIO();
        var askHandler = new AskCommandHandler(CreateTestRuntimeAsync, askConsole);

        var exitCode = await askHandler.ExecuteAsync(
            "Какое напряжение питания требуется для работы контроллера?",
            stream: true,
            diagnostics: false);

        Assert.Equal(0, exitCode);
        var output = askConsole.Output;

        Assert.Contains("24 В", output, StringComparison.Ordinal);
        Assert.Contains("[S1]", output, StringComparison.Ordinal);
        Assert.Contains("Sources:", output, StringComparison.Ordinal);
        Assert.Contains("power", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IngestCommand_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var txtPath = MultiFormatFixtureFiles.CreateTxt(_tempDirectory, "cancel.txt", "Sample text.");
        var console = new TestConsoleIO();
        var handler = new IngestCommandHandler(CreateTestRuntimeAsync, console);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ExecuteAsync(txtPath, cts.Token));
    }

    [Fact]
    public async Task AskCommand_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var console = new TestConsoleIO();
        var handler = new AskCommandHandler(CreateTestRuntimeAsync, console);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync("Test question?", stream: false, diagnostics: false, cancellationToken: cts.Token));
    }
}

