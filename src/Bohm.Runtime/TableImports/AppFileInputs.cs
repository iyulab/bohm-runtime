using System.Text.RegularExpressions;

namespace Bohm.Runtime.TableImports;

/// <summary>
/// The kinds of file an application takes in itself, read from the file inputs on its page — an
/// application that reads a bank's spreadsheet itself knows its own data (which rows it already has,
/// how it sorts them); putting the rows straight into its stored data would pass all of that by.
/// </summary>
/// <remarks>
/// Read from the page as written, like <see cref="Adoption.PageCompatibility"/>: inputs a script makes
/// later are not seen, and text inside a <c>&lt;script&gt;</c> or written out as text is not an input.
/// An <c>accept</c> list keeps its file extensions; the media types of tables count as theirs
/// (<c>text/csv</c> → <c>.csv</c>), other media types (<c>image/*</c>) are left out.
/// </remarks>
public static partial class AppFileInputs
{
    /// <summary>A file input with no <c>accept</c> — it takes any file.</summary>
    public const string AnyFile = "*";

    private static readonly Dictionary<string, string> TableMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text/csv"] = ".csv",
        ["text/tab-separated-values"] = ".tsv",
        ["application/vnd.ms-excel"] = ".xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
    };

    /// <summary>The extensions the page's file inputs take, lowercase and sorted — <see cref="AnyFile"/> when one takes any; empty when the page has none.</summary>
    public static IReadOnlyList<string> Accepts(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var page = Script().Replace(html, "");
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match input in Input().Matches(page))
        {
            var attributes = input.Groups["attributes"].Value;
            if (!FileType().IsMatch(attributes)) continue;
            var accept = Accept().Match(attributes);
            if (!accept.Success)
            {
                kinds.Add(AnyFile);
                continue;
            }

            foreach (var item in accept.Groups["value"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (item.StartsWith('.')) kinds.Add(item.ToLowerInvariant());
                else if (TableMediaTypes.TryGetValue(item, out var extension)) kinds.Add(extension);
            }
        }

        return [.. kinds];
    }

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Script();

    [GeneratedRegex(@"<input\b(?<attributes>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex Input();

    [GeneratedRegex(@"\btype\s*=\s*(?:""file""|'file'|file\b)", RegexOptions.IgnoreCase)]
    private static partial Regex FileType();

    [GeneratedRegex(@"\baccept\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex Accept();
}
