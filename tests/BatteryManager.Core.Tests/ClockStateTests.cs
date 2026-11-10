using BatteryManager.Core;
using Xunit;

namespace BatteryManager.Core.Tests;

public sealed class ClockStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void NewActiveReadingWinsAfterCalendarCorrectionAndRetainsRealUtc(int correctionHours)
    {
        var store = Begin();
        Put(store, Active(80, At(0)), At(0));
        var later = At(10, correctionHours);
        Put(store, Active(61, later), later);
        var snapshot = store.Snapshot(later);
        var device = Assert.Single(snapshot.Devices);
        var reading = Assert.Single(device.Readings);
        Assert.Equal(61, device.BatteryPercent);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(BatteryFreshness.Current, device.Freshness);
        Assert.Equal(later.Utc, snapshot.DecidedAt);
        Assert.Equal(later.Utc, reading.ObservedAt);
        Assert.Equal(later.Utc, reading.ReceivedAt);
        Assert.Equal(later.Utc.AddMinutes(15), reading.ExpiresAt);
        Assert.Equal(TimeSpan.FromSeconds(45), store.NextExpiryDelay(later));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void CrossSourceChoiceUsesElapsedReadingOrder(int correctionHours)
    {
        var store = Begin();
        Put(store, Active(80, At(0), "a-old"), At(0));
        var later = At(10, correctionHours);
        Put(store, Active(61, later, "z-new"), later);
        var device = Assert.Single(store.Snapshot(later).Devices);
        Assert.Equal(61, device.BatteryPercent);
        Assert.Equal("z-new", device.BatterySource);
        Assert.Equal(2, device.Readings.Count);
    }

    [Fact]
    public void LaterCalendarTimeCannotMakeAnOlderObservationReplaceOrDisconnect()
    {
        var store = Begin();
        Put(store, Active(61, At(10)), At(10));
        var old = Active(1, At(5, 1)) with { Connected = false };
        Put(store, old, At(11, 1));
        var device = Assert.Single(store.Snapshot(At(11, 1)).Devices);
        Assert.Equal(61, device.BatteryPercent);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(At(10).Utc, Assert.Single(device.Readings).ReceivedAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void RepeatedWindowsPublicationKeepsTheOriginalFieldDeadline(int correctionHours)
    {
        var store = Begin();
        var original = Cache(70, At(0));
        Put(store, original, At(0));
        store.BeginScan(2, false);
        var repeatedAt = At(14 * 60, correctionHours);
        Put(store, original with { ReceivedAt = repeatedAt.Utc, ReceivedElapsed = repeatedAt.Elapsed }, repeatedAt, 2);
        var reading = Assert.Single(Assert.Single(store.Snapshot(repeatedAt).Devices).Readings);
        Assert.Equal(Start, reading.ReceivedAt);
        Assert.Equal(repeatedAt.Utc.AddMinutes(1), reading.ExpiresAt);
        Assert.Equal(70, Assert.Single(store.Snapshot(At(15 * 60 - 1, correctionHours)).Devices).BatteryPercent);
        var expired = Assert.Single(store.Snapshot(At(15 * 60, correctionHours)).Devices);
        Assert.Null(expired.BatteryPercent);
        Assert.Equal(BatteryFreshness.Expired, expired.Freshness);
    }

    [Fact]
    public void WindowsFreshReadAfterRollbackRenewsOnlyItsActualElapsedLifetime()
    {
        var store = Begin();
        Put(store, Cache(70, At(0)), At(0));
        store.BeginScan(2, false);
        var later = At(14 * 60, -1);
        Put(store, Cache(69, later), later, 2);
        var device = Assert.Single(store.Snapshot(At(15 * 60, -1)).Devices);
        Assert.Equal(69, device.BatteryPercent);
        Assert.Equal(later.Utc, Assert.Single(device.Readings).ReceivedAt);
        Assert.Equal(later.Utc.AddMinutes(15), Assert.Single(device.Readings).ExpiresAt);
        Assert.Null(Assert.Single(store.Snapshot(At(29 * 60, -1)).Devices).BatteryPercent);
    }

    [Theory]
    [InlineData(BatteryEvidence.ActiveRead)]
    [InlineData(BatteryEvidence.SystemCache)]
    public void CalendarCorrectionBetweenIoAndPublicationDoesNotInvalidateTheCompletedRead(BatteryEvidence evidence)
    {
        var store = Begin();
        var completedAt = At(1);
        var publishedAt = At(2, -1);
        var observation = evidence == BatteryEvidence.ActiveRead ? Active(73, completedAt) : Cache(73, completedAt);
        Put(store, observation with { ReceivedAt = publishedAt.Utc, ReceivedElapsed = publishedAt.Elapsed }, publishedAt);
        var device = Assert.Single(store.Snapshot(publishedAt).Devices);
        Assert.Equal(73, device.BatteryPercent);
        Assert.Equal(completedAt.Utc, Assert.Single(device.Readings).ReceivedAt);
        Assert.Null(device.LastError);
        Assert.Equal(publishedAt.Utc.AddSeconds(899), Assert.Single(device.Readings).ExpiresAt);
    }

    [Fact]
    public void PublicationUtcAheadOfCurrentUtcIsValidWhenElapsedStampIsNotFuture()
    {
        var store = Begin();
        Put(store, Active(73, At(1)), At(2, -1));
        var device = Assert.Single(store.Snapshot(At(2, -1)).Devices);
        Assert.Equal(73, device.BatteryPercent);
        Assert.Null(device.LastError);
    }

    [Fact]
    public void DisconnectBarrierStillRejectsAnOldWindowsFieldAfterRollback()
    {
        var store = Begin();
        var cached = Cache(70, At(0));
        Put(store, cached, At(0));
        Put(store, Connection(At(5, -1), false), At(5, -1));
        Assert.Equal(ConnectionState.Offline, Assert.Single(store.Snapshot(At(5, -1)).Devices).Connection);
        store.BeginScan(2, false);
        Put(store, cached with { ReceivedAt = At(10, -1).Utc, ReceivedElapsed = At(10, -1).Elapsed }, At(10, -1), 2);
        var afterReconnect = Assert.Single(store.Snapshot(At(10, -1)).Devices);
        Assert.Equal(ConnectionState.Online, afterReconnect.Connection);
        Assert.Equal(BatteryFreshness.Retained, afterReconnect.Freshness);
        Assert.Equal(At(10, -1).Utc.AddSeconds(890), Assert.Single(afterReconnect.Readings).ExpiresAt);
        Put(store, Cache(68, At(11, -1)), At(11, -1), 2);
        Assert.Equal(BatteryFreshness.Recent, Assert.Single(store.Snapshot(At(11, -1)).Devices).Freshness);
        Assert.Equal(68, Assert.Single(store.Snapshot(At(11, -1)).Devices).BatteryPercent);
    }

    [Fact]
    public void NewestErrorUsesElapsedOrderWhileShowingActualAttemptUtc()
    {
        var store = Begin();
        Put(store, Active(70, At(0), "a-old"), At(0));
        Put(store, Active(70, At(0), "z-new"), At(0));
        store.Apply(1, new("a-old", [], Error: "old error"), At(5));
        store.Apply(1, new("z-new", [], Error: "new error"), At(10, -1));
        var device = Assert.Single(store.Snapshot(At(10, -1)).Devices);
        Assert.Equal("new error", device.LastError);
        Assert.Equal(At(10, -1).Utc, Assert.Single(device.Readings, x => x.Source == "z-new").AttemptedAt);
    }

    [Fact]
    public void RepeatedSourceTimestampDoesNotResurrectAfterCalendarForwardCorrection()
    {
        var store = Begin();
        var original = Active(70, At(0)) with
        {
            Battery = new(70, BatteryEvidence.SourceTimestamp, Start.AddMinutes(-1), ReadAt: Start, ReadElapsed: TimeSpan.Zero)
        };
        Put(store, original, At(0));
        Assert.Equal(Start.AddMinutes(14), Assert.Single(Assert.Single(store.Snapshot(At(0)).Devices).Readings).ExpiresAt);
        var later = At(10 * 60, 1);
        var repeated = original with
        {
            ReceivedAt = later.Utc, ReceivedElapsed = later.Elapsed,
            Battery = original.Battery! with { ReadAt = later.Utc, ReadElapsed = later.Elapsed }
        };
        Put(store, repeated, later);
        Assert.Equal(later.Utc.AddMinutes(4), Assert.Single(Assert.Single(store.Snapshot(later).Devices).Readings).ExpiresAt);
        Assert.Null(Assert.Single(store.Snapshot(At(14 * 60, 1)).Devices).BatteryPercent);
        var afterExpiry = At(16 * 60, 1);
        Put(store, repeated with
        {
            ReceivedAt = afterExpiry.Utc, ReceivedElapsed = afterExpiry.Elapsed,
            Battery = repeated.Battery! with { ReadAt = afterExpiry.Utc, ReadElapsed = afterExpiry.Elapsed }
        }, afterExpiry);
        Assert.Null(Assert.Single(store.Snapshot(afterExpiry).Devices).BatteryPercent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ExplicitlyOfflineCleanupWaitsForElapsedCacheExpiry(int correctionHours)
    {
        var store = Begin();
        Put(store, Active(70, At(0)), At(0));
        Put(store, Connection(At(10, correctionHours), false), At(10, correctionHours));
        store.EndScan(1);
        Assert.Single(store.Snapshot(At(899, correctionHours)).Devices);
        Assert.Empty(store.Snapshot(At(900, correctionHours)).Devices);
    }

    [Fact]
    public void ARejectedFutureElapsedObservationDoesNotBlockTheNextValidOne()
    {
        var store = Begin();
        Put(store, Active(1, At(100)), At(0));
        Assert.Null(Assert.Single(store.Snapshot(At(0)).Devices).BatteryPercent);
        Put(store, Active(71, At(1)), At(1));
        var device = Assert.Single(store.Snapshot(At(1)).Devices);
        Assert.Equal(71, device.BatteryPercent);
        Assert.Null(device.LastError);
    }

    [Fact]
    public void LegacyUtcOverloadKeepsAnEntireUtcTimelineForStampedReaders()
    {
        var store = Begin();
        var observation = Active(71, At(0));
        store.Apply(1, new(observation.Source, [observation]), Start);
        var reading = Assert.Single(Assert.Single(store.Snapshot(Start).Devices).Readings);
        Assert.Equal(71, reading.Percent);
        Assert.Equal(BatteryFreshness.Current, reading.Freshness);
        Assert.Equal(Start.AddMinutes(15), reading.ExpiresAt);
        Assert.Null(Assert.Single(store.Snapshot(Start.AddMinutes(15)).Devices).BatteryPercent);
    }

    private static StateStore Begin()
    {
        var store = new StateStore();
        store.BeginScan(1, false);
        return store;
    }

    private static ClockInstant At(int seconds, int correctionHours = 0) =>
        new(Start.AddSeconds(seconds).AddHours(correctionHours), TimeSpan.FromSeconds(seconds));

    private static DeviceObservation Active(int percent, ClockInstant at, string source = "active") =>
        new("bt:clock", null, "001122334455", "Clock keyboard", DeviceKind.Keyboard, source, at.Utc,
            Connected: true, PathPresent: true,
            Battery: new(percent, BatteryEvidence.ActiveRead, at.Utc, ReadAt: at.Utc, ReadElapsed: at.Elapsed),
            ReceivedElapsed: at.Elapsed);

    private static DeviceObservation Cache(int percent, ClockInstant at) => Active(percent, at, "windows") with
    { Battery = new(percent, BatteryEvidence.SystemCache, ReadAt: at.Utc, ReadElapsed: at.Elapsed) };

    private static DeviceObservation Connection(ClockInstant at, bool connected) =>
        Active(0, at) with { Battery = null, Connected = connected };

    private static void Put(StateStore store, DeviceObservation observation, ClockInstant now, long generation = 1) =>
        store.Apply(generation, new(observation.Source, [observation]), now);
}
