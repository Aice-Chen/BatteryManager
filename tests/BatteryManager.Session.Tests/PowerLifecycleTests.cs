using BatteryManager.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BatteryManager.Session.Tests;

public sealed class PowerLifecycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SuspensionBlocksManualAndPeriodicReadsAndPollingContinuesAfterResume()
    {
        var clock = new FakeTimeProvider(Start);
        var source = new Source((_, publish, _) =>
        {
            publish(Device(clock.GetUtcNow(), 73));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        await Settled(session);
        session.StartPolling();
        session.Suspend();
        Assert.True(session.Snapshot.IsSuspended);
        Assert.Equal("Suspended", (await session.RefreshAsync()).Outcome);
        Assert.Equal("Suspended", (await session.RefreshAsync(manual: false)).Outcome);
        for (var step = 0; step < 4; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }
        Assert.Equal(1, source.Calls);

        Assert.Equal("Completed", (await session.ResumeAsync().WaitAsync(TestDeadline)).Outcome);
        await Settled(session);
        Assert.False(session.Snapshot.IsSuspended);
        var callsAfterResume = source.Calls;
        await AdvanceUntil(clock, () => source.Calls > callsAfterResume, 40);
        await Settled(session);
        Assert.Equal(1, source.MaximumActive);
        Assert.Equal(73, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
        await session.StopAndDrainAsync().WaitAsync(TestDeadline);
    }

    [Fact]
    public async Task BatteryExpiresDuringLongSuspensionThroughSubscriptionAndResumeReadsAgain()
    {
        var clock = new FakeTimeProvider(Start);
        var source = new Source((call, publish, _) =>
        {
            publish(Device(clock.GetUtcNow(), call == 1 ? 73 : 81));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        await using var subscriber = session.Subscribe().GetAsyncEnumerator();
        Assert.True(await subscriber.MoveNextAsync());
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var initial = await Until(subscriber, s => s.OutstandingOperations == 0 && s.State.Devices.Count == 1);
        var originalExpiry = Assert.Single(Assert.Single(initial.State.Devices).Readings).ExpiresAt;
        Assert.Equal(Start.AddMinutes(15), originalExpiry);
        session.Suspend();
        await Until(subscriber, s => s.IsSuspended);

        // No Snapshot getter or new scan is used to force expiry. A sleeping app
        // must publish elapsed-time changes once its timers can execute again.
        var expiredTask = Until(subscriber, s => s.IsSuspended && s.State.Devices.Any(d => d.Freshness == BatteryFreshness.Expired));
        for (var step = 0; step < 17 && !expiredTask.IsCompleted; step++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(10);
        }
        var expired = Assert.Single((await expiredTask).State.Devices);
        Assert.Null(expired.BatteryPercent);
        Assert.Equal(originalExpiry, Assert.Single(expired.Readings).ExpiresAt);
        Assert.Equal(1, source.Calls);

        Assert.Equal("Completed", (await session.ResumeAsync().WaitAsync(TestDeadline)).Outcome);
        var restored = await Until(subscriber, s => !s.IsSuspended && s.OutstandingOperations == 0 && s.State.Devices.Any(d => d.BatteryPercent == 81));
        var device = Assert.Single(restored.State.Devices);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(clock.GetUtcNow().AddMinutes(15), Assert.Single(device.Readings).ExpiresAt);
        await session.StopAndDrainAsync().WaitAsync(TestDeadline);
    }

    [Fact]
    public async Task ResumePublishesClockJumpExpiryBeforeTheFreshReadCompletes()
    {
        var clock = new SleepingClock(Start);
        var freshEntered = Signal();
        var freshRelease = Signal();
        var source = new Source(async (call, publish, _) =>
        {
            if (call > 1) { freshEntered.TrySetResult(); await freshRelease.Task; }
            publish(Device(clock.GetUtcNow(), call == 1 ? 73 : 64));
        });
        await using var session = new BatterySession(source, clock);
        await using var subscriber = session.Subscribe().GetAsyncEnumerator();
        Assert.True(await subscriber.MoveNextAsync());
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var initial = await Until(subscriber, s => s.OutstandingOperations == 0 && s.State.Devices.Count == 1);
        var expiry = Assert.Single(Assert.Single(initial.State.Devices).Readings).ExpiresAt;
        session.Suspend();
        await Until(subscriber, s => s.IsSuspended);
        // Unlike advancing a fake timer, this advances UTC without executing any
        // timer callback, matching the first application work after a long sleep.
        clock.PassSleep(TimeSpan.FromMinutes(16));
        var resume = session.ResumeAsync();
        try
        {
            var expired = await Until(subscriber, s => !s.IsSuspended && s.State.Devices.Any(d => d.Freshness == BatteryFreshness.Expired));
            Assert.Null(Assert.Single(expired.State.Devices).BatteryPercent);
            Assert.Equal(expiry, Assert.Single(Assert.Single(expired.State.Devices).Readings).ExpiresAt);
            await freshEntered.Task.WaitAsync(TestDeadline);
            Assert.False(resume.IsCompleted);
            freshRelease.TrySetResult();
            Assert.Equal("Completed", (await resume.WaitAsync(TestDeadline)).Outcome);
            await Until(subscriber, s => s.OutstandingOperations == 0 && s.State.Devices.Any(d => d.BatteryPercent == 64));
        }
        finally { freshRelease.TrySetResult(); await session.StopAndDrainAsync().WaitAsync(TestDeadline); }
    }

    [Fact]
    public async Task ResumeWithoutSuspendNotificationClosesOldScanBeforeStartingFreshRead()
    {
        var entered = Signal();
        var release = Signal();
        var source = new Source(async (call, publish, _) =>
        {
            if (call == 1)
            {
                publish(Device(DateTimeOffset.UtcNow, 73));
                entered.TrySetResult();
                await release.Task;
                publish(Device(DateTimeOffset.UtcNow, 1));
            }
            else publish(Device(DateTimeOffset.UtcNow, 64));
        });
        await using var session = new BatterySession(source);
        var oldRead = session.RefreshAsync();
        await entered.Task.WaitAsync(TestDeadline);
        try
        {
            var resume = session.ResumeAsync();
            Assert.Equal("Canceled", (await oldRead.WaitAsync(TestDeadline)).Outcome);
            Assert.False(resume.IsCompleted);
            Assert.Equal(1, source.Calls);
            Assert.Equal("Busy", (await session.RefreshAsync()).Outcome);
            release.TrySetResult();
            Assert.Equal("Completed", (await resume.WaitAsync(TestDeadline)).Outcome);
            await Settled(session);
            Assert.Equal(64, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
            Assert.Equal(2, source.Calls);
            Assert.Equal(1, source.MaximumActive);
        }
        finally { release.TrySetResult(); await session.StopAndDrainAsync().WaitAsync(TestDeadline); }
    }

    [Fact]
    public async Task ResumeWaitsForPhysicalCompletionRejectsLateDataAndCoalescesDuplicateRequests()
    {
        var clock = new FakeTimeProvider(Start);
        var oldEntered = Signal();
        var oldRelease = Signal();
        var freshEntered = Signal();
        var freshRelease = Signal();
        var source = new Source(async (call, publish, _) =>
        {
            if (call == 1)
            {
                publish(Device(clock.GetUtcNow(), 73));
                oldEntered.TrySetResult();
                await oldRelease.Task;
                publish(Device(clock.GetUtcNow(), 1));
                publish(new("power.reader", [], Complete: true, Authoritative: true));
            }
            else
            {
                freshEntered.TrySetResult();
                await freshRelease.Task;
                publish(Device(clock.GetUtcNow(), 64));
            }
        });
        await using var session = new BatterySession(source, clock);
        var oldRead = session.RefreshAsync();
        await oldEntered.Task.WaitAsync(TestDeadline);
        try
        {
            session.Suspend();
            Assert.Equal("Canceled", (await oldRead.WaitAsync(TestDeadline)).Outcome);
            var resumes = Enumerable.Range(0, 20).Select(_ => session.ResumeAsync()).ToArray();
            Assert.All(resumes, result => Assert.False(result.IsCompleted));
            Assert.Equal("Busy", (await session.RefreshAsync()).Outcome);
            Assert.Equal(1, source.Calls);
            Assert.Equal(1, session.Snapshot.OutstandingOperations);

            oldRelease.TrySetResult();
            await freshEntered.Task.WaitAsync(TestDeadline);
            Assert.Equal(1, source.MaximumActive);
            Assert.Equal(73, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
            var duplicateDuringFreshRead = session.ResumeAsync();
            freshRelease.TrySetResult();
            var results = await Task.WhenAll(resumes.Append(duplicateDuringFreshRead)).WaitAsync(TestDeadline);
            Assert.All(results, result => Assert.Equal("Completed", result.Outcome));
            Assert.Single(results.Select(result => result.Generation).Distinct());
            await Settled(session);
            Assert.Equal(2, source.Calls);
            Assert.Equal(64, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
        }
        finally
        {
            oldRelease.TrySetResult();
            freshRelease.TrySetResult();
            await session.StopAndDrainAsync().WaitAsync(TestDeadline);
        }
    }

    [Fact]
    public async Task SuspendingAgainCancelsQueuedResumeAndOnlyNewestResumeCanStartHardware()
    {
        var entered = Signal();
        var release = Signal();
        var source = new Source(async (call, publish, _) =>
        {
            if (call == 1) { entered.TrySetResult(); await release.Task; }
            publish(Device(DateTimeOffset.UtcNow, call == 1 ? 2 : 44));
        });
        await using var session = new BatterySession(source);
        var oldRead = session.RefreshAsync();
        await entered.Task.WaitAsync(TestDeadline);
        try
        {
            session.Suspend();
            var abandoned = session.ResumeAsync();
            session.Suspend();
            Assert.Equal("Canceled", (await abandoned.WaitAsync(TestDeadline)).Outcome);
            Assert.True(session.Snapshot.IsSuspended);
            var latest = session.ResumeAsync();
            Assert.False(latest.IsCompleted);
            Assert.Equal(1, source.Calls);
            release.TrySetResult();
            Assert.Equal("Canceled", (await oldRead.WaitAsync(TestDeadline)).Outcome);
            Assert.Equal("Completed", (await latest.WaitAsync(TestDeadline)).Outcome);
            await Settled(session);
            Assert.Equal(2, source.Calls);
            Assert.Equal(1, source.MaximumActive);
            Assert.Equal(44, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
        }
        finally { release.TrySetResult(); await session.StopAndDrainAsync().WaitAsync(TestDeadline); }
    }

    [Fact]
    public async Task StoppingWithQueuedResumeWaitsForHardwareWithoutStartingAnotherScan()
    {
        var entered = Signal();
        var release = Signal();
        var source = new Source(async (_, publish, _) =>
        {
            publish(Device(DateTimeOffset.UtcNow, 73));
            entered.TrySetResult();
            await release.Task;
            publish(Device(DateTimeOffset.UtcNow, 1));
        });
        await using var session = new BatterySession(source,
            options: SessionOptions.Default with { ShutdownTimeout = TimeSpan.FromMilliseconds(20) });
        var oldRead = session.RefreshAsync();
        await entered.Task.WaitAsync(TestDeadline);
        try
        {
            session.Suspend();
            var resume = session.ResumeAsync();
            var stop = session.StopAndDrainAsync();
            Assert.False(stop.IsCompleted);
            Assert.Equal(1, session.Snapshot.OutstandingOperations);
            Assert.Equal("Stopped", (await session.RefreshAsync()).Outcome);
            Assert.Equal("Stopped", (await session.ResumeAsync()).Outcome);
            release.TrySetResult();
            await stop.WaitAsync(TestDeadline);
            Assert.NotEqual("Completed", (await resume.WaitAsync(TestDeadline)).Outcome);
            Assert.Equal("Canceled", (await oldRead.WaitAsync(TestDeadline)).Outcome);
            Assert.Equal(0, session.Snapshot.OutstandingOperations);
            Assert.Equal(1, source.Calls);
            Assert.Equal(73, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
        }
        finally { release.TrySetResult(); await session.StopAndDrainAsync().WaitAsync(TestDeadline); }
    }

    [Fact]
    public async Task SuspendDoesNotWaitForBlockedNativeCancellationCallback()
    {
        var entered = Signal();
        var callbackEntered = Signal();
        var releaseRead = Signal();
        using var releaseCallback = new ManualResetEventSlim();
        var source = new Source(async (_, _, token) =>
        {
            using var registration = token.Register(() => { callbackEntered.TrySetResult(); releaseCallback.Wait(); });
            entered.TrySetResult();
            await releaseRead.Task;
        });
        await using var session = new BatterySession(source);
        var read = session.RefreshAsync();
        await entered.Task.WaitAsync(TestDeadline);
        try
        {
            await Task.Run(session.Suspend).WaitAsync(TestDeadline);
            Assert.Equal("Canceled", (await read.WaitAsync(TestDeadline)).Outcome);
            await callbackEntered.Task.WaitAsync(TestDeadline);
            Assert.True(session.Snapshot.IsSuspended);
            Assert.Equal(1, session.Snapshot.OutstandingOperations);
        }
        finally
        {
            releaseCallback.Set();
            releaseRead.TrySetResult();
            await session.StopAndDrainAsync().WaitAsync(TestDeadline);
        }
    }

    [Fact]
    public async Task ManySuspendResumeCyclesKeepOneDeviceAndDeliverLatestStateToSlowSubscriber()
    {
        var clock = new FakeTimeProvider(Start);
        var source = new Source((call, publish, _) =>
        {
            publish(Device(clock.GetUtcNow(), call % 101));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        await using var subscriber = session.Subscribe().GetAsyncEnumerator();
        Assert.True(await subscriber.MoveNextAsync());
        string? deviceId = null;
        for (var cycle = 0; cycle < 200; cycle++)
        {
            session.Suspend();
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("Completed", (await session.ResumeAsync().WaitAsync(TestDeadline)).Outcome);
            await Settled(session);
            var current = Assert.Single(session.Snapshot.State.Devices);
            deviceId ??= current.Id;
            Assert.Equal(deviceId, current.Id);
        }
        Assert.True(await subscriber.MoveNextAsync().AsTask().WaitAsync(TestDeadline));
        var last = subscriber.Current;
        Assert.False(last.IsSuspended);
        Assert.False(last.IsRefreshing);
        Assert.Equal(0, last.OutstandingOperations);
        Assert.Equal(200 % 101, Assert.Single(last.State.Devices).BatteryPercent);
        Assert.Equal(200, source.Calls);
        Assert.Equal(1, source.MaximumActive);
        await session.StopAndDrainAsync().WaitAsync(TestDeadline);
    }

    private static SourceBatch Device(DateTimeOffset at, int percent) => new("power.reader",
        [new("bt-classic:001122334455", null, "001122334455", "Power test keyboard", DeviceKind.Keyboard,
            "power.reader", at, Connected: true, PathPresent: true,
            Battery: new(percent, BatteryEvidence.ActiveRead, at))], Complete: true, Authoritative: true);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Settled(BatterySession session)
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        while (session.Snapshot.OutstandingOperations != 0 || session.Snapshot.IsRefreshing)
            await Task.Delay(5, deadline.Token);
    }

    private static async Task AdvanceUntil(FakeTimeProvider clock, Func<bool> predicate, int maximumSeconds)
    {
        for (var second = 0; second < maximumSeconds && !predicate(); second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }
        Assert.True(predicate(), "Periodic reading did not resume within its interval.");
    }

    private static async Task<SessionSnapshot> Until(IAsyncEnumerator<SessionSnapshot> subscriber, Func<SessionSnapshot, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        while (await subscriber.MoveNextAsync().AsTask().WaitAsync(deadline.Token))
            if (predicate(subscriber.Current)) return subscriber.Current;
        throw new InvalidOperationException("The session ended before the expected update.");
    }

    private sealed class Source(Func<int, Action<SourceBatch>, CancellationToken, Task> action) : IDeviceSource
    {
        private int _calls;
        private int _active;
        private int _maximumActive;
        public string Name => "PowerLifecycleTest";
        public int Calls => Volatile.Read(ref _calls);
        public int MaximumActive => Volatile.Read(ref _maximumActive);
        public async Task ReadAsync(ScanContext context, Action<SourceBatch> publish, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            int previous;
            do { previous = Volatile.Read(ref _maximumActive); }
            while (active > previous && Interlocked.CompareExchange(ref _maximumActive, active, previous) != previous);
            try { await action(call, publish, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class SleepingClock(DateTimeOffset initial) : TimeProvider
    {
        private readonly FakeTimeProvider _timers = new(initial);
        private long _elapsedTicks;
        public void PassSleep(TimeSpan elapsed) => Interlocked.Add(ref _elapsedTicks, elapsed.Ticks);
        public override DateTimeOffset GetUtcNow() => initial.AddTicks(Interlocked.Read(ref _elapsedTicks));
        // Windows QPC includes sleep even while timer callbacks cannot run.
        public override long GetTimestamp() => Interlocked.Read(ref _elapsedTicks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timers.CreateTimer(callback, state, dueTime, period);
    }
}
