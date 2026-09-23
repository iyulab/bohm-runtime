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
/// (<c>{"date":"2026-09-23","event":"load-error"}</c>). Days are the person's local calendar days.
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
                else if (root.TryGetProperty("event", out var @event) && @event.GetString() == "load-error")
                    log._loadErrors[date] = log._loadErrors.GetValueOrDefault(date) + 1;
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

    /// <summary>The signals recorded for <paramref name="date"/>.</summary>
    public IReadOnlySet<UsageSignal> SignalsOn(DateOnly date)
    {
        lock (_lock) return _seen.Where(s => s.Item1 == date).Select(s => s.Item2).ToHashSet();
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
