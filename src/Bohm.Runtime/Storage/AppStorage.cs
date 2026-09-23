using System.Globalization;

namespace Bohm.Runtime.Storage;

/// <summary>
/// The durable key-value storage of one application — the on-disk counterpart of a page's
/// <c>localStorage</c>.
/// </summary>
/// <remarks>
/// <para>
/// Layout of the storage directory:
/// <c>local.json</c> is the snapshot (sorted, indented JSON a person can read and copy);
/// <c>journal.ndjson</c> holds the operations applied since that snapshot, one per line;
/// <c>versions/</c> keeps previous snapshots.
/// </para>
/// <para>
/// The journal is the write-ahead log. <see cref="ApplyAsync"/> returns only after the
/// operations are flushed to the storage device, so a returned sequence number is an
/// acknowledgement that survives a crash. Every so often the snapshot is rewritten and the journal
/// emptied; the previous snapshot is moved into <c>versions/</c> rather than deleted.
/// </para>
/// <para>
/// Loading never silently drops data it could not read: unreadable files are set aside unchanged
/// and every repair is reported in <see cref="Recovery"/>.
/// </para>
/// </remarks>
public sealed class AppStorage : IAsyncDisposable
{
    private const string SnapshotFile = "local.json";
    private const string JournalFile = "journal.ndjson";
    private const string VersionsDirectory = "versions";

    private readonly string _directory;
    private readonly StorageOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _items;
    private FileStream _journal;
    private long _snapshotSequence;
    private int _journaledSinceSnapshot;
    private bool _disposed;

    private AppStorage(string directory, StorageOptions options, Dictionary<string, string> items, long snapshotSequence,
        long sequence, int journaledSinceSnapshot, FileStream journal, IReadOnlyList<StorageRecoveryEvent> recovery)
    {
        _directory = directory;
        _options = options;
        _items = items;
        _snapshotSequence = snapshotSequence;
        Sequence = sequence;
        _journaledSinceSnapshot = journaledSinceSnapshot;
        _journal = journal;
        Recovery = recovery;
    }

    /// <summary>Sequence number of the last acknowledged operation; 0 for storage never written.</summary>
    public long Sequence { get; private set; }

    /// <summary>Repairs made while loading. Empty when the storage was found intact.</summary>
    public IReadOnlyList<StorageRecoveryEvent> Recovery { get; }

    /// <summary>Opens the storage in <paramref name="directory"/>, creating it if absent, and recovers its state.</summary>
    public static async Task<AppStorage> OpenAsync(string directory, StorageOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        options ??= new StorageOptions();
        Directory.CreateDirectory(Path.Combine(directory, VersionsDirectory));

        var recovery = new List<StorageRecoveryEvent>();
        var (snapshotSequence, items) = await LoadSnapshotAsync(directory, options, recovery, cancellationToken).ConfigureAwait(false);

        var journalPath = Path.Combine(directory, JournalFile);
        var (sequence, replayed, journalSetAside) = await ReplayJournalAsync(journalPath, snapshotSequence, items, options, recovery, cancellationToken).ConfigureAwait(false);

        // Not FileMode.Append: an append-mode stream cannot be truncated below where it was opened,
        // and both a failed write and a checkpoint need to truncate.
        var journal = new FileStream(journalPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        journal.Seek(0, SeekOrigin.End);
        var storage = new AppStorage(directory, options, items, snapshotSequence, sequence, replayed, journal, recovery);

        // A set-aside journal or a fallback snapshot means the recovered state exists only in memory
        // (or only in an old file); write it down now so the next load starts from it.
        if (journalSetAside || recovery.Exists(e => e.Kind == StorageRecoveryKind.UsedPreviousSnapshot))
            await storage.CheckpointAsync(cancellationToken).ConfigureAwait(false);

        return storage;
    }

    /// <summary>Returns a copy of the current items, sorted by key.</summary>
    public IReadOnlyDictionary<string, string> GetItems()
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new SortedDictionary<string, string>(_items, StringComparer.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Durably applies <paramref name="operations"/> in order and returns the sequence number of the
    /// last one. When this returns, the operations are on the storage device. If it throws, none of
    /// them took effect.
    /// </summary>
    public async Task<long> ApplyAsync(IReadOnlyList<StorageOperation> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (operations.Count == 0) return Sequence;

            using var lines = new MemoryStream();
            for (var i = 0; i < operations.Count; i++)
                StorageFormat.WriteJournalLine(lines, Sequence + 1 + i, operations[i]);

            var lengthBefore = _journal.Length;
            try
            {
                await _journal.WriteAsync(lines.GetBuffer().AsMemory(0, (int)lines.Length), cancellationToken).ConfigureAwait(false);
                _journal.Flush(flushToDisk: true);
            }
            catch
            {
                // Leave no partial line behind: a later write would otherwise land after it and
                // turn an unacknowledged tail into corruption in the middle of the journal.
                TryTruncate(_journal, lengthBefore);
                throw;
            }

            foreach (var operation in operations) operation.ApplyTo(_items);
            Sequence += operations.Count;
            _journaledSinceSnapshot += operations.Count;

            if (_journaledSinceSnapshot >= _options.CheckpointEvery)
                await CheckpointCoreAsync(cancellationToken).ConfigureAwait(false);

            return Sequence;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Rewrites the snapshot from the current state and empties the journal.</summary>
    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await CheckpointCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes the current state to <paramref name="path"/> in the snapshot format, outside this
    /// storage's own files, and returns the sequence number it holds. The file is complete or absent.
    /// </summary>
    public async Task<long> SaveSnapshotAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await DurableFile.WriteAtomicallyAsync(path, StorageFormat.WriteSnapshot(Sequence, _items), cancellationToken).ConfigureAwait(false);
            return Sequence;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces the current state with the snapshot saved in <paramref name="path"/> by
    /// <see cref="SaveSnapshotAsync"/>. The state it replaces is not lost: it becomes the previous
    /// snapshot, kept in <c>versions/</c> like any other. The sequence number moves forward, never back.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a readable snapshot; nothing was changed.</exception>
    public async Task RestoreAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!StorageFormat.TryReadSnapshot(bytes, out _, out var restored))
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a readable storage snapshot.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // The state being replaced is written down first, so it becomes a version when the restored one is.
            await CheckpointCoreAsync(cancellationToken).ConfigureAwait(false);
            _items.Clear();
            foreach (var (key, value) in restored) _items[key] = value;
            Sequence++;
            await CheckpointCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _journal.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckpointCoreAsync(CancellationToken cancellationToken)
    {
        var snapshotPath = Path.Combine(_directory, SnapshotFile);
        if (Sequence == _snapshotSequence && File.Exists(snapshotPath)) return;

        // Order matters for crash safety. The journal is emptied last, so at every point either the
        // current snapshot or a previous one, plus the untouched journal, reproduces the state.
        var temporary = snapshotPath + ".tmp";
        var bytes = StorageFormat.WriteSnapshot(Sequence, _items);
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(snapshotPath))
            File.Move(snapshotPath, VersionPath(_snapshotSequence), overwrite: true);
        File.Move(temporary, snapshotPath, overwrite: true);

        _journal.SetLength(0);
        _journal.Seek(0, SeekOrigin.Begin);
        _journal.Flush(flushToDisk: true);
        _snapshotSequence = Sequence;
        _journaledSinceSnapshot = 0;

        PruneVersions();
    }

    private string VersionPath(long sequence)
    {
        var stamp = _options.Clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return Path.Combine(_directory, VersionsDirectory, $"{sequence:D12}-{stamp}.json");
    }

    private void PruneVersions()
    {
        var versions = ListVersions(_directory);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions.Take(_options.KeepRecentVersions)) keep.Add(version.Path);

        var oldestDay = DateOnly.FromDateTime(_options.Clock.GetUtcNow().UtcDateTime).AddDays(-_options.KeepDailyVersionsForDays);
        foreach (var newestOfDay in versions.Where(v => v.Day > oldestDay).GroupBy(v => v.Day).Select(g => g.First()))
            keep.Add(newestOfDay.Path);

        foreach (var version in versions.Where(v => !keep.Contains(v.Path)))
            File.Delete(version.Path);
    }

    private sealed record Version(string Path, long Sequence, DateOnly Day);

    /// <summary>Previous snapshots, newest first. Files that do not follow the naming scheme are left alone.</summary>
    private static List<Version> ListVersions(string directory)
    {
        var versions = new List<Version>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(directory, VersionsDirectory), "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0
                || !long.TryParse(name.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
                || !DateTime.TryParseExact(name[(dash + 1)..], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp))
                continue;
            versions.Add(new Version(path, sequence, DateOnly.FromDateTime(stamp)));
        }

        versions.Sort((a, b) => b.Sequence != a.Sequence ? b.Sequence.CompareTo(a.Sequence) : string.CompareOrdinal(b.Path, a.Path));
        return versions;
    }

    private static async Task<(long Sequence, Dictionary<string, string> Items)> LoadSnapshotAsync(
        string directory, StorageOptions options, List<StorageRecoveryEvent> recovery, CancellationToken cancellationToken)
    {
        var snapshotPath = Path.Combine(directory, SnapshotFile);
        var hadSnapshot = File.Exists(snapshotPath);
        if (hadSnapshot)
        {
            var bytes = await File.ReadAllBytesAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            if (StorageFormat.TryReadSnapshot(bytes, out var sequence, out var items)) return (sequence, items);
            var aside = DurableFile.SetAside(snapshotPath, options.Clock);
            recovery.Add(new(StorageRecoveryKind.UsedPreviousSnapshot, $"The snapshot could not be read and was set aside as {Path.GetFileName(aside)}."));
        }

        foreach (var version in ListVersions(directory))
        {
            var bytes = await File.ReadAllBytesAsync(version.Path, cancellationToken).ConfigureAwait(false);
            if (!StorageFormat.TryReadSnapshot(bytes, out var sequence, out var items)) continue;
            if (!hadSnapshot)
                recovery.Add(new(StorageRecoveryKind.UsedPreviousSnapshot, $"No current snapshot was found; loaded {Path.GetFileName(version.Path)}."));
            else
                recovery.Add(new(StorageRecoveryKind.UsedPreviousSnapshot, $"Loaded {Path.GetFileName(version.Path)} instead."));
            return (sequence, items);
        }

        return (0, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static async Task<(long Sequence, int Replayed, bool SetAside)> ReplayJournalAsync(string journalPath, long snapshotSequence,
        Dictionary<string, string> items, StorageOptions options, List<StorageRecoveryEvent> recovery, CancellationToken cancellationToken)
    {
        if (!File.Exists(journalPath)) return (snapshotSequence, 0, false);

        var bytes = await File.ReadAllBytesAsync(journalPath, cancellationToken).ConfigureAwait(false);
        var last = snapshotSequence;
        var replayed = 0;
        var position = 0;
        while (position < bytes.Length)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', position);
            if (newline < 0)
            {
                // An incomplete final line was never acknowledged: its write did not finish.
                await using (var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    stream.SetLength(position);
                    stream.Flush(flushToDisk: true);
                }

                recovery.Add(new(StorageRecoveryKind.TruncatedJournalTail, $"Discarded an incomplete final journal line of {bytes.Length - position} bytes."));
                break;
            }

            var line = bytes.AsSpan(position, newline - position);
            position = newline + 1;
            if (line.IsEmpty) continue;

            if (!StorageFormat.TryReadJournalLine(line, out var sequence, out var operation))
            {
                var aside = DurableFile.SetAside(journalPath, options.Clock);
                recovery.Add(new(StorageRecoveryKind.CorruptJournal,
                    $"A journal line after sequence {last} could not be read; replay stopped there and the journal was set aside as {Path.GetFileName(aside)}."));
                return (last, replayed, true);
            }

            if (sequence <= snapshotSequence) continue; // Already in the snapshot: the journal was not emptied after it.
            if (sequence <= last)
            {
                recovery.Add(new(StorageRecoveryKind.SequenceRegression, $"Ignored journal sequence {sequence}, which is not after {last}."));
                continue;
            }

            if (sequence != last + 1)
                recovery.Add(new(StorageRecoveryKind.SequenceGap, $"Journal sequence jumped from {last} to {sequence}; {sequence - last - 1} operation(s) are missing."));

            operation!.ApplyTo(items);
            last = sequence;
            replayed++;
        }

        return (last, replayed, false);
    }

    private static void TryTruncate(FileStream stream, long length)
    {
        try
        {
            stream.SetLength(length);
            stream.Seek(length, SeekOrigin.Begin);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException)
        {
            // The original failure is what the caller needs to see; loading will discard the tail.
        }
    }
}
