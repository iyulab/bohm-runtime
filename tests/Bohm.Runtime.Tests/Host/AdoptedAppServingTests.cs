using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bohm.Runtime.Tests.Host;

public sealed partial class AdoptedAppServingTests : IAsyncLifetime
{
    private const string Page = "<!DOCTYPE html>\n<html><head><title>Notes</title></head><body><script>localStorage.setItem('n','1')</script></body></html>";

    private RunningHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await RunningHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_document_is_served_from_the_applications_own_origin_with_the_script_after_the_doctype()
    {
        var id = await _host.AdoptAsync(Page);

        using var response = await _host.ClientForApp(id).GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.StartsWith("<!DOCTYPE html><script>", body, StringComparison.Ordinal);
        Assert.EndsWith("</script>\n<html><head><title>Notes</title></head><body><script>localStorage.setItem('n','1')</script></body></html>", body, StringComparison.Ordinal);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task The_stored_document_is_not_modified_by_serving()
    {
        var id = await _host.AdoptAsync(Page);
        using var _ = await _host.ClientForApp(id).GetAsync("/");

        Assert.Equal(Encoding.UTF8.GetBytes(Page), await _host.Catalog.ReadHtmlAsync(id));
    }

    [Fact]
    public async Task Responses_carry_the_policy_that_keeps_data_inside_the_origin()
    {
        var id = await _host.AdoptAsync(Page);

        using var response = await _host.ClientForApp(id).GetAsync("/");

        Assert.Equal("default-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob:; form-action 'self'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Stored_data_is_inlined_as_ascii_that_cannot_close_the_script()
    {
        var id = await _host.AdoptAsync(Page);
        await using (var storage = await _host.Catalog.OpenStorageAsync(id))
            await storage.ApplyAsync([Runtime.Storage.StorageOperation.Set("note", "</script><b>메모</b>")]);

        // The storage was opened directly above only to seed it; the host opens its own instance.
        var body = await (await _host.ClientForApp(id).GetAsync("/")).Content.ReadAsStringAsync();
        var shim = body[..body.IndexOf("</script>", StringComparison.Ordinal)];

        Assert.DoesNotContain("</script><b>", shim, StringComparison.Ordinal);
        Assert.All(shim, c => Assert.True(c <= 0x7F));
        Assert.Contains("\\u003C/script\\u003E", shim, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("example.com")]
    [InlineData("0123456789abcdef0123456789abcdef.example.com")]
    [InlineData("not-an-id.localhost")]
    public async Task Hosts_other_than_an_application_origin_are_not_served(string host)
    {
        using var response = await _host.ClientFor(host).GetAsync("/");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task An_unknown_application_is_not_found()
    {
        using var response = await _host.ClientForApp("0123456789abcdef0123456789abcdef").GetAsync("/");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task A_document_that_declares_its_charset_is_served_with_that_charset()
    {
        var bytes = Encoding.Latin1.GetBytes("<!doctype html><meta charset=\"EUC-KR\"><p>x</p>");
        var id = (await _host.Catalog.AdoptAsync(bytes)).Id;

        using var response = await _host.ClientForApp(id).GetAsync("/");

        Assert.Equal("euc-kr", response.Content.Headers.ContentType!.CharSet);
    }

    [Fact]
    public async Task Writes_are_journaled_and_seen_by_the_next_load()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await LoadAsync(id);

        using var response = await PostAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"n","value":"1"},{"seq":2,"op":"set","key":"m","value":"2"},{"seq":3,"op":"remove","key":"n"}]}""");

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal(3, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("ack").GetInt64());
        var next = await LoadAsync(id);
        Assert.Equal("""{"m":"2"}""", next.Items);
    }

    [Fact]
    public async Task Resending_a_batch_does_not_apply_it_twice()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await LoadAsync(id);
        const string first = """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"log","value":"a"}]}""";
        const string overlapping = """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"log","value":"a"},{"seq":2,"op":"set","key":"log","value":"ab"}]}""";

        (await PostAsync(id, page, first)).Dispose();
        (await PostAsync(id, page, first)).Dispose();
        using var response = await PostAsync(id, page, overlapping);

        Assert.Equal(2, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("ack").GetInt64());
        await using var storage = await ReopenStorageAsync(id);
        Assert.Equal(2, storage.Sequence);
    }

    [Fact]
    public async Task A_write_without_the_request_header_is_refused()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await LoadAsync(id);

        using var response = await PostAsync(id, page, """{"tab":"TAB","ops":[]}""", header: false);

        HttpAssert.Status(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task A_write_from_another_origin_is_refused()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await LoadAsync(id);

        using var response = await PostAsync(id, page, """{"tab":"TAB","ops":[]}""", origin: "http://evil.localhost");

        HttpAssert.Status(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task One_applications_session_cannot_write_to_another()
    {
        var a = await _host.AdoptAsync(Page);
        var b = await _host.AdoptAsync(Page);
        var pageOfA = await LoadAsync(a);
        await LoadAsync(b);

        // A's cookie and tab presented at B's origin.
        using var response = await PostAsync(b, pageOfA, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"x","value":"stolen"}]}""");

        HttpAssert.Status(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task Malformed_operations_are_rejected_whole()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await LoadAsync(id);

        using var response = await PostAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"ok","value":"1"},{"seq":2,"op":"explode"}]}""");

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Equal("{}", (await LoadAsync(id)).Items);
    }

    [Fact]
    public async Task Data_survives_a_restart_of_the_host()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await LoadAsync(id);
        (await PostAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"kept","value":"yes"}]}""")).Dispose();

        await _host.StopKeepingDataAsync();
        _host = await RunningHost.StartAsync(_host.DataRoot);

        Assert.Equal("""{"kept":"yes"}""", (await LoadAsync(id)).Items);
    }

    private sealed record LoadedPage(string Cookie, string Tab, string Items);

    private async Task<LoadedPage> LoadAsync(string appId)
    {
        using var response = await _host.ClientForApp(appId).GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var boot = JsonDocument.Parse(BootData().Match(body).Groups[1].Value).RootElement;
        return new LoadedPage(cookie[..cookie.IndexOf(';', StringComparison.Ordinal)], boot.GetProperty("tab").GetString()!, boot.GetProperty("items").GetRawText());
    }

    private async Task<HttpResponseMessage> PostAsync(string appId, LoadedPage page, string json, bool header = true, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/storage")
        {
            Content = new StringContent(json.Replace("TAB", page.Tab, StringComparison.Ordinal), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Cookie", page.Cookie);
        if (header) request.Headers.Add("X-Bohm-Request", "1");
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await _host.ClientForApp(appId).SendAsync(request);
    }

    private async Task<Runtime.Storage.AppStorage> ReopenStorageAsync(string appId)
    {
        await _host.StopKeepingDataAsync();
        _host = await RunningHost.StartAsync(_host.DataRoot);
        return await _host.Catalog.OpenStorageAsync(appId);
    }

    [GeneratedRegex("var boot = (\\{.*?\\});", RegexOptions.Singleline)]
    private static partial Regex BootData();
}
