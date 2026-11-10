using BatteryManager.Core;
using Xunit;

namespace BatteryManager.Core.Tests;

public sealed class LinkedBatterySelectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
    private const string Address = "001122334455";
    private const string LeEndpoint = "bt-le:001122334455";
    private const string ClassicEndpoint = "bt-classic:001122334455";
    private const string ReceiverEndpoint = "hid:lofree:receiver:selection-test";
    private const string BasSource = "bluetooth.gatt.bas";
    private const string WindowsSource = "windows.pnp";
    private const string ReceiverSource = "hid.lofree.compx";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkingAndRelinkingKeepTheBluetoothMembersPreferredSource(bool separateBluetoothEndpoints)
    {
        var f = new Fixture(separateBluetoothEndpoints);
        f.Scan(0, Receiver(0, 80));
        f.Scan(10, f.Bas(10, 60));
        var raw = f.Scan(11, f.Windows(11, 65));
        var bluetooth = BluetoothMember(raw);
        AssertSelected(bluetooth, BasSource, 60);
        Assert.Equal(separateBluetoothEndpoints ? 2 : 1, bluetooth.SourceAttempts.Select(a => a.EndpointId).Distinct().Count());

        var group = Group(raw);
        f.Store.SetDeviceLinks([group], At(11));
        AssertSelected(Assert.Single(f.Store.Snapshot(At(11)).Devices), BasSource, 60);
        AssertSelected(Assert.Single(f.Scan(12, f.Windows(12, 64)).Devices), BasSource, 60);

        f.Store.SetDeviceLinks([], At(13));
        var separated = f.Store.Snapshot(At(13));
        Assert.Equal(2, separated.Devices.Count);
        AssertSelected(BluetoothMember(separated), BasSource, 60);
        f.Store.SetDeviceLinks([group], At(14));
        AssertSelected(Assert.Single(f.Store.Snapshot(At(14)).Devices), BasSource, 60);

        // Both battery readings are now Recent; connection-only evidence keeps
        // the LE endpoint online without pretending to refresh its BAS value.
        var recent = Assert.Single(f.Scan(56, f.Windows(56, 63), f.BluetoothConnection(56)).Devices);
        AssertSelected(recent, BasSource, 60);
        Assert.Equal(BatteryFreshness.Recent, recent.Freshness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BluetoothFailureFallsBackToWindowsAndRecoveryRestoresBas(bool separateBluetoothEndpoints)
    {
        var f = new Fixture(separateBluetoothEndpoints);
        f.Scan(0, Receiver(0, 80), f.Windows(0, 65));
        var initial = f.Scan(1, f.Bas(1, 60));
        f.Store.SetDeviceLinks([Group(initial)], At(1));
        AssertSelected(Assert.Single(f.Store.Snapshot(At(1)).Devices), BasSource, 60);

        var failed = f.Bas(2, null) with { Status = ReadStatus.Timeout, Error = "BAS read timed out" };
        var fallback = Assert.Single(f.Scan(2, failed, f.Windows(2, 64)).Devices);
        AssertSelected(fallback, WindowsSource, 64);
        var retained = Assert.Single(fallback.Readings, r => r.Source == BasSource);
        Assert.Equal(BatteryFreshness.Retained, retained.Freshness);
        Assert.Equal(Start.AddSeconds(901), retained.ExpiresAt);
        Assert.Equal(ReadStatus.Timeout, retained.Attempt);

        AssertSelected(Assert.Single(f.Scan(3, f.Bas(3, 59)).Devices), BasSource, 59);
        AssertSelected(Assert.Single(f.Scan(4, f.Windows(4, 63)).Devices), BasSource, 59);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherMembersNewReadingWinsWithoutAnUnselectedWindowsRefreshStealingItBack(bool separateBluetoothEndpoints)
    {
        var f = new Fixture(separateBluetoothEndpoints);
        f.Scan(0, Receiver(0, 80));
        f.Scan(10, f.Bas(10, 60));
        var initial = f.Scan(11, f.Windows(11, 65));
        f.Store.SetDeviceLinks([Group(initial)], At(11));

        AssertSelected(Assert.Single(f.Scan(20, Receiver(20, 75)).Devices), ReceiverSource, 75);
        var refreshed = f.Scan(21, f.Windows(21, 64));
        AssertSelected(BluetoothMember(refreshed), BasSource, 60);
        AssertSelected(Assert.Single(refreshed.Devices), ReceiverSource, 75);

        AssertSelected(Assert.Single(f.Scan(22, f.Bas(22, 59)).Devices), BasSource, 59);
        AssertSelected(Assert.Single(f.Scan(23, f.Windows(23, 63)).Devices), BasSource, 59);

        AssertSelected(Assert.Single(f.Scan(24, Receiver(24, 74)).Devices), ReceiverSource, 74);
        var basFailure = f.Bas(25, null) with { Status = ReadStatus.Timeout };
        AssertSelected(Assert.Single(f.Scan(25, basFailure, f.Windows(25, 62)).Devices), WindowsSource, 62);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisconnectAndExpiryStillInvalidatePreviouslyPreferredBluetoothEvidence(bool separateBluetoothEndpoints)
    {
        var f = new Fixture(separateBluetoothEndpoints);
        f.Scan(0, Receiver(0, 80), f.Windows(0, 65));
        var initial = f.Scan(10, f.Bas(10, 60));
        f.Store.SetDeviceLinks([Group(initial)], At(10));
        AssertSelected(Assert.Single(f.Store.Snapshot(At(10)).Devices), BasSource, 60);

        var disconnected = Assert.Single(f.Scan(20, f.Bas(20, null, false), f.Windows(20, null, false), Receiver(20, 75)).Devices);
        AssertSelected(disconnected, ReceiverSource, 75);
        Assert.All(disconnected.Readings.Where(r => r.Source is BasSource or WindowsSource), r => Assert.Equal(BatteryFreshness.Retained, r.Freshness));

        var staleCache = Assert.Single(f.Scan(21, f.Windows(21, 99, null)).Devices);
        AssertSelected(staleCache, ReceiverSource, 75);
        Assert.Contains(staleCache.Readings, r => r.Source == WindowsSource && r.Percent == 99 && r.Freshness == BatteryFreshness.Unknown);

        var expired = Assert.Single(f.Scan(920, Receiver(920, null)).Devices);
        Assert.Equal(ConnectionState.Online, expired.Connection);
        Assert.Null(expired.BatteryPercent);
        Assert.Null(expired.SelectedReading);
        Assert.Equal(BatteryFreshness.Expired, expired.Freshness);
        Assert.Null(DeviceDisplayMapper.Map(expired)!.Source);
    }

    private static void AssertSelected(DeviceState device, string source, int percent)
    {
        Assert.Equal(source, device.BatterySource);
        Assert.Equal(percent, device.BatteryPercent);
        var selected = Assert.IsType<BatteryReading>(device.SelectedReading);
        Assert.Equal(source, selected.Source);
        Assert.Equal(percent, selected.Percent);
        var display = Assert.IsType<DeviceDisplay>(DeviceDisplayMapper.Map(device));
        Assert.Equal(source == WindowsSource ? "windows" : source, display.Source);
        Assert.Equal(percent, display.Battery.Value);
    }

    private static DeviceState BluetoothMember(StateSnapshot snapshot) =>
        Assert.Single(snapshot.DetectedDevices, d => d.SourceAttempts.Any(a => a.Source == BasSource));

    private static DeviceLinkGroup Group(StateSnapshot snapshot)
    {
        Assert.Equal(2, snapshot.DetectedDevices.Count);
        var receiver = Assert.Single(snapshot.DetectedDevices, d => d.SourceAttempts.Any(a => a.Source == ReceiverSource));
        return new(receiver.Id, snapshot.DetectedDevices.Select(d => new DeviceLinkMember(d.Id, d.Name, d.Kind)).ToArray());
    }

    private static ClockInstant At(int seconds) => new(Start.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));
    private static DeviceObservation Receiver(int seconds, int? percent) =>
        Observation(seconds, ReceiverEndpoint, ReceiverSource, percent, BatteryEvidence.ActiveRead, true, null);

    private static DeviceObservation Observation(int seconds, string endpoint, string source, int? percent,
        BatteryEvidence evidence, bool? connected, string? address)
    {
        var at = At(seconds);
        return new(endpoint, null, address, address is null ? "Receiver keyboard" : "Bluetooth keyboard", DeviceKind.Keyboard,
            source, at.Utc, Connected: connected, PathPresent: true,
            Battery: percent is { } value ? new(value, evidence, evidence == BatteryEvidence.ActiveRead ? at.Utc : null,
                ReadAt: at.Utc, ReadElapsed: at.Elapsed) : null,
            Status: percent is null ? ReadStatus.Missing : ReadStatus.Success, ReceivedElapsed: at.Elapsed,
            Transport: address is null ? DeviceTransport.Receiver : DeviceTransport.Bluetooth);
    }

    private sealed class Fixture(bool separateBluetoothEndpoints)
    {
        public StateStore Store { get; } = new();
        private long generation;
        public DeviceObservation Bas(int second, int? percent, bool? connected = true) =>
            Observation(second, LeEndpoint, BasSource, percent, BatteryEvidence.ActiveRead, connected, Address);
        public DeviceObservation Windows(int second, int? percent, bool? connected = true) =>
            Observation(second, separateBluetoothEndpoints ? ClassicEndpoint : LeEndpoint, WindowsSource, percent,
                BatteryEvidence.SystemCache, connected, Address);
        public DeviceObservation BluetoothConnection(int second) =>
            Observation(second, LeEndpoint, "bluetooth.connection", null, BatteryEvidence.ActiveRead, true, Address);
        public StateSnapshot Scan(int second, params DeviceObservation[] observations)
        {
            Store.BeginScan(++generation, false);
            foreach (var source in observations.GroupBy(o => o.Source))
                Store.Apply(generation, new(source.Key, source.ToArray()), At(second));
            Store.EndScan(generation);
            return Store.Snapshot(At(second));
        }
    }
}
