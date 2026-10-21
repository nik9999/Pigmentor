using System.Text;

namespace Pigmentor.RAG.Cli.Console;

/// <summary>
/// Standard console implementation with secure NO-ECHO secret reading.
/// </summary>
public sealed class SystemConsoleIO : IConsoleIO
{
    static SystemConsoleIO()
    {
        try
        {
            System.Console.OutputEncoding = Encoding.UTF8;
            System.Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Best effort when streams are redirected
        }
    }

    public void Write(string message)
    {
        System.Console.Write(message);
    }

    public void WriteLine(string message = "")
    {
        System.Console.WriteLine(message);
    }

    public void WriteError(string message)
    {
        System.Console.Error.WriteLine(message);
    }

    public string? ReadLine()
    {
        return System.Console.ReadLine();
    }

    public string ReadSecret(string prompt = "")
    {
        if (!string.IsNullOrEmpty(prompt))
        {
            System.Console.Write(prompt);
        }

        var buffer = new StringBuilder();

        while (true)
        {
            var keyInfo = System.Console.ReadKey(intercept: true);

            // Enter key completes input
            if (keyInfo.Key == ConsoleKey.Enter)
            {
                System.Console.WriteLine();
                break;
            }

            // Ctrl+C raises cancellation
            if (keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control) && keyInfo.Key == ConsoleKey.C)
            {
                System.Console.WriteLine();
                throw new OperationCanceledException("Secret entry cancelled by user (Ctrl+C).");
            }

            // Backspace removes character from buffer
            if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Remove(buffer.Length - 1, 1);
                }
                continue;
            }

            // Ignore other control keys
            if (char.IsControl(keyInfo.KeyChar))
            {
                continue;
            }

            // Append character to buffer with NO ECHO on terminal
            buffer.Append(keyInfo.KeyChar);
        }

        return buffer.ToString();
    }

    public void Clear()
    {
        try
        {
            System.Console.Clear();
        }
        catch
        {
            // Ignore in non-interactive terminals or redirected streams
        }
    }
}

