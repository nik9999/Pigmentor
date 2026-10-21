namespace Pigmentor.App.Runtime;

/// <summary>
/// Abstraction for tokenizers converting text into token sequences for ONNX models.
/// </summary>
public interface IOnnxTokenizer : IDisposable
{
    /// <summary>
    /// Encodes the given input text into an array of token IDs.
    /// </summary>
    long[] Tokenize(string text);
}

