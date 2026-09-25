using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
// The Microsoft.Extensions.AI ↔ OpenAI conversions are marked for evaluation by the OpenAI SDK.
// They are the maintained, two-way mapping for this wire shape; re-implementing it here would be
// the worse risk. A change in them shows up in this project's tests.
#pragma warning disable OPENAI001
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
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
internal static class OpenAIChatBridge
{
    /// <summary>Whether the request is one this bridge can answer.</summary>
    public static bool Handles(LlmProvider provider, string method, string path) =>
        provider.Style == KeyStyle.Bearer && method == "POST"
        && path.TrimEnd('/').EndsWith("chat/completions", StringComparison.Ordinal);

    /// <summary>The request, read into Microsoft.Extensions.AI terms.</summary>
    public sealed record Parsed(IReadOnlyList<MeaiChatMessage> Messages, ChatOptions Options, bool Stream);

    /// <exception cref="FormatException">The body is not a chat completions request.</exception>
    public static Parsed Parse(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException e)
        {
            throw new FormatException("The request body is not JSON.", e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
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
            return new Parsed(wire.AsChatMessages().ToList(), options, stream);
        }
    }

    /// <summary>Answers the request with <paramref name="model"/>, in the OpenAI chat completions shape.</summary>
    public static async Task AnswerAsync(HttpContext context, IChatClient model, Parsed request)
    {
        var response = context.Response;
        var cancel = context.RequestAborted;
        if (!request.Stream)
        {
            var answer = await model.GetResponseAsync(request.Messages, request.Options, cancel).ConfigureAwait(false);
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/json";
            await response.Body.WriteAsync(ModelReaderWriter.Write(answer.AsOpenAIChatCompletion(), ModelReaderWriterOptions.Json, OpenAI.OpenAIContext.Default).ToMemory(), cancel).ConfigureAwait(false);
            return;
        }

        // Server-sent events, one per update, each flushed as it arrives — a typing effect in the
        // application depends on it.
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        var updates = model.GetStreamingResponseAsync(request.Messages, request.Options, cancel).AsOpenAIStreamingChatCompletionUpdatesAsync(cancel);
        await foreach (var update in updates.ConfigureAwait(false))
        {
            await WriteEventAsync(response, ModelReaderWriter.Write(update, ModelReaderWriterOptions.Json, OpenAI.OpenAIContext.Default).ToString(), cancel).ConfigureAwait(false);
        }

        await WriteEventAsync(response, "[DONE]", cancel).ConfigureAwait(false);
    }

    private static async Task WriteEventAsync(HttpResponse response, string data, CancellationToken cancel)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes($"data: {data}\n\n"), cancel).ConfigureAwait(false);
        await response.Body.FlushAsync(cancel).ConfigureAwait(false);
    }

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
