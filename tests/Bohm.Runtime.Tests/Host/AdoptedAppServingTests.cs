using System.Net;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

public sealed class AdoptedAppServingTests : IAsyncLifetime
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

        Assert.Equal("default-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob:; form-action 'self'; frame-ancestors 'self'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/missing.js")]
    [InlineData("/__bohm/storage")]
    public async Task Every_response_from_the_origin_keeps_its_address_and_its_responses_to_itself(string path)
    {
        var id = await _host.AdoptAsync(Page);

        using var response = await _host.ClientForApp(id).GetAsync(path);

        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy")));
        // Would lose the writes a closing page sends (see IsolationHeaders).
        Assert.False(response.Headers.Contains("Cross-Origin-Opener-Policy"));
        Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
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
        var page = await _host.LoadAsync(id);

        using var response = await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"n","value":"1"},{"seq":2,"op":"set","key":"m","value":"2"},{"seq":3,"op":"remove","key":"n"}]}""");

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal(3, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("ack").GetInt64());
        var next = await _host.LoadAsync(id);
        Assert.Equal("""{"m":"2"}""", next.Items);
    }

    [Fact]
    public async Task Resending_a_batch_does_not_apply_it_twice()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);
        const string first = """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"log","value":"a"}]}""";
        const string overlapping = """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"log","value":"a"},{"seq":2,"op":"set","key":"log","value":"ab"}]}""";

        (await _host.PostStorageAsync(id, page, first)).Dispose();
        (await _host.PostStorageAsync(id, page, first)).Dispose();
        using var response = await _host.PostStorageAsync(id, page, overlapping);

        Assert.Equal(2, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("ack").GetInt64());
        await using var storage = await ReopenStorageAsync(id);
        Assert.Equal(2, storage.Sequence);
    }

    [Fact]
    public async Task A_write_without_the_request_header_is_refused()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);

        using var response = await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[]}""", header: false);

        HttpAssert.Status(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task A_write_from_another_origin_is_refused()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);

        using var response = await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[]}""", origin: "http://evil.localhost");

        HttpAssert.Status(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task One_applications_session_cannot_write_to_another()
    {
        var a = await _host.AdoptAsync(Page);
        var b = await _host.AdoptAsync(Page);
        var pageOfA = await _host.LoadAsync(a);
        await _host.LoadAsync(b);

        // A's cookie and tab presented at B's origin.
        using var response = await _host.PostStorageAsync(b, pageOfA, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"x","value":"stolen"}]}""");

        HttpAssert.Status(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task Malformed_operations_are_rejected_whole()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);

        using var response = await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"ok","value":"1"},{"seq":2,"op":"explode"}]}""");

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Equal("{}", (await _host.LoadAsync(id)).Items);
    }

    [Fact]
    public async Task Data_survives_a_restart_of_the_host()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"kept","value":"yes"}]}""")).Dispose();

        await _host.StopKeepingDataAsync();
        _host = await RunningHost.StartAsync(_host.DataRoot);

        Assert.Equal("""{"kept":"yes"}""", (await _host.LoadAsync(id)).Items);
    }

    private async Task<Runtime.Storage.AppStorage> ReopenStorageAsync(string appId)
    {
        await _host.StopKeepingDataAsync();
        _host = await RunningHost.StartAsync(_host.DataRoot);
        return await _host.Catalog.OpenStorageAsync(appId);
    }
}
