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
        using var proposed = JsonDocument.Parse(_server.Received.First().Body);
        Assert.Equal("none", proposed.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(1024, AnswerBound(proposed.RootElement));
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
        var request = Assert.Single(_server.Received);
        Assert.Equal(("GET", "/v1/models"), (request.Method, request.PathAndQuery));
        Assert.Equal($"Bearer {ServerKey}", request.Headers["Authorization"]);

        _server.Refusal = (200, """{"object":"list","data":[{"id":"other-model"}]}""");
        Assert.False((await CheckAsync(host)).GetProperty("modelListed").GetBoolean());
        _server.Refusal = (200, "not a list");
        Assert.Equal(JsonValueKind.Null, (await CheckAsync(host)).GetProperty("modelListed").ValueKind);

        // Nothing of an application's went, but the requests left this computer.
        var sent = Assert.Single(JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(_server.Address.Authority, sent.GetProperty("host").GetString());
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

        using var closed = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop(); // nothing listens there now
        using (var set = await SetAsync(host, $"http://127.0.0.1:{port}/v1", "m")) HttpAssert.Status(HttpStatusCode.OK, set);
        var check = await CheckAsync(host);
        Assert.Equal("unreachable", check.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Null, check.GetProperty("status").ValueKind);
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
    public async Task By_default_proposals_are_made_by_the_server_ahead_of_the_model_on_this_computer()
    {
        await using var host = await StartAsync(fixedAtStart: true, withLocalModel: true);
        var model = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/edit/model")).RootElement;
        Assert.Equal(JsonValueKind.Null, model.GetProperty("missing").ValueKind);

        using var response = await host.ControlClient().PostAsync($"/__control/apps/{await host.AdoptAsync(App)}/proposals", new StringContent(
            """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
            Encoding.UTF8, "application/json"));

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal("company/fixed-model", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("model").GetString());
        Assert.Equal("/v1/chat/completions", Assert.Single(_server.Received).PathAndQuery);
        Assert.Empty(_local.Calls);
        var sent = Assert.Single(JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(_server.Address.Authority, sent.GetProperty("host").GetString());
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

    private async Task<RunningHost> StartAsync(bool fixedAtStart, bool withLocalModel = false)
    {
        var endpoint = new Uri(_server.Address, "v1/");
        var host = await RunningHost.StartAsync(configure: o => o with
        {
            CompanyModel = fixedAtStart ? new CompanyModelOptions(endpoint, "fixed-model") : null,
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
