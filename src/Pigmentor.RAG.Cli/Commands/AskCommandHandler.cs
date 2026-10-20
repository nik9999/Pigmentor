using Pigmentor.App.Runtime;
using Pigmentor.Generation;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

public sealed class AskCommandHandler
{
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

    public void PrintSourcesAndCitations(RagResponse response)
    {
        if (response.Citations.Count == 0 && response.Sources.Count == 0)
        {
            return;
        }

        _console.WriteLine();
        _console.WriteLine("Sources:");

        if (response.Citations.Count > 0)
        {
            foreach (var citation in response.Citations)
            {
                var doc = citation.DocumentId;
                var snippet = TruncateSnippet(citation.Snippet);
                _console.WriteLine($"  [{citation.Label}] {doc} — {snippet}");
            }
        }
        else
        {
            var idx = 1;
            foreach (var source in response.Sources)
            {
                var doc = source.Chunk.DocumentId;
                var snippet = TruncateSnippet(source.Chunk.Text);
                _console.WriteLine($"  [S{idx}] {doc} — {snippet}");
                idx++;
            }
        }
    }

    public void PrintDiagnostics(RagDiagnostics diag)
    {
        _console.WriteLine();
        _console.WriteLine("Diagnostics:");
        _console.WriteLine($"  Retrieval candidates: {diag.RetrievedCandidateCount}");
        _console.WriteLine($"  Sources used:         {diag.ContextSourceCount}");
        _console.WriteLine($"  Valid citations:      {diag.ValidCitationCount}");
        _console.WriteLine($"  Invalid citations:    {diag.InvalidCitationCount}");
        _console.WriteLine($"  Prompt tokens:        {diag.PromptTokenCount}");
        _console.WriteLine($"  Latency:              {diag.TotalLatencyMs} ms");
    }

    private static string TruncateSnippet(string? text, int maxLength = 120)
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

        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..maxLength] + "...";
    }
}

