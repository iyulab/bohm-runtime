using Bohm.Runtime.Host.Edit;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Moving storage replaces pieces of an application; a piece that took a declaration away while
/// other code still uses the name leaves an application that stops with «… is not defined».
/// </summary>
public sealed class RemovedButUsedTests
{
    private static IReadOnlyList<string> Check(string removedCode, string newCode, string sourceAfter) =>
        EditProposals.RemovedButUsed([new SourceEdit(removedCode, newCode)], sourceAfter);

    [Fact]
    public void A_variable_whose_declaration_was_replaced_but_is_still_read_is_found() =>
        Assert.Equal(["db"], Check("let db;\nlet auth;", "", "function save() { if (!db) return; }"));

    [Fact]
    public void A_name_used_in_an_inline_script_of_a_document_is_found() =>
        Assert.Equal(["db"], Check("let db;", "", "<p>x</p>\n<script type=\"module\">if (!db) { show(`no ${db}`); }</script>"));

    [Fact]
    public void Code_keeps_template_expressions_and_drops_text_comments_and_strings()
    {
        var code = EditProposals.CodeOnly("a(`x ${db} y ${f({ k: 1 })} z`); /* db */ b('db', \"db\"); // db\nc();");
        Assert.Contains("db", code, StringComparison.Ordinal);                 // inside ${…}
        Assert.Contains("f({ k: 1 })", code, StringComparison.Ordinal);        // nested braces stay code
        Assert.DoesNotContain("x ", code, StringComparison.Ordinal);
        Assert.Equal(1, code.Split("db").Length - 1);                          // only the expression, not the comment or strings
        Assert.Contains("c();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_drops_the_body_of_a_regular_expression_literal_but_keeps_a_division()
    {
        var code = EditProposals.CodeOnly("const m = s.match(/(\\d+)\\s*[a-z/]반/g); const r = total / d / 2; if (/^\\d+$/.test(x)) go(d);");
        Assert.DoesNotContain("\\d", code, StringComparison.Ordinal);          // the pattern is not code
        Assert.DoesNotContain("[a-z", code, StringComparison.Ordinal);         // a slash inside a class does not end it
        Assert.Contains("total / d / 2", code, StringComparison.Ordinal);      // a division after a name stays
        Assert.Contains(".test(x)) go(d);", code, StringComparison.Ordinal);   // what follows the literal stays
    }

    [Fact]
    public void A_letter_of_a_regular_expression_escape_is_not_a_use_of_a_removed_name() =>
        // A removed callback's parameter `d`, and `\d` in a pattern that stayed (cycle-379 — a finished move was held back).
        Assert.Empty(Check("snap.forEach((d) => list.push(d.data()));", "",
            "<script>const n = str.match(/(\\d+)\\s*반/); if (/^\\d+$/.test(str)) save(n);</script>"));

    [Fact]
    public void A_removed_name_used_after_a_regular_expression_literal_is_still_found() =>
        Assert.Equal(["db"], Check("let db;", "", "<script>if (/^a/.test(s)) save(db);</script>"));

    [Fact]
    public void A_condition_in_a_removed_piece_does_not_make_its_keywords_names() =>
        Assert.Empty(Check("if (typeof db !== 'undefined') { load(); }\nready.then(async () => { await go(); });", "",
            "<script>if (typeof crypto !== 'undefined') {}\nconst f = async () => {};</script>"));

    [Fact]
    public void A_function_removed_while_still_called_is_found() =>
        Assert.Equal(["initAuth"], Check("async function initAuth() { await signIn(); }", "", "window.onload = () => initAuth();"));

    [Fact]
    public void An_imported_name_still_used_is_found_under_its_local_name() =>
        Assert.Equal(["col"], Check("import { getFirestore, collection as col } from \"x\";", "", "const list = col(db, 'a');\nconst db = {};"));

    [Theory]
    [InlineData("let db = null;\nfunction save() { if (!db) return; }")]   // declared again
    [InlineData("function save(db) { return db; }")]                     // a parameter
    [InlineData("const store = { db: 1 }; store.db = 2;")]               // a property, not the name
    [InlineData("function save() { return 1; }")]                        // no longer used
    [InlineData("let a, db, b;\nif (!db) {}")]                            // one of several declared together
    [InlineData("<i class=\"icon-db\"></i><p>db</p><script>save();</script>")]   // the word in markup, not in a script
    [InlineData("<script>log('db is gone'); // db removed\n</script>")]           // in a string and a comment
    [InlineData("<script>const id = `local-db-${n}`;</script>")]                    // in a template's fixed text
    [InlineData("<script>list.map(db => db.id);</script>")]                         // a parameter without parentheses
    [InlineData("<script>const f = (a, db = 1, { c }) => db;</script>")]            // a parameter with a default
    [InlineData("<script>const [db, setDb] = useState([]);</script>")]              // an array pattern (a React state)
    [InlineData("<script>const { data: db, other } = load();</script>")]            // an object pattern with a new name
    [InlineData("<script>let { db = 1 } = options; use(db);</script>")]             // an object pattern with a default
    [InlineData("<script>for (const [key, db] of entries) use(db);</script>")]      // a pattern in a loop
    public void A_name_that_is_declared_again_or_not_used_is_not_found(string sourceAfter) =>
        Assert.Empty(Check("let db;", "", sourceAfter));
}
