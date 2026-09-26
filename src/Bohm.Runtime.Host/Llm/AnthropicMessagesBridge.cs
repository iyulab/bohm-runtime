using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// Answers a request an application wrote for the Anthropic Messages API (<c>POST …/v1/messages</c>)
/// with a model that does not speak that API: text, images given inline, system instructions,
/// several turns, the application's own tools, and the answer streamed as the API streams it.
/// </summary>
/// <remarks>
/// The wire shape is read and written here from the API's published reference. The Anthropic SDK
/// has this conversion (to and from Microsoft.Extensions.AI) but keeps it internal.
/// TODO(upstream): when the Anthropic SDK makes that conversion public, use it here the way
/// <see cref="OpenAIChatBridge"/> uses the OpenAI one, and remove the reading and writing below.
/// </remarks>
internal sealed class AnthropicMessagesBridge : IChatBridge
{
    public static readonly AnthropicMessagesBridge Instance = new();

    private AnthropicMessagesBridge()
    {
    }

    public bool AnswersChat(LlmProvider provider) => provider.Style == KeyStyle.ApiKeyHeader;

    public bool Handles(LlmProvider provider, string method, string path) =>
        AnswersChat(provider) && method == "POST" && path.TrimEnd('/').EndsWith("v1/messages", StringComparison.Ordinal);

    public string? Unsupported(LlmProvider provider, string method, string path) =>
        AnswersChat(provider) && method == "POST" && path.TrimEnd('/').EndsWith("v1/messages/count_tokens", StringComparison.Ordinal)
            ? "The AI model on this computer cannot count tokens the way this provider does."
            : null;

    /// <exception cref="FormatException">The body is not a Messages request this bridge can read.</exception>
    public BridgedChat Parse(ReadOnlySpan<byte> body, string path)
    {
        using var document = ChatBridges.ParseObject(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            throw new FormatException("The request has no messages.");

        var conversation = new List<ChatMessage>();
        if (root.TryGetProperty("system", out var system))
        {
            var instructions = system.ValueKind switch
            {
                JsonValueKind.String => system.GetString(),
                JsonValueKind.Array => string.Join("\n\n", system.EnumerateArray().Select(b => ChatBridges.Text(b, "text")).OfType<string>()),
                _ => null,
            };
            if (!string.IsNullOrEmpty(instructions)) conversation.Add(new ChatMessage(ChatRole.System, instructions));
        }

        foreach (var message in messages.EnumerateArray())
        {
            var role = ChatBridges.Text(message, "role") switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                var other => throw new FormatException($"A message has the role '{other}'."),
            };
            if (!message.TryGetProperty("content", out var content)) throw new FormatException("A message has no content.");
            conversation.Add(new ChatMessage(role, ContentsOf(content)));
        }

        var options = new ChatOptions();
        if (ChatBridges.Number(root, "max_tokens") is { } max) options.MaxOutputTokens = (int)max;
        if (ChatBridges.Number(root, "temperature") is { } temperature) options.Temperature = (float)temperature;
        if (ChatBridges.Number(root, "top_p") is { } topP) options.TopP = (float)topP;
        if (ChatBridges.Number(root, "top_k") is { } topK) options.TopK = (int)topK;
        options.StopSequences = ChatBridges.Strings(root, "stop_sequences");

        if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            // Only the application's own tools: the provider's server tools (web search and the
            // like) have a type and no schema, and nothing on this computer runs them.
            options.Tools = [.. tools.EnumerateArray()
                .Where(t => t.TryGetProperty("input_schema", out _) && ChatBridges.Text(t, "name") is not null)
                .Select(t => ChatBridges.Declaration(ChatBridges.Text(t, "name")!, ChatBridges.Text(t, "description"), t.GetProperty("input_schema")))];
        }

        if (root.TryGetProperty("tool_choice", out var choice))
        {
            options.ToolMode = ChatBridges.Text(choice, "type") switch
            {
                "none" => ChatToolMode.None,
                "any" => ChatToolMode.RequireAny,
                "tool" when ChatBridges.Text(choice, "name") is { } name => ChatToolMode.RequireSpecific(name),
                _ => ChatToolMode.Auto,
            };
        }

        // Without "thinking" the API does not think; with it, the budget says how much. A model here
        // that reasons by default would otherwise spend minutes before the first word on a CPU.
        options.Reasoning = new ReasoningOptions
        {
            Effort = root.TryGetProperty("thinking", out var thinking) ? ChatBridges.Text(thinking, "type") switch
            {
                "enabled" => ChatBridges.EffortForBudget(ChatBridges.Number(thinking, "budget_tokens")),
                "adaptive" => ReasoningEffort.Medium,
                _ => ReasoningEffort.None,
            } : ReasoningEffort.None,
        };

        var stream = root.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        return new BridgedChat(conversation, options, stream, ChatBridges.Text(root, "model"));
    }

    private static List<AIContent> ContentsOf(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return [new TextContent(content.GetString())];
        if (content.ValueKind != JsonValueKind.Array) throw new FormatException("A message's content is neither text nor blocks.");

        var contents = new List<AIContent>();
        foreach (var block in content.EnumerateArray())
        {
            switch (ChatBridges.Text(block, "type"))
            {
                case "text":
                    contents.Add(new TextContent(ChatBridges.Text(block, "text")));
                    break;
                case "image" when block.TryGetProperty("source", out var source):
                    contents.Add(ChatBridges.Text(source, "type") switch
                    {
                        "base64" => new DataContent(Base64(ChatBridges.Text(source, "data")), ChatBridges.Text(source, "media_type") ?? "image/png"),
                        "url" when Uri.TryCreate(ChatBridges.Text(source, "url"), UriKind.Absolute, out var url) => new UriContent(url, "image/*"),
                        _ => throw new FormatException("An image block has a source this model cannot read."),
                    });
                    break;
                case "tool_use":
                    contents.Add(new FunctionCallContent(ChatBridges.Text(block, "id") ?? "", ChatBridges.Text(block, "name") ?? "",
                        block.TryGetProperty("input", out var input) ? ChatBridges.Arguments(input) : []));
                    break;
                case "tool_result":
                    contents.Add(new FunctionResultContent(ChatBridges.Text(block, "tool_use_id") ?? "",
                        block.TryGetProperty("content", out var result) ? ResultText(result) : ""));
                    break;
                case "thinking" or "redacted_thinking":
                    // The model's earlier reasoning, sent back as the API asks: the model here
                    // reasons afresh, so it is left out.
                    break;
                case var type:
                    throw new FormatException($"Content of type '{type}' is not something the model on this computer can read.");
            }
        }

        return contents;
    }

    private static string ResultText(JsonElement result) => result.ValueKind switch
    {
        JsonValueKind.String => result.GetString()!,
        JsonValueKind.Array => string.Concat(result.EnumerateArray().Select(b => ChatBridges.Text(b, "text"))),
        _ => result.GetRawText(),
    };

    private static byte[] Base64(string? data)
    {
        try
        {
            return Convert.FromBase64String(data ?? "");
        }
        catch (FormatException e)
        {
            throw new FormatException("An image's data is not base64.", e);
        }
    }

    /// <summary>Answers the request with <paramref name="model"/>, in the Messages API's shape.</summary>
    public async Task AnswerAsync(HttpContext context, IChatClient model, BridgedChat request)
    {
        var response = context.Response;
        var cancel = context.RequestAborted;
        var id = "msg_" + Guid.NewGuid().ToString("N");
        var modelName = request.Model ?? "local";
        if (!request.Stream)
        {
            var answer = await model.GetResponseAsync(request.Messages, request.Options, cancel).ConfigureAwait(false);
            var blocks = new JsonArray();
            foreach (var content in answer.Messages.SelectMany(m => m.Contents))
            {
                if (content is TextContent { Text.Length: > 0 } text)
                {
                    if (blocks.Count > 0 && blocks[^1] is JsonObject last && (string?)last["type"] == "text") last["text"] = (string?)last["text"] + text.Text;
                    else blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                }
                else if (content is FunctionCallContent call)
                {
                    blocks.Add(ToolUse(call));
                }
            }

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/json";
            var message = new JsonObject
            {
                ["id"] = id,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = modelName,
                ["content"] = blocks,
                ["stop_reason"] = StopReason(answer.FinishReason, blocks.Any(b => (string?)b!["type"] == "tool_use")),
                ["stop_sequence"] = null,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = answer.Usage?.InputTokenCount ?? 0,
                    ["output_tokens"] = answer.Usage?.OutputTokenCount ?? 0,
                },
            };
            await ChatBridges.WriteAsync(response, message.ToJsonString(), cancel).ConfigureAwait(false);
            return;
        }

        // The Messages stream: named server-sent events, one content block open at a time.
        ChatBridges.StartStream(context, "text/event-stream");
        await EventAsync("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = id,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = modelName,
                ["content"] = new JsonArray(),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
            },
        }).ConfigureAwait(false);

        var index = -1;
        var textOpen = false;
        var calledTool = false;
        ChatFinishReason? finish = null;
        UsageDetails? usage = null;
        await foreach (var update in model.GetStreamingResponseAsync(request.Messages, request.Options, cancel).ConfigureAwait(false))
        {
            finish = update.FinishReason ?? finish;
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } text:
                        if (!textOpen)
                        {
                            textOpen = true;
                            await EventAsync("content_block_start", new JsonObject
                            {
                                ["type"] = "content_block_start",
                                ["index"] = ++index,
                                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" },
                            }).ConfigureAwait(false);
                        }

                        await EventAsync("content_block_delta", new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = index,
                            ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text.Text },
                        }).ConfigureAwait(false);
                        break;
                    case FunctionCallContent call:
                        if (textOpen)
                        {
                            textOpen = false;
                            await StopBlockAsync(index).ConfigureAwait(false);
                        }

                        calledTool = true;
                        var start = ToolUse(call);
                        var input = start["input"]!.ToJsonString();
                        start["input"] = new JsonObject();
                        await EventAsync("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = ++index, ["content_block"] = start }).ConfigureAwait(false);
                        await EventAsync("content_block_delta", new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = index,
                            ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = input },
                        }).ConfigureAwait(false);
                        await StopBlockAsync(index).ConfigureAwait(false);
                        break;
                    case UsageContent used:
                        usage = used.Details;
                        break;
                }
            }
        }

        if (textOpen) await StopBlockAsync(index).ConfigureAwait(false);
        await EventAsync("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = StopReason(finish, calledTool), ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["input_tokens"] = usage?.InputTokenCount ?? 0, ["output_tokens"] = usage?.OutputTokenCount ?? 0 },
        }).ConfigureAwait(false);
        await EventAsync("message_stop", new JsonObject { ["type"] = "message_stop" }).ConfigureAwait(false);

        Task EventAsync(string name, JsonObject data) => ChatBridges.WriteAsync(response, $"event: {name}\ndata: {data.ToJsonString()}\n\n", cancel);

        Task StopBlockAsync(int at) => EventAsync("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = at });
    }

    private static JsonObject ToolUse(FunctionCallContent call) => new()
    {
        ["type"] = "tool_use",
        ["id"] = string.IsNullOrEmpty(call.CallId) ? "toolu_" + Guid.NewGuid().ToString("N") : call.CallId,
        ["name"] = call.Name,
        ["input"] = ChatBridges.ArgumentsNode(call.Arguments),
    };

    private static string StopReason(ChatFinishReason? finish, bool calledTool) =>
        calledTool || finish == ChatFinishReason.ToolCalls ? "tool_use"
        : finish == ChatFinishReason.Length ? "max_tokens"
        : finish == ChatFinishReason.ContentFilter ? "refusal"
        : "end_turn";
}
