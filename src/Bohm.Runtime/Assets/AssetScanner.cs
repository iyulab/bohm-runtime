using System.Text.RegularExpressions;

namespace Bohm.Runtime.Assets;

/// <summary>What kind of code a referenced file is, which decides how it is scanned in turn.</summary>
public enum AssetKind
{
    /// <summary>A classic script (<c>&lt;script src&gt;</c>). Not scanned further: its dependencies are loaded by running it.</summary>
    Script,

    /// <summary>An ECMAScript module. Its static imports are followed.</summary>
    Module,

    /// <summary>A style sheet. Its <c>@import</c>s and fonts are followed.</summary>
    Style,

    /// <summary>A font.</summary>
    Font,
}

/// <summary>A file a document or another file refers to, resolved to an absolute URL.</summary>
public sealed record AssetReference(Uri Url, AssetKind Kind);

/// <summary>
/// Finds the code an application loads from other hosts by reading it — scripts, modules, style
/// sheets and fonts written literally into the document or into those files. It does not run
/// anything, so a URL assembled at run time is not found; that is the price of never executing
/// untrusted code to learn what it needs.
/// </summary>
public static partial class AssetScanner
{
    /// <summary>References in an HTML document, resolved against <paramref name="baseUrl"/> when relative.</summary>
    public static IReadOnlyList<AssetReference> ScanHtml(string html, Uri? baseUrl = null)
    {
        var found = new List<AssetReference>();
        foreach (Match script in ScriptTag().Matches(html))
        {
            var attributes = script.Groups["attrs"].Value;
            var isModule = TypeModule().IsMatch(attributes);
            if (Attribute(attributes, "src") is { } src)
                Add(found, src, baseUrl, isModule ? AssetKind.Module : AssetKind.Script);
            else if (isModule)
                foreach (var specifier in ModuleSpecifiers(script.Groups["body"].Value))
                    Add(found, specifier, baseUrl, AssetKind.Module);
        }

        foreach (Match link in LinkTag().Matches(html))
        {
            var attributes = link.Groups["attrs"].Value;
            var rel = (Attribute(attributes, "rel") ?? "").ToLowerInvariant();
            if (Attribute(attributes, "href") is not { } href) continue;
            if (rel.Contains("stylesheet", StringComparison.Ordinal)) Add(found, href, baseUrl, AssetKind.Style);
            else if (rel.Contains("modulepreload", StringComparison.Ordinal)) Add(found, href, baseUrl, AssetKind.Module);
            else if (rel.Contains("preload", StringComparison.Ordinal) && (Attribute(attributes, "as") ?? "") is var kind)
            {
                if (kind == "script") Add(found, href, baseUrl, AssetKind.Script);
                else if (kind == "style") Add(found, href, baseUrl, AssetKind.Style);
                else if (kind == "font") Add(found, href, baseUrl, AssetKind.Font);
            }
        }

        foreach (Match style in StyleTag().Matches(html))
            found.AddRange(ScanCss(style.Groups["body"].Value, baseUrl));

        return Distinct(found);
    }

    /// <summary>Static imports and re-exports of a module, resolved against the module's own URL.</summary>
    public static IReadOnlyList<AssetReference> ScanModule(string source, Uri moduleUrl)
    {
        var found = new List<AssetReference>();
        foreach (var specifier in ModuleSpecifiers(source))
            Add(found, specifier, moduleUrl, AssetKind.Module);
        return Distinct(found);
    }

    /// <summary><c>@import</c>s and fonts of a style sheet, resolved against the sheet's URL.</summary>
    public static IReadOnlyList<AssetReference> ScanCss(string css, Uri? sheetUrl)
    {
        var found = new List<AssetReference>();
        foreach (Match import in CssImport().Matches(css))
            Add(found, import.Groups["url"].Value, sheetUrl, AssetKind.Style);
        foreach (Match url in CssUrl().Matches(css))
        {
            var value = url.Groups["url"].Value;
            if (FontFile().IsMatch(value)) Add(found, value, sheetUrl, AssetKind.Font);
        }

        return Distinct(found);
    }

    private static IEnumerable<string> ModuleSpecifiers(string source)
    {
        foreach (Match match in StaticImport().Matches(source)) yield return match.Groups["spec"].Value;
        foreach (Match match in DynamicImport().Matches(source)) yield return match.Groups["spec"].Value;
    }

    private static void Add(List<AssetReference> found, string reference, Uri? baseUrl, AssetKind kind)
    {
        reference = System.Net.WebUtility.HtmlDecode(reference.Trim());
        if (reference.Length == 0 || reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        Uri? url;
        if (Uri.TryCreate(reference, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") url = absolute;
        else if (baseUrl is not null && !reference.Contains(':', StringComparison.Ordinal) && Uri.TryCreate(baseUrl, reference, out var relative)) url = relative;
        else return; // A bare specifier ("react") or a relative path with no remote base: nothing to fetch.

        if (url.Scheme is not ("http" or "https")) return;
        found.Add(new AssetReference(new UriBuilder(url) { Fragment = "" }.Uri, kind));
    }

    private static string? Attribute(string attributes, string name)
    {
        var match = Regex.Match(attributes, $"""(?:^|\s){name}\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s>]+))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static List<AssetReference> Distinct(List<AssetReference> found) =>
        found.GroupBy(r => r.Url.AbsoluteUri).Select(g => g.First()).ToList();

    [GeneratedRegex("""<script\b(?<attrs>[^>]*)>(?<body>.*?)</script\s*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptTag();

    [GeneratedRegex("""\btype\s*=\s*["']?module\b""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeModule();

    [GeneratedRegex("""<link\b(?<attrs>[^>]*)>""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkTag();

    [GeneratedRegex("""<style\b[^>]*>(?<body>.*?)</style\s*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex StyleTag();

    // import x from "…" · import "…" · export … from "…" — including minified forms without spaces.
    [GeneratedRegex("""(?:\bimport|\bexport)\b[^;"'`()]*?\bfrom\s*["'](?<spec>[^"'\s]+)["']|\bimport\s*["'](?<spec>[^"'\s]+)["']""", RegexOptions.CultureInvariant)]
    private static partial Regex StaticImport();

    [GeneratedRegex("""\bimport\s*\(\s*["'](?<spec>[^"'\s]+)["']\s*\)""", RegexOptions.CultureInvariant)]
    private static partial Regex DynamicImport();

    [GeneratedRegex("""@import\s+(?:url\(\s*)?["']?(?<url>[^"')\s;]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssImport();

    [GeneratedRegex("""url\(\s*["']?(?<url>[^"')]+?)["']?\s*\)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssUrl();

    [GeneratedRegex("""\.(?:woff2?|ttf|otf|eot)(?:[?#]|$)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FontFile();
}
