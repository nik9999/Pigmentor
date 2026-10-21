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

    public async Task<int> ExecuteAsync(string? explicitRootPath = null, CancellationToken cancellationToken = default)
    {
        var created = await _configManager.InitializeAsync(explicitRootPath, cancellationToken).ConfigureAwait(false);
        var config = await _configManager.LoadAsync(cancellationToken).ConfigureAwait(false);
        var effectiveRoot = _paths.ResolveEffectiveRootPath(config.RootPath);
        var dbPath = _paths.GetDatabasePath(effectiveRoot);

        if (created)
        {
            _console.WriteLine("Pigmentor workspace initialized.");
        }
        else
        {
            _console.WriteLine("Pigmentor workspace already exists (verified).");
        }

        _console.WriteLine();
        _console.WriteLine($"Root path: {_paths.ResolveEffectiveRootPath(config.RootPath)}");
        _console.WriteLine($"Database:  {dbPath}");
        _console.WriteLine($"Config:    {_paths.ConfigFilePath}");
        _console.WriteLine();
        _console.WriteLine("Document silos:");
        foreach (var silo in PigmentorSilos.All)
        {
            _console.WriteLine($"  - {silo}/");
        }

        _console.WriteLine();
        _console.WriteLine("Next steps:");
        _console.WriteLine("  pigmentor auth gigachat");
        _console.WriteLine("  pigmentor sync");
        _console.WriteLine("  pigmentor ask \"<question>\"");

        return 0;
    }
}
