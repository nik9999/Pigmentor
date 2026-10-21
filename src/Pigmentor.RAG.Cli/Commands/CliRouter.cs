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
                if (args.Length > 1)
                {
                    _console.WriteError($"Error: Unknown argument '{args[1]}' for command 'init'.");
                    return 2;
                }
                return await new InitCommandHandler(_paths, _configManager, _console).ExecuteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            case "sync":
                if (args.Length > 1)
                {
                    _console.WriteError($"Error: Unknown argument '{args[1]}' for command 'sync'.");
                    return 2;
                }
                return await new SyncCommandHandler(_paths, _configManager, _runtimeFactory, _console).ExecuteAsync(cancellationToken).ConfigureAwait(false);

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
                if (args.Length > 2)
                {
                    _console.WriteError($"Error: Unexpected extra argument '{args[2]}' for command 'ingest'.");
                    return 2;
                }
                return await new IngestCommandHandler(_runtimeFactory, _console).ExecuteAsync(args[1], cancellationToken).ConfigureAwait(false);

            case "ask":
                return await RouteAskAsync(args[1..], cancellationToken).ConfigureAwait(false);

            case "chat":
                if (args.Length > 1)
                {
                    _console.WriteError($"Error: Unexpected argument '{args[1]}' for command 'chat'.");
                    return 2;
                }
                return await new ChatCommandHandler(_runtimeFactory, _console).ExecuteAsync(cancellationToken).ConfigureAwait(false);

            case "status":
                var check = false;
                for (var i = 1; i < args.Length; i++)
                {
                    if (args[i].Equals("--check", StringComparison.OrdinalIgnoreCase))
                    {
                        check = true;
                    }
                    else
                    {
                        _console.WriteError($"Error: Unknown option '{args[i]}' for command 'status'.");
                        return 2;
                    }
                }
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
        switch (action)
        {
            case "gigachat":
                if (subArgs.Length > 1)
                {
                    _console.WriteError($"Error: Unexpected argument '{subArgs[1]}' for 'auth gigachat'.");
                    return 2;
                }
                return await authHandler.SetGigaChatAsync(cancellationToken).ConfigureAwait(false);

            case "status":
                if (subArgs.Length > 1)
                {
                    _console.WriteError($"Error: Unexpected argument '{subArgs[1]}' for 'auth status'.");
                    return 2;
                }
                return await authHandler.ShowStatusAsync(cancellationToken).ConfigureAwait(false);

            case "logout":
                if (subArgs.Length > 2)
                {
                    _console.WriteError($"Error: Unexpected argument '{subArgs[2]}' for 'auth logout'.");
                    return 2;
                }
                if (subArgs.Length == 2 && !subArgs[1].Equals("gigachat", StringComparison.OrdinalIgnoreCase))
                {
                    _console.WriteError($"Error: Unknown target '{subArgs[1]}' for 'auth logout'. Supported: 'gigachat'.");
                    return 2;
                }
                return await authHandler.LogoutGigaChatAsync(cancellationToken).ConfigureAwait(false);

            default:
                _console.WriteError($"Error: Unknown auth action '{subArgs[0]}'. Expected 'gigachat', 'status', or 'logout'.");
                return 2;
        }
    }

    private async Task<int> RouteConfigAsync(string[] subArgs, CancellationToken cancellationToken)
    {
        var configHandler = new ConfigCommandHandler(_paths, _configManager, _console);

        if (subArgs.Length == 0)
        {
            _console.WriteError("Error: Missing config action. Expected 'show' or 'set'.");
            return 2;
        }

        var action = subArgs[0].ToLowerInvariant();
        if (action == "show")
        {
            if (subArgs.Length > 1)
            {
                _console.WriteError($"Error: Unexpected argument '{subArgs[1]}' for 'config show'.");
                return 2;
            }
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
            if (subArgs.Length > 3)
            {
                _console.WriteError($"Error: Unexpected extra argument '{subArgs[3]}' for 'config set'.");
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
            _console.WriteError("Error: Question or --bulk <file> is required.");
            _console.WriteLine("Usage: pigmentor ask <question> [--stream] [--diagnostics]");
            _console.WriteLine("       pigmentor ask --bulk <file> [--diagnostics] [--output <file>] [--force]");
            return 2;
        }

        var stream = false;
        var diagnostics = false;
        var force = false;
        string? bulkPath = null;
        string? outputPath = null;
        var questionParts = new List<string>();

        for (var i = 0; i < subArgs.Length; i++)
        {
            var arg = subArgs[i];
            if (arg.Equals("--stream", StringComparison.OrdinalIgnoreCase))
            {
                stream = true;
            }
            else if (arg.Equals("--diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics = true;
            }
            else if (arg.Equals("--force", StringComparison.OrdinalIgnoreCase))
            {
                force = true;
            }
            else if (arg.Equals("--bulk", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= subArgs.Length || subArgs[i + 1].StartsWith('-'))
                {
                    _console.WriteError("Error: Option '--bulk' requires a file path.");
                    return 2;
                }
                bulkPath = subArgs[++i];
            }
            else if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= subArgs.Length || subArgs[i + 1].StartsWith('-'))
                {
                    _console.WriteError("Error: Option '--output' requires a file path.");
                    return 2;
                }
                outputPath = subArgs[++i];
            }
            else if (arg.StartsWith('-'))
            {
                _console.WriteError($"Error: Unknown option '{arg}'.");
                return 2;
            }
            else
            {
                questionParts.Add(arg);
            }
        }

        var askHandler = new AskCommandHandler(_runtimeFactory, _console);

        if (bulkPath != null)
        {
            if (stream)
            {
                _console.WriteError("Error: Option '--stream' cannot be used with '--bulk'.");
                return 2;
            }

            if (questionParts.Count > 0)
            {
                _console.WriteError("Error: Questions cannot be provided as arguments when using '--bulk'.");
                return 2;
            }

            return await askHandler.ExecuteBulkAsync(bulkPath, diagnostics, outputPath, force, cancellationToken).ConfigureAwait(false);
        }

        if (outputPath != null)
        {
            _console.WriteError("Error: Option '--output' can only be used with '--bulk'.");
            return 2;
        }

        if (force)
        {
            _console.WriteError("Error: Option '--force' can only be used with '--bulk'.");
            return 2;
        }

        var question = string.Join(" ", questionParts).Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            _console.WriteError("Error: Question is required.");
            _console.WriteLine("Usage: pigmentor ask <question> [--stream] [--diagnostics]");
            return 2;
        }

        return await askHandler.ExecuteAsync(question, stream, diagnostics, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void PrintHelp()
    {
        _console.WriteLine("Pigmentor RAG Command Line Interface");
        _console.WriteLine();
        _console.WriteLine("Usage: pigmentor <command> [options]");
        _console.WriteLine();
        _console.WriteLine("Commands:");
        _console.WriteLine("  init                             Initialize configuration and workspace silos");
        _console.WriteLine("  sync                             Synchronize all documents across workspace silos");
        _console.WriteLine("  auth gigachat                    Securely set GigaChat Authorization Key (no echo)");
        _console.WriteLine("  auth status                      Display credential status and provenance");
        _console.WriteLine("  auth logout [gigachat]           Remove credentials from secure OS storage");
        _console.WriteLine("  config show                      Display active non-secret configuration");
        _console.WriteLine("  config set <setting> <value>     Set non-secret configuration setting");
        _console.WriteLine("  ingest <path>                    Ingest file or directory into vector database");
        _console.WriteLine("  ask <question> [options]         Ask a grounded question against ingested docs");
        _console.WriteLine("      --stream                     Stream answer tokens in real-time");
        _console.WriteLine("      --diagnostics                Display retrieval and generation diagnostics");
        _console.WriteLine("      --bulk <file>                Run questions in batch from a plain-text file");
        _console.WriteLine("      --output <file>              Save bulk ask results to a file");
        _console.WriteLine("      --force                      Overwrite output file if it already exists");
        _console.WriteLine("  chat                             Start interactive RAG query session");
        _console.WriteLine("  status [options]                 Show system readiness and provider setup");
        _console.WriteLine("      --check                      Perform connectivity and health checks");
        _console.WriteLine();
        _console.WriteLine("Environment Variables:");
        _console.WriteLine("  PIGMENTOR_GIGACHAT_AUTH_KEY      GigaChat Authorization Key for headless/CI scenarios");
    }
}
