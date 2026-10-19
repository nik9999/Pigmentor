using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Pigmentor.GigaChat.Extensions;
using Pigmentor.GigaChat.Model;

namespace Pigmentor.GigaChat;

/// <summary>
/// <see cref="IChatClient"/> implementation wrapping the Sber GigaChat API.
/// </summary>
public sealed class GigaChatChatClient : IChatClient
{
    private readonly GigaChatClient _client;
    private readonly string _defaultModelId;
    private readonly bool _disposeClient;
    private bool _disposed;

    public ChatClientMetadata Metadata { get; }

    public GigaChatChatClient(GigaChatClient client, string? defaultModelId = null, bool disposeClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _defaultModelId = defaultModelId ?? client.Options.DefaultModel;
        _disposeClient = disposeClient;

        Metadata = new ChatClientMetadata(
            providerName: "gigachat",
            providerUri: _client.Options.BaseUrl,
            defaultModelId: _defaultModelId);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();

        var request = await BuildRequestAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var responseDto = await _client.CompleteChatAsync(request, cancellationToken).ConfigureAwait(false);

        return GigaChatMessageConverter.ToChatResponse(responseDto);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();

        var request = await BuildRequestAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var stream = _client.CompleteChatStreamingAsync(request, cancellationToken);

        await foreach (var choice in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var delta = choice.Delta;
            if (delta == null)
            {
                continue;
            }

            var contents = new List<AIContent>();

            if (!string.IsNullOrEmpty(delta.Content))
            {
                contents.Add(new TextContent(delta.Content));
            }

            if (delta.FunctionCall != null)
            {
                IDictionary<string, object?>? arguments = null;
                if (delta.FunctionCall.Arguments.ValueKind == JsonValueKind.Object)
                {
                    arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(delta.FunctionCall.Arguments.GetRawText());
                }

                contents.Add(new FunctionCallContent(
                    callId: Guid.NewGuid().ToString("N"),
                    name: delta.FunctionCall.Name,
                    arguments: arguments));
            }

            var finishReason = GigaChatMessageConverter.ToChatFinishReason(choice.FinishReason);

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = contents,
                FinishReason = finishReason,
                ModelId = request.Model,
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey == null)
        {
            if (serviceType == typeof(ChatClientMetadata))
            {
                return Metadata;
            }

            if (serviceType.IsInstanceOfType(this))
            {
                return this;
            }

            if (serviceType.IsInstanceOfType(_client))
            {
                return _client;
            }
        }

        return null;
    }

    private async Task<GigaChatCompletionsRequest> BuildRequestAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var model = options?.ModelId ?? _defaultModelId;
        var functions = GigaChatMessageConverter.ToGigaChatFunctions(options);

        object? functionCall = null;
        if (functions != null && functions.Count > 0)
        {
            functionCall = options?.ToolMode switch
            {
                NoneChatToolMode => "none",
                RequiredChatToolMode req when !string.IsNullOrEmpty(req.RequiredFunctionName) =>
                    new { name = req.RequiredFunctionName },
                _ => "auto"
            };
        }

        var messageDtos = GigaChatMessageConverter.ToGigaChatMessages(messages);

        int msgIdx = 0;
        foreach (var message in messages)
        {
            if (msgIdx < messageDtos.Count)
            {
                var dataContents = message.Contents.OfType<DataContent>().ToList();
                if (dataContents.Count > 0)
                {
                    messageDtos[msgIdx].Attachments ??= [];
                    foreach (var data in dataContents)
                    {
                        byte[] bytes = data.Data.ToArray();
                        string mimeType = string.IsNullOrEmpty(data.MediaType) ? "image/png" : data.MediaType;
                        string ext = mimeType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || mimeType.Contains("jpg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
                        string fileName = $"img_{Guid.NewGuid():N}.{ext}";

                        string fileId = await _client.UploadFileAsync(bytes, fileName, mimeType, "general", cancellationToken).ConfigureAwait(false);
                        messageDtos[msgIdx].Attachments!.Add(fileId);
                    }
                }
            }
            msgIdx++;
        }

        return new GigaChatCompletionsRequest
        {
            Model = model,
            Messages = messageDtos,
            Temperature = (float?)options?.Temperature,
            TopP = (float?)options?.TopP,
            MaxTokens = options?.MaxOutputTokens,
            RepetitionPenalty = (float?)(options?.FrequencyPenalty ?? options?.PresencePenalty),
            Functions = functions,
            FunctionCall = functionCall
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_disposeClient)
            {
                _client.Dispose();
            }
            _disposed = true;
        }
    }
}
