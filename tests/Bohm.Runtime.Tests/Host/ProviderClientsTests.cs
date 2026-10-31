using System.Net;
using System.Text.Json;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Bohm.Runtime.Tests.Host;

public sealed class ProviderClientsTests : IAsyncLifetime
{
    private const string Key = "sk-declared-0123456789";

    private FakeProvider _provider = null!;

    public async ValueTask InitializeAsync() => _provider = await FakeProvider.StartAsync();

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    private ProviderDeclaration Declared(string api, string auth = """{ "style": "bearer" }""", string model = """{ "id": "m" }""")
    {
        var json = $$"""{ "id": "declared", "baseUrl": "{{_provider.Address}}", "api": "{{api}}", "auth": {{auth}}, "models": [ {{model}} ] }""";
        Assert.True(ProviderDeclaration.TryParse(json, ProviderOrigin.Person, out var declaration, out var problem), problem);
        return declaration!;
    }

    private async Task<FakeProvider.ReceivedRequest> AskAsync(ProviderDeclaration provider, string? key = Key)
    {
        var model = provider.Models[0];
        using var client = ProviderClients.Create(provider, model, model.Id, key);
        // Streamed, as the runtime asks.
        var text = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) text.Append(update.Text);
        Assert.Equal(FakeProvider.Reply, text.ToString().Trim());   // the fake streams word by word
        return Assert.Single(_provider.Asked);
    }

    [Theory]
    [InlineData("openai-chat", """{ "id": "m" }""", "/chat/completions")]
    [InlineData("openai-responses", """{ "id": "m", "path": "v1" }""", "/v1/responses")]   // the model's path puts its API under /v1
    [InlineData("anthropic-messages", """{ "id": "m" }""", "/v1/messages")]
    public async Task A_declared_provider_is_asked_in_the_format_it_declares(string api, string model, string path)
    {
        var request = await AskAsync(Declared(api, model: model));

        Assert.Equal("POST", request.Method);
        Assert.EndsWith(path, request.PathAndQuery);
    }

    [Fact]
    public async Task One_gateway_serves_models_in_different_formats_by_the_model()
    {
        var gateway = Declared("openai-chat", model: """{ "id": "m", "api": "anthropic-messages", "path": "anthropic" }""");

        var request = await AskAsync(gateway);

        Assert.Equal("/anthropic/v1/messages", request.PathAndQuery);
    }

    [Fact]
    public async Task A_bearer_key_goes_in_the_authorization_header()
    {
        var request = await AskAsync(Declared("openai-chat"));
        Assert.Equal("Bearer " + Key, request.Headers["Authorization"]);
    }

    [Fact]
    public async Task A_key_in_a_header_of_the_declared_name_is_sent_there_and_not_as_a_bearer()
    {
        var request = await AskAsync(Declared("openai-chat", auth: """{ "style": "header", "name": "x-gateway-key" }"""));

        Assert.Equal(Key, request.Headers["x-gateway-key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task A_provider_that_takes_no_key_is_asked_without_one_even_when_one_is_at_hand()
    {
        var request = await AskAsync(Declared("openai-chat", auth: """{ "style": "none" }"""), key: Key);

        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.DoesNotContain(Key, request.Body);
    }

    [Fact]
    public async Task Anthropics_key_goes_through_the_SDKs_own_slot_which_presents_it_as_that_API_version_expects()
    {
        var request = await AskAsync(Declared("anthropic-messages"));

        Assert.Equal("Bearer " + Key, request.Headers["Authorization"]);
        Assert.False(request.Headers.ContainsKey("x-api-key"));
    }

    // What a person's own tools leave on a machine: the variables the vendors' SDKs read when they are not handed a credential.
    private static readonly string[] AmbientVariables =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "GOOGLE_API_KEY", "GEMINI_API_KEY", "OPENAI_API_KEY",
    ];

    [Theory]
    [InlineData("anthropic-messages", """{ "style": "header", "name": "x-gateway-key" }""", Key)]
    [InlineData("anthropic-messages", """{ "style": "none" }""", null)]
    [InlineData("anthropic-messages", """{ "style": "bearer" }""", null)]
    [InlineData("google-gemini", """{ "style": "google" }""", null)]
    [InlineData("google-gemini", """{ "style": "none" }""", null)]
    [InlineData("openai-chat", """{ "style": "bearer" }""", null)]
    [InlineData("openai-responses", """{ "style": "bearer" }""", null)]
    public async Task A_key_kept_on_the_machine_is_never_sent_to_a_provider_the_person_declared(string api, string auth, string? key)
    {
        // The SDKs fill an empty credential slot from the machine; only the key the person gave this provider may travel to it.
        var profile = Directory.CreateTempSubdirectory("bohm-vendor-profile-");
        var before = AmbientVariables.Append("ANTHROPIC_CONFIG_DIR").ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in AmbientVariables) Environment.SetEnvironmentVariable(name, "ambient-must-stay-home");
            // A profile of the vendor's own tools, with no credentials file: the failure seen on a machine that had run them was a throw.
            Directory.CreateDirectory(Path.Combine(profile.FullName, "credentials"));
            File.WriteAllText(Path.Combine(profile.FullName, "active_config"), "default");
            Environment.SetEnvironmentVariable("ANTHROPIC_CONFIG_DIR", profile.FullName);

            var model = api == "openai-responses" ? """{ "id": "m", "path": "v1" }""" : """{ "id": "m" }""";   // the fake serves Responses under /v1
            var request = await AskAsync(Declared(api, auth, model), key);

            Assert.DoesNotContain(request.Headers, h => h.Value.Contains("must-stay-home"));
            Assert.DoesNotContain("must-stay-home", request.PathAndQuery);
            Assert.DoesNotContain("must-stay-home", request.Body);
            if (key is not null) Assert.Equal(key, request.Headers["x-gateway-key"]);
        }
        finally
        {
            foreach (var (name, value) in before) Environment.SetEnvironmentVariable(name, value);
            profile.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_gateway_in_front_of_Anthropic_that_wants_the_key_in_a_header_of_its_own_gets_it_there()
    {
        var request = await AskAsync(Declared("anthropic-messages", """{ "style": "header", "name": "x-gateway-key" }"""));

        Assert.Equal(Key, request.Headers["x-gateway-key"]);
    }
}

public sealed class ProviderChoiceTests : IAsyncLifetime
{
    private const string Key = "sk-declared-0123456789";

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("bohm-choice-").FullName;
    private FakeProvider _provider = null!;
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync();
        Assert.True(ProviderDeclaration.TryParse($$"""
            { "id": "ollama", "name": "Ollama", "baseUrl": "{{_provider.Address}}", "api": "openai-chat", "auth": { "style": "none" },
              "models": [ { "id": "llama", "contextWindow": 8192, "maxTokens": 1024, "input": ["text", "image"] } ] }
            """, ProviderOrigin.Person, out var none, out var problem), problem);
        Assert.True(ProviderDeclaration.TryParse($$"""
            { "id": "keyed", "baseUrl": "{{_provider.Address}}", "api": "openai-chat", "models": [ { "id": "m" } ] }
            """, ProviderOrigin.Person, out var keyed, out problem), problem);
        await ProviderStore.WriteAsync(_dataRoot, [none!, keyed!], CancellationToken.None);
        _host = await RunningHost.StartAsync(_dataRoot);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private async Task ChooseAsync(string provider, string model)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { provider, model }), System.Text.Encoding.UTF8, "application/json");
        using var response = await _host.ControlClient().PutAsync("/__control/agent/model", content);
        HttpAssert.Status(HttpStatusCode.OK, response);
    }

    [Fact]
    public async Task A_provider_that_takes_no_key_can_be_chosen_and_asked_with_what_its_declaration_knows_of_the_model()
    {
        await ChooseAsync("ollama", "llama");
        var agent = _host.Services.GetRequiredService<AgentModel>();

        Assert.Null(agent.Missing);
        var chosen = await agent.GetAsync(CancellationToken.None);

        Assert.NotNull(chosen);
        Assert.Equal("ollama/llama", chosen.Name);
        Assert.Equal(new ModelLimits(8192, 1024), chosen.Limits);
        Assert.True(chosen.Sees);
        var response = await chosen.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal(FakeProvider.Reply, response.Text);
        Assert.False(Assert.Single(_provider.Asked).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task A_provider_the_person_added_is_fitted_to_what_its_model_takes()
    {
        await ChooseAsync("ollama", "llama");   // declared: a context window of 8192 and answers of up to 1024
        var chosen = await _host.Services.GetRequiredService<AgentModel>().GetAsync(CancellationToken.None);

        Assert.NotNull(chosen!.Client.GetService<ModelFitChatClient>());
        Assert.Equal(new ModelLimits(8192, 1024), ModelLimits.Of(chosen.Client, chosen.Limits));
        await chosen.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { MaxOutputTokens = 8000 });

        var body = Assert.Single(_provider.Asked).Body;
        Assert.Contains("1024", body);               // the model's own bound, not the 8000 the feature asked for
        Assert.DoesNotContain("8000", body);
    }

    [Fact]
    public async Task A_model_learned_to_think_is_known_to_the_next_start_and_to_the_next_client_before_it_is_asked()
    {
        _provider.Thinks = true;
        await ChooseAsync("ollama", "llama");   // declared without a word on whether it thinks
        var chosen = await _host.Services.GetRequiredService<AgentModel>().GetAsync(CancellationToken.None);
        Assert.Null(ModelLimits.Of(chosen!.Client, chosen.Limits).Reasoning);

        await chosen.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.True(ModelLimits.Of(chosen.Client, chosen.Limits).Reasoning);
        var address = _host.Services.GetRequiredService<ProviderCatalog>().ById("ollama")!.BaseUrl;
        Assert.True(new LearnedThinking(new Bohm.Runtime.Host.RuntimeHostOptions { DataRoot = _host.DataRoot }).Thinks(address, "llama"));   // a later start reads the file

        // Another use of the same model, with a client of its own, knows it from the first request.
        using var edit = new StringContent("""{"provider":"ollama","model":"llama"}""", System.Text.Encoding.UTF8, "application/json");
        using var put = await _host.ControlClient().PutAsync("/__control/edit/model", edit);
        HttpAssert.Status(HttpStatusCode.OK, put);
        var later = await _host.Services.GetRequiredService<EditModel>().GetAsync(CancellationToken.None);
        Assert.NotSame(chosen.Client, later!.Client);
        Assert.True(ModelLimits.Of(later.Client, later.Limits).Reasoning);
    }

    private async Task<ChosenEditModel> ChooseOpenAsync(string modelJson)
    {
        Assert.True(ProviderDeclaration.TryParse($$"""
            { "id": "open", "baseUrl": "{{_provider.Address}}", "api": "openai-chat", "auth": { "style": "none" }, "models": [ {{modelJson}} ] }
            """, ProviderOrigin.Person, out var open, out var problem), problem);
        await _host.Services.GetRequiredService<ProviderCatalog>().AddAsync(open!, CancellationToken.None);
        await ChooseAsync("open", "w");
        return (await _host.Services.GetRequiredService<AgentModel>().GetAsync(CancellationToken.None))!;
    }

    private int ModelListRequests => _provider.Received.Count(r => r.Method == "GET" && r.PathAndQuery.EndsWith("/models", StringComparison.Ordinal));

    [Fact]
    public async Task A_model_nobody_gave_a_context_window_is_asked_of_its_server_once()
    {
        _provider.Models = """{"object":"list","data":[{"id":"w","object":"model","max_model_len":16384}]}""";

        var chosen = await ChooseOpenAsync("""{ "id": "w" }""");

        Assert.Equal(16384, ModelLimits.Of(chosen.Client, chosen.Limits).ContextWindow);
        Assert.Equal(1, ModelListRequests);
        await _host.Services.GetRequiredService<AgentModel>().GetAsync(CancellationToken.None);
        Assert.Equal(1, ModelListRequests);   // the same client is not asked again
    }

    [Fact]
    public async Task A_declared_context_window_is_not_asked_of_the_server()
    {
        _provider.Models = """{"object":"list","data":[{"id":"w","object":"model","max_model_len":16384}]}""";

        var chosen = await ChooseOpenAsync("""{ "id": "w", "contextWindow": 8192 }""");

        Assert.Equal(8192, ModelLimits.Of(chosen.Client, chosen.Limits).ContextWindow);
        Assert.Equal(0, ModelListRequests);
    }

    [Fact]
    public async Task A_server_that_reports_no_window_leaves_it_unknown_and_the_request_goes_on()
    {
        _provider.Models = """{"object":"list","data":[{"id":"w","object":"model"}]}""";

        var chosen = await ChooseOpenAsync("""{ "id": "w" }""");

        Assert.Null(ModelLimits.Of(chosen.Client, chosen.Limits).ContextWindow);
        Assert.Equal(FakeProvider.Reply, (await chosen.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")])).Text);
    }

    [Fact]
    public async Task A_model_the_declaration_says_does_not_think_is_not_marked_by_what_it_sends()
    {
        _provider.Thinks = true;
        Assert.True(ProviderDeclaration.TryParse($$"""
            { "id": "plain", "baseUrl": "{{_provider.Address}}", "api": "openai-chat", "auth": { "style": "none" }, "models": [ { "id": "p", "reasoning": false } ] }
            """, ProviderOrigin.Person, out var plain, out var problem), problem);
        await _host.Services.GetRequiredService<ProviderCatalog>().AddAsync(plain!, CancellationToken.None);
        await ChooseAsync("plain", "p");
        var chosen = await _host.Services.GetRequiredService<AgentModel>().GetAsync(CancellationToken.None);

        await chosen!.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.False(ModelLimits.Of(chosen.Client, chosen.Limits).Reasoning);
        Assert.False(new LearnedThinking(new Bohm.Runtime.Host.RuntimeHostOptions { DataRoot = _host.DataRoot }).Thinks(plain!.BaseUrl, "p"));
    }

    [Fact]
    public async Task A_provider_that_takes_a_key_is_missing_it_until_it_is_connected()
    {
        await ChooseAsync("keyed", "m");
        var agent = _host.Services.GetRequiredService<AgentModel>();

        Assert.Equal(new EditModelMissing("key", "keyed"), agent.Missing);
        Assert.Null(await agent.GetAsync(CancellationToken.None));
        Assert.Empty(_provider.Asked);

        _host.Services.GetRequiredService<ICredentialVault>().Write("llm/keyed", Key);
        Assert.Null(agent.Missing);
        var chosen = await agent.GetAsync(CancellationToken.None);
        var response = await chosen!.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal(FakeProvider.Reply, response.Text);
        Assert.Equal("Bearer " + Key, Assert.Single(_provider.Asked).Headers["Authorization"]);
        Assert.Null(chosen.Sees);   // a model nobody described, at an OpenAI-compatible base: asked, not assumed
    }

    [Fact]
    public async Task A_choice_whose_provider_has_been_taken_away_is_no_longer_a_choice()
    {
        await ChooseAsync("ollama", "llama");
        var agent = _host.Services.GetRequiredService<AgentModel>();
        Assert.NotNull(agent.Chosen);

        await _host.Services.GetRequiredService<ProviderCatalog>().RemoveAsync("ollama", CancellationToken.None);

        Assert.Null(agent.Chosen);   // back to the model on this computer
        Assert.Equal("localModel", agent.Missing!.Needs);
    }

    [Fact]
    public async Task An_unknown_provider_cannot_be_chosen()
    {
        using var content = new StringContent("""{"provider":"nowhere","model":"m"}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await _host.ControlClient().PutAsync("/__control/agent/model", content);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
