using Pigmentor.App.Runtime;
using Pigmentor.Generation;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

public sealed class ChatCommandHandler
{
    private readonly Func<CancellationToken, Task<PigmentorAppRuntime>> _runtimeFactory;
    private readonly IConsoleIO _console;

    /// <summary>
    /// Optional hook to register turn-level cancellation interceptor (primarily for unit testing).
    /// If null, defaults to registering <see cref="Program.ConsoleCancelInterceptor"/> in live CLI.
    /// </summary>
    public Func<Action, IDisposable>? TurnCancelRegistrar { get; init; }

    public ChatCommandHandler(
        Func<CancellationToken, Task<PigmentorAppRuntime>> runtimeFactory,
        IConsoleIO console)
    {
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _console.WriteLine("Pigmentor interactive RAG");
        _console.WriteLine("Commands: /exit to quit, /help for help.");
        _console.WriteLine("Each query is answered independently against the index.");
        _console.WriteLine();

        await using var runtime = await _runtimeFactory(cancellationToken).ConfigureAwait(false);
        var askHandler = new AskCommandHandler(_runtimeFactory, _console);

        while (!cancellationToken.IsCancellationRequested)
        {
            _console.Write("> ");
            var input = _console.ReadLine();

            if (input == null)
            {
                break;
            }

            var trimmed = input.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("/quit", StringComparison.OrdinalIgnoreCase))
            {
                _console.WriteLine("Goodbye.");
                break;
            }

            if (trimmed.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                _console.WriteLine("Interactive Commands:");
                _console.WriteLine("  /help    Show this help message");
                _console.WriteLine("  /exit    Exit interactive chat (/quit also supported)");
                _console.WriteLine();
                _console.WriteLine("Note: Pigmentor v1 answers each question as an independent grounded query");
                _console.WriteLine("against the index. Multi-turn conversational memory is not supported.");
                _console.WriteLine();
                continue;
            }

            if (trimmed.Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                _console.WriteLine("Pigmentor v1 does not maintain conversational history; each turn is independent.");
                _console.WriteLine();
                continue;
            }

            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IDisposable? interceptorDisposable = null;

            if (TurnCancelRegistrar != null)
            {
                interceptorDisposable = TurnCancelRegistrar(() => turnCts.Cancel());
            }
            else
            {
                var prev = Program.ConsoleCancelInterceptor;
                Program.ConsoleCancelInterceptor = () =>
                {
                    turnCts.Cancel();
                    return true;
                };
                interceptorDisposable = new ActionDisposable(() => Program.ConsoleCancelInterceptor = prev);
            }

            try
            {
                var request = new RagRequest(trimmed);
                RagResponse? finalResponse = null;

                await foreach (var evt in runtime.Pipeline.AskStreamingAsync(request, turnCts.Token).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case RagTextDelta textDelta:
                            _console.Write(textDelta.Text);
                            break;

                        case RagCompleted completed:
                            finalResponse = completed.Response;
                            break;
                    }
                }

                _console.WriteLine();

                if (finalResponse != null)
                {
                    askHandler.PrintSourcesAndCitations(finalResponse);
                }

                _console.WriteLine();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Only active turn was cancelled (e.g. Ctrl+C during generation)
                _console.WriteLine();
                _console.WriteLine("Turn cancelled by user.");
                _console.WriteLine();
            }
            catch (Exception ex)
            {
                _console.WriteLine();
                _console.WriteError($"Error: {ex.Message}");
                _console.WriteLine();
            }
            finally
            {
                interceptorDisposable?.Dispose();
            }
        }

        return 0;
    }

    private sealed class ActionDisposable : IDisposable
    {
        private Action? _action;

        public ActionDisposable(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _action, null)?.Invoke();
        }
    }
}
