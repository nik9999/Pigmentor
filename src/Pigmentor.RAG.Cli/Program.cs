using Pigmentor.App.Security;
using Pigmentor.RAG.Cli.Commands;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli;

public static class Program
{
    /// <summary>
    /// When non-null, interceptor is invoked upon Ctrl+C. If it returns true, the keypress
    /// is treated as handled (e.g., cancelling an active turn in chat) and does not cancel the global process token.
    /// </summary>
    public static Func<bool>? ConsoleCancelInterceptor { get; set; }

    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();

        System.Console.CancelKeyPress += (sender, eventArgs) =>
        {
            // Do not terminate process immediately
            eventArgs.Cancel = true;

            if (ConsoleCancelInterceptor != null && ConsoleCancelInterceptor())
            {
                // Intercepted by active turn handler
                return;
            }

            cts.Cancel();
        };

        var console = new SystemConsoleIO();

        try
        {
            var router = CliRouter.CreateDefault(console);
            return await router.RouteAsync(args, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            console.WriteLine();
            console.WriteLine("Operation cancelled.");
            return 130; // Standard SIGINT exit code
        }
        catch (Exception ex)
        {
            var redactedMessage = SecretRedactor.Redact(ex.Message);
            console.WriteError($"Fatal Error: {redactedMessage}");

            if (args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase)))
            {
                var redactedStackTrace = SecretRedactor.Redact(ex.ToString());
                console.WriteError(redactedStackTrace);
            }

            return 1;
        }
    }
}

