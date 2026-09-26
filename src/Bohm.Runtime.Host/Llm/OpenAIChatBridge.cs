using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
// The Microsoft.Extensions.AI ↔ OpenAI conversions are marked for evaluation by the OpenAI SDK.
// They are the maintained, two-way mapping for this wire shape; re-implementing it here would be
// the worse risk. A change in them shows up in this project's tests.
#pragma warning disable OPENAI001
using OpenAIChatMessage = OpenAI.Chat.ChatMessage;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// Answers a request an application wrote for the OpenAI chat completions API
/// (<c>POST …/chat/completions</c>) with a model that does not speak that API — the application
/// keeps its code and gets an answer in the shape it expects, streamed when it asked for a stream.
/// </summary>
/// <remarks>
/// The conversion both ways is Microsoft.Extensions.AI's own (<c>AsChatMessages</c>,
/// <c>AsOpenAIChatCompletion</c>, <c>AsOpenAIStreamingChatCompletionUpdatesAsync</c>); this class
/// only reads the request's options and writes the result to the wire.
/// </remarks>
internal sealed class OpenAIChatBridge : IChatBridge
{
    public static readonly OpenAIChatBridge Instance = new();

    private OpenAIChatBridge()
    {
    }

    public bool AnswersChat(LlmProvider provider) => provider.Style == KeyStyle.Bearer;

    public bool Handles(LlmProvider provider, string method, string path) =>
        AnswersChat(provider) && method == "POST"
        && path.TrimEnd('/').EndsWith("chat/completions", StringComparison.Ordinal);

    public string? Unsupported(LlmProvider provider, string method, string path) => null;

    /// <exception cref="FormatException">The body is not a chat completions request.</exception>
    public BridgedChat Parse(ReadOnlySpan<byte> body, string path)
    {
        using (var document = ChatBridges.ParseObject(body))
        {
            var root = document.RootElement;
            // Spoken answers: an answer without the audio the application asked for breaks it where it
            // reads the audio, so it is told plainly instead.
            if (ChatBridges.Strings(root, "modalities") is { } modalities && modalities.Contains("audio"))
                throw new NotSupportedException("The AI model on this computer cannot answer with audio.");

            if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                throw new FormatException("The request has no messages.");

            var wire = new List<OpenAIChatMessage>();
            foreach (var message in messages.EnumerateArray())
            {
                try
                {
                    wire.Add(ModelReaderWriter.Read<OpenAIChatMessage>(BinaryData.FromString(message.GetRawText()), ModelReaderWriterOptions.Json, OpenAI.OpenAIContext.Default)
                        ?? throw new FormatException("A message is empty."));
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
                {
                    throw new FormatException("A message could not be read.", e);
                }
            }

            var options = new ChatOptions();
            if (Number(root, "temperature") is { } temperature) options.Temperature = (float)temperature;
            if (Number(root, "top_p") is { } topP) options.TopP = (float)topP;
            if ((Number(root, "max_completion_tokens") ?? Number(root, "max_tokens")) is { } max) options.MaxOutputTokens = (int)max;
            if (Number(root, "seed") is { } seed) options.Seed = (long)seed;
            if (Number(root, "frequency_penalty") is { } frequency) options.FrequencyPenalty = (float)frequency;
            if (Number(root, "presence_penalty") is { } presence) options.PresencePenalty = (float)presence;
            if (root.TryGetProperty("stop", out var stop))
            {
                options.StopSequences = stop.ValueKind switch
                {
                    JsonValueKind.String => [stop.GetString()!],
                    JsonValueKind.Array => [.. stop.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!)],
                    _ => null,
                };
            }

            if (root.TryGetProperty("response_format", out var format) && format.TryGetProperty("type", out var formatType))
            {
                options.ResponseFormat = formatType.GetString() switch
                {
                    "json_object" => Microsoft.Extensions.AI.ChatResponseFormat.Json,
                    "json_schema" when format.TryGetProperty("json_schema", out var schema) && schema.TryGetProperty("schema", out var body2)
                        => Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(body2.Clone(), schema.TryGetProperty("name", out var n) ? n.GetString() : null),
                    _ => null,
                };
            }

            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                // The application runs its own tools: the model only declares a call, which goes
                // back to the application as tool_calls, exactly as from the provider.
                options.Tools = [.. tools.EnumerateArray()
                    .Where(t => t.TryGetProperty("function", out _))
                    .Select(t => t.GetProperty("function"))
                    .Select(f => (AITool)AIFunctionFactory.CreateDeclaration(
                        f.GetProperty("name").GetString()!,
                        f.TryGetProperty("description", out var d) ? d.GetString() : null,
                        f.TryGetProperty("parameters", out var p) ? p.Clone() : JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()))];
            }

            // Thinking only when the request asks for it: most models an application names do not
            // think, and a model here that reasons by default would spend minutes before the first
            // word on a CPU.
            options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };
            if (root.TryGetProperty("reasoning_effort", out var effort) && effort.ValueKind == JsonValueKind.String)
            {
                ReasoningEffort? level = effort.GetString() switch
                {
                    "none" => ReasoningEffort.None,
                    "minimal" or "low" => ReasoningEffort.Low,
                    "medium" => ReasoningEffort.Medium,
                    "high" => ReasoningEffort.High,
                    _ => null,
                };
                if (level is { } known) options.Reasoning = new ReasoningOptions { Effort = known };
            }

            var stream = root.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
            return new BridgedChat(wire.AsChatMessages().ToList(), options, stream, ChatBridges.Text(root, "model"));
        }
    }

    /// <summary>Answers the request with <paramref name="model"/>, in the OpenAI chat completions shape.</summary>
    public async Task AnswerAsync(HttpContext context, IChatClient model, BridgedChat request)
    {
        var response = context.Response;
        var cancel = context.RequestAborted;
        if (!request.Stream)
        {
            var answer = await model.GetResponseAsync(request.Messages, request.Options, cancel).ConfigureAwait(false);
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/json";
            answer.ResponseId ??= NewId();
            answer.CreatedAt ??= DateTimeOffset.UtcNow;
            var completion = ModelReaderWriter.Write(answer.AsOpenAIChatCompletion(), ModelReaderWriterOptions.Json, OpenAI.OpenAIContext.Default);
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes(AsProviderWrites(completion, "message")), cancel).ConfigureAwait(false);
            return;
        }

        // Server-sent events, one per update, each flushed as it arrives — a typing effect in the
        // application depends on it.
        ChatBridges.StartStream(context, "text/event-stream");
        // One id and one time for the whole answer, as the provider gives them.
        var id = NewId();
        var created = DateTimeOffset.UtcNow;
        var updates = Stamped(model.GetStreamingResponseAsync(request.Messages, request.Options, cancel), id, created, cancel)
            .AsOpenAIStreamingChatCompletionUpdatesAsync(cancel);
        await foreach (var update in updates.ConfigureAwait(false))
        {
            var chunk = ModelReaderWriter.Write(update, ModelReaderWriterOptions.Json, OpenAI.OpenAIContext.Default);
            await WriteEventAsync(response, AsProviderWrites(chunk, "delta"), cancel).ConfigureAwait(false);
        }

        await WriteEventAsync(response, "[DONE]", cancel).ConfigureAwait(false);
    }

    private static string NewId() => "chatcmpl-" + Guid.NewGuid().ToString("N");

    private static async IAsyncEnumerable<ChatResponseUpdate> Stamped(IAsyncEnumerable<ChatResponseUpdate> updates, string id, DateTimeOffset created,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
    {
        await foreach (var update in updates.WithCancellation(cancel).ConfigureAwait(false))
        {
            update.ResponseId = id;
            update.CreatedAt ??= created;
            yield return update;
        }
    }

    /// <summary>
    /// The serialized result, with what the provider leaves out left out: an empty
    /// <c>tool_calls</c> (applications test <c>if (message.tool_calls)</c>, and an empty array is
    /// true in JavaScript), empty <c>annotations</c>, and log probabilities nobody asked for.
    /// </summary>
    private static string AsProviderWrites(BinaryData serialized, string part)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(serialized.ToString())!.AsObject();
        if (root["choices"] is System.Text.Json.Nodes.JsonArray choices)
        {
            foreach (var choice in choices.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                if (choice["logprobs"] is System.Text.Json.Nodes.JsonObject logprobs
                    && logprobs.All(p => p.Value is null or System.Text.Json.Nodes.JsonArray { Count: 0 }))
                    choice["logprobs"] = null;
                if (choice[part] is not System.Text.Json.Nodes.JsonObject body) continue;
                foreach (var name in new[] { "tool_calls", "annotations" })
                    if (body[name] is System.Text.Json.Nodes.JsonArray { Count: 0 }) body.Remove(name);
            }
        }

        return root.ToJsonString();
    }

    private static Task WriteEventAsync(HttpResponse response, string data, CancellationToken cancel) =>
        ChatBridges.WriteAsync(response, $"data: {data}\n\n", cancel);

    private static double? Number(JsonElement root, string name) => ChatBridges.Number(root, name);
}
