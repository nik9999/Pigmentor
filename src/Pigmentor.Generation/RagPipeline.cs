using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Tokens;
using Pigmentor.Retrieval;

namespace Pigmentor.Generation;

/// <summary>
/// Abstraction for text generation from a formatted prompt with optional streaming support.
/// </summary>
public interface IChatGenerator
{
    /// <summary>
    /// Generates a complete text response for the given prompt.
    /// </summary>
    Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a streaming text response, yielding incremental chunks as they arrive.
    /// Default implementation throws <see cref="NotSupportedException"/>.
    /// </summary>
    IAsyncEnumerable<string> GenerateStreamingAsync(string prompt, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException($"Streaming generation is not supported by {GetType().Name}.");
    }
}

/// <summary>
/// Pipeline orchestrating candidate retrieval, reranking/diversity, context budgeting,
/// lost-in-the-middle ordering, answer generation, and deterministic citation mapping.
/// </summary>
public sealed class RagPipeline : IRagPipeline
{
    private static readonly Regex CitationRegex = new(@"\[(s\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IRetriever _retriever;
    private readonly IChatGenerator _chat;
    private readonly IReranker _reranker;
    private readonly IContextBuilder _contextBuilder;
    private readonly ITokenCounter _tokenCounter;
    private readonly RagPipelineSettings _settings;

    /// <summary>
    /// The runtime settings configuring this RAG pipeline instance.
    /// </summary>
    public RagPipelineSettings Settings => _settings;

    public RagPipeline(
        IRetriever retriever,
        IChatClient chatClient)
        : this(retriever, chatClient, reranker: null, contextBuilder: null, tokenCounter: null, settings: null)
    {
    }

    public RagPipeline(
        IRetriever retriever,
        IChatGenerator chat)
        : this(retriever, chat, reranker: null, contextBuilder: null, tokenCounter: null, settings: null)
    {
    }

    public RagPipeline(
        IRetriever retriever,
        IChatClient chatClient,
        ITokenCounter tokenCounter)
        : this(retriever, chatClient, reranker: null, contextBuilder: null, tokenCounter: tokenCounter, settings: null)
    {
    }

    public RagPipeline(
        IRetriever retriever,
        IChatGenerator chat,
        ITokenCounter tokenCounter)
        : this(retriever, chat, reranker: null, contextBuilder: null, tokenCounter: tokenCounter, settings: null)
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
        IChatClient chatClient,
        IOptions<RagPipelineSettings> settingsOptions,
        IReranker? reranker = null,
        IContextBuilder? contextBuilder = null,
        ITokenCounter? tokenCounter = null)
        : this(retriever, new ChatClientGenerator(chatClient), reranker, contextBuilder, tokenCounter, settingsOptions?.Value)
    {
    }

    [ActivatorUtilitiesConstructor]
    public RagPipeline(
        IRetriever retriever,
        IChatGenerator chat,
        IOptions<RagPipelineSettings> settingsOptions,
        IReranker? reranker = null,
        IContextBuilder? contextBuilder = null,
        ITokenCounter? tokenCounter = null)
        : this(retriever, chat, reranker, contextBuilder, tokenCounter, settingsOptions?.Value)
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
        var prep = await PrepareExecutionAsync(request, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var genSw = Stopwatch.StartNew();
        var rawAnswer = await _chat.GenerateAsync(prep.Prompt, cancellationToken).ConfigureAwait(false);
        genSw.Stop();
        prep.Stopwatch.Stop();

        return FinalizeResponse(rawAnswer, prep, genSw.Elapsed);
    }

    /// <inheritdoc />
    public Task<RagResponse> AskAsync(
        string question,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        return AskAsync(new RagRequest(question, topK: topK), cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RagStreamEvent> AskStreamingAsync(
        RagRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prep = await PrepareExecutionAsync(request, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var genSw = Stopwatch.StartNew();
        var stream = _chat.GenerateStreamingAsync(prep.Prompt, cancellationToken);
        var answerBuilder = new StringBuilder();

        await foreach (var delta in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(delta))
            {
                answerBuilder.Append(delta);
                yield return new RagTextDelta(delta);
            }
        }

        genSw.Stop();
        prep.Stopwatch.Stop();

        var response = FinalizeResponse(answerBuilder.ToString(), prep, genSw.Elapsed);
        yield return new RagCompleted(response);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RagStreamEvent> AskStreamingAsync(
        string question,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        return AskStreamingAsync(new RagRequest(question, topK: topK), cancellationToken);
    }

    private async Task<PreparedExecution> PrepareExecutionAsync(
        RagRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);

        var topK = request.TopK ?? _settings.DefaultTopK;
        if (topK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), topK, "Effective TopK must be positive.");
        }

        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Candidate retrieval
        var retrievalSw = Stopwatch.StartNew();
        var retrievalRequest = new RetrievalRequest(request.Question, topK, request.MetadataFilter);
        var candidatePool = await _retriever.RetrieveAsync(retrievalRequest, cancellationToken).ConfigureAwait(false);
        retrievalSw.Stop();
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

        // 4. Context builder enforces token budget, accounting for prompt instructions, delimiters, and question overhead (§3, §4, §5)
        var basePrompt = RagPromptBuilder.BuildPrompt(request.Question, RagContext.Empty);
        var promptOverheadTokens = _tokenCounter.CountTokens(basePrompt);
        if (promptOverheadTokens > _settings.Budget.MaxInputTokens)
        {
            throw new InvalidOperationException(
                $"The question and prompt instructions consume {promptOverheadTokens} tokens, which exceeds the maximum input budget limit of {_settings.Budget.MaxInputTokens} tokens (MaxTotalTokens: {_settings.Budget.MaxTotalTokens}, ReservedOutputTokens: {_settings.Budget.ReservedOutputTokens}).");
        }

        var contextAllowance = _settings.Budget.ComputeAvailableContextTokens(promptOverheadTokens);
        var ragContext = _contextBuilder.BuildContext(rerankedCandidates, contextAllowance);

        // 5. Prompt assembly requiring explicit [S1], [S2] references and data boundary defense (§8, §9, §36, §37)
        var prompt = RagPromptBuilder.BuildPrompt(request.Question, ragContext);
        var promptTokenCount = _tokenCounter.CountTokens(prompt);

        // 6. Strict runtime invariant: PromptTokenCount <= MaxInputTokens (§10, §11, §12, §13)
        if (promptTokenCount > _settings.Budget.MaxInputTokens && ragContext.Sources.Count > 0)
        {
            var remainingSources = ragContext.Sources.ToList();
            while (remainingSources.Count > 0 && promptTokenCount > _settings.Budget.MaxInputTokens)
            {
                remainingSources.RemoveAt(remainingSources.Count - 1);
                ragContext = remainingSources.Count > 0
                    ? _contextBuilder.BuildContext(remainingSources, contextAllowance)
                    : RagContext.Empty;

                prompt = RagPromptBuilder.BuildPrompt(request.Question, ragContext);
                promptTokenCount = _tokenCounter.CountTokens(prompt);
            }
        }

        return new PreparedExecution(
            CandidatePool: candidatePool,
            FilteredCandidates: filteredCandidates,
            RerankedCandidates: rerankedCandidates,
            RagContext: ragContext,
            Prompt: prompt,
            PromptTokenCount: promptTokenCount,
            Stopwatch: stopwatch,
            RetrievalLatency: retrievalSw.Elapsed);
    }

    private static RagResponse FinalizeResponse(
        string? rawAnswer,
        PreparedExecution prep,
        TimeSpan generationLatency)
    {
        string answer;
        IReadOnlyList<Citation> citations;
        int validCount;
        int invalidCount;

        if (string.IsNullOrWhiteSpace(rawAnswer))
        {
            answer = string.Empty;
            citations = [];
            validCount = 0;
            invalidCount = 0;
        }
        else
        {
            answer = rawAnswer;
            (citations, validCount, invalidCount) = ParseAndMapCitations(answer, prep.RagContext.SourceMap);
        }

        var diagnostics = new RagDiagnostics(
            TotalLatency: prep.Stopwatch.Elapsed,
            RetrievedCandidateCount: prep.CandidatePool.Count,
            ContextSourceCount: prep.RagContext.Sources.Count,
            ContextTokenCount: prep.RagContext.Sources.Count > 0 ? prep.RagContext.TokenCount : null,
            FilteredCandidateCount: prep.FilteredCandidates.Count,
            RerankedCandidateCount: prep.RerankedCandidates.Count,
            PromptTokenCount: prep.PromptTokenCount,
            ValidCitationCount: validCount,
            InvalidCitationCount: invalidCount,
            RetrievalLatency: prep.RetrievalLatency,
            GenerationLatency: generationLatency);

        return new RagResponse(answer, citations, prep.RagContext.Sources, diagnostics);
    }

    private static (List<Citation> Citations, int ValidCount, int InvalidCount) ParseAndMapCitations(
        string answer,
        IReadOnlyDictionary<string, RetrievalResult> sourceMap)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return ([], 0, 0);
        }

        var matches = CitationRegex.Matches(answer);
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var citations = new List<Citation>();
        var invalidCount = 0;

        foreach (Match match in matches)
        {
            var tag = match.Groups[1].Value.ToUpperInvariant();

            if (sourceMap.TryGetValue(tag, out var sourceResult))
            {
                if (seenTags.Add(tag))
                {
                    var sourceIndex = int.TryParse(tag[1..], out var idx) ? idx : citations.Count + 1;
                    citations.Add(Citation.FromRetrievalResult(sourceIndex, sourceResult, tag));
                }
            }
            else if (seenTags.Add(tag))
            {
                invalidCount++;
            }
        }

        return (citations, citations.Count, invalidCount);
    }

    private sealed record PreparedExecution(
        IReadOnlyList<RetrievalResult> CandidatePool,
        IReadOnlyList<RetrievalResult> FilteredCandidates,
        IReadOnlyList<RetrievalResult> RerankedCandidates,
        RagContext RagContext,
        string Prompt,
        int PromptTokenCount,
        Stopwatch Stopwatch,
        TimeSpan RetrievalLatency);

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

        public async IAsyncEnumerable<string> GenerateStreamingAsync(
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var stream = _client.GetStreamingResponseAsync(prompt, cancellationToken: cancellationToken);
            await foreach (var update in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return update.Text;
                }
            }
        }
    }
}
