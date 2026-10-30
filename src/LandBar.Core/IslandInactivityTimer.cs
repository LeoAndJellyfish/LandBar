namespace LandBar.Core;

/// <summary>A one-shot inactivity deadline driven by the caller's monotonic clock.</summary>
public sealed class IslandInactivityTimer
{
    public IslandInactivityTimer(TimeSpan? timeout = null)
    {
        Timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public TimeSpan? Timeout { get; private set; }
    public TimeSpan? Deadline { get; private set; }

    /// <summary>Null disables automatic collapse. A changed duration starts a full new interval.</summary>
    public void Configure(TimeSpan? timeout, TimeSpan now)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (now < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(now));
        Timeout = timeout;
        Reset(now);
    }

    public void Reset(TimeSpan now)
    {
        if (now < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(now));
        Deadline = Timeout is { } timeout ? now + timeout : null;
    }

    public void Stop() => Deadline = null;

    public bool IsExpired(TimeSpan now) => Deadline is { } deadline && now >= deadline;
}
