using Tokenizers.DotNet;

namespace Pigmentor.App.Runtime;

/// <summary>
/// Production tokenizer implementation backed by HuggingFace Tokenizers.DotNet.
/// Fully compatible with BGE-M3 and SentencePiece / Hugging Face tokenizer.json artifacts.
/// </summary>
public sealed class HuggingFaceOnnxTokenizer : IOnnxTokenizer
{
    private readonly Tokenizer _tokenizer;
    private bool _disposed;

    public HuggingFaceOnnxTokenizer(string tokenizerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerPath);

        var resolvedPath = Path.GetFullPath(tokenizerPath);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException(
                $"Unable to load ONNX tokenizer from:\n{resolvedPath}\n\nThe tokenizer must be compatible with the configured BGE-M3 ONNX model.",
                resolvedPath);
        }

        try
        {
            _tokenizer = new Tokenizer(resolvedPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Unable to load ONNX tokenizer from:\n{resolvedPath}\n\nThe tokenizer must be compatible with the configured BGE-M3 ONNX model.\nDetails: {ex.Message}",
                ex);
        }
    }

    public long[] Tokenize(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var rawTokens = _tokenizer.Encode(text ?? string.Empty);
        var result = new long[rawTokens.Length];
        for (var i = 0; i < rawTokens.Length; i++)
        {
            result[i] = rawTokens[i];
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _tokenizer.Dispose();
        _disposed = true;
    }
}

