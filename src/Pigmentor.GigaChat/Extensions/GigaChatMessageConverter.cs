using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pigmentor.GigaChat.Model;

namespace Pigmentor.GigaChat.Extensions;

/// <summary>
/// Converts between Microsoft.Extensions.AI abstraction models and Sber GigaChat REST DTOs.
/// </summary>
public static class GigaChatMessageConverter
{
    public static List<GigaChatMessageDto> ToGigaChatMessages(IEnumerable<ChatMessage> chatMessages)
    {
        ArgumentNullException.ThrowIfNull(chatMessages);

        var result = new List<GigaChatMessageDto>();

        foreach (var message in chatMessages)
        {
            var dto = new GigaChatMessageDto();

            // 1. Tool result message (role = "function" in GigaChat)
            var toolResult = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
            if (toolResult != null || message.Role == ChatRole.Tool)
            {
                dto.Role = "function";
                dto.Name = toolResult?.CallId;
                if (toolResult?.Result is string strResult)
                {
                    dto.Content = strResult;
                }
                else if (toolResult?.Result != null)
                {
                    dto.Content = JsonSerializer.Serialize(toolResult.Result);
                }
                else
                {
                    dto.Content = message.Text;
                }

                result.Add(dto);
                continue;
            }

            // 2. Standard roles mapping
            dto.Role = message.Role switch
            {
                var r when r == ChatRole.System => "system",
                var r when r == ChatRole.User => "user",
                var r when r == ChatRole.Assistant => "assistant",
                _ => message.Role.Value.ToLowerInvariant()
            };

            // 3. Extract text content
            var textParts = message.Contents.OfType<TextContent>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t));
            var combinedText = string.Join("\n", textParts);
            if (!string.IsNullOrEmpty(combinedText))
            {
                dto.Content = combinedText;
            }
            else if (!string.IsNullOrEmpty(message.Text))
            {
                dto.Content = message.Text;
            }

            // 4. Extract function call (if assistant requested tool execution)
            var funcCall = message.Contents.OfType<FunctionCallContent>().FirstOrDefault();
            if (funcCall != null)
            {
                var argsElement = funcCall.Arguments != null
                    ? JsonSerializer.SerializeToElement(funcCall.Arguments)
                    : JsonSerializer.SerializeToElement(new Dictionary<string, object?>());

                dto.FunctionCall = new GigaChatFunctionCallDto
                {
                    Name = funcCall.Name,
                    Arguments = argsElement
                };
            }

            result.Add(dto);
        }

        return result;
    }

    public static List<GigaChatFunctionDto>? ToGigaChatFunctions(ChatOptions? options)
    {
        if (options?.Tools == null || options.Tools.Count == 0)
        {
            return null;
        }

        var functions = new List<GigaChatFunctionDto>();

        foreach (var tool in options.Tools)
        {
            if (tool is AIFunction aiFunction)
            {
                functions.Add(new GigaChatFunctionDto
                {
                    Name = aiFunction.Name,
                    Description = aiFunction.Description,
                    Parameters = aiFunction.JsonSchema
                });
            }
        }

        return functions.Count > 0 ? functions : null;
    }

    public static ChatResponse ToChatResponse(GigaChatCompletionsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var messages = new List<ChatMessage>();
        ChatFinishReason? finishReason = null;

        foreach (var choice in response.Choices)
        {
            if (choice.Message == null)
            {
                continue;
            }

            var contents = new List<AIContent>();

            if (!string.IsNullOrEmpty(choice.Message.Content))
            {
                contents.Add(new TextContent(choice.Message.Content));
            }

            if (choice.Message.FunctionCall != null)
            {
                var func = choice.Message.FunctionCall;
                IDictionary<string, object?>? arguments = null;

                if (func.Arguments.ValueKind == JsonValueKind.Object)
                {
                    arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(func.Arguments.GetRawText());
                }

                contents.Add(new FunctionCallContent(
                    callId: Guid.NewGuid().ToString("N"),
                    name: func.Name,
                    arguments: arguments));
            }

            finishReason = ToChatFinishReason(choice.FinishReason);

            var chatMsg = new ChatMessage(ChatRole.Assistant, contents);
            messages.Add(chatMsg);
        }

        UsageDetails? usage = null;
        if (response.Usage != null)
        {
            usage = new UsageDetails
            {
                InputTokenCount = response.Usage.PromptTokens,
                OutputTokenCount = response.Usage.CompletionTokens,
                TotalTokenCount = response.Usage.TotalTokens
            };
        }

        return new ChatResponse(messages)
        {
            ModelId = response.Model,
            CreatedAt = response.Created > 0
                ? DateTimeOffset.FromUnixTimeSeconds(response.Created)
                : DateTimeOffset.UtcNow,
            FinishReason = finishReason,
            Usage = usage
        };
    }

    public static ChatFinishReason? ToChatFinishReason(string? finishReason)
    {
        if (string.IsNullOrEmpty(finishReason))
        {
            return null;
        }

        return finishReason.ToLowerInvariant() switch
        {
            "stop" => ChatFinishReason.Stop,
            "length" => ChatFinishReason.Length,
            "function_call" => ChatFinishReason.ToolCalls,
            "blacklist" => new ChatFinishReason("content_filter"),
            _ => new ChatFinishReason(finishReason)
        };
    }
}
