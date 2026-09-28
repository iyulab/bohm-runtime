using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Which model proposes changes: the model on this computer by default, or a connected provider's
/// model the person chose — reached at the provider's OpenAI-compatible base with its key, and
/// counted as the application's data sent.
/// </summary>
public sealed class EditModelTests : IAsyncLifetime
{
    private const string App = """<!doctype html><title>Tasks</title><button onclick="add()">Add Task</button>""";
    private const string Key = "sk-real-secret-0123456789";

    private FakeProvider _provider = null!;
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync();
        _host = await RunningHost.StartAsync(configure: o => o with
        {
            LlmEndpoints = LlmProviders.All.ToDictionary(p => p.Host, _ => _provider.Address),
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task By_default_the_model_on_this_computer_proposes_and_without_one_the_proposal_says_so()
    {
        var chosen = await GetModelAsync();
        Assert.Equal(JsonValueKind.Null, chosen.GetProperty("provider").ValueKind);
        Assert.Equal("localModel", chosen.GetProperty("missing").GetProperty("needs").GetString());

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.Conflict, response);
        Assert.Equal("localModel", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("needs").GetString());
        Assert.Empty(_provider.Received);
    }

    [Fact]
    public async Task A_chosen_provider_without_its_key_is_missing_the_key_and_nothing_is_sent()
    {
        using var chose = await ChooseAsync("openai", "gpt-test");
        HttpAssert.Status(HttpStatusCode.OK, chose);
        var missing = JsonDocument.Parse(await chose.Content.ReadAsStringAsync()).RootElement.GetProperty("missing");
        Assert.Equal("key", missing.GetProperty("needs").GetString());
        Assert.Equal("openai", missing.GetProperty("provider").GetString());

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.Conflict, response);
        Assert.Equal("key", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("needs").GetString());
        Assert.Empty(_provider.Received);
        Assert.Empty(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
    }

    [Theory]
    [InlineData("openai", "/v1/chat/completions")]
    [InlineData("groq", "/openai/v1/chat/completions")]
    [InlineData("openrouter", "/api/v1/chat/completions")]
    [InlineData("mistral", "/v1/chat/completions")]
    public async Task A_chosen_providers_model_is_asked_at_its_OpenAI_compatible_base_with_its_key_and_it_is_counted_as_sent(string providerId, string path)
    {
        using (var connect = await _host.ControlClient().PutAsync($"/__control/llm/{providerId}/key", new StringContent(Key))) HttpAssert.Status(HttpStatusCode.OK, connect);
        using (var chose = await ChooseAsync(providerId, " model-x ")) HttpAssert.Status(HttpStatusCode.OK, chose);
        Assert.Equal(JsonValueKind.Null, (await GetModelAsync()).GetProperty("missing").ValueKind);

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.OK, response);
        var proposal = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal($"{providerId}/model-x", proposal.GetProperty("model").GetString());
        Assert.Equal(FakeProvider.Reply, proposal.GetProperty("summary").GetString());

        var request = Assert.Single(_provider.Received);
        Assert.Equal(path, request.PathAndQuery);
        Assert.Equal($"Bearer {Key}", request.Headers["Authorization"]); // every compatible base takes the key as a bearer token
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("model-x", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(["read_source", "replace"], body.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).Order());
        // Settings only the model on this computer is given — providers refuse the ones they do not know.
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));

        var sent = Assert.Single(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(LlmProviders.ById(providerId)!.Host, sent.GetProperty("host").GetString());
        Assert.Equal(1, sent.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Gemini_is_asked_through_its_own_API_with_its_key_and_it_is_counted_as_sent()
    {
        // Not the OpenAI-compatible base: Gemini's models sign their tool calls and refuse the next turn
        // without the signature, which that base drops. Its own API carries them.
        using (var connect = await _host.ControlClient().PutAsync("/__control/llm/google/key", new StringContent(Key))) HttpAssert.Status(HttpStatusCode.OK, connect);
        using (var chose = await ChooseAsync("google", "model-x")) HttpAssert.Status(HttpStatusCode.OK, chose);

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.OK, response);
        var proposal = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("google/model-x", proposal.GetProperty("model").GetString());
        Assert.Equal(FakeProvider.Reply, proposal.GetProperty("summary").GetString());

        var request = Assert.Single(_provider.Received);
        Assert.StartsWith("/v1beta/models/model-x:", request.PathAndQuery);
        Assert.Equal(Key, request.Headers["x-goog-api-key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(["read_source", "replace"], body.RootElement.GetProperty("tools").EnumerateArray()
            .SelectMany(t => t.GetProperty("functionDeclarations").EnumerateArray()).Select(f => f.GetProperty("name").GetString()).Order());

        var sent = Assert.Single(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal("generativelanguage.googleapis.com", sent.GetProperty("host").GetString());
        Assert.Equal(1, sent.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task OpenAI_is_asked_through_its_own_Responses_API_with_its_key_and_it_is_counted_as_sent()
    {
        // OpenAI's first-party surface: the Responses API, with the tools as top-level function tools.
        // TODO(upstream: docket #538): IronHive's OpenAI provider does not send store:false, so the Responses API
        // would keep the application's source. OpenAI stays on Chat Completions until a release that does; then
        // route "openai" to IronHive's OpenAIMessageGenerator in ProviderChoice.GetAsync and drop this skip.
        Assert.Skip("Waiting on docket #538: IronHive's OpenAI provider does not send store:false.");
        using (var connect = await _host.ControlClient().PutAsync("/__control/llm/openai/key", new StringContent(Key))) HttpAssert.Status(HttpStatusCode.OK, connect);
        using (var chose = await ChooseAsync("openai", "model-x")) HttpAssert.Status(HttpStatusCode.OK, chose);

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.OK, response);
        var proposal = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("openai/model-x", proposal.GetProperty("model").GetString());
        Assert.Equal(FakeProvider.Reply, proposal.GetProperty("summary").GetString());

        var request = Assert.Single(_provider.Received);
        Assert.Equal("/v1/responses", request.PathAndQuery);
        Assert.Equal($"Bearer {Key}", request.Headers["Authorization"]);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("model-x", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(["read_source", "replace"], body.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order());
        // The Responses API keeps what it is sent unless told not to; the application's source is sent to be
        // answered, not to be kept.
        Assert.True(body.RootElement.TryGetProperty("store", out var store), request.Body);
        Assert.False(store.GetBoolean());

        var sent = Assert.Single(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal("api.openai.com", sent.GetProperty("host").GetString());
        Assert.Equal(1, sent.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Anthropic_is_asked_through_its_own_Messages_API_with_its_key_and_it_is_counted_as_sent()
    {
        // Not the OpenAI-compatible base, which Anthropic keeps for evaluation only: its own API is the
        // one it supports, with the key in x-api-key and the tools as input schemas.
        using (var connect = await _host.ControlClient().PutAsync("/__control/llm/anthropic/key", new StringContent(Key))) HttpAssert.Status(HttpStatusCode.OK, connect);
        using (var chose = await ChooseAsync("anthropic", "model-x")) HttpAssert.Status(HttpStatusCode.OK, chose);

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.OK, response);
        var proposal = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("anthropic/model-x", proposal.GetProperty("model").GetString());
        Assert.Equal(FakeProvider.Reply, proposal.GetProperty("summary").GetString());

        var request = Assert.Single(_provider.Received);
        Assert.Equal("/v1/messages", request.PathAndQuery);
        Assert.Equal(Key, request.Headers["x-api-key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("model-x", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(["read_source", "replace"], body.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order());

        var sent = Assert.Single(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal("api.anthropic.com", sent.GetProperty("host").GetString());
        Assert.Equal(1, sent.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Anthropics_refusal_reaches_the_person_as_its_status_and_its_own_message()
    {
        using (var connect = await _host.ControlClient().PutAsync("/__control/llm/anthropic/key", new StringContent(Key))) HttpAssert.Status(HttpStatusCode.OK, connect);
        using (var chose = await ChooseAsync("anthropic", "model-x")) HttpAssert.Status(HttpStatusCode.OK, chose);
        _provider.Refusal = (404, """{"type":"error","error":{"type":"not_found_error","message":"model: model-x"}}""");

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var provider = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("provider");
        Assert.Equal(404, provider.GetProperty("status").GetInt32());
        Assert.Equal("model: model-x", provider.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Geminis_refusal_reaches_the_person_as_its_status_and_its_own_message()
    {
        using (var connect = await _host.ControlClient().PutAsync("/__control/llm/google/key", new StringContent(Key))) HttpAssert.Status(HttpStatusCode.OK, connect);
        using (var chose = await ChooseAsync("google", "model-x")) HttpAssert.Status(HttpStatusCode.OK, chose);
        _provider.Refusal = (404, """{"error":{"code":404,"message":"models/model-x is not found for API version v1beta.","status":"NOT_FOUND"}}""");

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var provider = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("provider");
        Assert.Equal(404, provider.GetProperty("status").GetInt32());
        Assert.Equal("models/model-x is not found for API version v1beta.", provider.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("""{"provider":"nowhere","model":"m"}""")]
    [InlineData("""{"provider":"openai","model":"  "}""")]
    [InlineData("""{"provider":"openai"}""")]
    [InlineData("not json")]
    public async Task A_choice_that_is_not_a_known_provider_and_a_model_name_is_refused(string body)
    {
        using var response = await _host.ControlClient().PutAsync("/__control/edit/model", new StringContent(body, Encoding.UTF8, "application/json"));

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Equal(JsonValueKind.Null, (await GetModelAsync()).GetProperty("provider").ValueKind);
    }

    [Fact]
    public async Task A_choice_is_remembered_across_launches_and_can_be_taken_back()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-edit-model-").FullName;
        try
        {
            var first = await RunningHost.StartAsync(dataRoot);
            using (var chose = await first.ControlClient().PutAsync("/__control/edit/model",
                new StringContent("""{"provider":"groq","model":"llama-x"}""", Encoding.UTF8, "application/json"))) HttpAssert.Status(HttpStatusCode.OK, chose);
            await first.StopKeepingDataAsync();

            await using var second = await RunningHost.StartAsync(dataRoot);
            var remembered = JsonDocument.Parse(await second.ControlClient().GetStringAsync("/__control/edit/model")).RootElement;
            Assert.Equal("groq", remembered.GetProperty("provider").GetString());
            Assert.Equal("llama-x", remembered.GetProperty("model").GetString());

            using var back = await second.ControlClient().DeleteAsync("/__control/edit/model");
            HttpAssert.Status(HttpStatusCode.OK, back);
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await back.Content.ReadAsStringAsync()).RootElement.GetProperty("provider").ValueKind);
            Assert.False(File.Exists(Path.Combine(dataRoot, "edit-model.json")));
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Theory]
    // The shapes providers refuse in: an object (OpenAI, Anthropic, most compatible bases) and a one-element
    // array (Gemini's OpenAI-compatible base).
    [InlineData("""{"error":{"message":"The model does not exist.","type":"invalid_request_error"}}""", 404, "The model does not exist.")]
    [InlineData("""[{"error":{"code":400,"message":"Function call is missing a thought_signature in functionCall parts.","status":"INVALID_ARGUMENT"}}]""", 400, "Function call is missing a thought_signature in functionCall parts.")]
    [InlineData("""not json""", 502, null)]
    public async Task A_providers_refusal_reaches_the_person_as_its_status_and_its_own_message(string refusal, int status, string? message)
    {
        using (var connect = await _host.ControlClient().PutAsync("/__control/llm/openai/key", new StringContent(Key))) HttpAssert.Status(HttpStatusCode.OK, connect);
        using (var chose = await ChooseAsync("openai", "model-x")) HttpAssert.Status(HttpStatusCode.OK, chose);
        _provider.Refusal = (status, refusal);

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var failure = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var provider = failure.GetProperty("provider");
        Assert.Equal(status, provider.GetProperty("status").GetInt32());
        if (message is null) Assert.Equal(JsonValueKind.Null, provider.GetProperty("message").ValueKind);
        else Assert.Equal(message, provider.GetProperty("message").GetString());
    }

    private async Task<JsonElement> GetModelAsync() => JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/edit/model")).RootElement;

    private Task<HttpResponseMessage> ChooseAsync(string provider, string model) =>
        _host.ControlClient().PutAsync("/__control/edit/model", new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["provider"] = provider, ["model"] = model }), Encoding.UTF8, "application/json"));

    private Task<HttpResponseMessage> ProposeAsync(string id) =>
        _host.ControlClient().PostAsync($"/__control/apps/{id}/proposals", new StringContent(
            """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
            Encoding.UTF8, "application/json"));
}
