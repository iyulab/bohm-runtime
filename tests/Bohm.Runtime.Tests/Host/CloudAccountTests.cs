using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The Bohm account: chosen like a provider only when the runtime was given the cloud's address, asked through the cloud's
/// relay in Anthropic's own API with the account's access token, counted as sent to the cloud — and its refusals told by
/// their code, the one whose credits cannot hold the output limit asked once more with the limit they can.
/// </summary>
public sealed class CloudAccountTests : IAsyncLifetime
{
    private const string App = """<!doctype html><title>Tasks</title><button onclick="add()">Add Task</button>""";
    private const string Token = "account-token-0123456789";

    private FakeProvider _cloud = null!;
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _cloud = await FakeProvider.StartAsync();
        _host = await RunningHost.StartAsync(configure: o => o with { CloudEndpoint = _cloud.Address });
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _cloud.DisposeAsync();
    }

    [Fact]
    public async Task Without_the_clouds_address_the_account_cannot_be_chosen_and_has_no_token_to_keep()
    {
        await using var host = await RunningHost.StartAsync();

        using var chose = await host.ControlClient().PutAsync("/__control/edit/model", Choice("bohm", "model-x"));
        HttpAssert.Status(HttpStatusCode.BadRequest, chose);
        using var account = await host.ControlClient().GetAsync("/__control/cloud/account");
        HttpAssert.Status(HttpStatusCode.NotFound, account);
        using var token = await host.ControlClient().PutAsync("/__control/cloud/account/token", new StringContent(Token));
        HttpAssert.Status(HttpStatusCode.NotFound, token);
    }

    [Fact]
    public async Task The_account_without_its_token_is_missing_the_account_and_nothing_is_sent()
    {
        var account = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/cloud/account")).RootElement;
        Assert.Equal(_cloud.Address.Authority, account.GetProperty("host").GetString());
        Assert.False(account.GetProperty("signedIn").GetBoolean());

        using (var chose = await _host.ControlClient().PutAsync("/__control/edit/model", Choice("bohm", "model-x")))
        {
            HttpAssert.Status(HttpStatusCode.OK, chose);
            var missing = JsonDocument.Parse(await chose.Content.ReadAsStringAsync()).RootElement.GetProperty("missing");
            Assert.Equal("account", missing.GetProperty("needs").GetString());
            Assert.Equal("bohm", missing.GetProperty("provider").GetString());
        }

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.Conflict, response);
        Assert.Empty(_cloud.Received);
    }

    [Fact]
    public async Task The_account_is_asked_through_the_clouds_relay_in_Anthropics_API_with_its_token_and_it_is_counted_as_sent()
    {
        await SignInAsync();

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal("bohm/model-x", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("model").GetString());
        var request = Assert.Single(_cloud.Received);
        Assert.Equal("/anthropic/v1/messages", request.PathAndQuery);
        Assert.Equal(Token, request.Headers["x-api-key"]);
        Assert.Equal("model-x", JsonDocument.Parse(request.Body).RootElement.GetProperty("model").GetString());

        var sent = Assert.Single(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(_cloud.Address.Authority, sent.GetProperty("host").GetString());

        // Signing out takes the token away: the account is missing again.
        using var signedOut = await _host.ControlClient().DeleteAsync("/__control/cloud/account/token");
        Assert.False(JsonDocument.Parse(await signedOut.Content.ReadAsStringAsync()).RootElement.GetProperty("signedIn").GetBoolean());
        Assert.Equal("account", JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/edit/model")).RootElement.GetProperty("missing").GetProperty("needs").GetString());
    }

    [Fact]
    public async Task A_request_the_credits_cannot_hold_is_asked_once_more_with_the_output_limit_they_can()
    {
        await SignInAsync();
        _cloud.RefusalsFirst.Enqueue((402, Refused("hold_rejected", affordableMaxTokens: 4000)));

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal(2, _cloud.Received.Count);
        // Streamed both times: the refusal comes before anything streams, so the second ask repeats nothing already told.
        Assert.All(_cloud.Received, r => Assert.True(JsonDocument.Parse(r.Body).RootElement.GetProperty("stream").GetBoolean()));
        Assert.Equal(4000, JsonDocument.Parse(_cloud.Received.Last().Body).RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Refused_again_after_asking_with_a_smaller_limit_the_person_is_told_the_accounts_code_and_its_message()
    {
        await SignInAsync();
        _cloud.Refusal = (402, Refused("hold_rejected", affordableMaxTokens: 4000));

        var provider = await RefusedAsync();

        Assert.Equal(2, _cloud.Received.Count);
        Assert.Equal(402, provider.GetProperty("status").GetInt32());
        Assert.True(provider.GetProperty("billing").GetBoolean());
        Assert.Equal("hold_rejected", provider.GetProperty("code").GetString());
        Assert.Equal("The account's credits cannot hold this answer.", provider.GetProperty("message").GetString());
        Assert.Equal(4000, provider.GetProperty("affordableMaxTokens").GetInt64());
    }

    [Theory]
    [InlineData("hold_rejected", 1000)]    // too small a limit to answer in — not worth asking
    [InlineData("balance_exhausted", null)]
    [InlineData("tag_limit_reached", null)]
    public async Task The_other_refusals_of_the_account_are_not_asked_again(string code, int? affordableMaxTokens)
    {
        await SignInAsync();
        _cloud.Refusal = (402, Refused(code, affordableMaxTokens));

        var provider = await RefusedAsync();

        Assert.Single(_cloud.Received);
        Assert.True(provider.GetProperty("billing").GetBoolean());
        Assert.Equal(code, provider.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("https://cloud.example", "https://cloud.example/")]
    [InlineData("https://cloud.example/base", "https://cloud.example/base/")]
    [InlineData("http://127.0.0.1:5077/", "http://127.0.0.1:5077/")]
    [InlineData("http://cloud.example/", null)]          // plain http only on this computer
    [InlineData("https://user:pass@cloud.example/", null)]
    [InlineData("https://cloud.example/?a=1", null)]
    [InlineData("cloud.example", null)]
    [InlineData("", null)]
    public void The_clouds_address_is_https_or_http_on_this_computer_and_ends_in_a_slash(string value, string? expected) =>
        Assert.Equal(expected, CloudAccount.EndpointOf(value)?.AbsoluteUri);

    private static string Refused(string code, int? affordableMaxTokens) =>
        JsonSerializer.Serialize(new { type = "error", error = new { type = code, message = "The account's credits cannot hold this answer.", affordable = 40, affordable_max_tokens = affordableMaxTokens } });

    private async Task SignInAsync()
    {
        using (var token = await _host.ControlClient().PutAsync("/__control/cloud/account/token", new StringContent(Token)))
            Assert.True(JsonDocument.Parse(await token.Content.ReadAsStringAsync()).RootElement.GetProperty("signedIn").GetBoolean());
        using var chose = await _host.ControlClient().PutAsync("/__control/edit/model", Choice("bohm", "model-x"));
        HttpAssert.Status(HttpStatusCode.OK, chose);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await chose.Content.ReadAsStringAsync()).RootElement.GetProperty("missing").ValueKind);
    }

    private async Task<JsonElement> RefusedAsync()
    {
        using var response = await ProposeAsync(await _host.AdoptAsync(App));
        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("provider").Clone();
    }

    private static StringContent Choice(string provider, string model) =>
        new(JsonSerializer.Serialize(new { provider, model }), Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> ProposeAsync(string id) =>
        _host.ControlClient().PostAsync($"/__control/apps/{id}/proposals", new StringContent(
            """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
            Encoding.UTF8, "application/json"));
}
