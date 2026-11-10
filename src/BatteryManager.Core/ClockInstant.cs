namespace BatteryManager.Core;

// Calendar time is retained for display. Elapsed time owns durations and ordering,
// so a Windows clock correction cannot extend a cache or reject a new read.
public readonly record struct ClockInstant(DateTimeOffset Utc, TimeSpan Elapsed)
{
    public static ClockInstant Read(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new(clock.GetUtcNow(), clock.GetElapsedTime(0, clock.GetTimestamp()));
    }

    // Compatibility for callers that explicitly model a single UTC test timeline.
    public static ClockInstant FromUtc(DateTimeOffset utc) => new(utc, utc - DateTimeOffset.UnixEpoch);
}
