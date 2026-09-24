using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Tests.Usage;

public sealed class RetentionTests
{
    private static readonly DateOnly D0 = new(2026, 9, 1);

    private static Retention Judge(int today, params int[] used) =>
        Retention.Of(used.Select(d => D0.AddDays(d)).ToList(), D0.AddDays(today));

    [Fact]
    public void Never_used_has_not_started() =>
        Assert.Equal(new Retention(RetentionState.NotStarted, null), Retention.Of([], D0));

    [Fact]
    public void Before_day_28_it_is_too_early_whatever_the_use() =>
        Assert.Equal(new Retention(RetentionState.TooEarly, 27), Judge(27, 0, 5, 12, 27));

    [Fact]
    public void Inside_the_window_without_use_there_it_is_open()
    {
        Assert.Equal(new Retention(RetentionState.InWindow, 28), Judge(28, 0, 27));
        Assert.Equal(new Retention(RetentionState.InWindow, 34), Judge(34, 0, 20));
    }

    [Theory]
    [InlineData(28)]
    [InlineData(31)]
    [InlineData(34)]
    public void One_day_of_use_in_days_28_to_34_retains(int usedOn) =>
        Assert.Equal(RetentionState.Retained, Judge(40, 0, usedOn).State);

    [Fact]
    public void Use_only_outside_the_window_lapses()
    {
        Assert.Equal(new Retention(RetentionState.Lapsed, 35), Judge(35, 0, 27));
        Assert.Equal(RetentionState.Lapsed, Judge(60, 0, 27, 35, 50).State);
    }

    [Fact]
    public void Day_zero_is_the_first_day_of_use_not_the_order_given() =>
        Assert.Equal(new Retention(RetentionState.Retained, 30), Judge(30, 29, 0));
}
