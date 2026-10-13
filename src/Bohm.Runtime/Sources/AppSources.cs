using LocalOrigin.Storage;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bohm.Runtime.Storage;

namespace Bohm.Runtime.Sources;

/// <summary>
/// How to read one table from a web page, so the same table can be read again the same way:
/// the pages it may be read from (<paramref name="Site"/> — an address, and every page under it),
/// the table on the page (<paramref name="Selector"/>) and the header names of the columns to keep,
/// in order.
/// </summary>
public sealed record SourceRule(string Site, string Selector, IReadOnlyList<string> Columns)
{
    /// <summary>Throws <see cref="ArgumentException"/> unless the rule names a web address, a table and distinct, non-blank columns.</summary>
    public static void Validate(SourceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!Uri.TryCreate(rule.Site, UriKind.Absolute, out var site) || site.Scheme is not ("http" or "https"))
            throw new ArgumentException($"'{rule.Site}' is not a web address.", nameof(rule));
        if (string.IsNullOrWhiteSpace(rule.Selector)) throw new ArgumentException("The rule names no table.", nameof(rule));
        if (rule.Columns is not { Count: > 0 }) throw new ArgumentException("The rule keeps no columns.", nameof(rule));
        if (rule.Columns.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("A column has no name.", nameof(rule));
        if (rule.Columns.Distinct(StringComparer.Ordinal).Count() != rule.Columns.Count)
            throw new ArgumentException("Two columns have the same name.", nameof(rule));
    }
}

/// <summary>
/// The person's permission to read a source: which pages, when it was given, and — when they asked for it — how
/// often the source is read again on its own while the browser runs (<paramref name="Every"/>, at least
/// <see cref="AppSources.MinimumEvery"/>). Reading on a schedule the person set is their doing, as a press of
/// «read again» is.
/// </summary>
public sealed record SourceGrant(string Site, DateTimeOffset GrantedAt, TimeSpan? Every = null);

/// <summary>
/// A source an application reads from: its rule, the permission for it (if any), when a reading was last kept,
/// and — while it is read on a schedule — the schedule's state (<see cref="Watch"/>).
/// </summary>
public sealed record AppSource(string Name, SourceRule Rule, SourceGrant? Grant, DateTimeOffset? LastReadAt, SourceWatch? Watch = null);

/// <summary>Why a scheduled reading failed, as the reader saw it — each is met differently (<see cref="AppSources.ReportFailureAsync"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SourceFailure>))]
public enum SourceFailure
{
    /// <summary>For a while: the network, a timeout, a server error, a busy server, a table not drawn yet. Tried again, waiting longer each time.</summary>
    [JsonStringEnumMemberName("transient")]
    Transient,

    /// <summary>The page wants the person: a sign-in, a check that they are not a robot, a refusal of access. Stops at once — trying again knocks on that wall.</summary>
    [JsonStringEnumMemberName("needs-person")]
    NeedsPerson,

    /// <summary>The table is not there or its columns changed. Tried once more — a page can draw late — then stops.</summary>
    [JsonStringEnumMemberName("shape-changed")]
    ShapeChanged,

    /// <summary>Outside what was allowed: the page left the permitted site, or the table has more rows than one reading keeps. Stops.</summary>
    [JsonStringEnumMemberName("refused")]
    Refused,
}

/// <summary>
/// The state of a source read on a schedule: when it is next due, when it was last read without a problem
/// (<paramref name="CheckedAt"/> — changed or not), the failures since, the last one and its reason, and whether
/// it stopped (<paramref name="Halted"/>) until the person reads it once. <paramref name="RowsHash"/> recognizes
/// the rows last kept, so a reading that finds the same rows is not kept again.
/// </summary>
public sealed record SourceWatch(DateTimeOffset NextAt, DateTimeOffset? CheckedAt = null, int Failures = 0, SourceFailure? LastFailure = null,
    string? Reason = null, bool Halted = false, string? RowsHash = null);

/// <summary>What was read from a source at one time: the page it came from and its rows, each a cell per column name.</summary>
public sealed record SourceReading(DateTimeOffset ReadAt, string Source, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);

/// <summary>What became of a schedule asked of <see cref="AppSources.ScheduleAsync"/>.</summary>
public enum ScheduleOutcome
{
    /// <summary>The schedule is set (or stopped).</summary>
    Scheduled,

    /// <summary>The application has no source by that name.</summary>
    Unknown,

    /// <summary>The source has no permission — a schedule is part of one.</summary>
    NotGranted,

    /// <summary>Shorter than <see cref="AppSources.MinimumEvery"/>.</summary>
    TooOften,
}

/// <summary>What became of a reading offered to <see cref="AppSources.RecordAsync"/>.</summary>
public enum RecordOutcome
{
    /// <summary>The reading was kept.</summary>
    Recorded,

    /// <summary>A scheduled reading found the rows last kept: nothing new was kept, and the source counts as read.</summary>
    Unchanged,

    /// <summary>The application has no source by that name.</summary>
    Unknown,

    /// <summary>The source has no permission — never given, or taken back.</summary>
    NotGranted,

    /// <summary>The page is not under the site the permission names.</summary>
    OutsideGrant,

    /// <summary>The columns are not the rule's columns, in the rule's order, or a row does not have one cell per column.</summary>
    ShapeMismatch,
}

/// <summary>
/// The sources of one application, in its folder: <c>sources.json</c> (the rules, the permissions and
/// when each was last read) and <c>sources/&lt;name&gt;.ndjson</c> (the readings, one per line, oldest first).
/// </summary>
/// <remarks>
/// Readings are appended and never rewritten: what was read from a page is a fact about that page at
/// that time, kept even after the permission is taken back. This class checks every reading before
/// keeping it — the permission, the page and the columns — so nothing outside what the person allowed,
/// and nothing of another shape, is stored.
/// </remarks>
public sealed partial class AppSources : IDisposable
{
    /// <summary>The shortest schedule a person can set: a page is not read on its own more than once a minute.</summary>
    public static readonly TimeSpan MinimumEvery = TimeSpan.FromMinutes(1);

    /// <summary>The schedule offered first.</summary>
    public static readonly TimeSpan DefaultEvery = TimeSpan.FromMinutes(5);

    /// <summary>The longest wait between attempts after failures that pass with time.</summary>
    public static readonly TimeSpan LongestBackoff = TimeSpan.FromHours(1);

    /// <summary>Format identifier written into every <c>sources.json</c>. Changes when its shape does.</summary>
    public const string Format = "bohm.sources/1";

    /// <summary>The file holding the rules, in the application's folder.</summary>
    public const string RulesFile = "sources.json";

    /// <summary>The folder holding the readings, in the application's folder.</summary>
    public const string ReadingsDirectory = "sources";

    private readonly string _folder;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private AppSources(string folder, TimeProvider clock)
    {
        _folder = folder;
        _clock = clock;
    }

    /// <summary>Opens the sources of the application whose folder is <paramref name="appFolder"/>. Nothing is created until something is declared.</summary>
    public static AppSources Open(string appFolder, TimeProvider? clock = null) => new(appFolder, clock ?? TimeProvider.System);

    /// <summary>Whether <paramref name="name"/> can name a source: a lowercase letter or digit, then up to 39 more of those or hyphens.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>The application's sources, in the order they were declared.</summary>
    public async Task<IReadOnlyList<AppSource>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadRulesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Declares source <paramref name="name"/>, or replaces its rule. <paramref name="granted"/> says the
    /// person has just allowed the rule's site to be read; without it the source is declared but cannot be
    /// read until it is declared again with permission. Readings already kept stay.
    /// </summary>
    public async Task<AppSource> DeclareAsync(string name, SourceRule rule, bool granted, CancellationToken cancellationToken = default)
    {
        RequireValidName(name);
        SourceRule.Validate(rule);
        var declared = new AppSource(name, rule with { Columns = [.. rule.Columns] }, granted ? new SourceGrant(rule.Site, _clock.GetUtcNow()) : null, null);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sources = (await ReadRulesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var at = sources.FindIndex(s => s.Name == name);
            if (at < 0) sources.Add(declared);
            else sources[at] = declared with { LastReadAt = sources[at].LastReadAt };
            await WriteRulesAsync(sources, cancellationToken).ConfigureAwait(false);
            return at < 0 ? declared : sources[at];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Takes back the permission for source <paramref name="name"/>. Its rule and its readings stay. False when there is no such source.</summary>
    public async Task<bool> RevokeAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!IsValidName(name)) return false;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sources = (await ReadRulesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var at = sources.FindIndex(s => s.Name == name);
            if (at < 0) return false;
            sources[at] = sources[at] with { Grant = null, Watch = null };
            await WriteRulesAsync(sources, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Keeps a reading of source <paramref name="name"/> taken from page <paramref name="source"/>, if the
    /// source has permission, the page is under the permitted site and the columns are the rule's — in its
    /// order, with one cell per column in every row. Anything else is refused and nothing is kept.
    /// A kept reading clears a schedule's failures — a stopped schedule starts again — and sets its next time.
    /// </summary>
    public Task<(RecordOutcome Outcome, SourceReading? Reading)> RecordAsync(
        string name, string source, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, CancellationToken cancellationToken = default) =>
        RecordAsync(name, source, columns, rows, scheduled: false, cancellationToken);

    /// <summary>
    /// Keeps a reading made on the source's schedule as <see cref="RecordAsync(string, string, IReadOnlyList{string}, IReadOnlyList{IReadOnlyList{string}}, CancellationToken)"/>
    /// does — except that one finding the rows last kept keeps nothing new (<see cref="RecordOutcome.Unchanged"/>):
    /// the source counts as read, and its readings grow only when the page changes.
    /// </summary>
    public Task<(RecordOutcome Outcome, SourceReading? Reading)> RecordScheduledAsync(
        string name, string source, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, CancellationToken cancellationToken = default) =>
        RecordAsync(name, source, columns, rows, scheduled: true, cancellationToken);

    private async Task<(RecordOutcome Outcome, SourceReading? Reading)> RecordAsync(
        string name, string source, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, bool scheduled, CancellationToken cancellationToken)
    {
        if (!IsValidName(name)) return (RecordOutcome.Unknown, null);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sources = (await ReadRulesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var at = sources.FindIndex(s => s.Name == name);
            if (at < 0) return (RecordOutcome.Unknown, null);
            var declared = sources[at];
            if (Check(declared.Rule, declared.Grant, source, columns, rows) is not RecordOutcome.Recorded and var refused) return (refused, null);

            var now = _clock.GetUtcNow();
            var reading = new SourceReading(
                now,
                source,
                [.. rows.Select(r => (IReadOnlyDictionary<string, string>)columns.Zip(r).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal))]);
            var hash = HashOf(rows);
            var watch = declared.Grant?.Every is { } every ? new SourceWatch(NextAfter(name, now, every), now, RowsHash: hash) : null;
            if (scheduled && declared.Watch?.RowsHash == hash)
            {
                sources[at] = declared with { Watch = watch };
                await WriteRulesAsync(sources, cancellationToken).ConfigureAwait(false);
                return (RecordOutcome.Unchanged, reading);
            }

            await AppendReadingAsync(name, reading, cancellationToken).ConfigureAwait(false);
            sources[at] = declared with { LastReadAt = reading.ReadAt, Watch = watch };
            await WriteRulesAsync(sources, cancellationToken).ConfigureAwait(false);
            return (RecordOutcome.Recorded, reading);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Sets how often source <paramref name="name"/> is read on its own — <paramref name="every"/>, or
    /// <see langword="null"/> to stop. Only a permitted source can have one, and none shorter than
    /// <see cref="MinimumEvery"/>. Turned on, it is due at once when its last reading is older than the schedule,
    /// otherwise a schedule after that reading.
    /// </summary>
    public async Task<(ScheduleOutcome Outcome, AppSource? Source)> ScheduleAsync(string name, TimeSpan? every, CancellationToken cancellationToken = default)
    {
        if (every < MinimumEvery) return (ScheduleOutcome.TooOften, null);
        if (!IsValidName(name)) return (ScheduleOutcome.Unknown, null);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sources = (await ReadRulesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var at = sources.FindIndex(s => s.Name == name);
            if (at < 0) return (ScheduleOutcome.Unknown, null);
            var declared = sources[at];
            if (declared.Grant is not { } grant) return (ScheduleOutcome.NotGranted, null);

            var now = _clock.GetUtcNow();
            var watch = every is { } e
                ? new SourceWatch(declared.LastReadAt is { } last && last + e > now ? NextAfter(name, last, e) : now, RowsHash: declared.Watch?.RowsHash)
                : null;
            sources[at] = declared with { Grant = grant with { Every = every }, Watch = watch };
            await WriteRulesAsync(sources, cancellationToken).ConfigureAwait(false);
            return (ScheduleOutcome.Scheduled, sources[at]);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The sources due to be read on their schedule now — permitted, scheduled, not stopped, and past their next time.</summary>
    public async Task<IReadOnlyList<AppSource>> DueAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        return [.. (await ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(s => s.Grant?.Every is not null && s.Watch is { Halted: false } w && w.NextAt <= now)];
    }

    /// <summary>
    /// Records that a scheduled reading of source <paramref name="name"/> failed, and when to try again:
    /// <see cref="SourceFailure.Transient"/> waits the schedule, doubling with each failure in a row up to
    /// <see cref="LongestBackoff"/> (never less than <paramref name="retryAfter"/>, the wait the site asked for);
    /// <see cref="SourceFailure.ShapeChanged"/> tries once more after the schedule and stops on a second in a row;
    /// <see cref="SourceFailure.NeedsPerson"/> and <see cref="SourceFailure.Refused"/> stop at once. A stopped
    /// source waits for the person: a reading they ask for, kept, starts it again. Nothing read is kept or lost.
    /// <see langword="null"/> when there is no such source or it is not on a schedule.
    /// </summary>
    public async Task<AppSource?> ReportFailureAsync(string name, SourceFailure failure, string? reason = null, TimeSpan? retryAfter = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidName(name)) return null;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sources = (await ReadRulesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var at = sources.FindIndex(s => s.Name == name);
            if (at < 0 || sources[at] is not { Grant.Every: { } every, Watch: { } watch } declared) return null;

            var now = _clock.GetUtcNow();
            var failures = watch.Failures + 1;
            var halted = failure is SourceFailure.NeedsPerson or SourceFailure.Refused
                || (failure is SourceFailure.ShapeChanged && watch.LastFailure is SourceFailure.ShapeChanged);
            var wait = failure is SourceFailure.Transient
                ? Max(Min(every * Math.Pow(2, Math.Min(failures - 1, 16)), Max(LongestBackoff, MinimumEvery)), retryAfter ?? TimeSpan.Zero)
                : every;
            sources[at] = declared with
            {
                Watch = watch with { NextAt = now + Max(wait, MinimumEvery), Failures = failures, LastFailure = failure, Reason = reason, Halted = halted },
            };
            await WriteRulesAsync(sources, cancellationToken).ConfigureAwait(false);
            return sources[at];
        }
        finally
        {
            _lock.Release();
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>
    /// The next time after <paramref name="from"/>: the schedule, moved by up to a tenth either way by the source's
    /// name — the same for a source every time, so sources set to the same schedule do not all come due together.
    /// </summary>
    private static DateTimeOffset NextAfter(string name, DateTimeOffset from, TimeSpan every)
    {
        uint h = 2166136261;
        foreach (var c in name) h = (h ^ c) * 16777619;
        var spread = ((int)(h % 201) - 100) / 1000.0;
        return from + every * (1 + spread);
    }

    /// <summary>Recognizes a reading's rows: the same rows, in the same order, give the same text.</summary>
    private static string HashOf(IReadOnlyList<IReadOnlyList<string>> rows) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows, SourcesJson.Default.IReadOnlyListIReadOnlyListString)));

    /// <summary>
    /// Whether a reading would be kept for a source with <paramref name="rule"/> and <paramref name="grant"/> —
    /// <see cref="RecordOutcome.Recorded"/> — or why not, without keeping anything: the check
    /// <see cref="RecordAsync"/> makes, for checking before a source exists.
    /// </summary>
    public static RecordOutcome Check(SourceRule rule, SourceGrant? grant, string source, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (grant is null) return RecordOutcome.NotGranted;
        if (!IsUnder(source, grant.Site)) return RecordOutcome.OutsideGrant;
        if (!columns.SequenceEqual(rule.Columns, StringComparer.Ordinal) || rows.Any(r => r.Count != columns.Count)) return RecordOutcome.ShapeMismatch;
        return RecordOutcome.Recorded;
    }

    /// <summary>The readings of source <paramref name="name"/>, oldest first, or <see langword="null"/> when there is no such source.</summary>
    public async Task<IReadOnlyList<SourceReading>?> ReadingsAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!IsValidName(name)) return null;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if ((await ReadRulesAsync(cancellationToken).ConfigureAwait(false)).All(s => s.Name != name)) return null;
            var path = ReadingsPath(name);
            if (!File.Exists(path)) return [];
            var text = Encoding.UTF8.GetString(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false));
            var readings = new List<SourceReading>();
            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize(line, SourcesJson.Default.StoredReading) is { } stored)
                        readings.Add(new SourceReading(stored.ReadAt, stored.Source, stored.Rows));
                }
                catch (JsonException)
                {
                    // An incomplete line — the end of an append a crash interrupted — is not a reading.
                }
            }

            return readings;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Writes the rules alone into the application folder <paramref name="appFolder"/> — without the
    /// permissions and without when each source was last read — for a copy of the application that
    /// leaves its data behind. Nothing is written when there are no sources.
    /// </summary>
    public async Task CopyRulesAsync(string appFolder, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AppSource> sources;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            sources = await ReadRulesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        if (sources.Count == 0) return;
        using var copy = Open(appFolder, _clock);
        await copy.WriteRulesAsync([.. sources.Select(s => s with { Grant = null, LastReadAt = null, Watch = null })], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="page"/> is <paramref name="site"/> or a page under it: same scheme, host and
    /// port, and a path that continues the site's at a <c>/</c>, <c>?</c> or <c>#</c> — so a host or path that
    /// merely begins with the same letters is outside.
    /// </summary>
    private static bool IsUnder(string page, string site)
    {
        if (!page.StartsWith(site, StringComparison.Ordinal)) return false;
        if (page.Length == site.Length || site.EndsWith('/')) return true;
        return page[site.Length] is '/' or '?' or '#';
    }

    private string RulesPath => Path.Combine(_folder, RulesFile);

    private string ReadingsPath(string name) => Path.Combine(_folder, ReadingsDirectory, name + ".ndjson");

    private async Task<IReadOnlyList<AppSource>> ReadRulesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RulesPath)) return [];
        var stored = JsonSerializer.Deserialize(await DurableFile.ReadAsync(RulesPath, cancellationToken).ConfigureAwait(false), SourcesJson.Default.StoredRules);
        if (stored is not { Format: Format }) throw new InvalidDataException($"'{RulesPath}' is not a {Format} file.");
        return stored.Sources;
    }

    private async Task WriteRulesAsync(IReadOnlyList<AppSource> sources, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredRules(Format, sources), SourcesJson.Default.StoredRules);
        await DurableFile.WriteAtomicallyAsync(RulesPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendReadingAsync(string name, SourceReading reading, CancellationToken cancellationToken)
    {
        var path = ReadingsPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.SerializeToUtf8Bytes(new StoredReading(reading.ReadAt, reading.Source, reading.Rows), SourcesJson.Default.StoredReading);
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        // A line a crash left unfinished must not swallow this one: start on a line of its own.
        var startsOwnLine = stream.Length == 0;
        if (!startsOwnLine)
        {
            stream.Seek(-1, SeekOrigin.End);
            startsOwnLine = stream.ReadByte() == '\n';
        }

        stream.Seek(0, SeekOrigin.End);
        if (!startsOwnLine) stream.WriteByte((byte)'\n');
        await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    public void Dispose() => _lock.Dispose();

    private static void RequireValidName(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException($"'{name}' cannot name a source.", nameof(name));
    }

    internal sealed record StoredRules(string Format, IReadOnlyList<AppSource> Sources);

    internal sealed record StoredReading(DateTimeOffset ReadAt, string Source, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(AppSources.StoredRules))]
[JsonSerializable(typeof(AppSources.StoredReading))]
[JsonSerializable(typeof(IReadOnlyList<IReadOnlyList<string>>))]
internal sealed partial class SourcesJson : JsonSerializerContext;
