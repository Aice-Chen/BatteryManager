using BatteryManager.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BatteryManager.Session.Tests;

public sealed class ClockSessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task NewReadAfterCalendarCorrectionKeepsIdentityAndReportsItsActualUtc(int hours)
    {
        var clock = new AdjustableClock();
        var percent = 73;
        var source = new Source((publish, _) =>
        {
            publish(Device(ClockInstant.Read(clock), percent));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        await Refresh(session);
        var first = Assert.Single(session.Snapshot.State.Devices);
        clock.Advance(TimeSpan.FromSeconds(30));
        clock.ShiftUtc(TimeSpan.FromHours(hours));
        percent = 64;
        await Refresh(session);
        var snapshot = session.Snapshot;
        var device = Assert.Single(snapshot.State.Devices);
        Assert.Equal(first.Id, device.Id);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(64, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, device.Freshness);
        var reading = Assert.Single(device.Readings);
        Assert.Equal(clock.GetUtcNow(), reading.ReceivedAt);
        Assert.Equal(clock.GetUtcNow(), reading.ObservedAt);
        Assert.Equal(clock.GetUtcNow().AddMinutes(15), reading.ExpiresAt);
        Assert.Equal(clock.GetUtcNow(), snapshot.State.DecidedAt);
        await session.StopAndDrainAsync().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task CalendarJumpDoesNotChangeFifteenMinuteBatteryLifetimeOrOriginalReadUtc(int hours)
    {
        var clock = new AdjustableClock();
        var source = new Source((publish, _) =>
        {
            publish(Device(ClockInstant.Read(clock), 73));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        await using var updates = session.Subscribe().GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        await Refresh(session);
        await Until(updates, s => s.OutstandingOperations == 0 && s.State.Devices.Count == 1);
        clock.ShiftUtc(TimeSpan.FromHours(hours));
        clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));
        var before = await Until(updates, s => s.State.DecidedAt == clock.GetUtcNow());
        var stillUsable = Assert.Single(before.State.Devices);
        Assert.Equal(73, stillUsable.BatteryPercent);
        Assert.Equal(Start, Assert.Single(stillUsable.Readings).ReceivedAt);
        Assert.Equal(clock.GetUtcNow().AddSeconds(1), Assert.Single(stillUsable.Readings).ExpiresAt);

        // An already queued expiry iteration may still be choosing its next delay;
        // yielding before the final elapsed second keeps the assertion about the
        // subscription, rather than using Snapshot to force recomputation.
        await Task.Delay(20);
        clock.Advance(TimeSpan.FromSeconds(1));
        var expired = await Until(updates, s => s.State.Devices.Any(d => d.Freshness == BatteryFreshness.Expired));
        Assert.Null(Assert.Single(expired.State.Devices).BatteryPercent);
        Assert.Equal(Start, Assert.Single(Assert.Single(expired.State.Devices).Readings).ReceivedAt);
        await session.StopAndDrainAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task CalendarCorrectionBetweenReadCompletionAndPublishDoesNotMakeTheReplyFutureDated()
    {
        var clock = new AdjustableClock();
        var source = new Source((publish, _) =>
        {
            var completed = ClockInstant.Read(clock);
            clock.ShiftUtc(TimeSpan.FromHours(-1));
            publish(Device(completed, 73));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        await Refresh(session);
        var snapshot = session.Snapshot;
        var device = Assert.Single(snapshot.State.Devices);
        Assert.Equal(73, device.BatteryPercent);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(BatteryFreshness.Current, device.Freshness);
        var reading = Assert.Single(device.Readings);
        Assert.Equal(Start, reading.ReceivedAt);
        Assert.Equal(Start, reading.ObservedAt);
        Assert.Equal(ReadStatus.Success, reading.Attempt);
        Assert.Equal(clock.GetUtcNow(), snapshot.State.DecidedAt);
        Assert.Equal(clock.GetUtcNow().AddMinutes(15), reading.ExpiresAt);
        await session.StopAndDrainAsync().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task ScanDeadlineUsesElapsedTimeAcrossUtcJumpAndStillRejectsLateData(int hours)
    {
        var clock = new AdjustableClock();
        var entered = Signal();
        var publishAllowed = Signal();
        var published = Signal();
        var release = Signal();
        var source = new Source(async (publish, _) =>
        {
            entered.TrySetResult();
            await publishAllowed.Task;
            publish(Device(ClockInstant.Read(clock), 73));
            published.TrySetResult();
            await release.Task;
            publish(Device(ClockInstant.Read(clock), 1));
        });
        await using var session = new BatterySession(source, clock);
        var refresh = session.RefreshAsync();
        await entered.Task.WaitAsync(Deadline);
        try
        {
            clock.ShiftUtc(TimeSpan.FromHours(hours));
            clock.Advance(TimeSpan.FromSeconds(9));
            publishAllowed.TrySetResult();
            await published.Task.WaitAsync(Deadline);
            Assert.False(refresh.IsCompleted);
            Assert.Equal(73, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("TimedOut", (await refresh.WaitAsync(Deadline)).Outcome);
            Assert.Equal(1, session.Snapshot.OutstandingOperations);
            Assert.Equal("Busy", (await session.RefreshAsync()).Outcome);
            release.TrySetResult();
            await Settled(session);
            Assert.Equal(73, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
        }
        finally
        {
            publishAllowed.TrySetResult();
            release.TrySetResult();
            await session.StopAndDrainAsync().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task RepeatedSourceTimestampCannotRenewOriginalLifetimeAfterCalendarJump(int hours)
    {
        var clock = new AdjustableClock();
        var sourceTime = Start.AddMinutes(-1);
        var source = new Source((publish, _) =>
        {
            var at = ClockInstant.Read(clock);
            publish(Device(at, 73, new(73, BatteryEvidence.SourceTimestamp, sourceTime,
                ReadAt: at.Utc, ReadElapsed: at.Elapsed)));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        await Refresh(session);
        clock.Advance(TimeSpan.FromMinutes(10));
        clock.ShiftUtc(TimeSpan.FromHours(hours));
        await Refresh(session);
        var repeated = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(73, repeated.BatteryPercent);
        Assert.Equal(sourceTime, Assert.Single(repeated.Readings, r => r.ExpiresAt is not null).ObservedAt);
        clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(59));
        Assert.Equal(73, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
        await session.StopAndDrainAsync().WaitAsync(Deadline);
    }

    private static SourceBatch Device(ClockInstant at, int percent, BatterySample? sample = null) => new("clock.reader",
        [new("bt-classic:001122334455", null, "001122334455", "Clock test keyboard", DeviceKind.Keyboard,
            "clock.reader", at.Utc, Connected: true, PathPresent: true,
            Battery: sample ?? new(percent, BatteryEvidence.ActiveRead, at.Utc, ReadAt: at.Utc, ReadElapsed: at.Elapsed),
            ReceivedElapsed: at.Elapsed)], Complete: true, Authoritative: true);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Refresh(BatterySession session)
    {
        Assert.Equal("Completed", (await session.RefreshAsync().WaitAsync(Deadline)).Outcome);
        await Settled(session);
    }

    private static async Task Settled(BatterySession session)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (session.Snapshot.OutstandingOperations != 0 || session.Snapshot.IsRefreshing)
            await Task.Delay(5, deadline.Token);
    }

    private static async Task<SessionSnapshot> Until(IAsyncEnumerator<SessionSnapshot> updates, Func<SessionSnapshot, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (await updates.MoveNextAsync().AsTask().WaitAsync(deadline.Token))
            if (predicate(updates.Current)) return updates.Current;
        throw new InvalidOperationException("The session stopped before the expected clock behavior was observed.");
    }

    private sealed class Source(Func<Action<SourceBatch>, CancellationToken, Task> read) : IDeviceSource
    {
        public string Name => "ClockSessionTest";
        public Task ReadAsync(ScanContext context, Action<SourceBatch> publish, CancellationToken cancellationToken) => read(publish, cancellationToken);
    }

    // Real elapsed time and wall-clock correction are deliberately independent.
    private sealed class AdjustableClock : TimeProvider
    {
        private readonly FakeTimeProvider _timers = new(Start);
        private long _offsetTicks;
        public void ShiftUtc(TimeSpan difference) => Interlocked.Add(ref _offsetTicks, difference.Ticks);
        public void Advance(TimeSpan elapsed) => _timers.Advance(elapsed);
        public override DateTimeOffset GetUtcNow() => _timers.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));
        public override long GetTimestamp() => _timers.GetTimestamp();
        public override long TimestampFrequency => _timers.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timers.CreateTimer(callback, state, dueTime, period);
    }
}
