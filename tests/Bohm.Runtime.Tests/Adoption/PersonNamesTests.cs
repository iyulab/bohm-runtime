using System.Text;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.TableImports;
using LocalOrigin.Storage;

namespace Bohm.Runtime.Tests.Adoption;

public sealed class PersonNamesTests : IDisposable
{
    private const string Loans = """[{"book":"Dune","due":"2026-12-01","n":1},{"book":"Emma","due":"2026-12-03","n":2}]""";

    private readonly string _root = Directory.CreateTempSubdirectory("bohm-names-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Tables_written_out_carry_the_persons_names_and_come_back_in_to_the_same_fields()
    {
        var catalog = new AdoptionCatalog(_root);
        RememberedKind[] kinds = [new("loans", "대출", [new("book", "책"), new("due", "반납일")])];
        var app = await catalog.AdoptAsync(Encoding.UTF8.GetBytes("<!doctype html><title>Loans</title>"), collections: kinds);
        await using (var storage = await catalog.OpenStorageAsync(app.Id))
        {
            await storage.ApplyAsync([KeyValueOperation.Set("loans", Loans), KeyValueOperation.Set("notes", """[{"text":"hi"}]""")]);   // «notes» has no person's name: it keeps its key
        }

        var target = Path.Combine(_root, "tables");
        var written = await catalog.ExportTablesAsync(app.Id, target);

        Assert.Equal(["notes.csv", "대출.csv"], written!.Select(t => t.File).Order(StringComparer.Ordinal));
        var csv = await File.ReadAllTextAsync(Path.Combine(target, "대출.csv"));
        Assert.StartsWith("책,반납일,n\r\n", csv.TrimStart('﻿'), StringComparison.Ordinal);   // a field with no name for the person keeps its own
        Assert.StartsWith("text\r\n", (await File.ReadAllTextAsync(Path.Combine(target, "notes.csv"))).TrimStart('﻿'), StringComparison.Ordinal);

        var declaration = PersonNames.Recall(TableImport.Collections([new("loans", Loans)])[0].Declaration, await catalog.GetCollectionsAsync(app.Id));
        var plan = TableImport.Plan(declaration, TableFile.Read(Encoding.UTF8.GetBytes(csv)), "[]");
        Assert.Equal(["book", "due", "n"], plan.Columns.Select(c => c.Field));
        Assert.Equal(2, plan.Added);
    }

    [Fact]
    public void A_name_that_would_read_back_into_another_field_is_not_used()
    {
        var kind = new RememberedKind("loans", "Loans", [new("book", "Title"), new("title", "Book"), new("due", "Title"), new("n", "n")]);

        // Every name here is another field's own name (ignoring case) — written out, it would read back into that field — so each field keeps its own.
        Assert.Equal(["book", "title", "due", "n"], PersonNames.Headers(["book", "title", "due", "n"], kind));
        var twice = new RememberedKind("loans", "Loans", [new("a", "Name"), new("b", "name")]);
        Assert.Equal(["Name", "b"], PersonNames.Headers(["a", "b"], twice));   // two fields named alike: the second keeps its own
        Assert.Equal(["book", "due"], PersonNames.Headers(["book", "due"], null));
    }
}
