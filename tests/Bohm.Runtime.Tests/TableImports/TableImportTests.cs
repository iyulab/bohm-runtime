using System.Text;
using System.Text.Json.Nodes;
using Bohm.Runtime.TableImports;

namespace Bohm.Runtime.Tests.TableImports;

/// <summary>
/// Rows of a table file put into an application's records by its declaration — matched by name, read by
/// the field's kind, the same record left out or written over, nothing lost silently: a column that goes
/// nowhere and a row that cannot be read are both in the plan.
/// </summary>
public sealed class TableImportTests
{
    private const string Books = """[{"id":1,"title":"자바 입문","isbn":"978-1","price":12000,"read":false},{"id":2,"title":"C# 깊이","isbn":"978-2","price":30000,"read":true}]""";

    private static TableFile File(string text) => TableFile.Read(Encoding.UTF8.GetBytes(text));

    private static ImportDeclaration Declaration(string? identity = "isbn") =>
        TableImport.Collections([new("books", Books)]).Single().Declaration with { Identity = identity };

    [Fact]
    public void A_key_holding_a_list_of_records_is_a_collection_with_its_fields_and_their_kinds()
    {
        var stored = new Dictionary<string, string>
        {
            ["books"] = Books,
            ["theme"] = "\"dark\"",
            ["empty"] = "[]",
            ["tags"] = """["a","b"]""",
            ["broken"] = "{not json",
        };

        var found = Assert.Single(TableImport.Collections(stored));

        Assert.Equal("books", found.Declaration.Collection);
        Assert.Equal(2, found.Records);
        Assert.Equal([("id", FieldKind.Number), ("title", FieldKind.Text), ("isbn", FieldKind.Text), ("price", FieldKind.Number), ("read", FieldKind.Boolean)],
            found.Declaration.Fields.Select(f => (f.Name, f.Kind)));
    }

    [Fact]
    public void Columns_match_fields_by_name_ignoring_case_and_spacing_and_a_column_that_goes_nowhere_is_shown_as_left_out()
    {
        var file = File("ISBN,Title,Publisher,price\n978-3,파이썬,한빛,\"25,000\"");

        var plan = TableImport.Plan(Declaration(), file, Books);

        Assert.Equal([("ISBN", "isbn"), ("Title", "title"), ("Publisher", (string?)null), ("price", "price")], plan.Columns.Select(c => (c.Column, c.Field)));
        Assert.Equal(["id", "read"], plan.Unfilled);
        Assert.Equal(1, plan.Added);
        var record = Assert.Single(plan.Sample);
        Assert.Equal(25000, record["price"]!.GetValue<long>()); // a grouped number is a number
        Assert.Equal(3, record["id"]!.GetValue<double>()); // the application's own key, next after the largest
    }

    [Fact]
    public void A_row_whose_identity_is_already_there_is_left_out_or_written_over_as_chosen()
    {
        var file = File("isbn,title,price\n978-2,C# 더 깊이,32000\n978-9,새 책,1000");

        var skip = TableImport.Plan(Declaration(), file, Books);
        Assert.Equal((1, 0, 1), (skip.Added, skip.Replaced, skip.Skipped));

        var replaced = JsonNode.Parse(TableImport.Apply(Declaration(), file, Books, SameRecord.Replace))!.AsArray();
        Assert.Equal(3, replaced.Count);
        Assert.Equal("C# 더 깊이", replaced[1]!["title"]!.GetValue<string>());
        Assert.True(replaced[1]!["read"]!.GetValue<bool>()); // a field the file has no column for is kept
        Assert.Equal(2, replaced[1]!["id"]!.GetValue<int>());
    }

    [Fact]
    public void A_cell_that_is_not_its_fields_kind_leaves_its_row_out_with_the_row_number_and_why()
    {
        var file = File("isbn,price,read\n978-5,비쌈,yes\n978-6,100,아마");

        var plan = TableImport.Plan(Declaration(), file, Books);

        Assert.Equal(0, plan.Added);
        Assert.Equal([new InvalidRow(2, "price", "비쌈"), new InvalidRow(3, "read", "아마")], plan.Invalid);
    }

    [Fact]
    public void The_same_file_into_the_same_data_gives_the_same_result_and_a_second_import_adds_nothing()
    {
        var file = File("isbn,title\n978-7,가\n978-8,나");

        var once = TableImport.Apply(Declaration(), file, Books);

        Assert.Equal(once, TableImport.Apply(Declaration(), file, Books));
        Assert.Equal(once, TableImport.Apply(Declaration(), file, once));
    }

    [Fact]
    public void Without_an_identity_every_row_is_added_and_the_records_already_there_stay_first_in_their_order()
    {
        var file = File("title\n하나\n둘");

        var result = JsonNode.Parse(TableImport.Apply(Declaration(identity: null), file, Books))!.AsArray();

        Assert.Equal(["자바 입문", "C# 깊이", "하나", "둘"], result.Select(r => r!["title"]!.GetValue<string>()));
        Assert.Equal([1, 2, 3, 4], result.Select(r => (int)r!["id"]!.GetValue<double>()));
    }

    [Fact]
    public void A_column_the_person_moved_or_left_out_overrides_the_match_by_name()
    {
        var file = File("서명,isbn\n코틀린,978-4");
        var chosen = new Dictionary<string, string?> { ["서명"] = "title", ["isbn"] = null };

        var plan = TableImport.Plan(Declaration(identity: null), file, Books, columns: chosen);

        Assert.Equal([("서명", "title"), ("isbn", (string?)null)], plan.Columns.Select(c => (c.Column, c.Field)));
        Assert.Null(Assert.Single(plan.Sample)["isbn"]);
    }

    [Fact]
    public void A_row_that_fills_no_field_is_not_a_record()
    {
        // Every column left out (headers the app does not know), and a row whose known cells are empty.
        var nothingMatches = File("제목,저자\n파이썬,홍길동");
        var emptyCells = File("title,메모\n,그냥 메모");

        Assert.Equal(0, TableImport.Plan(Declaration(identity: null), nothingMatches, Books).Added);
        Assert.Equal(0, TableImport.Plan(Declaration(identity: null), emptyCells, Books).Added);
        Assert.Equal(Books, TableImport.Apply(Declaration(identity: null), nothingMatches, Books)); // the records already there keep their bytes
    }

    [Theory]
    [InlineData("2026/10/01", "2026-10-01")]
    [InlineData("2026.1.5", "2026-01-05")]
    [InlineData("2026-10-01", "2026-10-01")]
    public void A_date_is_stored_the_way_dates_already_are(string cell, string stored)
    {
        var declaration = new ImportDeclaration("log", [new ImportField("on", FieldKind.Date)]);

        var result = JsonNode.Parse(TableImport.Apply(declaration, File($"on\n{cell}"), null))!.AsArray();

        Assert.Equal(stored, result[0]!["on"]!.GetValue<string>());
    }
}
