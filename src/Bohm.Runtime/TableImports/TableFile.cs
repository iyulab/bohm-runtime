using System.Text;

namespace Bohm.Runtime.TableImports;

/// <summary>Why a table file could not be read.</summary>
public static class TableFileProblem
{
    /// <summary>The file is not UTF-8 text (a spreadsheet saved in a legacy code page, or not text at all).</summary>
    public const string NotUtf8 = "not-utf8";

    /// <summary>The file has no header row.</summary>
    public const string Empty = "empty";

    /// <summary>A quoted cell is never closed.</summary>
    public const string UnclosedQuote = "unclosed-quote";

    /// <summary>The file is larger than <see cref="TableFile.MaxBytes"/>.</summary>
    public const string TooLarge = "too-large";

    /// <summary>The file has more rows than <see cref="TableFile.MaxRows"/>.</summary>
    public const string TooManyRows = "too-many-rows";

    /// <summary>Two columns share a header, so a cell could not say which one it belongs to.</summary>
    public const string DuplicateHeader = "duplicate-header";
}

/// <summary>A table file that could not be read, and why (<see cref="TableFileProblem"/>).</summary>
public sealed class TableFileException(string problem, string message) : Exception(message)
{
    public string Problem { get; } = problem;
}

/// <summary>
/// A delimited table file — comma-separated (CSV) or tab-separated (TSV) UTF-8 text whose first row
/// names the columns. Read the same way every time: the same bytes give the same table.
/// </summary>
/// <remarks>
/// Cells follow RFC 4180: a cell in double quotes may hold the delimiter, line breaks and doubled
/// quotes. A byte-order mark is skipped, line breaks may be CRLF or LF, blank lines are skipped, and a
/// row shorter than the header is filled with empty cells. Cells are kept as written — no trimming,
/// no number or date reading; what a cell means is decided by the column it goes into.
/// </remarks>
public sealed record TableFile(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>The largest file read.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>The most rows read, the header not counted.</summary>
    public const int MaxRows = 10_000;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads <paramref name="bytes"/>; the delimiter is a tab when <paramref name="fileName"/> ends in <c>.tsv</c>, otherwise found from the header row.</summary>
    /// <exception cref="TableFileException">The file cannot be read as a table.</exception>
    public static TableFile Read(ReadOnlySpan<byte> bytes, string? fileName = null)
    {
        if (bytes.Length > MaxBytes) throw new TableFileException(TableFileProblem.TooLarge, $"The file is larger than {MaxBytes / (1024 * 1024)} MB.");
        ReadOnlySpan<byte> byteOrderMark = [0xEF, 0xBB, 0xBF];
        if (bytes.StartsWith(byteOrderMark)) bytes = bytes[3..];

        string text;
        try
        {
            text = Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new TableFileException(TableFileProblem.NotUtf8, "The file is not UTF-8 text.");
        }

        var delimiter = fileName?.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) == true ? '\t' : DelimiterOf(text);
        var rows = Parse(text, delimiter);
        if (rows.Count == 0) throw new TableFileException(TableFileProblem.Empty, "The file has no header row.");

        var headers = rows[0];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            if (header.Length > 0 && !seen.Add(header))
                throw new TableFileException(TableFileProblem.DuplicateHeader, $"Two columns are named \"{header}\".");
        }

        if (rows.Count - 1 > MaxRows) throw new TableFileException(TableFileProblem.TooManyRows, $"The file has more than {MaxRows} rows.");

        var body = new List<IReadOnlyList<string>>(rows.Count - 1);
        foreach (var row in rows.Skip(1))
        {
            // A row longer than the header keeps its extra cells out: they belong to no column.
            var cells = new string[headers.Count];
            for (var i = 0; i < cells.Length; i++) cells[i] = i < row.Count ? row[i] : "";
            body.Add(cells);
        }

        return new TableFile(headers, body);
    }

    /// <summary>
    /// The delimiter of a file named neither way: whichever of tab, semicolon and comma occurs most
    /// often outside quotes in the header row — comma when none does (a table of one column).
    /// </summary>
    private static char DelimiterOf(string text)
    {
        int tabs = 0, semicolons = 0, commas = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is '\n' or '\r') break;
            else if (!quoted && c == '\t') tabs++;
            else if (!quoted && c == ';') semicolons++;
            else if (!quoted && c == ',') commas++;
        }

        return tabs > commas && tabs >= semicolons ? '\t' : semicolons > commas ? ';' : ',';
    }

    private static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var cellStarted = false;

        void EndCell()
        {
            row.Add(cell.ToString());
            cell.Clear();
            cellStarted = false;
        }

        void EndRow()
        {
            EndCell();
            // A blank line is no row.
            if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
            row = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
                continue;
            }

            if (c == '"' && !cellStarted) { quoted = true; cellStarted = true; }
            else if (c == delimiter) EndCell();
            else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
            else if (c == '\n') EndRow();
            else { cell.Append(c); cellStarted = true; }
        }

        if (quoted) throw new TableFileException(TableFileProblem.UnclosedQuote, "A quoted cell is never closed.");
        if (cellStarted || cell.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }
}
