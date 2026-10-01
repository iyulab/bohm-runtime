using System.Net;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

/// <summary>A proposed document served for a look before it is taken in — and kept away from the application.</summary>
public sealed class AppPreviewTests : IAsyncLifetime
{
    private const string Page = "<!doctype html><title>Stock</title>";

    private RunningHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await RunningHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_preview_is_served_on_its_own_origin_with_the_data_to_read_and_nowhere_to_write_it()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"notes","value":"kept"}]}""")).Dispose();

        var (token, origin) = await CreateAsync(id, "<!doctype html><title>Proposed</title><p>new</p>");
        Assert.Equal($"http://pv-{token}.localhost:{_host.Port}/", origin);

        using var preview = _host.ClientFor($"pv-{token}.localhost");
        using var document = await preview.GetAsync("/");
        HttpAssert.Status(HttpStatusCode.OK, document);
        var body = await document.Content.ReadAsStringAsync();
        Assert.Contains("<p>new</p>", body, StringComparison.Ordinal);
        Assert.Contains("\"notes\":\"kept\"", body, StringComparison.Ordinal);   // the application's data, to read
        Assert.False(document.Headers.Contains("Set-Cookie"));                 // no session of the application
        Assert.NotNull(document.Content.Headers.ContentType);
        Assert.Contains("default-src 'self'", string.Join(' ', document.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);

        // A write is acknowledged, so the page goes on as it would, and dropped.
        using var write = new HttpRequestMessage(HttpMethod.Post, "/__bohm/storage")
        {
            Content = new StringContent("""{"tab":"preview","ops":[{"seq":1,"op":"set","key":"notes","value":"overwritten"},{"seq":2,"op":"clear"}],"issued":2}""", Encoding.UTF8, "application/json"),
        };
        write.Headers.Add("X-Bohm-Request", "1");
        using var written = await preview.SendAsync(write);
        HttpAssert.Status(HttpStatusCode.OK, written);
        Assert.Equal(2, JsonDocument.Parse(await written.Content.ReadAsStringAsync()).RootElement.GetProperty("ack").GetInt64());
        Assert.Equal("""{"notes":"kept"}""", (await _host.LoadAsync(id)).Items);

        // Nothing it asks for is recorded against the application.
        using var missing = await preview.GetAsync("/footer.js");
        HttpAssert.Status(HttpStatusCode.NotFound, missing);
        var status = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/status")).RootElement;
        Assert.Empty(status.GetProperty("missingFiles").EnumerateArray());
    }

    [Fact]
    public async Task A_preview_routes_model_calls_as_the_application_does_and_declines_them()
    {
        using (var set = await _host.ControlClient().PutAsync("/__control/llm/company-model", new StringContent(
            """{"endpoint":"https://models.example.test/v1","model":"m"}""", Encoding.UTF8, "application/json"))) HttpAssert.Status(HttpStatusCode.OK, set);
        var id = await _host.AdoptAsync(Page);
        var (token, _) = await CreateAsync(id, Page);
        using var preview = _host.ClientFor($"pv-{token}.localhost");

        // The same calls are routed as on the application's own page: to the known providers and to the organization's server.
        var appBoot = BootOf(await _host.ClientForApp(id).GetStringAsync("/"));
        var previewBoot = BootOf(await preview.GetStringAsync("/"));
        Assert.Equal(appBoot.GetProperty("llmHosts").ToString(), previewBoot.GetProperty("llmHosts").ToString());
        Assert.Contains("api.openai.com", previewBoot.GetProperty("llmHosts").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal("https://models.example.test/v1/", previewBoot.GetProperty("companyBase").GetString());

        Assert.False((await ReadAsync(id, token)).GetProperty("askedModel").GetBoolean());
        // A relayed call is answered at once, as a provider answers a request it cannot serve, and no model is asked.
        using var llm = await preview.PostAsync("/__bohm/llm/api.openai.com/v1/chat/completions", new StringContent("{}"));
        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, llm);
        Assert.Equal("preview", JsonDocument.Parse(await llm.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("type").GetString());
        var report = await ReadAsync(id, token);
        Assert.True(report.GetProperty("askedModel").GetBoolean());
        Assert.Empty(report.GetProperty("blocked").EnumerateArray());

        var status = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/status")).RootElement;
        Assert.Empty(status.GetProperty("missingApis").EnumerateArray());
    }

    [Fact]
    public async Task Load_errors_reported_by_the_preview_are_the_previews_and_not_the_applications()
    {
        var id = await _host.AdoptAsync(Page);
        var (token, _) = await CreateAsync(id, Page + "<script>nope(</script>");
        using var preview = _host.ClientFor($"pv-{token}.localhost");

        await ReportAsync(preview, """{"tab":"preview","kind":"load-error","message":"Uncaught SyntaxError: Unexpected end of input (line 1)"}""");
        await ReportAsync(preview, """{"tab":"preview","kind":"load-error","message":"Uncaught SyntaxError: Unexpected end of input (line 1)"}""");
        await ReportAsync(preview, """{"tab":"preview","kind":"blocked","category":"library","host":"cdn.example.com"}""");
        await ReportAsync(preview, """{"tab":"preview","kind":"input"}""");

        var report = await ReadAsync(id, token);
        Assert.Equal("Uncaught SyntaxError: Unexpected end of input (line 1)", Assert.Single(report.GetProperty("errors").EnumerateArray()).GetString());
        Assert.Equal("library cdn.example.com", Assert.Single(report.GetProperty("blocked").EnumerateArray()).GetString());

        var status = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/status")).RootElement;
        Assert.Empty(status.GetProperty("recentLoadErrors").EnumerateArray());
        Assert.Empty(status.GetProperty("blocked").EnumerateArray());
        Assert.False(status.GetProperty("opened").GetBoolean());
        Assert.False(status.GetProperty("input").GetBoolean());
    }

    [Fact]
    public async Task A_preview_belongs_to_its_application_and_ends_when_removed()
    {
        var id = await _host.AdoptAsync(Page);
        var other = await _host.AdoptAsync(Page + "<p>other</p>");
        var (token, _) = await CreateAsync(id, Page);
        using var client = _host.ControlClient();

        using (var foreign = await client.GetAsync($"/__control/apps/{other}/previews/{token}")) HttpAssert.Status(HttpStatusCode.NotFound, foreign);
        using (var foreignRemove = await client.DeleteAsync($"/__control/apps/{other}/previews/{token}")) HttpAssert.Status(HttpStatusCode.NotFound, foreignRemove);
        using (var removed = await client.DeleteAsync($"/__control/apps/{id}/previews/{token}")) HttpAssert.Status(HttpStatusCode.NoContent, removed);
        using (var gone = await client.GetAsync($"/__control/apps/{id}/previews/{token}")) HttpAssert.Status(HttpStatusCode.NotFound, gone);
        using (var served = await _host.ClientFor($"pv-{token}.localhost").GetAsync("/")) HttpAssert.Status(HttpStatusCode.NotFound, served);

        // No preview of an application that is not here, nor of an empty document.
        using (var unknown = await client.PostAsync("/__control/apps/0123456789abcdef0123456789abcdef/previews", new StringContent(Page))) HttpAssert.Status(HttpStatusCode.NotFound, unknown);
        using (var empty = await client.PostAsync($"/__control/apps/{id}/previews", new ByteArrayContent([]))) HttpAssert.Status(HttpStatusCode.BadRequest, empty);
        // A host name that only looks like a preview is not one.
        using (var guessed = await _host.ClientFor("pv-0123456789abcdef0123456789abcdef.localhost").GetAsync("/")) HttpAssert.Status(HttpStatusCode.NotFound, guessed);
    }

    private async Task<(string Token, string Origin)> CreateAsync(string id, string html)
    {
        using var created = await _host.ControlClient().PostAsync($"/__control/apps/{id}/previews", new StringContent(html, Encoding.UTF8, "text/html"));
        HttpAssert.Status(HttpStatusCode.Created, created);
        var view = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        return (view.GetProperty("token").GetString()!, view.GetProperty("origin").GetString()!);
    }

    private static JsonElement BootOf(string page)
    {
        var start = page.IndexOf("{\"tab\":", StringComparison.Ordinal);
        Assert.True(start >= 0, "no boot in the page");
        var json = new Utf8JsonReader(Encoding.UTF8.GetBytes(page[start..]));
        return JsonElement.ParseValue(ref json);
    }

    private async Task<JsonElement> ReadAsync(string id, string token) =>
        JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/previews/{token}")).RootElement;

    private static async Task ReportAsync(HttpClient preview, string json)
    {
        // What stopped the page is a problem report; use the page saw is a usage report.
        var path = json.Contains("\"kind\":\"input\"", StringComparison.Ordinal) ? "/__bohm/usage" : "/__bohm/problems";
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Bohm-Request", "1");
        using var response = await preview.SendAsync(request);
        HttpAssert.Status(HttpStatusCode.OK, response);
    }
}
