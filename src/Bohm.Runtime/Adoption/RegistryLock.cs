using System.Diagnostics;
using System.Text.Json;

namespace Bohm.Runtime.Adoption;

/// <summary>How often a registry lock's holder rewrites it, how long an unchanged one is waited for before it is taken over, and how long to wait in all.</summary>
internal sealed record RegistryLockTiming(TimeSpan Beat, TimeSpan Stale, TimeSpan Wait)
{
    public static RegistryLockTiming Default { get; } = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
}

/// <summary>
/// The lock a publisher holds while it rewrites a folder registry's index — on a local drive or a share, from any
/// operating system. Only one creator of the file wins. Its holder writes an owner mark into it and rewrites the mark
/// every <see cref="RegistryLockTiming.Beat"/>, so a lock is alive while its mark keeps changing — judged by the waiter's
/// own clock watching it, never by comparing file times with another computer's clock — and a mark that has not changed
/// for <see cref="RegistryLockTiming.Stale"/> was left by a publisher that stopped and is taken over. The holder removes
/// the file only while it still carries its own mark.
/// </summary>
/// <remarks>
/// A lock with no mark was written by an earlier version, which relied on Windows refusing to delete a file its holder
/// has open. It is treated the old way: on Windows, one that has not been written for two minutes is deleted if Windows
/// allows it; elsewhere, where an open file can be deleted, it is waited for and never taken over.
/// </remarks>
internal sealed class RegistryLock : IAsyncDisposable
{
    private const string Format = "bohm.registry.lock/1";

    /// <summary>An unmarked lock older than this, by this computer's clock against the file's time, was left by an earlier version that stopped.</summary>
    private static readonly TimeSpan UnmarkedStale = TimeSpan.FromMinutes(2);

    private readonly string _path;
    private readonly string _owner;
    private readonly FileStream _stream;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _beating;
    private long _beat;

    private RegistryLock(string path, string owner, FileStream stream, TimeSpan beat)
    {
        _path = path;
        _owner = owner;
        _stream = stream;
        Write();
        _beating = BeatAsync(beat);
    }

    /// <summary>Takes the lock at <paramref name="path"/>, waiting for a holder and taking over one that stopped.</summary>
    /// <exception cref="RegistryRefusedException">Another publisher held it for the whole wait (<see cref="RegistryRefusedException.Busy"/>).</exception>
    public static async Task<RegistryLock> TakeAsync(string path, RegistryLockTiming timing, TimeProvider clock, CancellationToken cancellationToken)
    {
        var owner = $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid():n}";
        var waited = Stopwatch.StartNew();
        string? seen = null;
        var seenFor = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // Others may read the mark, and may move or delete the file once it is stale.
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 1);
                return new RegistryLock(path, owner, stream, timing.Beat);
            }
            // A lock its holder has just closed is on its way out, and until it is gone Windows refuses to create it again
            // with "access denied", not "exists" — the same wait as a held lock. (Reaching here, this computer has already
            // written the package to the folder, so a denial is not a folder it may not write to.)
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (waited.Elapsed > timing.Wait) throw new RegistryRefusedException(RegistryRefusedException.Busy);
                if (!File.Exists(path)) continue;   // released between the two looks

                var mark = ReadMark(path);
                if (mark is null)
                {
                    TakeOverUnmarked(path, clock);
                }
                else if (mark != seen)
                {
                    seen = mark;
                    seenFor.Restart();
                }
                else if (seenFor.Elapsed > timing.Stale && TakeOver(path, mark))
                {
                    seen = null;
                    continue;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The mark a lock carries, or <see langword="null"/> for one with none — an earlier version's, or one whose creator has not written it yet.</summary>
    private static string? ReadMark(string path)
    {
        try
        {
            using var reading = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var json = JsonDocument.Parse(reading);
            return json.RootElement.TryGetProperty("format", out var format) && format.GetString() == Format
                && json.RootElement.TryGetProperty("owner", out var owner) && owner.GetString() is { Length: > 0 }
                ? json.RootElement.GetRawText()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TakeOverUnmarked(string path, TimeProvider clock)
    {
        // The time is the share's clock against this computer's, so a share whose clock runs behind makes a held lock
        // look stale: Windows then refuses the delete, and that is waited out like any held lock.
        if (!OperatingSystem.IsWindows() || clock.GetUtcNow() - File.GetLastWriteTimeUtc(path) <= UnmarkedStale) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still held open, or already on its way out: not stale after all.
        }
    }

    /// <summary>
    /// Moves a stale lock aside — a move only one waiter can make — and removes it if it is still the one watched. One
    /// rewritten in between is put back.
    /// </summary>
    private static bool TakeOver(string path, string mark)
    {
        var aside = path + ".stale-" + Guid.NewGuid().ToString("n")[..8];
        try
        {
            File.Move(path, aside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (ReadMark(aside) == mark)
        {
            File.Delete(aside);
            return true;
        }

        try
        {
            File.Move(aside, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Delete(aside);   // a newer holder already made the lock again
        }

        return false;
    }

    private void Write()
    {
        _stream.SetLength(0);
        using (var json = new Utf8JsonWriter(_stream))
        {
            json.WriteStartObject();
            json.WriteString("format", Format);
            json.WriteString("owner", _owner);
            json.WriteNumber("beat", _beat++);
            json.WriteEndObject();
        }

        _stream.Flush(flushToDisk: true);
    }

    private async Task BeatAsync(TimeSpan beat)
    {
        using var timer = new PeriodicTimer(beat);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false)) Write();
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The share went away: the lock goes stale and the read-back after the index write tells the publisher.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _beating.ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        if (ReadMark(_path) is { } mark && JsonDocument.Parse(mark).RootElement.GetProperty("owner").GetString() == _owner)
        {
            try
            {
                File.Delete(_path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Someone reading the mark at this moment: it stays unchanged, so it goes stale and is taken over.
            }
        }
    }
}
