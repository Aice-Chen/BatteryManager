using BatteryManager.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BatteryManager.Session.Tests;

public sealed class OfflineCleanupSessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConfirmedOfflineRecordIsRemovedByExpirationSubscriptionWithoutAnotherScan()
    {
        var clock = new FakeTimeProvider(Start);
        var connected = true;
        var source = new Source((publish, _) =>
        {
            var now = clock.GetUtcNow();
            publish(new("reader", [Observation(now, connected,
                connected ? new(70, BatteryEvidence.ActiveRead, now) : null)], Complete: true, Authoritative: true));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        await using var subscriber = session.Subscribe().GetAsyncEnumerator();
        Assert.True(await subscriber.MoveNextAsync());

        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        await Until(subscriber, snapshot => !snapshot.IsRefreshing && snapshot.OutstandingOperations == 0 && snapshot.State.Devices.Any(device => device.Connection == ConnectionState.Online));
        connected = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var offline = await Until(subscriber, snapshot => !snapshot.IsRefreshing && snapshot.OutstandingOperations == 0 && snapshot.State.Devices.Any(device => device.Connection == ConnectionState.Offline));
        Assert.Equal(70, Assert.Single(offline.State.Devices).BatteryPercent);
        var expiry = Assert.Single(Assert.Single(offline.State.Devices).Readings).ExpiresAt;
        Assert.Equal(Start.AddMinutes(15), expiry);

        // Exercise the timer/subscription route. No polling and no Snapshot getter
        // are used to force the final cleanup or publication.
        var removed = Until(subscriber, snapshot => snapshot.State.DecidedAt >= expiry && snapshot.State.Devices.Count == 0);
        for (var step = 0; step < 30 && !removed.IsCompleted; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(5);
        }
        var clean = await removed;
        Assert.True(clean.State.Version > offline.State.Version);
        Assert.Empty(clean.State.Devices);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task RepeatedOfflineWindowsNodesStayAbsentAndFreshConnectionCanBeRediscovered()
    {
        var clock = new FakeTimeProvider(Start);
        var connected = false;
        var source = new Source((publish, _) =>
        {
            var now = clock.GetUtcNow();
            // The property intentionally arrives first, matching the Windows
            // parallel-reader ordering that previously exposed history nodes.
            publish(new("windows.pnp", [Observation(now, null, new(connected ? 45 : 80, BatteryEvidence.SystemCache, ReadAt: now)) with { Source = "windows.pnp" }]));
            publish(new("windows.aep", [Observation(now, connected, null) with { Source = "windows.aep" }]));
            return Task.CompletedTask;
        });
        await using var session = new BatterySession(source, clock);
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        Assert.Empty(session.Snapshot.State.Devices);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        Assert.Empty(session.Snapshot.State.Devices);

        connected = true;
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var device = Assert.Single(session.Snapshot.State.Devices);
        Assert.True(device.Visible);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(45, device.BatteryPercent);
        Assert.DoesNotContain(device.Readings, reading => reading.Percent == 80);
    }

    private static DeviceObservation Observation(DateTimeOffset at, bool? connected, BatterySample? battery) =>
        new("bt-classic:001122334455", "a71e0da2-9b41-482a-933b-328f661c1801", "001122334455",
            "Test keyboard", DeviceKind.Keyboard, "reader", at, connected, true, battery);

    private static async Task<SessionSnapshot> Until(IAsyncEnumerator<SessionSnapshot> subscriber, Func<SessionSnapshot, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await subscriber.MoveNextAsync().AsTask().WaitAsync(deadline.Token))
            if (predicate(subscriber.Current)) return subscriber.Current;
        throw new InvalidOperationException("The session ended before the expected state was published.");
    }

    private sealed class Source(Func<Action<SourceBatch>, CancellationToken, Task> action) : IDeviceSource
    {
        public string Name => "OfflineCleanupTest";
        public int Calls;
        public Task ReadAsync(ScanContext context, Action<SourceBatch> publish, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return action(publish, cancellationToken);
        }
    }
}
