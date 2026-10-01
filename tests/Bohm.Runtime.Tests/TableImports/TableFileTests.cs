using System.Text;
using Bohm.Runtime.TableImports;

namespace Bohm.Runtime.Tests.TableImports;

/// <summary>A CSV or TSV file read the same way every time — quotes, line breaks in cells, a byte-order mark, the usual delimiters.</summary>
public sealed class TableFileTests
{
    private static TableFile Read(string text, string? name = null) => TableFile.Read(Encoding.UTF8.GetBytes(text), name);

    [Fact]
    public void Quoted_cells_keep_delimiters_line_breaks_and_doubled_quotes()
    {
        var file = Read("제목,메모\r\n\"자바, 입문\",\"첫 줄\n둘째 줄\"\r\n\"\"\"인용\"\"\",끝\r\n");

        Assert.Equal(["제목", "메모"], file.Headers);
        Assert.Equal(2, file.Rows.Count);
        Assert.Equal(["자바, 입문", "첫 줄\n둘째 줄"], file.Rows[0]);
        Assert.Equal(["\"인용\"", "끝"], file.Rows[1]);
    }

    [Fact]
    public void A_byte_order_mark_is_skipped_and_blank_lines_and_a_missing_last_line_break_do_not_matter()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("isbn,title\n\n978-1,A\n978-2,B")).ToArray();

        var file = TableFile.Read(bytes);

        Assert.Equal(["isbn", "title"], file.Headers);
        Assert.Equal([["978-1", "A"], ["978-2", "B"]], file.Rows.Select(r => r.ToArray()));
    }

    [Theory]
    [InlineData("a\tb\n1\t2")]
    [InlineData("a;b\n1;2")]
    [InlineData("a,b\n1,2")]
    public void Without_a_tsv_name_the_delimiter_is_the_commonest_one_in_the_header(string text)
    {
        var file = Read(text);

        Assert.Equal(["a", "b"], file.Headers);
        Assert.Equal(["1", "2"], file.Rows[0]);
    }

    [Fact]
    public void A_tsv_file_is_split_by_tabs_whatever_its_header_looks_like()
    {
        var file = Read("a,b\tc\n1,2\t3", "list.tsv");

        Assert.Equal(["a,b", "c"], file.Headers);
    }

    [Fact]
    public void A_short_row_is_filled_and_a_long_rows_extra_cells_belong_to_no_column()
    {
        var file = Read("a,b,c\n1\n1,2,3,4");

        Assert.Equal(["1", "", ""], file.Rows[0]);
        Assert.Equal(["1", "2", "3"], file.Rows[1]);
    }

    [Fact]
    public void A_file_in_a_legacy_code_page_is_refused_as_not_utf8()
    {
        // A Korean word in CP949 (EUC-KR): not valid UTF-8.
        byte[] cp949 = [0xC1, 0xA6, 0xB8, 0xF1, 0x0A, 0x31];

        Assert.Equal(TableFileProblem.NotUtf8, Assert.Throws<TableFileException>(() => TableFile.Read(cp949)).Problem);
    }

    [Theory]
    [InlineData("", TableFileProblem.Empty)]
    [InlineData("\n\n", TableFileProblem.Empty)]
    [InlineData("a,b\n\"never closed,1", TableFileProblem.UnclosedQuote)]
    [InlineData("name,name\n1,2", TableFileProblem.DuplicateHeader)]
    public void A_file_that_is_not_a_table_is_refused_with_why(string text, string problem) =>
        Assert.Equal(problem, Assert.Throws<TableFileException>(() => Read(text)).Problem);

    [Fact]
    public void Too_many_rows_are_refused_not_cut()
    {
        var text = "n\n" + string.Join("\n", Enumerable.Range(0, TableFile.MaxRows + 1));

        Assert.Equal(TableFileProblem.TooManyRows, Assert.Throws<TableFileException>(() => Read(text)).Problem);
    }
}
