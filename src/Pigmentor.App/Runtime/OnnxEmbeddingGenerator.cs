using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Pigmentor.App.Runtime;

/// <summary>
/// Local ONNX embedding generator executing transformer models (specifically BGE-M3 dense embeddings) on CPU.
/// Implements Microsoft.Extensions.AI <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>.
/// Tokenization is strictly performed using real Hugging Face tokenizers (no fake hash-tokenizers).
/// Dense embeddings use CLS pooling ([0, 0, :]) with L2 normalization producing exact 1024-dimensional vectors.
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>, IDisposable
{
    private readonly string _modelPath;
    private readonly int _dimensions;
    private readonly InferenceSession _session;
    private readonly bool _disposeSession;
    private readonly IOnnxTokenizer _tokenizer;
    private readonly bool _disposeTokenizer;
    private bool _disposed;

    public EmbeddingGeneratorMetadata Metadata { get; }

    public OnnxEmbeddingGenerator(
        string modelPath,
        string? tokenizerPath = null,
        int dimensions = 1024,
        InferenceSession? sessionOverride = null,
        IOnnxTokenizer? tokenizerOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);

        _modelPath = Path.GetFullPath(modelPath);
        _dimensions = dimensions;

        if (sessionOverride != null)
        {
            _session = sessionOverride;
            _disposeSession = false;
        }
        else
        {
            if (!File.Exists(_modelPath))
            {
                throw new FileNotFoundException($"ONNX model file not found at '{_modelPath}'.", _modelPath);
            }

            var options = new SessionOptions();
            _session = new InferenceSession(_modelPath, options);
            _disposeSession = true;
        }

        if (tokenizerOverride != null)
        {
            _tokenizer = tokenizerOverride;
            _disposeTokenizer = false;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(tokenizerPath))
            {
                throw new ArgumentException(
                    "ONNX tokenizerPath must be specified when using OnnxEmbeddingGenerator. Fake/hash tokenizers are not permitted.",
                    nameof(tokenizerPath));
            }

            _tokenizer = new HuggingFaceOnnxTokenizer(tokenizerPath);
            _disposeTokenizer = true;
        }

        Metadata = new EmbeddingGeneratorMetadata("onnx", null, Path.GetFileNameWithoutExtension(_modelPath), dimensions);
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();

        var valueList = values.ToList();
        var results = new List<Embedding<float>>(valueList.Count);

        if (valueList.Count == 0)
        {
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(results));
        }

        foreach (var text in valueList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vector = InferSingleText(text);
            results.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(results));
    }

    private ReadOnlyMemory<float> InferSingleText(string text)
    {
        var tokens = _tokenizer.Tokenize(text);
        var seqLength = Math.Max(tokens.Length, 1);

        var inputIds = new DenseTensor<long>(new[] { 1, seqLength });
        var attentionMask = new DenseTensor<long>(new[] { 1, seqLength });

        for (var i = 0; i < seqLength; i++)
        {
            inputIds[0, i] = i < tokens.Length ? tokens[i] : 0L;
            attentionMask[0, i] = 1L;
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
                    var mask32 = new DenseTensor<int>(new[] { 1, seqLength });
                    for (var i = 0; i < seqLength; i++)
                    {
                        mask32[0, i] = 1;
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
                    var type32 = new DenseTensor<int>(new[] { 1, seqLength });
                    inputs.Add(NamedOnnxValue.CreateFromTensor(inputNode.Key, type32));
                }
                else
                {
                    var type64 = new DenseTensor<long>(new[] { 1, seqLength });
                    inputs.Add(NamedOnnxValue.CreateFromTensor(inputNode.Key, type64));
                }
            }
            else
            {
                if (isInt32)
                {
                    var ids32 = new DenseTensor<int>(new[] { 1, seqLength });
                    for (var i = 0; i < seqLength; i++)
                    {
                        ids32[0, i] = (int)inputIds[0, i];
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
            throw new InvalidOperationException("ONNX model produced no output tensors.");
        }

        // Identify primary output: prefer dense_vecs / sentence_embedding if exported, otherwise first tensor
        DisposableNamedOnnxValue? selectedOutput = null;
        foreach (var output in outputs)
        {
            var outName = output.Name.ToLowerInvariant();
            if (outName.Contains("sentence_embedding") || outName.Contains("dense_vecs"))
            {
                selectedOutput = output;
                break;
            }
        }

        selectedOutput ??= outputs[0];

        var vector = ExtractAndPoolVector(selectedOutput);
        NormalizeL2(vector);

        return vector;
    }

    private float[] ExtractAndPoolVector(DisposableNamedOnnxValue outputValue)
    {
        var tensor = outputValue.AsTensor<float>();
        var dims = tensor.Dimensions;
        var vector = new float[_dimensions];

        if (dims.Length == 2)
        {
            // Shape: [batch, dimensions]
            var outputDim = dims[1];
            if (outputDim != _dimensions)
            {
                throw new InvalidOperationException(
                    $"Configured embedding dimensions: {_dimensions}\nONNX model output dimensions: {outputDim}\nConfiguration/model mismatch.");
            }

            for (var d = 0; d < _dimensions; d++)
            {
                vector[d] = tensor[0, d];
            }
        }
        else if (dims.Length == 3)
        {
            // Shape: [batch, seq_len, dimensions]
            var outputDim = dims[2];
            if (outputDim != _dimensions)
            {
                throw new InvalidOperationException(
                    $"Configured embedding dimensions: {_dimensions}\nONNX model output dimensions: {outputDim}\nConfiguration/model mismatch.");
            }

            // BGE-M3 Dense Embedding Contract: CLS token pooling (first token [0, 0, :])
            for (var d = 0; d < _dimensions; d++)
            {
                vector[d] = tensor[0, 0, d];
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported ONNX model output rank: {dims.Length}. Expected 2D [batch, dim] or 3D [batch, seq_len, dim].");
        }

        return vector;
    }

    private static void NormalizeL2(float[] vector)
    {
        var sumSquares = 0.0;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSquares += vector[i] * vector[i];
        }

        var norm = (float)Math.Sqrt(sumSquares);
        if (norm > 1e-12f)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return Metadata;
        }

        return null;
    }

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
