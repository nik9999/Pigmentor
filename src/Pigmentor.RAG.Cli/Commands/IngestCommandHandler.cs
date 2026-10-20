using Pigmentor.App.Runtime;
using Pigmentor.Core.Abstractions;
using Pigmentor.Ingestion.Lifecycle;
using Pigmentor.Ingestion.Services;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

public sealed class IngestCommandHandler
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

    private readonly Func<CancellationToken, Task<PigmentorAppRuntime>> _runtimeFactory;
    private readonly IConsoleIO _console;

    public IngestCommandHandler(
        Func<CancellationToken, Task<PigmentorAppRuntime>> runtimeFactory,
        IConsoleIO console)
    {
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public static bool IsSupportedExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
    }

    public async Task<int> ExecuteAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            _console.WriteError("Error: File or directory path is required for ingestion.");
            return 2;
        }

        var fullPath = Path.GetFullPath(targetPath);

        if (File.Exists(fullPath))
        {
            return await IngestSingleFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        if (Directory.Exists(fullPath))
        {
            return await IngestDirectoryAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        _console.WriteError($"Error: Path not found: '{targetPath}'.");
        return 2;
    }

    private async Task<int> IngestSingleFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!IsSupportedExtension(filePath))
        {
            _console.WriteError($"Error: Unsupported file format '{Path.GetExtension(filePath)}'. Supported formats: PDF, DOCX, XLSX, TXT, BMP, PNG, JPG.");
            return 2;
        }

        await using var runtime = await _runtimeFactory(cancellationToken).ConfigureAwait(false);

        _console.WriteLine($"Ingesting: {Path.GetFileName(filePath)}");
        _console.WriteLine();

        try
        {
            var result = await runtime.IngestionService.IngestFileAsync(filePath, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.Skipped)
            {
                _console.WriteLine("Status:  unchanged");
                _console.WriteLine("Skipped: yes");
            }
            else
            {
                _console.WriteLine($"Status:         {result.Status.ToString().ToLowerInvariant()}");
                _console.WriteLine($"Document:       {result.DocumentId}");
                _console.WriteLine($"Chunks indexed: {result.ChunksIndexed}");
                _console.WriteLine($"Chunks deleted: {result.ChunksDeleted}");
                _console.WriteLine($"Version:        {result.Version}");
            }

            return result.Status == IngestionStatus.Failed ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            _console.WriteLine();
            _console.WriteLine("Ingestion cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            _console.WriteError($"Ingestion failed: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> IngestDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var allFiles = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var supportedFiles = allFiles.Where(IsSupportedExtension).ToList();
        var unsupportedCount = allFiles.Count - supportedFiles.Count;

        if (supportedFiles.Count == 0)
        {
            _console.WriteLine($"No supported documents found in directory: {directoryPath}");
            if (unsupportedCount > 0)
            {
                _console.WriteLine($"Skipped {unsupportedCount} unsupported file(s).");
            }
            return 0;
        }

        _console.WriteLine($"Discovered {supportedFiles.Count} supported document(s) in {directoryPath} (skipped {unsupportedCount} unsupported).");
        _console.WriteLine();

        await using var runtime = await _runtimeFactory(cancellationToken).ConfigureAwait(false);

        var succeeded = 0;
        var unchanged = 0;
        var failed = 0;

        foreach (var file in supportedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(directoryPath, file);
            _console.Write($"Ingesting: {relativePath} ... ");

            try
            {
                var result = await runtime.IngestionService.IngestFileAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);

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
        _console.WriteLine("Directory ingestion summary:");
        _console.WriteLine($"  Processed:             {supportedFiles.Count}");
        _console.WriteLine($"  Succeeded:             {succeeded}");
        _console.WriteLine($"  Unchanged:             {unchanged}");
        _console.WriteLine($"  Failed:                {failed}");
        _console.WriteLine($"  Skipped (unsupported): {unsupportedCount}");

        return failed > 0 ? 1 : 0;
    }
}
