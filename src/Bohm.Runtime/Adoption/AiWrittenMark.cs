using System.Text;
using System.Text.RegularExpressions;

namespace Bohm.Runtime.Adoption;

/// <summary>
/// The machine-readable mark an application's code carries when a model wrote it: two <c>meta</c> elements at the start of its
/// <c>head</c> — the IPTC digital source type for content made by a trained model, and a <c>generator</c> naming the browser.
/// The runtime writes it when it keeps a version a model wrote (made, kept from an answer, or edited), so a model that leaves it
/// out or removes it does not decide whether it is there; a version that already carries it is kept as it is. Code a person
/// brought in, and a version brought back from the history, are kept as they were.
/// </summary>
public static partial class AiWrittenMark
{
    /// <summary>The IPTC NewsCodes digital source type for media created by a trained algorithmic model.</summary>
    public const string DigitalSourceType = "http://cv.iptc.org/newscodes/digitalsourcetype/trainedAlgorithmicMedia";

    /// <summary>The elements written.</summary>
    public const string Elements =
        "<meta name=\"digital-source-type\" content=\"" + DigitalSourceType + "\">" +
        "<meta name=\"generator\" content=\"Bohm (AI-generated)\">";

    /// <summary>
    /// <paramref name="html"/> with the mark: right after the <c>head</c> start tag, else after the <c>html</c> start tag, else
    /// after the doctype, else at the very start (where a parser puts a <c>meta</c> into the head it makes). Unchanged when it
    /// already names the digital source type.
    /// </summary>
    public static ReadOnlyMemory<byte> Into(ReadOnlyMemory<byte> html)
    {
        var text = Encoding.UTF8.GetString(html.Span);
        if (Carries(text)) return html;
        var at = Head().Match(text) is { Success: true } head ? head.Index + head.Length
            : Html().Match(text) is { Success: true } root ? root.Index + root.Length
            : Doctype().Match(text) is { Success: true } doctype ? doctype.Index + doctype.Length
            : text.StartsWith('﻿') ? 1 : 0;
        return Encoding.UTF8.GetBytes(text.Insert(at, Elements));
    }

    /// <summary>Whether <paramref name="html"/> names the digital source type of model-made content.</summary>
    public static bool Carries(string html) => Marked().IsMatch(html);

    [GeneratedRegex(@"<head(?:\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Head();

    [GeneratedRegex(@"<html(?:\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Html();

    [GeneratedRegex(@"^﻿?\s*<!doctype[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Doctype();

    [GeneratedRegex(@"<meta\s[^>]*name\s*=\s*[""']?digital-source-type[""']?[^>]*trainedAlgorithmicMedia", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Marked();
}
