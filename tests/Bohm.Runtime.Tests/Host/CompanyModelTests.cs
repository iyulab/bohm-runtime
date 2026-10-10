using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The organization's model server: set by the person or fixed at start, it answers an application's
/// AI requests for a provider with no key connected and makes proposals — ahead of a model on this
/// computer — and every request to it is counted as sent to its host.
/// </summary>
public sealed class CompanyModelTests : IAsyncLifetime
{
    private const string App = """<!doctype html><title>Tasks</title><button onclick="add()">Add Task</button>""";
    private const string ServerKey = "org-key-0123456789";

    private FakeProvider _server = null!;
    private FakeChatModel _local = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await FakeProvider.StartAsync();
        _local = new FakeChatModel();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Theory]
    [InlineData("http://models.example:8000/v1", "qwen", "http://models.example:8000/v1/", "qwen")]
    [InlineData(" https://models.example/v1/ ", " qwen ", "https://models.example/v1/", "qwen")]
    public void A_usable_address_is_kept_with_a_trailing_slash_and_the_name_trimmed(string endpoint, string model, string expectedEndpoint, string expectedModel)
    {
        Assert.True(CompanyModelOptions.TryCreate(endpoint, model, out var options));
        Assert.Equal(expectedEndpoint, options!.Endpoint.AbsoluteUri);
        Assert.Equal(expectedModel, options.Model);
    }

    [Theory]
    [InlineData("models.example:8000/v1", "qwen")] // not an absolute address
    [InlineData("ftp://models.example/v1", "qwen")]
    [InlineData("http://user:secret@models.example/v1", "qwen")] // a secret in the address would be kept in plain text
    [InlineData("http://models.example/v1?key=1", "qwen")]
    [InlineData("http://models.example/v1", " ")]
    [InlineData(null, "qwen")]
    public void An_unusable_address_or_no_model_name_is_refused(string? endpoint, string? model) =>
        Assert.False(CompanyModelOptions.TryCreate(endpoint, model, out _));

    [Fact]
    public async Task A_server_set_by_the_person_is_remembered_across_launches_and_can_be_set_away()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-company-model-").FullName;
        var first = await RunningHost.StartAsync(dataRoot);
        var none = await GetAsync(first);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("endpoint").ValueKind);
        Assert.False(none.GetProperty("fixed").GetBoolean());

        using (var bad = await SetAsync(first, "ftp://models.example/v1", "qwen")) HttpAssert.Status(HttpStatusCode.BadRequest, bad);
        using (var set = await SetAsync(first, "http://models.example:8000/v1", "qwen"))
        {
            HttpAssert.Status(HttpStatusCode.OK, set);
            Assert.Equal("http://models.example:8000/v1/", JsonDocument.Parse(await set.Content.ReadAsStringAsync()).RootElement.GetProperty("endpoint").GetString());
        }

        Assert.All(JsonDocument.Parse(await first.ControlClient().GetStringAsync("/__control/llm")).RootElement.EnumerateArray(),
            p => Assert.Equal("company", p.GetProperty("answeredBy").GetString()));
        await first.StopKeepingDataAsync();

        await using var second = await RunningHost.StartAsync(dataRoot);
        var remembered = await GetAsync(second);
        Assert.Equal("http://models.example:8000/v1/", remembered.GetProperty("endpoint").GetString());
        Assert.Equal("qwen", remembered.GetProperty("model").GetString());

        using var cleared = await second.ControlClient().DeleteAsync("/__control/llm/company-model");
        HttpAssert.Status(HttpStatusCode.OK, cleared);
        Assert.False(File.Exists(Path.Combine(dataRoot, "company-model.json")));
        Assert.All(JsonDocument.Parse(await second.ControlClient().GetStringAsync("/__control/llm")).RootElement.EnumerateArray(),
            p => Assert.Equal(JsonValueKind.Null, p.GetProperty("answeredBy").ValueKind));
    }

    [Fact]
    public async Task The_models_limits_set_with_the_server_are_remembered_and_unusable_ones_refused()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-company-model-").FullName;
        var first = await RunningHost.StartAsync(dataRoot);
        Assert.Equal(JsonValueKind.Null, (await GetAsync(first)).GetProperty("contextWindow").ValueKind);

        using (var bad = await SetAsync(first, """{"endpoint":"http://models.example/v1","model":"qwen","contextWindow":4096,"maxTokens":8192}""")) HttpAssert.Status(HttpStatusCode.BadRequest, bad);
        using (var bad = await SetAsync(first, """{"endpoint":"http://models.example/v1","model":"qwen","reasoning":"yes"}""")) HttpAssert.Status(HttpStatusCode.BadRequest, bad);
        using (var set = await SetAsync(first, """{"endpoint":"http://models.example/v1","model":"qwen","contextWindow":32768,"maxTokens":4096,"reasoning":true}"""))
            HttpAssert.Status(HttpStatusCode.OK, set);
        await first.StopKeepingDataAsync();

        await using var second = await RunningHost.StartAsync(dataRoot);
        var remembered = await GetAsync(second);
        Assert.Equal(32768, remembered.GetProperty("contextWindow").GetInt32());
        Assert.Equal(4096, remembered.GetProperty("maxTokens").GetInt32());
        Assert.True(remembered.GetProperty("reasoning").GetBoolean());

        // Set again without them: unknown again.
        using (var plain = await SetAsync(second, "http://models.example/v1", "qwen")) HttpAssert.Status(HttpStatusCode.OK, plain);
        Assert.Equal(JsonValueKind.Null, (await GetAsync(second)).GetProperty("reasoning").ValueKind);
    }

    [Fact]
    public async Task A_known_answer_bound_lowers_an_applications_larger_one_and_a_thinking_model_is_asked_not_to_think_for_a_proposal()
    {
        await using var host = await StartAsync(fixedAtStart: false);
        using (var set = await SetAsync(host, $$"""{"endpoint":"{{_server.Address.AbsoluteUri}}v1","model":"set-model","maxTokens":1024,"reasoning":true}"""))
            HttpAssert.Status(HttpStatusCode.OK, set);
        var app = await host.AdoptAsync(App);

        using (var response = await PostFromAppAsync(host, app, "/__bohm/llm/api.openai.com/v1/chat/completions",
            """{"model":"gpt-4o-mini","max_tokens":4000,"messages":[{"role":"user","content":"Hello?"}]}"""))
            HttpAssert.Status(HttpStatusCode.OK, response);
        using (var asked = JsonDocument.Parse(Assert.Single(_server.Received).Body))
            Assert.Equal(1024, AnswerBound(asked.RootElement));

        _server.Received.Clear();
        using (var proposal = await host.ControlClient().PostAsync($"/__control/apps/{app}/proposals", new StringContent(
            """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
            Encoding.UTF8, "application/json")))
            HttpAssert.Status(HttpStatusCode.OK, proposal);
        using var proposed = JsonDocument.Parse(_server.Asked[0].Body);
        Assert.Equal("none", proposed.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(1024, AnswerBound(proposed.RootElement));
    }

    [Fact]
    public async Task A_model_that_thinks_unasked_is_found_out_by_one_short_question_before_the_first_long_task_and_remembered_across_launches()
    {
        // An administrator's listed server: its limits come from the list, so what is learned is kept apart from the choice.
        var dataRoot = Directory.CreateTempSubdirectory("bohm-company-thinks-").FullName;
        var endpoint = new Uri(_server.Address, "v1/");
        Func<Bohm.Runtime.Host.RuntimeHostOptions, Bohm.Runtime.Host.RuntimeHostOptions> listed = o => o with { CompanyModels = CompanyModelList.Of(new CompanyModelOptions(endpoint, "fixed-model")) };
        _server.Thinks = true;
        const string Edit = """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""";

        var first = await RunningHost.StartAsync(dataRoot, configure: listed);
        var app = await first.AdoptAsync(App);
        using (var response = await first.ControlClient().PostAsync($"/__control/apps/{app}/proposals", new StringContent(Edit, Encoding.UTF8, "application/json")))
            HttpAssert.Status(HttpStatusCode.OK, response);
        var question = Assert.Single(_server.ThinkingQuestions);
        Assert.True(AnswerBound(JsonDocument.Parse(question.Body).RootElement) is > 0 and <= 64);
        Assert.DoesNotContain("Add Task", question.Body, StringComparison.Ordinal); // nothing of the application's goes with it
        // The question came first, so the task itself already asks the model not to think.
        Assert.Equal("none", JsonDocument.Parse(_server.Asked[0].Body).RootElement.GetProperty("reasoning_effort").GetString());
        Assert.True(File.Exists(Path.Combine(dataRoot, "company-model-learned.json")));
        await first.StopKeepingDataAsync();

        _server.Received.Clear();
        await using var second = await RunningHost.StartAsync(dataRoot, configure: listed);
        using (var response = await second.ControlClient().PostAsync($"/__control/apps/{app}/proposals", new StringContent(Edit, Encoding.UTF8, "application/json")))
            HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Empty(_server.ThinkingQuestions); // known from the last launch
        Assert.Equal("none", JsonDocument.Parse(_server.Asked[0].Body).RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task A_model_whose_short_answer_carries_no_thinking_is_asked_once_and_tasks_go_as_they_are()
    {
        await using var host = await StartAsync(fixedAtStart: true);
        var app = await host.AdoptAsync(App);
        for (var i = 0; i < 2; i++)
            using (var response = await host.ControlClient().PostAsync($"/__control/apps/{app}/proposals", new StringContent(
                """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
                Encoding.UTF8, "application/json")))
                HttpAssert.Status(HttpStatusCode.OK, response);

        Assert.Single(_server.ThinkingQuestions); // once per server, model and key — not before every task
        Assert.All(_server.Asked, request => Assert.False(JsonDocument.Parse(request.Body).RootElement.TryGetProperty("reasoning_effort", out _)));
    }

    /// <summary>The answer bound a Chat Completions request asks for, by either of its names.</summary>
    private static int AnswerBound(JsonElement request) =>
        (request.TryGetProperty("max_completion_tokens", out var bound) || request.TryGetProperty("max_tokens", out bound)) ? bound.GetInt32() : -1;

    [Fact]
    public async Task A_server_fixed_at_start_cannot_be_changed_but_its_key_can_be_connected()
    {
        await using var host = await StartAsync(fixedAtStart: true);

        var state = await GetAsync(host);
        Assert.True(state.GetProperty("fixed").GetBoolean());
        Assert.Equal("fixed-model", state.GetProperty("model").GetString());
        using (var put = await SetAsync(host, "http://elsewhere.example/v1", "other")) HttpAssert.Status(HttpStatusCode.Conflict, put);
        using (var delete = await host.ControlClient().DeleteAsync("/__control/llm/company-model")) HttpAssert.Status(HttpStatusCode.Conflict, delete);

        using var key = await host.ControlClient().PutAsync("/__control/llm/company-model/key", new StringContent(ServerKey));
        HttpAssert.Status(HttpStatusCode.OK, key);
        Assert.True(JsonDocument.Parse(await key.Content.ReadAsStringAsync()).RootElement.GetProperty("keyConnected").GetBoolean());
    }

    [Fact]
    public async Task A_check_asks_the_server_for_its_models_with_the_key_and_says_whether_it_knows_the_model()
    {
        await using var host = await StartAsync(fixedAtStart: false);
        using (var key = await host.ControlClient().PutAsync("/__control/llm/company-model/key", new StringContent(ServerKey))) HttpAssert.Status(HttpStatusCode.OK, key);

        _server.Refusal = (200, """{"object":"list","data":[{"id":"other-model"},{"id":"set-model"}]}""");
        var listed = await CheckAsync(host);
        Assert.Equal("answers", listed.GetProperty("result").GetString());
        Assert.Equal(200, listed.GetProperty("status").GetInt32());
        Assert.True(listed.GetProperty("modelListed").GetBoolean());
        // The list once to tell how the server answered, and once more for the model's limits as IronHive reads them.
        Assert.All(_server.Received, request =>
        {
            Assert.Equal(("GET", "/v1/models"), (request.Method, request.PathAndQuery));
            Assert.Equal($"Bearer {ServerKey}", request.Headers["Authorization"]);
        });

        _server.Refusal = (200, """{"object":"list","data":[{"id":"other-model"}]}""");
        Assert.False((await CheckAsync(host)).GetProperty("modelListed").GetBoolean());
        _server.Refusal = (200, "not a list");
        Assert.Equal(JsonValueKind.Null, (await CheckAsync(host)).GetProperty("modelListed").ValueKind);

        // Nothing of an application's went, but the requests left this computer.
        var sent = Assert.Single(JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(_server.Address.Authority, sent.GetProperty("host").GetString());
    }

    [Fact]
    public async Task A_check_takes_the_context_window_a_vllm_server_reports_for_the_model()
    {
        await using var host = await StartAsync(fixedAtStart: false);
        _server.Models = """{"object":"list","data":[{"id":"other-model","object":"model","max_model_len":4096},{"id":"set-model","object":"model","owned_by":"vllm","max_model_len":32768}]}""";

        var check = await CheckAsync(host);

        Assert.Equal("answers", check.GetProperty("result").GetString());
        Assert.Equal(32768, check.GetProperty("reportedContextWindow").GetInt32());
        var state = await GetAsync(host);
        Assert.Equal(32768, state.GetProperty("reportedContextWindow").GetInt32());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("contextWindow").ValueKind); // as set: nobody set one
    }

    [Theory]
    [InlineData(401, "key-refused")]
    [InlineData(403, "key-refused")]
    [InlineData(404, "not-found")]
    [InlineData(500, "refused")]
    public async Task A_check_tells_a_refused_key_a_wrong_address_and_other_refusals_apart(int status, string result)
    {
        await using var host = await StartAsync(fixedAtStart: true);
        _server.Refusal = (status, """{"error":{"message":"no"}}""");
        var check = await CheckAsync(host);
        Assert.Equal(result, check.GetProperty("result").GetString());
        Assert.Equal(status, check.GetProperty("status").GetInt32());
        Assert.False(Assert.Single(_server.Received).Headers.ContainsKey("Authorization")); // no key connected, none made up
    }

    [Fact]
    public async Task A_check_of_a_server_that_does_not_answer_says_so_and_with_none_set_there_is_nothing_to_check()
    {
        await using var host = await RunningHost.StartAsync();
        using (var none = await host.ControlClient().PostAsync("/__control/llm/company-model/check", null)) HttpAssert.Status(HttpStatusCode.NotFound, none);

        // A port bound but not listening: a connection is refused, and no other test's server can take the port meanwhile
        // (a listener stopped right away let a parallel test's server bind the same port, and the check then answered).
        using var closed = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        closed.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)closed.LocalEndPoint!).Port;
        using (var set = await SetAsync(host, $"http://127.0.0.1:{port}/v1", "m")) HttpAssert.Status(HttpStatusCode.OK, set);
        var check = await CheckAsync(host);
        Assert.Equal("unreachable", check.GetProperty("result").GetString());
        Assert.Equal("connection-refused", check.GetProperty("unreached").GetString());
        Assert.Equal(JsonValueKind.Null, check.GetProperty("status").ValueKind);
    }

    [Fact]
    public async Task A_check_of_an_address_whose_name_does_not_resolve_says_so()
    {
        await using var host = await RunningHost.StartAsync();
        using (var set = await SetAsync(host, "http://models.invalid/v1", "m")) HttpAssert.Status(HttpStatusCode.OK, set);

        var check = await CheckAsync(host);

        Assert.Equal("unreachable", check.GetProperty("result").GetString());
        Assert.Equal("host-not-found", check.GetProperty("unreached").GetString());
    }

    [Fact]
    public void A_check_that_ran_out_of_time_says_no_answer_and_an_unknown_failure_says_nothing()
    {
        Assert.Equal("no-answer", CompanyModel.UnreachedOf(new TaskCanceledException()));
        Assert.Null(CompanyModel.UnreachedOf(new HttpRequestException("reset", new IOException("reset"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_a_providers_key_the_server_answers_the_applications_chat_request_and_it_is_counted_as_sent(bool withKey)
    {
        await using var host = await StartAsync(fixedAtStart: false, withLocalModel: true);
        if (withKey)
            using (var key = await host.ControlClient().PutAsync("/__control/llm/company-model/key", new StringContent(ServerKey))) HttpAssert.Status(HttpStatusCode.OK, key);
        var app = await host.AdoptAsync(App);

        using var response = await PostFromAppAsync(host, app, "/__bohm/llm/api.openai.com/v1/chat/completions",
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"Hello?"}]}""");

        HttpAssert.Status(HttpStatusCode.OK, response);
        var choice = Assert.Single(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("choices").EnumerateArray());
        Assert.Equal(FakeProvider.Reply, choice.GetProperty("message").GetProperty("content").GetString());

        var request = Assert.Single(_server.Received);
        Assert.Equal("/v1/chat/completions", request.PathAndQuery);
        Assert.Equal("set-model", JsonDocument.Parse(request.Body).RootElement.GetProperty("model").GetString()); // the server's model, not the one the app named
        if (withKey) Assert.Equal($"Bearer {ServerKey}", request.Headers["Authorization"]);
        else Assert.False(request.Headers.ContainsKey("Authorization")); // no made-up key is sent
        Assert.Empty(_local.Calls); // the organization's server comes first

        var sent = Assert.Single(JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(_server.Address.Authority, sent.GetProperty("host").GetString());
    }

    [Fact]
    public async Task Without_an_OpenAI_key_a_recording_sent_for_transcription_is_turned_into_text_by_the_servers_speech_model()
    {
        _server.Models = """{"object":"list","data":[{"id":"set-model","object":"model"},{"id":"whisper-large-v3-turbo","object":"model"}]}""";
        await using var host = await StartAsync(fixedAtStart: false);
        using (var key = await host.ControlClient().PutAsync("/__control/llm/company-model/key", new StringContent(ServerKey))) HttpAssert.Status(HttpStatusCode.OK, key);
        var app = await host.AdoptAsync(App);
        var recording = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x00, 0x01, 0x02 };

        using var response = await PostFormFromAppAsync(host, app, "/__bohm/llm/api.openai.com/v1/audio/transcriptions", recording, ("model", "whisper-1"), ("language", "ko"));

        HttpAssert.Status(HttpStatusCode.OK, response);
        var asked = Assert.Single(_server.Asked);
        Assert.Equal(("POST", "/v1/audio/transcriptions"), (asked.Method, asked.PathAndQuery));
        Assert.StartsWith("multipart/form-data", asked.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.Contains("whisper-large-v3-turbo", asked.Body, StringComparison.Ordinal); // the server's speech model, not the one the app named
        Assert.DoesNotContain("whisper-1", asked.Body, StringComparison.Ordinal);
        Assert.Contains("name=language", asked.Body, StringComparison.Ordinal);
        Assert.Contains("filename=recording.webm", asked.Body, StringComparison.Ordinal);
        Assert.Equal($"Bearer {ServerKey}", asked.Headers["Authorization"]);

        var sent = Assert.Single(JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(_server.Address.Authority, sent.GetProperty("host").GetString());
    }

    [Fact]
    public async Task A_server_that_lists_no_speech_model_is_not_sent_the_recording()
    {
        _server.Models = """{"object":"list","data":[{"id":"set-model","object":"model"}]}""";
        await using var host = await StartAsync(fixedAtStart: false);
        var app = await host.AdoptAsync(App);

        using var response = await PostFormFromAppAsync(host, app, "/__bohm/llm/api.openai.com/v1/audio/transcriptions", [1, 2, 3], ("model", "whisper-1"));

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        Assert.Equal("local_model_unavailable", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_server.Asked); // only the list was asked
    }

    [Theory]
    [InlineData("whisper-large-v3-turbo", true)]
    [InlineData("Systran/faster-whisper-small", true)]
    [InlineData("gpt-4o-mini-transcribe", true)]
    [InlineData("SenseVoiceSmall", true)]
    [InlineData("nvidia/parakeet-tdt-0.6b-v2", true)]
    [InlineData("ko-stt-base", true)]
    [InlineData("qwen3.8-27b", false)]
    [InlineData("qwen3-embedding-0.6b", false)]
    [InlineData("fastest-model", false)]
    [InlineData("lasr-7b", false)]
    public void A_speech_model_is_known_by_its_family_name(string id, bool speech) =>
        Assert.Equal(speech, CompanyModel.IsTranscriptionModel(id));

    [Fact]
    public async Task An_Anthropic_shaped_request_is_answered_by_the_server_in_the_Anthropic_shape()
    {
        await using var host = await StartAsync(fixedAtStart: true);
        var app = await host.AdoptAsync(App);

        using var response = await PostFromAppAsync(host, app, "/__bohm/llm/api.anthropic.com/v1/messages",
            """{"model":"claude-x","max_tokens":64,"messages":[{"role":"user","content":"Hello?"}]}""");

        HttpAssert.Status(HttpStatusCode.OK, response);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("message", body.GetProperty("type").GetString());
        Assert.Equal(FakeProvider.Reply, Assert.Single(body.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal("/v1/chat/completions", Assert.Single(_server.Received).PathAndQuery);
    }

    [Fact]
    public async Task When_the_server_refuses_the_application_gets_an_error_in_its_providers_shape()
    {
        await using var host = await StartAsync(fixedAtStart: true);
        _server.Refusal = (500, """{"error":{"message":"model overloaded"}}""");
        var app = await host.AdoptAsync(App);

        using var response = await PostFromAppAsync(host, app, "/__bohm/llm/api.openai.com/v1/chat/completions",
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"Hello?"}]}""");

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("bohm_company_model_failed", error.GetProperty("type").GetString());
        Assert.Contains("model overloaded", error.GetProperty("message").GetString(), StringComparison.Ordinal); // the server's own reason reaches the app
    }

    [Fact]
    public async Task A_connected_providers_key_still_goes_to_that_provider()
    {
        await using var host = await StartAsync(fixedAtStart: true);
        using (var key = await host.ControlClient().PutAsync("/__control/llm/openai/key", new StringContent("sk-real-0123456789"))) HttpAssert.Status(HttpStatusCode.OK, key);
        var providers = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm")).RootElement.EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal(JsonValueKind.Null, providers["openai"].GetProperty("answeredBy").ValueKind);
        Assert.Equal("company", providers["anthropic"].GetProperty("answeredBy").GetString());
    }

    [Fact]
    public async Task By_default_proposals_are_made_by_the_server_ahead_of_the_model_on_this_computer_which_is_first_asked_once_for_the_models_context_window_and_whether_it_thinks()
    {
        await using var host = await StartAsync(fixedAtStart: true, withLocalModel: true);
        _server.Models = """{"object":"list","data":[{"id":"fixed-model","object":"model","max_model_len":8192}]}""";
        var model = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/edit/model")).RootElement;
        Assert.Equal(JsonValueKind.Null, model.GetProperty("missing").ValueKind);

        var app = await host.AdoptAsync(App);
        using var response = await host.ControlClient().PostAsync($"/__control/apps/{app}/proposals", new StringContent(
            """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
            Encoding.UTF8, "application/json"));

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal("company/fixed-model", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("model").GetString());
        Assert.Equal([("GET", "/v1/models"), ("POST", "/v1/chat/completions"), ("POST", "/v1/chat/completions")], _server.Received.Select(r => (r.Method, r.PathAndQuery)));
        Assert.Single(_server.ThinkingQuestions);
        Assert.Equal(8192, (await GetAsync(host)).GetProperty("reportedContextWindow").GetInt32());
        Assert.Empty(_local.Calls);
        var sent = Assert.Single(JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(_server.Address.Authority, sent.GetProperty("host").GetString());

        // Asked once: the next proposal goes straight to the model.
        _server.Received.Clear();
        using (var again = await host.ControlClient().PostAsync($"/__control/apps/{app}/proposals", new StringContent(
            """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
            Encoding.UTF8, "application/json")))
            HttpAssert.Status(HttpStatusCode.OK, again);
        Assert.Equal("/v1/chat/completions", Assert.Single(_server.Received).PathAndQuery);
    }

    [Fact]
    public async Task When_the_server_refuses_a_proposal_the_person_gets_its_status_and_its_own_message()
    {
        await using var host = await StartAsync(fixedAtStart: true);
        _server.Refusal = (404, """{"error":{"message":"The model fixed-model does not exist.","type":"invalid_request_error"}}""");

        using var response = await host.ControlClient().PostAsync($"/__control/apps/{await host.AdoptAsync(App)}/proposals", new StringContent(
            """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
            Encoding.UTF8, "application/json"));

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var provider = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("provider");
        Assert.Equal(404, provider.GetProperty("status").GetInt32());
        Assert.Equal("The model fixed-model does not exist.", provider.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, provider.GetProperty("retryAfter").ValueKind);
        Assert.False(provider.TryGetProperty("billing", out _));
    }

    [Theory]
    // A busy server (503) and a rate limit (429), each with the hint of when to ask again.
    [InlineData(503)]
    [InlineData(429)]
    public async Task When_a_busy_server_says_when_to_ask_again_the_person_gets_that_wait(int status)
    {
        await using var host = await StartAsync(fixedAtStart: true);
        _server.Refusal = (status, """{"error":{"message":"busy"}}""");
        _server.RetryAfter = "7";

        using var response = await host.ControlClient().PostAsync($"/__control/apps/{await host.AdoptAsync(App)}/proposals", new StringContent(
            """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
            Encoding.UTF8, "application/json"));

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var provider = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("provider");
        Assert.Equal(status, provider.GetProperty("status").GetInt32());
        Assert.Equal(7, provider.GetProperty("retryAfter").GetInt32());
    }

    [Theory]
    // Out of credit: a 402, and an exhausted quota sent as a 429 — neither is a wait-and-retry.
    [InlineData(402, """{"error":{"message":"Insufficient balance.","type":"payment_required"}}""", "Insufficient balance.")]
    [InlineData(429, """{"error":{"message":"You exceeded your current quota.","type":"insufficient_quota","code":"insufficient_quota"}}""", "You exceeded your current quota.")]
    public async Task When_the_server_refuses_for_billing_the_person_is_told_it_is_billing_not_busy(int status, string body, string message)
    {
        await using var host = await StartAsync(fixedAtStart: true);
        _server.Refusal = (status, body);

        using var response = await host.ControlClient().PostAsync($"/__control/apps/{await host.AdoptAsync(App)}/proposals", new StringContent(
            """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
            Encoding.UTF8, "application/json"));

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var provider = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("provider");
        Assert.Equal(status, provider.GetProperty("status").GetInt32());
        Assert.Contains(message, provider.GetProperty("message").GetString());
        Assert.True(provider.GetProperty("billing").GetBoolean());
    }

    [Fact]
    public async Task When_the_server_fails_an_answer_it_had_started_the_person_is_told_it_failed_on_the_way_not_that_it_finished()
    {
        await using var host = await StartAsync(fixedAtStart: true);
        _server.StreamFailure = """{"error":{"message":"The server had an error while processing your request.","code":"server_error"}}""";

        using var response = await host.ControlClient().PostAsync($"/__control/apps/{await host.AdoptAsync(App)}/proposals", new StringContent(
            """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
            Encoding.UTF8, "application/json"));

        // An upstream that failed its response — a passing failure the shell tells as «try again shortly», not a finished proposal.
        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var provider = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("provider");
        Assert.Equal(502, provider.GetProperty("status").GetInt32());
        Assert.Contains("server_error", provider.GetProperty("message").GetString());
        Assert.False(provider.TryGetProperty("billing", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_applications_own_call_to_the_server_is_relayed_as_it_is_with_the_servers_key(bool withKey)
    {
        await using var host = await StartAsync(fixedAtStart: true);
        if (withKey)
            using (var key = await host.ControlClient().PutAsync("/__control/llm/company-model/key", new StringContent(ServerKey))) HttpAssert.Status(HttpStatusCode.OK, key);
        var app = await host.AdoptAsync(App);

        using var response = await PostFromAppAsync(host, app, "/__bohm/llm/company-model/chat/completions",
            """{"model":"the-apps-own-choice","messages":[{"role":"user","content":"Hello?"}]}""");

        HttpAssert.Status(HttpStatusCode.OK, response);
        var request = Assert.Single(_server.Received);
        Assert.Equal("/v1/chat/completions", request.PathAndQuery);
        Assert.Equal("the-apps-own-choice", JsonDocument.Parse(request.Body).RootElement.GetProperty("model").GetString()); // relayed, not bridged
        if (withKey) Assert.Equal($"Bearer {ServerKey}", request.Headers["Authorization"]);
        else Assert.False(request.Headers.ContainsKey("Authorization")); // the page's placeholder never goes on
        var sent = Assert.Single(JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(_server.Address.Authority, sent.GetProperty("host").GetString());
    }

    [Fact]
    public async Task Without_a_server_set_an_applications_call_to_it_has_nowhere_to_go()
    {
        await using var host = await RunningHost.StartAsync();
        var app = await host.AdoptAsync(App);

        using var response = await PostFromAppAsync(host, app, "/__bohm/llm/company-model/chat/completions", "{}");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
        Assert.Empty(_server.Received);
    }

    [Fact]
    public void A_listed_shape_keeps_its_openai_compatible_servers_and_models_in_order_and_leaves_out_what_cannot_be_used()
    {
        const string json = """
            {"providers":{
              "gpu":{"baseUrl":"http://models.example:8000/v1","api":"openai-completions","apiKey":"never-taken",
                "models":[{"id":"qwen3","name":"Qwen 3","contextWindow":32768,"maxTokens":8192,"reasoning":true,"input":["text","image","audio"]},
                          {"id":" "},{"name":"no id"},{"id":"qwen3","name":"again"},
                          {"id":"small","contextWindow":4096,"maxTokens":8192}]},
              "cloud":{"baseUrl":"https://api.anthropic.com","api":"anthropic-messages","models":[{"id":"claude"}]},
              "bad":{"baseUrl":"http://user:secret@models.example/v1","models":[{"id":"x"}]},
              "empty":{"baseUrl":"http://models.example/v1","models":[]},
              "GPU":{"baseUrl":"http://other.example/v1","models":[{"id":"y"}]},
              "cpu":{"baseUrl":"http://cpu.example/v1/","models":[{"id":"phi"}]}
            }}
            """;

        Assert.True(CompanyModelList.TryParse(json, out var list));
        Assert.Equal(["gpu", "cpu"], list!.Servers.Select(s => s.Name));
        Assert.Equal(["qwen3", "small", "phi"], list.Choices.Select(c => c.Model));

        var first = list.First;
        Assert.Equal(("gpu", "qwen3", "Qwen 3", "http://models.example:8000/v1/"), (first.Server, first.Model, first.DisplayName, first.Endpoint.AbsoluteUri));
        Assert.Equal(new ModelLimits(32768, 8192, true), first.Limits);
        Assert.Equal(["text", "image"], first.Input!);
        // An answer larger than the window makes no sense: that model's limits are unknown, the model is kept.
        Assert.Equal(ModelLimits.Unknown, list.Find("GPU", "small")!.Limits);
        Assert.Equal(["text"], list.Find("cpu", "phi")!.Input!);
        Assert.Null(list.Find("gpu", "claude"));
        Assert.DoesNotContain("never-taken", JsonSerializer.Serialize(list));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"gpu":{"baseUrl":"http://models.example/v1","models":[{"id":"x"}]}}""")]
    [InlineData("""{"providers":{"cloud":{"baseUrl":"https://api.anthropic.com","api":"anthropic-messages","models":[{"id":"claude"}]}}}""")]
    [InlineData("""{"providers":{"a/b":{"baseUrl":"http://models.example/v1","models":[{"id":"x"}]}}}""")]
    public void A_shape_with_nothing_usable_is_no_list(string json) =>
        Assert.False(CompanyModelList.TryParse(json, out _));

    [Fact]
    public async Task With_listed_servers_the_person_chooses_among_their_models_the_choice_is_remembered_and_a_model_no_longer_listed_falls_back_to_the_first()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-company-models-").FullName;
        var list = Listed($$$$"""
            {"providers":{
              "gpu":{"baseUrl":"{{{{_server.Address.AbsoluteUri}}}}v1","models":[{"id":"big","name":"Big","contextWindow":32768,"maxTokens":4096,"reasoning":true}]},
              "cpu":{"baseUrl":"{{{{_server.Address.AbsoluteUri}}}}v1","models":[{"id":"small"},{"id":"tiny","maxTokens":512}]}}}
            """);
        var first = await RunningHost.StartAsync(dataRoot, configure: o => o with { CompanyModels = list });

        var state = await GetAsync(first);
        Assert.True(state.GetProperty("fixed").GetBoolean());
        Assert.Equal(("gpu", "big", "Big", 32768), (state.GetProperty("server").GetString(), state.GetProperty("model").GetString(),
            state.GetProperty("name").GetString(), state.GetProperty("contextWindow").GetInt32()));
        Assert.Equal(["gpu/big", "cpu/small", "cpu/tiny"], state.GetProperty("choices").EnumerateArray()
            .Select(c => $"{c.GetProperty("server").GetString()}/{c.GetProperty("model").GetString()}"));

        using (var unlisted = await SetAsync(first, """{"server":"cpu","model":"big"}""")) HttpAssert.Status(HttpStatusCode.Conflict, unlisted);
        using (var byAddress = await SetAsync(first, "http://elsewhere.example/v1", "other")) HttpAssert.Status(HttpStatusCode.Conflict, byAddress);
        using (var none = await first.ControlClient().DeleteAsync("/__control/llm/company-model")) HttpAssert.Status(HttpStatusCode.Conflict, none);
        // The listed limits come with the choice; limits sent with it are not the person's to set here.
        using (var chosen = await SetAsync(first, """{"server":"CPU","model":"tiny","maxTokens":9000}"""))
        {
            HttpAssert.Status(HttpStatusCode.OK, chosen);
            var now = JsonDocument.Parse(await chosen.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(("cpu", "tiny", 512), (now.GetProperty("server").GetString(), now.GetProperty("model").GetString(), now.GetProperty("maxTokens").GetInt32()));
        }

        await first.StopKeepingDataAsync();

        await using (var second = await RunningHost.StartAsync(dataRoot, configure: o => o with { CompanyModels = list }))
            Assert.Equal("tiny", (await GetAsync(second)).GetProperty("model").GetString());

        var changed = Listed($$$$"""{"providers":{"gpu":{"baseUrl":"{{{{_server.Address.AbsoluteUri}}}}v1","models":[{"id":"big"}]}}}""");
        await using var third = await RunningHost.StartAsync(dataRoot, configure: o => o with { CompanyModels = changed });
        Assert.Equal(("gpu", "big"), ((await GetAsync(third)).GetProperty("server").GetString(), (await GetAsync(third)).GetProperty("model").GetString()));
    }

    [Fact]
    public async Task Each_listed_server_has_its_own_key_and_requests_go_with_the_key_of_the_server_in_use()
    {
        var list = Listed($$$$"""
            {"providers":{
              "gpu":{"baseUrl":"{{{{_server.Address.AbsoluteUri}}}}v1","models":[{"id":"big"}]},
              "cpu":{"baseUrl":"{{{{_server.Address.AbsoluteUri}}}}v1","models":[{"id":"small"}]}}}
            """);
        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = list });

        using (var key = await host.ControlClient().PutAsync("/__control/llm/company-model/key", new StringContent(ServerKey))) HttpAssert.Status(HttpStatusCode.OK, key);
        using (var chosen = await SetAsync(host, """{"server":"cpu","model":"small"}""")) HttpAssert.Status(HttpStatusCode.OK, chosen);

        var state = await GetAsync(host);
        Assert.False(state.GetProperty("keyConnected").GetBoolean());
        Assert.Equal([true, false], state.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("keyConnected").GetBoolean()));

        var app = await host.AdoptAsync(App);
        using (var response = await PostFromAppAsync(host, app, "/__bohm/llm/api.openai.com/v1/chat/completions",
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"Hello?"}]}"""))
            HttpAssert.Status(HttpStatusCode.OK, response);
        var request = Assert.Single(_server.Received);
        Assert.Equal("small", JsonDocument.Parse(request.Body).RootElement.GetProperty("model").GetString());
        Assert.False(request.Headers.ContainsKey("Authorization")); // the other server's key does not go

        _server.Received.Clear();
        using (var back = await SetAsync(host, """{"server":"gpu","model":"big"}""")) HttpAssert.Status(HttpStatusCode.OK, back);
        using (var response = await PostFromAppAsync(host, app, "/__bohm/llm/api.openai.com/v1/chat/completions",
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"Hello?"}]}"""))
            HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal($"Bearer {ServerKey}", Assert.Single(_server.Received).Headers["Authorization"]);
    }

    [Fact]
    public async Task The_models_of_a_server_not_yet_set_are_listed_by_id_with_the_context_each_reports_and_the_key_given()
    {
        await using var host = await RunningHost.StartAsync();
        _server.Models = """{"object":"list","data":[{"id":"zeta","object":"model","max_model_len":8192},{"id":"Alpha","object":"model"},{"id":"zeta","object":"model"}]}""";

        var listed = await ModelsAsync(host, new { endpoint = new Uri(_server.Address, "v1").AbsoluteUri, key = "listing-key" });

        Assert.Equal("answers", listed.GetProperty("result").GetString());
        var models = listed.GetProperty("models").EnumerateArray().Select(m => (m.GetProperty("id").GetString(), m.GetProperty("contextWindow").ValueKind == JsonValueKind.Null ? (int?)null : m.GetProperty("contextWindow").GetInt32())).ToList();
        Assert.Equal([("Alpha", (int?)null), ("zeta", 8192)], models);
        var request = Assert.Single(_server.Received);
        Assert.Equal(("GET", "/v1/models"), (request.Method, request.PathAndQuery));
        Assert.Equal("Bearer listing-key", request.Headers["Authorization"]);
        // Nothing is set by listing, and the request left this computer.
        Assert.Equal(JsonValueKind.Null, (await GetAsync(host)).GetProperty("endpoint").ValueKind);
        var sent = Assert.Single(JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(_server.Address.Authority, sent.GetProperty("host").GetString());
    }

    [Fact]
    public async Task With_no_address_the_server_in_use_is_listed_and_with_none_set_there_is_nothing_to_list()
    {
        await using (var empty = await RunningHost.StartAsync())
        using (var none = await empty.ControlClient().PostAsync("/__control/llm/company-model/models", null))
            HttpAssert.Status(HttpStatusCode.NotFound, none);

        await using var host = await StartAsync(fixedAtStart: false);
        _server.Models = """{"object":"list","data":[{"id":"set-model","object":"model"}]}""";
        _server.Received.Clear();
        var listed = await ModelsAsync(host, new { });
        Assert.Equal("set-model", Assert.Single(listed.GetProperty("models").EnumerateArray()).GetProperty("id").GetString());
        var asked = Assert.Single(_server.Received);
        // No key connected: none of the vault's — only the wire client's stand-in for «none» (the same as the server's chat requests).
        Assert.True(!asked.Headers.TryGetValue("Authorization", out var auth) || auth == "Bearer no-credential-required", auth);
    }

    [Theory]
    [InlineData(401, "key-refused")]
    [InlineData(404, "not-found")]
    [InlineData(500, "refused")]
    public async Task Listing_tells_a_refused_key_a_wrong_address_and_other_refusals_apart(int status, string result)
    {
        await using var host = await RunningHost.StartAsync();
        _server.Refusal = (status, """{"error":{"message":"no"}}""");
        var listed = await ModelsAsync(host, new { endpoint = new Uri(_server.Address, "v1").AbsoluteUri });
        Assert.Equal(result, listed.GetProperty("result").GetString());
        Assert.Equal(status, listed.GetProperty("status").GetInt32());
        Assert.Empty(listed.GetProperty("models").EnumerateArray());
    }

    [Theory]
    [InlineData("""{"endpoint":"ftp://example.test/v1"}""")]
    [InlineData("""{"endpoint":"https://user:pw@example.test/v1"}""")]
    [InlineData("""not json""")]
    public async Task Listing_refuses_an_address_it_would_not_set(string body)
    {
        await using var host = await RunningHost.StartAsync();
        using var refused = await host.ControlClient().PostAsync("/__control/llm/company-model/models", new StringContent(body, Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.BadRequest, refused);
        Assert.Empty(_server.Received);
    }

    private static async Task<JsonElement> ModelsAsync(RunningHost host, object body)
    {
        using var listed = await host.ControlClient().PostAsync("/__control/llm/company-model/models",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.OK, listed);
        return JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static CompanyModelList Listed(string json)
    {
        Assert.True(CompanyModelList.TryParse(json, out var list));
        return list!;
    }

    private async Task<RunningHost> StartAsync(bool fixedAtStart, bool withLocalModel = false)
    {
        var endpoint = new Uri(_server.Address, "v1/");
        var host = await RunningHost.StartAsync(configure: o => o with
        {
            CompanyModels = fixedAtStart ? CompanyModelList.Of(new CompanyModelOptions(endpoint, "fixed-model")) : null,
            LocalModel = withLocalModel ? new LocalModelOptions { ModelPath = "unused.gguf", Client = _local } : null,
        });
        if (!fixedAtStart)
            using (var set = await SetAsync(host, endpoint.AbsoluteUri, "set-model")) HttpAssert.Status(HttpStatusCode.OK, set);
        return host;
    }

    private static async Task<JsonElement> GetAsync(RunningHost host) =>
        JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/company-model")).RootElement;

    private static async Task<JsonElement> CheckAsync(RunningHost host)
    {
        using var check = await host.ControlClient().PostAsync("/__control/llm/company-model/check", null);
        HttpAssert.Status(HttpStatusCode.OK, check);
        return JsonDocument.Parse(await check.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> SetAsync(RunningHost host, string endpoint, string model) =>
        SetAsync(host, JsonSerializer.Serialize(new Dictionary<string, string> { ["endpoint"] = endpoint, ["model"] = model }));

    private static Task<HttpResponseMessage> SetAsync(RunningHost host, string body) =>
        host.ControlClient().PutAsync("/__control/llm/company-model", new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<HttpResponseMessage> PostFormFromAppAsync(RunningHost host, string app, string path, byte[] recording, params (string Name, string Value)[] fields)
    {
        using var load = await host.ClientForApp(app).GetAsync("/");
        var setCookie = Assert.Single(load.Headers.GetValues("Set-Cookie"));
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(recording);
        file.Headers.ContentType = new("audio/webm");
        form.Add(file, "file", "recording.webm");
        foreach (var (name, value) in fields) form.Add(new StringContent(value), name);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        request.Headers.Add("Cookie", setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)]);
        request.Headers.Authorization = new("Bearer", $"bohm-key-{app}");
        return await host.ClientForApp(app).SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostFromAppAsync(RunningHost host, string app, string path, string body)
    {
        using var load = await host.ClientForApp(app).GetAsync("/");
        var setCookie = Assert.Single(load.Headers.GetValues("Set-Cookie"));
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)]);
        request.Headers.Authorization = new("Bearer", $"bohm-key-{app}");
        return await host.ClientForApp(app).SendAsync(request);
    }
}
