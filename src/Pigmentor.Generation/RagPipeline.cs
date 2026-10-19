using System.Diagnostics;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Generation;

public interface IChatGenerator
{
    Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default);
}

public sealed record RagResponse(
    string Answer,
    IReadOnlyList<RetrievalResult> Sources,
    long LatencyMs);

public sealed class RagPipeline
{
    private readonly IRetriever _retriever;
    private readonly IChatGenerator _chat;

    public RagPipeline(IRetriever retriever, IChatGenerator chat)
    {
        _retriever = retriever;
        _chat = chat;
    }

    public async Task<RagResponse> AskAsync(
        string question,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var stopwatch = Stopwatch.StartNew();
        var sources = await _retriever.RetrieveAsync(question, topK, cancellationToken);
        var context = string.Join("\n\n", sources.Select((s, i) => $"[{i + 1}] {s.Chunk.Text}"));
        var prompt = $"Answer using only the context below. If the answer is not present, say so.\n\nContext:\n{context}\n\nQuestion: {question}";
        var answer = await _chat.GenerateAsync(prompt, cancellationToken);
        stopwatch.Stop();
        return new RagResponse(answer, sources, stopwatch.ElapsedMilliseconds);
    }
}
