using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Usage;

/// <summary>A fact about one day's use of an application. Carries no content — no keys, values or text.</summary>
public enum UsageSignal
{
    /// <summary>The application was opened.</summary>
    Opened,

    /// <summary>The person typed or pointed inside the application.</summary>
    Input,

    /// <summary>The application stored data.</summary>
    Wrote,
}

/// <summary>
/// The local usage record of one application: for each day, which of the <see cref="UsageSignal"/>s
/// occurred, and how many times the application failed while loading. It stays on this computer;
/// nothing here sends it anywhere.
/// </summary>
/// <remarks>
/// The file is append-only NDJSON with one line per first occurrence of a signal on a day
/// (<c>{"date":"2026-09-23","signal":"opened"}</c>) and one line per load failure
/// (<c>{"date":"2026-09-23","event":"load-error"}</c>), one line per closing page whose last writes the
/// host could not confirm as applied (<c>"event":"loss-suspected"</c>), one line each time the person takes in
/// a new revision or goes back to the previous one (<c>"event":"revised"</c> / <c>"reverted"</c>), and
/// one line per page load that reported how the running revision's reads matched the stored data
/// (<c>{"date":…,"event":"keys","revision":2,"missing":1,"unread":3,"seeded":3}</c> — counts only, never key names).
/// The record belongs to the application, not to a revision. Days are the person's local calendar days.
/// Recording is best-effort: a usage line is never allowed to fail the operation that caused it.
/// </remarks>
public sealed class UsageLog
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly HashSet<(DateOnly, UsageSignal)> _seen = [];
    private readonly Dictionary<DateOnly, int> _loadErrors = [];
    private readonly Dictionary<DateOnly, int> _lossSuspected = [];
    private readonly List<RevisionEvent> _revisions = [];
    private readonly SortedDictionary<int, KeyReport> _keys = [];

    private UsageLog(string path, TimeProvider clock)
    {
        _path = path;
        _clock = clock;
    }

    /// <summary>Opens the record in <paramref name="path"/>, reading what it already holds.</summary>
    public static UsageLog Open(string path, TimeProvider? clock = null)
    {
        var log = new UsageLog(path, clock ?? TimeProvider.System);
        if (!File.Exists(path)) return log;

        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!DateOnly.TryParseExact(root.GetProperty("date").GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                if (root.TryGetProperty("signal", out var signal) && Enum.TryParse<UsageSignal>(signal.GetString(), ignoreCase: true, out var parsed))
                    log._seen.Add((date, parsed));
                else if (root.TryGetProperty("event", out var @event))
                {
                    switch (@event.GetString())
                    {
                        case "load-error": log._loadErrors[date] = log._loadErrors.GetValueOrDefault(date) + 1; break;
                        case "loss-suspected": log._lossSuspected[date] = log._lossSuspected.GetValueOrDefault(date) + 1; break;
                        case "revised": log._revisions.Add(new RevisionEvent(date, Reverted: false)); break;
                        case "reverted": log._revisions.Add(new RevisionEvent(date, Reverted: true)); break;
                        case "keys" when ReadKeyReport(root, date) is { } keys: log._keys[keys.Revision] = keys; break;
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // An unreadable line (for example a write cut short) says nothing; skip it.
            }
        }

        return log;
    }

    /// <summary>Today's date in the person's local calendar.</summary>
    public DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    /// <summary>Records <paramref name="signal"/> for today, once per day.</summary>
    public void Record(UsageSignal signal)
    {
        var today = Today;
        lock (_lock)
        {
            if (!_seen.Add((today, signal))) return;
            Append($$"""{"date":"{{today:yyyy-MM-dd}}","signal":"{{signal.ToString().ToLowerInvariant()}}"}""");
        }
    }

    /// <summary>Counts one failure of the application while it was loading.</summary>
    public void RecordLoadError()
    {
        var today = Today;
        lock (_lock)
        {
            _loadErrors[today] = _loadErrors.GetValueOrDefault(today) + 1;
            Append($$"""{"date":"{{today:yyyy-MM-dd}}","event":"load-error"}""");
        }
    }

    /// <summary>
    /// Counts one closing page whose last writes the host could not confirm as applied before the page
    /// went away — a possible loss of the last moment's data. The count is the evidence; there is no content.
    /// </summary>
    public void RecordLossSuspected()
    {
        var today = Today;
        lock (_lock)
        {
            _lossSuspected[today] = _lossSuspected.GetValueOrDefault(today) + 1;
            Append($$"""{"date":"{{today:yyyy-MM-dd}}","event":"loss-suspected"}""");
        }
    }

    /// <summary>
    /// Records that the person took in a new revision (<c>{"event":"revised"}</c>) or went back to
    /// the previous one (<c>{"event":"reverted"}</c>). Every occurrence is a line: a revision is a
    /// deliberate act of keeping the application, a stronger sign of use than any single day's signals.
    /// </summary>
    public void RecordRevision(bool reverted)
    {
        var today = Today;
        lock (_lock)
        {
            _revisions.Add(new RevisionEvent(today, reverted));
            Append($$"""{"date":"{{today:yyyy-MM-dd}}","event":"{{(reverted ? "reverted" : "revised")}}"}""");
        }
    }

    /// <summary>
    /// Records how a page of revision <paramref name="revision"/> read the stored data: how many
    /// distinct keys it asked for that the data does not have, and how many of the <paramref name="seeded"/>
    /// keys it was given it never read. A revision whose keys changed shows both. Only the latest
    /// report per revision is kept in memory; every report is a line.
    /// </summary>
    public void RecordKeys(int revision, int missing, int unread, int seeded)
    {
        var report = new KeyReport(Today, revision, missing, unread, seeded);
        lock (_lock)
        {
            _keys[revision] = report;
            Append($$"""{"date":"{{report.Date:yyyy-MM-dd}}","event":"keys","revision":{{revision}},"missing":{{missing}},"unread":{{unread}},"seeded":{{seeded}}}""");
        }
    }

    /// <summary>The latest key report of each revision, in revision order.</summary>
    public IReadOnlyList<KeyReport> KeyReports
    {
        get
        {
            lock (_lock) return _keys.Values.ToList();
        }
    }

    private static KeyReport? ReadKeyReport(JsonElement root, DateOnly date)
    {
        static int? Count(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0 ? n : null;
        return Count(root, "revision") is { } revision and >= 1 && Count(root, "missing") is { } missing
            && Count(root, "unread") is { } unread && Count(root, "seeded") is { } seeded
            ? new KeyReport(date, revision, missing, unread, seeded)
            : null;
    }

    /// <summary>The signals recorded for <paramref name="date"/>.</summary>
    public IReadOnlySet<UsageSignal> SignalsOn(DateOnly date)
    {
        lock (_lock) return _seen.Where(s => s.Item1 == date).Select(s => s.Item2).ToHashSet();
    }

    /// <summary>
    /// The days the application was used — opened, and typed or pointed in, on the same day — in
    /// order. This is the definition every retention figure is computed from.
    /// </summary>
    public IReadOnlyList<DateOnly> UsedDays
    {
        get
        {
            lock (_lock)
            {
                return _seen.Where(s => s.Item2 == UsageSignal.Input && _seen.Contains((s.Item1, UsageSignal.Opened)))
                    .Select(s => s.Item1).Order().ToList();
            }
        }
    }

    /// <summary>The first day the application was used, or <see langword="null"/> if it never was.</summary>
    public DateOnly? FirstUsedOn => UsedDays is [var first, ..] ? first : null;

    /// <summary>Every day with anything recorded, in order: its signals, its load failures and its suspected losses.</summary>
    public IReadOnlyList<UsageDay> Days
    {
        get
        {
            lock (_lock)
            {
                return _seen.Select(s => s.Item1).Concat(_loadErrors.Keys).Concat(_lossSuspected.Keys).Distinct().Order()
                    .Select(d => new UsageDay(d, _seen.Contains((d, UsageSignal.Opened)), _seen.Contains((d, UsageSignal.Input)),
                        _seen.Contains((d, UsageSignal.Wrote)), _loadErrors.GetValueOrDefault(d), _lossSuspected.GetValueOrDefault(d)))
                    .ToList();
            }
        }
    }

    /// <summary>Each time the person took in a new revision or went back to the previous one, in order.</summary>
    public IReadOnlyList<RevisionEvent> Revisions
    {
        get
        {
            lock (_lock) return _revisions.ToList();
        }
    }

    /// <summary>
    /// The latest day the application was used — opened, and typed or pointed in, on the same day —
    /// or <see langword="null"/> if it never was. Opening alone does not count: a restored tab or a
    /// stray click would make a forgotten application look alive.
    /// </summary>
    public DateOnly? LastUsedOn
    {
        get
        {
            lock (_lock)
            {
                return _seen.Where(s => s.Item2 == UsageSignal.Input && _seen.Contains((s.Item1, UsageSignal.Opened)))
                    .Select(s => (DateOnly?)s.Item1)
                    .Max();
            }
        }
    }

    /// <summary>Load failures counted on <paramref name="date"/>.</summary>
    public int LoadErrorsOn(DateOnly date)
    {
        lock (_lock) return _loadErrors.GetValueOrDefault(date);
    }

    private void Append(string line)
    {
        try
        {
            File.AppendAllText(_path, line + "\n", Utf8NoBom);
        }
        catch (IOException)
        {
            // Best-effort by design: losing a usage line must never lose the person's data or block the application.
        }
    }
}

/// <summary>One day of an application's usage record. Carries no content.</summary>
public sealed record UsageDay(DateOnly Date, bool Opened, bool Input, bool Wrote, int LoadErrors, int LossSuspected = 0)
{
    /// <summary>Opened, and typed or pointed in, on this day.</summary>
    public bool Used => Opened && Input;
}

/// <summary>
/// How a page of revision <paramref name="Revision"/> read the stored data on <paramref name="Date"/>:
/// distinct keys asked for that the data lacked, and stored keys (of <paramref name="Seeded"/>) never read.
/// </summary>
public sealed record KeyReport(DateOnly Date, int Revision, int Missing, int Unread, int Seeded);

/// <summary>The person took in a new revision on <paramref name="Date"/>, or went back to the previous one.</summary>
public sealed record RevisionEvent(DateOnly Date, bool Reverted);
