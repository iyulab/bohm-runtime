using Bohm.Runtime.Host.Edit;

namespace Bohm.Runtime.Tests.Host;

/// <summary>Replacing a piece of an application's source the way the model copied it — including the indentation it got wrong.</summary>
public sealed class SourcePieceTests
{
    [Fact]
    public void An_exact_piece_there_once_is_replaced_as_written()
    {
        var done = SourcePiece.Replace("<p>a</p>\n<b>b</b>", "<b>b</b>", "<b>c</b>");

        Assert.Equal(("<p>a</p>\n<b>c</b>", "<b>b</b>", "<b>c</b>", "replaced"), (done.Source, done.Old, done.New, done.Message));
    }

    [Fact]
    public void A_piece_whose_lines_are_indented_differently_is_found_and_the_new_text_takes_the_sources_indent()
    {
        // The model copied header cells with 8 spaces where the source has 6.
        const string source = "<tr>\n      <th>품목</th>\n      <th>분류</th>\n</tr>";

        var done = SourcePiece.Replace(source, "        <th>품목</th>\n        <th>분류</th>", "        <th>품목</th>\n        <th>분류</th>\n        <th>공급처</th>");

        Assert.Equal("<tr>\n      <th>품목</th>\n      <th>분류</th>\n      <th>공급처</th>\n</tr>", done.Source);
        Assert.Equal("      <th>품목</th>\n      <th>분류</th>", done.Old); // what the source held, not the model's copy
        Assert.StartsWith("replaced", done.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_copied_with_an_indent_the_source_line_does_not_have_is_found()
    {
        const string source = "<form>\n<datalist id=\"locationList\"></datalist>\n</form>";

        var done = SourcePiece.Replace(source, "  <datalist id=\"locationList\"></datalist>", "  <datalist id=\"locationList\"></datalist>\n  <datalist id=\"supplierList\"></datalist>");

        Assert.Equal("<form>\n<datalist id=\"locationList\"></datalist>\n<datalist id=\"supplierList\"></datalist>\n</form>", done.Source);
    }

    [Fact]
    public void Lines_off_by_different_indents_are_still_found_and_the_new_text_goes_in_as_written()
    {
        const string source = "function f() {\n        if (s) {\n          const hay = [it.name];\n        }\n}";

        var done = SourcePiece.Replace(source, "        if (s) {\n            const hay = [it.name];", "        if (s) {\n          const hay = [it.name, it.supplier];");

        Assert.Equal("function f() {\n        if (s) {\n          const hay = [it.name, it.supplier];\n        }\n}", done.Source);
    }

    [Fact]
    public void A_piece_may_start_and_end_inside_lines_when_matched_loosely()
    {
        const string source = "const x = call(\n    a,\n    b) + 1;";

        var done = SourcePiece.Replace(source, "call(\n  a,\n  b)", "call(\n  a,\n  c)");

        Assert.Equal("const x = call(\n    a,\n    c) + 1;", done.Source);
        Assert.Equal("call(\n    a,\n    b)", done.Old);
    }

    [Fact]
    public void A_piece_ending_with_its_line_break_keeps_the_next_lines_indent()
    {
        const string source = "<ul>\n    <li>one</li>\n    <li>two</li>\n</ul>";

        var done = SourcePiece.Replace(source, "  <li>one</li>\n", "  <li>하나</li>\n");

        Assert.Equal("<ul>\n    <li>하나</li>\n    <li>two</li>\n</ul>", done.Source);
    }

    [Fact]
    public void Two_places_that_match_loosely_are_refused_as_more_than_once()
    {
        const string source = "<td>\n  <b>x</b>\n</td>\n<td>\n    <b>x</b>\n</td>";

        var done = SourcePiece.Replace(source, "<td>\n   <b>x</b>", "<td>\n   <b>y</b>");

        Assert.Null(done.Source);
        Assert.Contains("more than once", done.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_piece_not_there_even_loosely_is_refused_and_the_model_is_told_where_its_first_line_is()
    {
        const string source = "<table>\n  <tr>\n    <th>품목</th>\n    <th>수량</th>\n  </tr>\n</table>";

        var done = SourcePiece.Replace(source, "<th>품목</th>\n<th>분류</th>", "x");

        Assert.Null(done.Source);
        Assert.Contains("was not found", done.Message, StringComparison.Ordinal);
        Assert.Contains("line 3", done.Message, StringComparison.Ordinal);
    }

    private const string Expenses = "<script>\n  var all = totalsFor(\"\");\n  var trA = makeRow([\"전체\", fmt(all.food), fmt(all.total]), \"all\");\n  els.monthBody.appendChild(trA);\n</script>";

    [Theory]
    [InlineData("var trA = makeRow([\"전체\", fmt(all.food), fmt(all.total)), \"all\");")]   // the whole line, its bracket «paired»
    [InlineData("fmt(all.total)), \"all\");")]                                                // a stretch of it
    public void A_piece_copied_nearly_is_refused_with_the_nearest_line_as_it_is_and_where_they_differ(string piece)
    {
        var done = SourcePiece.Replace(Expenses, piece, "x");

        Assert.Null(done.Source);
        Assert.Contains("The nearest line is line 3", done.Message, StringComparison.Ordinal);
        Assert.Contains("the source has «ll.total]), \"all\"» and old_text has «ll.total)), \"all\"»", done.Message, StringComparison.Ordinal);
        Assert.EndsWith("\n3:   var trA = makeRow([\"전체\", fmt(all.food), fmt(all.total]), \"all\");", done.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_piece_equally_near_two_lines_names_neither()
    {
        const string source = "<p>first total line A</p>\n<p>first total line B</p>";

        var done = SourcePiece.Replace(source, "<p>first total line C</p>", "x");

        Assert.Null(done.Source);
        Assert.DoesNotContain("nearest line", done.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<i>nope</i>")]
    public void An_empty_or_absent_one_line_piece_without_indent_is_refused(string piece)
    {
        var done = SourcePiece.Replace("<p>a</p>", piece, "x");

        Assert.Null(done.Source);
        Assert.Contains(piece.Length == 0 ? "empty" : "was not found", done.Message, StringComparison.Ordinal);
    }
}
