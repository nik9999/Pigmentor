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
    private static readonly Regex BracketRegex = new(@"\[([^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"\b([sс]\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StandaloneCitationLineRegex = new(
        @"(?m)^[\t ]*(?:\[S\d+\][\t ]*)+\r?(?:\n|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // GOST designations (e.g. ПИМФ.426439.004, АБВГ.123456.789-01),
    // compact alphanumeric models (e.g. МВА8, MVA8),
    // hyphenated alphanumeric models (e.g. PX-400, ПX-400, TM-200, AI-8UI/D),
    // and protocol identifiers (e.g. RS-485, MODBUS-RTU).
    private static readonly Regex IdentifierRegex = new(
        @"\b(?:[A-Za-z\u0400-\u04FF]{2,6}\.[A-Za-z0-9\u0400-\u04FF][A-Za-z0-9\u0400-\u04FF./\-]*" +
        @"|[A-Za-z]{2,}[A-Za-z0-9]*-[A-Za-z0-9/]+" +
        @"|[A-Za-z\u0400-\u04FF]{2,}[A-Za-z0-9\u0400-\u04FF]*-[A-Za-z0-9\u0400-\u04FF/]*[0-9][A-Za-z0-9\u0400-\u04FF/]*" +
        @"|[A-Za-z\u0400-\u04FF]{2,6}[0-9]+[A-Za-z0-9\u0400-\u04FF/]*)\b",
        RegexOptions.Compiled);

    private readonly IRetriever _retriever;
    private readonly IChatGenerator _chat;
    private readonly IReranker _reranker;
    private readonly IContextBuilder _contextBuilder;
    private readonly ITokenCounter _tokenCounter;
    private readonly RagPipelineSettings _settings;
    private readonly RrfMerger _rrfMerger;

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
        RagPipelineSettings? settings = null,
        RrfMerger? rrfMerger = null)
        : this(retriever, new ChatClientGenerator(chatClient), reranker, contextBuilder, tokenCounter, settings, rrfMerger)
    {
    }

    public RagPipeline(
        IRetriever retriever,
        IChatClient chatClient,
        IOptions<RagPipelineSettings> settingsOptions,
        IReranker? reranker = null,
        IContextBuilder? contextBuilder = null,
        ITokenCounter? tokenCounter = null,
        RrfMerger? rrfMerger = null)
        : this(retriever, new ChatClientGenerator(chatClient), reranker, contextBuilder, tokenCounter, settingsOptions?.Value, rrfMerger)
    {
    }

    [ActivatorUtilitiesConstructor]
    public RagPipeline(
        IRetriever retriever,
        IChatGenerator chat,
        IOptions<RagPipelineSettings> settingsOptions,
        IReranker? reranker = null,
        IContextBuilder? contextBuilder = null,
        ITokenCounter? tokenCounter = null,
        RrfMerger? rrfMerger = null)
        : this(retriever, chat, reranker, contextBuilder, tokenCounter, settingsOptions?.Value, rrfMerger)
    {
    }

    public RagPipeline(
        IRetriever retriever,
        IChatGenerator chat,
        IReranker? reranker = null,
        IContextBuilder? contextBuilder = null,
        ITokenCounter? tokenCounter = null,
        RagPipelineSettings? settings = null,
        RrfMerger? rrfMerger = null)
    {
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _reranker = reranker ?? NoOpReranker.Instance;
        _settings = settings ?? new RagPipelineSettings();
        _tokenCounter = tokenCounter ?? new TokenCounter();
        _contextBuilder = contextBuilder ?? new ContextBuilder(_tokenCounter, _settings.ContextOptions with { Budget = _settings.Budget });
        _rrfMerger = rrfMerger ?? new RrfMerger(k: 60);
    }

    /// <inheritdoc />
    public async Task<RagResponse> AskAsync(
        RagRequest request,
        CancellationToken cancellationToken = default)
    {
        var prep = await PrepareExecutionAsync(request, cancellationToken).ConfigureAwait(false);

        if (TableQueryExecutor.TryExecute(request.Question, prep.RagContext, out var deterministicAnswer))
        {
            prep.Stopwatch.Stop();
            var deterministicResponse = FinalizeResponse(deterministicAnswer, prep, TimeSpan.Zero);
            if (deterministicResponse.Diagnostics.RequiredCitationsSatisfied)
            {
                return deterministicResponse;
            }
        }

#if DEBUG
        Console.Error.WriteLine("========== RAG DEBUG PROMPT ==========");
        Console.Error.WriteLine(prep.Prompt);
        Console.Error.WriteLine("========== END RAG DEBUG PROMPT ======");
#endif

        cancellationToken.ThrowIfCancellationRequested();
        var genSw = Stopwatch.StartNew();
        var rawAnswer = await _chat.GenerateAsync(prep.Prompt, cancellationToken).ConfigureAwait(false);
        genSw.Stop();
        prep.Stopwatch.Stop();

        var firstResponse = FinalizeResponse(rawAnswer, prep, genSw.Elapsed);

        string? firstRejectionReason = null;
        if (!firstResponse.Diagnostics.RequiredCitationsSatisfied)
        {
            if (firstResponse.Diagnostics.ValidCitationCount == 0)
            {
                firstRejectionReason = "Отсутствуют обязательные ссылки [S#] на источник с данными";
            }
            else if (firstResponse.Diagnostics.InvalidCitationCount > 0)
            {
                firstRejectionReason = $"Обнаружены недействительные ссылки [S#] ({firstResponse.Diagnostics.InvalidCitationCount})";
            }
            else
            {
                var (_, reason) = EvidenceValidator.ValidateEvidenceWithReason(rawAnswer, firstResponse.Citations, request.Question);
                firstRejectionReason = reason;
            }
        }

#if DEBUG
        Console.Error.WriteLine("========== [DEBUG: FIRST GENERATION] ==========");
        Console.Error.WriteLine($"Raw Answer:\n{rawAnswer}");
        Console.Error.WriteLine($"Valid Citations Count: {firstResponse.Diagnostics.ValidCitationCount}");
        Console.Error.WriteLine($"Invalid Citations Count: {firstResponse.Diagnostics.InvalidCitationCount}");
        Console.Error.WriteLine($"Detected Citations: [{string.Join(", ", firstResponse.Citations.Select(c => $"[S{c.SourceIndex}: {c.DocumentId}]"))}]");
        Console.Error.WriteLine($"RequiredCitationsSatisfied: {firstResponse.Diagnostics.RequiredCitationsSatisfied}");
        var (isFirstValid, debugFirstReason) = EvidenceValidator.DiagnoseEvidence(rawAnswer, firstResponse.Citations, request.Question);
        Console.Error.WriteLine($"EvidenceValidator Result: {isFirstValid} (Reason: {debugFirstReason ?? "None"})");
        Console.Error.WriteLine("================================================");
#endif

        var shouldRetry = prep.RagContext.Sources.Count > 0 &&
            !firstResponse.Diagnostics.HasInternalMarkerLeakage &&
            !string.IsNullOrWhiteSpace(rawAnswer) &&
            !SignalsInsufficientContext(firstResponse.Answer) &&
            !firstResponse.Diagnostics.RequiredCitationsSatisfied;

#if DEBUG
        if (shouldRetry)
        {
            Console.Error.WriteLine("========== [DEBUG: RETRY TRIGGERED] ==========");
            Console.Error.WriteLine($"Retry Reason: {firstRejectionReason ?? "unknown"}");
        }
        else
        {
            Console.Error.WriteLine("========== [DEBUG: RETRY NOT TRIGGERED] ==========");
            Console.Error.WriteLine("[DEBUG: PIPELINE DECISION]: Accepted first response.");
        }
#endif

        // Bounded retry if citations were required but violated (§5)
        if (shouldRetry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var retryPrompt = RagPromptBuilder.BuildRetryPrompt(request.Question, prep.RagContext, rawAnswer, firstRejectionReason);
#if DEBUG
            Console.Error.WriteLine("========== RAG DEBUG RETRY PROMPT ==========");
            Console.Error.WriteLine(retryPrompt);
            Console.Error.WriteLine("========== END RAG DEBUG RETRY PROMPT ======");
#endif
            var retrySw = Stopwatch.StartNew();
            var retryAnswer = await _chat.GenerateAsync(retryPrompt, cancellationToken).ConfigureAwait(false);
            retrySw.Stop();

            var retryResponse = FinalizeResponse(retryAnswer, prep, genSw.Elapsed + retrySw.Elapsed);
#if DEBUG
            Console.Error.WriteLine("========== [DEBUG: RETRY GENERATION] ==========");
            Console.Error.WriteLine($"Raw Retry Answer:\n{retryAnswer}");
            Console.Error.WriteLine($"Retry Valid Citations Count: {retryResponse.Diagnostics.ValidCitationCount}");
            Console.Error.WriteLine($"Retry Invalid Citations Count: {retryResponse.Diagnostics.InvalidCitationCount}");
            Console.Error.WriteLine($"Retry Citations: [{string.Join(", ", retryResponse.Citations.Select(c => $"[S{c.SourceIndex}: {c.DocumentId}]"))}]");
            Console.Error.WriteLine($"Retry RequiredCitationsSatisfied: {retryResponse.Diagnostics.RequiredCitationsSatisfied}");
            var (isRetryValid, retryRejectionReason) = EvidenceValidator.DiagnoseEvidence(retryAnswer, retryResponse.Citations, request.Question);
            Console.Error.WriteLine($"Retry EvidenceValidator Result: {isRetryValid} (Reason: {retryRejectionReason ?? "None"})");
            Console.Error.WriteLine("===============================================");
#endif
            if (retryResponse.Diagnostics.RequiredCitationsSatisfied)
            {
#if DEBUG
                Console.Error.WriteLine("[DEBUG: PIPELINE DECISION]: Accepted retry response.");
#endif
                return retryResponse;
            }

#if DEBUG
            Console.Error.WriteLine("[DEBUG: PIPELINE DECISION]: Safe failure (retry citations/evidence unsatisfied).");
#endif
            return new RagResponse(
                "Не удалось подтвердить факты в ответе обязательными ссылками на источники контекста.",
                retryResponse.Citations,
                prep.RagContext.Sources,
                retryResponse.Diagnostics with { RequiredCitationsSatisfied = false });
        }

        return firstResponse;
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

        if (TableQueryExecutor.TryExecute(request.Question, prep.RagContext, out var deterministicAnswer))
        {
            prep.Stopwatch.Stop();
            var deterministicResponse = FinalizeResponse(deterministicAnswer, prep, TimeSpan.Zero);
            if (deterministicResponse.Diagnostics.RequiredCitationsSatisfied)
            {
                yield return new RagTextDelta(deterministicResponse.Answer);
                yield return new RagCompleted(deterministicResponse);
                yield break;
            }
        }

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

        // 1. Candidate retrieval (with explicit query scope resolution if no filter was passed)
        var retrievalSw = Stopwatch.StartNew();
        var inferredFilter = request.MetadataFilter is null ? QueryScopeResolver.ResolveScope(request.Question) : null;
        var effectiveFilter = request.MetadataFilter ?? inferredFilter;
        var retrievalRequest = new RetrievalRequest(request.Question, topK, effectiveFilter);
        var candidatePool = await _retriever.RetrieveAsync(retrievalRequest, cancellationToken).ConfigureAwait(false);

        // Fallback: if an inferred scope yielded too few candidates (< topK / 2),
        // remove the inferred filter and repeat retrieval across the full corpus to avoid false-positive silo lock-in.
        if (request.MetadataFilter is null && inferredFilter is not null && candidatePool.Count < Math.Max(1, topK / 2))
        {
            var fallbackPool = await _retriever.RetrieveAsync(new RetrievalRequest(request.Question, topK, null), cancellationToken).ConfigureAwait(false);
            if (fallbackPool.Count > candidatePool.Count)
            {
                candidatePool = fallbackPool;
                effectiveFilter = null; // Clear effectiveFilter so post-filtering doesn't discard fallback candidates
            }
        }

        // Multi-source entity retrieval (§3): if the query mentions specific model identifiers, retrieve targeted candidates
        var identifiers = ExtractEntityIdentifiers(request.Question);
        if (identifiers.Count > 0)
        {
            var entityLists = new List<IReadOnlyList<RetrievalResult>>();
            foreach (var identifier in identifiers)
            {
                var entityReq = new RetrievalRequest(identifier, topK, effectiveFilter);
                var entityRes = await _retriever.RetrieveAsync(entityReq, cancellationToken).ConfigureAwait(false);
                if (entityRes.Count > 0)
                {
                    entityLists.Add(entityRes);
                }
            }

            if (entityLists.Count > 0)
            {
                candidatePool = MergeEntityCandidates(candidatePool, entityLists, topK);
            }
        }

        retrievalSw.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Defensive post-filtering (guarantees adherence even if retrieval backend had loose semantics)
        IReadOnlyList<RetrievalResult> filteredCandidates = effectiveFilter is not null
            ? candidatePool.Where(s => effectiveFilter.Matches(s.Chunk.Metadata)).ToList()
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
            Question: request.Question,
            CandidatePool: candidatePool,
            FilteredCandidates: filteredCandidates,
            RerankedCandidates: rerankedCandidates,
            RagContext: ragContext,
            Prompt: prompt,
            PromptTokenCount: promptTokenCount,
            Stopwatch: stopwatch,
            RetrievalLatency: retrievalSw.Elapsed);
    }

    private static readonly string[] InternalDelimiters =
    [
        "КОНЕЦ КОНТЕКСТА",
        "КОНЕЦ КОНТЕКСТ",
        "КОНТЕКСТ:",
        "ИНСТРУКЦИИ:",
        "ВОПРОС:"
    ];

    private static bool ContainsInternalDelimiters(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var marker in InternalDelimiters)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool SignalsInsufficientContext(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var lower = answer.ToLowerInvariant();
        return lower.Contains("недостаточно", StringComparison.Ordinal)
            || lower.Contains("отсутствует", StringComparison.Ordinal)
            || lower.Contains("отсутствуют", StringComparison.Ordinal)
            || lower.Contains("не найден", StringComparison.Ordinal)
            || lower.Contains("не указан", StringComparison.Ordinal)
            || lower.Contains("не указана", StringComparison.Ordinal)
            || lower.Contains("не указаны", StringComparison.Ordinal)
            || lower.Contains("нет информации", StringComparison.Ordinal)
            || lower.Contains("нет сведений", StringComparison.Ordinal)
            || lower.Contains("не содержится", StringComparison.Ordinal)
            || lower.Contains("cannot answer", StringComparison.Ordinal)
            || lower.Contains("insufficient", StringComparison.Ordinal);
    }

    private static RagResponse FinalizeResponse(
        string? rawAnswer,
        PreparedExecution prep,
        TimeSpan generationLatency)
    {
        if (!string.IsNullOrWhiteSpace(rawAnswer) && ContainsInternalDelimiters(rawAnswer))
        {
            var leakDiag = new RagDiagnostics(
                TotalLatency: prep.Stopwatch.Elapsed,
                RetrievedCandidateCount: prep.CandidatePool.Count,
                ContextSourceCount: prep.RagContext.Sources.Count,
                ContextTokenCount: prep.RagContext.Sources.Count > 0 ? prep.RagContext.TokenCount : null,
                FilteredCandidateCount: prep.FilteredCandidates.Count,
                RerankedCandidateCount: prep.RerankedCandidates.Count,
                PromptTokenCount: prep.PromptTokenCount,
                ValidCitationCount: 0,
                InvalidCitationCount: 0,
                RetrievalLatency: prep.RetrievalLatency,
                GenerationLatency: generationLatency,
                RequiredCitationsSatisfied: false,
                HasInternalMarkerLeakage: true);

            return new RagResponse(
                "Ответ отклонен: обнаружена утечка внутренних служебных маркеров контекста.",
                [],
                prep.RagContext.Sources,
                leakDiag);
        }

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
            answer = NormalizeCitationTags(rawAnswer);
            (citations, validCount, invalidCount) = ParseAndMapCitations(answer, prep.RagContext.SourceMap);
        }

        var signalsInsufficientContext = SignalsInsufficientContext(answer);
        var falseInsufficientContext = signalsInsufficientContext &&
            EvidenceValidator.HasDirectAnswerEvidence(
                prep.Question,
                prep.RagContext.SourceMap.Values.Select(source => source.Chunk.Text));

        bool requiredCitationsSatisfied;
        if (prep.RagContext.Sources.Count == 0 || string.IsNullOrWhiteSpace(answer))
        {
            requiredCitationsSatisfied = true;
        }
        else if (signalsInsufficientContext)
        {
            requiredCitationsSatisfied = !falseInsufficientContext;
        }
        else
        {
            var hasValidCitations = validCount > 0 && invalidCount == 0;
            var isGrounded = hasValidCitations && EvidenceValidator.ValidateEvidence(answer, citations, prep.Question);
            requiredCitationsSatisfied = isGrounded;
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
            GenerationLatency: generationLatency,
            RequiredCitationsSatisfied: requiredCitationsSatisfied,
            HasInternalMarkerLeakage: false);

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

        var bracketMatches = BracketRegex.Matches(answer);
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var citations = new List<Citation>();
        var invalidCount = 0;

        foreach (Match bm in bracketMatches)
        {
            var inner = bm.Groups[1].Value;
            var tagMatches = TagRegex.Matches(inner);
            foreach (Match tm in tagMatches)
            {
                var rawTag = tm.Groups[1].Value;
                var tag = "S" + rawTag[1..];
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
        }

        return (citations, citations.Count, invalidCount);
    }

    private static string NormalizeCitationTags(string answer)
    {
        var canonical = BracketRegex.Replace(answer, bracketMatch =>
        {
            var inner = bracketMatch.Groups[1].Value;
            var normalizedInner = TagRegex.Replace(inner, tagMatch =>
            {
                var rawTag = tagMatch.Groups[1].Value;
                return "S" + rawTag[1..];
            });
            return "[" + normalizedInner + "]";
        });

        return StandaloneCitationLineRegex.Replace(canonical, lineMatch =>
        {
            var prefix = canonical[..lineMatch.Index];
            var lineTags = TagRegex.Matches(lineMatch.Value)
                .Select(match => "S" + match.Groups[1].Value[1..])
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return lineTags.All(tag => prefix.Contains($"[{tag}]", StringComparison.OrdinalIgnoreCase))
                ? string.Empty
                : lineMatch.Value;
        }).TrimEnd();
    }

    private List<string> ExtractEntityIdentifiers(string text)
    {
        return ExtractEntityIdentifiers(text, _settings.ExcludedEntityProtocols);
    }

    internal static List<string> ExtractEntityIdentifiers(string text, ISet<string>? excludedProtocols)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }

        var matches = IdentifierRegex.Matches(text);
        foreach (Match m in matches)
        {
            var value = m.Value;
            var isExcluded = excludedProtocols != null && excludedProtocols.Contains(value);
            if (!isExcluded && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(value);
            }
        }

        return list;
    }

    internal IReadOnlyList<RetrievalResult> MergeEntityCandidates(
        IReadOnlyList<RetrievalResult> primary,
        IReadOnlyList<IReadOnlyList<RetrievalResult>> entityLists,
        int maxCount)
    {
        if (entityLists.Count == 0)
        {
            return primary;
        }

        var lists = new List<IReadOnlyList<RetrievalResult>>(entityLists.Count + 1)
        {
            primary
        };
        lists.AddRange(entityLists);

        return _rrfMerger.Merge(lists, maxCount);
    }

    internal IReadOnlyList<RetrievalResult> MergeEntityCandidates(
        IReadOnlyList<RetrievalResult> primary,
        IReadOnlyList<RetrievalResult> secondary,
        int maxCount)
    {
        if (secondary.Count == 0)
        {
            return primary;
        }

        return _rrfMerger.Merge(primary, secondary, maxCount);
    }

    internal IReadOnlyList<RetrievalResult> MergeEntityCandidates(
        IReadOnlyList<RetrievalResult> primary,
        IReadOnlyList<RetrievalResult> secondary,
        List<string>? entityKeywords,
        int maxCount) => MergeEntityCandidates(primary, secondary, maxCount);

    private sealed record PreparedExecution(
        string Question,
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
