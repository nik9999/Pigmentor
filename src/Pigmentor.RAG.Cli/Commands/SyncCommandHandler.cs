using Pigmentor.App.Configuration;
using Pigmentor.App.Paths;
using Pigmentor.App.Runtime;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Ingestion.Lifecycle;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

public sealed class SyncCommandHandler
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".docx",
        ".xlsx",
        ".txt",
        ".bmp",
        ".png",
        ".jpg",
        ".jpeg"
    };

    private readonly IPigmentorAppPaths _paths;
    private readonly PigmentorConfigManager _configManager;
    private readonly Func<CancellationToken, Task<PigmentorAppRuntime>> _runtimeFactory;
    private readonly IConsoleIO _console;

    public SyncCommandHandler(
        IPigmentorAppPaths paths,
        PigmentorConfigManager configManager,
        Func<CancellationToken, Task<PigmentorAppRuntime>> runtimeFactory,
        IConsoleIO console)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configManager.LoadAsync(cancellationToken).ConfigureAwait(false);
        var rootPath = _paths.ResolveEffectiveRootPath(config.RootPath);

        _console.WriteLine($"Synchronizing Pigmentor workspace: {rootPath}");
        _console.WriteLine();

        if (!Directory.Exists(rootPath))
        {
            _console.WriteError($"Error: Workspace rootPath '{rootPath}' does not exist. Run 'pigmentor init' first.");
            return 2;
        }

        // 1. Inspect top-level directories and warn on unknown directories
        var topLevelDirs = Directory.GetDirectories(rootPath);
        var inaccessibleCount = 0;

        foreach (var topDir in topLevelDirs)
        {
            var dirName = Path.GetFileName(topDir);
            if (!PigmentorSilos.IsKnownSilo(dirName))
            {
                _console.WriteLine($"Warning: Ignoring unknown top-level directory '{dirName}' (only known silos participate in sync).");
            }
        }

        // 2. Discover supported files across all known silos using resilient traversal
        var discoveredItems = new List<(string FilePath, string Silo)>();
        var unsupportedCount = 0;

        foreach (var silo in PigmentorSilos.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var siloDir = Path.Combine(rootPath, silo);

            if (!Directory.Exists(siloDir))
            {
                continue;
            }

            TraverseSiloResiliently(siloDir, silo, discoveredItems, ref unsupportedCount, ref inaccessibleCount, cancellationToken);
        }

        // Deterministic sort by file path
        discoveredItems.Sort((a, b) => string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase));

        if (discoveredItems.Count == 0)
        {
            _console.WriteLine("No supported documents found in workspace silos.");
            if (unsupportedCount > 0)
            {
                _console.WriteLine($"Skipped unsupported files: {unsupportedCount}");
            }
            if (inaccessibleCount > 0)
            {
                _console.WriteLine($"Skipped inaccessible paths: {inaccessibleCount}");
            }
            return 0;
        }

        _console.WriteLine($"Found {discoveredItems.Count} document(s) across silos (unsupported: {unsupportedCount}, inaccessible: {inaccessibleCount}).");
        _console.WriteLine();

        // 3. Synchronize documents through ingestion service
        await using var runtime = await _runtimeFactory(cancellationToken).ConfigureAwait(false);

        var succeeded = 0;
        var unchanged = 0;
        var failed = 0;

        foreach (var (filePath, silo) in discoveredItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(rootPath, filePath);
            _console.Write($"Syncing: [{silo}] {relativePath} ... ");

            var docId = Path.GetFileNameWithoutExtension(filePath);
            var metadata = new DocumentMetadata(
                Id: docId,
                Silo: silo,
                Department: "Engineering",
                Office: "Main",
                ConfidentialityLevel: "Internal",
                DocumentType: "Manual",
                FiscalYear: DateTime.UtcNow.Year,
                Author: "System",
                LastModified: DateOnly.FromDateTime(File.GetLastWriteTimeUtc(filePath)),
                Title: docId);

            try
            {
                var result = await runtime.IngestionService.IngestFileAsync(filePath, metadata, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (result.Skipped)
                {
                    _console.WriteLine("unchanged");
                    unchanged++;
                }
                else if (result.Status == IngestionStatus.Succeeded)
                {
                    _console.WriteLine($"succeeded ({result.ChunksIndexed} chunks)");
                    succeeded++;
                }
                else
                {
                    _console.WriteLine($"failed ({result.Status})");
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                _console.WriteLine("cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _console.WriteLine($"failed ({ex.Message})");
                failed++;
            }
        }

        _console.WriteLine();
        _console.WriteLine("Workspace synchronization summary:");
        _console.WriteLine($"  Processed:            {discoveredItems.Count}");
        _console.WriteLine($"  Succeeded:            {succeeded}");
        _console.WriteLine($"  Unchanged:            {unchanged}");
        _console.WriteLine($"  Failed:               {failed}");
        _console.WriteLine($"  Skipped unsupported:  {unsupportedCount}");
        _console.WriteLine($"  Skipped inaccessible: {inaccessibleCount}");

        return failed > 0 ? 1 : 0;
    }

    private void TraverseSiloResiliently(
        string currentDir,
        string silo,
        List<(string FilePath, string Silo)> results,
        ref int unsupportedCount,
        ref int inaccessibleCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(currentDir);
        }
        catch (Exception ex)
        {
            inaccessibleCount++;
            _console.WriteLine($"Warning: Inaccessible directory '{currentDir}': {ex.Message}");
            return;
        }

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            if (!string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext))
            {
                results.Add((file, silo));
            }
            else
            {
                unsupportedCount++;
            }
        }

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(currentDir);
        }
        catch (Exception ex)
        {
            inaccessibleCount++;
            _console.WriteLine($"Warning: Inaccessible subdirectories in '{currentDir}': {ex.Message}");
            return;
        }

        foreach (var subDir in subDirs)
        {
            TraverseSiloResiliently(subDir, silo, results, ref unsupportedCount, ref inaccessibleCount, cancellationToken);
        }
    }
}

