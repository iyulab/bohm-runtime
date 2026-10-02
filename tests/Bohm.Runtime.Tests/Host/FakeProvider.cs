using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Bohm.Runtime.Tests.Host;

/// <summary>A stand-in AI provider on loopback that records what it received.</summary>
public sealed class FakeProvider : IAsyncDisposable
{
    public const string Reply = "hello from the provider";

    private readonly WebApplication _app;

    private FakeProvider(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    public Uri Address { get; }

    public ConcurrentQueue<ReceivedRequest> Received { get; } = new();

    /// <summary>When set, every request is refused with this status and body — as a provider refuses one it cannot serve.</summary>
    public (int Status, string Body)? Refusal { get; set; }

    /// <summary>
    /// What <c>GET …/models</c> answers when there is no <see cref="Refusal"/> — and then <c>GET …/models/{id}</c>
    /// is not found, as on vLLM, which serves only the list; <see langword="null"/> answers both like any other request.
    /// </summary>
    public string? Models { get; set; }

    /// <summary>The OpenAI-compatible answer's <c>finish_reason</c> — <c>length</c> as a server ends an answer at its length limit; <see langword="null"/> leaves it out.</summary>
    public string? FinishReason { get; set; }

    /// <summary>The requests received other than for the model list — what was asked of a model.</summary>
    public IReadOnlyList<ReceivedRequest> Asked => [.. Received.Where(r => !(r.Method == "GET" && r.PathAndQuery.EndsWith("/models", StringComparison.Ordinal)))];

    public sealed record ReceivedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

    public static async Task<FakeProvider> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        FakeProvider? self = null;
        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            self!.Received.Enqueue(new ReceivedRequest(context.Request.Method, context.Request.Path + context.Request.QueryString,
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));

            if (self.Refusal is { } refusal)
            {
                context.Response.StatusCode = refusal.Status;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(refusal.Body);
                return;
            }

            if (self.Models is { } models && context.Request.Method == "GET" && context.Request.Path.Value?.EndsWith("/models", StringComparison.Ordinal) == true)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(models);
                return;
            }

            if (self.Models is not null && context.Request.Method == "GET" && context.Request.Path.Value?.Contains("/models/", StringComparison.Ordinal) == true)
            {
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"detail":"Not Found"}""");
                return;
            }

            // Gemini's own API: the model and the verb are in the path, the answer is a candidate's parts.
            if (context.Request.Path.Value?.Contains(":streamGenerateContent", StringComparison.Ordinal) == true)
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync($"data: {{\"candidates\":[{{\"content\":{{\"role\":\"model\",\"parts\":[{{\"text\":\"{Reply}\"}}]}},\"finishReason\":\"STOP\"}}]}}\n\n");
                return;
            }

            if (context.Request.Path.Value?.Contains(":generateContent", StringComparison.Ordinal) == true)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($"{{\"candidates\":[{{\"content\":{{\"role\":\"model\",\"parts\":[{{\"text\":\"{Reply}\"}}]}},\"finishReason\":\"STOP\"}}]}}");
                return;
            }

            // OpenAI's own API: the Responses shape — output items, each message's content parts; streamed as its events.
            if (context.Request.Path.Value?.EndsWith("/v1/responses", StringComparison.Ordinal) == true && body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                context.Response.ContentType = "text/event-stream";
                var message = $$$"""{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"{{{Reply}}}","annotations":[]}]}""";
                string[] events =
                [
                    """{"type":"response.created","sequence_number":0,"response":{"id":"resp_1","object":"response","created_at":1,"status":"in_progress","model":"model-x","output":[]}}""",
                    """{"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"type":"message","id":"msg_1","status":"in_progress","role":"assistant","content":[]}}""",
                    .. Reply.Split(' ').Select((word, i) => $$$"""{"type":"response.output_text.delta","sequence_number":{{{2 + i}}},"item_id":"msg_1","output_index":0,"content_index":0,"delta":"{{{(i == 0 ? "" : " ") + word}}}"}"""),
                    $$$$"""{"type":"response.output_item.done","sequence_number":90,"output_index":0,"item":{{{{message}}}}}""",
                    $$$$"""{"type":"response.completed","sequence_number":91,"response":{"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"model-x","output":[{{{{message}}}}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}""",
                ];
                foreach (var data in events)
                {
                    await context.Response.WriteAsync($"event: {JsonDocument.Parse(data).RootElement.GetProperty("type").GetString()}\ndata: {data}\n\n");
                    await context.Response.Body.FlushAsync();
                }

                return;
            }

            if (context.Request.Path.Value?.EndsWith("/v1/responses", StringComparison.Ordinal) == true)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($$$"""{"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"model-x","output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"{{{Reply}}}","annotations":[]}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}""");
                return;
            }

            // Anthropic's own API: the Messages shape — content blocks, a stop reason, and usage; streamed as its events.
            if (context.Request.Path.Value?.EndsWith("/v1/messages", StringComparison.Ordinal) == true && body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                context.Response.ContentType = "text/event-stream";
                string[] events =
                [
                    """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"model-x","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}}""",
                    """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
                    .. Reply.Split(' ').Select((word, i) => $$$"""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{(i == 0 ? "" : " ") + word}}}"}}"""),
                    """{"type":"content_block_stop","index":0}""",
                    """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}""",
                    """{"type":"message_stop"}""",
                ];
                foreach (var data in events)
                {
                    await context.Response.WriteAsync($"event: {JsonDocument.Parse(data).RootElement.GetProperty("type").GetString()}\ndata: {data}\n\n");
                    await context.Response.Body.FlushAsync();
                }

                return;
            }

            if (context.Request.Path.Value?.EndsWith("/v1/messages", StringComparison.Ordinal) == true)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($$$"""{"id":"msg_1","type":"message","role":"assistant","model":"model-x","content":[{"type":"text","text":"{{{Reply}}}"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}""");
                return;
            }

            if (body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                context.Response.ContentType = "text/event-stream";
                foreach (var word in Reply.Split(' '))
                {
                    await context.Response.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{word} \"}}}}]}}\n\n");
                    await context.Response.Body.FlushAsync();
                    await Task.Delay(50);
                }

                if (self.FinishReason is { } streamedFinish)
                    await context.Response.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{}},\"finish_reason\":\"{streamedFinish}\"}}]}}\n\n");
                await context.Response.WriteAsync("data: [DONE]\n\n");
                return;
            }

            context.Response.ContentType = "application/json";
            context.Response.Headers["x-provider-request-id"] = "req-1";
            var finish = self.FinishReason is { } reason ? $",\"finish_reason\":\"{reason}\"" : "";
            await context.Response.WriteAsync($"{{\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":\"{Reply}\"}}{finish}}}]}}");
        });
        await app.StartAsync();
        var address = new Uri(app.Urls.First() + "/");
        self = new FakeProvider(app, address);
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
