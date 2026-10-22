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

    // A change in several replacements where one missed: the button calls a function no script has. Measured as the most
    // common way a small model's change broke the page it was asked to change.
    [Fact]
    public void A_function_the_change_calls_that_nothing_defines_is_named()
    {
        const string before = "<button onclick=\"addTask()\">Add</button>\n<script>function addTask() {}</script>";
        var after = before.Replace("</button>", "</button><button onclick=\"exportToCSV()\">CSV</button>", StringComparison.Ordinal);

        var problem = Assert.Single(AppScripts.Dangling(before, after));

        Assert.Contains("uses exportToCSV ", problem, StringComparison.Ordinal);
        Assert.Empty(AppScripts.Dangling(before, after.Replace("</script>", " function exportToCSV() {}</script>", StringComparison.Ordinal)));
    }

    // Handed to a handler rather than called: the page fails the same way when the person presses it.
    [Fact]
    public void A_function_the_change_hands_over_by_name_that_nothing_defines_is_named()
    {
        const string before = "<button id=\"csv\">CSV</button>\n<script>const list = [];</script>";
        var after = before.Replace("</script>", " document.getElementById('csv').onclick = downloadCsv;</script>", StringComparison.Ordinal);

        Assert.Contains("uses downloadCsv ", Assert.Single(AppScripts.Dangling(before, after)), StringComparison.Ordinal);
    }

    // A member's name, a property's key, a class member and a label are not names the page reads.
    [Fact]
    public void Names_that_are_not_read_as_names_are_not_named()
    {
        const string before = "<script>const box = { size: 1 };</script>";
        var after = before.Replace("</script>", """
             box.width = box.size; const shape = { height: 2, size: box.size }; class Area { span = 1; grow() { return this.span; } }
             outer: for (const k of [1]) { continue outer; }
             </script>
            """, StringComparison.Ordinal);

        Assert.Empty(AppScripts.Dangling(before, after));
    }

    [Fact]
    public void An_element_the_change_looks_up_that_the_page_never_has_is_named()
    {
        const string before = "<div id=\"list\"></div>\n<script>document.getElementById('list').textContent = '';</script>";
        var after = before.Replace("</script>", " document.querySelector('#view-export').hidden = false;</script>", StringComparison.Ordinal);

        Assert.Contains("\"view-export\"", Assert.Single(AppScripts.Dangling(before, after)), StringComparison.Ordinal);
        // An id the page builds in a script counts as there.
        Assert.Empty(AppScripts.Dangling(before, after + "<script>const s = document.createElement('section'); s.id = 'view-export';</script>"));
    }

    // What the language and the browser give, what any scope binds (parameters, destructuring, catch), a global made by
    // assignment, an element id used as a global, and what the page already lacked before the change are not the change's fault.
    [Fact]
    public void What_a_page_may_call_without_defining_and_what_it_already_lacked_are_not_named()
    {
        const string before = "<p id=\"out\"></p>\n<script>legacyHelper();</script>";
        var after = before.Replace("</script>", """
             if (confirm('Sure?')) { const url = URL.createObjectURL(new Blob(['a'])); setTimeout(() => out.click(), 1); }
             [1].forEach(function (each) { each(); });
             const { pick, ...rest } = {}; pick(); rest();
             try {} catch (oops) { oops(); }
             window.later = function () {}; later();
             made = () => 1; made();
             </script>
            """, StringComparison.Ordinal);

        Assert.Empty(AppScripts.Dangling(before, after));
    }
}

