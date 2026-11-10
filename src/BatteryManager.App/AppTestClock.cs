namespace BatteryManager.App;

internal sealed record AppTestClockState(double OffsetSeconds, DateTimeOffset Utc);

// Only the opt-in app test host can change this offset. Timers and elapsed time
// still use the real clock; the Windows system date is never modified.
internal sealed class AppTestClock : TimeProvider
{
    private long _offsetTicks;
    public override DateTimeOffset GetUtcNow() => System.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));
    public override long GetTimestamp() => System.GetTimestamp();
    public override long TimestampFrequency => System.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => System.LocalTimeZone;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, dueTime, period);

    public void SetOffset(string action) => Interlocked.Exchange(ref _offsetTicks, action switch
    {
        "back-hour" => -TimeSpan.TicksPerHour,
        "forward-hour" => TimeSpan.TicksPerHour,
        "reset" => 0,
        _ => throw new ArgumentException("Use back-hour, forward-hour or reset.")
    });

    public AppTestClockState Capture() => new(TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks)).TotalSeconds, GetUtcNow());
}
