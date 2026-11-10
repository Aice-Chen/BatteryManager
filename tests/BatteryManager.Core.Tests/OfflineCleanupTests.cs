using BatteryManager.Core;
using Xunit;

namespace BatteryManager.Core.Tests;

public sealed class OfflineCleanupTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
    private const string Address = "001122334455";
    private const string Container = "a71e0da2-9b41-482a-933b-328f661c1801";

    [Fact]
    public void ConfirmedDisconnectKeepsQualifiedBatteryUntilItsOriginalExpiryThenRemovesRecord()
    {
        var store = new StateStore();
        Scan(store, 1, Start, new SourceBatch("windows.aep", [Connection(Start, true)]),
            new SourceBatch("windows.pnp", [Cache(Start, 72)]));
        var first = Assert.Single(store.Snapshot(Start).Devices);
        var originalExpiry = Assert.Single(first.Readings).ExpiresAt;
        Scan(store, 2, Start.AddSeconds(5), new SourceBatch("windows.aep", [Connection(Start.AddSeconds(5), false)]));
        var offline = Assert.Single(store.Snapshot(Start.AddSeconds(5)).Devices);
        Assert.False(offline.Visible);
        Assert.Equal(72, offline.BatteryPercent);

        var repeatedAt = Start.AddMinutes(14);
        Scan(store, 3, repeatedAt, new SourceBatch("windows.aep", [Connection(repeatedAt, false)]),
            new SourceBatch("windows.pnp", [Cache(repeatedAt, 10)]));
        var retained = Assert.Single(store.Snapshot(repeatedAt).Devices);
        Assert.Equal(72, retained.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, retained.Freshness);
        Assert.Equal(originalExpiry, Assert.Single(retained.Readings, reading => reading.Freshness == BatteryFreshness.Retained).ExpiresAt);
        Assert.Single(store.Snapshot(Start.AddMinutes(15).AddTicks(-1)).Devices);
        var beforeRemoval = store.Snapshot(Start.AddMinutes(15).AddTicks(-1)).Version;
        var removed = store.Snapshot(Start.AddMinutes(15));
        Assert.Empty(removed.Devices);
        Assert.True(removed.Version > beforeRemoval);
    }

    [Fact]
    public void RepeatedOfflineHistoryWithOnlyUnqualifiedCacheDoesNotSurviveClosedScans()
    {
        var store = new StateStore();
        foreach (var generation in new long[] { 1, 2, 3 })
        {
            var now = Start.AddSeconds(generation * 30);
            store.BeginScan(generation, false);
            store.Apply(generation, new SourceBatch("windows.pnp", [Cache(now, 80)]), now);
            Assert.False(Assert.Single(store.Snapshot(now).Devices).Visible);
            store.Apply(generation, new SourceBatch("windows.aep", [Connection(now, false)]), now);
            // In-flight evidence must stay available for another source in the same scan.
            Assert.Single(store.Snapshot(now).Devices);
            store.EndScan(generation);
            Assert.Empty(store.Snapshot(now).Devices);
        }
    }

    [Fact]
    public void SameScanOfflineThenPropertyThenOnlineStillQualifiesOnlyFreshCache()
    {
        var store = new StateStore();
        store.BeginScan(1, false);
        store.Apply(1, new SourceBatch("windows.aep", [Connection(Start, false)]), Start);
        store.Apply(1, new SourceBatch("windows.pnp", [Cache(Start.AddSeconds(1), 64)]), Start.AddSeconds(1));
        Assert.Single(store.Snapshot(Start.AddSeconds(1)).Devices);
        store.Apply(1, new SourceBatch("windows.aep", [Connection(Start.AddSeconds(2), true)]), Start.AddSeconds(2));
        Assert.True(Assert.Single(store.Snapshot(Start.AddSeconds(2)).Devices).Visible);
        store.EndScan(1);
        var device = Assert.Single(store.Snapshot(Start.AddSeconds(2)).Devices);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(64, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Recent, device.Freshness);
    }

    [Fact]
    public void NewPendingReceiverWaitsForConnectionConfirmationAndEstablishedPendingStaysVisible()
    {
        var store = new StateStore();
        var path = Active(Start, 65) with { Connected = null, Battery = null, Status = ReadStatus.Missing };
        store.BeginScan(1, false);
        store.Apply(1, new SourceBatch("reader", [path]), Start);
        var collecting = Assert.Single(store.Snapshot(Start).Devices);
        Assert.Equal(ConnectionState.Pending, collecting.Connection);
        Assert.False(collecting.Visible);
        store.EndScan(1);
        Assert.False(Assert.Single(store.Snapshot(Start).Devices).Visible);

        var later = Start.AddSeconds(30);
        store.BeginScan(2, false);
        store.Apply(2, new SourceBatch("reader", [path with { ReceivedAt = later }]), later);
        Assert.False(Assert.Single(store.Snapshot(later).Devices).Visible);
        store.Apply(2, new SourceBatch("reader", [path with { ReceivedAt = later, Connected = true }]), later);
        Assert.True(Assert.Single(store.Snapshot(later).Devices).Visible);
        store.EndScan(2);
        Assert.True(Assert.Single(store.Snapshot(later).Devices).Visible);
        later = Start.AddSeconds(140);
        store.BeginScan(3, false);
        store.Apply(3, new SourceBatch("reader", [path with { ReceivedAt = later }]), later);
        Assert.True(Assert.Single(store.Snapshot(later).Devices).Visible);
        Assert.Equal(ConnectionState.Pending, Assert.Single(store.Snapshot(later).Devices).Connection);
        store.EndScan(3);
    }

    [Fact]
    public void SameScanDisconnectBarrierSurvivesAPropertySnapshotUntilReconnect()
    {
        var store = new StateStore();
        store.BeginScan(1, false);
        store.Apply(1, new SourceBatch("windows.aep", [Connection(Start, false)]), Start);
        Assert.Single(store.Snapshot(Start).Devices);
        store.Apply(1, new SourceBatch("windows.pnp", [Cache(Start, 81)]), Start);
        store.Apply(1, new SourceBatch("windows.aep", [Connection(Start.AddSeconds(1), true)]), Start.AddSeconds(1));
        store.EndScan(1);
        var reconnected = Assert.Single(store.Snapshot(Start.AddSeconds(1)).Devices);
        Assert.Equal(ConnectionState.Online, reconnected.Connection);
        Assert.Null(reconnected.BatteryPercent);
        Assert.Equal(BatteryFreshness.Unknown, Assert.Single(reconnected.Readings).Freshness);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("error")]
    [InlineData("non-authoritative")]
    [InlineData("failed-authoritative")]
    public void FailureOrUnconfirmedAbsenceNeverAuthorizesCleanup(string failure)
    {
        var store = new StateStore();
        Scan(store, 1, Start, new SourceBatch("reader", [Active(Start, 65)]));
        var later = Start.AddMinutes(16);
        SourceBatch batch = failure switch
        {
            "timeout" => new SourceBatch("reader", [Active(later, 65) with { Connected = null, PathPresent = false, Battery = null, Status = ReadStatus.Timeout, Error = "No reply" }]),
            "error" => new SourceBatch("reader", [], Error: "Enumeration failed", FailureStatus: ReadStatus.Error),
            "failed-authoritative" => new SourceBatch("reader", [], Complete: true, Authoritative: true, Error: "Incomplete inventory", FailureStatus: ReadStatus.Error),
            _ => new SourceBatch("reader", [], Complete: true, Authoritative: false)
        };
        Scan(store, 2, later, batch);
        var device = Assert.Single(store.Snapshot(later).Devices);
        Assert.Equal(ConnectionState.Offline, device.Connection);
        Assert.Equal(BatteryFreshness.Expired, device.Freshness);
    }

    [Fact]
    public void CompleteAuthoritativeAbsenceRetainsRecentGraceThenCleansWithoutAnotherRead()
    {
        var store = new StateStore();
        Scan(store, 1, Start, new SourceBatch("reader", [Active(Start, 65) with { Battery = null }]));
        Scan(store, 2, Start.AddSeconds(10), new SourceBatch("reader", [], Complete: true, Authoritative: true));
        Assert.Equal(ConnectionState.Recent, Assert.Single(store.Snapshot(Start.AddSeconds(69)).Devices).Connection);
        Assert.Equal(Start.AddSeconds(70), store.NextExpiry(Start.AddSeconds(69)));
        Assert.Empty(store.Snapshot(Start.AddSeconds(70)).Devices);
    }

    [Fact]
    public void IdleReceiverPathIsPreservedAfterBatteryExpires()
    {
        var store = new StateStore();
        Scan(store, 1, Start, new SourceBatch("reader", [Active(Start, 95)]));
        var later = Start.AddMinutes(16);
        Scan(store, 2, later, new SourceBatch("reader", [Active(later, 95) with { Connected = null, Battery = null, Status = ReadStatus.Missing }]));
        var idle = Assert.Single(store.Snapshot(later).Devices);
        Assert.Equal(ConnectionState.Pending, idle.Connection);
        Assert.True(idle.Visible);
        Assert.Equal(BatteryFreshness.Expired, idle.Freshness);
    }

    [Fact]
    public void AnOnlineSecondEndpointPreservesTheWholeDevice()
    {
        var store = new StateStore();
        Scan(store, 1, Start, new SourceBatch("reader", [Active(Start, 65)]));
        var later = Start.AddMinutes(16);
        Scan(store, 2, later, new SourceBatch("reader", [Active(later, 65) with { Connected = false, Battery = null },
            Active(later, 44) with { EndpointId = "receiver:second" }]));
        var device = Assert.Single(store.Snapshot(later).Devices);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(44, device.BatteryPercent);
        Assert.Equal(2, device.Readings.Select(reading => reading.EndpointId).Distinct().Count());
    }

    [Fact]
    public void OneConfirmedOfflineEndpointCannotCleanAnUnconfirmedSecondEndpoint()
    {
        var store = new StateStore();
        Scan(store, 1, Start, new SourceBatch("reader", [Active(Start, 65), Active(Start, 66) with { EndpointId = "receiver:second" }]));
        var later = Start.AddMinutes(16);
        Scan(store, 2, later, new SourceBatch("reader", [Active(later, 65) with { Connected = false, Battery = null, PathPresent = false }]));
        var device = Assert.Single(store.Snapshot(later).Devices);
        Assert.Equal(ConnectionState.Offline, device.Connection);
        Assert.Equal(2, device.Readings.Select(reading => reading.EndpointId).Distinct().Count());
    }

    [Fact]
    public void ReconnectAfterCleanupStartsWithoutTheExpiredBatteryAndCanReadAgain()
    {
        var store = new StateStore();
        Scan(store, 1, Start, new SourceBatch("reader", [Active(Start, 65)]));
        var original = Assert.Single(store.Snapshot(Start).Devices);
        Scan(store, 2, Start.AddSeconds(1), new SourceBatch("reader", [Active(Start.AddSeconds(1), 65) with { Connected = false, Battery = null }]));
        Assert.Empty(store.Snapshot(Start.AddMinutes(15)).Devices);

        var returnedAt = Start.AddMinutes(16);
        Scan(store, 3, returnedAt, new SourceBatch("reader", [Active(returnedAt, 65) with { Battery = null, Status = ReadStatus.Missing }]));
        var returned = Assert.Single(store.Snapshot(returnedAt).Devices);
        Assert.Equal(original.Id, returned.Id);
        Assert.Equal(ConnectionState.Online, returned.Connection);
        Assert.Null(returned.BatteryPercent);
        Assert.Empty(returned.Readings);
        Scan(store, 4, returnedAt.AddSeconds(1), new SourceBatch("reader", [Active(returnedAt.AddSeconds(1), 42)]));
        Assert.Equal(42, Assert.Single(store.Snapshot(returnedAt.AddSeconds(1)).Devices).BatteryPercent);
    }

    [Fact]
    public void ReusedEndpointAfterCleanupDoesNotRetainOldAddressAliasesOrRejectNewDevice()
    {
        var store = new StateStore();
        Scan(store, 1, Start, new SourceBatch("reader", [Active(Start, 65)]));
        Scan(store, 2, Start.AddSeconds(1), new SourceBatch("reader", [Active(Start.AddSeconds(1), 65) with { Connected = false, Battery = null }]));
        Assert.Empty(store.Snapshot(Start.AddMinutes(15)).Devices);
        var later = Start.AddMinutes(16);
        Scan(store, 3, later, new SourceBatch("reader", [Active(later, 24) with { ContainerId = "e97da6dc-c4bc-43a0-a0aa-19dbd6ea1802", BluetoothAddress = "AABBCCDDEEFF", Name = "Replacement" }]));
        var replacement = Assert.Single(store.Snapshot(later).Devices);
        Assert.Equal("Replacement", replacement.Name);
        Assert.Equal(24, replacement.BatteryPercent);
        Assert.Null(replacement.LastError);
    }

    [Fact]
    public void CleaningOneConflictingDeviceDoesNotMakeItsSharedContainerTrustedAgain()
    {
        var store = new StateStore();
        var first = Active(Start, 65) with { Battery = null };
        var second = Active(Start, 75) with { EndpointId = "bt:second", BluetoothAddress = "AABBCCDDEEFF", Name = "Second", Battery = null };
        Scan(store, 1, Start, new SourceBatch("reader", [first, second]));
        Assert.Equal(2, store.Snapshot(Start).Devices.Count);
        Scan(store, 2, Start.AddSeconds(1), new SourceBatch("reader", [first with { ReceivedAt = Start.AddSeconds(1), Connected = false }]));
        Assert.Equal("Second", Assert.Single(store.Snapshot(Start.AddSeconds(1)).Devices).Name);

        var later = Start.AddSeconds(2);
        Scan(store, 3, later, new SourceBatch("reader", [second with { ReceivedAt = later },
            new("unattributed", Container, null, "Unattributed", DeviceKind.Unknown, "reader", later, PathPresent: true)]));
        var devices = store.Snapshot(later).Devices;
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, device => device.Name == "Second" && device.Connection == ConnectionState.Online);
        Assert.Contains(devices, device => device.Name == "Unattributed" && device.Connection == ConnectionState.Pending);
    }

    private static DeviceObservation Connection(DateTimeOffset at, bool connected) =>
        new("bt:one", Container, Address, "Keyboard", DeviceKind.Keyboard, "windows.aep", at, connected, true);

    private static DeviceObservation Cache(DateTimeOffset at, int percent) =>
        new("bt:one", Container, Address, "Keyboard", DeviceKind.Keyboard, "windows.pnp", at,
            PathPresent: true, Battery: new(percent, BatteryEvidence.SystemCache, ReadAt: at));

    private static DeviceObservation Active(DateTimeOffset at, int percent) =>
        new("bt:one", Container, Address, "Keyboard", DeviceKind.Keyboard, "reader", at, true, true,
            new(percent, BatteryEvidence.ActiveRead, at));

    private static void Scan(StateStore store, long generation, DateTimeOffset at, params SourceBatch[] batches)
    {
        store.BeginScan(generation, false);
        foreach (var batch in batches) store.Apply(generation, batch, at);
        store.EndScan(generation);
    }
}
