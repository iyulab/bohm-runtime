using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bohm.Runtime.Adoption;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A folder registry: an index listing packages by hash, read and installed from by another computer —
/// a new application the first time, the newer code as a new revision after that.
/// </summary>
public sealed class AppRegistryTests : IAsyncLifetime
{
    private RunningHost _publisher = null!;
    private RunningHost _reader = null!;
    private string _registry = null!;

    public async ValueTask InitializeAsync()
    {
        _publisher = await RunningHost.StartAsync();
        _reader = await RunningHost.StartAsync();
        // BOHM_TEST_REGISTRY_ROOT runs the same tests on a network share (a UNC path) instead of the local temp folder.
        var shared = Environment.GetEnvironmentVariable("BOHM_TEST_REGISTRY_ROOT");
        _registry = string.IsNullOrEmpty(shared)
            ? Directory.CreateTempSubdirectory("bohm-registry-").FullName
            : Directory.CreateDirectory(Path.Combine(shared, $"bohm-registry-{Guid.NewGuid():n}")).FullName;
    }

    public async ValueTask DisposeAsync()
    {
        await _publisher.DisposeAsync();
        await _reader.DisposeAsync();
        Directory.Delete(_registry, recursive: true);
    }

    [Fact]
    public async Task An_app_installed_from_a_registry_comes_in_and_remembers_where_from()
    {
        var id = await _publisher.AdoptAsync("<!doctype html><title>Rooms</title><p>rooms</p>");
        var v1 = await PublishAsync(id);
        WriteIndex(Entry(id, "Rooms", v1));

        using (var read = await ReadAsync(_reader, _registry))
        {
            HttpAssert.Status(HttpStatusCode.OK, read);
            var answer = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("ok", answer.GetProperty("state").GetString());
            Assert.Equal(id, answer.GetProperty("index").GetProperty("apps")[0].GetProperty("id").GetString());
        }

        // Inspecting fetches and checks it as installing would, and takes nothing in.
        using (var inspected = await _reader.ControlClient().PostAsync("/__control/registries/inspect",
                   new StringContent(JsonSerializer.Serialize(new { registry = _registry, id }), Encoding.UTF8)))
        {
            HttpAssert.Status(HttpStatusCode.OK, inspected);
            var seen = JsonDocument.Parse(await inspected.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("1", seen.GetProperty("manifest").GetProperty("version").GetString());
            Assert.False(seen.GetProperty("alreadyHere").GetBoolean());
            Assert.Empty(seen.GetProperty("compatibility").EnumerateArray());
        }

        Assert.Null(await _reader.Catalog.GetAsync(id, TestContext.Current.CancellationToken));

        using var installed = await InstallAsync(id);

        HttpAssert.Status(HttpStatusCode.Created, installed);
        var app = JsonDocument.Parse(await installed.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(id, app.GetProperty("id").GetString());
        var from = app.GetProperty("installedFrom");
        Assert.Equal(Path.GetFullPath(_registry), from.GetProperty("registry").GetString());
        Assert.Equal("default", from.GetProperty("channel").GetString());
        Assert.Equal("1", from.GetProperty("version").GetString());
        Assert.Equal("<p>rooms</p>", Encoding.UTF8.GetString(await _reader.Catalog.ReadHtmlAsync(id, TestContext.Current.CancellationToken))[^12..]);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_reader.DataRoot, "adopted"), ".fetching-*"));

        // Where this computer installed it from is its own record: a package made of it does not carry it.
        var package = Path.Combine(Path.GetTempPath(), $"bohm-reader-{Guid.NewGuid():n}.bohm");
        using (var packed = await _reader.ControlClient().PostAsync($"/__control/apps/{id}/package?data=none", new StringContent(package, Encoding.UTF8)))
            HttpAssert.Status(HttpStatusCode.OK, packed);
        try
        {
            using var zip = ZipFile.OpenRead(package);
            using var record = zip.GetEntry("app.json")!.Open();
            Assert.DoesNotContain("installedFrom", new StreamReader(record).ReadToEnd(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(package);
        }
    }

    [Fact]
    public async Task A_newer_version_in_the_registry_comes_in_as_a_new_revision_keeping_the_data_here()
    {
        var id = await _publisher.AdoptAsync("<p>one</p>");
        var v1 = await PublishAsync(id);
        WriteIndex(Entry(id, "One", v1));
        using (var first = await InstallAsync(id)) HttpAssert.Status(HttpStatusCode.Created, first);
        var page = await _reader.LoadAsync(id);
        using (var wrote = await _reader.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"mine","value":"kept"}]}""")) HttpAssert.Status(HttpStatusCode.OK, wrote);

        using (var revised = await _publisher.ControlClient().PostAsync($"/__control/apps/{id}/revisions", new StringContent("<p>two</p>", Encoding.UTF8)))
            HttpAssert.Status(HttpStatusCode.Created, revised);
        var v2 = await PublishAsync(id);
        WriteIndex(Entry(id, "One", v1, v2));

        using var updated = await InstallAsync(id);

        HttpAssert.Status(HttpStatusCode.Created, updated);
        var app = JsonDocument.Parse(await updated.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, app.GetProperty("revision").GetInt32());
        Assert.Equal("2", app.GetProperty("installedFrom").GetProperty("version").GetString());
        Assert.Equal("<p>two</p>", Encoding.UTF8.GetString(await _reader.Catalog.ReadHtmlAsync(id, TestContext.Current.CancellationToken)));
        Assert.Contains("kept", (await _reader.LoadAsync(id)).Items, StringComparison.Ordinal);

        // The same code again is not a new revision; an earlier version asked for by name is.
        using (var again = await InstallAsync(id)) HttpAssert.Status(HttpStatusCode.Conflict, again);
        using (var back = await InstallAsync(id, "1")) HttpAssert.Status(HttpStatusCode.Created, back);
        Assert.Equal("<p>one</p>", Encoding.UTF8.GetString(await _reader.Catalog.ReadHtmlAsync(id, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_registry_that_cannot_be_trusted_or_reached_installs_nothing()
    {
        var id = await _publisher.AdoptAsync("<p>one</p>");
        var v1 = await PublishAsync(id);
        var cancel = TestContext.Current.CancellationToken;

        // A package that is not the one the index lists.
        WriteIndex(Entry(id, "One", v1 with { Sha256 = "sha256:" + new string('0', 64) }));
        await AssertRefusedAsync(id, "damaged");

        // A path outside the registry.
        WriteIndex(Entry(id, "One", v1 with { Src = "../outside.bohm" }));
        await AssertRefusedAsync(id, "bad-entry");

        // An application or version it does not list.
        WriteIndex(Entry(id, "One", v1));
        using (var other = await InstallAsync(new string('a', 32))) Assert.Equal("not-listed", await ReasonAsync(other));
        using (var missing = await InstallAsync(id, "9")) Assert.Equal("not-listed", await ReasonAsync(missing));

        // An index past its validUntil — handing an old index out again must not bring old versions back.
        WriteIndex(Entry(id, "One", v1), validUntil: DateTimeOffset.UtcNow.AddMinutes(-1));
        await AssertStateAsync(_registry, "expired");
        await AssertRefusedAsync(id, "expired");

        // Not a registry, and not there now.
        File.Delete(Path.Combine(_registry, "index.json"));
        await AssertStateAsync(_registry, "unknown-format");
        var away = Path.Combine(_registry, "away");
        await AssertStateAsync(away, "unreachable");
        using (var unreachable = await _reader.ControlClient().PostAsync("/__control/registries/install",
                   new StringContent(JsonSerializer.Serialize(new { registry = away, id }), Encoding.UTF8))) Assert.Equal("unreachable", await ReasonAsync(unreachable));

        Assert.Null(await _reader.Catalog.GetAsync(id, cancel));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_reader.DataRoot, "adopted"), ".fetching-*"));
    }

    [Fact]
    public async Task A_published_app_is_listed_once_per_code_and_installs_on_another_computer()
    {
        var id = await _publisher.AdoptAsync("<p>one</p>");
        var registry = Path.Combine(_registry, "Team apps");
        Directory.CreateDirectory(registry);

        using (var first = await PublishThroughApiAsync(_publisher, registry, id))
        {
            HttpAssert.Status(HttpStatusCode.OK, first);
            Assert.Equal("1", JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("version").GetProperty("version").GetString());
        }

        using (var again = await PublishThroughApiAsync(_publisher, registry, id)) HttpAssert.Status(HttpStatusCode.OK, again);
        using (var revised = await _publisher.ControlClient().PostAsync($"/__control/apps/{id}/revisions", new StringContent("<p>two</p>", Encoding.UTF8)))
            HttpAssert.Status(HttpStatusCode.Created, revised);
        using (var second = await PublishThroughApiAsync(_publisher, registry, id)) HttpAssert.Status(HttpStatusCode.OK, second);

        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(registry, "index.json")))!;
        Assert.Equal("Team apps", index["name"]!.GetValue<string>());
        var versions = index["apps"]!.AsArray().Single()!["versions"]!.AsArray().Select(v => v!["version"]!.GetValue<string>()).ToArray();
        Assert.Equal(["1", "2"], versions);
        Assert.Equal(["1.bohm", "2.bohm"], Directory.GetFiles(Path.Combine(registry, "apps", id)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.False(File.Exists(Path.Combine(registry, "index.lock")));

        using var installed = await _reader.ControlClient().PostAsync("/__control/registries/install",
            new StringContent(JsonSerializer.Serialize(new { registry, id }), Encoding.UTF8));
        HttpAssert.Status(HttpStatusCode.Created, installed);
        Assert.Equal("<p>two</p>", Encoding.UTF8.GetString(await _reader.Catalog.ReadHtmlAsync(id, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Ten_publishers_at_once_from_two_computers_are_all_listed()
    {
        var registry = Path.Combine(_registry, "busy");
        Directory.CreateDirectory(registry);
        var mine = new List<(RunningHost Host, string Id)>();
        for (var i = 0; i < 5; i++)
        {
            mine.Add((_publisher, await _publisher.AdoptAsync($"<p>publisher {i}</p>")));
            mine.Add((_reader, await _reader.AdoptAsync($"<p>reader {i}</p>")));
        }

        var answers = await Task.WhenAll(mine.Select(m => PublishThroughApiAsync(m.Host, registry, m.Id)));

        foreach (var answer in answers)
        {
            HttpAssert.Status(HttpStatusCode.OK, answer);
            answer.Dispose();
        }

        var listed = JsonNode.Parse(File.ReadAllText(Path.Combine(registry, "index.json")))!["apps"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).ToHashSet();
        Assert.True(mine.All(m => listed.Contains(m.Id)), $"listed {listed.Count} of {mine.Count}");
        Assert.Equal(["index.json"], Directory.GetFiles(registry).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task Publishing_never_overwrites_a_version_or_an_index_it_did_not_write()
    {
        var id = await _publisher.AdoptAsync("<p>mine</p>");
        var registry = Path.Combine(_registry, "shared");
        Directory.CreateDirectory(Path.Combine(registry, "apps", id));

        // Someone else's code already published as version 1.
        var other = await _reader.AdoptAsync("<p>someone else's</p>");
        var theirs = Path.Combine(Path.GetTempPath(), $"bohm-theirs-{Guid.NewGuid():n}.bohm");
        using (var packed = await _reader.ControlClient().PostAsync($"/__control/apps/{other}/package?data=none", new StringContent(theirs, Encoding.UTF8)))
            HttpAssert.Status(HttpStatusCode.OK, packed);
        File.Move(theirs, Path.Combine(registry, "apps", id, "1.bohm"));
        var before = File.ReadAllBytes(Path.Combine(registry, "apps", id, "1.bohm"));
        using (var taken = await PublishThroughApiAsync(_publisher, registry, id))
        {
            HttpAssert.Status(HttpStatusCode.Conflict, taken);
            Assert.Equal("version-taken", JsonDocument.Parse(await taken.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString());
        }

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(registry, "apps", id, "1.bohm")));

        // An index in another format is left as it is.
        var foreign = Path.Combine(_registry, "foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "index.json"), """{"format":"someone.else/3"}""");
        using (var unknown = await PublishThroughApiAsync(_publisher, foreign, id)) Assert.Equal("unknown-format", await ReasonAsync(unknown));
        Assert.Equal("""{"format":"someone.else/3"}""", File.ReadAllText(Path.Combine(foreign, "index.json")));

        using (var away = await PublishThroughApiAsync(_publisher, Path.Combine(_registry, "away"), id)) Assert.Equal("unreachable", await ReasonAsync(away));
    }

    [Fact]
    public async Task A_reader_holding_the_index_is_waited_out_and_leaves_no_partial_index_behind()
    {
        var first = await _publisher.AdoptAsync("<p>first</p>");
        var second = await _publisher.AdoptAsync("<p>second</p>");
        var registry = Path.Combine(_registry, "read");
        Directory.CreateDirectory(registry);
        using (var published = await PublishThroughApiAsync(_publisher, registry, first)) HttpAssert.Status(HttpStatusCode.OK, published);

        // Another program reads the index without sharing delete, as most readers do, for a second.
        var reader = new FileStream(Path.Combine(registry, "index.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var publishing = PublishThroughApiAsync(_publisher, registry, second);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await reader.DisposeAsync();

        using (var published = await publishing) HttpAssert.Status(HttpStatusCode.OK, published);
        var listed = JsonNode.Parse(File.ReadAllText(Path.Combine(registry, "index.json")))!["apps"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).ToHashSet();
        Assert.True(listed.SetEquals([first, second]));
        Assert.Equal(["index.json"], Directory.GetFiles(registry).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task A_held_lock_that_looks_stale_by_a_skewed_clock_is_waited_for_not_given_up_on()
    {
        // TODO: elsewhere an open file can be deleted, so a held lock that looks stale is taken over — the lock needs a
        // liveness check that does not rest on Windows refusing to delete a file still open.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a lock file its holder has open.");
        var data = Directory.CreateTempSubdirectory("bohm-skewed-").FullName;
        try
        {
            // This computer's clock runs ten minutes ahead of the share's: every lock the share stamps looks stale.
            var catalog = new AdoptionCatalog(data, new Storage.ManualClock(DateTimeOffset.UtcNow.AddMinutes(10)));
            var app = await catalog.AdoptAsync(Encoding.UTF8.GetBytes("<p>held</p>"), cancellationToken: TestContext.Current.CancellationToken);
            var registry = Path.Combine(_registry, "held");
            Directory.CreateDirectory(registry);

            var held = new FileStream(Path.Combine(registry, "index.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            var publishing = catalog.PublishToRegistryAsync(registry, app.Id, "Team apps", new Dictionary<string, string>(), TimeSpan.FromDays(7),
                TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(publishing.IsCompleted, publishing.Exception?.ToString());

            await held.DisposeAsync();
            Assert.NotNull(await publishing);
            var listed = JsonNode.Parse(File.ReadAllText(Path.Combine(registry, "index.json")))!["apps"]!.AsArray().Select(a => a!["id"]!.GetValue<string>());
            Assert.Equal([app.Id], listed);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task A_lock_being_deleted_by_the_publisher_before_is_waited_for_not_an_error()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows keeps a deleted file's name until its last handle closes.");
        var id = await _publisher.AdoptAsync("<p>next</p>");
        var registry = Path.Combine(_registry, "releasing");
        Directory.CreateDirectory(registry);

        // The publisher before has closed its lock, but the file is still on its way out (another handle keeps it from
        // going): Windows refuses to create it again with "access denied" rather than "exists" until it is gone.
        var path = Path.Combine(registry, "index.lock");
        var closing = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
        var lingering = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await closing.DisposeAsync();

        var publishing = PublishThroughApiAsync(_publisher, registry, id);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(publishing.IsCompleted, publishing.IsCompleted ? $"answered {(int)(await publishing).StatusCode}" : null);
        await lingering.DisposeAsync();

        using (var published = await publishing) HttpAssert.Status(HttpStatusCode.OK, published);
        Assert.Equal(["index.json"], Directory.GetFiles(registry).Select(Path.GetFileName).ToArray());
    }

    private static Task<HttpResponseMessage> PublishThroughApiAsync(RunningHost host, string registry, string id) =>
        host.ControlClient().PostAsync("/__control/registries/publish", new StringContent(JsonSerializer.Serialize(new { registry, id }), Encoding.UTF8));

    private async Task AssertRefusedAsync(string id, string reason)
    {
        using var refused = await InstallAsync(id);
        Assert.Equal(reason, await ReasonAsync(refused));
    }

    private async Task AssertStateAsync(string root, string state)
    {
        using var read = await ReadAsync(_reader, root);
        HttpAssert.Status(HttpStatusCode.OK, read);
        Assert.Equal(state, JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString());
    }

    private static async Task<string?> ReasonAsync(HttpResponseMessage response)
    {
        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString();
    }

    /// <summary>Packs the publisher's application into the registry as the next version and returns its index entry.</summary>
    private async Task<Version> PublishAsync(string id)
    {
        var folder = Path.Combine(_registry, "apps", id);
        Directory.CreateDirectory(folder);
        var staging = Path.Combine(folder, $"staging-{Guid.NewGuid():n}.bohm");
        using var packed = await _publisher.ControlClient().PostAsync($"/__control/apps/{id}/package?data=none", new StringContent(staging, Encoding.UTF8));
        HttpAssert.Status(HttpStatusCode.OK, packed);
        var version = JsonDocument.Parse(await packed.Content.ReadAsStringAsync()).RootElement.GetProperty("version").GetString()!;
        var file = Path.Combine(folder, version + ".bohm");
        File.Move(staging, file);
        return new Version(version, $"apps/{id}/{version}.bohm", "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
    }

    private static JsonObject Entry(string id, string name, params Version[] versions) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["permissions"] = new JsonObject { ["storage"] = true, ["ai"] = new JsonArray(), ["tabs"] = new JsonArray(), ["network"] = new JsonArray(), ["shared"] = new JsonArray() },
        ["versions"] = new JsonArray(versions.Select(v => (JsonNode)new JsonObject { ["version"] = v.Name, ["src"] = v.Src, ["sha256"] = v.Sha256, ["contract"] = 1 }).ToArray()),
    };

    private void WriteIndex(JsonObject app, DateTimeOffset? validUntil = null)
    {
        var index = new JsonObject
        {
            ["manifestVersion"] = "0",
            ["format"] = "bohm.registry/0",
            ["name"] = "Team apps",
            ["date"] = DateTimeOffset.UtcNow.ToString("O"),
            ["validUntil"] = (validUntil ?? DateTimeOffset.UtcNow.AddDays(30)).ToString("O"),
            ["apps"] = new JsonArray(app),
            ["somethingNewer"] = "ignored",
        };
        File.WriteAllText(Path.Combine(_registry, "index.json"), index.ToJsonString());
    }

    private static Task<HttpResponseMessage> ReadAsync(RunningHost host, string root) =>
        host.ControlClient().PostAsync("/__control/registries/read", new StringContent(root, Encoding.UTF8));

    private Task<HttpResponseMessage> InstallAsync(string id, string? version = null) =>
        _reader.ControlClient().PostAsync("/__control/registries/install",
            new StringContent(JsonSerializer.Serialize(new { registry = _registry, id, version }), Encoding.UTF8));

    private sealed record Version(string Name, string Src, string Sha256);
}
