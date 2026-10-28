using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

public sealed class ProviderDeclarationTests
{
    private const string Ollama = """
        { "id": "ollama", "name": "Ollama", "baseUrl": "http://localhost:11434/v1", "api": "openai-chat",
          "auth": { "style": "none" },
          "models": [ { "id": "qwen3:8b", "name": "Qwen 3 8B", "input": ["text", "image"], "contextWindow": 32768, "maxTokens": 8192, "reasoning": true } ] }
        """;

    private static ProviderDeclaration Parse(string json, ProviderOrigin origin = ProviderOrigin.Person)
    {
        Assert.True(ProviderDeclaration.TryParse(json, origin, out var declaration, out var problem), problem);
        return declaration!;
    }

    private static string Refused(string json)
    {
        Assert.False(ProviderDeclaration.TryParse(json, ProviderOrigin.Person, out var declaration, out var problem));
        Assert.Null(declaration);
        return problem!;
    }

    [Fact]
    public void A_server_that_takes_no_key_is_one_declaration()
    {
        var ollama = Parse(Ollama);
        Assert.Equal("ollama", ollama.Id);
        Assert.Equal(new Uri("http://localhost:11434/v1/"), ollama.BaseUrl);   // normalized to end with a slash
        Assert.Equal(ProviderApi.OpenAIChat, ollama.Api);
        Assert.Equal(AuthStyle.None, ollama.Auth.Style);
        var model = Assert.Single(ollama.Models);
        Assert.Equal(new ModelLimits(32768, 8192, true), model.Limits);
        Assert.Equal(["text", "image"], model.Input);
        Assert.Equal("llm/ollama", ollama.VaultName);
    }

    [Fact]
    public void A_gateway_whose_models_speak_different_formats_is_one_declaration()
    {
        var gateway = Parse("""
            { "id": "gateway", "baseUrl": "https://gw.example/", "api": "openai-chat",
              "models": [ { "id": "model-a", "path": "openai/v1" },
                          { "id": "model-b", "api": "anthropic-messages", "path": "anthropic/" },
                          { "id": "model-c" } ] }
            """);

        var (a, b, c) = (gateway.Models[0], gateway.Models[1], gateway.Models[2]);
        Assert.Equal((ProviderApi.OpenAIChat, new Uri("https://gw.example/openai/v1/")), (gateway.ApiOf(a), gateway.RootOf(a)));
        Assert.Equal((ProviderApi.AnthropicMessages, new Uri("https://gw.example/anthropic/")), (gateway.ApiOf(b), gateway.RootOf(b)));
        Assert.Equal((ProviderApi.OpenAIChat, new Uri("https://gw.example/")), (gateway.ApiOf(c), gateway.RootOf(c)));
        Assert.Equal("gateway", gateway.Name);                          // no name: the id
        Assert.Equal(AuthStyle.Bearer, gateway.Auth.Style);             // no auth: a bearer key
    }

    [Theory]
    [InlineData("""{ "style": "header", "name": "x-api-key" }""", "Header", "x-api-key")]
    [InlineData("""{ "style": "google" }""", "Google", null)]
    [InlineData("""{ "style": "BEARER" }""", "Bearer", null)]
    public void The_key_is_presented_the_way_the_declaration_says(string auth, string style, string? header)
    {
        var declaration = Parse($$"""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "auth": {{auth}} }""");
        Assert.Equal(new ProviderAuth(Enum.Parse<AuthStyle>(style), header), declaration.Auth);
        Assert.Empty(declaration.Models);   // no models: the server is to be asked
    }

    [Theory]
    [InlineData("""{ "id": "Bad Id", "baseUrl": "https://p.example/", "api": "openai-chat" }""", "id")]
    [InlineData("""{ "id": "cloud", "baseUrl": "https://p.example/", "api": "openai-chat" }""", "id: reserved")]
    [InlineData("""{ "id": "p", "baseUrl": "ftp://p.example/", "api": "openai-chat" }""", "baseUrl")]
    [InlineData("""{ "id": "p", "baseUrl": "https://user:pw@p.example/", "api": "openai-chat" }""", "baseUrl")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/?k=1", "api": "openai-chat" }""", "baseUrl")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "my-own-format" }""", "api")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/" }""", "api")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "auth": { "style": "header" } }""", "auth")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "auth": { "style": "header", "name": "Host" } }""", "auth")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "auth": { "style": "cookie" } }""", "auth")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "models": [ { "id": "m", "api": "x" } ] }""", "models")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "models": [ { "id": "m", "path": "../up" } ] }""", "models")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "models": [ { "id": "m", "path": "/absolute" } ] }""", "models")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "models": [ { "id": "m", "path": "https://elsewhere.example/" } ] }""", "models")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "models": [ { "id": "m" }, { "id": "m" } ] }""", "models")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "models": {} }""", "models")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "anthropic-messages", "auth": { "style": "header", "name": "x-api-key" } }""", "auth")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "anthropic-messages", "auth": { "style": "header", "name": "Authorization" } }""", "auth")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "auth": { "style": "header", "name": "x-api-key" }, "models": [ { "id": "m", "api": "anthropic-messages" } ] }""", "auth")]
    [InlineData("""{ "id": "p", "baseUrl": "https://p.example/", "api": "google-gemini", "auth": { "style": "header", "name": "authorization" } }""", "auth")]
    [InlineData("[]", "not an object")]
    [InlineData("nope", "not JSON")]
    public void What_cannot_be_used_is_refused_with_the_property_named(string json, string expected) =>
        Assert.StartsWith(expected, Refused(json));

    [Theory]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("token")]
    [InlineData("secret")]
    public void A_key_in_the_declaration_is_refused_not_quietly_kept(string property)
    {
        var problem = Refused($$"""{ "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat", "{{property}}": "sk-0123456789" }""");
        Assert.Contains("key", problem);
        Assert.DoesNotContain("sk-0123456789", problem);
    }

    [Fact]
    public void Limits_that_do_not_make_sense_leave_the_models_limits_unknown()
    {
        var declaration = Parse("""
            { "id": "p", "baseUrl": "https://p.example/", "api": "openai-chat",
              "models": [ { "id": "m", "contextWindow": 1000, "maxTokens": 5000 } ] }
            """);
        Assert.Equal(ModelLimits.Unknown, declaration.Models[0].Limits);
    }

    [Fact]
    public void What_is_written_reads_back_the_same_and_has_no_key()
    {
        var gateway = Parse("""
            { "id": "gateway", "name": "Gateway", "baseUrl": "https://gw.example/", "api": "openai-chat",
              "auth": { "style": "header", "name": "x-token" },
              "models": [ { "id": "model-b", "name": "B", "api": "anthropic-messages", "path": "anthropic", "contextWindow": 200000, "maxTokens": 8192, "reasoning": false, "input": ["text"] } ] }
            """);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) gateway.WriteTo(writer);
        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        var again = Parse(json);
        Assert.Equal(gateway.Id, again.Id);
        Assert.Equal(gateway.BaseUrl, again.BaseUrl);
        Assert.Equal(gateway.Auth, again.Auth);
        // A model holds a list, which a record compares by reference — compare what is in it.
        static object Flat(DeclaredModel m) => (m.Id, m.Name, m.Api, m.Path, m.Limits, Input: string.Join(",", m.Input ?? []));
        Assert.Equal(gateway.Models.Select(Flat), again.Models.Select(Flat));
    }

    [Fact]
    public void The_providers_an_application_is_matched_to_are_the_built_in_declarations()
    {
        // One list: LlmProviders is derived from the declarations, so the hosts the runtime routes an application's request by
        // are the declared hosts, and a provider is added in one place.
        Assert.Equal(ProviderDeclaration.BuiltIn.Select(d => d.Id), LlmProviders.All.Select(p => p.Id));
        foreach (var declared in ProviderDeclaration.BuiltIn)
        {
            var known = LlmProviders.ById(declared.Id)!;
            Assert.Equal(declared.BaseUrl.Host, known.Host);
            Assert.Equal(declared.Name, known.DisplayName);
            Assert.Equal(declared.VaultName, known.VaultName);
            Assert.Equal(ProviderOrigin.BuiltIn, declared.Origin);
        }

        // What an application sees is pinned, since the derivation must not change it: the host, how its own request carries the key
        // (not what the runtime's client sends — Anthropic's SDK presents it as a bearer) and where OpenAI-shaped chat is answered.
        Assert.Equal(
            [
                ("openai", "api.openai.com", KeyStyle.Bearer, "v1/"),
                ("anthropic", "api.anthropic.com", KeyStyle.ApiKeyHeader, "v1/"),
                ("google", "generativelanguage.googleapis.com", KeyStyle.Google, "v1beta/openai/"),
                ("groq", "api.groq.com", KeyStyle.Bearer, "openai/v1/"),
                ("openrouter", "openrouter.ai", KeyStyle.Bearer, "api/v1/"),
                ("mistral", "api.mistral.ai", KeyStyle.Bearer, "v1/"),
            ],
            LlmProviders.All.Select(p => (p.Id, p.Host, p.Style, p.OpenAICompatiblePath)));

        // The format the runtime already uses for each: OpenAI over Responses, Anthropic over Messages, Google over Gemini, the rest over Chat.
        Assert.Equal(
            [ProviderApi.OpenAIResponses, ProviderApi.AnthropicMessages, ProviderApi.GoogleGemini, ProviderApi.OpenAIChat, ProviderApi.OpenAIChat, ProviderApi.OpenAIChat],
            ProviderDeclaration.BuiltIn.Select(d => d.Api));
    }
}

public sealed class ProviderStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("providers-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ProviderDeclaration Declared(string id, string address = "https://p.example/", ProviderOrigin origin = ProviderOrigin.Person)
    {
        Assert.True(ProviderDeclaration.TryParse($$"""{ "id": "{{id}}", "baseUrl": "{{address}}", "api": "openai-chat", "models": [ { "id": "m" } ] }""", origin, out var declaration, out var problem), problem);
        return declaration!;
    }

    [Fact]
    public async Task What_the_person_added_is_kept_and_read_back()
    {
        Assert.Empty(ProviderStore.Read(_root));

        await ProviderStore.WriteAsync(_root, [Declared("one"), Declared("two", "http://127.0.0.1:8080/")], CancellationToken.None);

        var read = ProviderStore.Read(_root);
        Assert.Equal(["one", "two"], read.Select(d => d.Id));
        Assert.All(read, d => Assert.Equal(ProviderOrigin.Person, d.Origin));
        Assert.Equal(new Uri("http://127.0.0.1:8080/"), read[1].BaseUrl);
        Assert.DoesNotContain("sk-", await File.ReadAllTextAsync(Path.Combine(_root, "providers.json")));

        await ProviderStore.WriteAsync(_root, [], CancellationToken.None);
        Assert.Empty(ProviderStore.Read(_root));
        Assert.False(File.Exists(Path.Combine(_root, "providers.json")));
    }

    [Fact]
    public void An_entry_that_cannot_be_used_does_not_take_the_others_with_it()
    {
        File.WriteAllText(Path.Combine(_root, "providers.json"), """
            { "format": "bohm.providers/0", "providers": [
                { "id": "good", "baseUrl": "https://p.example/", "api": "openai-chat" },
                { "id": "Bad Id", "baseUrl": "https://p.example/", "api": "openai-chat" },
                { "id": "good", "baseUrl": "https://other.example/", "api": "openai-chat" },
                { "id": "also-good", "baseUrl": "https://q.example/", "api": "anthropic-messages" } ] }
            """);
        Assert.Equal(["good", "also-good"], ProviderStore.Read(_root).Select(d => d.Id));   // a repeated id keeps the first
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "format": "something-else/9", "providers": [] }""")]
    [InlineData("""{ "format": "bohm.providers/0" }""")]
    [InlineData("[]")]
    public void A_file_that_cannot_be_used_is_no_providers_added(string content)
    {
        File.WriteAllText(Path.Combine(_root, "providers.json"), content);
        Assert.Empty(ProviderStore.Read(_root));
    }

    [Fact]
    public void Where_an_id_is_in_more_than_one_source_the_administrators_stands_over_the_persons_over_the_built_in()
    {
        var builtIn = ProviderDeclaration.BuiltIn;
        var person = new[] { Declared("openai", "https://my-gateway.example/v1/"), Declared("mine") };
        var policy = new[] { Declared("mine", "https://policy.example/", ProviderOrigin.Policy) };

        var merged = ProviderStore.Merge(builtIn, person, policy);

        Assert.Equal(builtIn.Select(d => d.Id).Append("mine"), merged.Select(d => d.Id));   // built-in order first, new ids after
        Assert.Equal(new Uri("https://my-gateway.example/v1/"), merged.Single(d => d.Id == "openai").BaseUrl);
        Assert.Equal(ProviderOrigin.Person, merged.Single(d => d.Id == "openai").Origin);
        Assert.Equal(new Uri("https://policy.example/"), merged.Single(d => d.Id == "mine").BaseUrl);

        // The order the sources are given in does not matter: origin decides.
        var reversed = ProviderStore.Merge(policy, person, builtIn);
        Assert.Equal(new Uri("https://policy.example/"), reversed.Single(d => d.Id == "mine").BaseUrl);
        Assert.Equal(new Uri("https://my-gateway.example/v1/"), reversed.Single(d => d.Id == "openai").BaseUrl);
    }
}
