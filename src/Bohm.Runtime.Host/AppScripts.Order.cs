using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace Bohm.Runtime.Host;

internal static partial class AppScripts
{
    /// <summary>
    /// A name a page's script reads, while it starts, before the line that gives the name its value: a call made as the script
    /// runs (<c>var items = load();</c>) reaches a function that reads a variable set only further down (<c>var win = window;</c>).
    /// Until that line the variable is undefined (<c>var</c>) or throws (<c>let</c>, <c>const</c>), and a <c>try</c>/<c>catch</c>
    /// around the read hides it: measured, a generated page then started with an empty list every time it opened, and the next
    /// save wrote over the records it had kept. Read in the order the script runs: its top level, and the body of a function it
    /// runs at once (wrapped in <c>(function () { … })()</c>, or given to <c>DOMContentLoaded</c> / <c>load</c>). A function only
    /// handed over (an event listener, a timer) runs later and is not followed; neither is a method, so this misses some faults and
    /// names none a browser would not also hit, apart from a read the code never reaches.
    /// </summary>
    internal static List<EarlyRead> ReadBeforeSet(string html)
    {
        var found = new List<EarlyRead>();
        foreach (Match script in InlineScript().Matches(html))
        {
            var attributes = script.Groups["attrs"].Value;
            if (SourceAttribute().IsMatch(attributes)) continue;
            var type = TypeAttribute().Match(attributes) is { Success: true } t ? t.Groups["type"].Value.Trim().ToLowerInvariant() : "";
            if (type is not ("" or "text/javascript" or "application/javascript" or "text/ecmascript" or "application/ecmascript")) continue;
            var body = script.Groups["body"];
            Script program;
            try { program = new Parser(Options).ParseScript(body.Value); }
            catch (ParseErrorException) { continue; }
            var lineBefore = Lines(html.AsSpan(0, body.Index));
            foreach (var sequence in Sequences(program.Body))
                Check(sequence, lineBefore, found);
        }

        return found.DistinctBy(f => (f.Call, f.Name)).ToList();
    }

    /// <summary>A read <see cref="ReadBeforeSet"/> found: what was run (a function's name, or <c>null</c> for the line itself), the name read, and both lines in the file.</summary>
    internal sealed record EarlyRead(string? Call, string Name, int Line, int SetAt, bool Throws);

    /// <summary>One problem per line that runs too early, naming every name it reads before its line — for the model, in a browser's words.</summary>
    internal static List<string> Problems(IEnumerable<EarlyRead> reads) =>
        reads.GroupBy(r => (r.Call, r.Line)).Select(g =>
        {
            var names = string.Join(", ", g.Select(r => $"{r.Name} (set at line {r.SetAt}{(r.Throws ? ", throws until then" : "")})"));
            var first = g.OrderBy(r => r.SetAt).First();
            return g.Key.Call is { } call
                ? $"Line {g.Key.Line} runs {call}() while the page starts, and {call}() reads {names} before the line that gives it a value, so it is undefined there. A try/catch around that read hides it, and the page starts without what it reads through it (its saved data, say), which the next save then overwrites. Move those lines above line {g.Key.Line}, or run {call}() after line {first.SetAt}."
                : $"Line {g.Key.Line} reads {names} while the page starts, before the line that gives it a value, so it is undefined there. Move that line above line {g.Key.Line}.";
        }).ToList();

    // The statement lists run in order as the page starts: the script, and the body of each function it runs at once.
    private static IEnumerable<IReadOnlyList<Statement>> Sequences(IReadOnlyList<Statement> statements)
    {
        yield return statements;
        foreach (var statement in statements)
            if (RunAtOnce(statement) is { } inner)
                foreach (var nested in Sequences(inner))
                    yield return nested;
    }

    // (function () { … })(), !function () { … }(), (() => { … })(), addEventListener('DOMContentLoaded' | 'load', function …), window.onload = function …
    private static IReadOnlyList<Statement>? RunAtOnce(Statement statement)
    {
        if (statement is not ExpressionStatement { Expression: var expression }) return null;
        while (expression is UnaryExpression unary) expression = unary.Argument;
        var function = expression switch
        {
            CallExpression { Callee: FunctionExpression or ArrowFunctionExpression } call => call.Callee,
            CallExpression { Callee: MemberExpression { Property: Identifier { Name: "addEventListener" } }, Arguments: [Literal { Value: "DOMContentLoaded" or "load" }, var handler, ..] } => handler,
            AssignmentExpression { Left: MemberExpression { Property: Identifier { Name: "onload" } }, Right: var handler } => handler,
            _ => null,
        };
        return function switch
        {
            FunctionExpression { Body: var block } => block.Body,
            ArrowFunctionExpression { Body: BlockStatement block } => block.Body,
            _ => null,
        };
    }

    private static void Check(IReadOnlyList<Statement> statements, int lineBefore, List<EarlyRead> found)
    {
        // What this list declares, the statement that gives each a value, and the functions it declares (hoisted) or assigns.
        var kinds = new Dictionary<string, VariableDeclarationKind>(StringComparer.Ordinal);
        var setAt = new Dictionary<string, int>(StringComparer.Ordinal);
        var functions = new Dictionary<string, (Node Function, int From)>(StringComparer.Ordinal);
        for (var i = 0; i < statements.Count; i++)
        {
            switch (statements[i])
            {
                case FunctionDeclaration { Id: { } name } declared:
                    functions[name.Name] = (declared, -1);
                    break;
                case VariableDeclaration declaration:
                    foreach (var declarator in declaration.Declarations)
                    {
                        if (declarator.Id is not Identifier id) continue;
                        kinds.TryAdd(id.Name, declaration.Kind);
                        if (declarator.Init is null) continue;
                        setAt.TryAdd(id.Name, i);
                        if (declarator.Init is FunctionExpression or ArrowFunctionExpression) functions.TryAdd(id.Name, (declarator.Init, i));
                    }
                    break;
                case ExpressionStatement { Expression: AssignmentExpression { Left: Identifier assigned } }:
                    setAt.TryAdd(assigned.Name, i);
                    break;
            }
        }

        for (var i = 0; i < statements.Count; i++)
        {
            if (statements[i] is FunctionDeclaration) continue;
            var line = lineBefore + statements[i].Location.Start.Line;
            var (reads, calls, used) = ReadNow(statements[i]);
            foreach (var name in reads)
                if (Late(name, i, used) is { } late) found.Add(new EarlyRead(null, name, line, late.Line, late.Throws));
            foreach (var call in calls)
            {
                if (!functions.TryGetValue(call, out var function) || function.From >= i) continue;
                var (reached, usedThere) = ReachedFrom(call);
                foreach (var name in reached)
                    if (Late(name, i, usedThere) is { } late) found.Add(new EarlyRead(call, name, line, late.Line, late.Throws));
            }
        }

        // Read before its line: a let or const throws there; a var is undefined, which fails only where it is used as a value
        // that has members (name.x, name[…], name()) — clearTimeout(timer) on a timer not set yet is harmless and not named.
        (int Line, bool Throws)? Late(string name, int at, HashSet<string> used) =>
            kinds.TryGetValue(name, out var kind) && setAt.TryGetValue(name, out var set) && set > at
            && (kind != VariableDeclarationKind.Var || used.Contains(name))
                ? (lineBefore + statements[set].Location.Start.Line, kind != VariableDeclarationKind.Var)
                : null;

        // Every name a function reads as it runs, through the functions of this list it calls in turn — less what it declares itself.
        (HashSet<string> Reads, HashSet<string> Used) ReachedFrom(string start)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var usedAll = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal) { start };
            var queue = new Queue<string>([start]);
            while (queue.TryDequeue(out var name))
            {
                var function = functions[name].Function;
                var own = Declared(function);
                var body = function switch { FunctionDeclaration f => (Node)f.Body, FunctionExpression f => f.Body, ArrowFunctionExpression f => f.Body, _ => function };
                var (reads, calls, used) = ReadNow(body);
                reached.UnionWith(reads.Where(r => !own.Contains(r)));
                usedAll.UnionWith(used.Where(r => !own.Contains(r)));
                foreach (var call in calls.Where(c => !own.Contains(c) && functions.ContainsKey(c) && seen.Add(c))) queue.Enqueue(call);
            }

            return (reached, usedAll);
        }
    }

    // The names a piece of code reads, the names it calls, and the names it uses as a value with members (name.x, name[…],
    // name()), when it runs — not inside the functions it only defines or hands over.
    private static (HashSet<string> Reads, HashSet<string> Calls, HashSet<string> Used) ReadNow(Node node)
    {
        var reads = new HashSet<string>(StringComparer.Ordinal);
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        Visit(node, root: true);
        return (reads, calls, used);

        void Visit(Node at, bool root)
        {
            switch (at)
            {
                case FunctionDeclaration or FunctionExpression or ArrowFunctionExpression or ClassDeclaration or ClassExpression when !root:
                    return;
                case UnaryExpression { Operator: Operator.TypeOf, Argument: Identifier }:
                    return;   // typeof name — the way to ask whether it is there yet
                case CatchClause:
                    return;   // what runs only when something failed (a toast for a full disk) is not the way the page starts
                case CallExpression { Callee: Identifier called }:
                    calls.Add(called.Name);
                    used.Add(called.Name);
                    break;
                case MemberExpression { Object: Identifier owner }:
                    used.Add(owner.Name);
                    break;
                case Identifier read:
                    reads.Add(read.Name);
                    return;
                case VariableDeclarator declarator:
                    if (declarator.Init is { } init) Visit(init, false);
                    return;
                case AssignmentExpression { Left: Identifier, Right: var right, Operator: Operator.Assignment }:
                    Visit(right, false);
                    return;
            }

            foreach (var child in at.ChildNodes)
            {
                if (child is Identifier && at switch
                    {
                        MemberExpression m => !m.Computed && ReferenceEquals(child, m.Property),
                        Property p => !p.Computed && ReferenceEquals(child, p.Key) && !p.Shorthand,
                        PropertyDefinition d => !d.Computed && ReferenceEquals(child, d.Key),
                        MethodDefinition d => !d.Computed && ReferenceEquals(child, d.Key),
                        LabeledStatement or BreakStatement or ContinueStatement or MetaProperty => true,
                        _ => false,
                    })
                    continue;
                Visit(child, false);
            }
        }
    }

    // What a function declares for itself — its parameters and every name declared inside it — which hides the list's names of the same.
    private static HashSet<string> Declared(Node function)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var parameters = function switch
        {
            FunctionDeclaration f => f.Params,
            FunctionExpression f => f.Params,
            ArrowFunctionExpression f => f.Params,
            _ => default,
        };
        foreach (var p in parameters) Bind(p, declared);
        Collect(function);
        return declared;

        void Collect(Node at)
        {
            foreach (var child in at.ChildNodes)
            {
                switch (child)
                {
                    case VariableDeclarator v: Bind(v.Id, declared); break;
                    case FunctionDeclaration { Id: { } name }: declared.Add(name.Name); break;
                    case ClassDeclaration { Id: { } type }: declared.Add(type.Name); break;
                    case CatchClause { Param: { } param }: Bind(param, declared); break;
                }

                Collect(child);
            }
        }
    }
}
