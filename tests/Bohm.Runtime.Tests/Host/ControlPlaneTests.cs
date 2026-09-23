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
