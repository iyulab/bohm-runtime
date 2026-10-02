using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Making an application of an answer, through the control API: a proposal from a model able to write one,
/// a look at it with the rows just read — nothing kept — and taking it in with its sources, allowed as it
/// is taken in, in one step.
/// </summary>
public sealed class AppPromotionTests : IAsyncLifetime
{
    private const string Html = """
        <!doctype html><title>Prices</title><ul id="list"></ul>
        <script>fetch('/__bohm/sources/prices').then(r => r.json()).then(d => d.rows.forEach(row => {
          const li = document.createElement('li'); li.textContent = row.Item + ' ' + row.Price; document.getElementById('list').append(li); }));</script>
        """;

    private const string Rows = """{"source":"https://shop.example/items?page=1","columns":["Item","Price"],"rows":[["Pen","1.20"],["Ink","3.00"]]}""";

    private static readonly string Request = """
        {"question":"Compare the prices","answer":"Pen 1.20","lang":"en","pages":[{"url":"https://shop.example/items","title":"Items",
         "tables":[{"selector":"#prices","headers":["Item","Price"],"rows":2,"preview":[["Pen","1.20"]]}]}]}
        """;

    private static readonly string Promotion =
        "{\"html\":" + JsonSerializer.Serialize(Html) + ",\"title\":\"Price list\","
        + """ "sources":[{"name":"prices","rule":{"site":"https://shop.example/items","selector":"#prices","columns":["Item","Price"]}}], """
        + "\"readings\":{\"prices\":" + Rows + "}}";

    private FakeProvider _server = null!;
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await FakeProvider.StartAsync();
        _host = await RunningHost.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _server.DisposeAsync();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private string[] AdoptedFolders() =>
        Directory.Exists(Path.Combine(_host.DataRoot, "adopted")) ? Directory.GetDirectories(Path.Combine(_host.DataRoot, "adopted")) : [];

    [Fact]
    public async Task Without_a_model_the_proposal_says_what_to_connect()
    {
        using var response = await _host.ControlClient().PostAsync("/__control/apps/proposals", Json(Request));

        HttpAssert.Status(HttpStatusCode.Conflict, response);
        Assert.Equal("localModel", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("needs").GetString());
    }

    [Fact]
    public async Task The_model_on_this_computer_is_not_asked_to_write_an_application()
    {
        var local = new FakeChatModel();
        await using var host = await RunningHost.StartAsync(configure: o => o with { LocalModel = new LocalModelOptions { ModelPath = "unused.gguf", Client = local } });

        using var response = await host.ControlClient().PostAsync("/__control/apps/proposals", Json(Request));

        HttpAssert.Status(HttpStatusCode.Conflict, response);
        Assert.Equal("largerModel", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("needs").GetString());
        Assert.Empty(local.Calls);
    }

    [Fact]
    public async Task The_organizations_server_is_asked_and_an_answer_with_no_application_fails_with_what_it_said()
    {
        using (var set = await _host.ControlClient().PutAsync("/__control/llm/company-model", Json($$"""{"endpoint":"{{new Uri(_server.Address, "v1/")}}","model":"m"}""")))
            HttpAssert.Status(HttpStatusCode.OK, set);

        using var response = await _host.ControlClient().PostAsync("/__control/apps/proposals", Json(Request));

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        Assert.Contains(FakeProvider.Reply, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("propose_app", Assert.Single(_server.Asked).Body, StringComparison.Ordinal);
        Assert.Empty(AdoptedFolders());
    }

    [Fact]
    public async Task An_answer_that_reached_its_length_limit_says_so()
    {
        _server.FinishReason = "length";
        using (var set = await _host.ControlClient().PutAsync("/__control/llm/company-model", Json($$"""{"endpoint":"{{new Uri(_server.Address, "v1/")}}","model":"m"}""")))
            HttpAssert.Status(HttpStatusCode.OK, set);

        using var response = await _host.ControlClient().PostAsync("/__control/apps/proposals", Json(Request));

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var failure = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("output-limit", failure.GetProperty("stopped").GetString());
        Assert.Contains("length limit", failure.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(AdoptedFolders());
    }

    [Theory]
    [InlineData("""{"question":"q","pages":[]}""")]
    [InlineData("""{"pages":[{"url":"https://a.example/","tables":[]}]}""")]
    [InlineData("not json")]
    public async Task A_request_without_a_question_or_a_page_is_refused(string body)
    {
        using var response = await _host.ControlClient().PostAsync("/__control/apps/proposals", Json(body));

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
    }

    [Fact]
    public async Task A_proposed_application_is_previewed_with_the_rows_just_read_and_nothing_is_kept()
    {
        using var created = await _host.ControlClient().PostAsync("/__control/previews", Json("{\"html\":" + JsonSerializer.Serialize(Html) + ",\"readings\":{\"prices\":" + Rows + "}}"));
        HttpAssert.Status(HttpStatusCode.Created, created);
        var view = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var token = view.GetProperty("token").GetString()!;
        Assert.Equal($"http://pv-{token}.localhost:{_host.Port}/", view.GetProperty("origin").GetString());

        using var preview = _host.ClientFor($"pv-{token}.localhost");
        var page = await preview.GetStringAsync("/");
        Assert.Contains("<ul id=\"list\">", page, StringComparison.Ordinal);
        Assert.Contains("\"items\":{}", page, StringComparison.Ordinal);
        var latest = JsonDocument.Parse(await preview.GetStringAsync("/__bohm/sources/prices")).RootElement;
        Assert.Equal("Ink", latest.GetProperty("rows")[1].GetProperty("Item").GetString());
        Assert.Equal("https://shop.example/items?page=1", latest.GetProperty("source").GetString());
        using (var unknown = await preview.GetAsync("/__bohm/sources/stock")) HttpAssert.Status(HttpStatusCode.NotFound, unknown);

        var report = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/previews/{token}")).RootElement;
        Assert.Empty(report.GetProperty("errors").EnumerateArray());
        using (var removed = await _host.ControlClient().DeleteAsync($"/__control/previews/{token}")) HttpAssert.Status(HttpStatusCode.NoContent, removed);
        using (var gone = await preview.GetAsync("/")) HttpAssert.Status(HttpStatusCode.NotFound, gone);
        Assert.Empty(AdoptedFolders());
    }

    [Fact]
    public async Task Taking_it_in_keeps_the_application_its_allowed_sources_and_the_rows_in_one_step()
    {
        using var response = await _host.ControlClient().PostAsync("/__control/apps/promotions", Json(Promotion));

        HttpAssert.Status(HttpStatusCode.Created, response);
        var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        var source = Assert.Single(JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/sources")).RootElement.EnumerateArray());
        Assert.Equal("https://shop.example/items", source.GetProperty("grant").GetProperty("site").GetString());
        var page = await _host.LoadAsync(id);
        using var read = new HttpRequestMessage(HttpMethod.Get, "/__bohm/sources/prices");
        read.Headers.Add("Cookie", page.Cookie);
        using var latest = await _host.ClientForApp(id).SendAsync(read);
        Assert.Equal("Pen", JsonDocument.Parse(await latest.Content.ReadAsStringAsync()).RootElement.GetProperty("rows")[0].GetProperty("Item").GetString());
        Assert.Contains("\"title\":\"Price list\"", await _host.ControlClient().GetStringAsync("/__control/apps"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"source":"https://other.example/items","columns":["Item","Price"],"rows":[]}""")]   // outside the site
    [InlineData("""{"source":"https://shop.example/items","columns":["Item"],"rows":[["Pen"]]}""")]         // another shape
    public async Task Rows_the_sources_would_refuse_leave_nothing_behind(string rows)
    {
        using var response = await _host.ControlClient().PostAsync("/__control/apps/promotions", Json(Promotion.Replace(Rows, rows, StringComparison.Ordinal)));

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Empty(AdoptedFolders());
    }

    [Theory]
    [InlineData("""{"html":"<p>x</p>","sources":[],"readings":{}}""")]
    [InlineData("""{"sources":[{"name":"prices","rule":{"site":"https://shop.example/items","selector":"#prices","columns":["Item"]}}],"readings":{}}""")]
    [InlineData("""{"html":"<p>x</p>","sources":[{"name":"Bad Name","rule":{"site":"https://shop.example/items","selector":"#prices","columns":["Item"]}}],"readings":{}}""")]
    [InlineData("""{"html":"<p>x</p>","sources":[{"name":"prices","rule":{"site":"https://shop.example/items","selector":"#prices","columns":["Item"]}}],"readings":{"stock":{"source":"https://shop.example/items","columns":["Item"],"rows":[]}}}""")]
    public async Task A_promotion_without_code_or_sources_or_with_rows_for_no_source_is_refused(string body)
    {
        using var response = await _host.ControlClient().PostAsync("/__control/apps/promotions", Json(body));

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Empty(AdoptedFolders());
    }
}
