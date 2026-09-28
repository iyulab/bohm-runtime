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
    /// The page: the answer as text — escaped, its lines kept — under the title, and the sources below.
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
        html.Append("""
            <style>
              body { font: 16px/1.6 system-ui, sans-serif; max-width: 46rem; margin: 2rem auto; padding: 0 1rem; color: #1f2328; background: #fff; }
              h1 { font-size: 1.5rem; margin: 0 0 1rem; }
              .text { overflow-wrap: anywhere; }
              .text table { border-collapse: collapse; margin: 1rem 0; }
              .text th, .text td { border: 1px solid #d1d9e0; padding: .3rem .6rem; text-align: left; vertical-align: top; }
              .text pre { background: #f6f8fa; padding: .75rem; overflow-x: auto; }
              .text code { font-family: ui-monospace, Consolas, monospace; font-size: .9em; }
              h2 { font-size: 1rem; margin: 2rem 0 .5rem; color: #59636e; }
              .sources { padding-left: 1.2rem; color: #59636e; }
              .url { font-size: .85rem; overflow-wrap: anywhere; color: #59636e; }
              @media (prefers-color-scheme: dark) {
                body { color: #e6edf3; background: #0d1117; } h2, .sources, .url { color: #9198a1; }
                .text th, .text td { border-color: #3d444d; } .text pre { background: #151b23; }
              }
            </style>

            """);
        html.Append("<h1>").Append(Escape(Title)).Append("</h1>\n");
        html.Append("<div class=\"text\">\n").Append(AnswerMarkdown.ToHtml(Text.Trim())).Append("</div>\n");
        if (Sources.Count > 0)
        {
            html.Append("<h2>").Append(Escape(SourcesHeading is { Length: > 0 } heading ? heading : "Sources")).Append("</h2>\n<ul class=\"sources\">\n");
            foreach (var source in Sources)
            {
                html.Append("<li>").Append(Escape(source.Name));
                if (source.Url is { Length: > 0 } url) html.Append("<br><span class=\"url\">").Append(Escape(url)).Append("</span>");
                html.Append("</li>\n");
            }

            html.Append("</ul>\n");
        }

        return Encoding.UTF8.GetBytes(html.ToString());
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A page an answer was read from: its name as the person saw it, and its address when it has one.</summary>
internal sealed record ResultSource(string Name, string? Url);
