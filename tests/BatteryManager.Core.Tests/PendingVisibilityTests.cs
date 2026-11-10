using BatteryManager.Core;
using Xunit;

namespace BatteryManager.Core.Tests;

public sealed class PendingVisibilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private const string Container = "b7915419-c44b-4842-aee6-dac3dd802d30";

    [Fact]
    public void PreviouslyConfirmedPendingHidesAtFifteenMinutesWithoutLosingTheDeviceOrRenewingOnRefresh()
    {
        var store = new StateStore();
        Scan(store, -105, true);
        var first = Scan(store, 0, null);
        Assert.True(first.Visible);
        for (var second = 30; second < 900; second += 30)
            Assert.True(Scan(store, second, null, manual: second % 60 == 0).Visible);
        Assert.True(Assert.Single(store.Snapshot(At(899.999)).Devices).Visible);
        Assert.Equal(TimeSpan.FromMilliseconds(1), store.NextExpiryDelay(At(899.999)));
        var hidden = Scan(store, 900, null, manual: true);
        Assert.Equal(first.Id, hidden.Id);
        Assert.Equal(ConnectionState.Pending, hidden.Connection);
        Assert.False(hidden.Visible);
        Assert.Single(hidden.SourceAttempts);
        Assert.False(Scan(store, 930, null, manual: true).Visible);
        // The path can expire between scans. Rediscovery without activity must
        // not grant another visible window to the same retained record.
        Assert.False(Scan(store, 990, null, manual: true).Visible);
    }

    [Fact]
    public void CountdownStartsAtRecentExpiryEvenWithoutASnapshotAtThatBoundary()
    {
        var store = new StateStore();
        Scan(store, 0, true);
        for (var second = 30; second <= 990; second += 30) Scan(store, second, null);
        Assert.True(Assert.Single(store.Snapshot(At(1004.999)).Devices).Visible);
        Assert.Equal(TimeSpan.FromMilliseconds(1), store.NextExpiryDelay(At(1004.999)));
        var hidden = Assert.Single(store.Snapshot(At(1005)).Devices);
        Assert.Equal(ConnectionState.Pending, hidden.Connection);
        Assert.False(hidden.Visible);
    }

    [Fact]
    public void ConfirmedConnectionRestoresHiddenDeviceWithoutBatteryAndRestartsNextPendingWindow()
    {
        var store = new StateStore();
        for (var second = 0; second <= 900; second += 30)
        {
            var unconfirmed = Scan(store, second, null, manual: second % 60 == 0);
            Assert.False(unconfirmed.Visible);
            Assert.Equal(ConnectionState.Pending, unconfirmed.Connection);
            Assert.Single(unconfirmed.SourceAttempts);
            Assert.Null(unconfirmed.BatteryPercent);
        }
        var hidden = Assert.Single(store.Snapshot(At(900)).Devices);
        Assert.False(hidden.Visible);
        var awake = Scan(store, 930, true, ReadStatus.Error);
        Assert.Equal(hidden.Id, awake.Id);
        Assert.True(awake.Visible);
        Assert.Equal(ConnectionState.Online, awake.Connection);
        Assert.Null(awake.BatteryPercent);
        for (var second = 960; second <= 1920; second += 30) Scan(store, second, null);
        Assert.True(Assert.Single(store.Snapshot(At(1934.999)).Devices).Visible);
        Assert.False(Assert.Single(store.Snapshot(At(1935)).Devices).Visible);
    }

    [Fact]
    public void ContinuedConnectionWithBatteryErrorsNeverStartsPendingHiding()
    {
        var store = new StateStore();
        for (var second = 0; second <= 2400; second += 30)
        {
            var device = Scan(store, second, true, ReadStatus.Error);
            Assert.True(device.Visible);
            Assert.Equal(ConnectionState.Online, device.Connection);
            Assert.Null(device.BatteryPercent);
        }
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void UtcCorrectionDoesNotChangeThePendingDeadline(int offsetSeconds)
    {
        var store = new StateStore();
        Scan(store, -105, true);
        Scan(store, 0, null);
        for (var second = 30; second < 900; second += 30)
        {
            var at = At(second, offsetSeconds);
            Apply(store, second + 1, at, [Path(at)], manual: false);
        }
        Assert.True(Assert.Single(store.Snapshot(At(899.999, offsetSeconds)).Devices).Visible);
        var deadline = At(900, offsetSeconds);
        Apply(store, 901, deadline, [Path(deadline)], manual: false);
        var hidden = Assert.Single(store.Snapshot(At(900, offsetSeconds)).Devices);
        Assert.False(hidden.Visible);
        Assert.Equal(ConnectionState.Pending, hidden.Connection);
    }

    [Fact]
    public void NewEndpointAndLaterIdentityMergeDoNotRenewAnAlreadyPendingDevice()
    {
        var store = new StateStore();
        Scan(store, -105, true);
        for (var second = 0; second <= 870; second += 30)
        {
            var at = At(second);
            var observations = second >= 600
                ? new[] { Path(at), Path(at, "receiver:two", Container) }
                : new[] { Path(at) };
            Apply(store, second + 1, at, observations, manual: false);
        }
        Assert.Equal(2, store.Snapshot(At(870)).Devices.Count);
        var bridgeAt = At(890);
        Apply(store, 891, bridgeAt, [Path(bridgeAt, container: Container)], manual: false);
        Assert.True(Assert.Single(store.Snapshot(bridgeAt).Devices).Visible);
        var hidden = Assert.Single(store.Snapshot(At(900)).Devices);
        Assert.Equal(2, hidden.SourceAttempts.Count);
        Assert.False(hidden.Visible);
    }

    [Fact]
    public void AnotherConnectedEndpointKeepsTheDeviceVisibleAndResetsPendingTime()
    {
        var store = new StateStore();
        for (var second = 0; second <= 900; second += 30)
        {
            var at = At(second);
            Apply(store, second + 1, at,
                [Path(at, container: Container), Path(at, "receiver:two", Container) with { Connected = true }], false);
        }
        var device = Assert.Single(store.Snapshot(At(900)).Devices);
        Assert.True(device.Visible);
        Assert.Equal(ConnectionState.Online, device.Connection);
    }

    [Fact]
    public void RestartRequiresFreshConfirmationEvenWhenTheSameDeviceWasPreviouslyOnline()
    {
        var previous = new StateStore();
        Scan(previous, -120, true);
        for (var second = 0; second <= 900; second += 30) Scan(previous, second, null);
        var hidden = Assert.Single(previous.Snapshot(At(900)).Devices);
        Assert.False(hidden.Visible);
        var restarted = new StateStore();
        var discovered = Scan(restarted, 930, null);
        Assert.Equal(hidden.Id, discovered.Id);
        Assert.False(discovered.Visible);
        Assert.Equal(ConnectionState.Pending, discovered.Connection);
        Assert.True(Scan(restarted, 960, true, ReadStatus.Timeout).Visible);
    }

    [Fact]
    public void PendingDeadlineIsScheduledIndependentlyOfBatteryAndPathExpiry()
    {
        var store = new StateStore(StateOptions.Default with { ConnectionLifetime = TimeSpan.FromHours(1) });
        Scan(store, -3660, true);
        Scan(store, 0, null);
        Assert.Equal(TimeSpan.FromMinutes(15), store.NextExpiryDelay(At(0)));
        Assert.True(Assert.Single(store.Snapshot(At(899.999)).Devices).Visible);
        Assert.False(Assert.Single(store.Snapshot(At(900)).Devices).Visible);
    }

    [Fact]
    public void PathReturningAfterAnOfflineGapKeepsTheOriginalConfirmationDeadline()
    {
        var store = new StateStore();
        Scan(store, 0, true);
        var returned = Scan(store, 960, null);
        Assert.Equal(ConnectionState.Pending, returned.Connection);
        Assert.True(returned.Visible);
        Scan(store, 990, null);
        Assert.True(Assert.Single(store.Snapshot(At(1004.999)).Devices).Visible);
        Assert.False(Scan(store, 1005, null).Visible);
        Assert.False(Scan(store, 1860, null).Visible);
    }

    [Fact]
    public void ConfirmedRemovalClearsAdmissionAndRediscoveryRequiresFreshConfirmation()
    {
        var store = new StateStore();
        Scan(store, -120, true);
        for (var second = 0; second <= 900; second += 30) Scan(store, second, null);
        Assert.False(Assert.Single(store.Snapshot(At(900)).Devices).Visible);
        var removedAt = At(930);
        Apply(store, 931, removedAt, [Path(removedAt) with { Connected = false }], false);
        Assert.Empty(store.Snapshot(removedAt).Devices);
        var rediscovered = Scan(store, 960, null);
        Assert.False(rediscovered.Visible);
        Assert.Equal(ConnectionState.Pending, rediscovered.Connection);
        Assert.True(Scan(store, 990, true).Visible);
    }

    [Fact]
    public void IdentityMergeInheritsConfirmationFromTheNewerRecordWithoutQualifyingBattery()
    {
        var store = new StateStore();
        var at = At(0);
        Apply(store, 1, at, [Path(at), Path(at, "receiver:two", Container) with { Connected = true }], false);
        var initial = store.Snapshot(at);
        var unconfirmed = Assert.Single(initial.Devices, device => !device.Visible);
        Assert.True(Assert.Single(initial.Devices, device => device.Id != unconfirmed.Id).Visible);
        for (var second = 30; second <= 120; second += 30)
        {
            at = At(second);
            Apply(store, second + 1, at, [Path(at), Path(at, "receiver:two", Container)], false);
        }
        at = At(150);
        Apply(store, 151, at, [Path(at, container: Container)], false);
        var merged = Assert.Single(store.Snapshot(at).Devices);
        Assert.Equal(unconfirmed.Id, merged.Id);
        Assert.Equal(ConnectionState.Pending, merged.Connection);
        Assert.True(merged.Visible);
        Assert.Null(merged.BatteryPercent);
        Assert.Equal(2, merged.SourceAttempts.Count);
    }

    [Fact]
    public void InvalidConnectionObservationCannotAdmitAPurePath()
    {
        var store = new StateStore();
        Apply(store, 1, At(0), [Path(At(30)) with { Connected = true }], false);
        var pending = Scan(store, 1, null);
        Assert.False(pending.Visible);
        Assert.Equal(ConnectionState.Pending, pending.Connection);
        Assert.True(Scan(store, 2, true).Visible);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void IdentityMergeUsesTheLatestConfirmedWindowRegardlessOfRecordOrder(bool confirmedRecordCreatedFirst, bool otherRecordWasConfirmed)
    {
        var store = new StateStore();
        var at = At(0);
        var firstPath = Path(at) with { Connected = otherRecordWasConfirmed ? true : null };
        var laterConfirmed = Path(at, "receiver:two", Container);
        Apply(store, 1, at, confirmedRecordCreatedFirst ? [laterConfirmed, firstPath] : [firstPath, laterConfirmed], false);
        var original = store.Snapshot(at);
        Assert.Equal(otherRecordWasConfirmed, original.Devices.Any(device => device.Visible));
        at = At(1000);
        Apply(store, 1001, at, [Path(at), Path(at, "receiver:two", Container) with { Connected = true }], false);
        for (var second = 1030; second <= 1180; second += 30)
        {
            at = At(second);
            Apply(store, second + 1, at, [Path(at), Path(at, "receiver:two", Container)], false);
        }
        // The newer confirmation ends at 1105. The other path either never
        // confirmed, or last confirmed through 105; neither can force hiding now.
        at = At(1200);
        Apply(store, 1201, at, [Path(at, container: Container)], false);
        var merged = Assert.Single(store.Snapshot(at).Devices);
        Assert.Equal(original.Devices[0].Id, merged.Id);
        Assert.Equal(ConnectionState.Pending, merged.Connection);
        Assert.True(merged.Visible);
        for (var second = 1230; second <= 1980; second += 30)
        {
            at = At(second);
            Apply(store, second + 1, at, [Path(at, container: Container)], false);
        }
        Assert.True(Assert.Single(store.Snapshot(At(2004.999)).Devices).Visible);
        Assert.Equal(TimeSpan.FromMilliseconds(1), store.NextExpiryDelay(At(2004.999)));
        Assert.False(Assert.Single(store.Snapshot(At(2005)).Devices).Visible);
    }

    [Fact]
    public void InvalidPendingLifetimeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StateStore(StateOptions.Default with { PendingVisibilityLifetime = TimeSpan.Zero }));
    }

    private static ClockInstant At(double seconds, int utcOffset = 0) =>
        new(Start.AddSeconds(seconds + utcOffset), TimeSpan.FromSeconds(seconds));

    private static DeviceObservation Path(ClockInstant at, string endpoint = "receiver:one", string? container = null) =>
        new(endpoint, container, null, "Receiver mouse", DeviceKind.Mouse, "reader", at.Utc,
            PathPresent: true, Status: ReadStatus.Missing, ReceivedElapsed: at.Elapsed);

    private static DeviceState Scan(StateStore store, int seconds, bool? connected,
        ReadStatus status = ReadStatus.Missing, bool manual = false)
    {
        var at = At(seconds);
        Apply(store, seconds + 1, at, [Path(at) with { Connected = connected, Status = status }], manual);
        return Assert.Single(store.Snapshot(at).Devices);
    }

    private static void Apply(StateStore store, long generation, ClockInstant at, DeviceObservation[] observations, bool manual)
    {
        store.BeginScan(generation, manual);
        store.Apply(generation, new("reader", observations), at);
        store.EndScan(generation);
    }
}
