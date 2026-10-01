using LocalOrigin.Storage;
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
    public async Task An_unsaved_adoption_is_marked_and_keeping_it_makes_it_an_ordinary_application()
    {
        var catalog = new AdoptionCatalog(_root);

        var app = await catalog.AdoptAsync(Html, unsaved: true);
        Assert.True(app.Unsaved);
        Assert.Null(app.LeftAt);
        Assert.Equal(app, await new AdoptionCatalog(_root).GetAsync(app.Id)); // survives a restart

        var kept = await catalog.KeepAsync(app.Id);
        Assert.Equal(app with { Unsaved = false }, kept);
        Assert.Equal(kept, await new AdoptionCatalog(_root).GetAsync(app.Id));
        Assert.Equal(kept, await catalog.KeepAsync(app.Id)); // again: no change
        Assert.Null(await catalog.KeepAsync("0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public async Task An_adoption_is_saved_unless_asked_and_its_record_says_nothing_new()
    {
        // Records written before unsaved applications existed carry no such field: they read as saved.
        var catalog = new AdoptionCatalog(_root);

        var app = await catalog.AdoptAsync(Html, "todo.html");

        Assert.False(app.Unsaved);
        var record = await File.ReadAllTextAsync(Path.Combine(_root, "adopted", app.Id, "app.json"));
        Assert.DoesNotContain("unsaved", record);
        Assert.DoesNotContain("leftAt", record);
    }

    [Fact]
    public async Task Leaving_marks_only_an_unsaved_application_and_coming_back_clears_the_mark()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        var catalog = new AdoptionCatalog(_root, clock);
        var unsaved = await catalog.AdoptAsync(Html, unsaved: true);
        var saved = await catalog.AdoptAsync(Html);

        Assert.Equal(clock.GetUtcNow(), (await catalog.SetLeftAsync(unsaved.Id, left: true))!.LeftAt);
        Assert.Equal(saved, await catalog.SetLeftAsync(saved.Id, left: true));
        Assert.Null((await catalog.SetLeftAsync(unsaved.Id, left: false))!.LeftAt);
        Assert.Null(await catalog.SetLeftAsync("0123456789abcdef0123456789abcdef", left: true));
    }

    [Fact]
    public async Task Unsaved_applications_left_for_the_retention_period_go_at_the_sweep_and_the_rest_stay()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        var catalog = new AdoptionCatalog(_root, clock);
        var old = await catalog.AdoptAsync(Html, unsaved: true);
        await catalog.SetLeftAsync(old.Id, left: true);
        clock.Advance(TimeSpan.FromDays(2));
        var recent = await catalog.AdoptAsync(Html, unsaved: true);
        await catalog.SetLeftAsync(recent.Id, left: true);
        var neverLeft = await catalog.AdoptAsync(Html, unsaved: true); // the shell ended with its tab open
        var saved = await catalog.AdoptAsync(Html);
        clock.Advance(AdoptionCatalog.UnsavedRetention - TimeSpan.FromDays(1)); // old: 15 days since it was left · recent: 13

        var discarded = new List<string>();
        var swept = await catalog.SweepUnsavedAsync((folder, _) => { discarded.Add(folder); Directory.Delete(folder, recursive: true); return Task.CompletedTask; });

        Assert.Equal([old.Id], swept.Select(r => r.Id));
        Assert.Single(discarded);
        Assert.Null(await catalog.GetAsync(old.Id));
        Assert.Contains(await catalog.ListRemovedAsync(), r => r.Id == old.Id);
        Assert.NotNull(await catalog.GetAsync(recent.Id));
        Assert.Equal(clock.GetUtcNow(), (await catalog.GetAsync(neverLeft.Id))!.LeftAt);
        Assert.Equal(saved, await catalog.GetAsync(saved.Id));
    }

    [Fact]
    public async Task Archiving_an_unsaved_application_keeps_it_so_the_sweep_never_takes_it()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        var catalog = new AdoptionCatalog(_root, clock);
        var result = await catalog.AdoptAsync(Html, unsaved: true);
        await catalog.SetLeftAsync(result.Id, left: true);

        var archived = await catalog.SetArchivedAsync(result.Id, archived: true);
        Assert.NotNull(archived!.ArchivedAt);
        Assert.False(archived.Unsaved);
        Assert.Null(archived.LeftAt);

        clock.Advance(AdoptionCatalog.UnsavedRetention + TimeSpan.FromDays(1));
        Assert.Empty(await catalog.SweepUnsavedAsync((_, _) => Task.CompletedTask));
        Assert.False((await catalog.SetArchivedAsync(result.Id, archived: false))!.Unsaved); // restored: an application like any other
    }

    [Fact]
    public async Task The_sweep_leaves_an_archived_record_that_still_carries_the_unsaved_mark()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        var catalog = new AdoptionCatalog(_root, clock);
        var result = await catalog.AdoptAsync(Html, unsaved: true);
        await catalog.SetLeftAsync(result.Id, left: true);
        // A record written before archiving implied keeping: both marks at once.
        var file = Path.Combine(_root, "adopted", result.Id, "app.json");
        var record = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        record["archivedAt"] = clock.GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(file, record.ToJsonString());
        var both = await catalog.GetAsync(result.Id);
        Assert.True(both!.Unsaved);
        Assert.NotNull(both.ArchivedAt);

        clock.Advance(AdoptionCatalog.UnsavedRetention + TimeSpan.FromDays(1));
        Assert.Empty(await catalog.SweepUnsavedAsync((_, _) => Task.CompletedTask));
        Assert.NotNull(await catalog.GetAsync(result.Id));
    }

    [Fact]
    public async Task An_unsaved_application_can_be_removed_without_archiving_it_first()
    {
        var catalog = new AdoptionCatalog(_root);
        var unsaved = await catalog.AdoptAsync(Html, unsaved: true);
        var saved = await catalog.AdoptAsync(Html);
        static Task Discard(string folder, CancellationToken _) { Directory.Delete(folder, recursive: true); return Task.CompletedTask; }

        Assert.NotNull(await catalog.RemoveAsync(unsaved.Id, Discard));
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RemoveAsync(saved.Id, Discard));
    }

    [Fact]
    public async Task Archiving_marks_the_record_only_and_restoring_brings_back_everything()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html, "todo.html");
        await using (var storage = await catalog.OpenStorageAsync(app.Id))
            await storage.ApplyAsync([KeyValueOperation.Set("loans", "7")]);
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
    public async Task A_repeated_download_beside_the_file_is_reported_as_a_name_match()
    {
        // Browsers number a repeated download instead of replacing the file: «loans (1).html» beside «loans.html».
        var catalog = new AdoptionCatalog(_root);
        var downloads = Path.Combine(_root, "Downloads");
        var app = await catalog.AdoptAsync(Html, Path.Combine(downloads, "Team loans.html"));
        var revised = Encoding.UTF8.GetBytes("<p>revised</p>");

        var numbered = Assert.Single(await catalog.FindEarlierAdoptionsAsync(revised, Path.Combine(downloads, "Team loans (1).html")));
        var unspaced = Assert.Single(await catalog.FindEarlierAdoptionsAsync(revised, Path.Combine(downloads, "team loans(12).html")));
        var elsewhere = await catalog.FindEarlierAdoptionsAsync(revised, Path.Combine(_root, "Desktop", "Team loans (1).html"));
        var otherName = await catalog.FindEarlierAdoptionsAsync(revised, Path.Combine(downloads, "Team loans 2.html"));
        var samePath = Assert.Single(await catalog.FindEarlierAdoptionsAsync(revised, Path.Combine(downloads, "Team loans.html")));

        Assert.Equal((app.Id, AdoptionMatchKind.SameName), (numbered.App.Id, numbered.Kind));
        Assert.Equal(AdoptionMatchKind.SameName, unspaced.Kind);
        Assert.Empty(elsewhere);
        Assert.Empty(otherName);
        Assert.Equal(AdoptionMatchKind.SameOriginalPath, samePath.Kind);

        // The numbered file taken in as a new revision: the original file still finds the application by name, too.
        await using var storage = await catalog.OpenStorageAsync(app.Id);
        var taken = await catalog.ReviseAsync(app.Id, revised, Path.Combine(downloads, "Team loans (1).html"), storage);
        Assert.Equal("Team loans", taken.Title); // a numbered download does not rename the application
        var back = Assert.Single(await catalog.FindEarlierAdoptionsAsync(Encoding.UTF8.GetBytes("<p>v3</p>"), Path.Combine(downloads, "Team loans (2).html")));
        Assert.Equal(AdoptionMatchKind.SameName, back.Kind);
    }

    [Fact]
    public async Task A_file_that_reads_every_stored_key_is_reported_as_a_stored_keys_match()
    {
        // A revised copy saved elsewhere under another name: no path ties it to the application, but its code
        // names the keys the application's data is under. Every key, as a quoted literal.
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html, Path.Combine(_root, "Downloads", "Team loans.html"));
        var empty = await catalog.AdoptAsync(Encoding.UTF8.GetBytes("<p>never stored anything</p>"), Path.Combine(_root, "notes.html"));
        await using (var storage = await catalog.OpenStorageAsync(app.Id))
        {
            await storage.ApplyAsync([KeyValueOperation.Set("teamShelf.loans", "[]"), KeyValueOperation.Set("teamShelf.members", "[]")]);

            var elsewhere = Path.Combine(_root, "Desktop", "shelf v2.html");
            var both = Encoding.UTF8.GetBytes("<script>load('teamShelf.loans'); load(\"teamShelf.members\")</script>");
            var match = Assert.Single(await catalog.FindEarlierAdoptionsAsync(both, elsewhere)); // storage still open: peeked, not reopened
            Assert.Equal((app.Id, AdoptionMatchKind.SameStoredKeys), (match.App.Id, match.Kind));
            Assert.Equal(AdoptionMatchKind.SameStoredKeys, Assert.Single(await catalog.FindEarlierAdoptionsAsync(both)).Kind); // no path at all

            var one = Encoding.UTF8.GetBytes("<script>load('teamShelf.loans')</script>");
            var unquoted = Encoding.UTF8.GetBytes("<p>teamShelf.loans and teamShelf.members</p>");
            Assert.Empty(await catalog.FindEarlierAdoptionsAsync(one, elsewhere));
            Assert.Empty(await catalog.FindEarlierAdoptionsAsync(unquoted, elsewhere));

            // A closer match wins: the same path is reported as a path match, once.
            var samePath = Assert.Single(await catalog.FindEarlierAdoptionsAsync(both, Path.Combine(_root, "Downloads", "Team loans.html")));
            Assert.Equal(AdoptionMatchKind.SameOriginalPath, samePath.Kind);
        }

        Assert.DoesNotContain(await catalog.FindEarlierAdoptionsAsync(Encoding.UTF8.GetBytes("<p>anything</p>")), m => m.App.Id == empty.Id);
    }

    [Fact]
    public async Task The_revision_history_lists_every_revision_including_one_put_back_from()
    {
        var catalog = new AdoptionCatalog(_root);
        var path = Path.Combine(_root, "Downloads", "Team loans.html");
        var app = await catalog.AdoptAsync(Html, path);
        var only = Assert.Single(await catalog.ListRevisionsAsync(app.Id)); // never revised: one revision, no folder yet
        Assert.Equal((1, (int?)null, app.AdoptedAt, true, (UndoneData?)null), (only.Revision, only.Previous, only.TakenInAt, only.InUse, only.Undone));

        await using var storage = await catalog.OpenStorageAsync(app.Id);
        await catalog.ReviseAsync(app.Id, Encoding.UTF8.GetBytes("<p>v2</p>"), Path.Combine(_root, "Downloads", "Team loans (1).html"), storage);
        await catalog.ReviseAsync(app.Id, Encoding.UTF8.GetBytes("<p>v3</p>"), null, storage); // an applied change
        await storage.ApplyAsync([KeyValueOperation.Set("written-in-v3", "yes")]);
        await catalog.RevertAsync(app.Id, storage);

        var history = await catalog.ListRevisionsAsync(app.Id);
        Assert.Equal([1, 2, 3], history.Select(r => r.Revision));
        Assert.Equal([null, 1, 2], history.Select(r => r.Previous));
        Assert.Equal([path, Path.Combine(_root, "Downloads", "Team loans (1).html"), null], history.Select(r => r.Source.OriginalPath));
        Assert.Equal([false, true, false], history.Select(r => r.InUse));      // put back to 2
        // What 3 wrote is kept aside — and revision 2's code never names its key, so it stays a file.
        Assert.Equal([null, null, UndoneData.Diverged], history.Select(r => r.Undone));
        Assert.Equal(app.AdoptedAt, history[0].TakenInAt);
    }

    [Fact]
    public async Task Data_a_revision_wrote_can_be_taken_back_in_only_while_nothing_was_written_since_and_given_back()
    {
        var catalog = new AdoptionCatalog(_root);
        var reads = Encoding.UTF8.GetBytes("<script>localStorage.getItem('loans')</script>");
        var app = await catalog.AdoptAsync(reads, Path.Combine(_root, "loans.html"));
        await using var storage = await catalog.OpenStorageAsync(app.Id);
        await storage.ApplyAsync([KeyValueOperation.Set("loans", "1")]);
        await catalog.ReviseAsync(app.Id, Encoding.UTF8.GetBytes("<script>localStorage.getItem('loans'); localStorage.getItem('notes')</script>"), null, storage);
        await storage.ApplyAsync([KeyValueOperation.Set("loans", "2"), KeyValueOperation.Set("notes", "x")]);
        await catalog.RevertAsync(app.Id, storage); // back to 1: loans=1; what 2 wrote is kept aside

        // Revision 1 does not name «notes»: taking it in would carry a key the code never reads.
        Assert.Equal(UndoneData.Diverged, (await catalog.ListRevisionsAsync(app.Id))[1].Undone);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ImportUndoneAsync(app.Id, 2, storage));

        // Revision 2 without «notes»: the code in use reads every kept key, and nothing was written since.
        var other = await catalog.AdoptAsync(reads, Path.Combine(_root, "other.html"));
        await using var otherStorage = await catalog.OpenStorageAsync(other.Id);
        await otherStorage.ApplyAsync([KeyValueOperation.Set("loans", "1")]);
        await catalog.ReviseAsync(other.Id, Encoding.UTF8.GetBytes("<script>/* v2 */ localStorage.getItem('loans')</script>"), null, otherStorage);
        await otherStorage.ApplyAsync([KeyValueOperation.Set("loans", "5")]);
        await catalog.RevertAsync(other.Id, otherStorage);
        Assert.Equal(UndoneData.Importable, (await catalog.ListRevisionsAsync(other.Id))[1].Undone);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UndoImportAsync(other.Id, 2, otherStorage)); // nothing taken in yet

        await catalog.ImportUndoneAsync(other.Id, 2, otherStorage);
        Assert.Equal("5", otherStorage.GetItems()["loans"]);
        Assert.Equal(UndoneData.Imported, (await catalog.ListRevisionsAsync(other.Id))[1].Undone);
        Assert.Equal(1, (await catalog.GetAsync(other.Id))!.Revision); // the code stays

        await catalog.UndoImportAsync(other.Id, 2, otherStorage); // the other way
        Assert.Equal("1", otherStorage.GetItems()["loans"]);
        Assert.Equal(UndoneData.Importable, (await catalog.ListRevisionsAsync(other.Id))[1].Undone);

        // Something written since going back: taking the kept data in would lose it.
        await otherStorage.ApplyAsync([KeyValueOperation.Set("loans", "2")]);
        Assert.Equal(UndoneData.Diverged, (await catalog.ListRevisionsAsync(other.Id))[1].Undone);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ImportUndoneAsync(other.Id, 2, otherStorage));
        Assert.Equal("2", otherStorage.GetItems()["loans"]);
    }

    [Fact]
    public async Task A_change_applied_without_a_file_keeps_the_name_and_the_file_it_came_from()
    {
        // An applied change is a revision with no file behind it. The application must stay known by the
        // name it had, and a revised copy of its original file must still find it.
        var catalog = new AdoptionCatalog(_root);
        var path = Path.Combine(_root, "Downloads", "Team loans.html");
        var app = await catalog.AdoptAsync(Html, path);
        await using var storage = await catalog.OpenStorageAsync(app.Id);

        var changed = await catalog.ReviseAsync(app.Id, Encoding.UTF8.GetBytes("<h1>Our shelf</h1>"), null, storage);
        var fromFileAgain = Encoding.UTF8.GetBytes("<h1>Team loans v3</h1>");
        var match = Assert.Single(await catalog.FindEarlierAdoptionsAsync(fromFileAgain, path));

        Assert.Null(app.Title);
        Assert.Null(changed.Source.OriginalPath);
        Assert.Equal("Team loans", changed.Title);
        Assert.Equal(changed, await new AdoptionCatalog(_root).GetAsync(app.Id)); // survives a restart
        Assert.Equal(app.Id, match.App.Id);
        Assert.Equal(AdoptionMatchKind.SameOriginalPath, match.Kind);

        // Once named, the name stays through later revisions and a revert.
        var again = await catalog.ReviseAsync(app.Id, fromFileAgain, Path.Combine(_root, "Downloads", "Team loans (1).html"), storage);
        Assert.Equal("Team loans", again.Title);
        Assert.Equal("Team loans", (await catalog.RevertAsync(app.Id, storage)).Title);
    }

    [Fact]
    public async Task A_made_application_keeps_its_title_through_a_change()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html, originalPath: null, unsaved: false, title: "Summary");
        await using var storage = await catalog.OpenStorageAsync(app.Id);

        var changed = await catalog.ReviseAsync(app.Id, Encoding.UTF8.GetBytes("<p>v2</p>"), null, storage);

        Assert.Equal("Summary", changed.Title);
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
            await storage.ApplyAsync([KeyValueOperation.Set("x", "1")]);

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
        await storage.ApplyAsync([KeyValueOperation.Set("loan", "3")]);
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
        await storage.ApplyAsync([KeyValueOperation.Set("loan", "3")]);
        await catalog.ReviseAsync(v1.Id, Encoding.UTF8.GetBytes("<p>revised</p>"), "loans.html", storage);
        await storage.ApplyAsync([KeyValueOperation.Set("loan", "broken"), KeyValueOperation.Set("extra", "x")]);

        var back = await catalog.RevertAsync(v1.Id, storage);

        Assert.Equal(v1 with { Title = "loans" }, back); // the name the first revision fixed stays
        Assert.Equal(Html, await catalog.ReadHtmlAsync(v1.Id));
        Assert.Equal(new Dictionary<string, string> { ["loan"] = "3" }, storage.GetItems());
        Assert.False(await catalog.CanRevertAsync(v1.Id));
        // What the reverted revision wrote is set aside, not deleted.
        var undone = Directory.GetFiles(Path.Combine(_root, "adopted", v1.Id, "revisions"), "data-undone.json", SearchOption.AllDirectories);
        Assert.Contains("broken", await File.ReadAllTextAsync(Assert.Single(undone)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reverting_a_revision_that_rewrote_much_of_a_large_store_brings_back_every_item_exactly()
    {
        // Not only a few keys: a store of thousands of items and some large values, which the revision
        // changes, deletes, adds to and grows. Reverting must bring back all of it, not part of it.
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html, "books.html");
        await using var storage = await catalog.OpenStorageAsync(app.Id);
        var before = new Dictionary<string, string>();
        for (var i = 0; i < 3000; i++) before[$"book:{i}"] = $"{{\"title\":\"Book {i}\",\"copies\":{i % 7}}}";
        before["catalog"] = new string('x', 256 * 1024);
        before["notes"] = string.Concat(Enumerable.Repeat("가나다라마바사 ", 20_000));
        await storage.ApplyAsync(before.Select(kv => KeyValueOperation.Set(kv.Key, kv.Value)).ToArray());

        await catalog.ReviseAsync(app.Id, Encoding.UTF8.GetBytes("<p>revised</p>"), "books.html", storage);
        var rewrite = new List<KeyValueOperation>();
        for (var i = 0; i < 3000; i += 2) rewrite.Add(KeyValueOperation.Set($"book:{i}", "changed"));
        for (var i = 1; i < 3000; i += 3) rewrite.Add(KeyValueOperation.Remove($"book:{i}"));
        for (var i = 0; i < 500; i++) rewrite.Add(KeyValueOperation.Set($"new:{i}", "added"));
        rewrite.Add(KeyValueOperation.Set("catalog", new string('y', 512 * 1024)));
        rewrite.Add(KeyValueOperation.Remove("notes"));
        await storage.ApplyAsync(rewrite.ToArray());

        await catalog.RevertAsync(app.Id, storage);

        var after = storage.GetItems();
        Assert.Equal(before.Count, after.Count);
        foreach (var (key, value) in before) Assert.Equal(value, after[key]);
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
