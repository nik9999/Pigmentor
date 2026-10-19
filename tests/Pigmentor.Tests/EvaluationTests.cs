using Microsoft.Extensions.AI;
using Pigmentor.Evaluation;

namespace Pigmentor.Tests;

public sealed class EvaluationTests
{
    [Fact]
    public async Task EmptyAnswerIsNotConsideredFaithful()
    {
        var evaluator = new GenerationEvaluator(new FixedChatClient("SUPPORTED"));

        var assessment = await evaluator.AssessAsync("question", "", []);

        Assert.Equal(0d, assessment.FaithfulnessScore);
        Assert.Equal(0, assessment.SupportedClaims);
    }

    private sealed class FixedChatClient(string response) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("evaluation-test");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
