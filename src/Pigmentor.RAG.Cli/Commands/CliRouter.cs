using Pigmentor.App.Configuration;
using Pigmentor.App.Credentials;
using Pigmentor.App.Paths;
using Pigmentor.App.Runtime;
using Pigmentor.App.Security;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

/// <summary>
/// Routes command-line arguments to specific command handlers with clean exit codes.
/// </summary>
public sealed class CliRouter
{
    private readonly IPigmentorAppPaths _paths;
    private readonly ISecretStore _secretStore;
    private readonly IGigaChatCredentialProvider _credentialProvider;
    private readonly PigmentorConfigManager _configManager;
    private readonly Func<CancellationToken, Task<PigmentorAppRuntime>> _runtimeFactory;
    private readonly IConsoleIO _console;

    public CliRouter(
        IPigmentorAppPaths paths,
        ISecretStore secretStore,
        IGigaChatCredentialProvider credentialProvider,
        PigmentorConfigManager configManager,
        Func<CancellationToken, Task<PigmentorAppRuntime>> runtimeFactory,
        IConsoleIO console)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public static CliRouter CreateDefault(IConsoleIO? console = null)
    {
        var io = console ?? new SystemConsoleIO();
        var paths = new PigmentorAppPaths();
        var secretStore = SecretStoreFactory.CreateDefault(paths);
        var credentialProvider = new GigaChatCredentialResolver(secretStore);
        var configManager = new PigmentorConfigManager(paths);

        async Task<PigmentorAppRuntime> RuntimeFactory(CancellationToken ct)
        {
            var config = await configManager.LoadAsync(ct).ConfigureAwait(false);
            return await PigmentorAppRuntime.CreateAsync(config, credentialProvider, cancellationToken: ct).ConfigureAwait(false);
        }

        return new CliRouter(paths, secretStore, credentialProvider, configManager, RuntimeFactory, io);
    }

    public async Task<int> RouteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();

        switch (command)
        {
            case "init":
                return await new InitCommandHandler(_paths, _configManager, _console).ExecuteAsync(cancellationToken).ConfigureAwait(false);

            case "auth":
                return await RouteAuthAsync(args[1..], cancellationToken).ConfigureAwait(false);

            case "config":
                return await RouteConfigAsync(args[1..], cancellationToken).ConfigureAwait(false);

            case "ingest":
                if (args.Length < 2)
                {
                    _console.WriteError("Error: File or directory path is required for ingestion.");
                    _console.WriteLine("Usage: pigmentor ingest <path>");
                    return 2;
                }
                return await new IngestCommandHandler(_runtimeFactory, _console).ExecuteAsync(args[1], cancellationToken).ConfigureAwait(false);

            case "ask":
                return await RouteAskAsync(args[1..], cancellationToken).ConfigureAwait(false);

            case "chat":
                return await new ChatCommandHandler(_runtimeFactory, _console).ExecuteAsync(cancellationToken).ConfigureAwait(false);

            case "status":
                var check = args.Length > 1 && args.Skip(1).Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase));
                return await new StatusCommandHandler(_paths, _configManager, _credentialProvider, _console).ExecuteAsync(check, cancellationToken).ConfigureAwait(false);

            default:
                _console.WriteError($"Error: Unknown command '{args[0]}'.");
                _console.WriteLine("Run 'pigmentor --help' for available commands.");
                return 2;
        }
    }

    private async Task<int> RouteAuthAsync(string[] subArgs, CancellationToken cancellationToken)
    {
        var authHandler = new AuthCommandHandler(_secretStore, _credentialProvider, _configManager, _console);

        if (subArgs.Length == 0)
        {
            _console.WriteError("Error: Missing auth action. Expected 'gigachat', 'status', or 'logout'.");
            return 2;
        }

        var action = subArgs[0].ToLowerInvariant();
        return action switch
        {
            "gigachat" => await authHandler.SetGigaChatAsync(cancellationToken).ConfigureAwait(false),
            "status" => await authHandler.ShowStatusAsync(cancellationToken).ConfigureAwait(false),
            "logout" => await authHandler.LogoutGigaChatAsync(cancellationToken).ConfigureAwait(false),
            _ => await InvalidActionAsync($"Unknown auth action '{subArgs[0]}'. Expected 'gigachat', 'status', or 'logout'.")
        };
    }

    private async Task<int> RouteConfigAsync(string[] subArgs, CancellationToken cancellationToken)
    {
        var configHandler = new ConfigCommandHandler(_configManager, _console);

        if (subArgs.Length == 0)
        {
            _console.WriteError("Error: Missing config action. Expected 'show' or 'set'.");
            return 2;
        }

        var action = subArgs[0].ToLowerInvariant();
        if (action == "show")
        {
            return await configHandler.ShowAsync(cancellationToken).ConfigureAwait(false);
        }

        if (action == "set")
        {
            if (subArgs.Length < 3)
            {
                _console.WriteError("Error: 'config set' requires <setting> and <value>.");
                _console.WriteLine("Usage: pigmentor config set <setting> <value>");
                return 2;
            }

            return await configHandler.SetAsync(subArgs[1], subArgs[2], cancellationToken).ConfigureAwait(false);
        }

        _console.WriteError($"Error: Unknown config action '{subArgs[0]}'. Expected 'show' or 'set'.");
        return 2;
    }

    private async Task<int> RouteAskAsync(string[] subArgs, CancellationToken cancellationToken)
    {
        if (subArgs.Length == 0)
        {
            _console.WriteError("Error: Question is required.");
            _console.WriteLine("Usage: pigmentor ask \"<question>\" [--stream] [--diagnostics]");
            return 2;
        }

        var stream = false;
        var diagnostics = false;
        string? question = null;

        foreach (var arg in subArgs)
        {
            if (arg.Equals("--stream", StringComparison.OrdinalIgnoreCase))
            {
                stream = true;
            }
            else if (arg.Equals("--diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics = true;
            }
            else if (question == null)
            {
                question = arg;
            }
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            _console.WriteError("Error: Question is required.");
            return 2;
        }

        var askHandler = new AskCommandHandler(_runtimeFactory, _console);
        return await askHandler.ExecuteAsync(question, stream, diagnostics, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private Task<int> InvalidActionAsync(string message)
    {
        _console.WriteError($"Error: {message}");
        return Task.FromResult(2);
    }

    private void PrintHelp()
    {
        _console.WriteLine("Pigmentor RAG Command Line Interface");
        _console.WriteLine();
        _console.WriteLine("Usage: pigmentor <command> [options]");
        _console.WriteLine();
        _console.WriteLine("Commands:");
        _console.WriteLine("  init                             Initialize configuration and directories");
        _console.WriteLine("  auth gigachat                    Securely set GigaChat Authorization Key (no echo)");
        _console.WriteLine("  auth status                      Display credential status and provenance");
        _console.WriteLine("  auth logout [gigachat]           Remove credentials from secure OS storage");
        _console.WriteLine("  config show                      Display active non-secret configuration");
        _console.WriteLine("  config set <setting> <value>     Set non-secret configuration setting");
        _console.WriteLine("  ingest <path>                    Ingest file or directory into vector database");
        _console.WriteLine("  ask \"<question>\" [options]       Ask a grounded question against ingested docs");
        _console.WriteLine("      --stream                     Stream answer tokens in real-time");
        _console.WriteLine("      --diagnostics                Display retrieval and generation diagnostics");
        _console.WriteLine("  chat                             Start interactive RAG chat session");
        _console.WriteLine("  status [options]                 Show system readiness and provider setup");
        _console.WriteLine("      --check                      Perform connectivity and auth check");
        _console.WriteLine();
        _console.WriteLine("Environment Variables:");
        _console.WriteLine("  PIGMENTOR_GIGACHAT_AUTH_KEY      GigaChat Authorization Key for headless/CI scenarios");
    }
}

