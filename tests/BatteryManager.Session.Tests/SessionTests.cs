using BatteryManager.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BatteryManager.Session.Tests;

public sealed class SessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SourceBatch Device(DateTimeOffset now, int percent = 73) => new("fake.raw", [new("bt-le:001122334455", null, "001122334455", "Test keyboard", DeviceKind.Keyboard, "fake.raw", now, true, true, new(percent, BatteryEvidence.ActiveRead, now))]);

    [Fact]
    public async Task SharedScanSurvivesOneWaiterCancel_AndClosedResultIsNotReused()
    {
        var entered = Signal(); var release = Signal();
        var source = new Source(async (_, publish, _) => { entered.TrySetResult(); await release.Task; publish(Device(DateTimeOffset.UtcNow)); });
        await using var session = new BatterySession(source);
        using var cancel = new CancellationTokenSource();
        var first = session.RefreshAsync(cancellationToken: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = session.RefreshAsync();
        await cancel.CancelAsync();
        Assert.Equal("Canceled", (await first).Outcome);
        try
        {
            Assert.False(second.IsCompleted);
            Assert.Equal(1, source.Calls);
            release.TrySetResult();
            Assert.Equal("Completed", (await second.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
            await EventuallyAsync(() => session.Snapshot.OutstandingOperations == 0);
            Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
            Assert.Equal(2, source.Calls);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task TimeoutPreservesPartialResult_BlocksReentry_RejectsLateSuccessAndAbsence()
    {
        var clock = new FakeTimeProvider(Start);
        var entered = Signal(); var release = Signal();
        var source = new Source(async (_, publish, _) =>
        {
            publish(Device(clock.GetUtcNow())); entered.TrySetResult();
            await release.Task;
            publish(Device(clock.GetUtcNow(), 1));
            publish(new("fake.raw", [], true, true));
        });
        await using var session = new BatterySession(source, clock);
        var read = session.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            Assert.Equal("TimedOut", (await read.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
            var device = Assert.Single(session.Snapshot.State.Devices);
            Assert.Equal(73, device.BatteryPercent);
            Assert.Equal(ReadStatus.Success, Assert.Single(device.Readings).Attempt);
            Assert.Equal("Busy", (await session.RefreshAsync()).Outcome);
            release.TrySetResult();
            await EventuallyAsync(() => session.Snapshot.OutstandingOperations == 0);
            Assert.Equal(73, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
            Assert.Equal(1, source.MaximumActive);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CompletedSourceRemainsSuccessfulWhenAnotherSourceTimesOut_AndDrainNotifiesSubscribers()
    {
        var clock = new FakeTimeProvider(Start);
        var entered = Signal(); var release = Signal();
        var source = new Source(async (_, publish, _) =>
        {
            publish(Device(clock.GetUtcNow()) with { Complete = true, Authoritative = true });
            entered.TrySetResult(); await release.Task;
        });
        await using var session = new BatterySession(source, clock);
        await using var subscriber = session.Subscribe().GetAsyncEnumerator();
        Assert.True(await subscriber.MoveNextAsync());
        var read = session.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            Assert.Equal("TimedOut", (await read.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
            Assert.Equal(ReadStatus.Success, Assert.Single(Assert.Single(session.Snapshot.State.Devices).Readings).Attempt);
            Assert.True(await subscriber.MoveNextAsync());
            Assert.Equal(1, subscriber.Current.OutstandingOperations);
            release.TrySetResult();
            using var drainDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            do
            {
                Assert.True(await subscriber.MoveNextAsync().AsTask().WaitAsync(drainDeadline.Token));
            } while (subscriber.Current.OutstandingOperations != 0);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task BlockedCancellationCallbackDoesNotOwnRequestCompletion()
    {
        var clock = new FakeTimeProvider(Start);
        var entered = Signal(); var releaseRead = Signal();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = Signal();
        var source = new Source(async (_, _, token) =>
        {
            using var registration = token.Register(() => { callbackEntered.TrySetResult(); releaseCallback.Wait(); });
            entered.TrySetResult(); await releaseRead.Task;
        });
        await using var session = new BatterySession(source, clock);
        var read = session.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            Assert.Equal("TimedOut", (await read.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("Busy", (await session.RefreshAsync()).Outcome);
        }
        finally { releaseCallback.Set(); releaseRead.TrySetResult(); }
    }

    [Fact]
    public async Task ExpirationStillRunsWhileSourceIsPhysicallyBlocked()
    {
        var clock = new FakeTimeProvider(Start);
        var entered = Signal(); var release = Signal();
        var source = new Source(async (_, publish, _) => { publish(Device(clock.GetUtcNow())); entered.TrySetResult(); await release.Task; });
        await using var session = new BatterySession(source, clock);
        var read = session.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            clock.Advance(TimeSpan.FromMinutes(15));
            await read.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
            Assert.Equal(1, session.Snapshot.OutstandingOperations);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task RenewedWindowsValueExpiresThroughSubscriptionWithoutSnapshotReads()
    {
        var clock = new ScheduledClock(Start);
        var source = new Source((_, publish, _) =>
        {
            var now = clock.GetUtcNow();
            publish(new("windows.pnp", [new("bt-classic:001122334455", null, "001122334455", "Test headset",
                DeviceKind.Headphones, "windows.pnp", now, Connected: true,
                Battery: new(50, BatteryEvidence.SystemCache, ReadAt: now))], Complete: true, Authoritative: true));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        using var subscriptionCancellation = new CancellationTokenSource();
        await using var subscriber = session.Subscribe(subscriptionCancellation.Token).GetAsyncEnumerator();
        Assert.True(await subscriber.MoveNextAsync());
        async Task<SessionSnapshot> Until(Func<SessionSnapshot, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Cancel the actual pending read before disposing its async iterator.
            // Canceling only WaitAsync leaves MoveNext running and masks a timeout with DisposeAsync failure.
            using var registration = timeout.Token.Register(subscriptionCancellation.Cancel);
            while (await subscriber.MoveNextAsync())
                if (predicate(subscriber.Current)) return subscriber.Current;
            throw new InvalidOperationException("The session ended before the expected update.");
        }
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        await Until(snapshot => !snapshot.IsRefreshing && snapshot.OutstandingOperations == 0 && snapshot.State.Devices.Count == 1);
        await clock.AdvanceWhenTimerScheduledAsync(Start.AddMinutes(14));
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var renewed = await Until(snapshot => snapshot.Generation == 2 && !snapshot.IsRefreshing && snapshot.OutstandingOperations == 0);
        Assert.Equal(50, Assert.Single(renewed.State.Devices).BatteryPercent);
        Assert.Equal(Start.AddMinutes(29), Assert.Single(Assert.Single(renewed.State.Devices).Readings).ExpiresAt);

        // Pass the original deadline. The next independent connection expiry must
        // still carry a usable battery, without a query forcing recomputation.
        await clock.AdvanceWhenTimerScheduledAsync(Start.AddMinutes(16));
        var afterOldDeadline = await Until(snapshot => snapshot.State.DecidedAt >= Start.AddMinutes(16));
        Assert.Equal(50, Assert.Single(afterOldDeadline.State.Devices).BatteryPercent);
        await clock.AdvanceWhenTimerScheduledAsync(Start.AddMinutes(29));
        var expired = await Until(snapshot => snapshot.State.Devices.Any(device => device.Freshness == BatteryFreshness.Expired));
        Assert.Null(Assert.Single(expired.State.Devices).BatteryPercent);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task PendingRowHidesThroughSubscriptionAtItsOwnDeadlineAndFreshConnectionRestoresIt()
    {
        var clock = new ScheduledClock(Start);
        var connected = false;
        var source = new Source((_, publish, _) =>
        {
            var at = ClockInstant.Read(clock);
            publish(new("reader", [new("receiver:one", null, null, "Receiver mouse", DeviceKind.Mouse,
                "reader", at.Utc, Connected: connected ? true : null, PathPresent: true,
                Status: ReadStatus.Missing, ReceivedElapsed: at.Elapsed)]));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        using var subscriptionCancellation = new CancellationTokenSource();
        await using var subscriber = session.Subscribe(subscriptionCancellation.Token).GetAsyncEnumerator();
        Assert.True(await subscriber.MoveNextAsync());
        async Task<SessionSnapshot> Until(Func<SessionSnapshot, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var registration = timeout.Token.Register(subscriptionCancellation.Cancel);
            while (await subscriber.MoveNextAsync())
                if (predicate(subscriber.Current)) return subscriber.Current;
            throw new InvalidOperationException("The session ended before the expected visibility change.");
        }

        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var first = await Until(snapshot => !snapshot.IsRefreshing && snapshot.OutstandingOperations == 0 && snapshot.State.Devices.Count == 1);
        Assert.False(Assert.Single(first.State.Devices).Visible);
        Assert.Equal(ConnectionState.Pending, Assert.Single(first.State.Devices).Connection);
        connected = true;
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var admitted = await Until(snapshot => snapshot.Generation == 2 && !snapshot.IsRefreshing && snapshot.OutstandingOperations == 0);
        Assert.True(Assert.Single(admitted.State.Devices).Visible);
        Assert.Null(Assert.Single(admitted.State.Devices).BatteryPercent);
        connected = false;
        // Keep a path across the 01:45 connection-grace boundary so Pending
        // starts there. Its independent visibility deadline is then 16:45.
        await clock.AdvanceWhenTimerScheduledAsync(Start.AddSeconds(90));
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        await Until(snapshot => snapshot.Generation == 3 && !snapshot.IsRefreshing && snapshot.OutstandingOperations == 0);
        await clock.AdvanceWhenTimerScheduledAsync(Start.AddMinutes(16.25));
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var pending = await Until(snapshot => snapshot.Generation == 4 && !snapshot.IsRefreshing && snapshot.OutstandingOperations == 0);
        Assert.True(Assert.Single(pending.State.Devices).Visible);
        Assert.Equal(ConnectionState.Pending, Assert.Single(pending.State.Devices).Connection);
        // No polling is started. The renewed path lasts until 17:00, so only
        // the Pending deadline can produce this update without a new query.
        await clock.AdvanceWhenTimerScheduledAsync(Start.AddMinutes(16.75));
        var hidden = await Until(snapshot => snapshot.State.DecidedAt == Start.AddMinutes(16.75) &&
            snapshot.State.Devices.Any(device => !device.Visible && device.Connection == ConnectionState.Pending));
        Assert.Equal(4, source.Calls);
        Assert.True(hidden.State.Version > pending.State.Version);

        connected = true;
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var restored = await Until(snapshot => snapshot.Generation == 5 && !snapshot.IsRefreshing && snapshot.OutstandingOperations == 0);
        var device = Assert.Single(restored.State.Devices);
        Assert.Equal(Assert.Single(first.State.Devices).Id, device.Id);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.True(device.Visible);
        Assert.Null(device.BatteryPercent);
        Assert.Equal(5, source.Calls);
    }

    [Fact]
    public async Task SlowSubscriberDoesNotBlockScan_AndReceivesLatestCompleteState()
    {
        int percent = 50;
        var source = new Source((_, publish, _) => { publish(Device(DateTimeOffset.UtcNow, Interlocked.Increment(ref percent))); return Task.CompletedTask; });
        await using var session = new BatterySession(source);
        using var cancel = new CancellationTokenSource();
        await using var slow = session.Subscribe(cancel.Token).GetAsyncEnumerator();
        Assert.True(await slow.MoveNextAsync());
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal("Completed", (await session.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
            await EventuallyAsync(() => session.Snapshot.OutstandingOperations == 0);
        }
        Assert.True(await slow.MoveNextAsync());
        Assert.Equal(70, Assert.Single(slow.Current.State.Devices).BatteryPercent);
        Assert.True((await session.StopAsync()).Drained);
    }

    [Fact]
    public async Task StopIsBoundedAndRetainsPhysicalOccupancyUntilActualCompletion()
    {
        var entered = Signal(); var release = Signal();
        var source = new Source(async (_, _, _) => { entered.TrySetResult(); await release.Task; });
        var options = SessionOptions.Default with { ShutdownTimeout = TimeSpan.FromMilliseconds(200) };
        await using var session = new BatterySession(source, options: options);
        var read = session.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.False((await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(3))).Drained);
            Assert.Equal("Canceled", (await read).Outcome);
            Assert.Equal("Stopped", (await session.RefreshAsync()).Outcome);
            Assert.Equal(1, session.Snapshot.OutstandingOperations);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullShutdownWaitsPastLogicalBudgetAndRejectsLateReports(bool sourceFailsAtEnd)
    {
        var entered = Signal(); var release = Signal();
        var source = new Source(async (_, publish, _) =>
        {
            publish(Device(DateTimeOffset.UtcNow, 73));
            entered.TrySetResult();
            await release.Task;
            publish(Device(DateTimeOffset.UtcNow, 1));
            if (sourceFailsAtEnd) throw new IOException("Native operation ended with an error.");
        });
        var options = SessionOptions.Default with { ShutdownTimeout = TimeSpan.FromMilliseconds(20) };
        await using var session = new BatterySession(source, options: options);
        session.StartPolling();
        var read = session.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task? drained = null;
        try
        {
            Assert.False((await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(3))).Drained);
            drained = session.StopAndDrainAsync();
            Assert.False(drained.IsCompleted);
            Assert.Equal(1, session.Snapshot.OutstandingOperations);
            Assert.Equal("Canceled", (await read).Outcome);
            Assert.Equal("Stopped", (await session.RefreshAsync()).Outcome);
            release.TrySetResult();
            await drained.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, session.Snapshot.OutstandingOperations);
            Assert.Equal(73, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
            Assert.Equal(1, source.Calls);
            await session.StopAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            release.TrySetResult();
            if (drained is not null) await drained.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task FullShutdownOfAnIdleSessionCompletesWithoutStartingHardware()
    {
        var source = new Source((_, _, _) => throw new InvalidOperationException("No scan was requested."));
        await using var session = new BatterySession(source);
        session.StartPolling();
        await session.StopAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, source.Calls);
        Assert.Equal(0, session.Snapshot.OutstandingOperations);
        Assert.Equal("Stopped", (await session.RefreshAsync()).Outcome);
    }

    [Fact]
    public async Task ExplicitLinksPublishImmediatelyWithoutStartingAnotherPhysicalScan()
    {
        var source = new Source((_, publish, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            publish(Device(now, 73));
            publish(new("receiver", [new("receiver:test", null, null, "USB keyboard", DeviceKind.Keyboard,
                "receiver", now, true, true, new(72, BatteryEvidence.ActiveRead, now))]));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source);
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        await EventuallyAsync(() => session.Snapshot.OutstandingOperations == 0);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscriber = session.Subscribe(cancel.Token).GetAsyncEnumerator();
        Assert.True(await subscriber.MoveNextAsync());
        var original = subscriber.Current.State;
        Assert.Equal(2, original.Devices.Count);
        var primary = original.Devices[0];
        session.SetDeviceLinks([new(primary.Id, original.DetectedDevices.Select(d => new DeviceLinkMember(d.Id, d.Name)).ToArray())]);
        Assert.True(await subscriber.MoveNextAsync());
        var linked = subscriber.Current.State;
        Assert.True(linked.Version > original.Version);
        Assert.Equal(primary.Id, Assert.Single(linked.Devices).Id);
        Assert.Equal(2, linked.DetectedDevices.Count);
        Assert.Equal(1, source.Calls);
        session.SetDeviceLinks([]);
        Assert.True(await subscriber.MoveNextAsync());
        Assert.Equal(2, subscriber.Current.State.Devices.Count);
        Assert.True(subscriber.Current.State.Version > linked.Version);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task LinkedVisibilityDeadlinePublishesEvenWhenRawMemberDeadlinesHaveAlreadyExpired()
    {
        var clock = new ScheduledClock(Start);
        var firstRound = true;
        var source = new Source((_, publish, _) =>
        {
            var at = ClockInstant.Read(clock);
            var observations = new List<DeviceObservation>
            {
                new("receiver:pending", null, null, "Receiver", DeviceKind.Keyboard, "test", at.Utc,
                    PathPresent: true, Status: ReadStatus.Missing, ReceivedElapsed: at.Elapsed)
            };
            if (firstRound)
            {
                observations.Add(new("bt:active", null, null, "Bluetooth", DeviceKind.Keyboard, "test", at.Utc,
                    Connected: true, PathPresent: true, Status: ReadStatus.Missing, ReceivedElapsed: at.Elapsed));
                firstRound = false;
            }
            publish(new("test", observations));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        using var subscriptionCancellation = new CancellationTokenSource();
        await using var subscriber = session.Subscribe(subscriptionCancellation.Token).GetAsyncEnumerator();
        Assert.True(await subscriber.MoveNextAsync());
        async Task<SessionSnapshot> Until(Func<SessionSnapshot, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var registration = timeout.Token.Register(subscriptionCancellation.Cancel);
            while (await subscriber.MoveNextAsync()) if (predicate(subscriber.Current)) return subscriber.Current;
            throw new InvalidOperationException("The linked visibility update was not published.");
        }
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var original = await Until(s => !s.IsRefreshing && s.OutstandingOperations == 0 && s.State.Devices.Count == 2);
        var primary = original.State.DetectedDevices.Single(d => d.Name == "Receiver");
        session.SetDeviceLinks([new(primary.Id, original.State.DetectedDevices.Select(d => new DeviceLinkMember(d.Id, d.Name)).ToArray())]);
        await Until(s => s.State.Devices.Count == 1);
        // Bluetooth's confirmation ends at 01:45 even though its path is gone.
        // Rediscovering the receiver at 14:30 must retain the 16:45 hide deadline.
        await clock.AdvanceWhenTimerScheduledAsync(Start.AddMinutes(14.5));
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        await Until(s => s.Generation == 2 && !s.IsRefreshing && s.OutstandingOperations == 0);
        await clock.AdvanceWhenTimerScheduledAsync(Start.AddMinutes(16.25));
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var pending = await Until(s => s.Generation == 3 && !s.IsRefreshing && s.OutstandingOperations == 0);
        Assert.True(Assert.Single(pending.State.Devices).Visible);
        Assert.All(pending.State.DetectedDevices, d => Assert.False(d.Visible));
        // The renewed path is valid until 17:00. Only the group visibility
        // deadline can produce the hidden update at 16:45 without a new read.
        await clock.AdvanceWhenTimerScheduledAsync(Start.AddMinutes(16.75));
        var hidden = await Until(s => s.State.DecidedAt == Start.AddMinutes(16.75) &&
            s.State.Devices.Count == 1 && !s.State.Devices[0].Visible && s.State.Devices[0].Connection == ConnectionState.Pending);
        Assert.True(hidden.State.Version > pending.State.Version);
        Assert.Equal(3, source.Calls);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    // A subscription update can arrive before the expiry worker installs its next timer.
    // Jump only when a live timer is ready to fire, under the same lock as timer cancellation.
    // This uses the public TimeProvider boundary; it never queries session state to force expiration.
    private sealed class ScheduledClock(DateTimeOffset start) : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(start);
        private readonly object _gate = new();
        private readonly HashSet<ScheduledTimer> _timers = [];
        private TaskCompletionSource _changed = Signal();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ScheduledTimer(this, callback, state, dueTime, period);
                _timers.Add(timer);
                Changed();
                return timer;
            }
        }
        public async Task AdvanceWhenTimerScheduledAsync(DateTimeOffset target)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_timers.Any(timer => timer.DueAt is { } due && due <= target))
                    {
                        _clock.Advance(target - _clock.GetUtcNow());
                        return;
                    }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(deadline.Token);
            }
        }
        private void Changed() { var previous = _changed; _changed = Signal(); previous.TrySetResult(); }
        private sealed class ScheduledTimer : ITimer
        {
            private readonly ScheduledClock _owner;
            private readonly ITimer _timer;
            private TimeSpan _period;
            public DateTimeOffset? DueAt { get; private set; }
            public ScheduledTimer(ScheduledClock owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                _owner = owner;
                _period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
                _timer = owner._clock.CreateTimer(_ =>
                {
                    lock (owner._gate)
                    {
                        DueAt = _period > TimeSpan.Zero ? owner.GetUtcNow() + _period : null;
                        owner.Changed();
                    }
                    callback(state);
                }, null, dueTime, period);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._gate)
                {
                    if (!_timer.Change(dueTime, period)) return false;
                    _period = period;
                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
                    _owner.Changed();
                    return true;
                }
            }
            public void Dispose()
            {
                lock (_owner._gate) { _timer.Dispose(); _owner._timers.Remove(this); _owner.Changed(); }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Source(Func<ScanContext, Action<SourceBatch>, CancellationToken, Task> action) : IDeviceSource
    {
        public string Name => "TestRaw";
        public int Calls;
        private int active;
        public int MaximumActive;
        public async Task ReadAsync(ScanContext context, Action<SourceBatch> publish, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var count = Interlocked.Increment(ref active);
            MaximumActive = Math.Max(MaximumActive, count);
            try { await action(context, publish, cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
