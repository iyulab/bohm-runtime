using System.Text.RegularExpressions;
using Bohm.Runtime.Host.Adoption;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A model's answer is written in Markdown. It is shown as formatted text — tables, lists, emphasis — and
/// nothing in it runs or loads: raw HTML stays text, an image is its description, a link is its words and
/// its address as text.
/// </summary>
public sealed partial class AnswerMarkdownTests
{
    private static readonly HashSet<string> Allowed =
        ["p", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "strong", "em", "del", "code", "pre", "blockquote",
         "table", "thead", "tbody", "tr", "th", "td", "hr", "br", "span"];

    [Fact]
    public void Tables_lists_and_emphasis_become_their_elements()
    {
        var html = AnswerMarkdown.ToHtml("""
            **Lunch** this week:

            | Day | Menu |
            |---|---|
            | Mon | Rice |

            - one
            - *two*
            """);

        Assert.Contains("<strong>Lunch</strong>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<td>Rice</td>", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<em>two</em>", html);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("Hi <b onclick=\"alert(1)\">there</b>")]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("![logo](https://tracker.example/pixel.png)")]
    [InlineData("<https://example.com/a>")]
    [InlineData("[x](https://example.com/?a=\"><script>)")]
    [InlineData("```html\n<script>alert(1)</script>\n```")]
    [InlineData("{#id .class onclick=alert(1)}\n# Heading {onclick=alert(1)}")]
    public void Nothing_in_an_answer_becomes_a_script_a_link_or_something_that_loads(string markdown)
    {
        var html = AnswerMarkdown.ToHtml(markdown);

        foreach (Match tag in Tag().Matches(html))
        {
            Assert.Contains(tag.Groups[1].Value.ToLowerInvariant(), Allowed);
            Assert.True(tag.Groups[2].Value.Trim() is "" or "class=\"url\"" || tag.Groups[2].Value.Trim().StartsWith("class=\"language-", StringComparison.Ordinal),
                $"attributes «{tag.Groups[2].Value}» in {html}");
        }

        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void A_link_keeps_its_words_and_shows_its_address_as_text()
    {
        var html = AnswerMarkdown.ToHtml("See [the menu](https://school.example/menu?w=1&d=2) or ![a map](https://maps.example/x.png).");

        Assert.Contains("the menu <span class=\"url\">(https://school.example/menu?w=1&amp;d=2)</span>", html);
        Assert.Contains("a map", html);
        Assert.DoesNotContain("maps.example", html);
    }

    [GeneratedRegex(@"<\s*([a-zA-Z][a-zA-Z0-9]*)([^>]*)>")]
    private static partial Regex Tag();
}
