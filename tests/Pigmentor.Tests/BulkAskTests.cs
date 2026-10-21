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

public sealed class BulkAskTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly PigmentorAppPaths _paths;
    private readonly PigmentorConfig _config;
    private readonly InMemorySecretStore _secretStore;
    private readonly GigaChatCredentialResolver _credentialResolver;
    private readonly DeterministicSemanticEmbeddingService _embedder;
    private readonly DeterministicRagChatClient _chatClient;

    public BulkAskTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_bulk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

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
    public void ParseQuestions_NormalText_TrimsAndSkipsEmptyLines()
    {
        var text = "\r\n  Сколько каналов имеет MDS AI-8UI?  \r\n\r\n   \r\nКакие диапазоны тока?   \r\n\r\n";
        var questions = AskCommandHandler.ParseQuestions(text);

        Assert.Equal(2, questions.Count);
        Assert.Equal("Сколько каналов имеет MDS AI-8UI?", questions[0]);
        Assert.Equal("Какие диапазоны тока?", questions[1]);
    }

    [Fact]
    public void ParseQuestions_EmptyOrWhitespace_ReturnsEmptyList()
    {
        Assert.Empty(AskCommandHandler.ParseQuestions("   \r\n \n \t  "));
        Assert.Empty(AskCommandHandler.ParseQuestions(string.Empty));
        Assert.Empty(AskCommandHandler.ParseQuestions(null!));
    }

    [Fact]
    public async Task ExecuteBulkAsync_MissingFile_ReturnsExitCode2()
    {
        var console = new TestConsoleIO();
        var handler = new AskCommandHandler(CreateTestRuntimeAsync, console);

        var exitCode = await handler.ExecuteBulkAsync(Path.Combine(_tempDirectory, "nonexistent.txt"));

        Assert.Equal(2, exitCode);
        Assert.Contains("not found", console.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteBulkAsync_EmptyQuestionsFile_ReturnsExitCode2()
    {
        var emptyFile = Path.Combine(_tempDirectory, "empty.txt");
        await File.WriteAllTextAsync(emptyFile, "   \r\n\r\n  \n");

        var console = new TestConsoleIO();
        var handler = new AskCommandHandler(CreateTestRuntimeAsync, console);

        var exitCode = await handler.ExecuteBulkAsync(emptyFile);

        Assert.Equal(2, exitCode);
        Assert.Contains("contains no valid questions", console.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteBulkAsync_OutputFileExistsWithoutForce_ReturnsExitCode2()
    {
        var questionsFile = Path.Combine(_tempDirectory, "q.txt");
        await File.WriteAllTextAsync(questionsFile, "Какая скорость интерфейса?");

        var outputFile = Path.Combine(_tempDirectory, "out.txt");
        await File.WriteAllTextAsync(outputFile, "pre-existing content");

        var console = new TestConsoleIO();
        var handler = new AskCommandHandler(CreateTestRuntimeAsync, console);

        var exitCode = await handler.ExecuteBulkAsync(questionsFile, outputPath: outputFile, force: false);

        Assert.Equal(2, exitCode);
        Assert.Contains("already exists", console.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("pre-existing content", await File.ReadAllTextAsync(outputFile));
    }

    [Fact]
    public async Task ExecuteBulkAsync_OutputFileExistsWithForce_OverwritesAndSucceeds()
    {
        var docxPath = MultiFormatFixtureFiles.CreateDocx(
            _tempDirectory,
            "device.docx",
            "Модуль MDS",
            "Для интерфейса RS-485 установлена скорость 115200 бит/с.");

        var ingestConsole = new TestConsoleIO();
        var ingestHandler = new IngestCommandHandler(CreateTestRuntimeAsync, ingestConsole);
        await ingestHandler.ExecuteAsync(docxPath);

        var questionsFile = Path.Combine(_tempDirectory, "q.txt");
        await File.WriteAllTextAsync(questionsFile, "Какая скорость установлена для RS-485?");

        var outputFile = Path.Combine(_tempDirectory, "out.txt");
        await File.WriteAllTextAsync(outputFile, "old content");

        var console = new TestConsoleIO();
        var handler = new AskCommandHandler(CreateTestRuntimeAsync, console);

        var exitCode = await handler.ExecuteBulkAsync(questionsFile, outputPath: outputFile, force: true);

        Assert.Equal(0, exitCode);
        var fileContent = await File.ReadAllTextAsync(outputFile);
        Assert.Contains("Bulk Ask Summary", fileContent, StringComparison.Ordinal);
        Assert.Contains("Questions:        1", fileContent, StringComparison.Ordinal);
        Assert.Contains("Completed:        1", fileContent, StringComparison.Ordinal);
        Assert.Contains("Failed:           0", fileContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteBulkAsync_SequentialQuestions_SucceedsAllAndEmitsSummary()
    {
        var docxPath = MultiFormatFixtureFiles.CreateDocx(
            _tempDirectory,
            "specs.docx",
            "Характеристики",
            "Модуль MDS имеет 8 независимых каналов ввода. Скорость интерфейса составляет 115200 бит/с.");

        var ingestConsole = new TestConsoleIO();
        var ingestHandler = new IngestCommandHandler(CreateTestRuntimeAsync, ingestConsole);
        await ingestHandler.ExecuteAsync(docxPath);

        var questionsFile = Path.Combine(_tempDirectory, "questions.txt");
        await File.WriteAllTextAsync(questionsFile, "Сколько каналов имеет модуль?\r\nКакая скорость интерфейса?\r\n");

        var console = new TestConsoleIO();
        var handler = new AskCommandHandler(CreateTestRuntimeAsync, console);

        var exitCode = await handler.ExecuteBulkAsync(questionsFile, diagnostics: false);

        Assert.Equal(0, exitCode);
        var output = console.Output;

        Assert.Contains("[1/2] QUESTION", output, StringComparison.Ordinal);
        Assert.Contains("Сколько каналов имеет модуль?", output, StringComparison.Ordinal);
        Assert.Contains("[2/2] QUESTION", output, StringComparison.Ordinal);
        Assert.Contains("Какая скорость интерфейса?", output, StringComparison.Ordinal);
        Assert.Contains("ANSWER", output, StringComparison.Ordinal);
        Assert.Contains("SOURCES", output, StringComparison.Ordinal);
        Assert.Contains("Bulk Ask Summary", output, StringComparison.Ordinal);
        Assert.Contains("Questions:        2", output, StringComparison.Ordinal);
        Assert.Contains("Completed:        2", output, StringComparison.Ordinal);
        Assert.Contains("Failed:           0", output, StringComparison.Ordinal);
        Assert.Contains("Total latency:", output, StringComparison.Ordinal);
        Assert.Contains("Average latency:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteBulkAsync_WithDiagnostics_EmitsDiagnosticsSection()
    {
        var docxPath = MultiFormatFixtureFiles.CreateDocx(
            _tempDirectory,
            "diag.docx",
            "Документ",
            "Тестовые данные для проверки диагностики.");

        var ingestConsole = new TestConsoleIO();
        var ingestHandler = new IngestCommandHandler(CreateTestRuntimeAsync, ingestConsole);
        await ingestHandler.ExecuteAsync(docxPath);

        var questionsFile = Path.Combine(_tempDirectory, "q_diag.txt");
        await File.WriteAllTextAsync(questionsFile, "Какие данные содержатся в документе?\r\n");

        var console = new TestConsoleIO();
        var handler = new AskCommandHandler(CreateTestRuntimeAsync, console);

        var exitCode = await handler.ExecuteBulkAsync(questionsFile, diagnostics: true);

        Assert.Equal(0, exitCode);
        var output = console.Output;

        Assert.Contains("DIAGNOSTICS", output, StringComparison.Ordinal);
        Assert.Contains("Retrieval candidates:", output, StringComparison.Ordinal);
        Assert.Contains("Sources used:", output, StringComparison.Ordinal);
        Assert.Contains("Valid citations:", output, StringComparison.Ordinal);
        Assert.Contains("Required citations satisfied:", output, StringComparison.Ordinal);
        Assert.Contains("Latency:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteBulkAsync_IndividualFailure_ContinuesAndReportsFailedSummary()
    {
        var docxPath = MultiFormatFixtureFiles.CreateDocx(
            _tempDirectory,
            "fail_test.docx",
            "Документ",
            "Тестовые данные модуля MDS.");

        var ingestConsole = new TestConsoleIO();
        var ingestHandler = new IngestCommandHandler(CreateTestRuntimeAsync, ingestConsole);
        await ingestHandler.ExecuteAsync(docxPath);

        var callIdx = 0;
        _chatClient.CustomResponder = _ =>
        {
            callIdx++;
            if (callIdx == 2)
            {
                throw new InvalidOperationException("Simulated Ollama inference timeout");
            }
            return "Ответ на вопрос [S1]";
        };

        var questionsFile = Path.Combine(_tempDirectory, "q_fail.txt");
        await File.WriteAllTextAsync(questionsFile, "Вопрос 1\r\nВопрос 2 с ошибкой\r\nВопрос 3\r\n");

        var console = new TestConsoleIO();
        var handler = new AskCommandHandler(CreateTestRuntimeAsync, console);

        var exitCode = await handler.ExecuteBulkAsync(questionsFile, diagnostics: false);

        // Batch exit code is 1 when at least one question failed
        Assert.Equal(1, exitCode);
        var output = console.Output;

        Assert.Contains("[1/3] QUESTION", output, StringComparison.Ordinal);
        Assert.Contains("[2/3] QUESTION", output, StringComparison.Ordinal);
        Assert.Contains("Simulated Ollama inference timeout", output, StringComparison.Ordinal);
        Assert.Contains("[3/3] QUESTION", output, StringComparison.Ordinal);

        Assert.Contains("Bulk Ask Summary", output, StringComparison.Ordinal);
        Assert.Contains("Questions:        3", output, StringComparison.Ordinal);
        Assert.Contains("Completed:        2", output, StringComparison.Ordinal);
        Assert.Contains("Failed:           1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRouter_RouteAskAsync_ValidationRules()
    {
        var configManager = new PigmentorConfigManager(_paths);

        // 1. Missing question and missing bulk
        var console1 = new TestConsoleIO();
        var router1 = new CliRouter(_paths, _secretStore, _credentialResolver, configManager, CreateTestRuntimeAsync, console1);
        var exit1 = await router1.RouteAsync(["ask"]);
        Assert.Equal(2, exit1);
        Assert.Contains("Question or --bulk <file> is required", console1.Error, StringComparison.Ordinal);

        // 2. --bulk without path
        var console2 = new TestConsoleIO();
        var router2 = new CliRouter(_paths, _secretStore, _credentialResolver, configManager, CreateTestRuntimeAsync, console2);
        var exit2 = await router2.RouteAsync(["ask", "--bulk"]);
        Assert.Equal(2, exit2);
        Assert.Contains("Option '--bulk' requires a file path", console2.Error, StringComparison.Ordinal);

        // 3. --bulk with --stream
        var console3 = new TestConsoleIO();
        var router3 = new CliRouter(_paths, _secretStore, _credentialResolver, configManager, CreateTestRuntimeAsync, console3);
        var exit3 = await router3.RouteAsync(["ask", "--bulk", "test.txt", "--stream"]);
        Assert.Equal(2, exit3);
        Assert.Contains("Option '--stream' cannot be used with '--bulk'", console3.Error, StringComparison.Ordinal);

        // 4. --bulk with positional question
        var console4 = new TestConsoleIO();
        var router4 = new CliRouter(_paths, _secretStore, _credentialResolver, configManager, CreateTestRuntimeAsync, console4);
        var exit4 = await router4.RouteAsync(["ask", "What is MDS?", "--bulk", "test.txt"]);
        Assert.Equal(2, exit4);
        Assert.Contains("Questions cannot be provided as arguments when using '--bulk'", console4.Error, StringComparison.Ordinal);

        // 5. --output without --bulk
        var console5 = new TestConsoleIO();
        var router5 = new CliRouter(_paths, _secretStore, _credentialResolver, configManager, CreateTestRuntimeAsync, console5);
        var exit5 = await router5.RouteAsync(["ask", "What is MDS?", "--output", "out.txt"]);
        Assert.Equal(2, exit5);
        Assert.Contains("Option '--output' can only be used with '--bulk'", console5.Error, StringComparison.Ordinal);

        // 6. --force without --bulk
        var console6 = new TestConsoleIO();
        var router6 = new CliRouter(_paths, _secretStore, _credentialResolver, configManager, CreateTestRuntimeAsync, console6);
        var exit6 = await router6.RouteAsync(["ask", "What is MDS?", "--force"]);
        Assert.Equal(2, exit6);
        Assert.Contains("Option '--force' can only be used with '--bulk'", console6.Error, StringComparison.Ordinal);
    }
}
