using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Sources;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Packing one application: a zip of its manifest and the folder an export makes, with the paths of
/// the person's files cut to their names, every file hashed and the published version counted.
/// </summary>
public sealed class AppPackageTests : IAsyncLifetime
{
    private RunningHost _host = null!;
    private string _out = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await RunningHost.StartAsync();
        _out = Directory.CreateTempSubdirectory("bohm-package-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_out, recursive: true);
    }

    [Fact]
    public async Task The_package_is_the_manifest_then_the_exported_folder_and_that_folder_is_taken_in_elsewhere()
    {
        var id = await _host.AdoptAsync("<!doctype html><title>Books</title>");
        var page = await _host.LoadAsync(id);
        using (var wrote = await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"books","value":"[1,2]"}]}""")) HttpAssert.Status(HttpStatusCode.OK, wrote);
        var target = Path.Combine(_out, "Books.bohm");

        using var response = await PackAsync(id, target, "all");

        HttpAssert.Status(HttpStatusCode.OK, response);
        var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(target, answer.GetProperty("path").GetString());
        Assert.Equal("1", answer.GetProperty("version").GetString());
        Assert.Empty(Directory.EnumerateFiles(_out, "*.partial-*"));

        using var zip = ZipFile.OpenRead(target);
        Assert.Equal("manifest.json", zip.Entries[0].FullName);
        var manifest = ReadManifest(zip);
        Assert.Equal("bohm.package/0", manifest.GetProperty("format").GetString());
        Assert.Equal("0", manifest.GetProperty("manifestVersion").GetString());
        Assert.Equal(id, manifest.GetProperty("id").GetString());
        Assert.Equal("all", manifest.GetProperty("data").GetString());
        Assert.Contains("storage", Strings(manifest.GetProperty("includes")));
        Assert.Equal(1, manifest.GetProperty("contents").GetProperty("bohm.adopted").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await _host.Catalog.ReadHtmlAsync(id, TestContext.Current.CancellationToken))),
            manifest.GetProperty("provenance").GetProperty("contentSha256").GetString());

        // Every file but the manifest is listed with its hash, and nothing else is.
        var hashes = manifest.GetProperty("provenance").GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        var entries = zip.Entries.Skip(1).ToList();
        Assert.Equal(hashes.Keys.Order(StringComparer.Ordinal), entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        foreach (var entry in entries)
        {
            using var stream = entry.Open();
            Assert.Equal(hashes[entry.FullName], "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream)));
        }

        // Unpacked, it is the folder an export makes — which the import that exists today takes in as the same app with its data.
        var unpacked = Path.Combine(_out, "unpacked");
        zip.ExtractToDirectory(unpacked);
        File.Delete(Path.Combine(unpacked, "manifest.json"));
        var exported = Path.Combine(_out, "exported");
        using (var export = await _host.ControlClient().PostAsync($"/__control/apps/{id}/export", new StringContent(exported, Encoding.UTF8))) HttpAssert.Status(HttpStatusCode.OK, export);
        Assert.Equal(Files(exported), Files(unpacked));
        foreach (var file in Files(exported))
            Assert.Equal(File.ReadAllBytes(Path.Combine(exported, file)), File.ReadAllBytes(Path.Combine(unpacked, file)));

        await using var other = await RunningHost.StartAsync();
        using var imported = await other.ControlClient().PostAsync("/__control/apps/import", new StringContent(unpacked, Encoding.UTF8));
        HttpAssert.Status(HttpStatusCode.Created, imported);
        Assert.Contains("[1,2]", (await other.LoadAsync(id)).Items, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_data_the_package_holds_only_what_makes_the_app()
    {
        var id = await _host.AdoptAsync("<p>x</p>");
        var page = await _host.LoadAsync(id);
        using (var wrote = await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"secret","value":"mine"}]}""")) HttpAssert.Status(HttpStatusCode.OK, wrote);
        var target = Path.Combine(_out, "x.bohm");

        using var response = await PackAsync(id, target, "none");

        HttpAssert.Status(HttpStatusCode.OK, response);
        using var zip = ZipFile.OpenRead(target);
        var manifest = ReadManifest(zip);
        Assert.Equal("none", manifest.GetProperty("data").GetString());
        Assert.Empty(Strings(manifest.GetProperty("includes")));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("storage/", StringComparison.Ordinal) || e.FullName == "usage.ndjson");
        Assert.Contains(zip.Entries, e => e.FullName == "app.html");
    }

    [Fact]
    public async Task Where_the_persons_file_was_is_cut_to_its_name()
    {
        var app = await _host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes("<p>books</p>"), @"C:\Users\someone\Desktop\books.html", cancellationToken: TestContext.Current.CancellationToken);
        var target = Path.Combine(_out, "books.bohm");

        using var response = await PackAsync(app.Id, target, "none");

        HttpAssert.Status(HttpStatusCode.OK, response);
        using var zip = ZipFile.OpenRead(target);
        Assert.Equal("books", ReadManifest(zip).GetProperty("name").GetString());
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            var text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("someone", text, StringComparison.Ordinal);
        }

        var record = JsonDocument.Parse(zip.GetEntry("app.json")!.Open()).RootElement;
        Assert.Equal("books.html", record.GetProperty("source").GetProperty("originalPath").GetString());
        // The application's own record keeps the full path.
        Assert.Equal(@"C:\Users\someone\Desktop\books.html", (await _host.Catalog.GetAsync(app.Id, TestContext.Current.CancellationToken))!.Source.OriginalPath);
    }

    [Fact]
    public async Task The_published_version_goes_up_only_when_the_code_changes()
    {
        var id = await _host.AdoptAsync("<p>one</p>");

        Assert.Equal("1", await VersionAsync(id, "a.bohm"));
        Assert.Equal("1", await VersionAsync(id, "b.bohm"));
        using (var revised = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions", new StringContent("<p>two</p>", Encoding.UTF8)))
            HttpAssert.Status(HttpStatusCode.Created, revised);
        Assert.Equal("2", await VersionAsync(id, "c.bohm"));

        // Without data, the count still travels with the code.
        using var zip = ZipFile.OpenRead(Path.Combine(_out, "c.bohm"));
        var published = JsonDocument.Parse(zip.GetEntry("published.json")!.Open()).RootElement;
        Assert.Equal(2, published.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task The_manifest_names_the_AI_the_page_calls_and_the_sites_its_sources_read()
    {
        var id = await _host.AdoptAsync("""<script>fetch("https://api.openai.com/v1/chat/completions", { method: "POST" })</script>""");
        using (var sources = AppSources.Open(Path.Combine(_host.DataRoot, "adopted", id)))
            await sources.DeclareAsync("list", new SourceRule("https://intra.example/list", "table", ["name"]), granted: true, TestContext.Current.CancellationToken);
        var target = Path.Combine(_out, "ai.bohm");

        using var response = await PackAsync(id, target, "none");

        HttpAssert.Status(HttpStatusCode.OK, response);
        using var zip = ZipFile.OpenRead(target);
        var permissions = ReadManifest(zip).GetProperty("permissions");
        Assert.Equal(["openai"], Strings(permissions.GetProperty("ai")));
        Assert.Equal(["https://intra.example/list"], Strings(permissions.GetProperty("tabs")));
        Assert.Empty(Strings(permissions.GetProperty("network")));
        Assert.Empty(Strings(permissions.GetProperty("shared")));
        Assert.True(permissions.GetProperty("storage").GetBoolean());
        // A package without data carries the rules, never the permission to read.
        Assert.Contains(zip.Entries, e => e.FullName == "sources.json");
    }

    [Fact]
    public async Task Bad_requests_are_refused_and_nothing_is_overwritten()
    {
        var id = await _host.AdoptAsync("<p>x</p>");
        var taken = Path.Combine(_out, "taken.bohm");
        await File.WriteAllTextAsync(taken, "keep me", TestContext.Current.CancellationToken);

        using (var relative = await PackAsync(id, "x.bohm", "all")) HttpAssert.Status(HttpStatusCode.BadRequest, relative);
        using (var notBohm = await PackAsync(id, Path.Combine(_out, "x.zip"), "all")) HttpAssert.Status(HttpStatusCode.BadRequest, notBohm);
        using (var noData = await PackAsync(id, Path.Combine(_out, "x.bohm"), null)) HttpAssert.Status(HttpStatusCode.BadRequest, noData);
        using (var leave = await PackAsync(id, Path.Combine(_out, "x.bohm"), "leave")) HttpAssert.Status(HttpStatusCode.BadRequest, leave);
        using (var exists = await PackAsync(id, taken, "all")) HttpAssert.Status(HttpStatusCode.Conflict, exists);
        using (var noFolder = await PackAsync(id, Path.Combine(_out, "no", "x.bohm"), "all")) HttpAssert.Status(HttpStatusCode.Conflict, noFolder);
        using (var unknown = await PackAsync(new string('b', 32), Path.Combine(_out, "y.bohm"), "all")) HttpAssert.Status(HttpStatusCode.NotFound, unknown);

        Assert.Equal("keep me", await File.ReadAllTextAsync(taken, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_out, "x.bohm")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_host.DataRoot, "adopted"), ".packing-*"));
    }

    [Fact]
    public async Task A_package_is_taken_in_on_another_computer_as_the_same_app_with_its_data()
    {
        var id = await _host.AdoptAsync("<!doctype html><title>Books</title>");
        var page = await _host.LoadAsync(id);
        using (var wrote = await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"books","value":"[1,2]"}]}""")) HttpAssert.Status(HttpStatusCode.OK, wrote);
        var package = Path.Combine(_out, "Books.bohm");
        using (var packed = await PackAsync(id, package, "all")) HttpAssert.Status(HttpStatusCode.OK, packed);

        await using var other = await RunningHost.StartAsync();
        using var imported = await ImportAsync(other, package);

        HttpAssert.Status(HttpStatusCode.Created, imported);
        Assert.Equal(id, JsonDocument.Parse(await imported.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString());
        Assert.Contains("[1,2]", (await other.LoadAsync(id)).Items, StringComparison.Ordinal);
        Assert.True(File.Exists(package), "the package is only read");
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(other.DataRoot, "adopted"), ".unpacking-*"));
    }

    [Fact]
    public async Task An_app_already_here_is_not_replaced_and_the_answer_says_whether_its_code_is_here()
    {
        var id = await _host.AdoptAsync("<p>one</p>");
        var package = Path.Combine(_out, "one.bohm");
        using (var packed = await PackAsync(id, package, "none")) HttpAssert.Status(HttpStatusCode.OK, packed);

        using (var same = await ImportAsync(_host, package))
        {
            HttpAssert.Status(HttpStatusCode.Conflict, same);
            var answer = JsonDocument.Parse(await same.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(id, answer.GetProperty("id").GetString());
            Assert.True(answer.GetProperty("sameCode").GetBoolean());
            Assert.True(answer.GetProperty("sameCodeInUse").GetBoolean());
        }

        using (var revised = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions", new StringContent("<p>two</p>", Encoding.UTF8)))
            HttpAssert.Status(HttpStatusCode.Created, revised);
        using (var older = await ImportAsync(_host, package))
        {
            HttpAssert.Status(HttpStatusCode.Conflict, older);
            var answer = JsonDocument.Parse(await older.Content.ReadAsStringAsync()).RootElement;
            Assert.True(answer.GetProperty("sameCode").GetBoolean());
            Assert.False(answer.GetProperty("sameCodeInUse").GetBoolean());
        }

        Assert.Equal("<p>two</p>", Encoding.UTF8.GetString(await _host.Catalog.ReadHtmlAsync(id, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_package_that_did_not_arrive_whole_is_refused_and_nothing_lands()
    {
        var id = await _host.AdoptAsync("<p>whole</p>");
        var package = Path.Combine(_out, "whole.bohm");
        using (var packed = await PackAsync(id, package, "none")) HttpAssert.Status(HttpStatusCode.OK, packed);
        await using var other = await RunningHost.StartAsync();

        var changed = Rewrite(package, "changed.bohm", (name, bytes) => name == "app.html" ? Encoding.UTF8.GetBytes("<p>evil</p>") : bytes);
        var unlisted = Rewrite(package, "unlisted.bohm", (_, bytes) => bytes, ("extra.js", "alert(1)"u8.ToArray()));
        var outside = Rewrite(package, "outside.bohm", (_, bytes) => bytes, ("../outside.txt", "x"u8.ToArray()));
        var missing = Rewrite(package, "missing.bohm", (name, bytes) => name == "app.html" ? null : bytes);

        foreach (var bad in new[] { changed, unlisted, outside, missing })
        {
            using var refused = await ImportAsync(other, bad);
            HttpAssert.Status(HttpStatusCode.BadRequest, refused);
            Assert.Equal("damaged", JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString());
        }

        Assert.False(Directory.Exists(Path.Combine(other.DataRoot, "adopted", id)));
        Assert.False(File.Exists(Path.Combine(other.DataRoot, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(other.DataRoot, "adopted", "outside.txt")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(other.DataRoot, "adopted"), ".unpacking-*"));
    }

    [Fact]
    public async Task A_file_that_is_not_a_package_of_a_known_format_is_refused()
    {
        var id = await _host.AdoptAsync("<p>x</p>");
        var package = Path.Combine(_out, "x.bohm");
        using (var packed = await PackAsync(id, package, "none")) HttpAssert.Status(HttpStatusCode.OK, packed);
        var text = Path.Combine(_out, "text.bohm");
        await File.WriteAllTextAsync(text, "not a zip", TestContext.Current.CancellationToken);
        var future = Rewrite(package, "future.bohm", (name, bytes) => name == "manifest.json"
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("bohm.package/0", "bohm.package/9", StringComparison.Ordinal))
            : bytes);
        await using var other = await RunningHost.StartAsync();

        using (var notZip = await ImportAsync(other, text))
            Assert.Equal("not-a-package", JsonDocument.Parse(await notZip.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString());
        using (var unknown = await ImportAsync(other, future))
        {
            HttpAssert.Status(HttpStatusCode.BadRequest, unknown);
            Assert.Equal("unknown-format", JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString());
        }
    }

    /// <summary>A copy of a package with each entry passed through <paramref name="change"/> (null drops it) and <paramref name="extra"/> entries added.</summary>
    private string Rewrite(string package, string name, Func<string, byte[], byte[]?> change, params (string Name, byte[] Bytes)[] extra)
    {
        var target = Path.Combine(_out, name);
        using var source = ZipFile.OpenRead(package);
        using var copy = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var entry in source.Entries)
        {
            using var input = entry.Open();
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            if (change(entry.FullName, buffer.ToArray()) is not { } bytes) continue;
            using var output = copy.CreateEntry(entry.FullName).Open();
            output.Write(bytes);
        }

        foreach (var (entryName, bytes) in extra)
        {
            using var output = copy.CreateEntry(entryName).Open();
            output.Write(bytes);
        }

        return target;
    }

    private static Task<HttpResponseMessage> ImportAsync(RunningHost host, string path) =>
        host.ControlClient().PostAsync("/__control/apps/import", new StringContent(path, Encoding.UTF8));

    private async Task<string?> VersionAsync(string id, string name)
    {
        using var response = await PackAsync(id, Path.Combine(_out, name), "none");
        HttpAssert.Status(HttpStatusCode.OK, response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("version").GetString();
    }

    private Task<HttpResponseMessage> PackAsync(string id, string target, string? data) =>
        _host.ControlClient().PostAsync($"/__control/apps/{id}/package" + (data is null ? "" : "?data=" + data), new StringContent(target, Encoding.UTF8));

    private static JsonElement ReadManifest(ZipArchive zip)
    {
        using var stream = zip.GetEntry("manifest.json")!.Open();
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string[] Files(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
}
