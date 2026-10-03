using System.Diagnostics;
using System.Net;
using System.Text;
using Bohm.Runtime.Host.Llm;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Stopping a streamed question: when the caller goes away while the model's answer is still coming, the
/// runtime lets go of the model too — the request to it ends, rather than staying open until the model finishes.
/// </summary>
public sealed class StreamStopTests
{
    [Fact]
    public async Task Stopping_a_streamed_turn_mid_answer_ends_the_request_to_the_model_every_time()
    {
        var closed = 0;
        var started = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var model = builder.Build();
        model.Run(async context =>
        {
            if (context.Request.Path.Value!.EndsWith("/models", StringComparison.Ordinal))
            {
                await context.Response.WriteAsync("""{"object":"list","data":[{"id":"org-model","object":"model"}]}""");
                return;
            }

            // The first piece of an answer, then nothing until the request goes away.
            Interlocked.Increment(ref started);
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: " + """{"id":"x","object":"chat.completion.chunk","created":0,"model":"org-model","choices":[{"index":0,"delta":{"role":"assistant","content":"Today"},"finish_reason":null}]}""" + "\n\n");
            await context.Response.Body.FlushAsync();
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch (OperationCanceledException) { Interlocked.Increment(ref closed); }
        });
        await model.StartAsync(TestContext.Current.CancellationToken);
        var address = new Uri(model.Urls.First() + "/");

        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = CompanyModelList.Of(new CompanyModelOptions(new Uri(address, "v1/"), "org-model")) });
        var slowest = TimeSpan.Zero;
        var Rounds = int.TryParse(Environment.GetEnvironmentVariable("BOHM_STOP_ROUNDS"), out var r) ? r : 30;
        for (var round = 0; round < Rounds; round++)
        {
            var closedBefore = Volatile.Read(ref closed);
            using var client = host.ControlClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/__control/agent/turns")
            {
                Content = new StringContent("""{"messages":[{"role":"user","text":"What is for lunch?"}]}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/x-ndjson");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
            var first = await reader.ReadLineAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Today", first, StringComparison.Ordinal);

            // The caller stops: the connection goes, as when the shell drops its request.
            var clock = Stopwatch.StartNew();
            response.Dispose();
            client.Dispose();
            while (Volatile.Read(ref closed) == closedBefore && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.True(Volatile.Read(ref closed) > closedBefore, $"round {round}: the request to the model stayed open {clock.Elapsed.TotalSeconds:0.0}s after the caller went away");
            if (clock.Elapsed > slowest) slowest = clock.Elapsed;
        }

        TestContext.Current.SendDiagnosticMessage($"rounds={Rounds} started={started} closed={closed} slowest={slowest.TotalMilliseconds:0}ms");
    }
}
