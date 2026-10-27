using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Credentials;
using Microsoft.Extensions.DependencyInjection;

namespace Bohm.Runtime.Tests.Host;

public sealed class ProviderControlTests : IAsyncLifetime
{
    private const string Key = "sk-declared-0123456789";

    private FakeProvider _provider = null!;
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync();
        _host = await RunningHost.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private string Declaration(string id, string auth = """{ "style": "bearer" }""", string address = "", string api = "openai-chat") => $$"""
        { "id": "{{id}}", "name": "{{id}} server", "baseUrl": "{{(address.Length > 0 ? address : _provider.Address.ToString())}}", "api": "{{api}}", "auth": {{auth}},
          "models": [ { "id": "model-a", "contextWindow": 8192, "maxTokens": 1024 } ] }
        """;

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _host.ControlClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private async Task<JsonElement> ListAsync() => (await SendAsync(HttpMethod.Get, "/__control/llm")).Body;

    private static JsonElement Find(JsonElement list, string id) => list.EnumerateArray().Single(p => p.GetProperty("id").GetString() == id);

    [Fact]
    public async Task A_provider_declared_by_the_person_joins_the_built_in_ones_and_stays()
    {
        var (status, view) = await SendAsync(HttpMethod.Put, "/__control/llm/providers", Declaration("local-server", """{ "style": "none" }"""));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("added", view.GetProperty("origin").GetString());
        Assert.Equal("openai-chat", view.GetProperty("api").GetString());
        Assert.False(view.GetProperty("keyRequired").GetBoolean());
        Assert.True(view.GetProperty("connected").GetBoolean());   // it takes no key: usable as it stands
        Assert.Equal("model-a", Assert.Single(view.GetProperty("models").EnumerateArray()).GetProperty("id").GetString());

        var list = await ListAsync();
        Assert.Equal(["openai", "anthropic", "google", "groq", "openrouter", "mistral", "local-server"], list.EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal("built-in", Find(list, "openai").GetProperty("origin").GetString());
        Assert.False(Find(list, "openai").GetProperty("connected").GetBoolean());   // a built-in one still needs its key

        // Kept across a restart: the file is at the data root, and the keys are not in it.
        var file = await File.ReadAllTextAsync(Path.Combine(_host.DataRoot, "providers.json"));
        Assert.Contains("local-server", file);
    }

    [Fact]
    public async Task A_declaration_that_cannot_be_used_is_refused_with_the_property_named()
    {
        var (status, problem) = await SendAsync(HttpMethod.Put, "/__control/llm/providers",
            """{ "id": "p", "baseUrl": "https://p.example/", "api": "my-own-format" }""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid-provider", problem.GetProperty("reason").GetString());
        Assert.StartsWith("api", problem.GetProperty("detail").GetString());
        Assert.Equal(6, (await ListAsync()).GetArrayLength());   // nothing was added
    }

    [Fact]
    public async Task A_key_pasted_into_a_declaration_is_refused_and_not_kept()
    {
        var (status, problem) = await SendAsync(HttpMethod.Put, "/__control/llm/providers",
            $$"""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "apiKey": "{{Key}}" }""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.DoesNotContain(Key, problem.ToString());
        Assert.False(File.Exists(Path.Combine(_host.DataRoot, "providers.json")));
    }

    [Fact]
    public async Task A_built_in_providers_id_cannot_be_declared_again()
    {
        var (status, problem) = await SendAsync(HttpMethod.Put, "/__control/llm/providers", Declaration("openai"));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("built-in-provider", problem.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_key_is_connected_to_a_declared_provider_like_to_a_built_in_one_and_goes_with_it_when_it_is_removed()
    {
        await SendAsync(HttpMethod.Put, "/__control/llm/providers", Declaration("gateway"));
        Assert.False(Find(await ListAsync(), "gateway").GetProperty("connected").GetBoolean());

        using var connect = await _host.ControlClient().PutAsync("/__control/llm/gateway/key", new StringContent(Key));
        HttpAssert.Status(HttpStatusCode.OK, connect);
        Assert.True(Find(await ListAsync(), "gateway").GetProperty("connected").GetBoolean());
        var vault = _host.Services.GetRequiredService<ICredentialVault>();
        Assert.Equal(Key, vault.Read("llm/gateway"));

        var (status, remaining) = await SendAsync(HttpMethod.Delete, "/__control/llm/providers/gateway");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(remaining.EnumerateArray(), p => p.GetProperty("id").GetString() == "gateway");
        Assert.Null(vault.Read("llm/gateway"));
    }

    [Fact]
    public async Task The_same_id_declared_again_at_another_address_does_not_carry_its_key_there()
    {
        var vault = _host.Services.GetRequiredService<ICredentialVault>();
        await SendAsync(HttpMethod.Put, "/__control/llm/providers", Declaration("gateway"));
        vault.Write("llm/gateway", Key);

        // The same address, declared again (a model added, say): the key stays.
        await SendAsync(HttpMethod.Put, "/__control/llm/providers", Declaration("gateway"));
        Assert.Equal(Key, vault.Read("llm/gateway"));

        // Another address: the key is not sent there — it is asked for again.
        await SendAsync(HttpMethod.Put, "/__control/llm/providers", Declaration("gateway", address: "https://other.example/"));
        Assert.Null(vault.Read("llm/gateway"));
        Assert.False(Find(await ListAsync(), "gateway").GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task A_provider_declared_as_taking_no_key_refuses_one()
    {
        await SendAsync(HttpMethod.Put, "/__control/llm/providers", Declaration("open-server", """{ "style": "none" }"""));

        using var connect = await _host.ControlClient().PutAsync("/__control/llm/open-server/key", new StringContent(Key));

        HttpAssert.Status(HttpStatusCode.Conflict, connect);
        Assert.Null(_host.Services.GetRequiredService<ICredentialVault>().Read("llm/open-server"));
    }

    [Fact]
    public async Task What_is_not_there_to_remove_is_not_found_and_a_built_in_provider_stays()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/__control/llm/providers/nowhere")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/__control/llm/providers/openai")).Status);
        Assert.Equal(6, (await ListAsync()).GetArrayLength());
    }

    [Fact]
    public async Task The_check_asks_in_the_declared_format_and_says_it_answered()
    {
        await SendAsync(HttpMethod.Put, "/__control/llm/providers", Declaration("gateway"));
        _host.Services.GetRequiredService<ICredentialVault>().Write("llm/gateway", Key);

        var (status, result) = await SendAsync(HttpMethod.Post, "/__control/llm/providers/gateway/check");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", result.GetProperty("result").GetString());
        var asked = Assert.Single(_provider.Received);   // (the check's question is the one the fake files under «thinking»)
        Assert.EndsWith("/chat/completions", asked.PathAndQuery);   // not the model list: the format the chat would use
        Assert.Equal("Bearer " + Key, asked.Headers["Authorization"]);
        Assert.Contains("\"model\":\"model-a\"", asked.Body);
    }

    [Theory]
    [InlineData(401, "key-refused")]
    [InlineData(403, "key-refused")]
    [InlineData(404, "not-found")]
    [InlineData(400, "bad-request")]
    [InlineData(500, "refused")]
    public async Task The_check_says_where_it_went_wrong_in_words(int refusal, string expected)
    {
        await SendAsync(HttpMethod.Put, "/__control/llm/providers", Declaration("gateway"));
        _host.Services.GetRequiredService<ICredentialVault>().Write("llm/gateway", Key);
        _provider.Refusal = (refusal, """{"error":{"message":"the key sk-leaky-0123456789 is wrong"}}""");

        var (status, result) = await SendAsync(HttpMethod.Post, "/__control/llm/providers/gateway/check", """{ "model": "model-a" }""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, result.GetProperty("result").GetString());
        Assert.Equal(refusal, result.GetProperty("status").GetInt32());
        Assert.DoesNotContain("sk-leaky", result.ToString());   // what the provider's reply held is not passed on
    }

    [Fact]
    public async Task The_check_of_a_provider_whose_key_is_not_connected_says_so_and_sends_nothing()
    {
        await SendAsync(HttpMethod.Put, "/__control/llm/providers", Declaration("gateway"));

        var (_, result) = await SendAsync(HttpMethod.Post, "/__control/llm/providers/gateway/check");

        Assert.Equal("no-key", result.GetProperty("result").GetString());
        Assert.Empty(_provider.Received);
    }

    [Fact]
    public async Task The_check_of_a_server_nothing_listens_at_says_unreachable()
    {
        await SendAsync(HttpMethod.Put, "/__control/llm/providers", Declaration("nowhere", """{ "style": "none" }""", address: "http://127.0.0.1:1/"));

        var (_, result) = await SendAsync(HttpMethod.Post, "/__control/llm/providers/nowhere/check");

        Assert.Equal("unreachable", result.GetProperty("result").GetString());
        Assert.Equal("connection-refused", result.GetProperty("unreached").GetString());
    }

    [Fact]
    public async Task The_check_of_a_provider_that_lists_no_model_asks_which_one()
    {
        await SendAsync(HttpMethod.Put, "/__control/llm/providers",
            $$"""{ "id": "bare", "baseUrl": "{{_provider.Address}}", "api": "openai-chat", "auth": { "style": "none" } }""");

        var (status, problem) = await SendAsync(HttpMethod.Post, "/__control/llm/providers/bare/check");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("model-needed", problem.GetProperty("reason").GetString());

        var (named, result) = await SendAsync(HttpMethod.Post, "/__control/llm/providers/bare/check", """{ "model": "any" }""");
        Assert.Equal(HttpStatusCode.OK, named);
        Assert.Equal("ok", result.GetProperty("result").GetString());
    }

    [Fact]
    public async Task The_check_of_an_unknown_provider_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/__control/llm/providers/nowhere/check")).Status);
    }
}
