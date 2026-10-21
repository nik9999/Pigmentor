using System.Net;
using Microsoft.Extensions.AI;
using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Paths;
using Pigmentor.App.Runtime;
using Pigmentor.App.Security;
using Pigmentor.RAG.Cli.Commands;
using Pigmentor.RAG.Cli.Console;
using Xunit;

namespace Pigmentor.Tests;

public sealed class ModelDeploymentAndStatusCheckTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly PigmentorAppPaths _paths;
    private readonly PigmentorConfigManager _configManager;
    private readonly InMemorySecretStore _secretStore;
    private readonly GigaChatCredentialResolver _credentialResolver;

    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly string FixtureModel1024 = Path.Combine(FixturesDir, "test_bge_m3_1024.onnx");
    private static readonly string FixtureModel768 = Path.Combine(FixturesDir, "test_bge_m3_768.onnx");
    private static readonly string FixtureTokenizer = Path.Combine(FixturesDir, "test_tokenizer.json");

    public ModelDeploymentAndStatusCheckTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_model_test_{Guid.NewGuid():N}");
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
    public void ModelPathResolution_ResolvesRelativeToApplicationBaseDirectory()
    {
        var relativeModelPath = Path.Combine("models", "bge-m3", "model.onnx");
        var resolved = _paths.ResolveApplicationPath(relativeModelPath);

        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativeModelPath));
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ProductionModelAndTestFixture_ArchitecturalSeparation()
    {
        // 1. Invariant: Test fixtures are isolated in tests/Pigmentor.Tests/Fixtures
        Assert.True(File.Exists(FixtureModel1024), "Test fixture test_bge_m3_1024.onnx must exist for unit tests.");
        Assert.True(File.Exists(FixtureTokenizer), "Test fixture test_tokenizer.json must exist for unit tests.");

        // 2. Canonical production models directory is Models/bge-m3 at repository root
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var canonicalModelsDir = Path.Combine(repoRoot, "Models", "bge-m3");
        var canonicalGitkeep = Path.Combine(canonicalModelsDir, ".gitkeep");
        var canonicalReadme = Path.Combine(canonicalModelsDir, "README.md");

        Assert.True(Directory.Exists(canonicalModelsDir), "Canonical Models/bge-m3 directory must exist at repository root.");
        Assert.True(File.Exists(canonicalGitkeep), "Canonical Models/bge-m3/.gitkeep must be tracked.");
        Assert.True(File.Exists(canonicalReadme), "Canonical Models/bge-m3/README.md must be tracked.");

        // 3. Test fixtures must NOT be present in canonical production directory
        Assert.False(File.Exists(Path.Combine(canonicalModelsDir, "test_bge_m3_1024.onnx")),
            "Test fixtures must never leak into canonical production Models/bge-m3.");

        // 4. Default configuration points to production names, NOT test fixture names
        var defaultConfig = PigmentorConfigManager.CreateDefaultConfig();
        Assert.Equal("models/bge-m3/model.onnx", defaultConfig.Embeddings!.ModelPath);
        Assert.Equal("models/bge-m3/tokenizer.json", defaultConfig.Embeddings.TokenizerPath);
        Assert.DoesNotContain("test_", defaultConfig.Embeddings.ModelPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CliCsproj_DeploysCanonicalModelsToOutputAndPublish()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var csprojPath = Path.Combine(repoRoot, "src", "Pigmentor.RAG.Cli", "Pigmentor.RAG.Cli.csproj");
        Assert.True(File.Exists(csprojPath), $"CLI csproj must exist at '{csprojPath}'.");

        var csprojText = await File.ReadAllTextAsync(csprojPath);

        // Verifies MSBuild includes Models\** and links to Models\
        Assert.Contains(@"Models\**", csprojText, StringComparison.Ordinal);
        Assert.Contains(@"<Link>Models\%(RecursiveDir)%(Filename)%(Extension)</Link>", csprojText, StringComparison.Ordinal);
        Assert.Contains("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>", csprojText, StringComparison.Ordinal);
        Assert.Contains("<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>", csprojText, StringComparison.Ordinal);
        Assert.Contains("CheckBgeM3ModelAssets", csprojText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusCheck_ReportsFailureAndExitCode1_WhenModelFileMissing()
    {
        using var mockServer = new MockHttpServer();
        mockServer.Start();

        var config = new PigmentorConfig
        {
            RootPath = _tempDirectory,
            Chat = new() { Provider = "Ollama", Endpoint = mockServer.Endpoint },
            Embeddings = new()
            {
                Provider = "Onnx",
                ModelPath = "non_existent_dir/model.onnx",
                TokenizerPath = FixtureTokenizer,
                Dimensions = 1024
            }
        };
        await _configManager.SaveAsync(config);

        var console = new TestConsoleIO();
        var handler = new StatusCommandHandler(_paths, _configManager, _credentialResolver, console);

        var exitCode = await handler.ExecuteAsync(check: true);
        Assert.Equal(1, exitCode);

        var output = console.Output;
        Assert.Contains("Embeddings (ONNX inference): FAILED", output, StringComparison.Ordinal);
        Assert.Contains("non_existent_dir", output, StringComparison.Ordinal);
        Assert.Contains("download-bge-m3.ps1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusCheck_ReportsFailureAndExitCode1_WhenTokenizerFileMissing()
    {
        using var mockServer = new MockHttpServer();
        mockServer.Start();

        var config = new PigmentorConfig
        {
            RootPath = _tempDirectory,
            Chat = new() { Provider = "Ollama", Endpoint = mockServer.Endpoint },
            Embeddings = new()
            {
                Provider = "Onnx",
                ModelPath = FixtureModel1024,
                TokenizerPath = "non_existent_tokenizer.json",
                Dimensions = 1024
            }
        };
        await _configManager.SaveAsync(config);

        var console = new TestConsoleIO();
        var handler = new StatusCommandHandler(_paths, _configManager, _credentialResolver, console);

        var exitCode = await handler.ExecuteAsync(check: true);
        Assert.Equal(1, exitCode);

        var output = console.Output;
        Assert.Contains("Embeddings (ONNX inference): FAILED", output, StringComparison.Ordinal);
        Assert.Contains("non_existent_tokenizer.json", output, StringComparison.Ordinal);
        Assert.Contains("download-bge-m3.ps1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusCheck_ReportsFailureAndExitCode1_WhenExternalDataFileMissing()
    {
        using var mockServer = new MockHttpServer();
        mockServer.Start();

        // Create an isolated subfolder with a small model file (< 50MB, simulating the BAAI 725KB graph file)
        // without an accompanying model.onnx_data file.
        var isolatedDir = Path.Combine(_tempDirectory, "isolated_model");
        Directory.CreateDirectory(isolatedDir);
        var smallModelPath = Path.Combine(isolatedDir, "model.onnx");
        await File.WriteAllBytesAsync(smallModelPath, [0x08, 0x01, 0x12, 0x00]); // Small mock ONNX file (< 50MB)

        var config = new PigmentorConfig
        {
            RootPath = _tempDirectory,
            Chat = new() { Provider = "Ollama", Endpoint = mockServer.Endpoint },
            Embeddings = new()
            {
                Provider = "Onnx",
                ModelPath = smallModelPath,
                TokenizerPath = FixtureTokenizer,
                Dimensions = 1024
            }
        };
        await _configManager.SaveAsync(config);

        var console = new TestConsoleIO();
        var handler = new StatusCommandHandler(_paths, _configManager, _credentialResolver, console);

        var exitCode = await handler.ExecuteAsync(check: true);
        Assert.Equal(1, exitCode);

        var output = console.Output;
        Assert.Contains("Embeddings (ONNX inference): FAILED", output, StringComparison.Ordinal);
        Assert.Contains("model.onnx_data", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusCheck_ReportsFailureAndExitCode1_OnEmbeddingDimensionMismatch()
    {
        using var mockServer = new MockHttpServer();
        mockServer.Start();

        // Model outputs 768 floats, but configured Dimensions is 1024
        var config = new PigmentorConfig
        {
            RootPath = _tempDirectory,
            Chat = new() { Provider = "Ollama", Endpoint = mockServer.Endpoint },
            Embeddings = new()
            {
                Provider = "Onnx",
                ModelPath = FixtureModel768,
                TokenizerPath = FixtureTokenizer,
                Dimensions = 1024
            }
        };
        await _configManager.SaveAsync(config);

        var console = new TestConsoleIO();
        var handler = new StatusCommandHandler(_paths, _configManager, _credentialResolver, console);

        var exitCode = await handler.ExecuteAsync(check: true);
        Assert.Equal(1, exitCode);

        var output = console.Output;
        Assert.Contains("Embeddings (ONNX inference): FAILED", output, StringComparison.Ordinal);
        Assert.Contains("mismatch", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StatusCheck_SucceedsWith1024Dimensions_WhenValidFixtureModelAndTokenizerUsed()
    {
        using var mockServer = new MockHttpServer();
        mockServer.Start();

        var config = new PigmentorConfig
        {
            RootPath = _tempDirectory,
            Chat = new() { Provider = "Ollama", Endpoint = mockServer.Endpoint },
            Embeddings = new()
            {
                Provider = "Onnx",
                ModelPath = FixtureModel1024,
                TokenizerPath = FixtureTokenizer,
                Dimensions = 1024
            }
        };
        await _configManager.SaveAsync(config);

        var console = new TestConsoleIO();
        var handler = new StatusCommandHandler(_paths, _configManager, _credentialResolver, console);

        var exitCode = await handler.ExecuteAsync(check: true);
        Assert.Equal(0, exitCode);

        var output = console.Output;
        Assert.Contains("Chat (Ollama): OK", output, StringComparison.Ordinal);
        Assert.Contains("Embeddings (ONNX inference): OK (1024 dimensions)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFiniteValues_AreIdentifiedAndRejected()
    {
        var validVector = new float[1024];
        validVector[0] = 1.0f;

        var nanVector = new float[1024];
        nanVector[5] = float.NaN;

        var infVector = new float[1024];
        infVector[10] = float.PositiveInfinity;

        static bool IsFiniteAndNonZero(float[] v)
        {
            var sumSquares = 0.0;
            foreach (var f in v)
            {
                if (float.IsNaN(f) || float.IsInfinity(f))
                {
                    return false;
                }
                sumSquares += f * f;
            }
            return sumSquares > 1e-12;
        }

        Assert.True(IsFiniteAndNonZero(validVector));
        Assert.False(IsFiniteAndNonZero(nanVector));
        Assert.False(IsFiniteAndNonZero(infVector));
        Assert.False(IsFiniteAndNonZero(new float[1024])); // All-zero
    }

    private sealed class MockHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public string Endpoint { get; }

        public MockHttpServer()
        {
            var port = FindFreePort();
            Endpoint = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"{Endpoint}/");
        }

        public void Start()
        {
            _listener.Start();
            Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        var resp = context.Response;
                        resp.StatusCode = 200;
                        resp.ContentType = "application/json";
                        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"models\":[]}");
                        resp.ContentLength64 = bytes.Length;
                        await resp.OutputStream.WriteAsync(bytes, _cts.Token);
                        resp.OutputStream.Close();
                    }
                    catch
                    {
                        break;
                    }
                }
            });
        }

        private static int FindFreePort()
        {
            using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _cts.Dispose();
        }
    }
}
