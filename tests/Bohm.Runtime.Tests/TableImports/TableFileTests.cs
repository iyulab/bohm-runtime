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
    public void A_file_in_a_legacy_code_page_is_refused_as_not_utf8_when_no_legacy_encoding_is_given()
    {
        // A Korean word in CP949 (EUC-KR): not valid UTF-8.
        byte[] cp949 = [0xC1, 0xA6, 0xB8, 0xF1, 0x0A, 0x31];

        Assert.Equal(TableFileProblem.NotUtf8, Assert.Throws<TableFileException>(() => TableFile.Read(cp949)).Problem);
    }

    [Fact]
    public void A_file_that_is_not_utf8_is_read_in_the_legacy_code_page_given()
    {
        // «거래일시,적요» / «2026.09.01,스타벅스» as a Korean bank's CSV comes — CP949, with no byte-order mark.
        var legacy = TableFile.StrictLegacy(949)!;
        var bytes = legacy.GetBytes("거래일시,적요\r\n2026.09.01,스타벅스\r\n");

        var table = TableFile.Read(bytes, "거래내역.csv", legacy);

        Assert.Equal(["거래일시", "적요"], table.Headers);
        Assert.Equal(["2026.09.01", "스타벅스"], table.Rows[0]);
    }

    [Fact]
    public void A_utf8_file_is_read_as_utf8_whatever_the_legacy_code_page()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("이름\n봄\n");
        Assert.Equal(["이름"], TableFile.Read(bytes, null, TableFile.StrictLegacy(949)).Headers);
    }

    [Fact]
    public void Bytes_the_legacy_code_page_cannot_read_either_are_refused_as_not_utf8()
    {
        // A lead byte followed by a space is no character in CP949 (and not UTF-8): nothing is guessed.
        byte[] neither = [0x61, 0x0A, 0x81, 0x20];
        Assert.Equal(TableFileProblem.NotUtf8, Assert.Throws<TableFileException>(() => TableFile.Read(neither, null, TableFile.StrictLegacy(949))).Problem);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65001)] // UTF-8 as the system code page: there is no legacy one
    [InlineData(-1)]
    public void A_code_page_that_is_no_legacy_one_gives_none(int codePage)
    {
        Assert.Null(TableFile.StrictLegacy(codePage));
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
