using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Applications written for the Anthropic Messages API or the Gemini API, with no key connected,
/// answered by a model on this computer in the shape each API gives. The requests are the
/// examples of each API's reference, trimmed to what they exercise.
/// </summary>
public sealed class LocalModelProviderShapesTests : IAsyncLifetime
{
    private FakeProvider _provider = null!;
    private FakeChatModel _model = null!;
    private RunningHost _host = null!;
    private string _app = null!;
    private string _cookie = null!;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync();
        _model = new FakeChatModel();
        _host = await RunningHost.StartAsync(configure: o => o with
        {
            LlmEndpoints = new Dictionary<string, Uri> { ["api.anthropic.com"] = _provider.Address, ["generativelanguage.googleapis.com"] = _provider.Address },
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
    public async Task Anthropic_a_message_is_answered_in_the_messages_shape_and_nothing_is_sent()
    {
        _model.Reply = "Hi! My name is Claude.";
        _model.Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 6 };

        using var response = await PostAsync("/__bohm/llm/api.anthropic.com/v1/messages", """
            {"model":"claude-sonnet-4-5","max_tokens":1024,"temperature":0.5,"top_k":40,"stop_sequences":["END"],
             "system":"You are a friendly assistant.",
             "messages":[{"role":"user","content":"Hello, Claude"}]}
            """);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.StartsWith("msg_", body.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal(("message", "assistant", "claude-sonnet-4-5"), (body.GetProperty("type").GetString(), body.GetProperty("role").GetString(), body.GetProperty("model").GetString()));
        var block = Assert.Single(body.GetProperty("content").EnumerateArray());
        Assert.Equal(("text", "Hi! My name is Claude."), (block.GetProperty("type").GetString(), block.GetProperty("text").GetString()));
        Assert.Equal("end_turn", body.GetProperty("stop_reason").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("stop_sequence").ValueKind);
        Assert.Equal((12, 6), (body.GetProperty("usage").GetProperty("input_tokens").GetInt32(), body.GetProperty("usage").GetProperty("output_tokens").GetInt32()));

        var call = Assert.Single(_model.Calls);
        Assert.Equal([ChatRole.System, ChatRole.User], call.Messages.Select(m => m.Role));
        Assert.Equal(("You are a friendly assistant.", "Hello, Claude"), (call.Messages[0].Text, call.Messages[1].Text));
        Assert.Equal((1024, 0.5f, 40), (call.Options!.MaxOutputTokens, call.Options.Temperature, call.Options.TopK));
        Assert.Equal(["END"], call.Options.StopSequences!);
        Assert.Empty(_provider.Received);
        await AssertNoKeyAskedAsync();
    }

    [Fact]
    public async Task Anthropic_content_blocks_system_blocks_images_and_earlier_turns_reach_the_model()
    {
        using var response = await PostAsync("/__bohm/llm/api.anthropic.com/v1/messages", """
            {"model":"m","max_tokens":64,
             "system":[{"type":"text","text":"Be brief."},{"type":"text","text":"Answer in Korean."}],
             "messages":[
               {"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}},{"type":"text","text":"What is this?"}]},
               {"role":"assistant","content":[{"type":"thinking","thinking":"hmm","signature":"x"},{"type":"text","text":"A picture."}]},
               {"role":"user","content":"Describe it."}]}
            """);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var messages = Assert.Single(_model.Calls).Messages;
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User], messages.Select(m => m.Role));
        Assert.Equal("Be brief.\n\nAnswer in Korean.", messages[0].Text);
        var image = Assert.IsType<DataContent>(messages[1].Contents[0]);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(Convert.FromBase64String("iVBORw0KGgo="), image.Data.ToArray());
        Assert.Equal("A picture.", Assert.Single(messages[2].Contents).As<TextContent>().Text); // earlier reasoning left out
    }

    [Fact]
    public async Task Anthropic_a_streamed_answer_follows_the_messages_event_sequence()
    {
        _model.Chunks = ["Hel", "lo", "!"];

        using var response = await PostAsync("/__bohm/llm/api.anthropic.com/v1/messages",
            """{"model":"claude-sonnet-4-5","max_tokens":256,"stream":true,"messages":[{"role":"user","content":"Hello"}]}""", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var events = (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('\n'))
            .Select(lines => (Name: lines[0]["event: ".Length..], Data: JsonDocument.Parse(lines[1]["data: ".Length..]).RootElement))
            .ToList();

        Assert.Equal(
            ["message_start", "content_block_start", "content_block_delta", "content_block_delta", "content_block_delta", "content_block_stop", "message_delta", "message_stop"],
            events.Select(e => e.Name));
        Assert.All(events, e => Assert.Equal(e.Name, e.Data.GetProperty("type").GetString()));
        Assert.StartsWith("msg_", events[0].Data.GetProperty("message").GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal("Hello!", string.Concat(events.Where(e => e.Name == "content_block_delta").Select(e => e.Data.GetProperty("delta").GetProperty("text").GetString())));
        Assert.All(events.Where(e => e.Name.StartsWith("content_block", StringComparison.Ordinal)), e => Assert.Equal(0, e.Data.GetProperty("index").GetInt32()));
        Assert.Equal("end_turn", events[^2].Data.GetProperty("delta").GetProperty("stop_reason").GetString());
    }

    [Fact]
    public async Task Anthropic_the_applications_tools_are_declared_and_a_call_returns_as_tool_use()
    {
        _model.Call = new FunctionCallContent("toolu_01", "get_weather", new Dictionary<string, object?> { ["location"] = "Seoul" });

        using var response = await PostAsync("/__bohm/llm/api.anthropic.com/v1/messages", """
            {"model":"m","max_tokens":1024,
             "tools":[{"name":"get_weather","description":"Get the current weather","input_schema":{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}},
                      {"type":"web_search_20250305","name":"web_search"}],
             "messages":[
               {"role":"user","content":"Weather in Paris?"},
               {"role":"assistant","content":[{"type":"tool_use","id":"toolu_00","name":"get_weather","input":{"location":"Paris"}}]},
               {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_00","content":"15 degrees"}]},
               {"role":"user","content":"And Seoul?"}]}
            """);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var call = Assert.Single(_model.Calls);
        Assert.Equal("get_weather", Assert.Single(call.Options!.Tools!).Name); // the provider's own server tool is not declared
        var earlier = Assert.IsType<FunctionCallContent>(Assert.Single(call.Messages[1].Contents));
        Assert.Equal(("toolu_00", "Paris"), (earlier.CallId, ((JsonElement)earlier.Arguments!["location"]!).GetString()));
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(call.Messages[2].Contents));
        Assert.Equal(("toolu_00", "15 degrees"), (result.CallId, result.Result as string));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var use = Assert.Single(body.GetProperty("content").EnumerateArray());
        Assert.Equal(("tool_use", "toolu_01", "get_weather"), (use.GetProperty("type").GetString(), use.GetProperty("id").GetString(), use.GetProperty("name").GetString()));
        Assert.Equal("Seoul", use.GetProperty("input").GetProperty("location").GetString());
        Assert.Equal("tool_use", body.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public async Task Anthropic_counting_tokens_gets_a_provider_shaped_error_not_a_request_for_a_key()
    {
        using var response = await PostAsync("/__bohm/llm/api.anthropic.com/v1/messages/count_tokens",
            """{"model":"m","messages":[{"role":"user","content":"Hello"}]}""");

        HttpAssert.Status(HttpStatusCode.NotImplemented, response);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("error", body.GetProperty("type").GetString());
        Assert.Equal("bohm_local_model_unsupported", body.GetProperty("error").GetProperty("type").GetString());
        Assert.Empty(_model.Calls);
        await AssertNoKeyAskedAsync();
    }

    [Fact]
    public async Task Anthropic_a_block_the_model_cannot_read_gets_an_invalid_request_error()
    {
        using var response = await PostAsync("/__bohm/llm/api.anthropic.com/v1/messages",
            """{"model":"m","max_tokens":10,"messages":[{"role":"user","content":[{"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"JVBERg=="}}]}]}""");

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("bohm_invalid_request", error.GetProperty("type").GetString());
        Assert.Contains("document", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(_model.Calls);
    }

    [Fact]
    public async Task Gemini_generate_content_is_answered_in_the_gemini_shape()
    {
        _model.Reply = "Meow. AI learns patterns from data.";
        _model.Usage = new UsageDetails { InputTokenCount = 9, OutputTokenCount = 7, TotalTokenCount = 16 };

        using var response = await PostAsync("/__bohm/llm/generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", """
            {"system_instruction":{"parts":[{"text":"You are a cat."}]},
             "contents":[{"parts":[{"text":"Explain how AI works"}]}],
             "generationConfig":{"temperature":0.3,"maxOutputTokens":100,"topP":0.9,"stopSequences":["Title"]}}
            """);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var candidate = Assert.Single(body.GetProperty("candidates").EnumerateArray());
        Assert.Equal("model", candidate.GetProperty("content").GetProperty("role").GetString());
        Assert.Equal("Meow. AI learns patterns from data.", Assert.Single(candidate.GetProperty("content").GetProperty("parts").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal("STOP", candidate.GetProperty("finishReason").GetString());
        var usage = body.GetProperty("usageMetadata");
        Assert.Equal((9, 7, 16), (usage.GetProperty("promptTokenCount").GetInt32(), usage.GetProperty("candidatesTokenCount").GetInt32(), usage.GetProperty("totalTokenCount").GetInt32()));
        Assert.Equal("gemini-2.5-flash", body.GetProperty("modelVersion").GetString());

        var call = Assert.Single(_model.Calls);
        Assert.Equal([ChatRole.System, ChatRole.User], call.Messages.Select(m => m.Role));
        Assert.Equal("You are a cat.", call.Messages[0].Text);
        Assert.Equal((0.3f, 100, 0.9f), (call.Options!.Temperature, call.Options.MaxOutputTokens, call.Options.TopP));
        Assert.Equal(["Title"], call.Options.StopSequences!);
        Assert.Empty(_provider.Received);
        await AssertNoKeyAskedAsync();
    }

    [Fact]
    public async Task Gemini_a_stream_with_alt_sse_is_server_sent_events_ending_with_the_finish_reason()
    {
        _model.Chunks = ["Once ", "upon ", "a time"];

        using var response = await PostAsync("/__bohm/llm/generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse",
            """{"contents":[{"role":"user","parts":[{"text":"Tell a story"}]},{"role":"model","parts":[{"text":"Sure."}]},{"role":"user","parts":[{"text":"Go on"}]}]}""",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var chunks = (await response.Content.ReadAsStringAsync()).Split("\r\n\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(e => JsonDocument.Parse(e["data: ".Length..]).RootElement)
            .Select(e => Assert.Single(e.GetProperty("candidates").EnumerateArray()))
            .ToList();
        Assert.Equal("Once upon a time", string.Concat(chunks.SelectMany(c => c.GetProperty("content").GetProperty("parts").EnumerateArray()).Select(p => p.GetProperty("text").GetString())));
        Assert.Equal("STOP", chunks[^1].GetProperty("finishReason").GetString());
        Assert.All(chunks[..^1], c => Assert.False(c.TryGetProperty("finishReason", out _)));
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], Assert.Single(_model.Calls).Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Gemini_a_stream_without_alt_sse_is_one_json_array()
    {
        _model.Chunks = ["a", "b"];

        using var response = await PostAsync("/__bohm/llm/generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent",
            """{"contents":[{"parts":[{"text":"hi"}]}]}""", HttpCompletionOption.ResponseHeadersRead);

        var array = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.Equal(3, array.GetArrayLength());
        Assert.Equal("STOP", Assert.Single(array[2].GetProperty("candidates").EnumerateArray()).GetProperty("finishReason").GetString());
    }

    [Fact]
    public async Task Gemini_json_output_images_and_functions_reach_the_model_in_its_terms()
    {
        _model.Call = new FunctionCallContent("find_book", "find_book", new Dictionary<string, object?> { ["title"] = "Dune" });

        using var response = await PostAsync("/__bohm/llm/generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", """
            {"contents":[{"role":"user","parts":[{"inline_data":{"mime_type":"image/jpeg","data":"/9j/4AAQ"}},{"text":"Which book?"}]}],
             "tools":[{"functionDeclarations":[{"name":"find_book","description":"Find a book","parameters":{"type":"OBJECT","properties":{"title":{"type":"STRING"}}}}]}],
             "generationConfig":{"responseMimeType":"application/json","responseSchema":{"type":"OBJECT","properties":{"title":{"type":"STRING"}}}}}
            """);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var call = Assert.Single(_model.Calls);
        var image = Assert.IsType<DataContent>(call.Messages[0].Contents[0]);
        Assert.Equal("image/jpeg", image.MediaType);
        var tool = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(call.Options!.Tools!));
        Assert.Equal("object", tool.JsonSchema.GetProperty("type").GetString());
        var format = Assert.IsType<ChatResponseFormatJson>(call.Options.ResponseFormat);
        Assert.Equal("string", format.Schema!.Value.GetProperty("properties").GetProperty("title").GetProperty("type").GetString());

        var part = Assert.Single(Assert.Single(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("candidates").EnumerateArray())
            .GetProperty("content").GetProperty("parts").EnumerateArray());
        Assert.Equal(("find_book", "Dune"), (part.GetProperty("functionCall").GetProperty("name").GetString(), part.GetProperty("functionCall").GetProperty("args").GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Gemini_counting_tokens_gets_a_provider_shaped_error_not_a_request_for_a_key()
    {
        using var response = await PostAsync("/__bohm/llm/generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:countTokens",
            """{"contents":[{"parts":[{"text":"hi"}]}]}""");

        HttpAssert.Status(HttpStatusCode.NotImplemented, response);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal((501, "UNIMPLEMENTED"), (error.GetProperty("code").GetInt32(), error.GetProperty("status").GetString()));
        await AssertNoKeyAskedAsync();
    }

    [Fact]
    public async Task With_a_key_connected_gemini_requests_go_to_the_provider_not_the_local_model()
    {
        using (var put = await _host.ControlClient().PutAsync("/__control/llm/google/key", new StringContent("AIza-real-key-0123456789")))
            HttpAssert.Status(HttpStatusCode.OK, put);

        using var response = await PostAsync("/__bohm/llm/generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
            """{"contents":[{"parts":[{"text":"hi"}]}]}""");

        Assert.Single(_provider.Received);
        Assert.Empty(_model.Calls);
    }

    [Theory]
    [InlineData("api.anthropic.com/v1/messages", """{"model":"m","max_tokens":10,"messages":[{"role":"user","content":"hi"}]}""", "None")]
    [InlineData("api.anthropic.com/v1/messages", """{"model":"m","max_tokens":9000,"thinking":{"type":"enabled","budget_tokens":4000},"messages":[{"role":"user","content":"hi"}]}""", "Medium")]
    [InlineData("generativelanguage.googleapis.com/v1beta/models/g:generateContent", """{"contents":[{"parts":[{"text":"hi"}]}]}""", "None")]
    [InlineData("generativelanguage.googleapis.com/v1beta/models/g:generateContent", """{"contents":[{"parts":[{"text":"hi"}]}],"generationConfig":{"thinkingConfig":{"thinkingBudget":-1}}}""", "Medium")]
    [InlineData("generativelanguage.googleapis.com/v1beta/models/g:generateContent", """{"contents":[{"parts":[{"text":"hi"}]}],"generationConfig":{"thinkingConfig":{"thinkingLevel":"HIGH"}}}""", "High")]
    public async Task Thinking_follows_what_the_request_asks_and_is_off_when_it_asks_nothing(string path, string body, string effort)
    {
        using var response = await PostAsync("/__bohm/llm/" + path, body);

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal(Enum.Parse<ReasoningEffort>(effort), Assert.Single(_model.Calls).Options!.Reasoning!.Effort);
    }

    [Theory]
    [InlineData("api.anthropic.com/v1/messages", """{"model":"m","max_tokens":48,"temperature":0,"messages":[{"role":"user","content":"Reply with one word: the capital of France."}]}""")]
    [InlineData("generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", """{"contents":[{"parts":[{"text":"Reply with one word: the capital of France."}]}],"generationConfig":{"temperature":0,"maxOutputTokens":48}}""")]
    public async Task A_real_model_answers_anthropic_and_gemini_requests(string path, string body)
    {
        var gguf = Environment.GetEnvironmentVariable("BOHM_TEST_GGUF");
        var server = Environment.GetEnvironmentVariable("BOHM_TEST_LLAMA_SERVER");
        Assert.SkipWhen(string.IsNullOrEmpty(gguf) || string.IsNullOrEmpty(server), "BOHM_TEST_GGUF and BOHM_TEST_LLAMA_SERVER are not set.");

        await using var host = await RunningHost.StartAsync(configure: o => o with
        {
            LocalModel = new LocalModelOptions { ModelPath = gguf!, ServerPath = server, ContextLength = 2048 },
        });
        var app = await host.AdoptAsync("<p>x</p>");
        using var load = await host.ClientForApp(app).GetAsync("/");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/llm/" + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", Assert.Single(load.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        using var client = host.ClientForApp(app);
        client.Timeout = TimeSpan.FromMinutes(5);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Contains("Paris", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("api.anthropic.com/v1/messages", """{"model":"m","max_tokens":10,"messages":[{"role":"user","content":"hi"}]}""")]
    [InlineData("generativelanguage.googleapis.com/v1beta/models/g:generateContent", """{"contents":[{"parts":[{"text":"hi"}]}]}""")]
    [InlineData("api.openai.com/v1/chat/completions", """{"model":"m","messages":[{"role":"user","content":"hi"}]}""")]
    public async Task A_model_that_stops_without_an_answer_gets_a_provider_shaped_error_not_an_empty_500(string path, string body)
    {
        _model.Failure = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.");

        using var response = await PostAsync("/__bohm/llm/" + path, body);

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Contains("did not finish", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("generativelanguage.googleapis.com/v1beta/models/g:generateContent", """{"contents":[{"parts":[{"text":"hi"}]}]}""", LlmProxy.DefaultLocalMaxOutputTokens)]
    [InlineData("generativelanguage.googleapis.com/v1beta/models/g:generateContent", """{"contents":[{"parts":[{"text":"hi"}]}],"generationConfig":{"maxOutputTokens":4000}}""", 4000)]
    [InlineData("api.openai.com/v1/chat/completions", """{"model":"m","messages":[{"role":"user","content":"hi"}]}""", LlmProxy.DefaultLocalMaxOutputTokens)]
    [InlineData("api.anthropic.com/v1/messages", """{"model":"m","max_tokens":20,"messages":[{"role":"user","content":"hi"}]}""", 20)]
    public async Task A_request_without_a_length_limit_gets_the_local_default_and_one_with_a_limit_keeps_it(string path, string body, int expected)
    {
        using var response = await PostAsync("/__bohm/llm/" + path, body);

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal(expected, Assert.Single(_model.Calls).Options!.MaxOutputTokens);
    }

    [Fact]
    public async Task An_answer_in_audio_gets_a_provider_shaped_error_not_text_without_the_audio()
    {
        using var response = await PostAsync("/__bohm/llm/api.openai.com/v1/chat/completions",
            """{"model":"gpt-4o-audio-preview","modalities":["text","audio"],"audio":{"voice":"ash","format":"wav"},"messages":[{"role":"user","content":"Say hi"}]}""");

        HttpAssert.Status(HttpStatusCode.NotImplemented, response);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("bohm_local_model_unsupported", error.GetProperty("type").GetString());
        Assert.Empty(_model.Calls);
    }

    private async Task AssertNoKeyAskedAsync()
    {
        var status = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{_app}/status")).RootElement;
        Assert.Empty(status.GetProperty("needsKey").EnumerateArray());
    }

    private Task<HttpResponseMessage> PostAsync(string path, string body, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", _cookie);
        return _host.ClientForApp(_app).SendAsync(request, completion);
    }
}

internal static class ContentExtensions
{
    public static T As<T>(this AIContent content) where T : AIContent => Assert.IsType<T>(content);
}
