using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// An application written for the OpenAI chat API, with no key connected, answered by a model on
/// this computer: same request, same response shape, nothing sent anywhere.
/// </summary>
public sealed class LocalModelBridgeTests : IAsyncLifetime
{
    private const string RealKey = "sk-real-secret-0123456789";

    private FakeProvider _provider = null!;
    private FakeModel _model = null!;
    private RunningHost _host = null!;
    private string _app = null!;
    private string _cookie = null!;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync();
        _model = new FakeModel();
        _host = await RunningHost.StartAsync(configure: o => o with
        {
            LlmEndpoints = new Dictionary<string, Uri> { ["api.openai.com"] = _provider.Address, ["api.anthropic.com"] = _provider.Address },
            LocalModel = new LocalModelOptions { ModelPath = "unused.gguf", Client = _model },
        });
        _app = await _host.AdoptAsync("<!doctype html><title>AI</title>");
        using var load = await _host.ClientForApp(_app).GetAsync("/");
        var setCookie = Assert.Single(load.Headers.GetValues("Set-Cookie"));
        _cookie = setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Without_a_key_the_local_model_answers_in_the_chat_completions_shape_and_nothing_is_sent()
    {
        _model.Reply = "Paris";

        using var response = await PostAsync("/__bohm/llm/api.openai.com/v1/chat/completions", """
            {"model":"gpt-4o-mini","temperature":0.2,"max_tokens":64,"stop":["\n\n"],
             "messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"Capital of France?"}]}
            """);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("chat.completion", body.GetProperty("object").GetString());
        var choice = Assert.Single(body.GetProperty("choices").EnumerateArray());
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Paris", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        // What the provider gives and leaves out: an id and a current time; no empty tool_calls,
        // which JavaScript would read as true.
        Assert.StartsWith("chatcmpl-", body.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.InRange(DateTimeOffset.FromUnixTimeSeconds(body.GetProperty("created").GetInt64()), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.False(choice.GetProperty("message").TryGetProperty("tool_calls", out _));
        Assert.Equal(JsonValueKind.Null, choice.GetProperty("logprobs").ValueKind);

        var call = Assert.Single(_model.Calls);
        Assert.Equal([ChatRole.System, ChatRole.User], call.Messages.Select(m => m.Role));
        Assert.Equal("Capital of France?", call.Messages[1].Text);
        Assert.Equal(0.2f, call.Options!.Temperature);
        Assert.Equal(64, call.Options.MaxOutputTokens);
        Assert.Equal(["\n\n"], call.Options.StopSequences!);

        Assert.Empty(_provider.Received);
        var egress = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement;
        Assert.Empty(egress.GetProperty("sent").EnumerateArray());
        var status = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{_app}/status")).RootElement;
        Assert.Empty(status.GetProperty("needsKey").EnumerateArray());
    }

    [Fact]
    public async Task A_streamed_request_gets_server_sent_chunks_ending_with_done()
    {
        _model.Chunks = ["Pa", "ri", "s"];

        using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions")
        {
            Content = Json("""{"model":"m","stream":true,"messages":[{"role":"user","content":"Capital?"}]}"""),
        };
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var events = (await response.Content.ReadAsStringAsync())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e["data: ".Length..])
            .ToList();

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("[DONE]", events[^1]);
        var text = string.Concat(events[..^1]
            .Select(e => JsonDocument.Parse(e).RootElement)
            .Select(e => Assert.Single(e.GetProperty("choices").EnumerateArray()).GetProperty("delta"))
            .Select(d => d.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : ""));
        Assert.Equal("Paris", text);
        Assert.All(events[..^1], e => Assert.Equal("chat.completion.chunk", JsonDocument.Parse(e).RootElement.GetProperty("object").GetString()));
        var ids = events[..^1].Select(e => JsonDocument.Parse(e).RootElement.GetProperty("id").GetString()).Distinct().ToList();
        Assert.StartsWith("chatcmpl-", Assert.Single(ids), StringComparison.Ordinal);
        Assert.All(events[..^1], e => Assert.True(JsonDocument.Parse(e).RootElement.GetProperty("created").GetInt64() > 1_700_000_000));
        Assert.All(events[..^1], e => Assert.False(Assert.Single(JsonDocument.Parse(e).RootElement.GetProperty("choices").EnumerateArray()).GetProperty("delta").TryGetProperty("tool_calls", out _)));
    }

    [Fact]
    public async Task With_a_key_connected_the_request_goes_to_the_provider_unchanged_not_to_the_local_model()
    {
        using (var put = await _host.ControlClient().PutAsync("/__control/llm/openai/key", new StringContent(RealKey))) HttpAssert.Status(HttpStatusCode.OK, put);

        using var response = await PostAsync("/__bohm/llm/api.openai.com/v1/chat/completions", """{"model":"m","messages":[]}""");

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Single(_provider.Received);
        Assert.Empty(_model.Calls);
    }

    [Fact]
    public async Task A_request_in_a_shape_the_local_model_does_not_answer_still_asks_for_a_key()
    {
        using var response = await PostAsync("/__bohm/llm/api.anthropic.com/v1/messages", """{"model":"m","max_tokens":10,"messages":[]}""");

        HttpAssert.Status(HttpStatusCode.Unauthorized, response);
        Assert.Empty(_model.Calls);
        var status = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{_app}/status")).RootElement;
        Assert.Equal(["anthropic"], status.GetProperty("needsKey").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task The_applications_tools_are_declared_to_the_model_and_its_call_returns_as_tool_calls()
    {
        _model.Call = new FunctionCallContent("call-1", "get_weather", new Dictionary<string, object?> { ["city"] = "Seoul" });

        using var response = await PostAsync("/__bohm/llm/api.openai.com/v1/chat/completions", """
            {"model":"m","messages":[{"role":"user","content":"Weather in Seoul?"}],
             "tools":[{"type":"function","function":{"name":"get_weather","description":"Weather now","parameters":{"type":"object","properties":{"city":{"type":"string"}}}}}]}
            """);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var tool = Assert.Single(Assert.Single(_model.Calls).Options!.Tools!);
        Assert.Equal("get_weather", tool.Name);
        var choice = Assert.Single(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("choices").EnumerateArray());
        var toolCall = Assert.Single(choice.GetProperty("message").GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("call-1", toolCall.GetProperty("id").GetString());
        Assert.Equal("get_weather", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Seoul", JsonDocument.Parse(toolCall.GetProperty("function").GetProperty("arguments").GetString()!).RootElement.GetProperty("city").GetString());
    }

    [Fact]
    public async Task A_body_that_is_not_a_chat_request_gets_a_provider_shaped_error()
    {
        using var response = await PostAsync("/__bohm/llm/api.openai.com/v1/chat/completions", """{"model":"m"}""");

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("bohm_invalid_request", error.GetProperty("type").GetString());
        Assert.Empty(_model.Calls);
    }

    [Fact]
    public async Task A_model_file_that_is_not_there_gets_an_unavailable_error_not_a_download()
    {
        await using var host = await RunningHost.StartAsync(configure: o => o with
        {
            LocalModel = new LocalModelOptions { ModelPath = Path.Combine(Path.GetTempPath(), "no-such-model.gguf") },
        });
        var app = await host.AdoptAsync("<p>x</p>");
        using var load = await host.ClientForApp(app).GetAsync("/");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions")
        {
            Content = Json("""{"model":"m","messages":[{"role":"user","content":"hi"}]}"""),
        };
        request.Headers.Add("Cookie", Assert.Single(load.Headers.GetValues("Set-Cookie")).Split(';')[0]);

        using var response = await host.ClientForApp(app).SendAsync(request);

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("bohm_local_model_unavailable", error.GetProperty("type").GetString());
        Assert.Contains("no-such-model.gguf", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_control_api_says_which_providers_the_local_model_answers()
    {
        var providers = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/llm")).RootElement.EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("answeredLocally").GetBoolean());
        Assert.True(providers["openai"]);
        Assert.True(providers["groq"]);
        Assert.False(providers["anthropic"]);
        Assert.False(providers["google"]);

        using var put = await _host.ControlClient().PutAsync("/__control/llm/openai/key", new StringContent(RealKey));
        Assert.False(JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement.GetProperty("answeredLocally").GetBoolean());
    }

    [Fact]
    public async Task A_chosen_model_is_remembered_across_launches_and_can_be_chosen_away()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-local-model-").FullName;
        var model = Path.Combine(dataRoot, "..", Path.GetRandomFileName() + ".gguf");
        await File.WriteAllTextAsync(model, "not really a model", TestContext.Current.CancellationToken);
        model = Path.GetFullPath(model);
        try
        {
            var first = await RunningHost.StartAsync(dataRoot);
            var none = JsonDocument.Parse(await first.ControlClient().GetStringAsync("/__control/llm/local-model")).RootElement;
            Assert.Equal(JsonValueKind.Null, none.GetProperty("modelPath").ValueKind);
            Assert.False(none.GetProperty("fixed").GetBoolean());

            using var chosen = await first.ControlClient().PutAsync("/__control/llm/local-model", new StringContent(model));
            HttpAssert.Status(HttpStatusCode.OK, chosen);
            Assert.Equal(model, JsonDocument.Parse(await chosen.Content.ReadAsStringAsync()).RootElement.GetProperty("modelPath").GetString());
            var openai = JsonDocument.Parse(await first.ControlClient().GetStringAsync("/__control/llm")).RootElement.EnumerateArray()
                .Single(p => p.GetProperty("id").GetString() == "openai");
            Assert.True(openai.GetProperty("answeredLocally").GetBoolean());
            await first.StopKeepingDataAsync(); // the data root stays for the second launch

            await using var second = await RunningHost.StartAsync(dataRoot);
            var remembered = JsonDocument.Parse(await second.ControlClient().GetStringAsync("/__control/llm/local-model")).RootElement;
            Assert.Equal(model, remembered.GetProperty("modelPath").GetString());
            Assert.False(remembered.GetProperty("loaded").GetBoolean());

            using var cleared = await second.ControlClient().DeleteAsync("/__control/llm/local-model");
            HttpAssert.Status(HttpStatusCode.OK, cleared);
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await cleared.Content.ReadAsStringAsync()).RootElement.GetProperty("modelPath").ValueKind);
            Assert.False(File.Exists(Path.Combine(dataRoot, "local-model.json")));
            Assert.All(JsonDocument.Parse(await second.ControlClient().GetStringAsync("/__control/llm")).RootElement.EnumerateArray(),
                p => Assert.False(p.GetProperty("answeredLocally").GetBoolean()));
        }
        finally
        {
            File.Delete(model);
        }
    }

    [Fact]
    public async Task Loading_ahead_of_use_needs_a_chosen_model()
    {
        await using var host = await RunningHost.StartAsync();

        using var response = await host.ControlClient().PostAsync("/__control/llm/local-model/load", null);

        HttpAssert.Status(HttpStatusCode.Conflict, response);
    }

    [Fact]
    public async Task A_load_started_ahead_of_use_that_fails_says_why_until_another_model_is_chosen()
    {
        var model = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".gguf");
        await File.WriteAllTextAsync(model, "not really a model", TestContext.Current.CancellationToken);
        try
        {
            // A server that is not there fails the load at once, without looking for one elsewhere.
            await using var host = await RunningHost.StartAsync(configure: o => o with { LlamaServerPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "llama-server.exe") });
            using var client = host.ControlClient();
            (await client.PutAsync("/__control/llm/local-model", new StringContent(model))).Dispose();

            using var started = await client.PostAsync("/__control/llm/local-model/load", null);

            HttpAssert.Status(HttpStatusCode.Accepted, started);
            var failed = await EventuallyAsync(async () =>
            {
                var view = JsonDocument.Parse(await client.GetStringAsync("/__control/llm/local-model")).RootElement;
                return view.GetProperty("loading").GetBoolean() ? null : view;
            });
            Assert.False(failed.GetProperty("loaded").GetBoolean());
            Assert.False(string.IsNullOrEmpty(failed.GetProperty("error").GetString()));

            using var again = await client.PutAsync("/__control/llm/local-model", new StringContent(model));
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("error").ValueKind);
        }
        finally
        {
            File.Delete(model);
        }
    }

    [Fact]
    public async Task A_real_model_loaded_ahead_of_use_is_running_before_the_first_request()
    {
        var model = Environment.GetEnvironmentVariable("BOHM_TEST_GGUF");
        var server = Environment.GetEnvironmentVariable("BOHM_TEST_LLAMA_SERVER");
        Assert.SkipWhen(string.IsNullOrEmpty(model) || string.IsNullOrEmpty(server), "BOHM_TEST_GGUF and BOHM_TEST_LLAMA_SERVER are not set.");
        await using var host = await RunningHost.StartAsync(configure: o => o with { LlamaServerPath = server });
        using var client = host.ControlClient();
        (await client.PutAsync("/__control/llm/local-model", new StringContent(model!))).Dispose();

        (await client.PostAsync("/__control/llm/local-model/load", null)).Dispose();

        var view = await EventuallyAsync(async () =>
        {
            var v = JsonDocument.Parse(await client.GetStringAsync("/__control/llm/local-model")).RootElement;
            return v.GetProperty("loading").GetBoolean() ? null : v;
        }, TimeSpan.FromMinutes(5));
        Assert.True(view.GetProperty("loaded").GetBoolean(), view.GetProperty("error").GetString());
    }

    private static async Task<JsonElement> EventuallyAsync(Func<Task<JsonElement?>> probe, TimeSpan? limit = null)
    {
        var until = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            if (await probe() is { } found) return found;
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition did not hold in time.");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("relative.gguf")]
    [InlineData(@"C:\no\such\model.gguf")]
    public async Task Only_a_model_file_that_is_there_can_be_chosen(string path)
    {
        await using var host = await RunningHost.StartAsync();

        using var response = await host.ControlClient().PutAsync("/__control/llm/local-model", new StringContent(path));

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.False(File.Exists(Path.Combine(host.DataRoot, "local-model.json")));
    }

    [Fact]
    public async Task A_model_fixed_at_start_cannot_be_changed_through_the_control_api()
    {
        using var response = await _host.ControlClient().DeleteAsync("/__control/llm/local-model");

        HttpAssert.Status(HttpStatusCode.Conflict, response);
        Assert.True(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/llm/local-model")).RootElement.GetProperty("fixed").GetBoolean());
    }

    /// <summary>
    /// The real thing, end to end: a GGUF model and a llama-server already on this computer, loaded
    /// with nothing downloaded. Runs only where both are named, since neither ships with the tests:
    /// <c>BOHM_TEST_GGUF</c> (a model file) and <c>BOHM_TEST_LLAMA_SERVER</c> (the executable).
    /// </summary>
    [Fact]
    public async Task A_real_model_on_this_computer_answers_when_one_is_named()
    {
        var model = Environment.GetEnvironmentVariable("BOHM_TEST_GGUF");
        var server = Environment.GetEnvironmentVariable("BOHM_TEST_LLAMA_SERVER");
        Assert.SkipWhen(string.IsNullOrEmpty(model) || string.IsNullOrEmpty(server), "BOHM_TEST_GGUF and BOHM_TEST_LLAMA_SERVER are not set.");

        await using var host = await RunningHost.StartAsync(configure: o => o with
        {
            LocalModel = new LocalModelOptions { ModelPath = model!, ServerPath = server, ContextLength = 2048 },
        });
        var app = await host.AdoptAsync("<p>x</p>");
        using var load = await host.ClientForApp(app).GetAsync("/");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions")
        {
            // Thinking off: a reasoning model can otherwise spend a small budget before answering.
            Content = Json("""{"model":"m","max_tokens":48,"temperature":0,"reasoning_effort":"none","messages":[{"role":"user","content":"Reply with one word: the capital of France."}]}"""),
        };
        request.Headers.Add("Cookie", Assert.Single(load.Headers.GetValues("Set-Cookie")).Split(';')[0]);

        // A cold first load (model not in the file cache, a freshly copied server scanned on first start) has taken
        // over HttpClient's 100 s default here; the product path waits without a limit, so the test does too, up to a bound.
        using var client = host.ClientForApp(app);
        client.Timeout = TimeSpan.FromMinutes(5);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var content = Assert.Single(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("choices").EnumerateArray())
            .GetProperty("message").GetProperty("content").GetString();
        Assert.Contains("Paris", content, StringComparison.OrdinalIgnoreCase);
        var egress = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement;
        Assert.Empty(egress.GetProperty("sent").EnumerateArray());
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> PostAsync(string path, string body) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) });

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        request.Headers.Add("Cookie", _cookie);
        request.Headers.Authorization = new("Bearer", $"bohm-key-{_app}");
        return _host.ClientForApp(_app).SendAsync(request, completion);
    }

    /// <summary>A model that records what it was asked and answers as told.</summary>
    private sealed class FakeModel : IChatClient
    {
        public List<(List<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

        public string Reply { get; set; } = "ok";

        public IReadOnlyList<string> Chunks { get; set; } = ["ok"];

        public FunctionCallContent? Call { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(([.. messages], options));
            var message = Call is { } call ? new ChatMessage(ChatRole.Assistant, [call]) : new ChatMessage(ChatRole.Assistant, Reply);
            return Task.FromResult(new ChatResponse(message)
            {
                ModelId = "local-test",
                FinishReason = Call is null ? ChatFinishReason.Stop : ChatFinishReason.ToolCalls,
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(([.. messages], options));
            foreach (var chunk in Chunks)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk) { ModelId = "local-test", ResponseId = "r-1" };
            }

            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, ModelId = "local-test", ResponseId = "r-1" };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
