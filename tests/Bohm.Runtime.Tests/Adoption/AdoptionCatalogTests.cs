using System.Text;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Storage;
using Bohm.Runtime.Tests.Storage;

namespace Bohm.Runtime.Tests.Adoption;

public sealed class AdoptionCatalogTests : IDisposable
{
    private static readonly byte[] Html = Encoding.UTF8.GetBytes("<!doctype html><title>할 일</title><script>localStorage.setItem('x','1')</script>");

    private readonly string _root = Directory.CreateTempSubdirectory("bohm-catalog-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Adopting_keeps_the_original_bytes_unchanged_and_records_their_hash()
    {
        var catalog = new AdoptionCatalog(_root);

        var app = await catalog.AdoptAsync(Html, "todo.html");

        Assert.Equal(Html, await catalog.ReadHtmlAsync(app.Id));
        Assert.Equal(64, app.Source.Sha256.Length);
        Assert.Equal(Html.Length, app.Source.Size);
        Assert.Equal("none", app.Protection);
        Assert.Equal(app, await catalog.GetAsync(app.Id));
    }

    [Fact]
    public async Task Archiving_marks_the_record_only_and_restoring_brings_back_everything()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html, "todo.html");
        await using (var storage = await catalog.OpenStorageAsync(app.Id))
            await storage.ApplyAsync([StorageOperation.Set("loans", "7")]);
        var filesBefore = Directory.GetFiles(Path.Combine(_root, "adopted", app.Id), "*", SearchOption.AllDirectories).Order().ToList();

        var archived = await catalog.SetArchivedAsync(app.Id, archived: true);
        Assert.NotNull(archived!.ArchivedAt);
        Assert.Equal(archived, await new AdoptionCatalog(_root).GetAsync(app.Id)); // survives a restart
        Assert.Contains(await catalog.ListAsync(), a => a.Id == app.Id && a.ArchivedAt is not null);
        Assert.Equal(archived, await catalog.SetArchivedAsync(app.Id, archived: true)); // again: no change
        Assert.Equal(filesBefore, Directory.GetFiles(Path.Combine(_root, "adopted", app.Id), "*", SearchOption.AllDirectories).Order().ToList());
        Assert.Equal(Html, await catalog.ReadHtmlAsync(app.Id));

        var restored = await catalog.SetArchivedAsync(app.Id, archived: false);
        Assert.Equal(app with { ArchivedAt = null }, restored);
        await using (var storage = await catalog.OpenStorageAsync(app.Id))
            Assert.Equal("7", storage.GetItems()["loans"]);
        Assert.Null(await catalog.SetArchivedAsync("0123456789abcdef0123456789abcdef", archived: true));
    }

    [Fact]
    public async Task Identifiers_are_valid_dns_labels_and_unique_per_adoption()
    {
        var catalog = new AdoptionCatalog(_root);

        var first = await catalog.AdoptAsync(Html);
        var second = await catalog.AdoptAsync(Html);

        Assert.NotEqual(first.Id, second.Id);
        Assert.True(AdoptionCatalog.IsValidId(first.Id));
        Assert.Matches("^[0-9a-f]{32}$", first.Id);
    }

    [Fact]
    public async Task Earlier_adoptions_of_the_same_bytes_are_found_but_not_merged()
    {
        var catalog = new AdoptionCatalog(_root);
        var first = await catalog.AdoptAsync(Html);
        await catalog.AdoptAsync(Encoding.UTF8.GetBytes("<p>other</p>"));

        var match = Assert.Single(await catalog.FindEarlierAdoptionsAsync(Html));

        Assert.Equal(first.Id, match.App.Id);
        Assert.Equal(AdoptionMatchKind.SameBytes, match.Kind);
    }

    [Fact]
    public async Task A_revised_file_at_the_same_path_is_reported_as_a_path_match()
    {
        var catalog = new AdoptionCatalog(_root);
        var path = Path.Combine(_root, "Downloads", "loans.html");
        var v1 = await catalog.AdoptAsync(Html, path);
        await catalog.AdoptAsync(Encoding.UTF8.GetBytes("<p>unrelated</p>"), Path.Combine(_root, "Downloads", "other.html"));

        var revised = Encoding.UTF8.GetBytes("<p>revised</p>");
        var matches = await catalog.FindEarlierAdoptionsAsync(revised, path);
        var none = await catalog.FindEarlierAdoptionsAsync(revised, Path.Combine(_root, "Downloads", "new.html"));
        var noPath = await catalog.FindEarlierAdoptionsAsync(revised);

        var match = Assert.Single(matches);
        Assert.Equal(v1.Id, match.App.Id);
        Assert.Equal(AdoptionMatchKind.SameOriginalPath, match.Kind);
        Assert.Empty(none);
        Assert.Empty(noPath);
    }

    [Fact]
    public async Task Same_bytes_at_the_same_path_is_a_bytes_match_and_paths_are_normalized()
    {
        var catalog = new AdoptionCatalog(_root);
        var path = Path.Combine(_root, "Downloads", "loans.html");
        await catalog.AdoptAsync(Html, path);

        var same = Assert.Single(await catalog.FindEarlierAdoptionsAsync(Html, path));
        var dotted = Assert.Single(await catalog.FindEarlierAdoptionsAsync(Encoding.UTF8.GetBytes("<p>v2</p>"),
            Path.Combine(_root, "Downloads", ".", "loans.html")));

        Assert.Equal(AdoptionMatchKind.SameBytes, same.Kind);
        Assert.Equal(AdoptionMatchKind.SameOriginalPath, dotted.Kind);
    }

    [Fact]
    public async Task The_record_is_readable_json_with_a_format()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html, "todo.html");

        var text = await File.ReadAllTextAsync(Path.Combine(_root, "adopted", app.Id, "app.json"));

        Assert.Contains("\"format\": \"bohm.adopted/1\"", text, StringComparison.Ordinal);
        Assert.Contains($"\"sha256\": \"{app.Source.Sha256}\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_application_has_its_own_storage_that_travels_with_its_folder()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html);
        await using (var storage = await catalog.OpenStorageAsync(app.Id))
            await storage.ApplyAsync([StorageOperation.Set("x", "1")]);

        // Copying the one folder to another data root is all it takes to move the application.
        var elsewhere = Directory.CreateTempSubdirectory("bohm-elsewhere-").FullName;
        try
        {
            CopyDirectory(Path.Combine(_root, "adopted", app.Id), Path.Combine(elsewhere, "adopted", app.Id));
            var moved = new AdoptionCatalog(elsewhere);

            Assert.Equal(app, await moved.GetAsync(app.Id));
            await using var storage = await moved.OpenStorageAsync(app.Id);
            Assert.Equal("1", storage.GetItems()["x"]);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public async Task Listing_ignores_interrupted_adoptions_and_foreign_folders()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html);
        Directory.CreateDirectory(Path.Combine(_root, "adopted", ".staging-0123456789abcdef0123456789abcdef"));
        Directory.CreateDirectory(Path.Combine(_root, "adopted", "not-an-app"));

        Assert.Equal(app.Id, Assert.Single(await catalog.ListAsync()).Id);
    }

    [Fact]
    public async Task An_application_that_cannot_be_read_is_reported_and_never_hides_or_changes_the_others()
    {
        // A record the system cannot open right now (held exclusively here — a file kept only in the
        // cloud while offline fails the same way), and one that is damaged.
        var catalog = new AdoptionCatalog(_root);
        var kept = await catalog.AdoptAsync(Html, "kept.html");
        var held = await catalog.AdoptAsync(Html, "held.html");
        var damaged = await catalog.AdoptAsync(Html, "damaged.html");
        var damagedRecord = Path.Combine(_root, "adopted", damaged.Id, "app.json");
        await File.WriteAllTextAsync(damagedRecord, "{\"format\":\"bohm.adopted/1\",\"id\":");
        var heldRecord = Path.Combine(_root, "adopted", held.Id, "app.json");
        var heldBytes = await File.ReadAllBytesAsync(heldRecord);

        CatalogListing listing;
        using (new FileStream(heldRecord, FileMode.Open, FileAccess.Read, FileShare.None))
            listing = await catalog.ReadListingAsync();

        Assert.Equal(kept.Id, Assert.Single(listing.Apps).Id);
        Assert.Equal(new[] { (damaged.Id, UnreadableApp.Damaged), (held.Id, UnreadableApp.CannotOpen) }.OrderBy(u => u.Id, StringComparer.Ordinal),
            listing.Unreadable.Select(u => (u.Id, u.Kind)));
        Assert.Equal(heldBytes, await File.ReadAllBytesAsync(heldRecord)); // nothing rewritten
        Assert.Equal("{\"format\":\"bohm.adopted/1\",\"id\":", await File.ReadAllTextAsync(damagedRecord));
        Assert.Equal(2, (await catalog.ListAsync()).Count); // once it opens again, it is back
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../../etc")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("")]
    public async Task Malformed_identifiers_never_reach_the_file_system(string id)
    {
        var catalog = new AdoptionCatalog(_root);

        Assert.Null(await catalog.GetAsync(id));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ReadHtmlAsync(id));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.OpenStorageAsync(id));
    }

    [Fact]
    public async Task Listing_is_ordered_by_adoption_time()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var catalog = new AdoptionCatalog(_root, clock);
        var older = await catalog.AdoptAsync(Html);
        clock.Advance(TimeSpan.FromMinutes(1));
        var newer = await catalog.AdoptAsync(Html);

        Assert.Equal([older.Id, newer.Id], (await catalog.ListAsync()).Select(a => a.Id));
    }

    [Fact]
    public async Task A_record_written_before_revisions_existed_is_still_listed_as_its_first_revision()
    {
        // The shape every application adopted before revisions existed has on disk.
        const string id = "0123456789abcdef0123456789abcdef";
        var folder = Path.Combine(_root, "adopted", id);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "app.html"), Html);
        await File.WriteAllTextAsync(Path.Combine(folder, "app.json"), $$"""
            {
              "format": "bohm.adopted/0",
              "id": "{{id}}",
              "adoptedAt": "2026-09-23T01:02:03.0000000+00:00",
              "source": { "sha256": "{{new string('a', 64)}}", "originalPath": "todo.html", "size": {{Html.Length}} },
              "protection": "none"
            }
            """);
        var catalog = new AdoptionCatalog(_root);

        var app = Assert.Single(await catalog.ListAsync());

        Assert.Equal(id, app.Id);
        Assert.Equal(1, app.Revision);
        Assert.Null(app.RevisedAt);
        Assert.Equal(Html, await catalog.ReadHtmlAsync(id));
    }

    [Fact]
    public async Task A_new_revision_keeps_the_application_its_data_and_its_adoption_date()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var catalog = new AdoptionCatalog(_root, clock);
        var v1 = await catalog.AdoptAsync(Html, "loans.html");
        await using var storage = await catalog.OpenStorageAsync(v1.Id);
        await storage.ApplyAsync([StorageOperation.Set("loan", "3")]);
        clock.Advance(TimeSpan.FromDays(2));
        var revisedHtml = Encoding.UTF8.GetBytes("<p>revised</p>");

        var v2 = await catalog.ReviseAsync(v1.Id, revisedHtml, "loans.html", storage);

        Assert.Equal(v1.Id, v2.Id);
        Assert.Equal(v1.AdoptedAt, v2.AdoptedAt);
        Assert.Equal(2, v2.Revision);
        Assert.Equal(clock.GetUtcNow(), v2.RevisedAt);
        Assert.Equal(revisedHtml.Length, v2.Source.Size);
        Assert.Equal(revisedHtml, await catalog.ReadHtmlAsync(v1.Id));
        Assert.Equal("3", storage.GetItems()["loan"]);
        Assert.Equal(v2, await catalog.GetAsync(v1.Id));
        Assert.True(await catalog.CanRevertAsync(v1.Id));
        Assert.False(await catalog.CanRevertAsync((await catalog.AdoptAsync(Html)).Id));
    }

    [Fact]
    public async Task Reverting_restores_the_previous_code_and_its_data_and_keeps_what_the_revision_wrote()
    {
        var catalog = new AdoptionCatalog(_root);
        var v1 = await catalog.AdoptAsync(Html, "loans.html");
        await using var storage = await catalog.OpenStorageAsync(v1.Id);
        await storage.ApplyAsync([StorageOperation.Set("loan", "3")]);
        await catalog.ReviseAsync(v1.Id, Encoding.UTF8.GetBytes("<p>revised</p>"), "loans.html", storage);
        await storage.ApplyAsync([StorageOperation.Set("loan", "broken"), StorageOperation.Set("extra", "x")]);

        var back = await catalog.RevertAsync(v1.Id, storage);

        Assert.Equal(v1, back);
        Assert.Equal(Html, await catalog.ReadHtmlAsync(v1.Id));
        Assert.Equal(new Dictionary<string, string> { ["loan"] = "3" }, storage.GetItems());
        Assert.False(await catalog.CanRevertAsync(v1.Id));
        // What the reverted revision wrote is set aside, not deleted.
        var undone = Directory.GetFiles(Path.Combine(_root, "adopted", v1.Id, "revisions"), "data-undone.json", SearchOption.AllDirectories);
        Assert.Contains("broken", await File.ReadAllTextAsync(Assert.Single(undone)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revisions_are_numbered_onward_and_an_interrupted_revision_is_not_reused()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html);
        await using var storage = await catalog.OpenStorageAsync(app.Id);
        // A revision cut short before its record was written leaves only its folder behind.
        Directory.CreateDirectory(Path.Combine(_root, "adopted", app.Id, "revisions", "2"));

        var revised = await catalog.ReviseAsync(app.Id, Encoding.UTF8.GetBytes("<p>v</p>"), null, storage);
        var reverted = await catalog.RevertAsync(app.Id, storage);
        var again = await catalog.ReviseAsync(app.Id, Encoding.UTF8.GetBytes("<p>w</p>"), null, storage);

        Assert.Equal(1, app.Revision);
        Assert.Equal(3, revised.Revision);
        Assert.Equal(1, reverted.Revision);
        Assert.Equal(4, again.Revision);
        Assert.Equal(Encoding.UTF8.GetBytes("<p>w</p>"), await catalog.ReadHtmlAsync(app.Id));
    }

    [Fact]
    public async Task The_same_bytes_as_the_current_revision_are_not_a_new_revision()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html);
        await using var storage = await catalog.OpenStorageAsync(app.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ReviseAsync(app.Id, Html, null, storage));
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RevertAsync(app.Id, storage));
        Assert.Equal(app, await catalog.GetAsync(app.Id));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(from)) CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }
}
