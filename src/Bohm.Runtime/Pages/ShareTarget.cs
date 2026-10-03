using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bohm.Runtime.Pages;

/// <summary>
/// That an application receives shared pages, as its web app manifest declares it with
/// <c>share_target</c>: the names of the query parameters it reads the title, the text and the address
/// from. A parameter the manifest does not name is <see langword="null"/>.
/// </summary>
/// <remarks>
/// Only the GET form is a share target here: the application is one page served at <c>/</c>, so the
/// shared values reach it in the query of that page, whatever <c>action</c> says. A target declared with
/// <c>method: "POST"</c> expects a form submission no page here can receive, and is not one.
/// </remarks>
public sealed partial record ShareTarget(string? Title, string? Text, string? Url)
{
    /// <summary>
    /// The share target the application <paramref name="html"/> declares in its manifest, or
    /// <see langword="null"/>. The manifest is read from a <c>&lt;link rel="manifest"&gt;</c> whose
    /// <c>href</c> is a <c>data:</c> address — a single-file application carries its manifest inside —
    /// in plain, percent-encoded or base64 form. A manifest elsewhere, or one that cannot be read, declares nothing.
    /// </summary>
    public static ShareTarget? Find(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        foreach (Match tag in LinkTag().Matches(html))
        {
            var attributes = Attributes(tag.Value);
            if (!attributes.TryGetValue("rel", out var rel) || !rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("manifest", StringComparer.OrdinalIgnoreCase))
                continue;
            return attributes.TryGetValue("href", out var href) && ManifestOf(href) is { } manifest ? FromManifest(manifest) : null;
        }

        return null;
    }

    private static ShareTarget? FromManifest(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("share_target", out var target) || target.ValueKind != JsonValueKind.Object)
                return null;
            if (target.TryGetProperty("method", out var method) && !(method.ValueKind == JsonValueKind.String && string.Equals(method.GetString(), "GET", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (!target.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object) return new ShareTarget(null, null, null);
            return new ShareTarget(Name(parameters, "title"), Name(parameters, "text"), Name(parameters, "url"));
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Name(JsonElement parameters, string field) =>
            parameters.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } name ? name : null;
    }

    /// <summary>The manifest text a <c>data:</c> address carries, or <see langword="null"/> for any other address.</summary>
    private static string? ManifestOf(string href)
    {
        href = WebUtility.HtmlDecode(href).Trim();
        if (!href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        var comma = href.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0) return null;
        var header = href[5..comma];
        var body = href[(comma + 1)..];
        try
        {
            if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                return Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(body)));
            return Uri.UnescapeDataString(body);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Attributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in Attribute().Matches(tag))
            attributes.TryAdd(attribute.Groups["name"].Value, attribute.Groups["double"].Success ? attribute.Groups["double"].Value
                : attribute.Groups["single"].Success ? attribute.Groups["single"].Value : attribute.Groups["bare"].Value);
        return attributes;
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkTag();

    [GeneratedRegex("""\s(?<name>[a-zA-Z-]+)\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<bare>[^\s"'>]+))""", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();
}
