using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Tokens;
using Pigmentor.Retrieval;

namespace Pigmentor.Generation;

/// <summary>
/// Abstraction for text generation from a formatted prompt.
/// </summary>
public interface IChatGenerator
{
    Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pipeline orchestrating candidate retrieval, reranking/diversity, context budgeting,
/// lost-in-the-middle ordering, answer generation, and deterministic citation mapping.
/// </summary>
public sealed class RagPipeline : IRagPipeline
{
    private static readonly Regex CitationRegex = new(@"\[(S\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IRetriever _retriever;
    private readonly IChatGenerator _chat;
    private readonly IReranker _reranker;
    private readonly IContextBuilder _contextBuilder;
    private readonly ITokenCounter _tokenCounter;
    private readonly RagPipelineSettings _settings;

    public RagPipeline(
        IRetriever retriever,
        IChatClient chatClient,
        ITokenCounter tokenCounter)
        : this(retriever, chatClient, null, null, tokenCounter)
    {
    }

    public RagPipeline(
        IRetriever retriever,
        IChatGenerator chat,
        ITokenCounter tokenCounter)
        : this(retriever, chat, null, null, tokenCounter)
    {
    }

    public RagPipeline(
        IRetriever retriever,
        IChatClient chatClient,
        IReranker? reranker = null,
        IContextBuilder? contextBuilder = null,
        ITokenCounter? tokenCounter = null,
        RagPipelineSettings? settings = null)
        : this(retriever, new ChatClientGenerator(chatClient), reranker, contextBuilder, tokenCounter, settings)
    {
    }

    public RagPipeline(
        IRetriever retriever,
        IChatGenerator chat,
        IReranker? reranker = null,
        IContextBuilder? contextBuilder = null,
        ITokenCounter? tokenCounter = null,
        RagPipelineSettings? settings = null)
    {
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _reranker = reranker ?? NoOpReranker.Instance;
        _settings = settings ?? new RagPipelineSettings();
        _tokenCounter = tokenCounter ?? new TokenCounter();
        _contextBuilder = contextBuilder ?? new ContextBuilder(_tokenCounter, _settings.ContextOptions with { Budget = _settings.Budget });
    }

    /// <inheritdoc />
    public async Task<RagResponse> AskAsync(
        RagRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);

        var stopwatch = Stopwatch.StartNew();
        var topK = request.TopK ?? _settings.DefaultTopK;

        // 1. Candidate retrieval (passing request with MetadataFilter into retrieval layer)
        var retrievalRequest = new RetrievalRequest(request.Question, topK, request.MetadataFilter);
        var candidatePool = await _retriever.RetrieveAsync(retrievalRequest, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Defensive post-filtering (guarantees adherence even if retrieval backend had loose semantics)
        IReadOnlyList<RetrievalResult> filteredCandidates = request.MetadataFilter is not null
            ? candidatePool.Where(s => request.MetadataFilter.Matches(s.Chunk.Metadata)).ToList()
            : candidatePool;

        // 3. Reranker receives candidates that satisfied mandatory filtering
        var rerankedCandidates = await _reranker.RerankAsync(
            request.Question,
            filteredCandidates,
            filteredCandidates.Count,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // 4. Context builder enforces token budget, accounting for prompt instructions and question overhead
        var promptOverheadTokens = _tokenCounter.CountTokens(BuildPrompt(request.Question, RagContext.Empty));
        var contextAllowance = _settings.Budget.ComputeAvailableContextTokens(promptOverheadTokens);
        var ragContext = _contextBuilder.BuildContext(rerankedCandidates, contextAllowance);

        // 5. Prompt assembly requiring explicit [S1], [S2] references
        var prompt = BuildPrompt(request.Question, ragContext);

        // 6. Answer generation
        var answer = await _chat.GenerateAsync(prompt, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        // 7. Deterministic citation parsing and validation against context sources
        var citations = ParseAndMapCitations(answer, ragContext.SourceMap);

        // 8. Diagnostics with real metrics
        var diagnostics = new RagDiagnostics(
            TotalLatency: stopwatch.Elapsed,
            RetrievedCandidateCount: candidatePool.Count,
            ContextSourceCount: ragContext.Sources.Count,
            ContextTokenCount: ragContext.Sources.Count > 0 ? ragContext.TokenCount : null);

        // 9. Response where sources correspond strictly to the context
        return new RagResponse(answer, citations, ragContext.Sources, diagnostics);
    }

    /// <summary>
    /// Convenience overload querying with raw question string and optional top-k limit.
    /// </summary>
    public Task<RagResponse> AskAsync(
        string question,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        return AskAsync(new RagRequest(question, topK: topK), cancellationToken);
    }

    private static string BuildPrompt(string question, RagContext context)
    {
        if (context.Sources.Count == 0)
        {
            return $"Answer the question using only the context below. If the answer cannot be found in the context, state that you do not have enough information.\n\nContext:\n(No relevant sources found.)\n\nQuestion: {question}";
        }

        return $"Answer the question using only the context sources below.\n" +
               $"For each factual claim, cite the corresponding source tag in brackets (e.g. [S1] or [S2]).\n" +
               $"Do not cite sources that are not in the context.\n" +
               $"If the answer cannot be found in the context, state that you do not have enough information.\n\n" +
               $"Context:\n{context.FormattedContext}\n\n" +
               $"Question: {question}";
    }

    private static List<Citation> ParseAndMapCitations(
        string answer,
        IReadOnlyDictionary<string, RetrievalResult> sourceMap)
    {
        if (string.IsNullOrWhiteSpace(answer) || sourceMap.Count == 0)
        {
            return [];
        }

        var matches = CitationRegex.Matches(answer);
        var citedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var citations = new List<Citation>();

        foreach (Match match in matches)
        {
            var tag = match.Groups[1].Value.ToUpperInvariant();
            if (citedTags.Add(tag) && sourceMap.TryGetValue(tag, out var sourceResult))
            {
                var sourceIndex = int.TryParse(tag[1..], out var idx) ? idx : citations.Count + 1;
                citations.Add(Citation.FromRetrievalResult(sourceIndex, sourceResult, tag));
            }
        }

        return citations;
    }

    private sealed class ChatClientGenerator : IChatGenerator
    {
        private readonly IChatClient _client;

        public ChatClientGenerator(IChatClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var response = await _client.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Text ?? string.Empty;
        }
    }
}
