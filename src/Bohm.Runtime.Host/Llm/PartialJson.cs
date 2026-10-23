using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// Reads a model's JSON answer as far as it goes. An answer that reached its length limit is a JSON value cut off in the
/// middle — read whole, it is nothing, and every item the model finished is thrown away with the one it did not. Asked
/// for a list, the items written before the cut are still the answer, so they are kept.
/// </summary>
internal static class PartialJson
{
    /// <summary>
    /// The complete elements of the array under <paramref name="property"/> of the first JSON object in
    /// <paramref name="text"/> — all of them when the answer is whole, the ones before the cut when it is not. Empty when
    /// there is no such object or array. Text around the object (a sentence before it, say) is passed over.
    /// </summary>
    public static IReadOnlyList<JsonElement> ArrayItems(string? text, string property)
    {
        var items = new List<JsonElement>();
        var start = text?.IndexOf('{') ?? -1;
        if (start < 0) return items;
        var bytes = Encoding.UTF8.GetBytes(text![start..]);
        // Not the final block: a value the answer did not finish is «more to come», not an error.
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return items;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var found = reader.ValueTextEquals(property);
                if (!reader.Read()) return items;
                if (!found || reader.TokenType != JsonTokenType.StartArray)
                {
                    if (found || !reader.TrySkip()) return items;
                    continue;
                }
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (!JsonDocument.TryParseValue(ref reader, out var item)) return items;
                    using (item) items.Add(item.RootElement.Clone());
                }
                return items;
            }
        }
        catch (JsonException)
        {
            // Not JSON past this point — what was read before it stands.
        }
        return items;
    }
}
