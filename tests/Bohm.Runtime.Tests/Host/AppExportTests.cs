using System.Net;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Exporting one application: its folder, as it is, copied to a folder the caller names — the data
/// checkpointed first so the copy reads without replaying operations, the original unchanged.
/// </summary>
public sealed class AppExportTests : IAsyncLifetime
{
    private RunningHost _host = null!;
    private string _out = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await RunningHost.StartAsync();
        _out = Directory.CreateTempSubdirectory("bohm-export-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_out, recursive: true);
    }

    [Fact]
    public async Task The_folder_is_copied_as_it_is_with_the_data_in_its_snapshot()
    {
        var id = await _host.AdoptAsync("<!doctype html><title>Books</title>");
        var page = await _host.LoadAsync(id);
        using (var wrote = await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"books","value":"[1,2]"}]}""")) HttpAssert.Status(HttpStatusCode.OK, wrote);
        var target = Path.Combine(_out, "Books (Bohm)");

        using var response = await ExportAsync(id, target);

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal(target, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("path").GetString());
        var original = Path.Combine(_host.DataRoot, "adopted", id);
        Assert.Equal(File.ReadAllBytes(Path.Combine(original, "app.html")), File.ReadAllBytes(Path.Combine(target, "app.html")));
        Assert.Equal(id, JsonDocument.Parse(File.ReadAllBytes(Path.Combine(target, "app.json"))).RootElement.GetProperty("id").GetString());
        var snapshot = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(target, "storage", "local.json"))).RootElement;
        Assert.Contains("[1,2]", snapshot.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(_out, "*.partial-*"));

        // The application keeps working where it was.
        using (var again = await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":2,"op":"set","key":"more","value":"x"}]}""")) HttpAssert.Status(HttpStatusCode.OK, again);
    }

    [Fact]
    public async Task Nothing_is_overwritten()
    {
        var id = await _host.AdoptAsync("<p>x</p>");
        var target = Path.Combine(_out, "taken");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "mine.txt"), "keep me", TestContext.Current.CancellationToken);

        using var response = await ExportAsync(id, target);

        HttpAssert.Status(HttpStatusCode.Conflict, response);
        Assert.Equal(["mine.txt"], Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("relative/folder")]
    [InlineData("")]
    public async Task Only_a_full_path_is_accepted(string target)
    {
        var id = await _host.AdoptAsync("<p>x</p>");

        using var response = await ExportAsync(id, target);

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
    }

    [Fact]
    public async Task A_missing_parent_folder_or_an_unknown_app_is_refused()
    {
        var id = await _host.AdoptAsync("<p>x</p>");

        using (var noParent = await ExportAsync(id, Path.Combine(_out, "no", "such", "place"))) HttpAssert.Status(HttpStatusCode.Conflict, noParent);
        using (var unknown = await ExportAsync(new string('b', 32), Path.Combine(_out, "x"))) HttpAssert.Status(HttpStatusCode.NotFound, unknown);
    }

    [Fact]
    public async Task An_archived_app_can_be_exported_too()
    {
        var id = await _host.AdoptAsync("<p>put away</p>");
        using (var archived = await _host.ControlClient().PostAsync($"/__control/apps/{id}/archive", null)) HttpAssert.Status(HttpStatusCode.OK, archived);

        using var response = await ExportAsync(id, Path.Combine(_out, "archived"));

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.True(File.Exists(Path.Combine(_out, "archived", "app.html")));
    }

    private Task<HttpResponseMessage> ExportAsync(string id, string target) =>
        _host.ControlClient().PostAsync($"/__control/apps/{id}/export", new StringContent(target, Encoding.UTF8));
}
