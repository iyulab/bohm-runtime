using System.Net;
using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// A model's answer, written in Markdown, as HTML to show: headings, paragraphs, lists, emphasis, code and
/// tables. Nothing in it runs or loads — raw HTML stays text, an image is its description, and a link is
/// its words with its address beside them as text. The answer is someone else's writing; showing it must
/// not let it act.
/// </summary>
internal static class AnswerMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static string ToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>().ToList())
            Flatten(link);
        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
            autolink.ReplaceBy(new LiteralInline(autolink.Url));

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        return writer.ToString();
    }

    /// <summary>Puts a link's words where the link was — and, for a link written with words, its address as text.</summary>
    private static void Flatten(LinkInline link)
    {
        if (link.Parent is null) return; // inside an image already flattened
        if (link.IsImage)
        {
            link.ReplaceBy(new LiteralInline(TextOf(link)));
            return;
        }

        while (link.FirstChild is { } child)
        {
            child.Remove();
            link.InsertBefore(child);
        }

        if (!link.IsAutoLink && !string.IsNullOrWhiteSpace(link.Url))
            link.InsertBefore(new HtmlInline($" <span class=\"url\">({WebUtility.HtmlEncode(link.Url)})</span>"));
        link.Remove();
    }

    private static string TextOf(ContainerInline container)
    {
        var text = new StringBuilder();
        foreach (var inline in container.Descendants<LiteralInline>())
            text.Append(inline.Content.ToString());
        return text.ToString();
    }
}
