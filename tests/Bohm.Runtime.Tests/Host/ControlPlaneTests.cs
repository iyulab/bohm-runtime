using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

public sealed class ControlPlaneTests : IAsyncLifetime
{
    private const string Page = "<!doctype html><title>Stock</title>";

    private RunningHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await RunningHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Requests_without_the_secret_are_refused()
    {
        using var none = await _host.ControlClient(secret: null).GetAsync("/__control/apps");
        using var wrong = await _host.ControlClient("guess").GetAsync("/__control/apps");

        HttpAssert.Status(HttpStatusCode.Unauthorized, none);
        HttpAssert.Status(HttpStatusCode.Unauthorized, wrong);
    }

    [Fact]
    public async Task Without_a_configured_secret_there_is_no_control_api()
    {
        await using var host = await RunningHost.StartAsync(secret: null);

        using var response = await host.ControlClient("anything").GetAsync("/__control/apps");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task The_control_api_is_not_reachable_from_an_application_origin()
    {
        var id = await _host.AdoptAsync(Page);
        var client = _host.ClientForApp(id);
        client.DefaultRequestHeaders.Authorization = new("Bearer", RunningHost.Secret);

        using var response = await client.GetAsync("/__control/apps");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task Adopting_returns_the_application_and_its_origin()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Page));
        content.Headers.Add("X-Bohm-Original-Path", Uri.EscapeDataString("다운로드/재고 관리.html"));

        using var response = await _host.ControlClient().PostAsync("/__control/apps", content);
        var app = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        HttpAssert.Status(HttpStatusCode.Created, response);
        var id = app.GetProperty("id").GetString()!;
        Assert.Equal($"http://{id}.localhost:{_host.Port}/", app.GetProperty("origin").GetString());
        Assert.Equal("다운로드/재고 관리.html", app.GetProperty("originalPath").GetString());
        Assert.Equal(Encoding.UTF8.GetBytes(Page), await _host.Catalog.ReadHtmlAsync(id));
    }

    [Fact]
    public async Task Stopping_right_after_an_adoption_leaves_nothing_open_in_the_data_root()
    {
        // Adoption starts a background fetch of the application's code. Stopping the host at once
        // must cancel and wait for it, so the data root can be removed (no file left open).
        for (var i = 0; i < 10; i++)
        {
            var host = await RunningHost.StartAsync();
            using (var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Page)))
            using (var adopted = await host.ControlClient().PostAsync("/__control/apps", content))
                HttpAssert.Status(HttpStatusCode.Created, adopted);
            await host.DisposeAsync(); // Deletes the data root; throws if a file is still open.
            Assert.False(Directory.Exists(host.DataRoot));
        }
    }

    [Fact]
    public async Task An_empty_body_is_not_adopted()
    {
        using var response = await _host.ControlClient().PostAsync("/__control/apps", new ByteArrayContent([]));

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Empty(await _host.Catalog.ListAsync());
    }

    [Fact]
    public async Task Earlier_adoptions_of_the_same_file_are_reported_before_adopting_again()
    {
        var first = await _host.AdoptAsync(Page);

        using var same = await _host.ControlClient().PostAsync("/__control/apps/matches", new ByteArrayContent(Encoding.UTF8.GetBytes(Page)));
        using var other = await _host.ControlClient().PostAsync("/__control/apps/matches", new ByteArrayContent(Encoding.UTF8.GetBytes("<p>other</p>")));

        var match = JsonDocument.Parse(await same.Content.ReadAsStringAsync()).RootElement[0];
        Assert.Equal(first, match.GetProperty("app").GetProperty("id").GetString());
        Assert.Equal("sameBytes", match.GetProperty("match").GetString());
        Assert.Equal(0, JsonDocument.Parse(await other.Content.ReadAsStringAsync()).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task A_revised_file_from_the_same_path_is_reported_as_another_version()
    {
        const string path = "다운로드/도서대출.html";
        using var v1 = new ByteArrayContent(Encoding.UTF8.GetBytes(Page));
        v1.Headers.Add("X-Bohm-Original-Path", Uri.EscapeDataString(path));
        using var adopted = await _host.ControlClient().PostAsync("/__control/apps", v1);
        var first = JsonDocument.Parse(await adopted.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();

        using var v2 = new ByteArrayContent(Encoding.UTF8.GetBytes(Page + "<button>반납</button>"));
        v2.Headers.Add("X-Bohm-Original-Path", Uri.EscapeDataString(path));
        using var response = await _host.ControlClient().PostAsync("/__control/apps/matches", v2);

        var match = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement[0];
        Assert.Equal(first, match.GetProperty("app").GetProperty("id").GetString());
        Assert.Equal("sameOriginalPath", match.GetProperty("match").GetString());
    }

    [Fact]
    public async Task A_new_revision_runs_new_code_on_the_same_data_and_the_old_page_can_no_longer_write()
    {
        var id = await _host.AdoptAsync(Page);
        var oldPage = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, oldPage, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"loan","value":"3"}]}""")).Dispose();

        using var revised = await ReviseAsync(id, Page + "<button>반납</button>");
        var app = JsonDocument.Parse(await revised.Content.ReadAsStringAsync()).RootElement;
        using var stale = await _host.PostStorageAsync(id, oldPage, """{"tab":"TAB","ops":[{"seq":2,"op":"set","key":"loan","value":"old code"}]}""");
        var newPage = await _host.LoadAsync(id);

        HttpAssert.Status(HttpStatusCode.Created, revised);
        Assert.Equal(id, app.GetProperty("id").GetString());
        Assert.Equal(2, app.GetProperty("revision").GetInt32());
        Assert.True(app.GetProperty("canRevert").GetBoolean());
        HttpAssert.Status(HttpStatusCode.Forbidden, stale);
        Assert.Equal("""{"loan":"3"}""", newPage.Items);
        Assert.Equal(Encoding.UTF8.GetBytes(Page + "<button>반납</button>"), await _host.Catalog.ReadHtmlAsync(id));
        Assert.Contains("\"event\":\"revised\"", await File.ReadAllTextAsync(Path.Combine(_host.DataRoot, "adopted", id, "usage.ndjson")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reverting_puts_back_the_previous_code_and_data()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"loan","value":"3"}]}""")).Dispose();
        (await ReviseAsync(id, "<p>broken</p>")).Dispose();
        var broken = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, broken, """{"tab":"TAB","ops":[{"seq":1,"op":"remove","key":"loan"}]}""")).Dispose();

        using var reverted = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions/revert", null);
        var app = JsonDocument.Parse(await reverted.Content.ReadAsStringAsync()).RootElement;
        using var again = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions/revert", null);

        HttpAssert.Status(HttpStatusCode.OK, reverted);
        Assert.Equal(1, app.GetProperty("revision").GetInt32());
        Assert.False(app.GetProperty("canRevert").GetBoolean());
        Assert.Equal("""{"loan":"3"}""", (await _host.LoadAsync(id)).Items);
        Assert.Equal(Encoding.UTF8.GetBytes(Page), await _host.Catalog.ReadHtmlAsync(id));
        HttpAssert.Status(HttpStatusCode.Conflict, again);
    }

    [Fact]
    public async Task The_same_bytes_or_an_unknown_application_are_not_revised()
    {
        var id = await _host.AdoptAsync(Page);

        using var same = await ReviseAsync(id, Page);
        using var unknown = await ReviseAsync("0123456789abcdef0123456789abcdef", Page);
        using var empty = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions", new ByteArrayContent([]));

        HttpAssert.Status(HttpStatusCode.Conflict, same);
        HttpAssert.Status(HttpStatusCode.NotFound, unknown);
        HttpAssert.Status(HttpStatusCode.BadRequest, empty);
        Assert.Equal(1, (await _host.Catalog.GetAsync(id))!.Revision);
    }

    private Task<HttpResponseMessage> ReviseAsync(string id, string html)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        content.Headers.Add("X-Bohm-Original-Path", Uri.EscapeDataString("다운로드/도서대출.html"));
        return _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions", content);
    }

    [Fact]
    public async Task Listing_shows_every_adopted_application()
    {
        var a = await _host.AdoptAsync(Page);
        var b = await _host.AdoptAsync("<p>b</p>");

        var list = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/apps")).RootElement;

        Assert.Equal([a, b], list.EnumerateArray().Select(e => e.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Status_reports_todays_signals_without_any_content()
    {
        var id = await _host.AdoptAsync(Page);
        using var load = await _host.ClientForApp(id).GetAsync("/");

        var status = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/status")).RootElement;

        Assert.True(status.GetProperty("opened").GetBoolean());
        Assert.False(status.GetProperty("input").GetBoolean());
        Assert.False(status.GetProperty("wrote").GetBoolean());
        Assert.Equal(0, status.GetProperty("loadErrors").GetInt32());
    }

    [Fact]
    public async Task Usage_reports_each_day_the_first_day_of_use_and_retention()
    {
        var id = await _host.AdoptAsync(Page);
        var today = DateOnly.FromDateTime(DateTime.Now);
        string Day(int back) => today.AddDays(-back).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        // Day 0 was 30 days ago; used again on day 29 — inside the fifth week.
        File.WriteAllLines(Path.Combine(_host.DataRoot, "adopted", id, "usage.ndjson"),
        [
            $$"""{"date":"{{Day(30)}}","signal":"opened"}""",
            $$"""{"date":"{{Day(30)}}","signal":"input"}""",
            $$"""{"date":"{{Day(30)}}","signal":"wrote"}""",
            $$"""{"date":"{{Day(1)}}","signal":"opened"}""",
            $$"""{"date":"{{Day(1)}}","signal":"input"}""",
            $$"""{"date":"{{Day(1)}}","event":"load-error"}""",
            $$"""{"date":"{{Day(1)}}","event":"revised"}""",
        ]);

        var usage = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/usage")).RootElement;

        Assert.Equal(Day(0), usage.GetProperty("today").GetString());
        Assert.Equal(Day(30), usage.GetProperty("firstUsed").GetString());
        Assert.Equal(Day(1), usage.GetProperty("lastUsed").GetString());
        Assert.Equal(30, usage.GetProperty("day").GetInt32());
        Assert.Equal("retained", usage.GetProperty("retention").GetString());
        var days = usage.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(2, days.Count);
        Assert.True(days[0].GetProperty("wrote").GetBoolean());
        Assert.Equal(1, days[1].GetProperty("loadErrors").GetInt32());
        Assert.Equal("revised", usage.GetProperty("revisions")[0].GetProperty("event").GetString());
        // Only facts — no keys, values or text of the application's data.
        Assert.DoesNotContain("Stock", usage.GetRawText());
    }

    [Fact]
    public async Task Usage_of_an_application_never_used_has_not_started()
    {
        var id = await _host.AdoptAsync(Page);

        var usage = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/usage")).RootElement;

        Assert.Equal("not-started", usage.GetProperty("retention").GetString());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("firstUsed").ValueKind);
        Assert.Empty(usage.GetProperty("days").EnumerateArray());
    }

    [Fact]
    public async Task A_page_reports_how_its_reads_matched_the_data_and_usage_shows_it_per_revision()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);

        async Task<HttpResponseMessage> Report(string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/usage")
            {
                Content = new StringContent(json.Replace("TAB", page.Tab, StringComparison.Ordinal), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Cookie", page.Cookie);
            request.Headers.Add("X-Bohm-Request", "1");
            return await _host.ClientForApp(id).SendAsync(request);
        }

        using (var ok = await Report("""{"tab":"TAB","kind":"keys","missing":1,"unread":2,"seeded":2}"""))
            HttpAssert.Status(HttpStatusCode.OK, ok);
        // More unread keys than it was given is not a fact a page can observe.
        using (var bad = await Report("""{"tab":"TAB","kind":"keys","missing":0,"unread":3,"seeded":2}"""))
            HttpAssert.Status(HttpStatusCode.BadRequest, bad);
        using (var missing = await Report("""{"tab":"TAB","kind":"keys","missing":1}"""))
            HttpAssert.Status(HttpStatusCode.BadRequest, missing);

        var usage = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/usage")).RootElement;
        var keys = Assert.Single(usage.GetProperty("keys").EnumerateArray());
        Assert.Equal(1, keys.GetProperty("revision").GetInt32());
        Assert.Equal(1, keys.GetProperty("missing").GetInt32());
        Assert.Equal(2, keys.GetProperty("unread").GetInt32());
        Assert.Equal(2, keys.GetProperty("seeded").GetInt32());
    }

    [Fact]
    public async Task Usage_of_an_unknown_application_is_not_found()
    {
        using var response = await _host.ControlClient().GetAsync("/__control/apps/0123456789abcdef0123456789abcdef/usage");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task Status_of_an_unknown_application_is_not_found()
    {
        using var response = await _host.ControlClient().GetAsync("/__control/apps/0123456789abcdef0123456789abcdef/status");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task Drain_reports_quiet_when_nothing_is_being_written()
    {
        using var response = await _host.ControlClient().PostAsync("/__control/drain", null);

        Assert.True(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("quiet").GetBoolean());
    }

    [Fact]
    public async Task The_usage_record_holds_dates_and_signals_only()
    {
        var id = await _host.AdoptAsync(Page);
        using var load = await _host.ClientForApp(id).GetAsync("/");

        var lines = await File.ReadAllLinesAsync(Path.Combine(_host.DataRoot, "adopted", id, "usage.ndjson"));

        var line = Assert.Single(lines);
        Assert.Matches("""^\{"date":"\d{4}-\d{2}-\d{2}","signal":"opened"\}$""", line);
    }

    [Fact]
    public async Task Listing_carries_the_last_day_each_application_was_used()
    {
        var used = await _host.AdoptAsync(Page);
        var neverUsed = await _host.AdoptAsync("<p>b</p>");
        await File.WriteAllLinesAsync(Path.Combine(_host.DataRoot, "adopted", used, "usage.ndjson"),
            ["""{"date":"2026-09-10","signal":"opened"}""", """{"date":"2026-09-10","signal":"input"}"""]);

        var list = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/apps")).RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("lastUsed"));

        Assert.Equal("2026-09-10", list[used].GetString());
        Assert.Equal(JsonValueKind.Null, list[neverUsed].ValueKind);
    }

    [Fact]
    public async Task Loading_twice_on_one_day_records_the_signal_once()
    {
        var id = await _host.AdoptAsync(Page);
        (await _host.ClientForApp(id).GetAsync("/")).Dispose();
        (await _host.ClientForApp(id).GetAsync("/")).Dispose();

        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_host.DataRoot, "adopted", id, "usage.ndjson")));
    }

    [Fact]
    public async Task Control_responses_are_json()
    {
        using var response = await _host.ControlClient().GetAsync("/__control/apps");

        Assert.Equal(new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" }, response.Content.Headers.ContentType);
    }
}
