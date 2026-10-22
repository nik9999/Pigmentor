using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Retrieval;

namespace Pigmentor.App.Runtime;

/// <summary>
/// Production cross-encoder reranker executing sequence-pair transformer models (e.g. BAAI/bge-reranker-v2-m3)
/// on CPU using ONNX Runtime. Computes calibrated relevance logits/probabilities for (query, passage) pairs
/// in parallel batches of up to <see cref="MaxPairsPerBatch"/>.
/// </summary>
public sealed class OnnxCrossEncoderReranker : IReranker, IDisposable
{
    private const long BosTokenId = 0L;
    private const long PadTokenId = 1L;
    private const long SepTokenId = 2L;

    private readonly InferenceSession _session;
    private readonly bool _disposeSession;
    private readonly IOnnxTokenizer _tokenizer;
    private readonly bool _disposeTokenizer;
    private readonly int _maxPairsPerBatch;
    private readonly int _maxSequenceLength;
    private readonly bool _applySigmoid;
    private readonly bool _enableNearDuplicateSuppression;
    private readonly double _nearDuplicateSimilarityCeiling;
    private bool _disposed;

    /// <summary>Maximum number of (query, passage) pairs processed in a single ONNX inference batch.</summary>
    public int MaxPairsPerBatch => _maxPairsPerBatch;

    /// <summary>Maximum sequence length for tokenized (query, passage) pairs.</summary>
    public int MaxSequenceLength => _maxSequenceLength;

    /// <summary>Whether scores are calibrated into [0, 1] using a numerically stable sigmoid.</summary>
    public bool ApplySigmoid => _applySigmoid;

    /// <summary>Whether cross-document near-duplicate suppression is applied before selecting top-K.</summary>
    public bool EnableNearDuplicateSuppression => _enableNearDuplicateSuppression;

    /// <summary>Cosine similarity threshold for cross-document near-duplicate chunk suppression.</summary>
    public double NearDuplicateSimilarityCeiling => _nearDuplicateSimilarityCeiling;

    /// <summary>
    /// Initializes an <see cref="OnnxCrossEncoderReranker"/> using an existing <see cref="InferenceSession"/> and <see cref="IOnnxTokenizer"/>.
    /// </summary>
    public OnnxCrossEncoderReranker(
        InferenceSession session,
        IOnnxTokenizer tokenizer,
        int maxPairsPerBatch = 16,
        int maxSequenceLength = 512,
        bool applySigmoid = true,
        bool disposeSession = false,
        bool disposeTokenizer = false,
        bool enableNearDuplicateSuppression = true,
        double nearDuplicateSimilarityCeiling = NearDuplicateDeduplicator.DefaultSimilarityCeiling)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _disposeSession = disposeSession;
        _disposeTokenizer = disposeTokenizer;
        _maxPairsPerBatch = maxPairsPerBatch > 0 ? maxPairsPerBatch : 16;
        _maxSequenceLength = maxSequenceLength > 0 ? maxSequenceLength : 512;
        _applySigmoid = applySigmoid;
        _enableNearDuplicateSuppression = enableNearDuplicateSuppression;
        _nearDuplicateSimilarityCeiling = nearDuplicateSimilarityCeiling;
    }

    /// <summary>
    /// Initializes an <see cref="OnnxCrossEncoderReranker"/> by loading the ONNX model and tokenizer from files.
    /// </summary>
    public OnnxCrossEncoderReranker(
        string modelPath,
        string? tokenizerPath = null,
        int maxPairsPerBatch = 16,
        int maxSequenceLength = 512,
        bool applySigmoid = true,
        bool enableNearDuplicateSuppression = true,
        double nearDuplicateSimilarityCeiling = NearDuplicateDeduplicator.DefaultSimilarityCeiling)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        var resolvedModelPath = Path.GetFullPath(modelPath);
        if (!File.Exists(resolvedModelPath))
        {
            throw new FileNotFoundException($"ONNX cross-encoder model file not found at '{resolvedModelPath}'.", resolvedModelPath);
        }

        var options = new SessionOptions();
        _session = new InferenceSession(resolvedModelPath, options);
        _disposeSession = true;

        var resolvedTokPath = tokenizerPath != null && !string.IsNullOrWhiteSpace(tokenizerPath)
            ? Path.GetFullPath(tokenizerPath)
            : Path.Combine(Path.GetDirectoryName(resolvedModelPath) ?? string.Empty, "tokenizer.json");

        if (!File.Exists(resolvedTokPath))
        {
            throw new FileNotFoundException(
                $"ONNX tokenizer file not found at '{resolvedTokPath}'. Ensure tokenizer.json is co-located with the model or specify tokenizerPath explicitly.",
                resolvedTokPath);
        }

        _tokenizer = new HuggingFaceOnnxTokenizer(resolvedTokPath);
        _disposeTokenizer = true;
        _maxPairsPerBatch = maxPairsPerBatch > 0 ? maxPairsPerBatch : 16;
        _maxSequenceLength = maxSequenceLength > 0 ? maxSequenceLength : 512;
        _applySigmoid = applySigmoid;
        _enableNearDuplicateSuppression = enableNearDuplicateSuppression;
        _nearDuplicateSimilarityCeiling = nearDuplicateSimilarityCeiling;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> RerankAsync(
        string query,
        IReadOnlyList<RetrievalResult> candidates,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (topK <= 0 || candidates.Count == 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (candidates.Count == 1)
        {
            return candidates;
        }

        var passageTexts = candidates.Select(c => c.Chunk.Text).ToList();
        var scores = await ScorePairsBatchedAsync(query, passageTexts, cancellationToken).ConfigureAwait(false);

        var scored = candidates
            .Select((c, i) => c with
            {
                Score = scores[i],
                RerankerScore = scores[i]
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Chunk.ChunkId, StringComparer.Ordinal)
            .ToList();

        var deduplicated = _enableNearDuplicateSuppression
            ? NearDuplicateDeduplicator.DeduplicateNearDuplicates(scored, _nearDuplicateSimilarityCeiling)
            : scored;

        return deduplicated.Take(topK).ToList();
    }

    /// <summary>
    /// Scores an arbitrary collection of passages against a query using batched ONNX cross-encoder inference.
    /// </summary>
    public async Task<float[]> ScorePairsBatchedAsync(
        string query,
        IReadOnlyList<string> passageTexts,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(passageTexts);

        if (passageTexts.Count == 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            var totalCount = passageTexts.Count;
            var scores = new float[totalCount];

            for (var offset = 0; offset < totalCount; offset += _maxPairsPerBatch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batchSize = Math.Min(_maxPairsPerBatch, totalCount - offset);
                var batchPassages = new List<string>(batchSize);
                for (var i = 0; i < batchSize; i++)
                {
                    batchPassages.Add(passageTexts[offset + i]);
                }

                var batchScores = InferBatch(query, batchPassages);
                for (var i = 0; i < batchSize; i++)
                {
                    scores[offset + i] = batchScores[i];
                }
            }

            return scores;
        }, cancellationToken).ConfigureAwait(false);
    }

    private float[] InferBatch(string query, List<string> passages)
    {
        var batchSize = passages.Count;
        var tokenizedPairs = new List<(List<long> Tokens, int QueryLength)>(batchSize);

        for (var i = 0; i < batchSize; i++)
        {
            tokenizedPairs.Add(BuildPairTokens(query, passages[i]));
        }

        var maxSeqLen = Math.Max(1, tokenizedPairs.Max(p => p.Tokens.Count));
        var inputIds = new DenseTensor<long>(new[] { batchSize, maxSeqLen });
        var attentionMask = new DenseTensor<long>(new[] { batchSize, maxSeqLen });
        var tokenTypeIds = new DenseTensor<long>(new[] { batchSize, maxSeqLen });

        for (var b = 0; b < batchSize; b++)
        {
            var (tokens, qLen) = tokenizedPairs[b];
            for (var j = 0; j < maxSeqLen; j++)
            {
                if (j < tokens.Count)
                {
                    inputIds[b, j] = tokens[j];
                    attentionMask[b, j] = 1L;
                    tokenTypeIds[b, j] = j < qLen ? 0L : 1L;
                }
                else
                {
                    inputIds[b, j] = PadTokenId;
                    attentionMask[b, j] = 0L;
                    tokenTypeIds[b, j] = 0L;
                }
            }
        }

        var inputs = new List<NamedOnnxValue>();

        foreach (var inputNode in _session.InputMetadata)
        {
            var name = inputNode.Key.ToLowerInvariant();
            var isInt32 = inputNode.Value.ElementType == typeof(int);

            if (name.Contains("mask"))
            {
                if (isInt32)
                {
                    var mask32 = new DenseTensor<int>(new[] { batchSize, maxSeqLen });
                    for (var b = 0; b < batchSize; b++)
                    {
                        for (var j = 0; j < maxSeqLen; j++)
                        {
                            mask32[b, j] = (int)attentionMask[b, j];
                        }
                    }
                    inputs.Add(NamedOnnxValue.CreateFromTensor(inputNode.Key, mask32));
                }
                else
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(inputNode.Key, attentionMask));
                }
            }
            else if (name.Contains("token_type") || name.Contains("type_ids"))
            {
                if (isInt32)
                {
                    var type32 = new DenseTensor<int>(new[] { batchSize, maxSeqLen });
                    for (var b = 0; b < batchSize; b++)
                    {
                        for (var j = 0; j < maxSeqLen; j++)
                        {
                            type32[b, j] = (int)tokenTypeIds[b, j];
                        }
                    }
                    inputs.Add(NamedOnnxValue.CreateFromTensor(inputNode.Key, type32));
                }
                else
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(inputNode.Key, tokenTypeIds));
                }
            }
            else
            {
                if (isInt32)
                {
                    var ids32 = new DenseTensor<int>(new[] { batchSize, maxSeqLen });
                    for (var b = 0; b < batchSize; b++)
                    {
                        for (var j = 0; j < maxSeqLen; j++)
                        {
                            ids32[b, j] = (int)inputIds[b, j];
                        }
                    }
                    inputs.Add(NamedOnnxValue.CreateFromTensor(inputNode.Key, ids32));
                }
                else
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(inputNode.Key, inputIds));
                }
            }
        }

        using var outputs = _session.Run(inputs);
        if (outputs.Count == 0)
        {
            throw new InvalidOperationException("ONNX cross-encoder model produced no output tensors.");
        }

        DisposableNamedOnnxValue? selectedOutput = null;
        foreach (var output in outputs)
        {
            var outName = output.Name.ToLowerInvariant();
            if (outName.Contains("logits") || outName.Contains("score") || outName.Contains("probabilities"))
            {
                selectedOutput = output;
                break;
            }
        }

        selectedOutput ??= outputs[0];

        var tensor = selectedOutput.AsTensor<float>();
        var dims = tensor.Dimensions;
        var scores = new float[batchSize];

        for (var b = 0; b < batchSize; b++)
        {
            float rawLogit;
            if (dims.Length == 1)
            {
                rawLogit = tensor[b];
            }
            else if (dims.Length == 2)
            {
                if (dims[1] == 1)
                {
                    rawLogit = tensor[b, 0];
                }
                else if (dims[1] == 2)
                {
                    rawLogit = tensor[b, 1]; // Class 1: positive relevance
                }
                else
                {
                    rawLogit = tensor[b, 0];
                }
            }
            else
            {
                rawLogit = tensor[b, 0, 0];
            }

            scores[b] = _applySigmoid ? Sigmoid(rawLogit) : rawLogit;
        }

        return scores;
    }

    private (List<long> Tokens, int QueryLength) BuildPairTokens(string query, string passage)
    {
        var qTokens = _tokenizer.Tokenize(query ?? string.Empty);
        var pTokens = _tokenizer.Tokenize(passage ?? string.Empty);

        var qStart = (qTokens.Length > 0 && qTokens[0] == BosTokenId) ? 1 : 0;
        var qEnd = (qTokens.Length > qStart && qTokens[^1] == SepTokenId) ? qTokens.Length - 1 : qTokens.Length;
        var rawQ = qTokens[qStart..qEnd];

        var pStart = (pTokens.Length > 0 && pTokens[0] == BosTokenId) ? 1 : 0;
        var pEnd = (pTokens.Length > pStart && pTokens[^1] == SepTokenId) ? pTokens.Length - 1 : pTokens.Length;
        var rawP = pTokens[pStart..pEnd];

        // XLM-RoBERTa / BGE-Reranker pair contract: <s> query </s></s> passage </s>
        // 1 (BOS) + rawQ + 2 (SEP SEP) + rawP + 1 (EOS)
        const int specialTokensOverhead = 4;
        var availableForQuery = Math.Max(1, _maxSequenceLength / 2);
        if (rawQ.Length > availableForQuery)
        {
            rawQ = rawQ[..availableForQuery];
        }

        var availableForPassage = Math.Max(0, _maxSequenceLength - rawQ.Length - specialTokensOverhead);
        if (rawP.Length > availableForPassage)
        {
            rawP = rawP[..availableForPassage];
        }

        var tokens = new List<long>(1 + rawQ.Length + 2 + rawP.Length + 1);
        tokens.Add(BosTokenId);
        tokens.AddRange(rawQ);
        tokens.Add(SepTokenId);
        tokens.Add(SepTokenId);
        var queryLength = tokens.Count;

        tokens.AddRange(rawP);
        tokens.Add(SepTokenId);

        return (tokens, queryLength);
    }

    private static float Sigmoid(float x)
    {
        if (x >= 0f)
        {
            return (float)(1.0 / (1.0 + Math.Exp(-x)));
        }
        var exp = Math.Exp(x);
        return (float)(exp / (1.0 + exp));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_disposeSession)
        {
            _session.Dispose();
        }

        if (_disposeTokenizer)
        {
            _tokenizer.Dispose();
        }

        _disposed = true;
    }
}
