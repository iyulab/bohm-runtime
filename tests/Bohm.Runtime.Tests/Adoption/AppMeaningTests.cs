using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Bohm.Runtime.Adoption;

namespace Bohm.Runtime.Tests.Adoption;

public sealed class AppMeaningTests : IDisposable
{
    private static readonly byte[] Html = Encoding.UTF8.GetBytes("<!doctype html><title>Reading log</title><p>log</p>");

    private readonly string _root = Directory.CreateTempSubdirectory("bohm-meaning-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(string id) => Path.Combine(_root, "adopted", id);

    [Fact]
    public async Task An_application_made_from_words_keeps_them_beside_it_and_reads_them_back()
    {
        var catalog = new AdoptionCatalog(_root);

        var app = await catalog.AdoptAsync(Html, title: "Reading log", request: "  A reading log with title, author and the day I finished  ", via: "made");

        var request = await new AdoptionCatalog(_root).GetFirstRequestAsync(app.Id);
        Assert.Equal("A reading log with title, author and the day I finished", request?.Text);
        Assert.Equal("made", request?.Via);
        Assert.Equal(app.AdoptedAt, request?.At);
        var file = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(Folder(app.Id), AppMeaning.FileName)))!;
        Assert.Equal(AppMeaning.Format, (string?)file["format"]);
    }

    [Fact]
    public async Task Without_words_or_a_known_way_nothing_is_kept()
    {
        var catalog = new AdoptionCatalog(_root);

        var brought = await catalog.AdoptAsync(Html, "log.html");
        var blank = await catalog.AdoptAsync(Html, request: "   ", via: "made");
        var unknownWay = await catalog.AdoptAsync(Html, request: "a log", via: "dreamed");

        foreach (var app in new[] { brought, blank, unknownWay })
        {
            Assert.False(File.Exists(Path.Combine(Folder(app.Id), AppMeaning.FileName)));
            Assert.Null(await catalog.GetFirstRequestAsync(app.Id));
        }

        Assert.Null(await catalog.GetFirstRequestAsync("not-an-id"));
    }

    [Fact]
    public async Task A_very_long_request_is_cut_to_its_opening()
    {
        var app = await new AdoptionCatalog(_root).AdoptAsync(Html, request: new string('x', FirstRequest.MaxLength + 50), via: "promoted");

        Assert.Equal(FirstRequest.MaxLength, (await new AdoptionCatalog(_root).GetFirstRequestAsync(app.Id))?.Text.Length);
    }

    [Fact]
    public void A_reader_ignores_members_it_does_not_know_and_refuses_another_format()
    {
        var later = """{"format":"bohm.app/1","request":{"text":"a log","at":"2026-11-06T01:02:03+00:00","via":"made","tone":"warm"},"collections":[{"name":"books"}],"seed":{}}""";
        Assert.Equal("a log", AppMeaning.ParseRequest(Encoding.UTF8.GetBytes(later))?.Text);

        foreach (var other in new[]
        {
            """{"format":"bohm.app/2","request":{"text":"a log","at":"2026-11-06T01:02:03+00:00","via":"made"}}""",
            """{"format":"bohm.sources/1","request":{"text":"a log","at":"2026-11-06T01:02:03+00:00","via":"made"}}""",
            """{"format":"bohm.app/1","request":{"text":"a log","via":"made"}}""",
            """{"format":"bohm.app/1"}""",
            """[]""",
        })
            Assert.Null(AppMeaning.ParseRequest(Encoding.UTF8.GetBytes(other)));
    }

    [Fact]
    public async Task A_revision_and_going_back_leave_the_first_request_as_it_was()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html, request: "a reading log", via: "made");
        var before = await catalog.GetFirstRequestAsync(app.Id);

        await using (var storage = await catalog.OpenStorageAsync(app.Id))
        {
            await catalog.ReviseAsync(app.Id, Encoding.UTF8.GetBytes("<!doctype html><title>Reading log</title><p>v2</p>"), null, storage, request: "add a rating");
            await catalog.RevertAsync(app.Id, storage);
        }

        Assert.Equal(before, await catalog.GetFirstRequestAsync(app.Id));
    }

    [Fact]
    public async Task The_history_shows_the_first_revision_with_the_words_it_was_asked_for_with()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html, request: "a reading log", via: "made");
        Assert.Equal("a reading log", Assert.Single(await catalog.ListRevisionsAsync(app.Id)).Request);

        await using (var storage = await catalog.OpenStorageAsync(app.Id))
            await catalog.ReviseAsync(app.Id, Encoding.UTF8.GetBytes("<!doctype html><title>Reading log</title><p>v2</p>"), null, storage, request: "add a rating");

        var history = await catalog.ListRevisionsAsync(app.Id);
        Assert.Equal(["a reading log", "add a rating"], history.Select(r => r.Request));

        var brought = await catalog.AdoptAsync(Html, "log.html");
        Assert.Null(Assert.Single(await catalog.ListRevisionsAsync(brought.Id)).Request);
    }

    [Fact]
    public async Task A_copy_without_data_carries_the_file_and_a_package_leaves_the_persons_words_behind()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Html, request: "a reading log for my team", via: "made");

        var copy = Path.Combine(_root, "copy");
        await catalog.ExportAsync(app.Id, copy, openStorage: null, withData: false);
        Assert.True(File.Exists(Path.Combine(copy, AppMeaning.FileName)));

        var package = Path.Combine(_root, "log.bohm");
        await catalog.PackAsync(app.Id, package, PackageData.None, new Dictionary<string, string>());
        using (var zip = ZipFile.OpenRead(package))
        {
            Assert.DoesNotContain(zip.Entries, e => e.FullName == AppMeaning.FileName);   // only the words were in it — nothing else to carry
            Assert.DoesNotContain(zip.Entries, e => new StreamReader(e.Open()).ReadToEnd().Contains("reading log for my team", StringComparison.Ordinal));
        }

        Assert.Equal("a reading log for my team", (await catalog.GetFirstRequestAsync(app.Id))?.Text);   // the person's own copy keeps them
    }

    [Fact]
    public async Task Leaving_the_words_behind_keeps_what_else_the_file_says()
    {
        var folder = Directory.CreateTempSubdirectory("bohm-meaning-later-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, AppMeaning.FileName),
                """{"format":"bohm.app/1","request":{"text":"mine","at":"2026-11-06T01:02:03+00:00","via":"made"},"collections":[{"name":"books","label":"Books","fields":{"t":"Title"}}]}""");

            await AppMeaning.KeepWhatTravelsAsync(folder);

            var left = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(folder, AppMeaning.FileName)))!.AsObject();
            Assert.False(left.ContainsKey("request"));
            Assert.Equal("Title", (string?)left["collections"]![0]!["fields"]!["t"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
