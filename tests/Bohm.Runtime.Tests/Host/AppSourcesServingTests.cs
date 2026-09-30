using System.Net;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Sources through the host: the shell declares a source and hands over what it read through the
/// control API; the application reads the rows at <c>/__bohm/sources/&lt;name&gt;</c> on its own origin.
/// </summary>
public sealed class AppSourcesServingTests : IAsyncLifetime
{
    private const string Rule = """{"rule":{"site":"https://shop.example/items","selector":"#prices","columns":["Item","Price"]},"granted":true}""";
    private const string Reading = """{"source":"https://shop.example/items?page=1","columns":["Item","Price"],"rows":[["Pen","1.20"],["Ink","3.00"]]}""";

    private RunningHost _host = null!;
    private string _out = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await RunningHost.StartAsync();
        _out = Directory.CreateTempSubdirectory("bohm-sources-export-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_out, recursive: true);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<string> AppWithSourceAsync(string declare = Rule)
    {
        var id = await _host.AdoptAsync("<!doctype html><title>Prices</title>");
        using var declared = await _host.ControlClient().PutAsync($"/__control/apps/{id}/sources/prices", Json(declare));
        HttpAssert.Status(HttpStatusCode.OK, declared);
        return id;
    }

    private async Task<HttpResponseMessage> RecordAsync(string id, string reading = Reading, string name = "prices") =>
        await _host.ControlClient().PostAsync($"/__control/apps/{id}/sources/{name}/readings", Json(reading));

    private async Task<HttpResponseMessage> AppGetAsync(string id, string path, string? cookie, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await _host.ClientForApp(id).SendAsync(request);
    }

    [Fact]
    public async Task A_declared_source_is_listed_for_the_shell_with_its_rule_and_permission()
    {
        var id = await AppWithSourceAsync();

        var list = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/sources")).RootElement;

        var source = Assert.Single(list.EnumerateArray());
        Assert.Equal("prices", source.GetProperty("name").GetString());
        Assert.Equal("#prices", source.GetProperty("rule").GetProperty("selector").GetString());
        Assert.Equal(["Item", "Price"], source.GetProperty("rule").GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("https://shop.example/items", source.GetProperty("grant").GetProperty("site").GetString());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("lastReadAt").ValueKind);
    }

    [Fact]
    public async Task The_application_reads_the_latest_rows_with_its_session_and_a_plain_fetch()
    {
        var id = await AppWithSourceAsync();
        using (var recorded = await RecordAsync(id)) HttpAssert.Status(HttpStatusCode.OK, recorded);
        var page = await _host.LoadAsync(id);

        using var response = await AppGetAsync(id, "/__bohm/sources/prices", page.Cookie);   // no X-Bohm-Request: a generated fetch cannot add it

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("default-src 'self'", string.Join(' ', response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        var latest = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("https://shop.example/items?page=1", latest.GetProperty("source").GetString());
        Assert.True(latest.GetProperty("readAt").TryGetDateTimeOffset(out _));
        var rows = latest.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Pen", rows[0].GetProperty("Item").GetString());
        Assert.Equal("3.00", rows[1].GetProperty("Price").GetString());
    }

    [Fact]
    public async Task Every_reading_is_there_for_the_application_oldest_first()
    {
        var id = await AppWithSourceAsync();
        (await RecordAsync(id)).Dispose();
        (await RecordAsync(id, """{"source":"https://shop.example/items","columns":["Item","Price"],"rows":[["Pen","1.25"]]}""")).Dispose();
        var page = await _host.LoadAsync(id);

        using var response = await AppGetAsync(id, "/__bohm/sources/prices/readings", page.Cookie);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var readings = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
        Assert.Equal(["1.20", "1.25"], readings.Select(r => r.GetProperty("rows")[0].GetProperty("Price").GetString()));
    }

    [Fact]
    public async Task A_source_not_read_yet_answers_with_no_rows()
    {
        var id = await AppWithSourceAsync();
        var page = await _host.LoadAsync(id);

        using var response = await AppGetAsync(id, "/__bohm/sources/prices", page.Cookie);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var latest = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("readAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("source").ValueKind);
        Assert.Empty(latest.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task Only_the_applications_own_page_reads_its_sources()
    {
        var id = await AppWithSourceAsync();
        (await RecordAsync(id)).Dispose();
        var page = await _host.LoadAsync(id);
        var other = await _host.AdoptAsync("<p>other</p>");
        var otherPage = await _host.LoadAsync(other);

        using (var noSession = await AppGetAsync(id, "/__bohm/sources/prices", cookie: null)) HttpAssert.Status(HttpStatusCode.Forbidden, noSession);
        using (var foreign = await AppGetAsync(id, "/__bohm/sources/prices", page.Cookie, origin: "http://evil.example")) HttpAssert.Status(HttpStatusCode.Forbidden, foreign);
        using (var anotherApp = await AppGetAsync(id, "/__bohm/sources/prices", otherPage.Cookie)) HttpAssert.Status(HttpStatusCode.Forbidden, anotherApp);
        using (var unknown = await AppGetAsync(id, "/__bohm/sources/stock", page.Cookie)) HttpAssert.Status(HttpStatusCode.NotFound, unknown);
        using (var elsewhere = await AppGetAsync(other, "/__bohm/sources/prices", otherPage.Cookie)) HttpAssert.Status(HttpStatusCode.NotFound, elsewhere);

        using var post = new HttpRequestMessage(HttpMethod.Post, "/__bohm/sources/prices") { Content = Json(Reading) };
        post.Headers.Add("Cookie", page.Cookie);
        post.Headers.Add("X-Bohm-Request", "1");
        using (var write = await _host.ClientForApp(id).SendAsync(post)) HttpAssert.Status(HttpStatusCode.MethodNotAllowed, write);   // the page reads; only the shell writes
    }

    [Fact]
    public async Task A_reading_the_permission_does_not_cover_is_refused_with_a_reason()
    {
        var id = await AppWithSourceAsync();

        using var outside = await RecordAsync(id, """{"source":"https://shop.example.evil/items","columns":["Item","Price"],"rows":[]}""");
        HttpAssert.Status(HttpStatusCode.Forbidden, outside);
        Assert.Equal("outside-grant", JsonDocument.Parse(await outside.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        using (var revoked = await _host.ControlClient().DeleteAsync($"/__control/apps/{id}/sources/prices/grant")) HttpAssert.Status(HttpStatusCode.NoContent, revoked);
        using var notGranted = await RecordAsync(id);
        HttpAssert.Status(HttpStatusCode.Forbidden, notGranted);
        Assert.Equal("not-granted", JsonDocument.Parse(await notGranted.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_reading_of_another_shape_is_refused_and_told_the_rules_columns()
    {
        var id = await AppWithSourceAsync();

        using var response = await RecordAsync(id, """{"source":"https://shop.example/items","columns":["Item","Cost"],"rows":[["Pen","1.20"]]}""");

        HttpAssert.Status(HttpStatusCode.Conflict, response);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("shape-mismatch", body.GetProperty("code").GetString());
        Assert.Equal(["Item", "Price"], body.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task Unknown_applications_sources_and_malformed_requests_are_told_apart()
    {
        var id = await AppWithSourceAsync();
        var client = _host.ControlClient();

        using (var noApp = await client.PutAsync($"/__control/apps/{new string('0', 32)}/sources/prices", Json(Rule))) HttpAssert.Status(HttpStatusCode.NotFound, noApp);
        using (var noSource = await RecordAsync(id, name: "stock")) HttpAssert.Status(HttpStatusCode.NotFound, noSource);
        using (var badName = await client.PutAsync($"/__control/apps/{id}/sources/Prices", Json(Rule))) HttpAssert.Status(HttpStatusCode.BadRequest, badName);
        using (var badRule = await client.PutAsync($"/__control/apps/{id}/sources/stock", Json("""{"rule":{"site":"shop.example","selector":"#t","columns":["A"]},"granted":true}"""))) HttpAssert.Status(HttpStatusCode.BadRequest, badRule);
        using (var notJson = await RecordAsync(id, "rows")) HttpAssert.Status(HttpStatusCode.BadRequest, notJson);
        using (var noRevoke = await client.DeleteAsync($"/__control/apps/{id}/sources/stock/grant")) HttpAssert.Status(HttpStatusCode.NotFound, noRevoke);
        using (var noSecret = await _host.ControlClient(secret: null).GetAsync($"/__control/apps/{id}/sources")) HttpAssert.Status(HttpStatusCode.Unauthorized, noSecret);
    }

    [Fact]
    public async Task A_preview_reads_the_applications_sources_as_they_are()
    {
        var id = await AppWithSourceAsync();
        (await RecordAsync(id)).Dispose();
        using var created = await _host.ControlClient().PostAsync($"/__control/apps/{id}/previews", new StringContent("<p>proposed</p>", Encoding.UTF8, "text/html"));
        HttpAssert.Status(HttpStatusCode.Created, created);
        var token = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();

        using var response = await _host.ClientFor($"pv-{token}.localhost").GetAsync("/__bohm/sources/prices");

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal("Pen", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("rows")[0].GetProperty("Item").GetString());
    }

    [Fact]
    public async Task Exporting_without_the_data_leaves_out_what_was_read_and_the_permission()
    {
        var id = await AppWithSourceAsync();
        (await RecordAsync(id)).Dispose();
        var page = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"notes","value":"mine"}]}""")).Dispose();
        var folder = Path.Combine(_out, "Prices (Bohm)");

        using (var exported = await _host.ControlClient().PostAsync($"/__control/apps/{id}/export?data=leave", new StringContent(folder, Encoding.UTF8)))
            HttpAssert.Status(HttpStatusCode.OK, exported);

        Assert.True(File.Exists(Path.Combine(folder, "app.html")));
        Assert.True(File.Exists(Path.Combine(folder, "app.json")));
        Assert.False(Directory.Exists(Path.Combine(folder, "storage")));
        Assert.False(Directory.Exists(Path.Combine(folder, "sources")));
        Assert.False(File.Exists(Path.Combine(folder, "usage.ndjson")));
        var rules = await File.ReadAllTextAsync(Path.Combine(folder, "sources.json"), TestContext.Current.CancellationToken);
        Assert.Contains("#prices", rules, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(rules).RootElement.GetProperty("sources")[0].GetProperty("grant").ValueKind);

        // Taken in elsewhere, it opens with no data and asks for its own permission before reading.
        await using var other = await RunningHost.StartAsync();
        using (var imported = await other.ControlClient().PostAsync("/__control/apps/import", new StringContent(folder, Encoding.UTF8))) HttpAssert.Status(HttpStatusCode.Created, imported);
        Assert.Equal("{}", (await other.LoadAsync(id)).Items);
        var source = Assert.Single(JsonDocument.Parse(await other.ControlClient().GetStringAsync($"/__control/apps/{id}/sources")).RootElement.EnumerateArray());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("grant").ValueKind);
        using var refused = await other.ControlClient().PostAsync($"/__control/apps/{id}/sources/prices/readings", Json(Reading));
        HttpAssert.Status(HttpStatusCode.Forbidden, refused);
    }

    [Fact]
    public async Task Exporting_with_the_data_keeps_what_was_read()
    {
        var id = await AppWithSourceAsync();
        (await RecordAsync(id)).Dispose();
        var folder = Path.Combine(_out, "Prices (Bohm)");

        using (var exported = await _host.ControlClient().PostAsync($"/__control/apps/{id}/export", new StringContent(folder, Encoding.UTF8)))
            HttpAssert.Status(HttpStatusCode.OK, exported);

        Assert.True(File.Exists(Path.Combine(folder, "sources", "prices.ndjson")));
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "sources.json"), TestContext.Current.CancellationToken))
            .RootElement.GetProperty("sources")[0].GetProperty("grant").ValueKind);
    }
}
