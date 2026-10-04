using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bohm.Runtime.TableImports;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A table file into an application's records through the control API: what it could go into, what it
/// would do, doing it in one write with the data before kept aside, pages loaded before unable to
/// write, and undoing it while nothing has been written since.
/// </summary>
public sealed class TableImportEndpointsTests : IAsyncLifetime
{
    private const string App = """<!doctype html><title>Books</title><script>const books = JSON.parse(localStorage.getItem("books") || "[]");</script>""";
    private const string Books = """[{"id":1,"title":"자바 입문","isbn":"978-1"},{"id":2,"title":"C# 깊이","isbn":"978-2"}]""";

    private RunningHost _host = null!;
    private string _app = null!;
    private RunningHost.LoadedPage _page = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await RunningHost.StartAsync();
        _app = await _host.AdoptAsync(App);
        _page = await _host.LoadAsync(_app);
        using var stored = await _host.PostStorageAsync(_app, _page,
            $$"""{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"books","value":{{JsonSerializer.Serialize(Books)}}},{"seq":2,"op":"set","key":"theme","value":"\"dark\""}]}""");
        HttpAssert.Status(HttpStatusCode.OK, stored);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_stored_lists_of_records_are_what_a_file_can_go_into()
    {
        var imports = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{_app}/imports")).RootElement;

        var books = Assert.Single(imports.GetProperty("collections").EnumerateArray());
        Assert.Equal("books", books.GetProperty("collection").GetString());
        Assert.Equal(2, books.GetProperty("records").GetInt32());
        Assert.Equal(["id", "title", "isbn"], books.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        Assert.Empty(imports.GetProperty("imports").EnumerateArray());
        Assert.Empty(imports.GetProperty("fileTypes").EnumerateArray()); // the page has no file input of its own
    }

    [Fact]
    public async Task The_file_kinds_an_application_takes_in_itself_are_told()
    {
        var app = await _host.AdoptAsync("""<!doctype html><title>Ledger</title><input type="file" accept=".csv,.xlsx" hidden><script>localStorage.setItem("tx", "[]");</script>""");
        var imports = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{app}/imports")).RootElement;
        Assert.Equal([".csv", ".xlsx"], imports.GetProperty("fileTypes").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task A_preview_shows_what_would_happen_and_writes_nothing()
    {
        using var preview = await PostAsync("imports/preview", Body("isbn,title,publisher\n978-2,C# 깊이,한빛\n978-3,파이썬,길벗", identity: "isbn"));

        HttpAssert.Status(HttpStatusCode.OK, preview);
        var plan = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((1, 1), (plan.GetProperty("added").GetInt32(), plan.GetProperty("skipped").GetInt32()));
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("columns")[2].GetProperty("field").ValueKind); // publisher goes nowhere, and says so
        Assert.Equal(3, plan.GetProperty("sample")[0].GetProperty("id").GetInt32());
        Assert.Equal(Books, await StoredBooksAsync());
    }

    [Fact]
    public async Task An_import_writes_the_rows_reloads_pages_and_can_be_undone_while_nothing_was_written_since()
    {
        using (var imported = await PostAsync("imports", Body("isbn,title\n978-3,파이썬\n978-4,코틀린", identity: "isbn")))
        {
            HttpAssert.Status(HttpStatusCode.Created, imported);
            var record = JsonDocument.Parse(await imported.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal((1, 2, "books.csv"), (record.GetProperty("number").GetInt32(), record.GetProperty("added").GetInt32(), record.GetProperty("file").GetString()));
        }

        var after = JsonNode.Parse(await StoredBooksAsync())!.AsArray();
        Assert.Equal(["자바 입문", "C# 깊이", "파이썬", "코틀린"], after.Select(b => b!["title"]!.GetValue<string>()));

        // A page loaded before the import can no longer write — it reloads and reads the imported data.
        using (var stale = await _host.PostStorageAsync(_app, _page, """{"tab":"TAB","ops":[{"seq":3,"op":"set","key":"books","value":"[]"}]}"""))
            Assert.NotEqual(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal(4, JsonNode.Parse(await StoredBooksAsync())!.AsArray().Count);

        // The same file again adds nothing: nothing to import.
        using (var again = await PostAsync("imports", Body("isbn,title\n978-3,파이썬\n978-4,코틀린", identity: "isbn")))
            HttpAssert.Status(HttpStatusCode.Conflict, again);

        using (var undone = await PostAsync("imports/1/undo", null))
        {
            HttpAssert.Status(HttpStatusCode.OK, undone);
            Assert.True(JsonDocument.Parse(await undone.Content.ReadAsStringAsync()).RootElement.GetProperty("undone").GetBoolean());
        }

        Assert.Equal(Books, await StoredBooksAsync());
        using (var twice = await PostAsync("imports/1/undo", null)) HttpAssert.Status(HttpStatusCode.Conflict, twice);
        Assert.True(JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{_app}/imports")).RootElement
            .GetProperty("imports")[0].GetProperty("undone").GetBoolean());
    }

    [Fact]
    public async Task What_the_person_settled_in_an_import_is_where_the_next_one_starts()
    {
        // The person puts «서명» into title and tells records apart by isbn.
        var first = JsonSerializer.Serialize(new
        {
            file = new { name = "books.csv", content = Convert.ToBase64String(Encoding.UTF8.GetBytes("서명,isbn\n파이썬,978-3")) },
            collection = "books",
            identity = "isbn",
            columns = new Dictionary<string, string?> { ["서명"] = "title" },
        });
        using (var imported = await PostAsync("imports", first)) HttpAssert.Status(HttpStatusCode.Created, imported);

        var books = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{_app}/imports")).RootElement.GetProperty("collections")[0];
        Assert.Equal("isbn", books.GetProperty("identity").GetString());

        // A file like it, sent with nothing chosen: «서명» goes into title again.
        using var preview = await PostAsync("imports/preview", Body("서명,isbn\n코틀린,978-4", identity: "isbn"));
        var plan = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("title", plan.GetProperty("columns")[0].GetProperty("field").GetString());
        Assert.Equal("코틀린", plan.GetProperty("sample")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task An_import_is_not_undone_over_data_written_since()
    {
        using (var imported = await PostAsync("imports", Body("title\n파이썬"))) HttpAssert.Status(HttpStatusCode.Created, imported);
        var page = await _host.LoadAsync(_app);
        using (var written = await _host.PostStorageAsync(_app, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"theme","value":"\"light\""}]}"""))
            HttpAssert.Status(HttpStatusCode.OK, written);

        using var undo = await PostAsync("imports/1/undo", null);

        HttpAssert.Status(HttpStatusCode.Conflict, undo);
        Assert.Equal(3, JsonNode.Parse(await StoredBooksAsync())!.AsArray().Count);
    }

    [Theory]
    [InlineData("theme", null, "unknown-collection")]
    [InlineData("books", "publisher", "unknown-identity")]
    public async Task A_choice_that_cannot_be_used_is_refused_with_why(string collection, string? identity, string problem)
    {
        using var refused = await PostAsync("imports/preview", Body("title\n파이썬", collection, identity));

        HttpAssert.Status(HttpStatusCode.BadRequest, refused);
        Assert.Equal(problem, JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task A_file_that_is_neither_utf8_nor_text_in_this_computers_code_page_is_refused()
    {
        // A lead byte followed by a line break: no character in UTF-8, nor in a double-byte code page (the
        // only kind assumed without the person choosing one).
        byte[] neither = [0x74, 0x0A, 0xB0, 0x0A, 0x31];
        using var preview = await PostAsync("imports/preview", Body(null, "books", "isbn", content: Convert.ToBase64String(neither)));

        HttpAssert.Status(HttpStatusCode.BadRequest, preview);
        Assert.Equal("not-utf8", JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task A_file_in_the_code_page_the_person_chose_is_read_in_it_and_says_so()
    {
        // «도서» as a Korean bank's CSV comes — CP949 — chosen by the person (the computer may be in any code page).
        byte[] cp949 = [0xB5, 0xB5, 0xBC, 0xAD, 0x0A, 0x31];

        using var preview = await PostAsync("imports/preview", Body(null, "books", null, content: Convert.ToBase64String(cp949), codePage: 949));

        HttpAssert.Status(HttpStatusCode.OK, preview);
        var plan = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("도서", plan.GetProperty("columns")[0].GetProperty("column").GetString());
        Assert.Equal(949, plan.GetProperty("codePage").GetInt32());
    }

    [Fact]
    public async Task A_utf8_file_says_no_code_page()
    {
        using var preview = await PostAsync("imports/preview", Body("title\n파이썬", codePage: 949));
        HttpAssert.Status(HttpStatusCode.OK, preview);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement.GetProperty("codePage").ValueKind);
    }

    [Fact]
    public async Task An_unknown_application_or_import_is_not_found()
    {
        using (var app = await _host.ControlClient().GetAsync("/__control/apps/0123456789abcdef0123456789abcdef/imports")) HttpAssert.Status(HttpStatusCode.NotFound, app);
        using (var import = await PostAsync("imports/7/undo", null)) HttpAssert.Status(HttpStatusCode.NotFound, import);
    }

    private static string Body(string? csv, string collection = "books", string? identity = null, string? content = null, int? codePage = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["file"] = new Dictionary<string, object?> { ["name"] = "books.csv", ["content"] = content ?? Convert.ToBase64String(Encoding.UTF8.GetBytes(csv!)), ["codePage"] = codePage },
            ["collection"] = collection,
            ["identity"] = identity,
        });

    private Task<HttpResponseMessage> PostAsync(string path, string? body) =>
        _host.ControlClient().PostAsync($"/__control/apps/{_app}/{path}", body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"));

    private async Task<string> StoredBooksAsync() =>
        JsonDocument.Parse((await _host.LoadAsync(_app)).Items).RootElement.GetProperty("books").GetString()!;
}
