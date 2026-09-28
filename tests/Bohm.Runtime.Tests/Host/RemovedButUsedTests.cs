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
    public void A_name_that_is_declared_again_or_not_used_is_not_found(string sourceAfter) =>
        Assert.Empty(Check("let db;", "", sourceAfter));
}
