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
    public async Task What_an_older_launch_remembered_becomes_the_answer_model_once_and_its_key_moves_where_a_providers_key_is_kept()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-company-model-").FullName;
        File.WriteAllText(Path.Combine(dataRoot, "company-model.json"),
            """{"format":"bohm.company-model/0","endpoint":"http://models.example:8000/v1/","model":"qwen","contextWindow":32768,"maxTokens":4096,"reasoning":true}""");
        var vault = new Bohm.Runtime.Credentials.MemoryCredentialVault();
        vault.Write("llm/company-model", ServerKey);

        await using var host = await RunningHost.StartAsync(dataRoot, configure: o => o with { Vault = vault });

        var state = await GetAsync(host);
        Assert.Equal(("http://models.example:8000/v1/", "qwen", 32768, 4096, true),
            (state.GetProperty("endpoint").GetString(), state.GetProperty("model").GetString(), state.GetProperty("contextWindow").GetInt32(),
             state.GetProperty("maxTokens").GetInt32(), state.GetProperty("reasoning").GetBoolean()));
        Assert.True(state.GetProperty("keyConnected").GetBoolean());
        Assert.False(File.Exists(Path.Combine(dataRoot, "company-model.json")));   // not read again
        Assert.True(File.Exists(Path.Combine(dataRoot, "answer-model.json")));
        Assert.Equal(ServerKey, vault.Read("llm/my-server"));
        Assert.Null(vault.Read("llm/company-model"));
        var declared = ProviderStore.Read(dataRoot).Single();
        Assert.Equal((AnswerModel.PersonServerId, ProviderOrigin.Person), (declared.Id, declared.Origin));
    }

    [Fact]
    public async Task A_listed_choice_is_remembered_as_the_answer_model_by_its_servers_provider()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-company-model-").FullName;
        var list = Listed("""{"providers":{"gpu":{"baseUrl":"http://a.example/v1","models":[{"id":"big"}]},"cpu":{"baseUrl":"http://b.example/v1","models":[{"id":"tiny"}]}}}""");
        var first = await RunningHost.StartAsync(dataRoot, configure: o => o with { CompanyModels = list });
        using (var set = await ChooseAsync(first, "cpu", "tiny")) HttpAssert.Status(HttpStatusCode.OK, set);
        // the provider id of the listed server, not its name in the list
        Assert.Equal("cpu", JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "answer-model.json"))).RootElement.GetProperty("provider").GetString());
        await first.StopKeepingDataAsync();

        await using var second = await RunningHost.StartAsync(dataRoot, configure: o => o with { CompanyModels = list });

        var again = await GetAsync(second);
        Assert.Equal(("cpu", "tiny"), (again.GetProperty("provider").GetString(), again.GetProperty("model").GetString()));
    }

    /// <summary>The answer bound a Chat Completions request asks for, by either of its names.</summary>
    private static int AnswerBound(JsonElement request) =>
        (request.TryGetProperty("max_completion_tokens", out var bound) || request.TryGetProperty("max_tokens", out bound)) ? bound.GetInt32() : -1;

    [Fact]
    public void A_check_that_ran_out_of_time_says_no_answer_and_an_unknown_failure_says_nothing()
    {
        Assert.Equal("no-answer", AnswerModel.UnreachedOf(new TaskCanceledException()));
        Assert.Null(AnswerModel.UnreachedOf(new HttpRequestException("reset", new IOException("reset"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_a_providers_key_the_server_answers_the_applications_chat_request_and_it_is_counted_as_sent(bool withKey)
    {
        await using var host = await StartAsync(fixedAtStart: false, withLocalModel: true);
        if (withKey)
            await ConnectKeyAsync(host);
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
        await ConnectKeyAsync(host);
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
        Assert.Equal(speech, AnswerModel.IsTranscriptionModel(id));

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






    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_applications_own_call_to_the_server_is_relayed_as_it_is_with_the_servers_key(bool withKey)
    {
        await using var host = await StartAsync(fixedAtStart: true);
        if (withKey)
            await ConnectKeyAsync(host);
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
        Assert.Null(list.Find("cpu", "phi")!.Input);   // the list does not say — not «text alone»
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
        Assert.Equal(("gpu", "big", "Big", 32768), (state.GetProperty("provider").GetString(), state.GetProperty("model").GetString(),
            state.GetProperty("name").GetString(), state.GetProperty("contextWindow").GetInt32()));
        Assert.Equal(["gpu/big", "cpu/small", "cpu/tiny"], state.GetProperty("choices").EnumerateArray()
            .Select(c => $"{c.GetProperty("provider").GetString()}/{c.GetProperty("model").GetString()}"));

        using (var unlisted = await ChooseAsync(first, "cpu", "big")) HttpAssert.Status(HttpStatusCode.Conflict, unlisted);
        using (var byAddress = await SetAsync(first, "http://elsewhere.example/v1", "other")) HttpAssert.Status(HttpStatusCode.Conflict, byAddress);
        using (var none = await first.ControlClient().DeleteAsync("/__control/llm/answer-model")) HttpAssert.Status(HttpStatusCode.Conflict, none);
        // The listed limits come with the choice.
        using (var chosen = await ChooseAsync(first, "cpu", "tiny"))
        {
            HttpAssert.Status(HttpStatusCode.OK, chosen);
            var now = JsonDocument.Parse(await chosen.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(("cpu", "tiny", 512), (now.GetProperty("provider").GetString(), now.GetProperty("model").GetString(), now.GetProperty("maxTokens").GetInt32()));
        }

        await first.StopKeepingDataAsync();

        await using (var second = await RunningHost.StartAsync(dataRoot, configure: o => o with { CompanyModels = list }))
            Assert.Equal("tiny", (await GetAsync(second)).GetProperty("model").GetString());

        var changed = Listed($$$$"""{"providers":{"gpu":{"baseUrl":"{{{{_server.Address.AbsoluteUri}}}}v1","models":[{"id":"big"}]}}}""");
        await using var third = await RunningHost.StartAsync(dataRoot, configure: o => o with { CompanyModels = changed });
        Assert.Equal(("gpu", "big"), ((await GetAsync(third)).GetProperty("provider").GetString(), (await GetAsync(third)).GetProperty("model").GetString()));
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

        await ConnectKeyAsync(host);
        using (var chosen = await ChooseAsync(host, "cpu", "small")) HttpAssert.Status(HttpStatusCode.OK, chosen);

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
        using (var back = await ChooseAsync(host, "gpu", "big")) HttpAssert.Status(HttpStatusCode.OK, back);
        using (var response = await PostFromAppAsync(host, app, "/__bohm/llm/api.openai.com/v1/chat/completions",
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"Hello?"}]}"""))
            HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal($"Bearer {ServerKey}", Assert.Single(_server.Received).Headers["Authorization"]);
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

    /// <summary>The key of the model server that answers is connected through its provider — where a listed server's key has always been kept.</summary>
    private static async Task ConnectKeyAsync(RunningHost host)
    {
        var provider = (await GetAsync(host)).GetProperty("provider").GetString();
        using var connected = await host.ControlClient().PutAsync($"/__control/llm/{provider}/key", new StringContent(ServerKey));
        HttpAssert.Status(HttpStatusCode.OK, connected);
    }

    private static async Task<JsonElement> GetAsync(RunningHost host) =>
        JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/answer-model")).RootElement.Clone();

    private static Task<HttpResponseMessage> ChooseAsync(RunningHost host, string provider, string model) =>
        host.ControlClient().PutAsync("/__control/llm/answer-model",
            new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["provider"] = provider, ["model"] = model }), Encoding.UTF8, "application/json"));

    /// <summary>Sets a server by its address the way the settings do: declares it as the person's own (<c>my-server</c>) and has its model answer.</summary>
    /// <returns>The refusal of the declaration, or the answer of choosing the model.</returns>
    private static async Task<HttpResponseMessage> SetAsync(RunningHost host, string endpoint, string model)
    {
        var declaration = JsonSerializer.Serialize(new
        {
            id = AnswerModel.PersonServerId, name = "My AI server", baseUrl = endpoint, api = "openai-chat",
            auth = new { style = "bearer", optional = true }, models = new[] { new { id = model } },
        });
        var declared = await host.ControlClient().PutAsync("/__control/llm/providers", new StringContent(declaration, Encoding.UTF8, "application/json"));
        if (declared.StatusCode != HttpStatusCode.OK) return declared;
        declared.Dispose();
        return await ChooseAsync(host, AnswerModel.PersonServerId, model);
    }

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
