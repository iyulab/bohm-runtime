using Bohm.Runtime.Host;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// An application's inline scripts are read as JavaScript before it is accepted: a syntax error is told with
/// the line in the file, and what a browser runs is never refused.
/// </summary>
public sealed class AppScriptsTests
{
    // Syntax errors of the kinds a generated application carries to its first load — a regular expression flag, a name
    // declared twice, an unclosed call, a const without a value — each known before the page runs.
    [Theory]
    [InlineData("const re = /\\d+/gq; re.test('1');", "flag")]
    [InlineData("let items = []; let items = [1];", "items")]
    [InlineData("console.log('a', 'b';", "")]
    [InlineData("const total;", "")]
    public void A_script_that_does_not_parse_is_told_with_its_line_in_the_file(string code, string named)
    {
        var html = "<!doctype html>\n<title>x</title>\n<p>hi</p>\n<script>\n" + code + "\n</script>";

        var problem = Assert.Single(AppScripts.SyntaxProblems(html));

        Assert.StartsWith("Script 1 does not parse as JavaScript, at line 5 of the file: ", problem, StringComparison.Ordinal);
        Assert.Contains(named, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void What_a_browser_runs_is_not_refused()
    {
        var html = """
            <!doctype html><title>ok</title>
            <script>
              const year = /(?<y>\d{4})-(?<m>\d{2})/u.exec('2026-10')?.groups.y;
              const letters = /\p{L}+/v.test('봄');
              const after = /(?<=\$)\d+/.exec('$12');
              const total = [1, 2].reduce((a, b) => a + b, 0) ?? 0;
              class Counter { #n = 0; static of() { return new Counter(); } get n() { return this.#n; } }
              const html = `<li>${total}</li>`; // a template, not markup the parser reads
              if (total > 1) { console.log('</div>'); }
            </script>
            <script type="module">
              const data = await Promise.resolve(import.meta.url);
              export const shown = data;
            </script>
            <script type="application/json">{ "not": javascript at all }</script>
            <script src="kept-from-when-it-was-added.js"></script>
            """;

        Assert.Empty(AppScripts.SyntaxProblems(html));
    }

    [Fact]
    public void Each_script_is_numbered_in_the_order_it_appears()
    {
        var html = "<script>ok();</script>\n<script type=\"text/template\"><b>{{x}}</b></script>\n<script>let a = ;</script>";

        Assert.StartsWith("Script 3 does not parse as JavaScript, at line 3 of the file: ", Assert.Single(AppScripts.SyntaxProblems(html)), StringComparison.Ordinal);
    }
}
