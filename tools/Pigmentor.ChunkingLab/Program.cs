using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using ExcelDataReader;
using UglyToad.PdfPig;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Tokens;
using Pigmentor.Ingestion.Chunking;

var options = LabOptions.Parse(args);
var repositoryRoot = FindRepositoryRoot();
var inputDirectory = ResolvePath(options.InputDirectory, repositoryRoot);
var outputPath = options.OutputPath is null ? null : ResolvePath(options.OutputPath, repositoryRoot);
if (options.ShowHelp)
{
    LabOptions.PrintHelp();
    return;
}

if (!Directory.Exists(inputDirectory))
{
    Console.Error.WriteLine($"Input directory was not found: {inputDirectory}");
    Environment.ExitCode = 2;
    return;
}

var files = Directory.EnumerateFiles(inputDirectory, "*", SearchOption.AllDirectories)
    .Where(path => !Path.GetFileName(path).StartsWith('.'))
    .Where(path => options.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToArray();

if (files.Length == 0)
{
    Console.WriteLine("No supported documents found.");
    return;
}

var strategies = CreateStrategies(options);
var allResults = new List<ChunkRecord>();
var chunksByStrategy = strategies.ToDictionary(item => item.Name, _ => new List<ChunkRecord>(), StringComparer.Ordinal);
foreach (var file in files)
{
    string content;
    try
    {
        content = await ReadDocumentTextAsync(file);
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException)
    {
        Console.Error.WriteLine($"Skipped {file}: {exception.Message}");
        continue;
    }

    if (string.IsNullOrWhiteSpace(content))
    {
        Console.WriteLine($"Skipped {Path.GetRelativePath(inputDirectory, file)}: no extractable text");
        continue;
    }

    var document = CreateDocument(file, inputDirectory, content);
    foreach (var (name, chunker) in strategies)
    {
        var chunks = new List<DocumentChunk>();
        await foreach (var chunk in chunker.ChunkAsync(document))
        {
            chunks.Add(chunk);
            var record = ChunkRecord.From(name, file, chunk);
            allResults.Add(record);
            chunksByStrategy[name].Add(record);
        }

        var average = chunks.Count == 0 ? 0 : chunks.Average(chunk => chunk.Text.Length);
        Console.WriteLine($"{name,-28} {Path.GetRelativePath(inputDirectory, file),-48} chunks={chunks.Count,4} avg_chars={average,7:0}");
    }
}

if (options.EvaluationPath is not null)
{
    var evaluationPath = ResolvePath(options.EvaluationPath, repositoryRoot);
    var evaluation = await RecallEvaluator.LoadAsync(evaluationPath);
    RecallEvaluator.PrintReport(
        evaluation,
        chunksByStrategy,
        options.RecallAtK,
        inputDirectory);
}

if (outputPath is not null)
{
    var outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(outputDirectory))
    {
        Directory.CreateDirectory(outputDirectory);
    }

    await File.WriteAllTextAsync(
        outputPath,
        JsonSerializer.Serialize(allResults, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nSaved {allResults.Count} chunk records to {outputPath}");
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "Pigmentor.slnx")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static string ResolvePath(string path, string repositoryRoot)
{
    if (Path.IsPathRooted(path))
    {
        return path;
    }

    var currentPath = Path.GetFullPath(path, Directory.GetCurrentDirectory());
    return File.Exists(currentPath) || Directory.Exists(currentPath)
        ? currentPath
        : Path.GetFullPath(path, repositoryRoot);
}

static IReadOnlyList<(string Name, IChunker Chunker)> CreateStrategies(LabOptions options)
{
    var strategies = new List<(string, IChunker)>();
    if (options.Strategy is "all" or "fixed")
    {
        strategies.Add(("fixed-size", new FixedSizeChunker(options.ChunkSize, options.Overlap)));
    }
    if (options.Strategy is "all" or "recursive")
    {
        strategies.Add(("recursive", new RecursiveCharacterChunker(options.ChunkSize)));
    }
    if (options.Strategy is "all" or "sentence")
    {
        strategies.Add(("sentence", new SentenceChunker(options.MaxSentences)));
    }
    if (options.Strategy is "all" or "token")
    {
        strategies.Add(("token-aware(fixed-size)", new TokenAwareChunker(
            new FixedSizeChunker(options.ChunkSize, options.Overlap),
            new TokenCounter(),
            options.MaxTokens)));
    }

    if (strategies.Count == 0)
    {
        throw new ArgumentException($"Unknown strategy '{options.Strategy}'. Use all, fixed, recursive, sentence or token.");
    }

    return strategies;
}

static Document CreateDocument(string path, string inputDirectory, string content)
{
    var relativePath = Path.GetRelativePath(inputDirectory, path).Replace('\\', '/');
    var id = Path.ChangeExtension(relativePath, null).Replace('/', ':');
    var title = Path.GetFileNameWithoutExtension(path);
    var metadata = new DocumentMetadata(
        id, "", "", "", "", "Reference", 0, "", DateOnly.MinValue, title);
    return new Document(metadata, content, relativePath);
}

static async Task<string> ReadDocumentTextAsync(string path)
{
    var extension = Path.GetExtension(path).ToLowerInvariant();
    if (extension is ".txt" or ".md" or ".html" or ".htm")
    {
        return await File.ReadAllTextAsync(path);
    }

    if (extension == ".pdf")
    {
        return await Task.Run(() =>
        {
            using var pdf = PdfDocument.Open(path);
            return string.Join("\n\n", pdf.GetPages().Select(page => page.Text));
        });
    }

    if (extension == ".docx")
    {
        return await Task.Run(() =>
        {
            using var word = WordprocessingDocument.Open(path, false);
            return word.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
        });
    }

    if (extension is ".xlsx" or ".xls")
    {
        return await Task.Run(() =>
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            using var stream = File.OpenRead(path);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var dataSet = reader.AsDataSet();
            return string.Join("\n\n", dataSet.Tables.Cast<System.Data.DataTable>().Select(table =>
                string.Join("\n", table.Rows.Cast<System.Data.DataRow>().Select(row =>
                    string.Join(" | ", row.ItemArray.Select(value => value?.ToString() ?? string.Empty))))));
        });
    }

    if (extension is ".png" or ".bmp")
    {
        return $"[IMAGE DOCUMENT]\nFile: {Path.GetFileName(path)}\nOCR/vision extraction is not configured for this run.";
    }

    if (extension == ".doc")
    {
        return $"[WORD DOCUMENT]\nFile: {Path.GetFileName(path)}\nLegacy .doc extraction is not configured; convert to .docx for text chunking.";
    }

    throw new InvalidDataException($"Unsupported document format: {extension}");
}

internal sealed record ChunkRecord(
    string Strategy,
    string SourcePath,
    string ChunkId,
    int ChunkIndex,
    int CharacterCount,
    string Preview,
    string Text)
{
    public static ChunkRecord From(string strategy, string sourcePath, DocumentChunk chunk) =>
        new(strategy, sourcePath, chunk.ChunkId, chunk.ChunkIndex, chunk.Text.Length,
            chunk.Text[..Math.Min(chunk.Text.Length, 160)].ReplaceLineEndings(" "), chunk.Text);
}

internal sealed class LabOptions
{
    public string InputDirectory { get; private set; } = Path.Combine("data", "technical-docs");
    public string Strategy { get; private set; } = "all";
    public int ChunkSize { get; private set; } = 800;
    public int Overlap { get; private set; } = 100;
    public int MaxSentences { get; private set; } = 3;
    public int MaxTokens { get; private set; } = 512;
    public string? OutputPath { get; private set; } = Path.Combine("artifacts", "chunking-results.json");
    public string? EvaluationPath { get; private set; }
    public IReadOnlyList<int> RecallAtK { get; private set; } = [1, 3, 5, 10];
    public HashSet<string> Extensions { get; } = [
        ".md", ".txt", ".html", ".htm", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".bmp", ".png"];
    public bool ShowHelp { get; private set; }

    public static LabOptions Parse(string[] args)
    {
        var result = new LabOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option is "--help" or "-h")
            {
                result.ShowHelp = true;
                continue;
            }
            if (option == "--no-output")
            {
                result.OutputPath = null;
                continue;
            }

            var value = i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"Missing value for {option}.");
            switch (option)
            {
                case "--input": result.InputDirectory = value; break;
                case "--strategy": result.Strategy = value.ToLowerInvariant(); break;
                case "--chunk-size": result.ChunkSize = ParsePositive(value, "--chunk-size"); break;
                case "--overlap": result.Overlap = ParseNonNegative(value, "--overlap"); break;
                case "--max-sentences": result.MaxSentences = ParsePositive(value, "--max-sentences"); break;
                case "--max-tokens": result.MaxTokens = ParsePositive(value, "--max-tokens"); break;
                case "--output": result.OutputPath = value; break;
                case "--eval": result.EvaluationPath = value; break;
                case "--k": result.RecallAtK = ParseKValues(value); break;
                default: throw new ArgumentException($"Unknown option '{option}'.");
            }
        }

        return result;
    }

    public static void PrintHelp() => Console.WriteLine("""
        Pigmentor.ChunkingLab

        Compare ingestion chunking strategies over documents in data/.

        Options:
          --input <path>          Input directory (default: data/technical-docs)
          --strategy <name>       all, fixed, recursive, sentence, token
          --chunk-size <number>   Character budget (default: 800)
          --overlap <number>      Fixed chunk overlap (default: 100)
          --max-sentences <n>     Sentences per chunk (default: 3)
          --max-tokens <number>   Token-aware budget (default: 512)
          --output <path>         JSON output (default: artifacts/chunking-results.json)
          --no-output             Do not write JSON results
          --eval <path>           Recall@K eval set JSON
          --k <1,3,5,10>          K values for Recall@K (default: 1,3,5,10)
          --help                  Show this help

        Eval query shape:
          [{ "id": "q1", "query": "...", "relevantDocuments": ["file.pdf"] }]
        Use optional "relevantChunkIds" for chunk-level ground truth.
        """);

    private static int ParsePositive(string value, string option) =>
        int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{option} must be a positive integer.");

    private static int ParseNonNegative(string value, string option) =>
        int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"{option} must be a non-negative integer.");

    private static int[] ParseKValues(string value)
    {
        var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => ParsePositive(item, "--k"))
            .Distinct()
            .OrderBy(item => item)
            .ToArray();
        return values.Length == 0
            ? throw new ArgumentException("--k must contain at least one positive integer.")
            : values;
    }
}
