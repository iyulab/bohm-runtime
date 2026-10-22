using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace Bohm.Runtime.Host;

/// <summary>
/// Whether an application's inline scripts and event handler attributes read as JavaScript — checked before a proposal is accepted, so a
/// syntax error goes back to the model in the same turn instead of surfacing only when the page opens.
/// Only syntax: whether the code works is the preview's to find. Scripts loaded by <c>src</c> and blocks
/// whose type is not JavaScript (JSON data, templates, import maps) are not read.
/// </summary>
internal static partial class AppScripts
{
    // The latest language, and regular expressions checked against JavaScript's own grammar only (an unknown flag,
    // an unclosed group) — never converted to .NET's, whose limits a page the browser runs must not be refused for.
    private static readonly ParserOptions Options = new()
    {
        EcmaVersion = EcmaVersion.Latest,
        OnRegExp = static (in RegExpParsingContext context) =>
        {
            context.Validate();
            return RegExpParseResult.ForSuccess(null, null);
        },
    };

    /// <summary>One line per inline script or event handler attribute that does not parse: which one, the line in the file, and the parser's message.</summary>
    internal static List<string> SyntaxProblems(string html)
    {
        var problems = new List<string>();
        var number = 0;
        foreach (Match script in InlineScript().Matches(html))
        {
            number++;
            var attributes = script.Groups["attrs"].Value;
            if (SourceAttribute().IsMatch(attributes)) continue;
            var type = TypeAttribute().Match(attributes) is { Success: true } t ? t.Groups["type"].Value.Trim().ToLowerInvariant() : "";
            var module = type == "module";
            if (!module && type is not ("" or "text/javascript" or "application/javascript" or "text/ecmascript" or "application/ecmascript")) continue;

            var body = script.Groups["body"];
            try
            {
                var parser = new Parser(Options);
                if (module) parser.ParseModule(body.Value);
                else parser.ParseScript(body.Value);
            }
            catch (ParseErrorException e)
            {
                // The line in the whole file, so an exact replacement can find it: lines before the script's body, plus the error's line in it.
                var line = Lines(html.AsSpan(0, body.Index)) + e.LineNumber;
                problems.Add($"Script {number} does not parse as JavaScript, at line {line} of the file: {e.Description}. Correct it.");
            }
        }

        // An event handler attribute is code too, and one the page only runs when the person acts — a quote of the
        // attribute's own kind inside it ends it early (measured: onclick="…querySelector('[data-action="…").
        // Handlers inside scripts are text the scripts build, not the page's.
        var scripts = InlineScript().Matches(html).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        foreach (Match handler in EventHandler().Matches(html))
        {
            if (scripts.Any(s => handler.Index >= s.Index && handler.Index < s.End)) continue;
            try { new Parser(Options).ParseScript("function handler(event) {\n" + System.Net.WebUtility.HtmlDecode(handler.Groups["code"].Value) + "\n}"); }
            catch (ParseErrorException e)
            {
                var line = Lines(html.AsSpan(0, handler.Index + 1)) + 1;
                problems.Add($"The {handler.Groups["event"].Value.ToLowerInvariant()} attribute at line {line} of the file does not parse as JavaScript: {e.Description}. A quote of the attribute's own kind ends it — use the other kind inside. Correct it.");
            }
        }

        return problems;
    }

    /// <summary>
    /// What a change leaves unfinished. First a script it left unreadable: every name that script defines then reads as
    /// missing, so the one line that broke is the cause to give, not the names (measured: one stray tag in a script, seventeen
    /// handlers it held reported as undefined). Otherwise what the change makes the application use that nothing gives it: a name called or read (a function handed to
    /// <c>onclick</c>, say) that no script, handler or element id defines, and an element looked up by id (<c>getElementById("x")</c>, <c>querySelector("#x")</c>) that
    /// the page never has. Only what <paramref name="after"/> lacks and <paramref name="before"/> did not already lack — a
    /// change made in several replacements where one missed (the button added, its function not) leaves exactly this, and
    /// the page fails only when the person presses it. Read by name, not by scope: a name defined anywhere counts as defined,
    /// so this misses some faults and invents none a browser would not also hit.
    /// </summary>
    internal static List<string> Unfinished(string before, string after)
    {
        var broken = SyntaxProblems(after);
        if (broken.Count > SyntaxProblems(before).Count) return broken;

        var (namesBefore, idsBefore) = Missing(before);
        var (names, ids) = Missing(after);
        var problems = new List<string>();
        foreach (var name in names.Where(n => !namesBefore.Contains(n)))
            problems.Add($"The page uses {name} but nothing defines it — no function, variable or element id of that name. Define it, or use what is there.");
        foreach (var id in ids.Where(i => !idsBefore.Contains(i)))
            problems.Add($"The page looks up the element with id \"{id}\" but no element has that id. Add the element, or use the id it has.");
        return problems;
    }

    private static (SortedSet<string> Names, SortedSet<string> Ids) Missing(string html)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var called = new HashSet<string>(StringComparer.Ordinal);
        var looked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in Code(html))
        {
            Node program;
            try { program = code.Module ? new Parser(Options).ParseModule(code.Text) : new Parser(Options).ParseScript(code.Text); }
            catch (ParseErrorException) { continue; }
            Walk(program, declared, called, looked);
        }

        // An element's id is a global name in a browser (window.<id>), and an id written anywhere — markup, or a script
        // that builds markup or sets .id — counts as the page having it.
        var ids = new HashSet<string>(IdAttribute().Matches(html).Select(m => m.Groups["id"].Value), StringComparer.Ordinal);
        var names = new SortedSet<string>(called.Where(n => !declared.Contains(n) && !ids.Contains(n) && !BrowserGlobals.Contains(n)), StringComparer.Ordinal);
        return (names, new SortedSet<string>(looked.Where(i => !ids.Contains(i)), StringComparer.Ordinal));
    }

    // Inline scripts in JavaScript, and each inline event handler (onclick="…") read as the body of a function.
    private static IEnumerable<(string Text, bool Module)> Code(string html)
    {
        foreach (Match script in InlineScript().Matches(html))
        {
            var attributes = script.Groups["attrs"].Value;
            if (SourceAttribute().IsMatch(attributes)) continue;
            var type = TypeAttribute().Match(attributes) is { Success: true } t ? t.Groups["type"].Value.Trim().ToLowerInvariant() : "";
            if (type == "module") yield return (script.Groups["body"].Value, true);
            else if (type is "" or "text/javascript" or "application/javascript" or "text/ecmascript" or "application/ecmascript") yield return (script.Groups["body"].Value, false);
        }

        foreach (Match handler in EventHandler().Matches(html))
            yield return ("function handler(event) {\n" + System.Net.WebUtility.HtmlDecode(handler.Groups["code"].Value) + "\n}", false);
    }

    private static void Walk(Node node, HashSet<string> declared, HashSet<string> called, HashSet<string> looked)
    {
        switch (node)
        {
            case FunctionDeclaration f:
                if (f.Id is { } name) declared.Add(name.Name);
                foreach (var p in f.Params) Bind(p, declared);
                break;
            case FunctionExpression f:
                if (f.Id is { } own) declared.Add(own.Name);
                foreach (var p in f.Params) Bind(p, declared);
                break;
            case ArrowFunctionExpression f:
                foreach (var p in f.Params) Bind(p, declared);
                break;
            case ClassDeclaration { Id: { } type }:
                declared.Add(type.Name);
                break;
            case VariableDeclarator v:
                Bind(v.Id, declared);
                break;
            case CatchClause c when c.Param is { } p:
                Bind(p, declared);
                break;
            case AssignmentExpression a:
                // name = … and window.name = … make a global
                if (a.Left is Identifier i) declared.Add(i.Name);
                else if (a.Left is MemberExpression { Object: Identifier { Name: "window" or "globalThis" or "self" }, Property: Identifier p, Computed: false }) declared.Add(p.Name);
                break;
            case CallExpression call:
                if (call.Callee is MemberExpression { Property: Identifier { Name: var method }, Computed: false }
                    && call.Arguments.Count > 0 && call.Arguments[0] is Literal { Value: string text })
                {
                    if (method == "getElementById" && SimpleId().IsMatch(text)) looked.Add(text);
                    else if (method == "querySelector" && text.StartsWith('#') && SimpleId().IsMatch(text[1..])) looked.Add(text[1..]);
                }
                break;
            case Identifier read:
                // Reached here only where a name is read: the walk below does not enter a member's or a property's own name.
                called.Add(read.Name);
                break;
        }

        foreach (var child in node.ChildNodes)
        {
            // Not a read of a name: obj.name, { name: … }, class members, labels.
            if (child is Identifier && node switch
                {
                    MemberExpression m => !m.Computed && ReferenceEquals(child, m.Property),
                    Property p => !p.Computed && ReferenceEquals(child, p.Key) && !p.Shorthand,
                    PropertyDefinition d => !d.Computed && ReferenceEquals(child, d.Key),
                    MethodDefinition d => !d.Computed && ReferenceEquals(child, d.Key),
                    LabeledStatement or BreakStatement or ContinueStatement => true,
                    MetaProperty => true,
                    _ => false,
                })
                continue;
            Walk(child, declared, called, looked);
        }
    }

    private static void Bind(Node pattern, HashSet<string> declared)
    {
        switch (pattern)
        {
            case Identifier i: declared.Add(i.Name); break;
            case AssignmentPattern a: Bind(a.Left, declared); break;
            case RestElement r: Bind(r.Argument, declared); break;
            case ArrayPattern a: foreach (var e in a.Elements) if (e is not null) Bind(e, declared); break;
            case ObjectPattern o:
                foreach (var p in o.Properties)
                {
                    if (p is AssignmentProperty ap) Bind(ap.Value, declared);
                    else if (p is RestElement rest) Bind(rest.Argument, declared);
                }
                break;
        }
    }

    // What a page may use by name without defining it: the language's and the browser's own. Not every one there is —
    // a name missing here and used by a change is reported, and the model, told so, can say it is the browser's.
    private static readonly HashSet<string> BrowserGlobals = new(StringComparer.Ordinal)
    {
        "undefined", "NaN", "Infinity", "globalThis", "window", "self", "top", "parent", "frames", "opener", "document", "console", "navigator", "location",
        "history", "screen", "performance", "crypto", "localStorage", "sessionStorage", "indexedDB", "caches", "origin", "name", "status", "event",
        "innerWidth", "innerHeight", "outerWidth", "outerHeight", "devicePixelRatio", "scrollX", "scrollY", "pageXOffset", "pageYOffset", "visualViewport",
        "speechSynthesis", "CSS", "Reflect", "Atomics", "WebAssembly", "JSON", "Math", "arguments",
        "alert", "confirm", "prompt", "print", "open", "close", "focus", "blur", "scrollTo", "scrollBy", "scroll", "postMessage", "getComputedStyle", "matchMedia",
        "setTimeout", "clearTimeout", "setInterval", "clearInterval", "requestAnimationFrame", "cancelAnimationFrame", "requestIdleCallback", "cancelIdleCallback",
        "queueMicrotask", "structuredClone", "fetch", "atob", "btoa", "reportError", "createImageBitmap",
        "parseInt", "parseFloat", "isNaN", "isFinite", "encodeURIComponent", "decodeURIComponent", "encodeURI", "decodeURI", "escape", "unescape", "eval",
        "String", "Number", "Boolean", "Symbol", "BigInt", "Object", "Array", "Function", "Date", "RegExp", "Error", "TypeError", "RangeError", "SyntaxError",
        "ReferenceError", "EvalError", "URIError", "AggregateError", "Promise", "Map", "Set", "WeakMap", "WeakSet", "WeakRef", "Proxy", "ArrayBuffer",
        "SharedArrayBuffer", "DataView", "Int8Array", "Uint8Array", "Uint8ClampedArray", "Int16Array", "Uint16Array", "Int32Array", "Uint32Array",
        "Float32Array", "Float64Array", "BigInt64Array", "BigUint64Array", "FinalizationRegistry",
        "Blob", "File", "FileReader", "FormData", "URL", "URLSearchParams", "Headers", "Request", "Response", "AbortController", "TextEncoder", "TextDecoder",
        "Event", "CustomEvent", "KeyboardEvent", "MouseEvent", "EventTarget", "Image", "Audio", "Option", "Worker", "WebSocket", "XMLHttpRequest",
        "MutationObserver", "ResizeObserver", "IntersectionObserver", "BroadcastChannel", "MessageChannel", "Notification", "DOMParser", "XMLSerializer",
        "Intl", "Path2D", "OffscreenCanvas", "ImageData", "DOMRect", "DOMMatrix", "Range", "Selection", "CSSStyleSheet", "AudioContext", "MediaRecorder",
        "MediaStream", "SpeechSynthesisUtterance", "ClipboardItem", "PerformanceObserver", "IDBKeyRange", "Document", "DocumentFragment", "HTMLElement",
        "Element", "Node", "NodeFilter", "Text", "Comment",
    };

    [GeneratedRegex(@"\b(?:id\s*=\s*[""']?|\.id\s*=\s*[""'])(?<id>[A-Za-z_][\w-]*)")]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"^[A-Za-z_][\w-]*$")]
    private static partial Regex SimpleId();

    // An inline event handler attribute, quoted either way.
    [GeneratedRegex(@"\s(?<event>on[a-z]+)\s*=\s*(?:""(?<code>[^""]*)""|'(?<code>[^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandler();

    private static int Lines(ReadOnlySpan<char> text) => text.Count('\n');

    // A script element's content ends at the first "</script" (the HTML rule), whatever the JavaScript inside says.
    [GeneratedRegex(@"<script\b(?<attrs>[^>]*)>(?<body>.*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"\bsrc\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SourceAttribute();

    [GeneratedRegex(@"\btype\s*=\s*(?:""(?<type>[^""]*)""|'(?<type>[^']*)'|(?<type>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex TypeAttribute();
}
