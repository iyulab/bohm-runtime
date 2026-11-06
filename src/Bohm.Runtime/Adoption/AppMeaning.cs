using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalOrigin.Storage;

namespace Bohm.Runtime.Adoption;

/// <summary>
/// How an application came to be — the words it was first asked for with.
/// </summary>
/// <param name="Text">The request as the person finally gave it.</param>
/// <param name="At">When the application was made from it.</param>
/// <param name="Via">How: <c>made</c> (asked for in words), <c>promoted</c> (a question's answer kept as an application) or <c>adopted</c> (a page brought in).</param>
public sealed record FirstRequest(string Text, DateTimeOffset At, string Via)
{
    /// <summary>The ways an application comes to be.</summary>
    public static readonly IReadOnlySet<string> Ways = new HashSet<string>(["made", "promoted", "adopted"], StringComparer.Ordinal);

    /// <summary>The longest request kept, in characters. A longer one is cut — what it was for is in its opening.</summary>
    public const int MaxLength = 8000;
}

/// <summary>
/// The file beside an application's code that says what the application means: what it was first asked for
/// (<see cref="FirstRequest"/>), and — in later versions of the format — what it remembers and the names of its fields.
/// </summary>
/// <remarks>
/// Kept apart from <c>app.json</c>: that file is the runtime's record of the application, this one is what the application
/// is for. Readers ignore members they do not know, so a later version that adds one does not break an earlier reader,
/// and a writer keeps the members it does not know.
/// </remarks>
public static class AppMeaning
{
    /// <summary>The file's name in the application folder.</summary>
    public const string FileName = "meaning.json";

    /// <summary>Format identifier written into the file.</summary>
    public const string Format = "bohm.app/1";

    private static readonly JsonSerializerOptions Writer = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A new file holding only <paramref name="request"/>.</summary>
    public static byte[] Write(FirstRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var node = new JsonObject
        {
            ["format"] = Format,
            ["request"] = new JsonObject
            {
                ["text"] = request.Text,
                ["at"] = request.At.ToString("O", CultureInfo.InvariantCulture),
                ["via"] = request.Via,
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(node, Writer);
    }

    /// <summary>
    /// The request a person asked for, cleaned for keeping: trimmed, cut to <see cref="FirstRequest.MaxLength"/>, and
    /// <see langword="null"/> when there are no words or the way is not one of <see cref="FirstRequest.Ways"/>.
    /// </summary>
    public static FirstRequest? Request(string? text, string? via, DateTimeOffset at)
    {
        var words = text?.Trim();
        if (string.IsNullOrEmpty(words) || via is null || !FirstRequest.Ways.Contains(via)) return null;
        return new FirstRequest(words.Length > FirstRequest.MaxLength ? words[..FirstRequest.MaxLength] : words, at, via);
    }

    /// <summary>The first request in the file at <paramref name="folder"/>, or <see langword="null"/> when there is none or it cannot be read.</summary>
    public static async Task<FirstRequest?> ReadRequestAsync(string folder, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            return ParseRequest(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>The first request in <paramref name="file"/>'s bytes — <see langword="null"/> when there is none, or it is not of this format.</summary>
    public static FirstRequest? ParseRequest(ReadOnlySpan<byte> file)
    {
        if (JsonNode.Parse(file) is not JsonObject root || root["format"]?.GetValueKind() != JsonValueKind.String
            || !IsThisFormat(root["format"]!.GetValue<string>())) return null;
        if (root["request"] is not JsonObject request) return null;
        var text = request["text"] is JsonValue t && t.TryGetValue(out string? s) ? s : null;
        var via = request["via"] is JsonValue v && v.TryGetValue(out string? w) ? w : null;
        var at = request["at"] is JsonValue a && a.TryGetValue(out string? when)
            && DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : (DateTimeOffset?)null;
        return at is { } time ? Request(text, via, time) : null;
    }

    /// <summary>
    /// Takes the person's words out of the file in <paramref name="folder"/> — for a copy that leaves this computer
    /// (the words a person asked with are theirs, not the receiver's). What else the file holds stays; a file left with
    /// nothing but its format is removed.
    /// </summary>
    public static async Task KeepWhatTravelsAsync(string folder, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            File.Delete(path);   // unreadable: nothing in it can be told apart from the person's words
            return;
        }

        if (node is not JsonObject root)
        {
            File.Delete(path);
            return;
        }

        if (!root.Remove("request")) return;
        if (root.All(member => member.Key == "format"))
        {
            File.Delete(path);
            return;
        }

        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(root, Writer), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="format"/> is this file's format at any version this reader can read (version 1).</summary>
    private static bool IsThisFormat(string format) => format == Format;
}
