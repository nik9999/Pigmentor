using Pigmentor.App.Runtime;
using Tokenizers.DotNet;
using Xunit;

namespace Pigmentor.Tests;

public sealed class OnnxEmbeddingTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly string Model1024Path = Path.Combine(FixturesDir, "test_bge_m3_1024.onnx");
    private static readonly string Model768Path = Path.Combine(FixturesDir, "test_bge_m3_768.onnx");
    private static readonly string TokenizerPath = Path.Combine(FixturesDir, "test_tokenizer.json");

    [Fact]
    public void HuggingFaceTokenizer_TokenizesRussianEnglishAndTechnicalTokens()
    {
        using var tokenizer = new HuggingFaceOnnxTokenizer(TokenizerPath);

        // 1. Technical Russian text
        var tokensTech = tokenizer.Tokenize("Как настроить RS-485?");
        Assert.NotEmpty(tokensTech);
        Assert.Contains(4L, tokensTech); // 'RS-485' token id

        // 2. Modbus Holding Register 40001
        var tokensModbus = tokenizer.Tokenize("Какой тип у Modbus Holding Register 40001?");
        Assert.NotEmpty(tokensModbus);
        Assert.Contains(5L, tokensModbus); // 'Modbus'
        Assert.Contains(7L, tokensModbus); // 'Holding'
        Assert.Contains(8L, tokensModbus); // 'Register'
        Assert.Contains(9L, tokensModbus); // '40001'

        // 3. Technical numbers and units
        var tokensUnits = tokenizer.Tokenize("4-20 mA 115200");
        Assert.Contains(10L, tokensUnits); // '4-20'
        Assert.Contains(11L, tokensUnits); // 'mA'
        Assert.Contains(12L, tokensUnits); // '115200'
    }

    [Fact]
    public void Tokenizer_DeterminismAndInputSensitivity()
    {
        using var tokenizer = new HuggingFaceOnnxTokenizer(TokenizerPath);

        const string input1 = "Как настроить RS-485?";
        const string input2 = "Как оформить отпуск сотруднику?";

        // Determinism: same input produces identical token sequences
        var run1 = tokenizer.Tokenize(input1);
        var run2 = tokenizer.Tokenize(input1);
        Assert.Equal(run1, run2);

        // Input sensitivity: different inputs produce different token sequences
        var other = tokenizer.Tokenize(input2);
        Assert.NotEqual(run1, other);
    }

    [Fact]
    public void HuggingFaceOnnxTokenizer_FailsFastWithActionableError_WhenFileMissing()
    {
        var missingPath = Path.Combine(AppContext.BaseDirectory, "non_existent_tokenizer.json");

        var ex = Assert.Throws<FileNotFoundException>(() => new HuggingFaceOnnxTokenizer(missingPath));
        Assert.Contains(missingPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains("BGE-M3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnnxEmbeddingGenerator_RejectsMissingTokenizer_NoFakeHashFallback()
    {
        // Must reject null/whitespace tokenizer path without silently using hash fallback
        var ex = Assert.Throws<ArgumentException>(() => new OnnxEmbeddingGenerator(Model1024Path, tokenizerPath: null));
        Assert.Contains("tokenizerPath must be specified", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnnxEmbeddingGenerator_ValidatesOutputDimensionsStrictly_RejectsMismatch()
    {
        // Model outputs 768 floats, but generator is configured with 1024 floats.
        // Must fail fast with a configuration mismatch error rather than truncating or zero-padding.
        using var generator = new OnnxEmbeddingGenerator(
            modelPath: Model768Path,
            tokenizerPath: TokenizerPath,
            dimensions: 1024);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync(["Тестовый запрос для проверки размерности"]));

        Assert.Contains("Configured embedding dimensions: 1024", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ONNX model output dimensions: 768", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Configuration/model mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnnxEmbeddingGenerator_InfersNormalized1024Vectors_WithDeterministicOutputs()
    {
        using var generator = new OnnxEmbeddingGenerator(
            modelPath: Model1024Path,
            tokenizerPath: TokenizerPath,
            dimensions: 1024);

        const string input1 = "Как настроить RS-485?";
        const string input2 = "Какой тип у Modbus Holding Register 40001?";

        var results1 = await generator.GenerateAsync([input1]);
        Assert.Single(results1);
        var vec1 = results1[0].Vector.ToArray();

        // 1. Exact dimensionality
        Assert.Equal(1024, vec1.Length);

        // 2. L2 Normalization (norm should be ~1.0)
        var sumSquares = 0.0;
        foreach (var val in vec1)
        {
            sumSquares += val * val;
        }
        var norm = Math.Sqrt(sumSquares);
        Assert.True(Math.Abs(norm - 1.0) < 1e-4, $"Expected L2 norm ~1.0, got {norm}");

        // 3. Determinism
        var resultsRepeat = await generator.GenerateAsync([input1]);
        var vecRepeat = resultsRepeat[0].Vector.ToArray();
        for (var i = 0; i < 1024; i++)
        {
            Assert.Equal(vec1[i], vecRepeat[i], precision: 6);
        }

        // 4. Input sensitivity: different inputs produce distinct vectors
        var results2 = await generator.GenerateAsync([input2]);
        var vec2 = results2[0].Vector.ToArray();
        Assert.False(vec1.SequenceEqual(vec2), "Different inputs must produce distinct embedding vectors.");
    }

    [Fact]
    public void OnnxEmbeddingGenerator_Disposal_IsIdempotent()
    {
        var generator = new OnnxEmbeddingGenerator(
            modelPath: Model1024Path,
            tokenizerPath: TokenizerPath,
            dimensions: 1024);

        // First disposal
        generator.Dispose();

        // Subsequent disposal should not throw
        generator.Dispose();
    }
}

