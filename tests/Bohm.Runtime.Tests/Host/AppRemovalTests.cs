using System.Net;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Removing an application for good: only once it is archived, its folder handed to the recycle bin
/// (here a stand-in, so tests never touch the real one), its usage record kept for the report.
/// </summary>
public sealed class AppRemovalTests : IAsyncLifetime
{
    private readonly List<string> _discarded = [];
    private Func<string, Task>? _discardFails;
    private RunningHost _host = null!;
    private string _discardTo = null!;

    public async ValueTask InitializeAsync()
    {
        _discardTo = Directory.CreateTempSubdirectory("bohm-recycle-").FullName;
        _host = await RunningHost.StartAsync(configure: o => o with
        {
            Discard = async (folder, _) =>
            {
                if (_discardFails is { } fail) await fail(folder);
                var to = Path.Combine(_discardTo, Path.GetFileName(folder));
                Directory.Move(folder, to);
                _discarded.Add(to);
            },
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_discardTo, recursive: true);
    }

    [Fact]
    public async Task An_archived_app_is_removed_its_folder_discarded_and_its_usage_kept_in_the_report()
    {
        var id = await _host.AdoptAsync("<!doctype html><title>Old</title>");
        using (var load = await _host.ClientForApp(id).GetAsync("/")) HttpAssert.Status(HttpStatusCode.OK, load);
        await ArchiveAsync(id);
        var before = ReportedUsage(await ReportAsync(), id);

        using var removed = await _host.ControlClient().DeleteAsync($"/__control/apps/{id}");

        HttpAssert.Status(HttpStatusCode.OK, removed);
        Assert.Equal(id, JsonDocument.Parse(await removed.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString());
        var folder = Assert.Single(_discarded);
        Assert.True(File.Exists(Path.Combine(folder, "app.html")), "the whole application folder goes to the recycle bin");
        Assert.False(Directory.Exists(Path.Combine(_host.DataRoot, "adopted", id)));
        Assert.DoesNotContain(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/apps")).RootElement.EnumerateArray(),
            a => a.GetProperty("id").GetString() == id);
        using (var served = await _host.ClientForApp(id).GetAsync("/")) Assert.NotEqual(HttpStatusCode.OK, served.StatusCode);

        var report = await ReportAsync();
        var app = report.GetProperty("apps").EnumerateArray().Single(a => a.GetProperty("id").GetString() == id);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), app.GetProperty("removedOn").GetString());
        Assert.Equal(JsonValueKind.String, app.GetProperty("archivedOn").ValueKind);
        Assert.Equal(before, ReportedUsage(report, id));
    }

    [Fact]
    public async Task An_app_in_use_is_not_removed()
    {
        var id = await _host.AdoptAsync("<p>in use</p>");

        using var response = await _host.ControlClient().DeleteAsync($"/__control/apps/{id}");

        HttpAssert.Status(HttpStatusCode.Conflict, response);
        Assert.Empty(_discarded);
        Assert.True(File.Exists(Path.Combine(_host.DataRoot, "adopted", id, "app.html")));
    }

    [Fact]
    public async Task An_unknown_app_is_not_found()
    {
        using var response = await _host.ControlClient().DeleteAsync($"/__control/apps/{new string('a', 32)}");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task When_the_recycle_bin_refuses_nothing_changes()
    {
        var id = await _host.AdoptAsync("<p>kept</p>");
        await ArchiveAsync(id);
        _discardFails = _ => throw new IOException("recycle bin unavailable");

        using var response = await _host.ControlClient().DeleteAsync($"/__control/apps/{id}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(_host.DataRoot, "adopted", id, "app.html")));
        Assert.False(Directory.Exists(Path.Combine(_host.DataRoot, "removed", id)));
        var app = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/apps")).RootElement.EnumerateArray()
            .Single(a => a.GetProperty("id").GetString() == id);
        Assert.Equal(JsonValueKind.String, app.GetProperty("archivedAt").ValueKind);
        Assert.DoesNotContain((await ReportAsync()).GetProperty("apps").EnumerateArray(), a => a.TryGetProperty("removedOn", out var r) && r.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task A_removal_stopped_before_the_recycle_bin_is_reported_not_hidden()
    {
        var id = await _host.AdoptAsync("<p>half gone</p>");
        await ArchiveAsync(id);
        // As if the runtime ended between renaming the folder aside and sending it on.
        Directory.Move(Path.Combine(_host.DataRoot, "adopted", id), Path.Combine(_host.DataRoot, "adopted", ".removing-" + id));

        var unreadable = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/apps/unreadable")).RootElement;

        var entry = Assert.Single(unreadable.EnumerateArray());
        Assert.Equal(id, entry.GetProperty("id").GetString());
        Assert.Equal("interruptedRemoval", entry.GetProperty("kind").GetString());
        Assert.Contains(".removing-" + id, entry.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_host.DataRoot, "adopted", ".removing-" + id, "app.html")), "nothing is touched");
    }

    private async Task ArchiveAsync(string id)
    {
        using var archived = await _host.ControlClient().PostAsync($"/__control/apps/{id}/archive", null);
        HttpAssert.Status(HttpStatusCode.OK, archived);
    }

    private async Task<JsonElement> ReportAsync() => JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/usage-report")).RootElement;

    private static string ReportedUsage(JsonElement report, string id) =>
        report.GetProperty("apps").EnumerateArray().Single(a => a.GetProperty("id").GetString() == id).GetProperty("usage").GetRawText();
}
