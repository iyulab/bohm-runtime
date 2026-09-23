using System.Text;
using System.Text.RegularExpressions;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Places the compatibility script in front of an adopted document's own content, without
/// altering that content. The stored bytes are never modified; this produces the served copy.
/// </summary>
internal static partial class ShimInjector
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Returns the document to serve and the charset to declare for it. The charset is the one the
    /// document declares for itself, or UTF-8 when it declares none — the injected script comes
    /// first and can push a late <c>&lt;meta charset&gt;</c> past the 1024 bytes browsers scan, so the
    /// response header has to carry the document's own choice.
    /// </summary>
    public static (byte[] Body, string Charset) Inject(ReadOnlySpan<byte> document, string script)
    {
        if (script.Any(c => c > 0x7F)) throw new ArgumentException("The injected script must be ASCII.", nameof(script));
        var tag = "<script>" + script + "</script>";

        if (document.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || document.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            // UTF-16 is not ASCII-compatible, so the script cannot be spliced in as bytes.
            var encoding = document[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            var text = encoding.GetString(document[2..]);
            var at = InsertionPoint(text);
            var served = encoding.GetPreamble().Concat(encoding.GetBytes(text.Insert(at, tag))).ToArray();
            return (served, encoding == Encoding.Unicode ? "utf-16le" : "utf-16be");
        }

        var start = document.StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
        var head = Encoding.Latin1.GetString(document[start..Math.Min(document.Length, start + 4096)]);
        var offset = start + InsertionPoint(head);

        var body = new byte[document.Length + tag.Length];
        document[..offset].CopyTo(body);
        Encoding.ASCII.GetBytes(tag, body.AsSpan(offset));
        document[offset..].CopyTo(body.AsSpan(offset + tag.Length));

        var charset = start > 0 ? "utf-8" : DeclaredCharset(Encoding.Latin1.GetString(document[..Math.Min(document.Length, 1024)])) ?? "utf-8";
        return (body, charset);
    }

    /// <summary>
    /// Where the script can go without changing how the document is parsed: after leading
    /// whitespace, comments and the doctype. Anything placed before a doctype would switch the
    /// page into quirks mode.
    /// </summary>
    private static int InsertionPoint(string text)
    {
        var position = 0;
        while (true)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            if (string.CompareOrdinal(text, position, "<!--", 0, 4) == 0)
            {
                var end = text.IndexOf("-->", position + 4, StringComparison.Ordinal);
                if (end < 0) return position;
                position = end + 3;
                continue;
            }

            if (position + 9 <= text.Length && text.AsSpan(position, 9).Equals("<!doctype", StringComparison.OrdinalIgnoreCase))
            {
                var end = text.IndexOf('>', position);
                return end < 0 ? position : end + 1;
            }

            return position;
        }
    }

    private static string? DeclaredCharset(string prefix)
    {
        var match = MetaCharset().Match(prefix);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex("""<meta[^>]*?charset\s*=\s*["']?\s*([A-Za-z0-9_\-:.]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharset();
}
