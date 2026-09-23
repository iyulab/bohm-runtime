using System.Text;
using Bohm.Runtime.Storage;

namespace Bohm.Runtime.Tests.Storage;

public sealed class AppStorageTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bohm-storage-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string Journal => Path.Combine(_directory, "journal.ndjson");
    private string Snapshot => Path.Combine(_directory, "local.json");
    private string Versions => Path.Combine(_directory, "versions");

    [Fact]
    public async Task New_storage_is_empty_and_reports_no_recovery()
    {
        await using var storage = await AppStorage.OpenAsync(_directory);

        Assert.Empty(storage.GetItems());
        Assert.Equal(0, storage.Sequence);
        Assert.Empty(storage.Recovery);
    }

    [Fact]
    public async Task Acknowledged_operations_survive_reopening()
    {
        await using (var storage = await AppStorage.OpenAsync(_directory))
        {
            Assert.Equal(3, await storage.ApplyAsync([StorageOperation.Set("a", "1"), StorageOperation.Set("b", "2"), StorageOperation.Set("c", "3")]));
            Assert.Equal(5, await storage.ApplyAsync([StorageOperation.Remove("b"), StorageOperation.Set("a", "one")]));
        }

        await using var reopened = await AppStorage.OpenAsync(_directory);

        Assert.Equal(new Dictionary<string, string> { ["a"] = "one", ["c"] = "3" }, reopened.GetItems());
        Assert.Equal(5, reopened.Sequence);
        Assert.Empty(reopened.Recovery);
    }

    [Fact]
    public async Task Operations_survive_when_the_process_ends_without_disposing()
    {
        // Simulates an abrupt exit: while the storage is still open and never disposed, its files
        // are copied as they are on disk and opened elsewhere. Everything acknowledged must be there.
        var storage = await AppStorage.OpenAsync(_directory);
        await storage.ApplyAsync([StorageOperation.Set("draft", "unsaved work")]);

        var crashed = Directory.CreateTempSubdirectory("bohm-crashed-").FullName;
        try
        {
            await CopyWhileOpenAsync(Journal, Path.Combine(crashed, "journal.ndjson"));
            await using var recovered = await AppStorage.OpenAsync(crashed);
            Assert.Equal("unsaved work", recovered.GetItems()["draft"]);
            Assert.Equal(1, recovered.Sequence);
        }
        finally
        {
            await storage.DisposeAsync();
            Directory.Delete(crashed, recursive: true);
        }
    }

    [Fact]
    public async Task Clear_removes_every_key()
    {
        await using (var storage = await AppStorage.OpenAsync(_directory))
            await storage.ApplyAsync([StorageOperation.Set("a", "1"), StorageOperation.Clear(), StorageOperation.Set("b", "2")]);

        await using var reopened = await AppStorage.OpenAsync(_directory);
        Assert.Equal(new Dictionary<string, string> { ["b"] = "2" }, reopened.GetItems());
    }

    [Fact]
    public async Task An_incomplete_final_journal_line_is_discarded_and_the_rest_kept()
    {
        await using (var storage = await AppStorage.OpenAsync(_directory))
            await storage.ApplyAsync([StorageOperation.Set("kept", "yes")]);
        await File.AppendAllTextAsync(Journal, "{\"seq\":2,\"op\":\"set\",\"key\":\"lost\",\"va");

        await using (var reopened = await AppStorage.OpenAsync(_directory))
        {
            Assert.Equal(new Dictionary<string, string> { ["kept"] = "yes" }, reopened.GetItems());
            Assert.Equal(StorageRecoveryKind.TruncatedJournalTail, Assert.Single(reopened.Recovery).Kind);

            // The tail is gone from disk, so the next write lands on a clean line boundary.
            Assert.Equal(2, await reopened.ApplyAsync([StorageOperation.Set("after", "ok")]));
        }

        await using var again = await AppStorage.OpenAsync(_directory);
        Assert.Equal(new Dictionary<string, string> { ["after"] = "ok", ["kept"] = "yes" }, again.GetItems());
        Assert.Empty(again.Recovery);
    }

    [Fact]
    public async Task A_corrupt_line_in_the_middle_stops_replay_and_sets_the_journal_aside()
    {
        await File.WriteAllTextAsync(Journal, string.Join('\n',
            "{\"seq\":1,\"op\":\"set\",\"key\":\"a\",\"value\":\"1\"}",
            "not json",
            "{\"seq\":3,\"op\":\"set\",\"key\":\"c\",\"value\":\"3\"}",
            ""));

        await using var storage = await AppStorage.OpenAsync(_directory);

        Assert.Equal(new Dictionary<string, string> { ["a"] = "1" }, storage.GetItems());
        Assert.Equal(StorageRecoveryKind.CorruptJournal, Assert.Single(storage.Recovery).Kind);
        var aside = Assert.Single(Directory.GetFiles(_directory, "journal.ndjson.unreadable-*"));
        Assert.Contains("\"key\":\"c\"", await File.ReadAllTextAsync(aside), StringComparison.Ordinal);

        // The recovered state was written down, so it does not depend on the set-aside file.
        Assert.True(File.Exists(Snapshot));
    }

    [Fact]
    public async Task Sequence_gaps_and_regressions_are_reported()
    {
        await File.WriteAllTextAsync(Journal, string.Join('\n',
            "{\"seq\":1,\"op\":\"set\",\"key\":\"a\",\"value\":\"1\"}",
            "{\"seq\":4,\"op\":\"set\",\"key\":\"b\",\"value\":\"2\"}",
            "{\"seq\":2,\"op\":\"set\",\"key\":\"a\",\"value\":\"stale\"}",
            ""));

        await using var storage = await AppStorage.OpenAsync(_directory);

        Assert.Equal(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, storage.GetItems());
        Assert.Equal(4, storage.Sequence);
        Assert.Equal([StorageRecoveryKind.SequenceGap, StorageRecoveryKind.SequenceRegression], storage.Recovery.Select(e => e.Kind));
    }

    [Fact]
    public async Task Checkpoint_writes_a_readable_sorted_snapshot_and_empties_the_journal()
    {
        await using var storage = await AppStorage.OpenAsync(_directory);
        await storage.ApplyAsync([StorageOperation.Set("zeta", "끝"), StorageOperation.Set("alpha", "{\"n\":1}")]);

        await storage.CheckpointAsync();

        var text = await File.ReadAllTextAsync(Snapshot, Encoding.UTF8);
        Assert.Contains("\"format\": \"bohm.storage/0\"", text, StringComparison.Ordinal);
        Assert.Contains("\"zeta\": \"끝\"", text, StringComparison.Ordinal); // not \uXXXX-escaped
        Assert.True(text.IndexOf("\"alpha\"", StringComparison.Ordinal) < text.IndexOf("\"zeta\"", StringComparison.Ordinal));
        Assert.Equal(0, new FileInfo(Journal).Length);
    }

    [Fact]
    public async Task State_is_the_snapshot_plus_the_journal_written_after_it()
    {
        await using (var storage = await AppStorage.OpenAsync(_directory))
        {
            await storage.ApplyAsync([StorageOperation.Set("a", "1")]);
            await storage.CheckpointAsync();
            await storage.ApplyAsync([StorageOperation.Set("b", "2")]);
        }

        await using var reopened = await AppStorage.OpenAsync(_directory);
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, reopened.GetItems());
        Assert.Equal(2, reopened.Sequence);
    }

    [Fact]
    public async Task Journal_lines_already_in_the_snapshot_are_not_applied_twice()
    {
        // A crash after the snapshot was replaced but before the journal was emptied leaves
        // operations in both places; replay must skip the ones the snapshot already contains.
        await using (var storage = await AppStorage.OpenAsync(_directory))
            await storage.ApplyAsync([StorageOperation.Set("counter", "1"), StorageOperation.Set("counter", "2")]);
        var journal = await File.ReadAllBytesAsync(Journal);
        await using (var storage = await AppStorage.OpenAsync(_directory))
        {
            await storage.CheckpointAsync();
        }

        await File.WriteAllBytesAsync(Journal, journal);
        await File.AppendAllTextAsync(Journal, "{\"seq\":3,\"op\":\"remove\",\"key\":\"counter\"}\n");

        await using var reopened = await AppStorage.OpenAsync(_directory);
        Assert.Empty(reopened.GetItems());
        Assert.Equal(3, reopened.Sequence);
        Assert.Empty(reopened.Recovery);
    }

    [Fact]
    public async Task Checkpointing_keeps_the_previous_snapshot_as_a_version()
    {
        await using var storage = await AppStorage.OpenAsync(_directory);
        await storage.ApplyAsync([StorageOperation.Set("v", "first")]);
        await storage.CheckpointAsync();
        await storage.ApplyAsync([StorageOperation.Set("v", "second")]);
        await storage.CheckpointAsync();

        var version = Assert.Single(Directory.GetFiles(Versions));
        Assert.StartsWith("000000000001-", Path.GetFileName(version), StringComparison.Ordinal);
        Assert.Contains("\"v\": \"first\"", await File.ReadAllTextAsync(version), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_checkpoint_automatically_after_the_configured_number_of_operations()
    {
        await using var storage = await AppStorage.OpenAsync(_directory, new StorageOptions { CheckpointEvery = 3 });
        await storage.ApplyAsync([StorageOperation.Set("a", "1"), StorageOperation.Set("b", "2")]);
        Assert.False(File.Exists(Snapshot));

        await storage.ApplyAsync([StorageOperation.Set("c", "3")]);
        Assert.True(File.Exists(Snapshot));
        Assert.Equal(0, new FileInfo(Journal).Length);
    }

    [Fact]
    public async Task A_missing_snapshot_falls_back_to_the_newest_previous_version()
    {
        // The crash window inside a checkpoint: the old snapshot was moved into versions/ but the
        // new one was not yet renamed into place. The journal still holds everything after it.
        await using (var storage = await AppStorage.OpenAsync(_directory))
        {
            await storage.ApplyAsync([StorageOperation.Set("a", "1")]);
            await storage.CheckpointAsync();
            await storage.ApplyAsync([StorageOperation.Set("b", "2")]);
        }

        File.Move(Snapshot, Path.Combine(Versions, "000000000001-20260101T000000Z.json"));

        await using var reopened = await AppStorage.OpenAsync(_directory);
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, reopened.GetItems());
        Assert.Equal(StorageRecoveryKind.UsedPreviousSnapshot, Assert.Single(reopened.Recovery).Kind);
        Assert.True(File.Exists(Snapshot));
    }

    [Fact]
    public async Task An_unreadable_snapshot_is_set_aside_not_deleted()
    {
        await using (var storage = await AppStorage.OpenAsync(_directory))
        {
            await storage.ApplyAsync([StorageOperation.Set("a", "1")]);
            await storage.CheckpointAsync();
        }

        await File.WriteAllTextAsync(Snapshot, "{ truncated");

        await using var reopened = await AppStorage.OpenAsync(_directory);
        Assert.Contains(reopened.Recovery, e => e.Kind == StorageRecoveryKind.UsedPreviousSnapshot);
        var aside = Assert.Single(Directory.GetFiles(_directory, "local.json.unreadable-*"));
        Assert.Equal("{ truncated", await File.ReadAllTextAsync(aside));
    }

    [Fact]
    public async Task Old_versions_are_pruned_to_the_recent_and_daily_limits()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        var options = new StorageOptions { KeepRecentVersions = 2, KeepDailyVersionsForDays = 3, Clock = clock };
        await using var storage = await AppStorage.OpenAsync(_directory, options);

        for (var day = 0; day < 6; day++)
        {
            for (var i = 0; i < 2; i++)
            {
                await storage.ApplyAsync([StorageOperation.Set("n", $"{day}-{i}")]);
                await storage.CheckpointAsync();
                clock.Advance(TimeSpan.FromHours(1));
            }

            clock.Advance(TimeSpan.FromDays(1));
        }

        // 11 previous snapshots were made over six days. Kept: the newest of each of the last 3 days
        // (the pruning day included), plus the 2 most recent — one of which is already the newest
        // of its day. 3 + 2 - 1 = 4.
        Assert.Equal(4, Directory.GetFiles(Versions).Length);
    }

    private static async Task CopyWhileOpenAsync(string path, string copy)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var target = File.Create(copy);
        await source.CopyToAsync(target);
    }
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
