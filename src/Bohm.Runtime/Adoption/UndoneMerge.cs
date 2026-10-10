using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bohm.Runtime.Adoption;

/// <summary>
/// A three-way merge of an application's stored data: the data a revision found when it was taken in (the base), what
/// it had written when the application was put back from it (kept aside), and the data now — written on since going
/// back. Key by key, a side that left a value as the base had it takes the other side's; where both changed it, a value
/// that is JSON is merged the same way one level down — an object property by property, a list of objects that each
/// carry a unique scalar <c>id</c> record by record (a record missing from a side was removed there). Anything else
/// changed on both sides is a conflict and nothing is merged: the kept data stays a file, as before.
/// </summary>
/// <remarks>
/// No names are known — not of keys, not of fields, not of kinds of application: only that both sides changed
/// different things. Equal means equal as JSON (property order and spacing aside); a key no merge was needed for keeps
/// its stored text byte for byte, and a merged one is written compactly with its letters as they are, the way an
/// application's own <c>JSON.stringify</c> writes them. The same three inputs always give the same result.
/// </remarks>
internal static class UndoneMerge
{
    /// <summary>The merged data, and how many changes it took in from the kept side (a key, a property or a record each).</summary>
    internal sealed record Result(IReadOnlyDictionary<string, string> Items, int Incoming);

    private static readonly JsonSerializerOptions Written = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The merge of <paramref name="kept"/> into <paramref name="now"/> over <paramref name="based"/>; <see langword="null"/> on a conflict.</summary>
    public static Result? Merge(IReadOnlyDictionary<string, string> based, IReadOnlyDictionary<string, string> kept, IReadOnlyDictionary<string, string> now)
    {
        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        var incoming = 0;
        foreach (var key in now.Keys.Concat(kept.Keys).Concat(based.Keys).Distinct(StringComparer.Ordinal))
        {
            var b = based.GetValueOrDefault(key);
            var k = kept.GetValueOrDefault(key);
            var n = now.GetValueOrDefault(key);
            string? merged;
            if (SameText(k, b)) merged = n;
            else if (SameText(n, b))
            {
                merged = k;
                incoming++;
            }
            else if (SameText(k, n)) merged = n;
            else if (b is null || k is null || n is null
                || !TryParse(b, out var bn) || !TryParse(k, out var kn) || !TryParse(n, out var nn)
                || !TryMerge(new Slot(bn), new Slot(kn), new Slot(nn), out var node, ref incoming))
                return null;
            else merged = node.Node?.ToJsonString(Written) ?? "null";

            if (merged is not null) items[key] = merged;
        }

        return new Result(items, incoming);
    }

    /// <summary>A value at one place, or its absence there (<see cref="Present"/> false) — JSON <c>null</c> is a value.</summary>
    private readonly record struct Slot(JsonNode? Node, bool Present = true)
    {
        public static readonly Slot Absent = new(null, false);
    }

    private static bool TryMerge(Slot based, Slot kept, Slot now, out Slot merged, ref int incoming)
    {
        merged = now;
        if (Same(kept, based)) return true;
        if (Same(now, based))
        {
            merged = kept;
            incoming++;
            return true;
        }

        if (Same(kept, now)) return true;
        if (!based.Present || !kept.Present || !now.Present) return false;

        if (based.Node is JsonObject bo && kept.Node is JsonObject ko && now.Node is JsonObject no)
        {
            var result = new JsonObject();
            foreach (var name in no.Select(p => p.Key).Concat(ko.Select(p => p.Key)).Concat(bo.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
            {
                if (!TryMerge(At(bo, name), At(ko, name), At(no, name), out var property, ref incoming)) return false;
                if (property.Present) result[name] = property.Node?.DeepClone();
            }

            merged = new Slot(result);
            return true;
        }

        if (based.Node is JsonArray ba && kept.Node is JsonArray ka && now.Node is JsonArray na
            && Records(ba) is { } bRecords && Records(ka) is { } kRecords && Records(na) is { } nRecords)
        {
            var result = new JsonArray();
            // The order now, then records only the kept side added, in its order.
            foreach (var id in nRecords.Keys.Concat(kRecords.Keys).Concat(bRecords.Keys).Distinct(StringComparer.Ordinal))
            {
                if (!TryMerge(Find(bRecords, id), Find(kRecords, id), Find(nRecords, id), out var record, ref incoming)) return false;
                if (record.Present) result.Add(record.Node?.DeepClone());
            }

            merged = new Slot(result);
            return true;
        }

        return false;

        static Slot At(JsonObject o, string name) => o.TryGetPropertyValue(name, out var v) ? new Slot(v) : Slot.Absent;
        static Slot Find(Dictionary<string, JsonNode> records, string id) => records.TryGetValue(id, out var v) ? new Slot(v) : Slot.Absent;
    }

    /// <summary>
    /// The list's records by their <c>id</c> (its JSON text, so <c>"1"</c> and <c>1</c> differ), in list order — or
    /// <see langword="null"/> when an element is not an object, has no scalar string or number <c>id</c>, or shares one.
    /// </summary>
    private static Dictionary<string, JsonNode>? Records(JsonArray list)
    {
        var records = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var element in list)
        {
            if (element is not JsonObject o || !o.TryGetPropertyValue("id", out var id) || id is not JsonValue value
                || value.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number)
                || !records.TryAdd(value.ToJsonString(), o))
                return null;
        }

        return records;
    }

    private static bool Same(Slot a, Slot b) => a.Present == b.Present && (!a.Present || JsonNode.DeepEquals(a.Node, b.Node));

    private static bool SameText(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        return TryParse(a, out var an) && TryParse(b, out var bn) && JsonNode.DeepEquals(an, bn);
    }

    private static bool TryParse(string text, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }
}
