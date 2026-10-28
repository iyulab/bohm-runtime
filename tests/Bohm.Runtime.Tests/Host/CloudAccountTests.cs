using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The Bohm account: chosen like a provider only when the runtime was given the cloud's address, asked in Anthropic's own
/// API at the metering gateway the cloud names (<c>GET me</c>) with the account's access token, counted as sent to that
/// gateway — and its refusals told by the gateway's code, the one whose credits cannot hold the output limit asked once
/// more with the limit they can. Signing in is at the identity service the cloud names (its protected resource metadata),
/// through the loopback; the session is kept in the vault and refreshed here, and signing out revokes it.
/// </summary>
public sealed class CloudAccountTests : IAsyncLifetime
{
    private const string App = """<!doctype html><title>Tasks</title><button onclick="add()">Add Task</button>""";
    private FakeIdentity _identity = null!;
    private FakeProvider _cloud = null!;
    private FakeProvider _gateway = null!;
    private RunningHost _host = null!;
    private readonly Bohm.Runtime.Credentials.MemoryCredentialVault _vault = new();

    public async ValueTask InitializeAsync()
    {
        _identity = await FakeIdentity.StartAsync();
        _cloud = await FakeProvider.StartAsync();
        _gateway = await FakeProvider.StartAsync();
        _cloud.Answers["/.well-known/oauth-protected-resource"] = JsonSerializer.Serialize(new { resource = _cloud.Address.AbsoluteUri, authorization_servers = new[] { _identity.Issuer } });
        _cloud.Answers["/me"] = JsonSerializer.Serialize(new { account = "a", ai = new { anthropic = new Uri(_gateway.Address, "anthropic").AbsoluteUri }, links = true });
        _host = await RunningHost.StartAsync(configure: o => o with { CloudEndpoint = _cloud.Address, Vault = _vault });
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _cloud.DisposeAsync();
        await _gateway.DisposeAsync();
        await _identity.DisposeAsync();
    }

    /// <summary>The account's access token now — the one the identity service handed out last.</summary>
    private string Token => _identity.AccessToken!;

    /// <summary>What reached the cloud apart from looking up where to sign in.</summary>
    private List<FakeProvider.ReceivedRequest> CloudCalls => [.. _cloud.Received.Where(r => !r.PathAndQuery.StartsWith("/.well-known/", StringComparison.Ordinal))];

    [Fact]
    public async Task Without_the_clouds_address_the_account_cannot_be_chosen_and_has_no_token_to_keep()
    {
        await using var host = await RunningHost.StartAsync();

        using var chose = await host.ControlClient().PutAsync("/__control/edit/model", Choice("bohm", "model-x"));
        HttpAssert.Status(HttpStatusCode.BadRequest, chose);
        using var account = await host.ControlClient().GetAsync("/__control/cloud/account");
        HttpAssert.Status(HttpStatusCode.NotFound, account);
        using var signIn = await host.ControlClient().PostAsync("/__control/cloud/account/sign-in", null);
        HttpAssert.Status(HttpStatusCode.NotFound, signIn);
    }



    [Fact]
    public async Task Looking_at_the_account_tells_its_credits_asked_of_the_cloud_with_its_token()
    {
        _cloud.Answers["/me/balance"] = """{"balance":187,"lots":[]}""";

        var kept = await SignInOnlyAsync();
        Assert.True(kept.GetProperty("signedIn").GetBoolean());
        Assert.Equal(187, kept.GetProperty("balance").GetInt64());
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("problem").ValueKind);

        var looked = await AccountAsync(await _host.ControlClient().GetAsync("/__control/cloud/account"));
        Assert.Equal(187, looked.GetProperty("balance").GetInt64());
        Assert.True(looked.GetProperty("links").GetBoolean());   // the cloud says it keeps links
        Assert.Equal("a", looked.GetProperty("account").GetString());   // the account id the cloud names, for the person to read out

        // A cloud that does not keep links: «send as a link» is not offered.
        _cloud.Answers["/me"] = JsonSerializer.Serialize(new { account = "a", ai = new { anthropic = new Uri(_gateway.Address, "anthropic").AbsoluteUri }, links = false });
        Assert.False((await AccountAsync(await _host.ControlClient().GetAsync("/__control/cloud/account"))).GetProperty("links").GetBoolean());
        Assert.All(CloudCalls, r => Assert.Equal("Bearer " + Token, r.Headers["Authorization"]));
        // Asking for its own balance carries nothing of an application's: not counted as data sent.
        Assert.Empty(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());

        var asked = CloudCalls.Count;
        var signedOut = await AccountAsync(await _host.ControlClient().DeleteAsync("/__control/cloud/account/session"));
        Assert.Equal("signedOut", signedOut.GetProperty("problem").GetString());
        Assert.Equal(JsonValueKind.Null, signedOut.GetProperty("account").ValueKind);
        Assert.Equal(asked, CloudCalls.Count);   // signed out, the cloud is not asked
    }

    [Fact]
    public async Task A_saved_screen_is_kept_as_a_link_under_the_account_and_counted_as_sent()
    {
        _cloud.Answers["/me/balance"] = """{"balance":10}""";
        _cloud.Posted["/me/links"] = (201, """{"id":"abc","url":"/l/abc","expiresAt":"2026-11-11T09:00:00+00:00"}""");
        await SignInOnlyAsync();

        var kept = await AccountAsync(await _host.ControlClient().PostAsync("/__control/cloud/links", new StringContent("<p>Stock</p>", Encoding.UTF8, "text/html")));

        Assert.Equal(new Uri(_cloud.Address, "/l/abc").AbsoluteUri, kept.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("problem").ValueKind);
        var sent = CloudCalls.Last();
        Assert.Equal("/me/links", sent.PathAndQuery);
        Assert.Equal("<p>Stock</p>", sent.Body);
        Assert.Equal("Bearer " + Token, sent.Headers["Authorization"]);
        Assert.StartsWith("text/html", sent.Headers["Content-Type"], StringComparison.Ordinal);
        var counted = Assert.Single(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(_cloud.Address.Authority, counted.GetProperty("host").GetString());
    }

    [Theory]
    [InlineData(413, "tooLarge")]
    [InlineData(409, "quotaReached")]
    [InlineData(401, "unauthorized")]
    [InlineData(500, "unreachable")]
    public async Task A_screen_the_cloud_does_not_keep_says_why(int status, string problem)
    {
        _cloud.Answers["/me/balance"] = """{"balance":10}""";
        _cloud.Posted["/me/links"] = (status, """{"error":{"code":"x"}}""");
        await SignInOnlyAsync();

        var refused = await AccountAsync(await _host.ControlClient().PostAsync("/__control/cloud/links", new StringContent("<p>x</p>", Encoding.UTF8, "text/html")));

        Assert.Equal(JsonValueKind.Null, refused.GetProperty("url").ValueKind);
        Assert.Equal(problem, refused.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Without_a_token_or_the_clouds_address_no_screen_leaves()
    {
        var signedOut = await AccountAsync(await _host.ControlClient().PostAsync("/__control/cloud/links", new StringContent("<p>x</p>", Encoding.UTF8, "text/html")));
        Assert.Equal("signedOut", signedOut.GetProperty("problem").GetString());
        Assert.Empty(CloudCalls);

        await using var host = await RunningHost.StartAsync();
        using var none = await host.ControlClient().PostAsync("/__control/cloud/links", new StringContent("<p>x</p>", Encoding.UTF8, "text/html"));
        HttpAssert.Status(HttpStatusCode.NotFound, none);
    }

    [Fact]
    public async Task The_accounts_links_are_told_on_the_clouds_host_and_not_counted_as_sent()
    {
        _cloud.Answers["/me/balance"] = """{"balance":10}""";
        _cloud.Answers["/me/links"] = """
            [{"id":"new","url":"/l/new","createdAt":"2026-10-12T09:00:00+00:00","expiresAt":"2026-11-11T09:00:00+00:00","bytes":2048},
             {"id":"old","url":"/l/old","createdAt":"2026-10-01T09:00:00+00:00","expiresAt":"2026-10-31T09:00:00+00:00","bytes":512},
             {"id":"elsewhere","url":"https://example.com/l/elsewhere"},
             {"url":"/l/no-id"}]
            """;
        await SignInOnlyAsync();

        var told = await AccountAsync(await _host.ControlClient().GetAsync("/__control/cloud/links"));

        var links = told.GetProperty("links").EnumerateArray().ToList();
        Assert.Equal(["new", "old"], links.Select(l => l.GetProperty("id").GetString()));
        Assert.Equal(new Uri(_cloud.Address, "/l/new").AbsoluteUri, links[0].GetProperty("url").GetString());
        Assert.Equal(2048, links[0].GetProperty("bytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, told.GetProperty("problem").ValueKind);
        Assert.Equal("Bearer " + Token, CloudCalls.Last().Headers["Authorization"]);
        Assert.Empty(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
    }

    [Theory]
    [InlineData(204)]
    [InlineData(404)]   // already not live — it no longer opens either way
    public async Task Taking_a_link_back_tells_the_links_left(int status)
    {
        _cloud.Answers["/me/balance"] = """{"balance":10}""";
        _cloud.Deleted["/me/links/old"] = status;
        _cloud.Answers["/me/links"] = """[{"id":"new","url":"/l/new"}]""";
        await SignInOnlyAsync();

        var left = await AccountAsync(await _host.ControlClient().DeleteAsync("/__control/cloud/links/old"));

        Assert.Equal(["new"], left.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("id").GetString()));
        var taken = Assert.Single(_cloud.Received, r => r.Method == "DELETE");
        Assert.Equal("/me/links/old", taken.PathAndQuery);
        Assert.Equal("Bearer " + Token, taken.Headers["Authorization"]);
    }

    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(500, "unreachable")]
    public async Task A_link_the_cloud_does_not_take_back_says_why(int status, string problem)
    {
        _cloud.Answers["/me/balance"] = """{"balance":10}""";
        _cloud.Deleted["/me/links/old"] = status;
        await SignInOnlyAsync();

        var refused = await AccountAsync(await _host.ControlClient().DeleteAsync("/__control/cloud/links/old"));

        Assert.Equal(JsonValueKind.Null, refused.GetProperty("links").ValueKind);
        Assert.Equal(problem, refused.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Only_a_links_id_reaches_the_cloud_and_without_a_token_or_its_address_nothing_is_asked()
    {
        using (var odd = await _host.ControlClient().DeleteAsync("/__control/cloud/links/a.b"))
            HttpAssert.Status(HttpStatusCode.BadRequest, odd);
        using (var escaped = await _host.ControlClient().DeleteAsync("/__control/cloud/links/a%2Fb"))
            HttpAssert.Status(HttpStatusCode.BadRequest, escaped);
        var signedOut = await AccountAsync(await _host.ControlClient().GetAsync("/__control/cloud/links"));
        Assert.Equal("signedOut", signedOut.GetProperty("problem").GetString());
        Assert.Empty(CloudCalls);

        await using var host = await RunningHost.StartAsync();
        using var none = await host.ControlClient().GetAsync("/__control/cloud/links");
        HttpAssert.Status(HttpStatusCode.NotFound, none);
    }

    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(503, "unreachable")]
    public async Task A_balance_the_cloud_does_not_tell_says_why(int status, string problem)
    {
        await SignInOnlyAsync();
        _cloud.Refusal = (status, """{"error":"no"}""");

        var account = await AccountAsync(await _host.ControlClient().GetAsync("/__control/cloud/account"));

        Assert.True(account.GetProperty("signedIn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, account.GetProperty("balance").ValueKind);
        Assert.Equal(problem, account.GetProperty("problem").GetString());
    }




    [Fact]
    public async Task When_the_cloud_cannot_tell_where_the_ai_goes_the_request_fails_as_unreachable_and_nothing_is_sent()
    {
        await SignInAsync();
        _cloud.Answers.TryRemove("/me", out _);

        using var response = await ProposeAsync(await _host.AdoptAsync(App));

        Assert.False(response.IsSuccessStatusCode);
        Assert.Empty(_gateway.Received);
        Assert.Empty(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
    }

    [Fact]
    public async Task A_declined_sign_in_keeps_no_session_and_says_so()
    {
        _identity.Declines = true;

        var account = await SignInOnlyAsync();

        Assert.False(account.GetProperty("signedIn").GetBoolean());
        Assert.Equal("denied", account.GetProperty("problem").GetString());
        Assert.Empty(CloudCalls);
    }

    [Fact]
    public async Task A_cloud_still_waking_up_is_waited_for_when_a_person_asks_to_sign_in()
    {
        // A cloud that scaled to zero answers its first request only once it has started — longer than a balance is waited for.
        _cloud.AnswerDelay = CloudAccount.BalanceTimeout + TimeSpan.FromSeconds(1);

        var begun = await AccountAsync(await _host.ControlClient().PostAsync("/__control/cloud/account/sign-in", null));

        Assert.Equal(JsonValueKind.Null, begun.GetProperty("problem").ValueKind);
        Assert.NotNull(begun.GetProperty("url").GetString());
    }

    [Fact]
    public async Task When_the_cloud_names_no_identity_service_signing_in_cannot_begin()
    {
        _cloud.Answers.TryRemove("/.well-known/oauth-protected-resource", out _);

        var begun = await AccountAsync(await _host.ControlClient().PostAsync("/__control/cloud/account/sign-in", null));

        Assert.Equal(JsonValueKind.Null, begun.GetProperty("url").ValueKind);
        Assert.Equal("unreachable", begun.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task The_session_kept_holds_its_refresh_token_but_not_the_access_token()
    {
        // The access token lives in memory only, refreshed after a start — kept, it would pass one Windows
        // Credential Manager entry and outlive its purpose on disk.
        await SignInOnlyAsync();

        var kept = _vault.Read(CloudAccount.VaultName);
        Assert.NotNull(kept);
        Assert.Contains(_identity.RefreshToken!, kept, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, kept, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_access_token_about_to_end_is_refreshed_once_even_when_asked_for_at_once()
    {
        _identity.ExpiresIn = 30;   // within the client's refresh-ahead window: every use refreshes first
        _cloud.Answers["/me/balance"] = """{"balance":5}""";
        await SignInOnlyAsync();
        var before = _identity.Refreshes;
        _identity.RefreshDelay = TimeSpan.FromMilliseconds(300);

        // Looks at once: the session turns over one at a time, so neither uses a refresh token the other spent.
        var asked = Enumerable.Range(0, 4).Select(_ => _host.ControlClient().GetAsync("/__control/cloud/account")).ToList();
        var looks = await Task.WhenAll(asked.Select(async a => await AccountAsync(await a)));

        Assert.All(looks, look => Assert.Equal(5, look.GetProperty("balance").GetInt64()));
        Assert.Equal(before + 2 * asked.Count, _identity.Refreshes);   // each look asks for a token twice: its balance, then what the cloud keeps
        Assert.NotNull(_identity.RefreshToken);   // never an old one used again — the session is still live
        Assert.Equal("Bearer " + Token, CloudCalls.Last().Headers["Authorization"]);
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

    [Theory]
    // A debug build reads the variable.
    [InlineData("https://cloud.example", null, false, "https://cloud.example/", null)]
    [InlineData(null, null, false, null, null)]
    [InlineData("http://cloud.example/", null, false, null, "BOHM_CLOUD is not an https address")]
    // A release build that carries the account uses its own address — the variable cannot send the session elsewhere.
    [InlineData(null, "https://cloud.bohm.example/", false, "https://cloud.bohm.example/", null)]
    [InlineData("https://elsewhere.example/", "https://cloud.bohm.example/", false, "https://cloud.bohm.example/", "BOHM_CLOUD is ignored: this build uses its own address (cloud.bohm.example)")]
    [InlineData(null, "http://cloud.bohm.example/", false, null, "This build's cloud address is not an https address")]
    // An administrator's --no-cloud wins over both, and says so whenever there was an account to turn off.
    [InlineData("https://cloud.example/", null, true, null, "BOHM_CLOUD is ignored: the Bohm account is turned off (--no-cloud)")]
    [InlineData(null, "https://cloud.bohm.example/", true, null, "The Bohm account is turned off (--no-cloud)")]
    [InlineData(null, null, true, null, null)]
    public void The_cloud_a_run_uses_is_the_builds_own_address_or_the_variable_and_no_cloud_wins(
        string? variable, string? builtIn, bool turnedOff, string? endpoint, string? note)
    {
        var (chosen, said) = CloudAccount.Choose(variable, builtIn, turnedOff);
        Assert.Equal(endpoint, chosen?.AbsoluteUri);
        if (note is null) Assert.Null(said);
        else Assert.StartsWith(note, said);
    }

    [Fact]
    public void A_debug_build_carries_no_address_of_its_own() => Assert.Null(CloudAccount.BuiltInAddress);

    /// <summary>The requests to the gateway's Anthropic path.</summary>
    private List<FakeProvider.ReceivedRequest> Messages => [.. _gateway.Received.Where(r => r.PathAndQuery.StartsWith("/anthropic/", StringComparison.Ordinal))];

    /// <summary>A gateway's refusal: the provider's own error shape, with the gateway's object beside it.</summary>
    private static string Refused(string code, int? affordableMaxTokens) =>
        JsonSerializer.Serialize(new
        {
            type = "error",
            error = new { type = "invalid_request_error", message = "The account's credits cannot hold this answer." },
            tokenway = new { code, required = 90000, available = 40000, held = 0, max_affordable_max_tokens = affordableMaxTokens, retryable = false },
        });

    private static async Task<JsonElement> AccountAsync(HttpResponseMessage response)
    {
        using (response)
        {
            HttpAssert.Status(HttpStatusCode.OK, response);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }
    }

    /// <summary>
    /// Signs in as the person would: the address the runtime gives is opened (here by a client that follows the identity
    /// service's redirect back to the runtime's loopback port, as the browser does), then the runtime is asked to finish.
    /// </summary>
    private async Task<JsonElement> SignInOnlyAsync()
    {
        var begun = await AccountAsync(await _host.ControlClient().PostAsync("/__control/cloud/account/sign-in", null));
        var url = begun.GetProperty("url").GetString()!;
        Assert.StartsWith(_identity.Issuer + "/authorize?", url, StringComparison.Ordinal);
        var finishing = _host.ControlClient().PostAsync("/__control/cloud/account/sign-in/finish", null);
        using (var browser = new HttpClient())
        using (await browser.GetAsync(new Uri(url)))
        {
        }

        return await AccountAsync(await finishing);
    }

    private async Task SignInAsync()
    {
        Assert.True((await SignInOnlyAsync()).GetProperty("signedIn").GetBoolean());
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
