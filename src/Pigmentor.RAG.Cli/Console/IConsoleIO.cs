namespace Pigmentor.RAG.Cli.Console;

/// <summary>
/// Abstraction over console input/output for testability and secure no-echo secret handling.
/// </summary>
public interface IConsoleIO
{
    void Write(string message);
    void WriteLine(string message = "");
    void WriteError(string message);
    string? ReadLine();
    string ReadSecret(string prompt = "");
    void Clear();
}

