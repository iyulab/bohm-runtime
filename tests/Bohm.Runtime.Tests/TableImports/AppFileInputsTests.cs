using Bohm.Runtime.TableImports;

namespace Bohm.Runtime.Tests.TableImports;

/// <summary>
/// The kinds of file an application takes in itself, read from its page's file inputs — so a file
/// dropped on the application can go to the application's own import, which knows its data.
/// </summary>
public sealed class AppFileInputsTests
{
    [Fact]
    public void Extensions_in_accept_are_read_lowercase_without_repeats()
    {
        const string html = """
            <input type="file" id="fileInput" accept=".csv,.XLSX,.xls, .txt" multiple hidden>
            <input type='file' accept='.csv'>
            """;
        Assert.Equal([".csv", ".txt", ".xls", ".xlsx"], AppFileInputs.Accepts(html));
    }

    [Fact]
    public void Media_types_for_tables_count_as_their_extensions()
    {
        const string html = """<input accept="text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,image/*" type="file">""";
        Assert.Equal([".csv", ".xlsx"], AppFileInputs.Accepts(html));
    }

    [Fact]
    public void A_file_input_with_no_accept_takes_any_file()
    {
        Assert.Equal([AppFileInputs.AnyFile], AppFileInputs.Accepts("""<label>Backup <input type="file"></label>"""));
    }

    [Theory]
    [InlineData("""<input type="text" accept=".csv">""")] // not a file input
    [InlineData("""<p>Use &lt;input type="file" accept=".csv"&gt; to …</p>""")] // written about, not there
    [InlineData("""<script>const s = '<input type="file" accept=".csv">';</script>""")] // a string in a script
    [InlineData("")]
    public void Nothing_else_counts(string html)
    {
        Assert.Empty(AppFileInputs.Accepts(html));
    }
}
