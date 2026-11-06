using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A turn may name the model it is asked of for itself alone (<c>model: { provider, model, effort? }</c>): the choice the person
/// remembered stands for every turn that does not, whatever one turn asked. One theory, several shapes of request.
/// </summary>
public sealed class TurnModelTests : IAsyncLifetime
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("bohm-turn-model-").FullName;
    private FakeProvider _provider = null!;
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync();
        Assert.True(ProviderDeclaration.TryParse($$"""
            { "id": "srv", "baseUrl": "{{_provider.Address}}", "api": "openai-chat", "auth": { "style": "none" },
              "models": [ { "id": "remembered" }, { "id": "thinker", "reasoning": true }, { "id": "plain", "reasoning": false }, { "id": "unknown" } ] }
            """, ProviderOrigin.Person, out var none, out var problem), problem);
        Assert.True(ProviderDeclaration.TryParse($$"""
            { "id": "keyed", "baseUrl": "{{_provider.Address}}", "api": "openai-chat", "models": [ { "id": "m", "reasoning": true } ] }
            """, ProviderOrigin.Person, out var keyed, out problem), problem);
        await ProviderStore.WriteAsync(_dataRoot, [none!, keyed!], CancellationToken.None);
        _host = await RunningHost.StartAsync(_dataRoot);
        using var chose = await _host.ControlClient().PutAsync("/__control/agent/model", Json("""{"provider":"srv","model":"remembered"}"""));
        HttpAssert.Status(HttpStatusCode.OK, chose);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> TurnAsync(string extra) =>
        _host.ControlClient().PostAsync("/__control/agent/turns", Json($$"""{"messages":[{"role":"user","text":"Hi"}]{{extra}}}"""));

    private async Task AssertRememberedChoiceStandsAsync()
    {
        var shown = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/agent/model")).RootElement;
        Assert.Equal(("srv", "remembered"), (shown.GetProperty("provider").GetString(), shown.GetProperty("model").GetString()));
        Assert.Equal("remembered", JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dataRoot, "agent-model.json"))).RootElement.GetProperty("model").GetString());
    }

    private string AskedModel() => JsonDocument.Parse(_provider.Asked[^1].Body).RootElement.GetProperty("model").GetString()!;

    [Fact]
    public async Task A_turn_asks_the_model_it_names_and_changes_nothing_that_is_remembered()
    {
        // Without the field: the remembered model, as before.
        using (var plain = await TurnAsync(""))
        {
            HttpAssert.Status(HttpStatusCode.OK, plain);
            Assert.Equal("srv/remembered", JsonDocument.Parse(await plain.Content.ReadAsStringAsync()).RootElement.GetProperty("model").GetString());
        }

        // With it: that model, for this turn alone.
        using (var named = await TurnAsync(""","model":{"provider":"srv","model":"thinker","effort":"high"}"""))
        {
            HttpAssert.Status(HttpStatusCode.OK, named);
            Assert.Equal("srv/thinker", JsonDocument.Parse(await named.Content.ReadAsStringAsync()).RootElement.GetProperty("model").GetString());
        }

        Assert.Equal("thinker", AskedModel());
        Assert.Contains("\"high\"", _provider.Asked[^1].Body);   // the level went out with the request
        await AssertRememberedChoiceStandsAsync();

        // The next turn without the field is back on the remembered model, and no level goes out.
        using (var after = await TurnAsync(""))
            Assert.Equal("srv/remembered", JsonDocument.Parse(await after.Content.ReadAsStringAsync()).RootElement.GetProperty("model").GetString());
        Assert.Equal("remembered", AskedModel());
        Assert.DoesNotContain("reasoning_effort\":\"high", _provider.Asked[^1].Body);
    }

    [Fact]
    public async Task A_model_without_an_effort_is_asked_the_way_the_remembered_one_is()
    {
        using var named = await TurnAsync(""","model":{"provider":"srv","model":"unknown"}""");

        HttpAssert.Status(HttpStatusCode.OK, named);
        Assert.Equal("unknown", AskedModel());
        await AssertRememberedChoiceStandsAsync();
    }

    [Theory]
    [InlineData("plain")]     // declared as not thinking
    [InlineData("unknown")]   // nobody said: a setting it may refuse is not sent
    public async Task A_level_is_refused_for_a_model_not_known_to_think_and_nothing_is_asked(string model)
    {
        using var refused = await TurnAsync($$""","model":{"provider":"srv","model":"{{model}}","effort":"low"}""");

        HttpAssert.Status(HttpStatusCode.BadRequest, refused);
        Assert.Equal("effort-unsupported", JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString());
        Assert.Empty(_provider.Asked);
        await AssertRememberedChoiceStandsAsync();
    }

    [Fact]
    public async Task A_provider_whose_key_is_not_connected_is_missing_for_the_turn_and_not_for_the_next()
    {
        using var missing = await TurnAsync(""","model":{"provider":"keyed","model":"m"}""");

        HttpAssert.Status(HttpStatusCode.Conflict, missing);
        var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(("key", "keyed"), (body.GetProperty("needs").GetString(), body.GetProperty("provider").GetString()));
        Assert.Empty(_provider.Asked);

        using var next = await TurnAsync("");
        HttpAssert.Status(HttpStatusCode.OK, next);
        await AssertRememberedChoiceStandsAsync();
    }

    [Fact]
    public async Task The_model_on_this_computer_can_be_named_for_a_turn_and_is_missing_while_none_is_set()
    {
        // Named by its provider alone (no model name to read); with none set, what is missing is said, as for the default.
        using var missing = await TurnAsync(""","model":{"provider":"local"}""");

        HttpAssert.Status(HttpStatusCode.Conflict, missing);
        var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("localModel", body.GetProperty("needs").GetString());
        Assert.Empty(_provider.Asked);
        await AssertRememberedChoiceStandsAsync();
    }

    [Theory]
    [InlineData(""","model":{"provider":"srv"}""", "model-unknown")]
    [InlineData(""","model":{"provider":"nowhere","model":"m"}""", "model-unknown")]
    [InlineData(""","model":{"provider":"srv","model":"  "}""", "model-unknown")]
    [InlineData(""","model":{"provider":"srv","model":"thinker","effort":"max"}""", "bad-request")]
    [InlineData(""","model":5""", "bad-request")]
    [InlineData(""","model":{"model":"thinker"}""", "bad-request")]
    public async Task A_model_that_cannot_be_chosen_or_a_level_that_is_not_one_is_a_bad_request(string extra, string reason)
    {
        using var refused = await TurnAsync(extra);

        HttpAssert.Status(HttpStatusCode.BadRequest, refused);
        if (reason != "bad-request") Assert.Equal(reason, JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString());
        Assert.Empty(_provider.Asked);
        await AssertRememberedChoiceStandsAsync();
    }
}
