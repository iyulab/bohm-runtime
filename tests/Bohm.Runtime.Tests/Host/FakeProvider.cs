using System.Collections.Concurrent;
using System.Net;
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

            if (body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                context.Response.ContentType = "text/event-stream";
                foreach (var word in Reply.Split(' '))
                {
                    await context.Response.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{word} \"}}}}]}}\n\n");
                    await context.Response.Body.FlushAsync();
                    await Task.Delay(50);
                }

                await context.Response.WriteAsync("data: [DONE]\n\n");
                return;
            }

            context.Response.ContentType = "application/json";
            context.Response.Headers["x-provider-request-id"] = "req-1";
            await context.Response.WriteAsync($"{{\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":\"{Reply}\"}}}}]}}");
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
