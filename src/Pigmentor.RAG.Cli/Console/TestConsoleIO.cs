using System.Text;

namespace Pigmentor.RAG.Cli.Console;

/// <summary>
/// In-memory test console for verifying CLI command output, error streams, and simulating input.
/// </summary>
public sealed class TestConsoleIO : IConsoleIO
{
    private readonly StringBuilder _output = new();
    private readonly StringBuilder _error = new();
    private readonly Queue<string> _inputs = new();
    private readonly Queue<string> _secretInputs = new();

    public string Output => _output.ToString();
    public string Error => _error.ToString();

    public void EnqueueInput(string input) => _inputs.Enqueue(input);
    public void EnqueueSecretInput(string secret) => _secretInputs.Enqueue(secret);

    public void Write(string message)
    {
        _output.Append(message);
    }

    public void WriteLine(string message = "")
    {
        _output.AppendLine(message);
    }

    public void WriteError(string message)
    {
        _error.AppendLine(message);
    }

    public string? ReadLine()
    {
        return _inputs.Count > 0 ? _inputs.Dequeue() : null;
    }

    public string ReadSecret(string prompt = "")
    {
        if (!string.IsNullOrEmpty(prompt))
        {
            _output.Append(prompt);
        }

        if (_secretInputs.Count > 0)
        {
            return _secretInputs.Dequeue();
        }

        return string.Empty;
    }

    public void Clear()
    {
        _output.AppendLine("[CLEAR]");
    }
}

