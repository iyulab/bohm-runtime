using System.Globalization;
using System.Text;

namespace Bohm.Runtime.Host.Edit;

/// <summary>
/// The text of one string field of a tool call's arguments, read while the model writes them: the arguments
/// arrive as pieces of one JSON object (the provider's raw partial JSON), and each piece gives back what it
/// added to that field, unescaped — a long field (an application's HTML) can be shown as it is written.
/// Only a field of the top-level object is read; a field of the same name deeper in is not.
/// </summary>
internal sealed class ToolArgumentText(string field)
{
    private readonly StringBuilder _key = new();
    private readonly StringBuilder _hex = new();
    private int _depth;
    private bool _inString;
    private bool _escaped;
    private bool _readingKey;
    private bool _expectKey;
    private bool _inField;
    private char? _highSurrogate;

    /// <summary>Takes the next piece of the arguments and returns what it added to the field — empty when nothing.</summary>
    public string Add(string piece)
    {
        var added = new StringBuilder();
        foreach (var c in piece)
        {
            if (_inString) { ReadInString(c, added); continue; }
            switch (c)
            {
                case '{' or '[':
                    _depth++;
                    _expectKey = c == '{' && _depth == 1;
                    break;
                case '}' or ']':
                    _depth--;
                    break;
                case ',' when _depth == 1:
                    _expectKey = true;
                    break;
                case '"':
                    _inString = true;
                    _readingKey = _depth == 1 && _expectKey;
                    _inField = _depth == 1 && !_expectKey && _key.ToString() == field;
                    if (_readingKey) _key.Clear();
                    break;
                case ':' when _depth == 1:
                    _expectKey = false;
                    break;
            }
        }

        return added.ToString();
    }

    private void ReadInString(char c, StringBuilder added)
    {
        if (_hex.Length > 0 || (_escaped && c == 'u'))
        {
            if (_escaped) { _escaped = false; _hex.Append('u'); return; }
            _hex.Append(c);
            if (_hex.Length < 5) return;
            var unit = (char)int.Parse(_hex.ToString(1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            _hex.Clear();
            Take(unit, added);
            return;
        }

        if (_escaped)
        {
            _escaped = false;
            Take(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => c }, added);
            return;
        }

        switch (c)
        {
            case '\\':
                _escaped = true;
                return;
            case '"':
                _inString = false;
                _inField = false;
                _readingKey = false;
                return;
            default:
                Take(c, added);
                return;
        }
    }

    private void Take(char c, StringBuilder added)
    {
        if (_readingKey) { _key.Append(c); return; }
        if (!_inField) return;
        // A pair split by a piece's end is given back whole, so no piece ends in half a character.
        if (char.IsHighSurrogate(c)) { _highSurrogate = c; return; }
        if (_highSurrogate is { } high) { added.Append(high); _highSurrogate = null; }
        added.Append(c);
    }
}
