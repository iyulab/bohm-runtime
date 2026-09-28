using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Host;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A result made for the person starts unsaved: kept, it becomes one of their applications; left, it
/// stays for the retention period counted from when it was left, and the next start after that
/// removes it the way a removal does — never while it can be open.
/// </summary>
public sealed class UnsavedAppsTests
{
    private const string Page = "<!doctype html><title>Summary</title><p>Rows</p>";

    private static RuntimeHostOptions Discarding(RuntimeHostOptions o) =>
        o with { Discard = (folder, _) => { Directory.Delete(folder, recursive: true); return Task.CompletedTask; } };

    [Fact]
    public async Task An_unsaved_result_is_listed_as_such_and_keeping_it_makes_it_an_ordinary_application()
    {
        await using var host = await RunningHost.StartAsync(configure: Discarding);
        var id = (await host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page), unsaved: true)).Id;

        var listed = await AppAsync(host, id);
        Assert.True(listed.GetProperty("unsaved").GetBoolean());

        using var keep = await host.ControlClient().PostAsync($"/__control/apps/{id}/keep", null);
        HttpAssert.Status(HttpStatusCode.OK, keep);
        var kept = await AppAsync(host, id);
        Assert.False(kept.GetProperty("unsaved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("expiresAt").ValueKind);

        using var unknown = await host.ControlClient().PostAsync("/__control/apps/0123456789abcdef0123456789abcdef/keep", null);
        HttpAssert.Status(HttpStatusCode.NotFound, unknown);
    }

    [Fact]
    public async Task Archiving_an_unsaved_result_keeps_it_and_the_next_start_leaves_it()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-unsaved-").FullName;
        try
        {
            var first = await RunningHost.StartAsync(dataRoot, configure: Discarding);
            var id = (await first.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page), unsaved: true)).Id;
            using (var left = await first.ControlClient().PostAsync($"/__control/apps/{id}/left", null)) HttpAssert.Status(HttpStatusCode.OK, left);
            using (var archive = await first.ControlClient().PostAsync($"/__control/apps/{id}/archive", null)) HttpAssert.Status(HttpStatusCode.OK, archive);
            var listed = await AppAsync(first, id);
            Assert.False(listed.GetProperty("unsaved").GetBoolean());
            Assert.Equal(JsonValueKind.Null, listed.GetProperty("expiresAt").ValueKind);
            await first.StopKeepingDataAsync();

            await using var second = await RunningHost.StartAsync(dataRoot, configure: Discarding);
            Assert.NotNull((await second.Catalog.GetAsync(id))!.ArchivedAt);
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Leaving_an_unsaved_result_starts_its_retention_and_opening_it_again_ends_it()
    {
        await using var host = await RunningHost.StartAsync(configure: Discarding);
        var id = (await host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page), unsaved: true)).Id;

        using (var left = await host.ControlClient().PostAsync($"/__control/apps/{id}/left", null)) HttpAssert.Status(HttpStatusCode.OK, left);
        var listed = await AppAsync(host, id);
        var leftAt = listed.GetProperty("leftAt").GetDateTimeOffset();
        Assert.Equal(leftAt + AdoptionCatalog.UnsavedRetention, listed.GetProperty("expiresAt").GetDateTimeOffset());

        using (var opened = await host.ClientForApp(id).GetAsync("/")) HttpAssert.Status(HttpStatusCode.OK, opened);
        var back = await AppAsync(host, id);
        Assert.Equal(JsonValueKind.Null, back.GetProperty("leftAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, back.GetProperty("expiresAt").ValueKind);
    }

    [Fact]
    public async Task An_unsaved_result_can_be_removed_without_archiving_it_but_a_saved_application_cannot()
    {
        await using var host = await RunningHost.StartAsync(configure: Discarding);
        var unsaved = (await host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page), unsaved: true)).Id;
        var saved = await host.AdoptAsync(Page);

        using (var removed = await host.ControlClient().DeleteAsync($"/__control/apps/{unsaved}")) HttpAssert.Status(HttpStatusCode.OK, removed);
        using (var refused = await host.ControlClient().DeleteAsync($"/__control/apps/{saved}")) HttpAssert.Status(HttpStatusCode.Conflict, refused);
        Assert.Null(await host.Catalog.GetAsync(unsaved));
    }

    [Fact]
    public async Task The_next_start_removes_results_left_for_the_retention_period_and_keeps_the_rest()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-unsaved-").FullName;
        try
        {
            var first = await RunningHost.StartAsync(dataRoot, configure: Discarding);
            var old = (await first.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page), unsaved: true)).Id;
            var recent = (await first.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page), unsaved: true)).Id;
            var openAtExit = (await first.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page), unsaved: true)).Id;
            var saved = await first.AdoptAsync(Page);
            await first.Catalog.SetLeftAsync(old, left: true);
            await first.Catalog.SetLeftAsync(recent, left: true);
            await first.StopKeepingDataAsync();
            // Time passes: the old one was left 15 days ago, the recent one 13.
            MoveLeftAt(dataRoot, old, TimeSpan.FromDays(-15));
            MoveLeftAt(dataRoot, recent, TimeSpan.FromDays(-13));

            await using var second = await RunningHost.StartAsync(dataRoot, configure: Discarding);

            Assert.Null(await second.Catalog.GetAsync(old));
            Assert.Contains(await second.Catalog.ListRemovedAsync(), r => r.Id == old);
            Assert.NotNull(await second.Catalog.GetAsync(recent));
            Assert.NotNull((await second.Catalog.GetAsync(openAtExit))!.LeftAt); // counted from this start
            Assert.False((await second.Catalog.GetAsync(saved))!.Unsaved);
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static void MoveLeftAt(string dataRoot, string id, TimeSpan by)
    {
        var path = Path.Combine(dataRoot, "adopted", id, "app.json");
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var leftAt = DateTimeOffset.Parse(record["leftAt"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        record["leftAt"] = (leftAt + by).ToString("O", CultureInfo.InvariantCulture);
        File.WriteAllText(path, record.ToJsonString());
    }

    private static async Task<JsonElement> AppAsync(RunningHost host, string id)
    {
        using var document = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/apps"));
        return document.RootElement.EnumerateArray().Single(a => a.GetProperty("id").GetString() == id).Clone();
    }
}
