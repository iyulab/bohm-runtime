using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// Answers a request an application wrote for the Gemini API (<c>POST …/models/{model}:generateContent</c>
/// and <c>:streamGenerateContent</c>) with a model that does not speak that API: text, inline
/// images, system instructions, several turns, JSON output, the application's own functions, and
/// the answer streamed as server-sent events (<c>alt=sse</c>) or as a JSON array, as the API does.
/// </summary>
/// <remarks>
/// The wire shape is read and written here from the API's published reference; REST bodies accept
/// field names in camelCase and snake_case alike, and so does this. The Google GenAI SDK has this
/// conversion (to and from Microsoft.Extensions.AI) but keeps it internal.
/// TODO(upstream): when the Google GenAI SDK makes that conversion public, use it here the way
/// <see cref="OpenAIChatBridge"/> uses the OpenAI one, and remove the reading and writing below.
/// </remarks>
internal sealed partial class GeminiBridge : IChatBridge
{
    public static readonly GeminiBridge Instance = new();

    private GeminiBridge()
    {
    }

    public bool AnswersChat(LlmProvider provider) => provider.Style == KeyStyle.Google;

    public bool Handles(LlmProvider provider, string method, string path) =>
        AnswersChat(provider) && method == "POST" && Generate().Match(path) is { Success: true } m && m.Groups["verb"].Value != "countTokens";

    public string? Unsupported(LlmProvider provider, string method, string path) =>
        AnswersChat(provider) && method == "POST" && Generate().Match(path) is { Success: true } m && m.Groups["verb"].Value == "countTokens"
            ? "The AI model answering in this provider's place cannot count tokens the way this provider does."
            : null;

    [GeneratedRegex(@"^v1(alpha|beta)?/(models|tunedModels)/(?<model>[^/:]+):(?<verb>generateContent|streamGenerateContent|countTokens)$")]
    private static partial Regex Generate();

    /// <exception cref="FormatException">The body is not a generateContent request this bridge can read.</exception>
    public BridgedChat Parse(ReadOnlySpan<byte> body, string path)
    {
        using var document = ChatBridges.ParseObject(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
            throw new FormatException("The request has no contents.");

        var conversation = new List<ChatMessage>();
        if (ChatBridges.TryGet(root, out var system, "systemInstruction", "system_instruction"))
        {
            var instructions = system.ValueKind == JsonValueKind.String
                ? system.GetString()
                : ChatBridges.TryGet(system, out var parts, "parts") ? string.Join("\n\n", parts.EnumerateArray().Select(p => ChatBridges.Text(p, "text")).OfType<string>()) : null;
            if (!string.IsNullOrEmpty(instructions)) conversation.Add(new ChatMessage(ChatRole.System, instructions));
        }

        foreach (var content in contents.EnumerateArray())
        {
            var role = ChatBridges.Text(content, "role") switch
            {
                null or "user" => ChatRole.User,
                "model" => ChatRole.Assistant,
                "function" or "tool" => ChatRole.Tool,
                var other => throw new FormatException($"A content has the role '{other}'."),
            };
            if (!ChatBridges.TryGet(content, out var parts, "parts") || parts.ValueKind != JsonValueKind.Array) throw new FormatException("A content has no parts.");
            conversation.Add(new ChatMessage(role, [.. parts.EnumerateArray().Select(PartOf).OfType<AIContent>()]));
        }

        var options = new ChatOptions();
        if (ChatBridges.TryGet(root, out var config, "generationConfig", "generation_config"))
        {
            if (ChatBridges.Number(config, "temperature") is { } temperature) options.Temperature = (float)temperature;
            if (ChatBridges.Number(config, "topP", "top_p") is { } topP) options.TopP = (float)topP;
            if (ChatBridges.Number(config, "topK", "top_k") is { } topK) options.TopK = (int)topK;
            if (ChatBridges.Number(config, "maxOutputTokens", "max_output_tokens") is { } max) options.MaxOutputTokens = (int)max;
            if (ChatBridges.Number(config, "seed") is { } seed) options.Seed = (long)seed;
            if (ChatBridges.Number(config, "presencePenalty", "presence_penalty") is { } presence) options.PresencePenalty = (float)presence;
            if (ChatBridges.Number(config, "frequencyPenalty", "frequency_penalty") is { } frequency) options.FrequencyPenalty = (float)frequency;
            options.StopSequences = ChatBridges.Strings(config, "stopSequences", "stop_sequences");
            if (ChatBridges.Text(config, "responseMimeType", "response_mime_type") == "application/json")
            {
                options.ResponseFormat = ChatBridges.TryGet(config, out var jsonSchema, "responseJsonSchema", "response_json_schema")
                    ? ChatResponseFormat.ForJsonSchema(jsonSchema.Clone())
                    : ChatBridges.TryGet(config, out var schema, "responseSchema", "response_schema")
                        ? ChatResponseFormat.ForJsonSchema(StandardSchema(schema))
                        : ChatResponseFormat.Json;
            }
        }

        // Thinking as the request sets it (a budget, or a level); a request that does not set it gets
        // none — models before 2.5 do not think, and a model here that reasons by default would
        // otherwise spend minutes before the first word on a CPU.
        options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };
        if (ChatBridges.TryGet(root, out var generation, "generationConfig", "generation_config")
            && ChatBridges.TryGet(generation, out var thinking, "thinkingConfig", "thinking_config"))
        {
            options.Reasoning.Effort = ChatBridges.Text(thinking, "thinkingLevel", "thinking_level")?.ToLowerInvariant() switch
            {
                "minimal" or "low" => ReasoningEffort.Low,
                "medium" => ReasoningEffort.Medium,
                "high" => ReasoningEffort.High,
                _ => ChatBridges.Number(thinking, "thinkingBudget", "thinking_budget") switch
                {
                    null or 0 => ReasoningEffort.None,
                    < 0 => ReasoningEffort.Medium,
                    var budget => ChatBridges.EffortForBudget(budget),
                },
            };
        }

        if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            options.Tools = [.. tools.EnumerateArray()
                .SelectMany(t => ChatBridges.TryGet(t, out var declarations, "functionDeclarations", "function_declarations") && declarations.ValueKind == JsonValueKind.Array
                    ? declarations.EnumerateArray()
                    : [])
                .Where(d => ChatBridges.Text(d, "name") is not null)
                .Select(d => ChatBridges.Declaration(ChatBridges.Text(d, "name")!, ChatBridges.Text(d, "description"),
                    ChatBridges.TryGet(d, out var json, "parametersJsonSchema", "parameters_json_schema") ? json
                    : ChatBridges.TryGet(d, out var parameters, "parameters") ? StandardSchema(parameters)
                    : null))];
        }

        return new BridgedChat(conversation, options, path.EndsWith(":streamGenerateContent", StringComparison.Ordinal), Generate().Match(path).Groups["model"].Value);
    }

    private static AIContent? PartOf(JsonElement part)
    {
        if (ChatBridges.Text(part, "text") is { } text)
        {
            // A part marked as the model's earlier reasoning: the model here reasons afresh.
            return part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True ? null : new TextContent(text);
        }

        if (ChatBridges.TryGet(part, out var inline, "inlineData", "inline_data"))
        {
            try
            {
                return new DataContent(Convert.FromBase64String(ChatBridges.Text(inline, "data") ?? ""), ChatBridges.Text(inline, "mimeType", "mime_type") ?? "application/octet-stream");
            }
            catch (FormatException e)
            {
                throw new FormatException("Inline data is not base64.", e);
            }
        }

        if (ChatBridges.TryGet(part, out var file, "fileData", "file_data")
            && Uri.TryCreate(ChatBridges.Text(file, "fileUri", "file_uri"), UriKind.Absolute, out var uri))
            return new UriContent(uri, ChatBridges.Text(file, "mimeType", "mime_type") ?? "application/octet-stream");

        // Gemini names calls by function; an id, when there is one, pairs a call with its result.
        if (ChatBridges.TryGet(part, out var call, "functionCall", "function_call"))
        {
            var name = ChatBridges.Text(call, "name") ?? "";
            return new FunctionCallContent(ChatBridges.Text(call, "id") ?? name, name, ChatBridges.TryGet(call, out var args, "args") ? ChatBridges.Arguments(args) : []);
        }

        if (ChatBridges.TryGet(part, out var result, "functionResponse", "function_response"))
        {
            var name = ChatBridges.Text(result, "name") ?? "";
            return new FunctionResultContent(ChatBridges.Text(result, "id") ?? name,
                ChatBridges.TryGet(result, out var response, "response") ? response.GetRawText() : "");
        }

        throw new FormatException("A part is not something the model answering in this provider's place can read.");
    }

    /// <summary>
    /// Gemini's schema (an OpenAPI subset) as a JSON schema: its type names are upper case
    /// (<c>OBJECT</c>, <c>STRING</c>), where JSON schema's are lower case.
    /// </summary>
    private static JsonElement StandardSchema(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!;
        Lower(node);
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

        static void Lower(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj.ToList())
                    {
                        if (key == "type" && value is JsonValue v && v.TryGetValue<string>(out var type)) obj[key] = type.ToLowerInvariant();
                        else Lower(value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array) Lower(item);
                    break;
            }
        }
    }

    /// <summary>Answers the request with <paramref name="model"/>, in the Gemini API's shape.</summary>
    public async Task AnswerAsync(HttpContext context, IChatClient model, BridgedChat request)
    {
        var response = context.Response;
        var cancel = context.RequestAborted;
        var id = Guid.NewGuid().ToString("N");
        var modelName = request.Model ?? "local";
        if (!request.Stream)
        {
            var answer = await model.GetResponseAsync(request.Messages, request.Options, cancel).ConfigureAwait(false);
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/json";
            var parts = Parts(answer.Messages.SelectMany(m => m.Contents));
            await ChatBridges.WriteAsync(response, Chunk(parts, answer.FinishReason, answer.Usage).ToJsonString(), cancel).ConfigureAwait(false);
            return;
        }

        // With alt=sse, server-sent events; without it, one JSON array written element by element.
        var sse = string.Equals(context.Request.Query["alt"], "sse", StringComparison.OrdinalIgnoreCase);
        ChatBridges.StartStream(context, sse ? "text/event-stream" : "application/json");
        var first = true;
        ChatFinishReason? finish = null;
        UsageDetails? usage = null;
        await foreach (var update in model.GetStreamingResponseAsync(request.Messages, request.Options, cancel).ConfigureAwait(false))
        {
            finish = update.FinishReason ?? finish;
            usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
            var parts = Parts(update.Contents);
            if (parts.Count > 0) await WriteChunkAsync(Chunk(parts, null, null)).ConfigureAwait(false);
        }

        // The last chunk carries why the answer ended and what it used, as the API's does.
        await WriteChunkAsync(Chunk([new JsonObject { ["text"] = "" }], finish ?? ChatFinishReason.Stop, usage)).ConfigureAwait(false);
        if (!sse) await ChatBridges.WriteAsync(response, first ? "[]" : "\n]", cancel).ConfigureAwait(false);

        Task WriteChunkAsync(JsonObject chunk)
        {
            var text = chunk.ToJsonString();
            if (sse) return ChatBridges.WriteAsync(response, $"data: {text}\r\n\r\n", cancel);
            var written = ChatBridges.WriteAsync(response, (first ? "[" : "\n,") + text, cancel);
            first = false;
            return written;
        }

        JsonObject Chunk(JsonArray parts, ChatFinishReason? reason, UsageDetails? used)
        {
            var candidate = new JsonObject { ["content"] = new JsonObject { ["parts"] = parts, ["role"] = "model" } };
            if (reason is { } ended) candidate["finishReason"] = FinishReason(ended);
            candidate["index"] = 0;
            var chunk = new JsonObject { ["candidates"] = new JsonArray(candidate) };
            if (used is not null || reason is not null)
            {
                var input = used?.InputTokenCount ?? 0;
                var output = used?.OutputTokenCount ?? 0;
                chunk["usageMetadata"] = new JsonObject { ["promptTokenCount"] = input, ["candidatesTokenCount"] = output, ["totalTokenCount"] = used?.TotalTokenCount ?? input + output };
            }

            chunk["modelVersion"] = modelName;
            chunk["responseId"] = id;
            return chunk;
        }
    }

    private static JsonArray Parts(IEnumerable<AIContent> contents)
    {
        var parts = new JsonArray();
        foreach (var content in contents)
        {
            if (content is TextContent { Text.Length: > 0 } text)
            {
                if (parts.Count > 0 && parts[^1] is JsonObject last && last["text"] is not null) last["text"] = (string?)last["text"] + text.Text;
                else parts.Add(new JsonObject { ["text"] = text.Text });
            }
            else if (content is FunctionCallContent call)
            {
                parts.Add(new JsonObject { ["functionCall"] = new JsonObject { ["name"] = call.Name, ["args"] = ChatBridges.ArgumentsNode(call.Arguments) } });
            }
        }

        return parts;
    }

    private static string FinishReason(ChatFinishReason reason) =>
        reason == ChatFinishReason.Length ? "MAX_TOKENS"
        : reason == ChatFinishReason.ContentFilter ? "SAFETY"
        : "STOP";
}
