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
    public void A_name_that_is_declared_again_or_not_used_is_not_found(string sourceAfter) =>
        Assert.Empty(Check("let db;", "", sourceAfter));
}
