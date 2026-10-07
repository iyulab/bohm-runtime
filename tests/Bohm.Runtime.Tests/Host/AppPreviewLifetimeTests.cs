using Bohm.Runtime.Host.Adoption;
using Bohm.Runtime.Tests.Storage;

namespace Bohm.Runtime.Tests.Host;

/// <summary>How long a preview is held, and how many — on a clock the test moves.</summary>
public sealed class AppPreviewLifetimeTests
{
    private static readonly byte[] Page = "<!doctype html><title>Proposed</title>"u8.ToArray();

    [Fact]
    public void A_preview_lives_while_it_is_used_and_goes_once_it_is_left_alone_for_its_lifetime()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero));
        var previews = new AppPreviews(clock);
        var token = previews.Create("app", Page);

        // Tried for ten minutes, looked at every half minute — as the shell's watch does while a person tries it.
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.NotNull(previews.Find("app", token));
        }

        clock.Advance(AppPreviews.Lifetime);
        Assert.NotNull(previews.Find("app", token));

        clock.Advance(AppPreviews.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Null(previews.Find("app", token));
        Assert.False(previews.Remove("app", token));
    }

    [Fact]
    public void Making_a_preview_drops_the_expired_ones_and_keeps_the_rest()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero));
        var previews = new AppPreviews(clock);
        var old = previews.Create("app", Page);
        clock.Advance(AppPreviews.Lifetime - TimeSpan.FromSeconds(30));
        var recent = previews.Create("app", Page);

        clock.Advance(TimeSpan.FromMinutes(1));
        var fresh = previews.Create("app", Page);

        Assert.Null(previews.Find(old));
        Assert.NotNull(previews.Find(recent));
        Assert.NotNull(previews.Find(fresh));
    }

    [Fact]
    public void At_most_eight_are_held_and_the_oldest_goes_first()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero));
        var previews = new AppPreviews(clock);
        var tokens = new List<string>();
        for (var i = 0; i < 9; i++)
        {
            tokens.Add(previews.Create("app", Page));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Null(previews.Find(tokens[0]));
        Assert.All(tokens.Skip(1), token => Assert.NotNull(previews.Find(token)));
    }
}
