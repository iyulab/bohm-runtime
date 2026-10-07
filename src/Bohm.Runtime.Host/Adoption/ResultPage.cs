using System.Net;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>A page the runtime makes for the person out of an answer they were given, and where the answer came from.</summary>
/// <param name="Title">What the page is called — its title and first heading.</param>
/// <param name="Text">The answer, as the person read it.</param>
/// <param name="Sources">The pages the answer was read from, in order.</param>
/// <param name="Language">The language the page is written in (a BCP 47 tag), when known.</param>
/// <param name="SourcesHeading">The heading over the sources, in that language.</param>
internal sealed record ResultPage(string Title, string Text, IReadOnlyList<ResultSource> Sources, string? Language, string? SourcesHeading)
{
    private const int MaxTitle = 200;
    private const int MaxText = 200_000;
    private const int MaxSources = 50;

    /// <summary>
    /// Reads <c>{ title, text, sources?: [{ name, url? }], lang?, sourcesHeading? }</c>; <see langword="null"/>
    /// when the body is not that shape, the title or the text is empty, or either is too long.
    /// </summary>
    public static ResultPage? Read(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var title = String(root, "title")?.Trim();
            var text = String(root, "text");
            if (string.IsNullOrEmpty(title) || title.Length > MaxTitle || string.IsNullOrWhiteSpace(text) || text.Length > MaxText) return null;

            var sources = new List<ResultSource>();
            if (root.TryGetProperty("sources", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxSources) return null;
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || String(item, "name") is not { Length: > 0 } name) return null;
                    sources.Add(new ResultSource(name, String(item, "url")));
                }
            }

            return new ResultPage(title, text, sources, String(root, "lang"), String(root, "sourcesHeading"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The page: the answer as text — escaped, its lines kept, its own opening title left out — under the title, and the sources below.
    /// It carries no script and loads nothing: it is made from what the person already read, and it
    /// reads the same offline and years later.
    /// </summary>
    public byte[] Render()
    {
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html");
        if (Language is { Length: > 0 } lang) html.Append(" lang=\"").Append(Escape(lang)).Append('"');
        html.Append(">\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<title>").Append(Escape(Title)).Append("</title>\n");
        html.Append(Style);
        html.Append("<main>\n<h1>").Append(Escape(Title)).Append("</h1>\n");
        html.Append("<div class=\"text\">\n").Append(AnswerMarkdown.ToHtml(WithoutOwnTitle(Text))).Append("</div>\n");
        if (Sources.Count > 0)
        {
            html.Append("<footer>\n<h2>").Append(Escape(SourcesHeading is { Length: > 0 } heading ? heading : "Sources")).Append("</h2>\n<ul class=\"sources\">\n");
            foreach (var source in Sources)
            {
                html.Append("<li>").Append(Escape(source.Name));
                if (source.Url is { Length: > 0 } url) html.Append("<br><span class=\"url\">").Append(Escape(url)).Append("</span>");
                html.Append("</li>\n");
            }

            html.Append("</ul>\n</footer>\n");
        }

        html.Append("</main>\n");
        return Encoding.UTF8.GetBytes(html.ToString());
    }

    /// <summary>
    /// The text without a first-level heading it opens with: a model writing a page often heads it with its own
    /// title, which the page already shows above it — kept, the page has two titles.
    /// </summary>
    internal static string WithoutOwnTitle(string text)
    {
        var trimmed = text.Trim();
        var firstLine = trimmed.Split('\n', 2)[0].TrimEnd('\r');
        if (!firstLine.StartsWith("# ", StringComparison.Ordinal)) return trimmed;
        var rest = trimmed.Length > firstLine.Length ? trimmed[firstLine.Length..].Trim() : "";
        return rest.Length > 0 ? rest : trimmed;   // a page that is only a heading keeps it
    }

    /// <summary>
    /// A quiet, readable page: the text on a sheet, section headings set apart, the sources in a band below — in light
    /// and dark, and plain when printed. Style only: the page carries no script.
    /// </summary>
    private const string Style = """
        <style>
          :root { color-scheme: light dark; --fg: #1f2328; --muted: #59636e; --line: #d8dee4; --bg: #f3f4f6; --sheet: #fff; --soft: #f6f8fa; --accent: #8a2a52; }
          @media (prefers-color-scheme: dark) { :root { --fg: #e6edf3; --muted: #9198a1; --line: #30363d; --bg: #0d1117; --sheet: #151b23; --soft: #1c232c; --accent: #e58bb0; } }
          body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.7 system-ui, "Segoe UI", "Malgun Gothic", sans-serif; }
          main { box-sizing: border-box; max-width: 48rem; margin: 2.5rem auto; padding: 2.5rem 3rem; background: var(--sheet); border: 1px solid var(--line); border-radius: 12px; }
          h1 { font-size: 1.75rem; line-height: 1.3; letter-spacing: -0.01em; margin: 0 0 1.5rem; padding-bottom: 1rem; border-bottom: 1px solid var(--line); }
          .text { overflow-wrap: anywhere; }
          .text > :first-child { margin-top: 0; }
          .text h1, .text h2 { font-size: 1.25rem; line-height: 1.4; margin: 2.25rem 0 .75rem; }
          .text h3, .text h4 { font-size: 1.05rem; line-height: 1.4; margin: 1.75rem 0 .5rem; }
          .text > h1:first-child, .text > h2:first-child, .text > h3:first-child { margin-top: 0; }
          .text p { margin: .75rem 0; }
          .text ul, .text ol { padding-left: 1.4rem; }
          .text li { margin: .25rem 0; }
          .text li::marker { color: var(--accent); }
          .text hr { border: 0; border-top: 1px solid var(--line); margin: 2rem 0; }
          .text blockquote { margin: 1rem 0; padding: .5rem 1rem; border-left: 3px solid var(--accent); background: var(--soft); color: var(--muted); }
          .text table { border-collapse: collapse; margin: 1rem 0; width: 100%; font-size: .95rem; }
          .text th, .text td { border: 1px solid var(--line); padding: .4rem .65rem; text-align: left; vertical-align: top; }
          .text th { background: var(--soft); }
          .text pre { background: var(--soft); padding: .75rem; overflow-x: auto; border-radius: 6px; }
          .text code { font-family: ui-monospace, Consolas, monospace; font-size: .9em; }
          footer { margin-top: 2.5rem; padding: 1rem 1.25rem; background: var(--soft); border-radius: 8px; color: var(--muted); font-size: .9rem; }
          footer h2 { font-size: .85rem; margin: 0 0 .5rem; }
          .sources { margin: 0; padding-left: 1.2rem; }
          .sources li { margin: .35rem 0; }
          .url { font-size: .8rem; overflow-wrap: anywhere; }
          @media (max-width: 40rem) { main { margin: 0; padding: 1.5rem 1rem; border: 0; border-radius: 0; } }
          @media print { body { background: #fff; } main { max-width: none; margin: 0; padding: 0; border: 0; } }
        </style>

        """;

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A page an answer was read from: its name as the person saw it, and its address when it has one.</summary>
internal sealed record ResultSource(string Name, string? Url);
