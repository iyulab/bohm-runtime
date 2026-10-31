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
