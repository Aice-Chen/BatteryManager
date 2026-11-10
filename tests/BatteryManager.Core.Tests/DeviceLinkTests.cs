using BatteryManager.Core;
using Xunit;

namespace BatteryManager.Core.Tests;

public sealed class DeviceLinkTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private const string Receiver = "hid:lofree:05ac:024f:receiver:test";
    private const string Bluetooth = "bt-le:001122334455";

    [Fact]
    public void ExplicitLinkUsesOnePrimaryCardWithoutChangingDetectedIdentitiesAndUnlinkRestoresBoth()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true, 80), Observation(0, Bluetooth, true, 60));
        Assert.Equal(2, raw.Devices.Count);
        var group = Group(raw);
        f.Store.SetDeviceLinks([group], At(0));
        var linked = f.Store.Snapshot(At(0));
        Assert.Equal(group.PrimaryDeviceId, Assert.Single(linked.Devices).Id);
        Assert.Equal("Receiver keyboard", Assert.Single(linked.Devices).Name);
        Assert.Equal(2, linked.DetectedDevices.Count);
        Assert.Equal(raw.Devices.Select(d => d.Id), linked.DetectedDevices.Select(d => d.Id));
        Assert.Equal(2, Assert.Single(linked.Devices).SourceAttempts.Count);
        f.Store.SetDeviceLinks([], At(1));
        var separated = f.Store.Snapshot(At(1));
        Assert.Equal(raw.Devices.Select(d => d.Id), separated.Devices.Select(d => d.Id));
        Assert.Equal(new int?[] { 80, 60 }, separated.Devices.Select(d => d.BatteryPercent));
    }

    [Fact]
    public void TheCurrentlyOnlineTransportWinsOverAnotherTransportWithinItsGracePeriod()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true, 80), Observation(0, Bluetooth, null));
        var group = Group(raw);
        f.Store.SetDeviceLinks([group], At(0));
        var switched = f.Scan(50, Observation(50, Receiver, null), Observation(50, Bluetooth, true, 60, systemCache: true));
        var device = Assert.Single(switched.Devices);
        Assert.Equal(60, device.BatteryPercent);
        Assert.Equal("windows.pnp", device.BatterySource);
        Assert.Equal(BatteryFreshness.Retained, Assert.Single(device.Readings, r => r.EndpointId == Receiver).Freshness);
        Assert.Equal(DeviceTransport.Mixed, DeviceDisplayMapper.Map(device)!.Transport);
        Assert.Equal(group.PrimaryDeviceId, device.Id);
    }

    [Fact]
    public void EquallyConfirmedTransportsUseTheNewerQualifiedReadRatherThanAnEvidenceTypePreference()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true, 80), Observation(0, Bluetooth, null));
        f.Store.SetDeviceLinks([Group(raw)], At(0));
        var switched = f.Scan(20, Observation(20, Bluetooth, true, 60, systemCache: true));
        Assert.Equal(60, Assert.Single(switched.Devices).BatteryPercent);
        Assert.Equal(BatteryFreshness.Recent, Assert.Single(switched.Devices).Freshness);
        Assert.Equal(80, Assert.Single(switched.DetectedDevices, d => d.Name == "Receiver keyboard").BatteryPercent);
    }

    [Fact]
    public void LinkDoesNotQualifyAnUnconfirmedWindowsCacheFromAnotherTransport()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true, 80), Observation(0, Bluetooth, null, 99, systemCache: true));
        f.Store.SetDeviceLinks([Group(raw)], At(0));
        var device = Assert.Single(f.Store.Snapshot(At(0)).Devices);
        Assert.Equal(80, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Unknown, Assert.Single(device.Readings, r => r.EndpointId == Bluetooth).Freshness);
        Assert.Null(Assert.Single(raw.DetectedDevices, d => d.Name == "Bluetooth keyboard").BatteryPercent);
    }

    [Fact]
    public void OnlyOldTransportBatteryIsGrayAndKeepsItsOriginalExpiryWhileAnotherTransportIsOnline()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true, 80), Observation(0, Bluetooth, null));
        f.Store.SetDeviceLinks([Group(raw)], At(0));
        var switched = f.Scan(50, Observation(50, Bluetooth, true));
        var device = Assert.Single(switched.Devices);
        Assert.Equal(80, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, device.Freshness);
        Assert.Equal(Start.AddMinutes(15), Assert.Single(device.Readings).ExpiresAt);
        for (var second = 80; second < 900; second += 30) f.Scan(second, Observation(second, Bluetooth, true));
        var expired = Assert.Single(f.Scan(900, Observation(900, Bluetooth, true)).Devices);
        Assert.True(expired.Visible);
        Assert.Equal(ConnectionState.Online, expired.Connection);
        Assert.Null(expired.BatteryPercent);
        Assert.Equal(BatteryFreshness.Expired, expired.Freshness);
    }

    [Fact]
    public void FreshReadOnEitherTransportSuppressesMainFailureWhileKeepingSourceErrorDetails()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true, 80), Observation(0, Bluetooth, true));
        f.Store.SetDeviceLinks([Group(raw)], At(0));
        var failed = Observation(30, Receiver, true) with { Status = ReadStatus.Timeout, Error = "receiver timeout" };
        var device = Assert.Single(f.Scan(30, failed, Observation(30, Bluetooth, true, 60, systemCache: true)).Devices);
        var display = DeviceDisplayMapper.Map(device)!;
        Assert.False(display.ReadFailed);
        Assert.Equal(60, display.Battery.Value);
        Assert.Contains(display.Sources, s => s.Result == SourceDisplayResult.Failed && s.Error == "receiver timeout");
    }

    [Fact]
    public void GroupHasItsOwnPendingWindowEvenWhenOneMemberWasAlreadyHidden()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, null), Observation(0, Bluetooth, true));
        var group = Group(raw);
        f.Store.SetDeviceLinks([group], At(0));
        for (var second = 30; second <= 900; second += 30)
            f.Scan(second, Observation(second, Receiver, null), Observation(second, Bluetooth, true));
        Assert.False(Assert.Single(f.Store.Snapshot(At(900)).DetectedDevices, d => d.Name == "Receiver keyboard").Visible);
        Assert.True(Assert.Single(f.Store.Snapshot(At(900)).Devices).Visible);
        for (var second = 930; second <= 1890; second += 30)
            f.Scan(second, Observation(second, Receiver, null));
        Assert.Equal(ConnectionState.Offline, Assert.Single(f.Store.Snapshot(At(1890)).DetectedDevices, d => d.Name == "Bluetooth keyboard").Connection);
        Assert.True(Assert.Single(f.Store.Snapshot(At(1904.999)).Devices).Visible);
        Assert.Equal(TimeSpan.FromMilliseconds(1), f.Store.NextExpiryDelay(At(1904.999)));
        var hidden = Assert.Single(f.Store.Snapshot(At(1905)).Devices);
        Assert.Equal(ConnectionState.Pending, hidden.Connection);
        Assert.False(hidden.Visible);
        var restored = Assert.Single(f.Scan(1930, Observation(1930, Receiver, true), Observation(1930, Bluetooth, null)).Devices);
        Assert.Equal(group.PrimaryDeviceId, restored.Id);
        Assert.True(restored.Visible);
        Assert.Null(restored.BatteryPercent);
    }

    [Fact]
    public void ReapplyingSameLinkOrChangingItsLabelDoesNotResetHiddenState()
    {
        var f = new Fixture();
        f.Scan(-120, Observation(-120, Receiver, true), Observation(-120, Bluetooth, null));
        var raw = f.Scan(0, Observation(0, Receiver, null), Observation(0, Bluetooth, null));
        var group = Group(raw);
        f.Store.SetDeviceLinks([group], At(0));
        for (var second = 30; second <= 900; second += 30)
        {
            f.Scan(second, Observation(second, Receiver, null), Observation(second, Bluetooth, null));
            f.Store.SetDeviceLinks([group], At(second));
        }
        var hidden = f.Store.Snapshot(At(900));
        Assert.False(Assert.Single(hidden.Devices).Visible);
        var renamed = group with { Members = group.Members.Select(m => m.DeviceId == group.PrimaryDeviceId ? m with { Name = "Saved keyboard" } : m).ToArray() };
        f.Store.SetDeviceLinks([renamed], At(930));
        var changed = f.Store.Snapshot(At(930));
        Assert.False(Assert.Single(changed.Devices).Visible);
        Assert.Equal("Saved keyboard", Assert.Single(changed.Devices).Name);
        Assert.True(changed.Version > hidden.Version);
        Assert.False(Assert.Single(f.Scan(990, Observation(990, Receiver, null), Observation(990, Bluetooth, null)).Devices).Visible);
    }

    [Fact]
    public void GroupKeepsItsConfirmationAfterTheOnlyConfirmedMemberIsCleaned()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true), Observation(0, Bluetooth, null));
        var group = Group(raw);
        f.Store.SetDeviceLinks([group], At(0));
        var removed = f.Scan(30, Observation(30, Receiver, false), Observation(30, Bluetooth, null));
        Assert.False(Assert.Single(removed.DetectedDevices).Visible);
        Assert.True(Assert.Single(removed.Devices).Visible);
        Assert.Equal(ConnectionState.Pending, Assert.Single(removed.Devices).Connection);
        var rebuilt = f.Scan(60, Observation(60, Receiver, null), Observation(60, Bluetooth, null));
        Assert.All(rebuilt.DetectedDevices, device => Assert.False(device.Visible));
        Assert.True(Assert.Single(rebuilt.Devices).Visible);
        for (var second = 90; second <= 900; second += 30)
            f.Scan(second, Observation(second, Receiver, null), Observation(second, Bluetooth, null));
        Assert.True(Assert.Single(f.Store.Snapshot(At(929.999)).Devices).Visible);
        Assert.False(Assert.Single(f.Store.Snapshot(At(930)).Devices).Visible);
        f.Store.SetDeviceLinks([], At(931));
        f.Store.SetDeviceLinks([group], At(932));
        Assert.False(Assert.Single(f.Store.Snapshot(At(932)).Devices).Visible);
    }

    [Fact]
    public void SavedAssociationDoesNotAdmitNeverConfirmedMembersAfterRestart()
    {
        var original = new Fixture();
        var group = Group(original.Scan(0, Observation(0, Receiver, true), Observation(0, Bluetooth, true)));
        var restarted = new Fixture();
        restarted.Store.SetDeviceLinks([group], At(0));
        var discovered = restarted.Scan(0, Observation(0, Bluetooth, null));
        var device = Assert.Single(discovered.Devices);
        Assert.Equal(group.PrimaryDeviceId, device.Id);
        Assert.Equal(ConnectionState.Pending, device.Connection);
        Assert.False(device.Visible);
        Assert.False(Assert.Single(discovered.DetectedDevices).Visible);
        var awake = restarted.Scan(30, Observation(30, Bluetooth, true) with { Status = ReadStatus.Timeout });
        Assert.True(Assert.Single(awake.Devices).Visible);
        Assert.Null(Assert.Single(awake.Devices).BatteryPercent);
        Assert.Equal(group.PrimaryDeviceId, Assert.Single(awake.Devices).Id);
    }

    [Fact]
    public void LinkingARecentlyRediscoveredUnconfirmedMemberCannotReviveAnExpiredConfirmedMember()
    {
        var f = new Fixture();
        var original = f.Scan(0, Observation(0, Receiver, true), Observation(0, Bluetooth, true));
        Assert.All(original.Devices, device => Assert.True(device.Visible));
        var otherId = original.DetectedDevices.Single(device => device.Name == "Bluetooth keyboard").Id;
        var removed = f.Scan(30, Observation(30, Receiver, null), Observation(30, Bluetooth, false));
        Assert.DoesNotContain(removed.DetectedDevices, device => device.Id == otherId);
        for (var second = 60; second <= 1020; second += 30) f.Scan(second, Observation(second, Receiver, null));
        Assert.False(Assert.Single(f.Store.Snapshot(At(1020)).Devices).Visible);
        var rediscovered = f.Scan(1050, Observation(1050, Receiver, null), Observation(1050, Bluetooth, null));
        Assert.All(rediscovered.Devices, device => Assert.False(device.Visible));
        Assert.Contains(rediscovered.DetectedDevices, device => device.Id == otherId);
        var group = Group(rediscovered);
        f.Store.SetDeviceLinks([group], At(1050));
        var linked = Assert.Single(f.Store.Snapshot(At(1050)).Devices);
        Assert.Equal(ConnectionState.Pending, linked.Connection);
        Assert.False(linked.Visible);
        var awake = Assert.Single(f.Scan(1080, Observation(1080, Bluetooth, true)).Devices);
        Assert.Equal(group.PrimaryDeviceId, awake.Id);
        Assert.True(awake.Visible);
    }

    [Fact]
    public void RemovingAndRestoringAMemberRecomputesTheGroupFromItsConfiguredMembers()
    {
        var f = new Fixture();
        const string thirdEndpoint = "receiver:third";
        DeviceObservation Third(int second) => Observation(second, Receiver, null) with { EndpointId = thirdEndpoint, Name = "Third connection" };
        var raw = f.Scan(0, Observation(0, Receiver, true, 80), Observation(0, Bluetooth, null), Third(0));
        var group = Group(raw);
        var otherId = raw.DetectedDevices.Single(device => device.Name == "Bluetooth keyboard").Id;
        f.Scan(1000, Observation(1000, Receiver, null), Observation(1000, Bluetooth, true, 60), Third(1000));
        for (var second = 1030; second <= 1180; second += 30)
            f.Scan(second, Observation(second, Receiver, null), Observation(second, Bluetooth, null), Third(second));
        f.Scan(1200, Observation(1200, Receiver, null), Observation(1200, Bluetooth, null), Third(1200));
        f.Store.SetDeviceLinks([group], At(1200));
        Assert.True(Assert.Single(f.Store.Snapshot(At(1200)).Devices).Visible);
        var smaller = group with { Members = group.Members.Where(member => member.DeviceId != otherId).ToArray() };
        f.Store.SetDeviceLinks([smaller], At(1230));
        var removed = f.Store.Snapshot(At(1230));
        Assert.False(Assert.Single(removed.Devices, device => device.Id == group.PrimaryDeviceId).Visible);
        Assert.True(Assert.Single(removed.Devices, device => device.Id == otherId).Visible);
        f.Store.SetDeviceLinks([group], At(1240));
        Assert.True(Assert.Single(f.Store.Snapshot(At(1240)).Devices).Visible);
        f.Store.SetDeviceLinks([], At(1241));
        var unlinked = f.Store.Snapshot(At(1241));
        Assert.False(Assert.Single(unlinked.Devices, device => device.Id == group.PrimaryDeviceId).Visible);
        Assert.True(Assert.Single(unlinked.Devices, device => device.Id == otherId).Visible);
        f.Store.SetDeviceLinks([group], At(1242));
        for (var second = 1260; second <= 1890; second += 30) f.Scan(second, Observation(second, Bluetooth, null));
        var expired = Assert.Single(f.Store.Snapshot(At(1900)).Devices);
        Assert.True(expired.Visible);
        Assert.Null(expired.BatteryPercent);
        Assert.Equal(Start.AddSeconds(1900), Assert.Single(expired.Readings, reading => reading.EndpointId == Bluetooth).ExpiresAt);
        for (var second = 1920; second <= 1980; second += 30) f.Scan(second, Observation(second, Bluetooth, null));
        Assert.True(Assert.Single(f.Store.Snapshot(At(2004.999)).Devices).Visible);
        Assert.False(Assert.Single(f.Store.Snapshot(At(2005)).Devices).Visible);
    }

    [Fact]
    public void RepeatedNegativeRepliesFromDifferentSourcesDoNotExtendTheGroupWindow()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true, 80), Observation(0, Bluetooth, null));
        var group = Group(raw);
        f.Store.SetDeviceLinks([group], At(0));
        f.Scan(20, Observation(20, Receiver, false), Observation(20, Bluetooth, null));
        f.Scan(40, Observation(40, Receiver, false), Observation(40, Bluetooth, null));
        f.Scan(60, Observation(60, Receiver, false) with { Source = "another.connection" }, Observation(60, Bluetooth, null));
        for (var second = 90; second <= 900; second += 30) f.Scan(second, Observation(second, Bluetooth, null));
        Assert.True(Assert.Single(f.Store.Snapshot(At(919.999)).Devices).Visible);
        Assert.False(Assert.Single(f.Store.Snapshot(At(920)).Devices).Visible);
        Assert.True(Assert.Single(f.Scan(940, Observation(940, Receiver, true)).Devices).Visible);
    }

    [Fact]
    public void SavedLinkWorksAfterRestartWhenOnlySecondaryMemberIsDetected()
    {
        var original = new Fixture();
        var group = Group(original.Scan(0, Observation(0, Receiver, true), Observation(0, Bluetooth, true) with { Kind = DeviceKind.Unknown }));
        var restarted = new Fixture();
        restarted.Store.SetDeviceLinks([group], At(0));
        Assert.Empty(restarted.Store.Snapshot(At(0)).Devices);
        var snapshot = restarted.Scan(10, Observation(10, Bluetooth, true, 60) with { Kind = DeviceKind.Unknown });
        Assert.Equal(group.PrimaryDeviceId, Assert.Single(snapshot.Devices).Id);
        Assert.Equal("Receiver keyboard", Assert.Single(snapshot.Devices).Name);
        Assert.Equal(DeviceKind.Keyboard, Assert.Single(snapshot.Devices).Kind);
        Assert.Equal(DeviceKind.Unknown, Assert.Single(snapshot.DetectedDevices).Kind);
        Assert.Single(snapshot.DetectedDevices);
        Assert.NotEqual(group.PrimaryDeviceId, Assert.Single(snapshot.DetectedDevices).Id);
        Assert.Equal(60, Assert.Single(snapshot.Devices).BatteryPercent);
        restarted.Store.SetDeviceLinks([], At(11));
        Assert.Equal(Assert.Single(snapshot.DetectedDevices).Id, Assert.Single(restarted.Store.Snapshot(At(11)).Devices).Id);
    }

    [Fact]
    public void RemovingThePrimaryRawRecordPreservesItsSavedTypeInsteadOfUsingTheOtherTransport()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true), Observation(0, Bluetooth, true) with { Kind = DeviceKind.Unknown });
        var group = Group(raw);
        f.Store.SetDeviceLinks([group], At(0));
        var removed = f.Scan(30, Observation(30, Receiver, false), Observation(30, Bluetooth, true, 60) with { Kind = DeviceKind.Unknown });
        Assert.Equal(DeviceKind.Unknown, Assert.Single(removed.DetectedDevices).Kind);
        Assert.DoesNotContain(removed.DetectedDevices, device => device.Id == group.PrimaryDeviceId);
        Assert.Equal(DeviceKind.Keyboard, Assert.Single(removed.Devices).Kind);
        var otherChanged = f.Scan(60, Observation(60, Bluetooth, true, 60) with { Kind = DeviceKind.Other });
        Assert.Equal(DeviceKind.Other, Assert.Single(otherChanged.DetectedDevices).Kind);
        Assert.Equal(DeviceKind.Keyboard, Assert.Single(otherChanged.Devices).Kind);
        var updated = group with { Members = group.Members.Select(member => member.DeviceId == group.PrimaryDeviceId
            ? member with { Kind = DeviceKind.Mouse } : member).ToArray() };
        f.Store.SetDeviceLinks([updated], At(60));
        var changed = f.Store.Snapshot(At(60));
        Assert.Equal(DeviceKind.Mouse, Assert.Single(changed.Devices).Kind);
        Assert.True(changed.Version > otherChanged.Version);
    }

    [Fact]
    public void SameSourceAndPercentageKeepTheExactSelectedEndpointChargingState()
    {
        var f = new Fixture();
        var first = Observation(0, Receiver, true, 80);
        first = first with { Battery = first.Battery! with { Charging = false } };
        var second = first with { EndpointId = Receiver + ":second", Name = "Second transport", Battery = first.Battery! with { Charging = true } };
        var raw = f.Scan(0, first, second);
        var group = new DeviceLinkGroup(raw.DetectedDevices[0].Id,
            raw.DetectedDevices.Select(device => new DeviceLinkMember(device.Id, device.Name, device.Kind)).ToArray());
        f.Store.SetDeviceLinks([group], At(0));
        var newer = Observation(20, Receiver, true, 80);
        newer = newer with { EndpointId = second.EndpointId, Name = second.Name, Battery = newer.Battery! with { Charging = true } };
        var selectedSecond = f.Scan(20, newer);
        var device = Assert.Single(selectedSecond.Devices);
        Assert.Equal(2, device.Readings.Count);
        Assert.All(device.Readings, reading =>
        {
            Assert.Equal(device.BatterySource, reading.Source);
            Assert.Equal(device.BatteryPercent, reading.Percent);
            Assert.Equal(device.Freshness, reading.Freshness);
        });
        Assert.False(device.Readings[0].Charging);
        Assert.Equal(second.EndpointId, device.SelectedReading!.EndpointId);
        Assert.True(DeviceDisplayMapper.Map(device)!.Battery.Charging);
        var newest = Observation(25, Receiver, true, 80);
        newest = newest with { Battery = newest.Battery! with { Charging = false } };
        var selectedFirst = f.Scan(25, newest);
        Assert.Equal(Receiver, Assert.Single(selectedFirst.Devices).SelectedReading!.EndpointId);
        Assert.False(DeviceDisplayMapper.Map(Assert.Single(selectedFirst.Devices))!.Battery.Charging);
        Assert.True(selectedFirst.Version > selectedSecond.Version);
    }

    [Fact]
    public void CleanupDoesNotForgetTheUserLinkWhenTheSameMemberReturns()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true), Observation(0, Bluetooth, true));
        var group = Group(raw);
        f.Store.SetDeviceLinks([group], At(0));
        Assert.Empty(f.Scan(30, Observation(30, Receiver, false), Observation(30, Bluetooth, false)).Devices);
        var returned = f.Scan(60, Observation(60, Bluetooth, true, 50));
        Assert.Equal(group.PrimaryDeviceId, Assert.Single(returned.Devices).Id);
        Assert.Equal(50, Assert.Single(returned.Devices).BatteryPercent);
    }

    [Fact]
    public void DetectedStateChangesAdvanceVersionEvenIfGroupedCardDoesNotChange()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true, 80), Observation(0, Bluetooth, true));
        f.Store.SetDeviceLinks([Group(raw)], At(0));
        var before = f.Store.Snapshot(At(0));
        var renamed = f.Scan(0, Observation(0, Bluetooth, true) with { Name = "Updated Bluetooth label" });
        var beforeCard = Assert.Single(before.Devices);
        var afterCard = Assert.Single(renamed.Devices);
        Assert.Equal((beforeCard.Id, beforeCard.Name, beforeCard.Connection, beforeCard.Visible, beforeCard.BatteryPercent, beforeCard.Freshness),
            (afterCard.Id, afterCard.Name, afterCard.Connection, afterCard.Visible, afterCard.BatteryPercent, afterCard.Freshness));
        Assert.True(renamed.Version > before.Version);
        Assert.Contains(renamed.DetectedDevices, d => d.Name == "Updated Bluetooth label");
    }

    [Fact]
    public void HiddenAndOfflineDetectedDevicesKeepTransportMetadataWithoutPretendingToBeOnline()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true, 80), Observation(0, Bluetooth, true, 60));
        var offline = f.Scan(30, Observation(30, Receiver, false), Observation(30, Bluetooth, false));
        Assert.All(offline.DetectedDevices, d => Assert.False(d.Visible));
        Assert.Equal(DeviceTransport.Receiver, DeviceDisplayMapper.DetectedTransport(offline.DetectedDevices[0]));
        Assert.Equal(DeviceTransport.Bluetooth, DeviceDisplayMapper.DetectedTransport(offline.DetectedDevices[1]));
        Assert.All(offline.DetectedDevices, d => Assert.Null(DeviceDisplayMapper.Map(d)));
    }

    [Fact]
    public void OriginalWindowsNameWhitespaceIsPreservedWhileDeviceIdsRemainStrict()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true), Observation(0, Bluetooth, true) with { Name = "Kzzi-K75 5.0 " });
        var primary = Assert.Single(raw.DetectedDevices, device => device.Name == "Kzzi-K75 5.0 ");
        var group = new DeviceLinkGroup(primary.Id,
            raw.DetectedDevices.Select(device => new DeviceLinkMember(device.Id, device.Name, device.Kind)).ToArray());
        Assert.Null(DeviceLinks.Validate([group]));
        f.Store.SetDeviceLinks([group], At(0));
        Assert.Equal("Kzzi-K75 5.0 ", Assert.Single(f.Store.Snapshot(At(0)).Devices).Name);
        Assert.Null(DeviceLinks.Validate([group with { Members = group.Members.Select(member => member with { Name = "  " + member.Name }).ToArray() }]));
        Assert.NotNull(DeviceLinks.Validate([group with { Members = group.Members.Select(member => member with { Name = "   " }).ToArray() }]));
        Assert.NotNull(DeviceLinks.Validate([group with { Members = group.Members.Select(member => member with { DeviceId = member.DeviceId + " " }).ToArray() }]));
    }

    [Fact]
    public void LinkSettingsAreCopiedAndRejectOverlappingMembersAndInvalidText()
    {
        var f = new Fixture();
        var raw = f.Scan(0, Observation(0, Receiver, true), Observation(0, Bluetooth, true));
        var group = Group(raw);
        var mutable = group.Members.ToArray();
        f.Store.SetDeviceLinks([group with { Members = mutable }], At(0));
        mutable[0] = mutable[0] with { Name = "Mutated outside store" };
        Assert.Equal("Receiver keyboard", Assert.Single(f.Store.Snapshot(At(0)).Devices).Name);
        Assert.NotNull(DeviceLinks.Validate([group, group]));
        Assert.NotNull(DeviceLinks.Validate([group with { PrimaryDeviceId = "unknown" }]));
        Assert.NotNull(DeviceLinks.Validate([group with { Members = [group.Members[0]] }]));
        Assert.NotNull(DeviceLinks.Validate([group with { Members = [group.Members[0], group.Members[1] with { Name = "bad\uD800" }] }]));
        Assert.NotNull(DeviceLinks.Validate([group with { Members = [group.Members[0], group.Members[1] with { Kind = (DeviceKind)99 }] }]));
        Assert.Null(DeviceLinks.Validate([group with { Members = [group.Members[0], group.Members[1] with { Name = "键盘 🖱" }] }]));
        Assert.Throws<ArgumentException>(() => f.Store.SetDeviceLinks([group, group], At(1)));
        Assert.Single(f.Store.Snapshot(At(1)).Devices);
    }

    private static DeviceLinkGroup Group(StateSnapshot snapshot)
    {
        var raw = snapshot.DetectedDevices;
        return new(raw.Single(d => d.Name == "Receiver keyboard").Id,
            raw.Select(d => new DeviceLinkMember(d.Id, d.Name, d.Kind)).ToArray());
    }

    private static ClockInstant At(double seconds) => new(Start.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));

    private static DeviceObservation Observation(int seconds, string endpoint, bool? connected, int? percent = null, bool systemCache = false)
    {
        var receiver = endpoint == Receiver;
        var at = At(seconds);
        return new(endpoint, null, receiver ? null : "001122334455", receiver ? "Receiver keyboard" : "Bluetooth keyboard", DeviceKind.Keyboard,
            receiver ? "hid.lofree.compx" : "windows.pnp", at.Utc, Connected: connected, PathPresent: true,
            Battery: percent is { } value ? new(value, systemCache ? BatteryEvidence.SystemCache : BatteryEvidence.ActiveRead,
                systemCache ? null : at.Utc, ReadAt: at.Utc, ReadElapsed: at.Elapsed) : null,
            Status: percent is not null ? ReadStatus.Success : ReadStatus.Missing, ReceivedElapsed: at.Elapsed,
            Transport: receiver ? DeviceTransport.Receiver : DeviceTransport.Bluetooth);
    }

    private sealed class Fixture
    {
        public StateStore Store { get; } = new();
        private long _generation;
        public StateSnapshot Scan(int seconds, params DeviceObservation[] observations)
        {
            Store.BeginScan(++_generation, false);
            foreach (var group in observations.GroupBy(o => o.Source)) Store.Apply(_generation, new(group.Key, group.ToArray()), At(seconds));
            Store.EndScan(_generation);
            return Store.Snapshot(At(seconds));
        }
    }
}
