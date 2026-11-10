using BatteryManager.Core;
using Xunit;

namespace BatteryManager.Core.Tests;

public sealed class DeviceDisplayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
    private const string BluetoothEndpoint = "bt-classic:020000000011";

    [Theory]
    [InlineData(ReadStatus.Error, SourceDisplayResult.Failed, true)]
    [InlineData(ReadStatus.Timeout, SourceDisplayResult.Failed, true)]
    [InlineData(ReadStatus.Missing, SourceDisplayResult.NoData, false)]
    [InlineData(ReadStatus.Canceled, SourceDisplayResult.NoData, false)]
    [InlineData(ReadStatus.Unsupported, SourceDisplayResult.Unsupported, false)]
    public void First_attempt_without_any_battery_is_still_available_to_display(ReadStatus status,
        SourceDisplayResult result, bool failed)
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, status: status, error: failed ? "sample failure" : null));
        var device = Assert.Single(store.Snapshot(At(0)).Devices);
        Assert.Empty(device.Readings);
        var display = Assert.IsType<DeviceDisplay>(DeviceDisplayMapper.Map(device));
        Assert.Null(display.Battery.Value);
        Assert.Null(display.Source);
        Assert.Equal(BatteryTone.None, display.Battery.Tone);
        Assert.Equal(result, Assert.Single(display.Sources).Result);
        Assert.Equal(failed, display.ReadFailed);
        Assert.True(display.Expandable);
        Assert.Equal(DeviceTransport.Bluetooth, display.Transport);
    }

    [Fact]
    public void Last_failure_remains_until_that_source_provides_a_new_result()
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, status: ReadStatus.Error, error: "old failure"));
        var failed = store.Snapshot(At(0));
        Assert.True(DeviceDisplayMapper.Map(Assert.Single(failed.Devices))!.ReadFailed);
        store.BeginScan(2, false);
        var next = store.Snapshot(At(1));
        Assert.True(DeviceDisplayMapper.Map(Assert.Single(next.Devices))!.ReadFailed);
        Assert.Equal(failed.Version, next.Version);
        Put(store, 2, 1, Observation(1, source: "bluetooth.gatt.bas", status: ReadStatus.Missing));
        Assert.True(DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(1)).Devices))!.ReadFailed);
        Scan(store, 3, 2, Observation(2, status: ReadStatus.Missing));
        var recovered = DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(2)).Devices))!;
        Assert.False(recovered.ReadFailed);
        Assert.All(recovered.Sources, source => Assert.Null(source.Error));
    }

    [Fact]
    public void No_response_does_not_show_failure_and_does_not_promote_stale_charging()
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, battery: new(9, BatteryEvidence.ActiveRead, T0, Charging: true)));
        Scan(store, 2, 120, Observation(120, connected: null, status: ReadStatus.Timeout, error: "timeout"));
        var display = DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(120)).Devices))!;
        Assert.Equal(DisplayPresence.NoResponse, display.Presence);
        Assert.Equal(BatteryTone.Stale, display.Battery.Tone);
        Assert.Equal(9, display.Battery.Value);
        Assert.True(display.Battery.Low);
        Assert.False(display.Battery.Charging);
        Assert.False(display.ReadFailed);
        Assert.False(display.TrayEligible);
    }

    [Fact]
    public void Expiration_removes_display_value_but_preserves_source_result_and_coarse_capability()
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, battery: new(90, BatteryEvidence.ActiveRead, T0, Precision: "coarse:4")));
        var fresh = DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(0)).Devices))!;
        Assert.Equal(90, fresh.Battery.Value);
        Assert.True(Assert.Single(fresh.Sources).Coarse);
        Scan(store, 2, 960, Observation(960, status: ReadStatus.Missing));
        var expired = DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(960)).Devices))!;
        Assert.Null(expired.Battery.Value);
        Assert.Equal(BatteryTone.None, expired.Battery.Tone);
        Assert.Null(expired.Source);
        Assert.False(expired.Battery.Charging);
        Assert.True(Assert.Single(expired.Sources).Coarse);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    [InlineData(100, false)]
    public void Valid_zero_and_low_battery_boundary_are_preserved(int percentage, bool low)
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, battery: new(percentage, BatteryEvidence.ActiveRead, T0)));
        var display = DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(0)).Devices))!;
        Assert.Equal(percentage, display.Battery.Value);
        Assert.Equal(low, display.Battery.Low);
        Assert.Equal(BatteryTone.Normal, display.Battery.Tone);
        Assert.False(display.Battery.Charging);
    }

    [Fact]
    public void Successful_alternate_windows_source_keeps_failure_in_details_without_warning()
    {
        var store = new StateStore();
        store.BeginScan(1, false);
        Put(store, 1, 0, Observation(0, source: "windows.aep.classic", status: ReadStatus.Error, error: "AEP failed"));
        Put(store, 1, 0, Observation(0, source: "windows.pnp", battery: new(73, BatteryEvidence.SystemCache)));
        store.EndScan(1);
        var display = DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(0)).Devices))!;
        Assert.Equal("windows", display.Source);
        Assert.Equal(73, display.Battery.Value);
        Assert.Equal(BatteryTone.Normal, display.Battery.Tone);
        Assert.False(display.ReadFailed);
        var source = Assert.Single(display.Sources);
        Assert.Equal(SourceDisplayResult.Success, source.Result);
        Assert.Equal("AEP failed", source.Error);
    }

    [Fact]
    public void Recent_remains_in_the_tray_until_the_connection_grace_ends()
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, battery: new(55, BatteryEvidence.ActiveRead, T0)));
        var snapshot = store.Snapshot(At(50));
        var device = Assert.Single(snapshot.Devices);
        Assert.Equal(ConnectionState.Recent, device.Connection);
        var display = DeviceDisplayMapper.Map(device)!;
        Assert.Equal(DisplayPresence.Online, display.Presence);
        Assert.True(display.TrayEligible);
        Assert.Same(device, TraySelection.Select(snapshot, []));
        var expiredConnection = store.Snapshot(At(106));
        Assert.Null(TraySelection.Select(expiredConnection, []));
    }

    [Fact]
    public void Hidden_or_offline_devices_do_not_have_display_records()
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, battery: new(55, BatteryEvidence.ActiveRead, T0)));
        Scan(store, 2, 1, Observation(1, connected: false));
        Assert.Null(DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(1)).Devices)));
        store.BeginScan(3, false);
        Put(store, 3, 2, Observation(2, connected: null) with { EndpointId = "new-path", BluetoothAddress = null });
        var pending = Assert.Single(store.Snapshot(At(2)).Devices, device => device.Connection == ConnectionState.Pending);
        Assert.Null(DeviceDisplayMapper.Map(pending));
        store.EndScan(3);
        Assert.Null(DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(2)).Devices, device => device.Connection == ConnectionState.Pending)));
        Scan(store, 4, 3, Observation(3, connected: true) with { EndpointId = "new-path", BluetoothAddress = null });
        Assert.NotNull(DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(3)).Devices, device => device.Connection == ConnectionState.Online)));
    }

    [Fact]
    public void User_presentation_overrides_do_not_mutate_detection_or_cross_identity()
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0));
        var device = Assert.Single(store.Snapshot(At(0)).Devices);
        var custom = DeviceDisplayMapper.Map(device, new(device.Id, "Desk earbuds", DisplayDeviceKind.Earbuds))!;
        Assert.Equal("Desk earbuds", custom.Name);
        Assert.Equal(device.Name, custom.OriginalName);
        Assert.True(custom.NameSetByUser);
        Assert.True(custom.KindSetByUser);
        Assert.Equal(DisplayDeviceKind.Earbuds, custom.Kind);
        Assert.Equal(DisplayDeviceKind.Headphones, custom.KindDetected);
        Assert.Equal(DeviceKind.Headphones, device.Kind);
        var mismatch = DeviceDisplayMapper.Map(device, new("different", "Wrong", DisplayDeviceKind.Mouse))!;
        Assert.Equal(device.Name, mismatch.Name);
        Assert.False(mismatch.NameSetByUser);
        Assert.False(mismatch.KindSetByUser);
        Assert.True(DeviceDisplayMapper.Map(device, new(device.Id, device.Name))!.NameSetByUser);
    }

    [Theory]
    [InlineData("hid:atk:373b:11d9:receiver:sample", "hid.atk.compx", DeviceTransport.Receiver)]
    [InlineData("hid:lofree:05ac:024f:receiver:sample", "hid.lofree.compx", DeviceTransport.Receiver)]
    [InlineData("hid:mchose:a8a5:2255:receiver:sample", "hid.mchose.g3", DeviceTransport.Receiver)]
    [InlineData("usb:arbitrary", "hid.unknown", DeviceTransport.Unknown)]
    [InlineData("usb:arbitrary", "hid.atk.compx", DeviceTransport.Unknown)]
    [InlineData("bt-le:020000000011", "bluetooth.gatt.bas", DeviceTransport.Bluetooth)]
    public void Transport_uses_verified_path_and_source_evidence(string endpoint, string source, DeviceTransport expected)
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, source: source) with { EndpointId = endpoint, BluetoothAddress = null });
        Assert.Equal(expected, DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(0)).Devices))!.Transport);
    }

    [Fact]
    public void Multiple_live_transports_are_shown_together_until_one_connection_is_removed()
    {
        const string container = "b4ef6992-d9e3-40f6-927d-dc0179b5b90d";
        var store = new StateStore();
        store.BeginScan(1, false);
        Put(store, 1, 0, Observation(0) with { ContainerId = container });
        var receiver = Observation(0, source: "hid.atk.compx") with
        { EndpointId = "hid:atk:373b:11d9:receiver:sample", BluetoothAddress = null, ContainerId = container };
        Put(store, 1, 0, receiver);
        store.EndScan(1);
        Assert.Equal(DeviceTransport.Mixed, DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(0)).Devices))!.Transport);
        Scan(store, 2, 1, Observation(1, connected: false) with { ContainerId = container });
        Assert.Equal(DeviceTransport.Receiver, DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(1)).Devices))!.Transport);
    }

    [Fact]
    public void Connections_list_each_method_once_in_fixed_order_with_its_own_state()
    {
        const string container = "b4ef6992-d9e3-40f6-927d-dc0179b5b90d";
        var store = new StateStore();
        store.BeginScan(1, false);
        var receiver = Observation(0, source: "hid.atk.compx") with
        { EndpointId = "hid:atk:373b:11d9:receiver:sample", BluetoothAddress = null, ContainerId = container };
        Put(store, 1, 0, receiver);
        Put(store, 1, 0, Observation(0) with { ContainerId = container });
        store.EndScan(1);
        var both = DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(0)).Devices))!;
        Assert.Equal([new ConnectionDisplay(DeviceTransport.Bluetooth, ConnectionDisplayState.Connected),
            new ConnectionDisplay(DeviceTransport.Receiver, ConnectionDisplayState.Connected)], both.Connections);
        Scan(store, 2, 1, Observation(1, connected: false) with { ContainerId = container });
        var switched = DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(1)).Devices))!;
        Assert.Equal([new ConnectionDisplay(DeviceTransport.Bluetooth, ConnectionDisplayState.NotConnected),
            new ConnectionDisplay(DeviceTransport.Receiver, ConnectionDisplayState.Connected)], switched.Connections);
    }

    [Fact]
    public void Unverified_paths_do_not_add_a_connection_method()
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, source: "hid.unknown") with { EndpointId = "usb:arbitrary", BluetoothAddress = null });
        Assert.Empty(DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(0)).Devices))!.Connections);
    }

    [Fact]
    public void Detected_transports_remain_known_after_disconnect_without_showing_the_device()
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, battery: new(55, BatteryEvidence.ActiveRead, T0)));
        Scan(store, 2, 1, Observation(1, connected: false));
        var device = Assert.Single(store.Snapshot(At(1)).DetectedDevices);
        Assert.Equal([DeviceTransport.Bluetooth], DeviceDisplayMapper.DetectedTransports(device));
        Assert.False(device.Visible);
        Assert.Null(DeviceDisplayMapper.Map(device));
    }

    [Fact]
    public void Saved_transport_metadata_does_not_admit_a_path_or_add_connection_evidence()
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, connected: null));
        var raw = Assert.Single(store.Snapshot(At(0)).DetectedDevices);
        store.SetDeviceLinks([new(raw.Id,
        [
            new(raw.Id, raw.Name, raw.Kind) { Transports = [DeviceTransport.Bluetooth] },
            new("saved:receiver", "Saved receiver") { Transports = [DeviceTransport.Receiver] }
        ])], At(0));
        var linked = Assert.Single(store.Snapshot(At(0)).Devices);
        Assert.False(linked.Visible);
        Assert.Equal(ConnectionState.Pending, linked.Connection);
        Assert.Equal([DeviceTransport.Bluetooth], DeviceDisplayMapper.DetectedTransports(linked));
        Assert.Null(DeviceDisplayMapper.Map(linked));
    }

    [Fact]
    public void Saved_transport_metadata_is_deep_copied_and_empty_members_keep_their_default_value()
    {
        var transports = new List<DeviceTransport> { DeviceTransport.Bluetooth };
        DeviceLinkGroup group = new("first", [new("first", "First") { Transports = transports }, new("second", "Second")]);
        var copied = Assert.Single(DeviceLinks.CopyValidated([group]));
        transports.Add(DeviceTransport.Receiver);
        Assert.Equal([DeviceTransport.Bluetooth], copied.Members[0].Transports);
        Assert.Equal(new DeviceLinkMember("second", "Second"), copied.Members[1]);
    }

    [Theory]
    [InlineData(DeviceTransport.Unknown)]
    [InlineData(DeviceTransport.Mixed)]
    [InlineData((DeviceTransport)999)]
    public void Saved_transport_metadata_rejects_non_specific_methods(DeviceTransport transport)
    {
        Assert.NotNull(DeviceLinks.Validate([new("first",
            [new("first", "First") { Transports = [transport] }, new("second", "Second")])]));
    }

    [Fact]
    public void Saved_transport_metadata_rejects_duplicate_methods()
    {
        Assert.NotNull(DeviceLinks.Validate([new("first",
            [new("first", "First") { Transports = [DeviceTransport.Bluetooth, DeviceTransport.Bluetooth] }, new("second", "Second")])]));
    }

    [Fact]
    public void Connection_only_success_is_not_a_successful_battery_read()
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0, battery: new(55, BatteryEvidence.ActiveRead, T0)));
        Scan(store, 2, 1, Observation(1));
        var display = DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(1)).Devices))!;
        Assert.Equal(BatteryTone.Stale, display.Battery.Tone);
        Assert.Equal(55, display.Battery.Value);
        Assert.Equal(SourceDisplayResult.NoData, Assert.Single(display.Sources).Result);
    }

    [Fact]
    public void Source_batch_failure_and_cancellation_reach_first_read_details()
    {
        var store = new StateStore();
        Scan(store, 1, 0, Observation(0));
        store.BeginScan(2, false);
        store.Apply(2, new("windows.pnp", [], FailureStatus: ReadStatus.Timeout, Error: "scan timeout"), At(1));
        store.EndScan(2);
        Assert.True(DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(1)).Devices))!.ReadFailed);
        store.BeginScan(3, false);
        store.Apply(3, new("windows.pnp", [], FailureStatus: ReadStatus.Canceled), At(2));
        store.EndScan(3);
        var canceled = DeviceDisplayMapper.Map(Assert.Single(store.Snapshot(At(2)).Devices))!;
        Assert.False(canceled.ReadFailed);
        Assert.Equal(SourceDisplayResult.NoData, Assert.Single(canceled.Sources).Result);
        Assert.Null(Assert.Single(canceled.Sources).Error);
    }

    private static ClockInstant At(int seconds) => new(T0.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));
    private static DeviceObservation Observation(int seconds, string source = "windows.pnp", bool? connected = true,
        BatterySample? battery = null, ReadStatus status = ReadStatus.Success, string? error = null) =>
        new(BluetoothEndpoint, null, "020000000011", "Sample headphones", DeviceKind.Headphones, source,
            T0.AddSeconds(seconds), connected, true, battery, status, error, TimeSpan.FromSeconds(seconds));

    private static void Put(StateStore store, long generation, int seconds, DeviceObservation observation) =>
        store.Apply(generation, new(observation.Source, [observation]), At(seconds));

    private static void Scan(StateStore store, long generation, int seconds, DeviceObservation observation)
    {
        store.BeginScan(generation, false);
        Put(store, generation, seconds, observation);
        store.EndScan(generation);
    }
}
