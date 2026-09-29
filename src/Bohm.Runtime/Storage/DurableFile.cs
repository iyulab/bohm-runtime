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

        // Windows refuses to rename over a file while anyone has it open — here, usually another request
        // reading the same record for a few milliseconds. Wait for the read to end rather than fail the write.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException && attempt < ReplaceAttempts && File.Exists(temporary))
            {
                await Task.Delay(ReplaceRetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private const int ReplaceAttempts = 10;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(20);   // 20 ms, 40 ms, … — under a second in all

    /// <summary>
    /// Reads a file that may be written or replaced at the same moment, sharing every kind of access so
    /// the read never makes another opener fail. A replacement waits for the read to end (see
    /// <see cref="WriteAtomicallyAsync"/>); the reader keeps the bytes of whichever file it opened.
    /// </summary>
    public static async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenShared(path);
        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Opens <paramref name="path"/> for reading, sharing every kind of access (see <see cref="ReadAsync"/>).</summary>
    public static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

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
