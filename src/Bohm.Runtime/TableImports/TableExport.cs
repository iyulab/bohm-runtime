using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bohm.Runtime.TableImports;

/// <summary>
/// An application's records as table files a spreadsheet opens — the way out that matches the way in
/// (<see cref="TableImport"/>): each collection (a key whose value is a JSON array of objects —
/// <see cref="TableImport.Collections"/>) becomes one CSV, a column per field in the order the records
/// first show them. Nothing about the application changes; its data is only read.
/// </summary>
public static class TableExport
{
    /// <summary>The byte order mark a CSV starts with, so a spreadsheet reads it as UTF-8 rather than this computer's code page.</summary>
    private static readonly byte[] Utf8Mark = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// The CSV of one collection: a header of its fields, then a row per record — text as it is, numbers and
    /// true/false as written, nothing for a field a record lacks or holds null in, and a nested value as its
    /// JSON. UTF-8 with a byte order mark, lines ended by CR LF (RFC 4180).
    /// </summary>
    /// <remarks>
    /// Text that a spreadsheet would read as a formula (it starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab
    /// or a carriage return) gets a leading apostrophe: the data was written by an application nobody reviewed,
    /// and a cell must open as what it says, not run.
    /// </remarks>
    public static byte[] Csv(CollectionCandidate collection, string stored)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var fields = collection.Declaration.Fields.Select(f => f.Name).ToList();
        var text = new StringBuilder();
        AppendRow(text, fields);
        foreach (var record in TableImport.Records(stored) ?? [])
            AppendRow(text, fields.Select(f => Cell(record[f])));
        return [.. Utf8Mark, .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>A file name for a collection's table: the key, with what a file name cannot hold replaced.</summary>
    public static string FileName(string collection)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. collection.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().TrimEnd('.');
        return (name.Length == 0 ? "table" : name) + ".csv";
    }

    private static string Cell(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.GetValueKind() == JsonValueKind.String => Inert(value.GetValue<string>()),
        JsonValue value => value.ToJsonString(),
        _ => node.ToJsonString(),
    };

    private static string Inert(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    private static void AppendRow(StringBuilder text, IEnumerable<string> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) text.Append(',');
            first = false;
            if (cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 || (cell.Length > 0 && (char.IsWhiteSpace(cell[0]) || char.IsWhiteSpace(cell[^1]))))
                text.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            else
                text.Append(cell);
        }

        text.Append("\r\n");
    }
}
