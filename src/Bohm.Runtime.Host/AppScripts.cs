using System.Text.RegularExpressions;
using Acornima;

namespace Bohm.Runtime.Host;

/// <summary>
/// Whether an application's inline scripts read as JavaScript — checked before a proposal is accepted, so a
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

    /// <summary>One line per inline script that does not parse: which script, the line in the file, and the parser's message.</summary>
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

        return problems;
    }

    private static int Lines(ReadOnlySpan<char> text) => text.Count('\n');

    // A script element's content ends at the first "</script" (the HTML rule), whatever the JavaScript inside says.
    [GeneratedRegex(@"<script\b(?<attrs>[^>]*)>(?<body>.*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"\bsrc\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SourceAttribute();

    [GeneratedRegex(@"\btype\s*=\s*(?:""(?<type>[^""]*)""|'(?<type>[^']*)'|(?<type>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex TypeAttribute();
}
