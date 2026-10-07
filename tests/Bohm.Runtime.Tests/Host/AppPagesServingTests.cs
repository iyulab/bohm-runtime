using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Pages through the host: the shell hands over a page the person sent through the control API; the
/// application reads the list and each page at <c>/__bohm/pages</c> on its own origin. Which applications
/// receive pages is what their manifest's <c>share_target</c> says.
/// </summary>
public sealed class AppPagesServingTests : IAsyncLifetime
{
    private const string Sent = """{"url":"https://news.example/story","title":"A story","text":"First line.\nSecond line.","html":"<p>First line.</p><p>Second line.</p>","lang":"en","byline":"Kim"}""";
    private const string ReceivingApp =
        """<!doctype html><title>Reading list</title><link rel="manifest" href='data:application/manifest+json,{"share_target":{"action":"/","params":{"title":"title","url":"url"}}}'>""";

    private RunningHost _host = null!;
    private string _out = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await RunningHost.StartAsync();
        _out = Directory.CreateTempSubdirectory("bohm-pages-export-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_out, recursive: true);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendAsync(string id, string page = Sent) =>
        await _host.ControlClient().PostAsync($"/__control/apps/{id}/pages", Json(page));

    private async Task<string> SentIdAsync(string id, string page = Sent)
    {
        using var response = await SendAsync(id, page);
        HttpAssert.Status(HttpStatusCode.OK, response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("page").GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> AppGetAsync(string id, string path, string? cookie, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await _host.ClientForApp(id).SendAsync(request);
    }

    [Fact]
    public async Task A_sent_page_is_answered_as_listed_and_read_whole_by_the_application_with_a_plain_fetch()
    {
        var id = await _host.AdoptAsync(ReceivingApp);

        using var sent = await SendAsync(id);
        HttpAssert.Status(HttpStatusCode.OK, sent);
        var answer = JsonDocument.Parse(await sent.Content.ReadAsStringAsync()).RootElement;
        Assert.False(answer.GetProperty("withoutHtml").GetBoolean());
        var pageId = answer.GetProperty("page").GetProperty("id").GetString()!;
        Assert.Equal("First line. Second line.", answer.GetProperty("page").GetProperty("excerpt").GetString());

        var loaded = await _host.LoadAsync(id);
        using var list = await AppGetAsync(id, "/__bohm/pages", loaded.Cookie);   // no X-Bohm-Request: a generated fetch cannot add it
        HttpAssert.Status(HttpStatusCode.OK, list);
        Assert.Equal("no-store", list.Headers.CacheControl?.ToString());
        var listed = Assert.Single(JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.EnumerateArray());
        Assert.Equal(pageId, listed.GetProperty("id").GetString());
        Assert.Equal("A story", listed.GetProperty("title").GetString());
        Assert.False(listed.TryGetProperty("text", out _));   // the list is light; the page is read by its id

        using var one = await AppGetAsync(id, $"/__bohm/pages/{pageId}", loaded.Cookie);
        HttpAssert.Status(HttpStatusCode.OK, one);
        var page = JsonDocument.Parse(await one.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("https://news.example/story", page.GetProperty("url").GetString());
        Assert.Equal("First line.\nSecond line.", page.GetProperty("text").GetString());
        Assert.Equal("<p>First line.</p><p>Second line.</p>", page.GetProperty("html").GetString());
        Assert.Equal("en", page.GetProperty("lang").GetString());
        Assert.Equal("Kim", page.GetProperty("byline").GetString());
    }

    [Fact]
    public async Task An_application_that_received_nothing_lists_no_pages()
    {
        var id = await _host.AdoptAsync(ReceivingApp);
        var loaded = await _host.LoadAsync(id);

        using var list = await AppGetAsync(id, "/__bohm/pages", loaded.Cookie);

        HttpAssert.Status(HttpStatusCode.OK, list);
        Assert.Equal("[]", await list.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_the_applications_own_page_reads_its_pages_and_only_the_shell_sends_them()
    {
        var id = await _host.AdoptAsync(ReceivingApp);
        var pageId = await SentIdAsync(id);
        var loaded = await _host.LoadAsync(id);
        var other = await _host.AdoptAsync("<p>other</p>");
        var otherLoaded = await _host.LoadAsync(other);

        using (var noSession = await AppGetAsync(id, "/__bohm/pages", cookie: null)) HttpAssert.Status(HttpStatusCode.Forbidden, noSession);
        using (var foreign = await AppGetAsync(id, $"/__bohm/pages/{pageId}", loaded.Cookie, origin: "http://evil.example")) HttpAssert.Status(HttpStatusCode.Forbidden, foreign);
        using (var anotherApp = await AppGetAsync(id, $"/__bohm/pages/{pageId}", otherLoaded.Cookie)) HttpAssert.Status(HttpStatusCode.Forbidden, anotherApp);
        using (var elsewhere = await AppGetAsync(other, $"/__bohm/pages/{pageId}", otherLoaded.Cookie)) HttpAssert.Status(HttpStatusCode.NotFound, elsewhere);
        using (var unknown = await AppGetAsync(id, "/__bohm/pages/0000000000000000", loaded.Cookie)) HttpAssert.Status(HttpStatusCode.NotFound, unknown);
        using (var climbing = await AppGetAsync(id, "/__bohm/pages/..%2Fapp", loaded.Cookie)) HttpAssert.Status(HttpStatusCode.NotFound, climbing);

        using var post = new HttpRequestMessage(HttpMethod.Post, "/__bohm/pages") { Content = Json(Sent) };
        post.Headers.Add("Cookie", loaded.Cookie);
        post.Headers.Add("X-Bohm-Request", "1");
        using (var write = await _host.ClientForApp(id).SendAsync(post)) HttpAssert.Status(HttpStatusCode.MethodNotAllowed, write);   // the page reads; only the shell sends
    }

    [Fact]
    public async Task A_page_without_an_address_or_text_or_with_too_much_text_is_refused()
    {
        var id = await _host.AdoptAsync(ReceivingApp);
        var tooMuch = JsonSerializer.Serialize(new { url = "https://news.example/huge", text = new string('a', 2 * 1024 * 1024 + 1) });

        using (var noUrl = await SendAsync(id, """{"text":"x"}""")) HttpAssert.Status(HttpStatusCode.BadRequest, noUrl);
        using (var notWeb = await SendAsync(id, """{"url":"file:///C:/x.txt","text":"x"}""")) HttpAssert.Status(HttpStatusCode.BadRequest, notWeb);
        using (var noText = await SendAsync(id, """{"url":"https://news.example/a","text":" "}""")) HttpAssert.Status(HttpStatusCode.BadRequest, noText);
        using (var malformed = await SendAsync(id, "{")) HttpAssert.Status(HttpStatusCode.BadRequest, malformed);
        using (var huge = await SendAsync(id, tooMuch)) HttpAssert.Status(HttpStatusCode.RequestEntityTooLarge, huge);
        using (var noApp = await SendAsync(new string('0', 32))) HttpAssert.Status(HttpStatusCode.NotFound, noApp);
        Assert.Equal("[]", await (await AppGetAsync(id, "/__bohm/pages", (await _host.LoadAsync(id)).Cookie)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_shell_is_told_which_applications_receive_pages_and_under_which_query_names()
    {
        var receiving = await _host.AdoptAsync(ReceivingApp);
        var plain = await _host.AdoptAsync("<!doctype html><title>Notes</title>");
        var archived = await _host.AdoptAsync(ReceivingApp.Replace("Reading list", "Old list", StringComparison.Ordinal));
        using (var put = await _host.ControlClient().PostAsync($"/__control/apps/{archived}/archive", null)) Assert.True(put.IsSuccessStatusCode);

        var targets = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/apps/page-targets")).RootElement;

        var target = Assert.Single(targets.EnumerateArray());
        Assert.Equal(receiving, target.GetProperty("id").GetString());
        Assert.Equal("title", target.GetProperty("params").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, target.GetProperty("params").GetProperty("text").ValueKind);
        Assert.Equal("url", target.GetProperty("params").GetProperty("url").GetString());
        Assert.NotEqual(plain, receiving);
    }

    [Fact]
    public async Task A_preview_reads_the_applications_pages_as_they_are_and_a_new_proposal_has_none()
    {
        var id = await _host.AdoptAsync(ReceivingApp);
        await SentIdAsync(id);
        using var created = await _host.ControlClient().PostAsync($"/__control/apps/{id}/previews", new StringContent("<p>proposed</p>", Encoding.UTF8, "text/html"));
        HttpAssert.Status(HttpStatusCode.Created, created);
        var token = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();

        using var response = await _host.ClientFor($"pv-{token}.localhost").GetAsync("/__bohm/pages");

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal("A story", Assert.Single(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray()).GetProperty("title").GetString());

        using var fresh = await _host.ControlClient().PostAsync("/__control/previews", Json("""{"html":"<p>new</p>","readings":{}}"""));
        HttpAssert.Status(HttpStatusCode.Created, fresh);
        var freshToken = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        using var none = await _host.ClientFor($"pv-{freshToken}.localhost").GetAsync("/__bohm/pages");
        Assert.Equal("[]", await none.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_new_proposal_is_tried_with_pages_sent_to_its_preview_which_go_with_it()
    {
        using var created = await _host.ControlClient().PostAsync("/__control/previews", Json(JsonSerializer.Serialize(new { html = ReceivingApp, readings = new { } })));
        HttpAssert.Status(HttpStatusCode.Created, created);
        var view = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var token = view.GetProperty("token").GetString();
        Assert.Equal("title", view.GetProperty("shareTarget").GetProperty("title").GetString());
        Assert.Equal("url", view.GetProperty("shareTarget").GetProperty("url").GetString());

        using var sent = await _host.ControlClient().PostAsync($"/__control/previews/{token}/pages", Json(Sent));
        HttpAssert.Status(HttpStatusCode.OK, sent);
        var pageId = JsonDocument.Parse(await sent.Content.ReadAsStringAsync()).RootElement.GetProperty("page").GetProperty("id").GetString();

        using var preview = _host.ClientFor($"pv-{token}.localhost");
        var listed = Assert.Single(JsonDocument.Parse(await preview.GetStringAsync("/__bohm/pages")).RootElement.EnumerateArray());
        Assert.Equal(pageId, listed.GetProperty("id").GetString());
        var whole = JsonDocument.Parse(await preview.GetStringAsync($"/__bohm/pages/{pageId}")).RootElement;
        Assert.Equal("First line.\nSecond line.", whole.GetProperty("text").GetString());
        Assert.Empty(Directory.EnumerateDirectories(_host.DataRoot, "pages", SearchOption.AllDirectories));   // held with the preview, nowhere on disk

        using (await _host.ControlClient().DeleteAsync($"/__control/previews/{token}")) { }
        using var gone = await _host.ControlClient().PostAsync($"/__control/previews/{token}/pages", Json(Sent));
        HttpAssert.Status(HttpStatusCode.NotFound, gone);

        using var plain = await _host.ControlClient().PostAsync("/__control/previews", Json("""{"html":"<p>new</p>","readings":{}}"""));
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await plain.Content.ReadAsStringAsync()).RootElement.GetProperty("shareTarget").ValueKind);
    }

    [Fact]
    public async Task A_preview_asks_no_model_until_a_page_is_sent_and_then_its_own_page_asks_as_the_application_would()
    {
        using var created = await _host.ControlClient().PostAsync("/__control/previews", Json(JsonSerializer.Serialize(new { html = ReceivingApp, readings = new { } })));
        var token = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        using var preview = _host.ClientFor($"pv-{token}.localhost");
        using var load = await preview.GetAsync("/");
        var cookie = Assert.Single(load.Headers.GetValues("Set-Cookie")).Split(';')[0];   // a session of the preview's own
        async Task<HttpResponseMessage> AskAsync(string? withCookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions") { Content = Json("""{"model":"m"}""") };
            if (withCookie is not null) request.Headers.Add("Cookie", withCookie);
            return await preview.SendAsync(request);
        }

        using (var declined = await AskAsync(cookie)) HttpAssert.Status(HttpStatusCode.ServiceUnavailable, declined);
        Assert.True(JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/previews/{token}")).RootElement.GetProperty("askedModel").GetBoolean());

        using (var sent = await _host.ControlClient().PostAsync($"/__control/previews/{token}/pages", Json(Sent))) HttpAssert.Status(HttpStatusCode.OK, sent);

        using var asked = await AskAsync(cookie);   // relayed as the application's would be: here, no key is connected
        HttpAssert.Status(HttpStatusCode.Unauthorized, asked);
        Assert.Equal("bohm_no_key", JsonDocument.Parse(await asked.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("type").GetString());
        using var stranger = await AskAsync(null);
        HttpAssert.Status(HttpStatusCode.Forbidden, stranger);
    }

    [Fact]
    public async Task Pages_are_data_they_go_with_the_data_and_stay_behind_without_it()
    {
        var id = await _host.AdoptAsync(ReceivingApp);
        await SentIdAsync(id);
        var withData = Path.Combine(_out, "with");
        var withoutData = Path.Combine(_out, "without");

        using (var exported = await _host.ControlClient().PostAsync($"/__control/apps/{id}/export", new StringContent(withData, Encoding.UTF8))) HttpAssert.Status(HttpStatusCode.OK, exported);
        using (var exported = await _host.ControlClient().PostAsync($"/__control/apps/{id}/export?data=leave", new StringContent(withoutData, Encoding.UTF8))) HttpAssert.Status(HttpStatusCode.OK, exported);

        Assert.True(File.Exists(Path.Combine(withData, "pages", "index.ndjson")));
        Assert.False(Directory.Exists(Path.Combine(withoutData, "pages")));

        var package = Path.Combine(_out, "List.bohm");
        using (var packed = await _host.ControlClient().PostAsync($"/__control/apps/{id}/package?data=all", new StringContent(package, Encoding.UTF8))) HttpAssert.Status(HttpStatusCode.OK, packed);
        using var zip = ZipFile.OpenRead(package);
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.Contains("received-pages", manifest.RootElement.GetProperty("includes").EnumerateArray().Select(i => i.GetString()));
    }
}
