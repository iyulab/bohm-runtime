using System.Net;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Writing an application's records out as tables a spreadsheet opens — the way out matching the way in:
/// every key holding a JSON array of objects becomes one CSV in a new folder, the application only read.
/// </summary>
public sealed class AppTableExportTests : IAsyncLifetime
{
    private RunningHost _host = null!;
    private string _out = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await RunningHost.StartAsync();
        _out = Directory.CreateTempSubdirectory("bohm-tables-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_out, recursive: true);
    }

    private static string Set(int seq, string key, string value) =>
        JsonSerializer.Serialize(new { seq, op = "set", key, value });

    [Fact]
    public async Task Each_collection_becomes_a_csv_with_a_column_per_field_and_the_rest_is_left_out()
    {
        var id = await _host.AdoptAsync("<!doctype html><title>Stock</title>");
        var page = await _host.LoadAsync(id);
        var items = """[{"id":1,"name":"A4 paper, 500","qty":10,"supplier":{"name":"Office mart"}},{"id":2,"name":"Toner \"black\"","qty":2.5,"note":"line one\nline two"},{"id":3,"name":"=HYPERLINK(\"x\")","qty":null,"done":true}]""";
        var log = """[{"item":1,"kind":"in","by":"Kim"}]""";
        var ops = string.Join(",", Set(1, "stock-items", items), Set(2, "stock-log", log), Set(3, "theme", "\"dark\""), Set(4, "empty", "[]"));
        using (var wrote = await _host.PostStorageAsync(id, page, $$"""{"tab":"TAB","ops":[{{ops}}]}""")) HttpAssert.Status(HttpStatusCode.OK, wrote);
        var target = Path.Combine(_out, "Stock — tables");

        using var response = await TablesAsync(id, target);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(target, answer.GetProperty("path").GetString());
        var tables = answer.GetProperty("tables").EnumerateArray().ToList();
        Assert.Equal(["stock-items", "stock-log"], tables.Select(t => t.GetProperty("collection").GetString()));
        Assert.Equal(["stock-items.csv", "stock-log.csv"], tables.Select(t => t.GetProperty("file").GetString()));
        Assert.Equal(3, tables[0].GetProperty("records").GetInt32());
        Assert.Equal(["id", "name", "qty", "supplier", "note", "done"], tables[0].GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(["stock-items.csv", "stock-log.csv"], Directory.EnumerateFiles(target).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        var bytes = await File.ReadAllBytesAsync(Path.Combine(target, "stock-items.csv"), TestContext.Current.CancellationToken);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);   // so a spreadsheet reads it as UTF-8
        Assert.Equal(
            "id,name,qty,supplier,note,done\r\n"
            + "1,\"A4 paper, 500\",10,\"{\"\"name\"\":\"\"Office mart\"\"}\",,\r\n"
            + "2,\"Toner \"\"black\"\"\",2.5,,\"line one\nline two\",\r\n"
            + "3,\"'=HYPERLINK(\"\"x\"\")\",,,,true\r\n",   // a formula opens as text, not run
            Encoding.UTF8.GetString(bytes[3..]));
        Assert.Empty(Directory.EnumerateDirectories(_out, "*.partial-*"));

        // Only read: the application's data is as it was.
        using var again = await TablesAsync(id, Path.Combine(_out, "again"));
        HttpAssert.Status(HttpStatusCode.OK, again);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_out, "again", "stock-items.csv"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_application_with_no_records_gets_no_folder()
    {
        var id = await _host.AdoptAsync("<p>x</p>");
        var target = Path.Combine(_out, "none");

        using var response = await TablesAsync(id, target);

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Empty(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("tables").EnumerateArray());
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Nothing_is_overwritten_and_only_a_full_path_for_a_known_app_is_taken()
    {
        var id = await _host.AdoptAsync("<p>x</p>");
        var taken = Path.Combine(_out, "taken");
        Directory.CreateDirectory(taken);

        using (var conflict = await TablesAsync(id, taken)) HttpAssert.Status(HttpStatusCode.Conflict, conflict);
        using (var relative = await TablesAsync(id, "relative/folder")) HttpAssert.Status(HttpStatusCode.BadRequest, relative);
        using (var unknown = await TablesAsync(new string('0', 32), Path.Combine(_out, "unknown"))) HttpAssert.Status(HttpStatusCode.NotFound, unknown);
        Assert.Empty(Directory.EnumerateFileSystemEntries(taken));
    }

    private Task<HttpResponseMessage> TablesAsync(string id, string target) =>
        _host.ControlClient().PostAsync($"/__control/apps/{id}/tables", new StringContent(target, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
}
