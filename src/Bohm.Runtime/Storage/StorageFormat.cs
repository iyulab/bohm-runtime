using System.Text.Encodings.Web;
using System.Text.Json;

namespace Bohm.Runtime.Storage;

/// <summary>
/// The on-disk encoding of snapshots and journal lines. Internal and versioned: the snapshot
/// carries <see cref="SnapshotFormat"/>, and a change to either encoding bumps it.
/// </summary>
internal static class StorageFormat
{
    public const string SnapshotFormat = "bohm.storage/0";

    // Snapshots are meant to be read by people, so non-ASCII text is written as-is rather than
    // escaped. These files are never served to a browser, so HTML-sensitive escaping buys nothing.
    private static readonly JsonWriterOptions SnapshotWriter = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonWriterOptions JournalWriter = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] WriteSnapshot(long sequence, IReadOnlyDictionary<string, string> items)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, SnapshotWriter))
        {
            writer.WriteStartObject();
            writer.WriteString("format", SnapshotFormat);
            writer.WriteNumber("seq", sequence);
            writer.WriteStartObject("items");
            foreach (var key in items.Keys.Order(StringComparer.Ordinal))
                writer.WriteString(key, items[key]);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Reads a snapshot, or returns <see langword="false"/> if the bytes are not one.</summary>
    public static bool TryReadSnapshot(ReadOnlySpan<byte> bytes, out long sequence, out Dictionary<string, string> items)
    {
        sequence = 0;
        items = new(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(bytes);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.GetString() != SnapshotFormat
                || !root.TryGetProperty("seq", out var seq) || !seq.TryGetInt64(out sequence)
                || !root.TryGetProperty("items", out var entries) || entries.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var entry in entries.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String) return false;
                items[entry.Name] = entry.Value.GetString()!;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static void WriteJournalLine(Stream output, long sequence, StorageOperation operation)
    {
        using (var writer = new Utf8JsonWriter(output, JournalWriter))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", sequence);
            switch (operation.Kind)
            {
                case StorageOperationKind.Set:
                    writer.WriteString("op", "set");
                    writer.WriteString("key", operation.Key);
                    writer.WriteString("value", operation.Value);
                    break;
                case StorageOperationKind.Remove:
                    writer.WriteString("op", "remove");
                    writer.WriteString("key", operation.Key);
                    break;
                case StorageOperationKind.Clear:
                    writer.WriteString("op", "clear");
                    break;
            }

            writer.WriteEndObject();
        }

        output.WriteByte((byte)'\n');
    }

    /// <summary>Reads one complete journal line, or returns <see langword="false"/> if it is not one.</summary>
    public static bool TryReadJournalLine(ReadOnlySpan<byte> line, out long sequence, out StorageOperation? operation)
    {
        sequence = 0;
        operation = null;
        try
        {
            var reader = new Utf8JsonReader(line);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("seq", out var seq) || !seq.TryGetInt64(out sequence)
                || !root.TryGetProperty("op", out var op))
                return false;

            operation = op.GetString() switch
            {
                "set" when root.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                        && root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                    => StorageOperation.Set(key.GetString()!, value.GetString()!),
                "remove" when root.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                    => StorageOperation.Remove(key.GetString()!),
                "clear" => StorageOperation.Clear(),
                _ => null,
            };
            return operation is not null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}

