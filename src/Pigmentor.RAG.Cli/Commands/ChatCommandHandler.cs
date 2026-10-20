using Pigmentor.App.Runtime;
using Pigmentor.Generation;
using Pigmentor.RAG.Cli.Console;

namespace Pigmentor.RAG.Cli.Commands;

public sealed class ChatCommandHandler
{
    private readonly Func<CancellationToken, Task<PigmentorAppRuntime>> _runtimeFactory;
    private readonly IConsoleIO _console;

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
        _console.WriteLine("Commands: /exit to quit, /clear to reset conversation, /help for help.");
        _console.WriteLine();

        await using var runtime = await _runtimeFactory(cancellationToken).ConfigureAwait(false);
        var askHandler = new AskCommandHandler(_runtimeFactory, _console);

        var conversationId = Guid.NewGuid().ToString("N");

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

            if (trimmed.Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                conversationId = Guid.NewGuid().ToString("N");
                _console.WriteLine("Conversation context cleared (new session started).");
                _console.WriteLine();
                continue;
            }

            if (trimmed.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                _console.WriteLine("Interactive Commands:");
                _console.WriteLine("  /clear   Start a new conversation context");
                _console.WriteLine("  /help    Show this help message");
                _console.WriteLine("  /exit    Exit interactive chat");
                _console.WriteLine();
                continue;
            }

            try
            {
                var request = new RagRequest(trimmed)
                {
                    ConversationId = conversationId
                };

                RagResponse? finalResponse = null;

                await foreach (var evt in runtime.Pipeline.AskStreamingAsync(request, cancellationToken).ConfigureAwait(false))
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
            catch (OperationCanceledException)
            {
                _console.WriteLine();
                _console.WriteLine("Turn cancelled by user.");
                // In interactive chat, user cancelling one turn with Ctrl+C returns to prompt
            }
            catch (Exception ex)
            {
                _console.WriteLine();
                _console.WriteError($"Error: {ex.Message}");
                _console.WriteLine();
            }
        }

        return 0;
    }
}

