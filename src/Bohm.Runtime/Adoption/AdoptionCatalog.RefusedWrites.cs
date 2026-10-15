using System.Text.Json;
using LocalOrigin.Storage;

namespace Bohm.Runtime.Adoption;

public sealed partial class AdoptionCatalog
{
    /// <summary>
    /// The file in an application's folder that keeps writes refused because the page that made them was still
    /// running code that had since been replaced: one JSON object per line — <c>at</c> (when it was refused),
    /// <c>op</c> (<c>set</c> · <c>remove</c> · <c>clear</c>), <c>key</c>, <c>value</c>.
    /// </summary>
    public const string RefusedWritesFile = "refused-writes.ndjson";

    private readonly Lock _refusedWrites = new();

    /// <summary>
    /// Keeps <paramref name="operations"/> beside application <paramref name="id"/>: writes the person made that were
    /// refused, not applied. They are not the application's data — the new code owns that — but they are not thrown
    /// away either; returns the file they were added to. On the device when this returns.
    /// </summary>
    public Task<string> KeepRefusedWritesAsync(string id, IReadOnlyList<KeyValueOperation> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var path = Path.Combine(AppDirectory(id), RefusedWritesFile);
        if (operations.Count == 0) return Task.FromResult(path);

        var at = _clock.GetUtcNow();
        using var lines = new MemoryStream();
        foreach (var operation in operations)
        {
            using (var writer = new Utf8JsonWriter(lines))
            {
                writer.WriteStartObject();
                writer.WriteString("at", at);
                writer.WriteString("op", operation.Kind switch
                {
                    KeyValueOperationKind.Set => "set",
                    KeyValueOperationKind.Remove => "remove",
                    _ => "clear",
                });
                if (operation.Key is not null) writer.WriteString("key", operation.Key);
                if (operation.Value is not null) writer.WriteString("value", operation.Value);
                writer.WriteEndObject();
            }

            lines.WriteByte((byte)'\n');
        }

        cancellationToken.ThrowIfCancellationRequested();
        // A few lines at a time, rarely: written under a lock so two refused pages never interleave their lines.
        lock (_refusedWrites)
        {
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            file.Write(lines.GetBuffer().AsSpan(0, (int)lines.Length));
            file.Flush(flushToDisk: true);
        }

        return Task.FromResult(path);
    }
}
