using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The answer model by its own route: the model that answers an application's request with no key and makes proposals is chosen among
/// the models of providers (the person's own, or the organization's listed ones), not set by an address.
/// </summary>
public sealed class AnswerModelRouteTests
{
    private const string Mine = """
        { "id": "ollama", "name": "Ollama", "baseUrl": "http://models.example:11434/v1", "api": "openai-chat", "auth": { "style": "none" },
          "models": [ { "id": "llama3.2", "contextWindow": 32768 }, { "id": "qwen", "maxTokens": 4096 } ] }
        """;

    private const string Gateway = """
        { "id": "gateway", "name": "Gateway", "baseUrl": "https://gateway.example/anthropic/", "api": "anthropic-messages", "auth": { "style": "bearer" },
          "models": [ { "id": "claude" } ] }
        """;

    private const string Listed = """{"providers":{"gpu":{"baseUrl":"http://a.example/v1","models":[{"id":"big"},{"id":"mid"}]},"cpu":{"baseUrl":"http://b.example/v1","models":[{"id":"tiny"}]}}}""";

    [Fact]
    public async Task Nothing_answers_until_a_model_of_a_provider_the_person_declared_is_chosen_and_only_such_models_may_be()
    {
        await using var host = await RunningHost.StartAsync();
        var none = await GetAsync(host);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("provider").ValueKind);
        Assert.Empty(none.GetProperty("choices").EnumerateArray());

        await DeclareAsync(host, Mine);
        await DeclareAsync(host, Gateway);   // not OpenAI Chat: a server that answers without a key is asked in that format

        var choices = (await GetAsync(host)).GetProperty("choices").EnumerateArray().ToList();
        Assert.Equal(["ollama/llama3.2", "ollama/qwen"], choices.Select(c => $"{c.GetProperty("provider").GetString()}/{c.GetProperty("model").GetString()}"));
        Assert.Equal(32768, choices[0].GetProperty("contextWindow").GetInt32());

        using (var notOne = await ChooseAsync(host, "gateway", "claude")) HttpAssert.Status(HttpStatusCode.Conflict, notOne);
        using (var builtIn = await ChooseAsync(host, "openai", "gpt-5")) HttpAssert.Status(HttpStatusCode.Conflict, builtIn);
        using (var unknownModel = await ChooseAsync(host, "ollama", "nope")) HttpAssert.Status(HttpStatusCode.Conflict, unknownModel);
        using (var missing = await host.ControlClient().PutAsync("/__control/llm/answer-model", new StringContent("""{"provider":"ollama"}""", Encoding.UTF8, "application/json")))
            HttpAssert.Status(HttpStatusCode.BadRequest, missing);

        using (var chosen = await ChooseAsync(host, "ollama", "qwen"))
        {
            HttpAssert.Status(HttpStatusCode.OK, chosen);
            var view = JsonDocument.Parse(await chosen.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(("ollama", "qwen", "http://models.example:11434/v1/", false, 4096),
                (view.GetProperty("provider").GetString(), view.GetProperty("model").GetString(), view.GetProperty("endpoint").GetString(),
                 view.GetProperty("fixed").GetBoolean(), view.GetProperty("maxTokens").GetInt32()));
        }

        // It answers as the provider's own model — and the providers' table says which providers' chat it answers for.
        var providers = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm")).RootElement.EnumerateArray().ToList();
        Assert.All(providers.Where(p => p.GetProperty("origin").GetString() == "built-in"), p => Assert.Equal("company", p.GetProperty("answeredBy").GetString()));
    }

    [Fact]
    public async Task The_choice_is_remembered_and_the_key_is_the_chosen_providers_own()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-answer-model-").FullName;
        var vault = new Bohm.Runtime.Credentials.MemoryCredentialVault();
        var first = await RunningHost.StartAsync(dataRoot, configure: o => o with { Vault = vault });
        await DeclareAsync(first, Mine);
        using (var chosen = await ChooseAsync(first, "ollama", "llama3.2")) HttpAssert.Status(HttpStatusCode.OK, chosen);
        Assert.False((await GetAsync(first)).GetProperty("keyConnected").GetBoolean());
        await first.StopKeepingDataAsync();

        vault.Write("llm/ollama", "server-key-0123456789");
        await using var second = await RunningHost.StartAsync(dataRoot, configure: o => o with { Vault = vault });
        var again = await GetAsync(second);
        Assert.Equal(("ollama", "llama3.2", true), (again.GetProperty("provider").GetString(), again.GetProperty("model").GetString(), again.GetProperty("keyConnected").GetBoolean()));
        Assert.True(again.GetProperty("choices").EnumerateArray().All(c => c.GetProperty("keyConnected").GetBoolean()));
    }

    [Fact]
    public async Task Choosing_none_leaves_the_providers_declaration_where_it_is()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-answer-model-").FullName;
        await using var host = await RunningHost.StartAsync(dataRoot);
        await DeclareAsync(host, Mine);
        using (var chosen = await ChooseAsync(host, "ollama", "qwen")) HttpAssert.Status(HttpStatusCode.OK, chosen);
        Assert.True(File.Exists(Path.Combine(dataRoot, "answer-model.json")));

        using var cleared = await host.ControlClient().DeleteAsync("/__control/llm/answer-model");
        HttpAssert.Status(HttpStatusCode.OK, cleared);
        Assert.False(File.Exists(Path.Combine(dataRoot, "answer-model.json")));
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await cleared.Content.ReadAsStringAsync()).RootElement.GetProperty("provider").ValueKind);
        var providers = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm")).RootElement.EnumerateArray().ToList();
        Assert.Contains(providers, p => p.GetProperty("id").GetString() == "ollama");   // the declaration is the person's own like any other
        Assert.All(providers, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("answeredBy").ValueKind));
    }

    [Fact]
    public async Task Taking_the_chosen_provider_away_leaves_nothing_answering()
    {
        await using var host = await RunningHost.StartAsync();
        await DeclareAsync(host, Mine);
        using (var chosen = await ChooseAsync(host, "ollama", "qwen")) HttpAssert.Status(HttpStatusCode.OK, chosen);

        using var removed = await host.ControlClient().DeleteAsync("/__control/llm/providers/ollama");
        HttpAssert.Status(HttpStatusCode.OK, removed);

        var view = await GetAsync(host);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("provider").ValueKind);
        Assert.Empty(view.GetProperty("choices").EnumerateArray());
    }

    [Fact]
    public async Task When_the_organization_listed_its_servers_only_their_models_may_answer_and_one_always_does()
    {
        Assert.True(CompanyModelList.TryParse(Listed, out var list));
        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = list });
        await DeclareAsync(host, Mine);   // the person's own providers are not choices when the servers are listed

        var view = await GetAsync(host);
        Assert.True(view.GetProperty("fixed").GetBoolean());
        Assert.Equal(["gpu/big", "gpu/mid", "cpu/tiny"], view.GetProperty("choices").EnumerateArray().Select(c => $"{c.GetProperty("provider").GetString()}/{c.GetProperty("model").GetString()}"));
        Assert.Equal(("gpu", "big"), (view.GetProperty("provider").GetString(), view.GetProperty("model").GetString()));   // the first listed

        using (var other = await ChooseAsync(host, "ollama", "qwen")) HttpAssert.Status(HttpStatusCode.Conflict, other);
        using (var chosen = await ChooseAsync(host, "cpu", "tiny")) HttpAssert.Status(HttpStatusCode.OK, chosen);
        Assert.Equal(("cpu", "tiny"), ((await GetAsync(host)).GetProperty("provider").GetString(), (await GetAsync(host)).GetProperty("model").GetString()));

        using var cleared = await host.ControlClient().DeleteAsync("/__control/llm/answer-model");
        HttpAssert.Status(HttpStatusCode.Conflict, cleared);
    }

    private const string ServerKey = "org-key-0123456789";

    [Fact]
    public async Task A_check_asks_the_answer_models_server_for_its_models_with_its_key_and_says_whether_it_knows_the_model()
    {
        await using var server = await FakeProvider.StartAsync();
        await using var host = await RunningHost.StartAsync();
        await ServeAsync(host, server);
        using (var key = await host.ControlClient().PutAsync("/__control/llm/srv/key", new StringContent(ServerKey))) HttpAssert.Status(HttpStatusCode.OK, key);

        server.Refusal = (200, """{"object":"list","data":[{"id":"other-model"},{"id":"set-model"}]}""");
        var listed = await CheckAsync(host);
        Assert.Equal("answers", listed.GetProperty("result").GetString());
        Assert.Equal(200, listed.GetProperty("status").GetInt32());
        Assert.True(listed.GetProperty("modelListed").GetBoolean());
        // The list once to tell how the server answered, and once more for the model's limits as IronHive reads them.
        Assert.All(server.Received, request =>
        {
            Assert.Equal(("GET", "/v1/models"), (request.Method, request.PathAndQuery));
            Assert.Equal($"Bearer {ServerKey}", request.Headers["Authorization"]);
        });

        server.Refusal = (200, """{"object":"list","data":[{"id":"other-model"}]}""");
        Assert.False((await CheckAsync(host)).GetProperty("modelListed").GetBoolean());
        server.Refusal = (200, "not a list");
        Assert.Equal(JsonValueKind.Null, (await CheckAsync(host)).GetProperty("modelListed").ValueKind);

        // Nothing of an application's went, but the requests left this computer.
        var sent = Assert.Single(JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(server.Address.Authority, sent.GetProperty("host").GetString());
    }

    [Fact]
    public async Task A_check_takes_the_context_window_a_vllm_server_reports_for_the_model()
    {
        await using var server = await FakeProvider.StartAsync();
        await using var host = await RunningHost.StartAsync();
        await ServeAsync(host, server);
        server.Models = """{"object":"list","data":[{"id":"other-model","object":"model","max_model_len":4096},{"id":"set-model","object":"model","owned_by":"vllm","max_model_len":32768}]}""";

        var check = await CheckAsync(host);

        Assert.Equal("answers", check.GetProperty("result").GetString());
        Assert.Equal(32768, check.GetProperty("reportedContextWindow").GetInt32());
        var state = await GetAsync(host);
        Assert.Equal(32768, state.GetProperty("reportedContextWindow").GetInt32());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("contextWindow").ValueKind);   // as declared: nobody gave one
    }

    [Theory]
    [InlineData(401, "key-refused")]
    [InlineData(403, "key-refused")]
    [InlineData(404, "not-found")]
    [InlineData(500, "refused")]
    public async Task A_check_tells_a_refused_key_a_wrong_address_and_other_refusals_apart(int status, string result)
    {
        await using var server = await FakeProvider.StartAsync();
        await using var host = await RunningHost.StartAsync();
        await ServeAsync(host, server);
        server.Refusal = (status, """{"error":{"message":"no"}}""");

        var check = await CheckAsync(host);

        Assert.Equal(result, check.GetProperty("result").GetString());
        Assert.Equal(status, check.GetProperty("status").GetInt32());
        Assert.False(Assert.Single(server.Received).Headers.ContainsKey("Authorization"));   // no key connected, none made up
    }

    [Fact]
    public async Task A_check_of_a_server_that_does_not_answer_says_so_and_with_none_chosen_there_is_nothing_to_check()
    {
        await using var host = await RunningHost.StartAsync();
        using (var none = await host.ControlClient().PostAsync("/__control/llm/answer-model/check", null)) HttpAssert.Status(HttpStatusCode.NotFound, none);

        // Port 1 on loopback: nothing listens there, so a connection is refused, and no other test's server can take it meanwhile —
        // servers here ask for a port from the ephemeral range, which never hands out port 1. A port bound but not listening is not
        // the same everywhere: macOS drops the connection attempt instead of refusing it.
        await DeclareAsync(host, Server("http://127.0.0.1:1/v1/"));
        using (var chosen = await ChooseAsync(host, "srv", "set-model")) HttpAssert.Status(HttpStatusCode.OK, chosen);
        var refused = await CheckAsync(host);
        Assert.Equal("unreachable", refused.GetProperty("result").GetString());
        Assert.Equal("connection-refused", refused.GetProperty("unreached").GetString());
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("status").ValueKind);

        await DeclareAsync(host, Server("http://models.invalid/v1/"));
        var unresolved = await CheckAsync(host);
        Assert.Equal("unreachable", unresolved.GetProperty("result").GetString());
        Assert.Equal("host-not-found", unresolved.GetProperty("unreached").GetString());
    }

    [Fact]
    public async Task The_models_of_a_server_not_declared_yet_are_listed_by_id_with_the_context_each_reports_and_the_key_given()
    {
        await using var server = await FakeProvider.StartAsync();
        await using var host = await RunningHost.StartAsync();
        server.Models = """{"object":"list","data":[{"id":"zeta","object":"model","max_model_len":8192},{"id":"Alpha","object":"model"},{"id":"zeta","object":"model"}]}""";

        var listed = await ModelsAsync(host, new { endpoint = new Uri(server.Address, "v1").AbsoluteUri, key = "listing-key" });

        Assert.Equal("answers", listed.GetProperty("result").GetString());
        var models = listed.GetProperty("models").EnumerateArray().Select(m => (m.GetProperty("id").GetString(), m.GetProperty("contextWindow").ValueKind == JsonValueKind.Null ? (int?)null : m.GetProperty("contextWindow").GetInt32())).ToList();
        Assert.Equal([("Alpha", (int?)null), ("zeta", 8192)], models);
        var request = Assert.Single(server.Received);
        Assert.Equal(("GET", "/v1/models"), (request.Method, request.PathAndQuery));
        Assert.Equal("Bearer listing-key", request.Headers["Authorization"]);
        // Nothing is declared or chosen by listing, and the request left this computer.
        Assert.Equal(JsonValueKind.Null, (await GetAsync(host)).GetProperty("provider").ValueKind);
        var sent = Assert.Single(JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(server.Address.Authority, sent.GetProperty("host").GetString());
    }

    [Fact]
    public async Task Listing_the_models_of_a_declared_providers_address_without_a_key_tries_the_key_connected_for_it()
    {
        await using var server = await FakeProvider.StartAsync();
        await using var host = await RunningHost.StartAsync();
        await ServeAsync(host, server);
        using (var key = await host.ControlClient().PutAsync("/__control/llm/srv/key", new StringContent(ServerKey))) HttpAssert.Status(HttpStatusCode.OK, key);
        server.Models = """{"object":"list","data":[{"id":"set-model","object":"model"}]}""";
        server.Received.Clear();

        var listed = await ModelsAsync(host, new { endpoint = new Uri(server.Address, "v1/").AbsoluteUri });

        Assert.Equal("set-model", Assert.Single(listed.GetProperty("models").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal($"Bearer {ServerKey}", Assert.Single(server.Received).Headers["Authorization"]);

        // Another address is another place: the key connected for the first does not go there.
        server.Received.Clear();
        var elsewhere = new UriBuilder(server.Address) { Host = "localhost" }.Uri;
        await ModelsAsync(host, new { endpoint = new Uri(elsewhere, "v1/").AbsoluteUri });
        var asked = Assert.Single(server.Received);
        Assert.True(!asked.Headers.TryGetValue("Authorization", out var auth) || auth == "Bearer no-credential-required", auth);
    }

    [Theory]
    [InlineData(401, "key-refused")]
    [InlineData(404, "not-found")]
    [InlineData(500, "refused")]
    public async Task Listing_tells_a_refused_key_a_wrong_address_and_other_refusals_apart(int status, string result)
    {
        await using var server = await FakeProvider.StartAsync();
        await using var host = await RunningHost.StartAsync();
        server.Refusal = (status, """{"error":{"message":"no"}}""");

        var listed = await ModelsAsync(host, new { endpoint = new Uri(server.Address, "v1").AbsoluteUri });

        Assert.Equal(result, listed.GetProperty("result").GetString());
        Assert.Equal(status, listed.GetProperty("status").GetInt32());
        Assert.Empty(listed.GetProperty("models").EnumerateArray());
    }

    [Theory]
    [InlineData("""{"endpoint":"ftp://example.test/v1"}""")]
    [InlineData("""{"endpoint":"https://user:pw@example.test/v1"}""")]
    [InlineData("""{}""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public async Task Listing_refuses_an_address_a_provider_could_not_be_declared_at(string body)
    {
        await using var server = await FakeProvider.StartAsync();
        await using var host = await RunningHost.StartAsync();

        using var refused = await host.ControlClient().PostAsync("/__control/llm/providers/models", new StringContent(body, Encoding.UTF8, "application/json"));

        HttpAssert.Status(HttpStatusCode.BadRequest, refused);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task A_listed_servers_key_is_connected_through_its_provider_where_a_listed_servers_key_has_always_been_kept()
    {
        Assert.True(CompanyModelList.TryParse(Listed, out var list));
        var vault = new Bohm.Runtime.Credentials.MemoryCredentialVault();
        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = list, Vault = vault });

        using (var key = await host.ControlClient().PutAsync("/__control/llm/gpu/key", new StringContent(ServerKey))) HttpAssert.Status(HttpStatusCode.OK, key);

        // The key a PC already holds for a listed server (by the name in the list) is the one connected, and no key appears under the provider's own id.
        Assert.Equal(ServerKey, vault.Read("llm/company-model/gpu"));
        Assert.True(string.IsNullOrEmpty(vault.Read("llm/gpu")));
        Assert.Equal([true, true, false], (await GetAsync(host)).GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("keyConnected").GetBoolean()));
        Assert.True((await GetAsync(host)).GetProperty("keyConnected").GetBoolean());   // the first listed model answers: gpu's

        using (var gone = await host.ControlClient().DeleteAsync("/__control/llm/gpu/key")) HttpAssert.Status(HttpStatusCode.OK, gone);
        Assert.True(string.IsNullOrEmpty(vault.Read("llm/company-model/gpu")));
    }

    [Fact]
    public async Task A_server_listed_without_a_name_keeps_its_key_where_the_older_way_kept_it()
    {
        var list = CompanyModelList.Of(new CompanyModelOptions(new Uri("http://models.example:8000/v1/"), "fixed-model"));
        var vault = new Bohm.Runtime.Credentials.MemoryCredentialVault();
        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = list, Vault = vault });
        var provider = (await GetAsync(host)).GetProperty("provider").GetString();

        using (var key = await host.ControlClient().PutAsync($"/__control/llm/{provider}/key", new StringContent(ServerKey))) HttpAssert.Status(HttpStatusCode.OK, key);

        Assert.Equal(ServerKey, vault.Read("llm/company-model"));
        Assert.True((await GetAsync(host)).GetProperty("keyConnected").GetBoolean());
    }

    [Theory]
    [InlineData("""{"id":"m","contextWindow":8192,"maxTokens":16000}""")]
    [InlineData("""{"id":"m","contextWindow":0}""")]
    [InlineData("""{"id":"m","maxTokens":-5}""")]
    public async Task Limits_that_do_not_fit_together_are_refused_when_a_person_hands_in_the_declaration_but_do_not_drop_a_server_read_back_from_a_file(string model)
    {
        var declaration = $$"""{ "id": "srv", "baseUrl": "http://models.example/v1", "api": "openai-chat", "auth": { "style": "none" }, "models": [ {{model}} ] }""";
        await using var host = await RunningHost.StartAsync();

        using var refused = await host.ControlClient().PutAsync("/__control/llm/providers", new StringContent(declaration, Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.BadRequest, refused);
        Assert.Contains("maxTokens", await refused.Content.ReadAsStringAsync());

        // Read back from a file, the same declaration keeps its server and takes the limits as unknown.
        Assert.False(ProviderDeclaration.TryParse(declaration, ProviderOrigin.Person, out _, out _, strictLimits: true));
        Assert.True(ProviderDeclaration.TryParse(declaration, ProviderOrigin.Person, out var read, out _));
        Assert.Equal(ModelLimits.Unknown, read!.Models[0].Limits);
    }

    [Fact]
    public async Task Limits_given_with_a_declared_model_are_the_answer_models_limits()
    {
        await using var host = await RunningHost.StartAsync();
        await DeclareAsync(host, """{ "id": "srv", "baseUrl": "http://models.example/v1", "api": "openai-chat", "auth": { "style": "none" }, "models": [ { "id": "m", "contextWindow": 32768, "maxTokens": 4096, "reasoning": true } ] }""");
        using (var chosen = await ChooseAsync(host, "srv", "m")) HttpAssert.Status(HttpStatusCode.OK, chosen);

        var view = await GetAsync(host);

        Assert.Equal((32768, 4096, true), (view.GetProperty("contextWindow").GetInt32(), view.GetProperty("maxTokens").GetInt32(), view.GetProperty("reasoning").GetBoolean()));
    }

    /// <summary>A provider with one model at <paramref name="baseUrl"/>, as the person declares a server of their own: a key when one is connected.</summary>
    private static string Server(string baseUrl) =>
        $$"""{ "id": "srv", "name": "Srv", "baseUrl": "{{baseUrl}}", "api": "openai-chat", "auth": { "style": "bearer", "optional": true }, "models": [ { "id": "set-model" } ] }""";

    /// <summary>Declares <paramref name="server"/> as the provider <c>srv</c> with the model <c>set-model</c> and has that model answer.</summary>
    private static async Task ServeAsync(RunningHost host, FakeProvider server)
    {
        await DeclareAsync(host, Server(new Uri(server.Address, "v1/").AbsoluteUri));
        using var chosen = await ChooseAsync(host, "srv", "set-model");
        HttpAssert.Status(HttpStatusCode.OK, chosen);
    }

    private static async Task<JsonElement> CheckAsync(RunningHost host)
    {
        using var check = await host.ControlClient().PostAsync("/__control/llm/answer-model/check", null);
        HttpAssert.Status(HttpStatusCode.OK, check);
        return JsonDocument.Parse(await check.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> ModelsAsync(RunningHost host, object body)
    {
        using var listed = await host.ControlClient().PostAsync("/__control/llm/providers/models",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.OK, listed);
        return JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task DeclareAsync(RunningHost host, string declaration)
    {
        using var declared = await host.ControlClient().PutAsync("/__control/llm/providers", new StringContent(declaration, Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.OK, declared);
    }

    private static Task<HttpResponseMessage> ChooseAsync(RunningHost host, string provider, string model) =>
        host.ControlClient().PutAsync("/__control/llm/answer-model",
            new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["provider"] = provider, ["model"] = model }), Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> GetAsync(RunningHost host) =>
        JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/answer-model")).RootElement.Clone();
}
