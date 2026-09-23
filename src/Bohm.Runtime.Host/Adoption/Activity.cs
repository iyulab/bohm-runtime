namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Counts storage requests in progress, so that shutting down can wait until the last ones — often
/// sent by pages in the moment they close — have been written.
/// </summary>
internal sealed class Activity(TimeProvider clock)
{
    private int _inProgress;
    private long _lastChangeTicks = clock.GetTimestamp();

    public IDisposable Begin()
    {
        Interlocked.Increment(ref _inProgress);
        Touch();
        return new Scope(this);
    }

    /// <summary>
    /// Waits until no request has been in progress for <paramref name="quiet"/>, or until
    /// <paramref name="limit"/> passes. Returns whether it became quiet.
    /// </summary>
    public async Task<bool> WaitForQuietAsync(TimeSpan quiet, TimeSpan limit, CancellationToken cancellationToken)
    {
        var start = clock.GetTimestamp();
        while (clock.GetElapsedTime(start) < limit)
        {
            if (Volatile.Read(ref _inProgress) == 0 && clock.GetElapsedTime(Interlocked.Read(ref _lastChangeTicks)) >= quiet) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(20), clock, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private void Touch() => Interlocked.Exchange(ref _lastChangeTicks, clock.GetTimestamp());

    private sealed class Scope(Activity owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Touch();
            Interlocked.Decrement(ref owner._inProgress);
        }
    }
}
