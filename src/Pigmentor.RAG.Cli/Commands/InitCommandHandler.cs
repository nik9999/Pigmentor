using Pigmentor.App.Configuration;
using Pigmentor.App.Paths;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

public sealed class InitCommandHandler
{
    private readonly IPigmentorAppPaths _paths;
    private readonly PigmentorConfigManager _configManager;
    private readonly IConsoleIO _console;

    public InitCommandHandler(IPigmentorAppPaths paths, PigmentorConfigManager configManager, IConsoleIO console)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var created = await _configManager.InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (created)
        {
            _console.WriteLine("Pigmentor initialized.");
        }
        else
        {
            _console.WriteLine("Pigmentor configuration already exists (unchanged).");
        }

        _console.WriteLine();
        _console.WriteLine($"Config:    {_paths.ConfigFilePath}");
        _console.WriteLine($"Data:      {_paths.DataDirectory}");
        _console.WriteLine($"Database:  {_paths.DefaultDatabasePath}");
        _console.WriteLine();
        _console.WriteLine("Next steps:");
        _console.WriteLine("  pigmentor auth gigachat");
        _console.WriteLine("  pigmentor ingest <path>");
        _console.WriteLine("  pigmentor ask \"<question>\"");

        return 0;
    }
}

