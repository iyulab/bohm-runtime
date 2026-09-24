namespace Bohm.Runtime.Usage;

/// <summary>Where an application stands against the 30-day retention rule.</summary>
public enum RetentionState
{
    /// <summary>Never used, so there is no first day to count from.</summary>
    NotStarted,

    /// <summary>Fewer than 28 days since the first day of use: too early to tell.</summary>
    TooEarly,

    /// <summary>Inside days 28–34 and not yet used there.</summary>
    InWindow,

    /// <summary>Used on at least one of days 28–34.</summary>
    Retained,

    /// <summary>Day 34 has passed without a day of use in the window.</summary>
    Lapsed,
}

/// <summary>
/// The 30-day retention of one application. Day 0 is its first day of use; it is retained when it
/// was used on at least one of days 28 to 34 — the fifth week, so an application used once a week is
/// not lost to which weekday it happens to fall on.
/// </summary>
public sealed record Retention(RetentionState State, int? Day)
{
    /// <summary>First day of the window, counted from day 0.</summary>
    public const int WindowStart = 28;

    /// <summary>Last day of the window, counted from day 0.</summary>
    public const int WindowEnd = 34;

    /// <summary>Judges <paramref name="usedDays"/> (days of use, any order) as of <paramref name="today"/>.</summary>
    public static Retention Of(IReadOnlyCollection<DateOnly> usedDays, DateOnly today)
    {
        if (usedDays.Count == 0) return new Retention(RetentionState.NotStarted, null);
        var first = usedDays.Min();
        var day = today.DayNumber - first.DayNumber;
        if (usedDays.Any(d => d.DayNumber - first.DayNumber is >= WindowStart and <= WindowEnd))
            return new Retention(RetentionState.Retained, day);
        return new Retention(day switch
        {
            < WindowStart => RetentionState.TooEarly,
            <= WindowEnd => RetentionState.InWindow,
            _ => RetentionState.Lapsed,
        }, day);
    }
}
