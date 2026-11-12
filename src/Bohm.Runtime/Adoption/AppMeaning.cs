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
/// One kind of record an application remembers, named for the person: the storage key its records live under, what the
/// person calls them, and what they call each field.
/// </summary>
/// <param name="Name">The storage key (the collection).</param>
/// <param name="Label">The person's name for the records.</param>
/// <param name="Fields">Field name → the person's name for it, in the order given.</param>
public sealed record RememberedKind(string Name, string Label, IReadOnlyList<KeyValuePair<string, string>> Fields)
{
    /// <summary>The most kinds of record kept, and the most fields kept for one.</summary>
    public const int MaxCollections = 32, MaxFields = 64;

    /// <summary>The longest name or label kept, in characters.</summary>
    public const int MaxText = 200;

    /// <summary>
    /// The collections given, cleaned for keeping: trimmed, each name once, nothing empty or over <see cref="MaxText"/>,
    /// at most <see cref="MaxCollections"/> of them and <see cref="MaxFields"/> fields each.
    /// </summary>
    public static IReadOnlyList<RememberedKind> Clean(IEnumerable<RememberedKind>? given)
    {
        static string? Text(string? s) => s?.Trim() is { Length: > 0 and <= MaxText } t ? t : null;
        var kept = new List<RememberedKind>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in given ?? [])
        {
            if (kept.Count == MaxCollections) break;
            if (c is null || Text(c.Name) is not { } name || Text(c.Label) is not { } label || !names.Add(name)) continue;
            var fields = new List<KeyValuePair<string, string>>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in c.Fields ?? [])
            {
                if (fields.Count == MaxFields) break;
                if (Text(key) is { } k && Text(value) is { } v && keys.Add(k)) fields.Add(new(k, v));
            }

            kept.Add(new RememberedKind(name, label, fields));
        }

        return kept;
    }
}

/// <summary>
/// One condition the application must keep through every change — said in the person's words, held by a judge that checks
/// the application against it each time it is changed.
/// </summary>
/// <param name="Says">The condition, one line in the person's words («a loan keeps the date it was lent»).</param>
/// <param name="From">Where it came from: <c>request</c> (the first request said it), <c>asked</c> (the model asked about a boundary
/// and the person said yes) or <c>person</c> (the person added it).</param>
/// <param name="Field">The field it is about, when it is about one (a field key of a remembered kind).</param>
public sealed record AppCheck(string Says, string From, string? Field = null)
{
    /// <summary>Where a condition comes from.</summary>
    public static readonly IReadOnlySet<string> Sources = new HashSet<string>(["request", "asked", "person"], StringComparer.Ordinal);

    /// <summary>The most conditions kept, and the longest one in characters.</summary>
    public const int MaxChecks = 32, MaxText = 300;

    /// <summary>
    /// The conditions given, cleaned for keeping: trimmed, each said once, nothing empty, over <see cref="MaxText"/> or from
    /// an unknown source, a field only when it is a short name, at most <see cref="MaxChecks"/>.
    /// </summary>
    public static IReadOnlyList<AppCheck> Clean(IEnumerable<AppCheck>? given)
    {
        var kept = new List<AppCheck>();
        var said = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in given ?? [])
        {
            if (kept.Count == MaxChecks) break;
            if (c?.Says?.Trim() is not { Length: > 0 and <= MaxText } says || c.From is null || !Sources.Contains(c.From) || !said.Add(says)) continue;
            var field = c.Field?.Trim() is { Length: > 0 and <= RememberedKind.MaxText } f ? f : null;
            kept.Add(new AppCheck(says, c.From, field));
        }

        return kept;
    }
}

/// <summary>The person's names (<see cref="RememberedKind"/>) laid on the tables an application's records make.</summary>
public static class PersonNames
{
    /// <summary>The kind kept for <paramref name="collection"/> (its storage key), if any.</summary>
    public static RememberedKind? For(IReadOnlyList<RememberedKind> kinds, string collection) =>
        kinds.FirstOrDefault(k => k.Name == collection);

    /// <summary>
    /// <paramref name="declaration"/> with the person's name of each field as one more header that means it — so a table written
    /// out with those names (<see cref="TableImports.TableExport"/>) comes back in to the same fields.
    /// </summary>
    public static TableImports.ImportDeclaration Recall(TableImports.ImportDeclaration declaration, IReadOnlyList<RememberedKind> kinds)
    {
        if (For(kinds, declaration.Collection) is not { } kind) return declaration;
        var labels = kind.Fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        return declaration with
        {
            Fields = [.. declaration.Fields.Select(f => labels.TryGetValue(f.Name, out var label) && label != f.Name ? f with { Aliases = [.. f.Aliases ?? [], label] } : f)],
        };
    }

    /// <summary>The header for each of <paramref name="fields"/>: the person's name where one is kept, the field's own name otherwise — each header once.</summary>
    public static IReadOnlyList<string> Headers(IReadOnlyList<string> fields, RememberedKind? kind)
    {
        var labels = kind?.Fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal) ?? [];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headers = new List<string>(fields.Count);
        foreach (var field in fields)
        {
            // Two fields the person named alike, or a name equal to another field's own name, would read back into one field: keep the field's name then.
            var header = labels.TryGetValue(field, out var label) && !fields.Contains(label, StringComparer.OrdinalIgnoreCase) && used.Add(label) ? label : field;
            if (header == field) used.Add(field);
            headers.Add(header);
        }

        return headers;
    }
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

    /// <summary>A new file holding <paramref name="request"/>, <paramref name="collections"/> and <paramref name="checks"/> — any may be absent; <see langword="null"/> when all are.</summary>
    public static byte[]? Write(FirstRequest? request, IReadOnlyList<RememberedKind>? collections = null, IReadOnlyList<AppCheck>? checks = null)
    {
        if (request is null && collections is not { Count: > 0 } && checks is not { Count: > 0 }) return null;
        var node = new JsonObject { ["format"] = Format };
        if (request is not null)
            node["request"] = new JsonObject
            {
                ["text"] = request.Text,
                ["at"] = request.At.ToString("O", CultureInfo.InvariantCulture),
                ["via"] = request.Via,
            };
        if (collections is { Count: > 0 }) node["collections"] = CollectionsNode(collections);
        if (checks is { Count: > 0 }) node["checks"] = ChecksNode(checks);
        return JsonSerializer.SerializeToUtf8Bytes(node, Writer);
    }

    private static JsonArray CollectionsNode(IReadOnlyList<RememberedKind> collections)
    {
        var array = new JsonArray();
        foreach (var c in collections)
        {
            var fields = new JsonObject();
            foreach (var (key, label) in c.Fields) fields[key] = label;
            array.Add(new JsonObject { ["name"] = c.Name, ["label"] = c.Label, ["fields"] = fields });
        }

        return array;
    }

    /// <summary>The collections in the file at <paramref name="folder"/> — empty when there are none or it cannot be read.</summary>
    public static async Task<IReadOnlyList<RememberedKind>> ReadCollectionsAsync(string folder, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return [];
        try
        {
            return ParseCollections(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return [];
        }
    }

    /// <summary>The collections in <paramref name="file"/>'s bytes, cleaned (<see cref="RememberedKind.Clean"/>) — empty when not of this format.</summary>
    public static IReadOnlyList<RememberedKind> ParseCollections(ReadOnlySpan<byte> file)
    {
        if (JsonNode.Parse(file) is not JsonObject root || root["format"]?.GetValueKind() != JsonValueKind.String
            || !IsThisFormat(root["format"]!.GetValue<string>()) || root["collections"] is not JsonArray array) return [];
        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        var given = array.OfType<JsonObject>().Select(c => new RememberedKind(Str(c["name"]) ?? "", Str(c["label"]) ?? "",
            c["fields"] is JsonObject f ? [.. f.Select(m => new KeyValuePair<string, string>(m.Key, Str(m.Value) ?? ""))] : []));
        return RememberedKind.Clean(given);
    }

    private static JsonArray ChecksNode(IReadOnlyList<AppCheck> checks)
    {
        var array = new JsonArray();
        foreach (var c in checks)
        {
            var node = new JsonObject { ["says"] = c.Says, ["from"] = c.From };
            if (c.Field is not null) node["field"] = c.Field;
            array.Add(node);
        }

        return array;
    }

    /// <summary>The conditions in the file at <paramref name="folder"/> — empty when there are none or it cannot be read.</summary>
    public static async Task<IReadOnlyList<AppCheck>> ReadChecksAsync(string folder, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return [];
        try
        {
            return ParseChecks(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return [];
        }
    }

    /// <summary>The conditions in <paramref name="file"/>'s bytes, cleaned (<see cref="AppCheck.Clean"/>) — empty when not of this format.</summary>
    public static IReadOnlyList<AppCheck> ParseChecks(ReadOnlySpan<byte> file)
    {
        if (JsonNode.Parse(file) is not JsonObject root || root["format"]?.GetValueKind() != JsonValueKind.String
            || !IsThisFormat(root["format"]!.GetValue<string>()) || root["checks"] is not JsonArray array) return [];
        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        return AppCheck.Clean(array.OfType<JsonObject>().Select(c => new AppCheck(Str(c["says"]) ?? "", Str(c["from"]) ?? "", Str(c["field"]))));
    }

    /// <summary>
    /// Sets the conditions the file in <paramref name="folder"/> holds to <paramref name="checks"/> (cleaned), keeping everything
    /// else it holds. A file left with nothing but its format is removed; none is made for no conditions.
    /// </summary>
    public static Task SetChecksAsync(string folder, IReadOnlyList<AppCheck> checks, CancellationToken cancellationToken = default) =>
        SetMemberAsync(folder, "checks", AppCheck.Clean(checks) is { Count: > 0 } kept ? ChecksNode(kept) : null, cancellationToken);

    private static async Task SetMemberAsync(string folder, string member, JsonNode? value, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, FileName);
        JsonObject root;
        try
        {
            root = File.Exists(path) && JsonNode.Parse(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false)) is JsonObject read ? read : new JsonObject { ["format"] = Format };
        }
        catch (JsonException)
        {
            root = new JsonObject { ["format"] = Format };
        }

        if (value is not null) root[member] = value;
        else root.Remove(member);
        if (root.All(m => m.Key == "format"))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        await DurableFile.WriteAtomicallyAsync(path, JsonSerializer.SerializeToUtf8Bytes(root, Writer), cancellationToken).ConfigureAwait(false);
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
    /// Sets what the file in <paramref name="folder"/> says the application remembers to <paramref name="collections"/>, keeping
    /// everything else it holds (the request, members of a later format). A file left with nothing but its format is removed;
    /// none is made for no collections.
    /// </summary>
    public static Task SetCollectionsAsync(string folder, IReadOnlyList<RememberedKind> collections, CancellationToken cancellationToken = default) =>
        SetMemberAsync(folder, "collections", collections.Count > 0 ? CollectionsNode(collections) : null, cancellationToken);

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

        // The conditions are said in the person's words too (they come from the request, the person's yes, or the person).
        var removed = root.Remove("request") | root.Remove("checks");
        if (!removed) return;
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
