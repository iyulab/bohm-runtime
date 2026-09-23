using System.Globalization;

namespace Bohm.Runtime.Storage;

/// <summary>File operations whose completion means the bytes have reached the storage device.</summary>
internal static class DurableFile
{
    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="contents"/> so that a crash leaves
    /// either the old file or the complete new one, never a partial file: the bytes are written to
    /// a sibling temporary file, flushed to the device, then renamed over the destination.
    /// </summary>
    public static async Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Renames an unreadable file out of the way instead of deleting it, so whatever it holds can
    /// still be recovered by hand. Returns the new path.
    /// </summary>
    public static string SetAside(string path, TimeProvider clock)
    {
        var stamp = clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var aside = $"{path}.unreadable-{stamp}";
        File.Move(path, aside);
        return aside;
    }
}
