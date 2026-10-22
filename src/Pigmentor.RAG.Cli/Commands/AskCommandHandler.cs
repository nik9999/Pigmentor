using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pigmentor.App.Runtime;
using Pigmentor.Generation;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

public sealed class AskCommandHandler
{
    private static readonly string[] LineSeparators = ["\r\n", "\r", "\n"];

    private readonly Func<CancellationToken, Task<PigmentorAppRuntime>> _runtimeFactory;
    private readonly IConsoleIO _console;

    public AskCommandHandler(
        Func<CancellationToken, Task<PigmentorAppRuntime>> runtimeFactory,
        IConsoleIO console)
    {
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public async Task<int> ExecuteAsync(
        string question,
        bool stream = false,
        bool diagnostics = false,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            _console.WriteError("Error: Question cannot be empty.");
            return 2;
        }

        await using var runtime = await _runtimeFactory(cancellationToken).ConfigureAwait(false);

        var request = new RagRequest(question.Trim())
        {
            ConversationId = conversationId
        };

        try
        {
            if (stream)
            {
                return await ExecuteStreamingAsync(runtime.Pipeline, request, diagnostics, cancellationToken).ConfigureAwait(false);
            }

            return await ExecuteNonStreamingAsync(runtime.Pipeline, request, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _console.WriteLine();
            _console.WriteLine("Operation cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            _console.WriteError($"Error executing query: {ex.Message}");
            return 1;
        }
    }

    public async Task<int> ExecuteBulkAsync(
        string bulkFilePath,
        bool diagnostics = false,
        string? outputPath = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bulkFilePath) || !File.Exists(bulkFilePath))
        {
            _console.WriteError($"Error: Bulk questions file '{bulkFilePath}' not found.");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            if (File.Exists(outputPath) && !force)
            {
                _console.WriteError($"Error: Output file '{outputPath}' already exists. Use --force to overwrite.");
                return 2;
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(bulkFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _console.WriteError($"Error reading bulk file '{bulkFilePath}': {ex.Message}");
            return 2;
        }

        var questions = ParseQuestions(content);
        if (questions.Count == 0)
        {
            _console.WriteError($"Error: Bulk questions file '{bulkFilePath}' contains no valid questions.");
            return 2;
        }

        await using var fileWriter = !string.IsNullOrWhiteSpace(outputPath)
            ? new StreamWriter(outputPath, false, Encoding.UTF8)
            : null;

        void WriteOutput(string? text = "")
        {
            _console.WriteLine(text ?? string.Empty);
            fileWriter?.WriteLine(text ?? string.Empty);
        }

        await using var runtime = await _runtimeFactory(cancellationToken).ConfigureAwait(false);
        var pipeline = runtime.Pipeline;

        var completedCount = 0;
        var failedCount = 0;
        long totalLatencyMs = 0;

        const string Separator = "============================================================";

        WriteOutput(Separator);

        for (var i = 0; i < questions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var question = questions[i];

            WriteOutput(string.Create(CultureInfo.InvariantCulture, $"[{i + 1}/{questions.Count}] QUESTION"));
            WriteOutput(question);
            WriteOutput();

            var sw = Stopwatch.StartNew();
            try
            {
                var request = new RagRequest(question);
                var response = await pipeline.AskAsync(request, cancellationToken).ConfigureAwait(false);
                sw.Stop();

                var latency = response.Diagnostics.TotalLatencyMs > 0 ? response.Diagnostics.TotalLatencyMs : sw.ElapsedMilliseconds;
                totalLatencyMs += latency;
                completedCount++;

                WriteOutput("ANSWER");
                WriteOutput(response.Answer);
                WriteOutput();
                WriteOutput("SOURCES");
                WriteOutput(FormatSourcesAndCitations(response, truncateSnippets: false));

                if (diagnostics)
                {
                    WriteOutput();
                    WriteOutput("DIAGNOSTICS");
                    WriteOutput(FormatDiagnostics(response.Diagnostics));
                }
            }
            catch (OperationCanceledException)
            {
                _console.WriteLine();
                _console.WriteLine("Bulk operation cancelled by user.");
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                failedCount++;

                WriteOutput("ANSWER");
                WriteOutput($"Error: {ex.Message}");
                WriteOutput();
                WriteOutput("SOURCES");
                WriteOutput("  (None)");

                if (diagnostics)
                {
                    WriteOutput();
                    WriteOutput("DIAGNOSTICS");
                    WriteOutput("  (None)");
                }
            }

            WriteOutput();
            WriteOutput(Separator);
        }

        var averageLatencyMs = completedCount > 0 ? totalLatencyMs / completedCount : 0;

        WriteOutput();
        WriteOutput("Bulk Ask Summary");
        WriteOutput("================");
        WriteOutput(string.Create(CultureInfo.InvariantCulture, $"{"Questions:".PadRight(18)}{questions.Count}"));
        WriteOutput(string.Create(CultureInfo.InvariantCulture, $"{"Completed:".PadRight(18)}{completedCount}"));
        WriteOutput(string.Create(CultureInfo.InvariantCulture, $"{"Failed:".PadRight(18)}{failedCount}"));
        WriteOutput(string.Create(CultureInfo.InvariantCulture, $"{"Total latency:".PadRight(18)}{totalLatencyMs} ms"));
        WriteOutput(string.Create(CultureInfo.InvariantCulture, $"{"Average latency:".PadRight(18)}{averageLatencyMs} ms"));

        if (fileWriter != null)
        {
            await fileWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return failedCount == 0 ? 0 : 1;
    }

    public static IReadOnlyList<string> ParseQuestions(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var lines = text.Split(LineSeparators, StringSplitOptions.None);
        var questions = new List<string>();

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                questions.Add(trimmed);
            }
        }

        return questions;
    }

    private async Task<int> ExecuteStreamingAsync(
        IRagPipeline pipeline,
        RagRequest request,
        bool diagnostics,
        CancellationToken cancellationToken)
    {
        RagResponse? finalResponse = null;

        await foreach (var evt in pipeline.AskStreamingAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case RagTextDelta textDelta:
                    _console.Write(textDelta.Text);
                    break;

                case RagCompleted completed:
                    finalResponse = completed.Response;
                    break;
            }
        }

        _console.WriteLine();

        if (finalResponse != null)
        {
            PrintSourcesAndCitations(finalResponse);

            if (diagnostics)
            {
                PrintDiagnostics(finalResponse.Diagnostics);
            }
        }

        return 0;
    }

    private async Task<int> ExecuteNonStreamingAsync(
        IRagPipeline pipeline,
        RagRequest request,
        bool diagnostics,
        CancellationToken cancellationToken)
    {
        var response = await pipeline.AskAsync(request, cancellationToken).ConfigureAwait(false);

        _console.WriteLine(response.Answer);

        PrintSourcesAndCitations(response);

        if (diagnostics)
        {
            PrintDiagnostics(response.Diagnostics);
        }

        return 0;
    }

    public static string FormatSourcesAndCitations(RagResponse response, bool truncateSnippets = true)
    {
        if (response.Citations.Count == 0 && response.Sources.Count == 0)
        {
            return "  (None)";
        }

        var sb = new StringBuilder();
        if (response.Citations.Count > 0)
        {
            for (var i = 0; i < response.Citations.Count; i++)
            {
                var citation = response.Citations[i];
                var doc = citation.DocumentId;
                var snippet = truncateSnippets ? TruncateSnippet(citation.Snippet) : CleanSnippet(citation.Snippet);
                sb.Append(CultureInfo.InvariantCulture, $"  [{citation.Label}] {doc} — {snippet}");
                if (i < response.Citations.Count - 1)
                {
                    sb.AppendLine();
                }
            }
        }
        else
        {
            for (var i = 0; i < response.Sources.Count; i++)
            {
                var source = response.Sources[i];
                var doc = source.Chunk.DocumentId;
                var snippet = truncateSnippets ? TruncateSnippet(source.Chunk.Text) : CleanSnippet(source.Chunk.Text);
                sb.Append(CultureInfo.InvariantCulture, $"  [S{i + 1}] {doc} — {snippet}");
                if (i < response.Sources.Count - 1)
                {
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    public void PrintSourcesAndCitations(RagResponse response)
    {
        if (response.Citations.Count == 0 && response.Sources.Count == 0)
        {
            return;
        }

        _console.WriteLine();
        _console.WriteLine("Sources:");
        _console.WriteLine(FormatSourcesAndCitations(response, truncateSnippets: true));
    }

    public static string FormatDiagnostics(RagDiagnostics diag)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Retrieval candidates: {diag.RetrievedCandidateCount}");
        if (diag.MaxDenseScore.HasValue)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Max dense similarity: {diag.MaxDenseScore.Value:F3}");
        }
        if (diag.MaxSparseScore.HasValue)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Max sparse score:     {diag.MaxSparseScore.Value:F3}");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Sources used:         {diag.ContextSourceCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Valid citations:      {diag.ValidCitationCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Invalid citations:    {diag.InvalidCitationCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Required citations satisfied: {diag.RequiredCitationsSatisfied.ToString().ToLowerInvariant()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Prompt tokens:        {diag.PromptTokenCount}");
        sb.Append(CultureInfo.InvariantCulture, $"  Latency:              {diag.TotalLatencyMs} ms");
        return sb.ToString();
    }

    public void PrintDiagnostics(RagDiagnostics diag)
    {
        _console.WriteLine();
        _console.WriteLine("Diagnostics:");
        _console.WriteLine(FormatDiagnostics(diag));
    }

    public static string CleanSnippet(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (singleLine.Contains("  ", StringComparison.Ordinal))
        {
            singleLine = singleLine.Replace("  ", " ", StringComparison.Ordinal);
        }

        return singleLine;
    }

    public static string TruncateSnippet(string? text, int maxLength = 120)
    {
        var singleLine = CleanSnippet(text);
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..maxLength] + "...";
    }
}

