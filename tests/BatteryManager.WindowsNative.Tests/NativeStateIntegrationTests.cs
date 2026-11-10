using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class NativeStateIntegrationTests
{
    [Fact]
    public async Task RawPropertiesThroughRealReaderAndCorePreserveFailedValueAndHideExplicitDisconnect()
    {
        var clock = new ManualClock();
        var connected = true;
        var failed = false;
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [WindowsNativeSourceTests.Aep(connected)],
                WindowsQueryKind.PnpDevice => [WindowsNativeSourceTests.Pnp("AABBCCDDEEFF")],
                _ => []
            }),
            Read = _ => Task.FromResult(failed ? new RawPnpProperty(0x17, 0, 0, []) : WindowsNativeSourceTests.Good())
        };
        var source = new WindowsNativeSource(io, clock);
        var store = new StateStore();
        async Task Scan(long generation)
        {
            store.BeginScan(generation, true);
            await source.ReadAsync(new(generation, true), batch => store.Apply(generation, batch, clock.GetUtcNow()), CancellationToken.None);
            store.EndScan(generation);
        }
        await Scan(1);
        var online = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, online.Connection);
        Assert.Equal(73, online.BatteryPercent);
        Assert.Equal(BatteryFreshness.Recent, online.Freshness);
        Assert.Equal(DeviceTransport.Bluetooth, DeviceDisplayMapper.Map(online)!.Transport);
        var expiry = Assert.Single(online.Readings).ExpiresAt;

        clock.Advance(TimeSpan.FromSeconds(30));
        failed = true;
        await Scan(2);
        var retained = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, retained.Connection);
        Assert.Equal(73, retained.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, retained.Freshness);
        Assert.Equal(expiry, Assert.Single(retained.Readings).ExpiresAt);
        Assert.Contains("0x00000017", retained.LastError);

        clock.Advance(TimeSpan.FromSeconds(30));
        connected = false;
        failed = false;
        await Scan(3);
        var offline = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Offline, offline.Connection);
        Assert.False(offline.Visible);
    }

    [Fact]
    public async Task OnePnpFailureDoesNotTurnAnotherSuccessfulBatteryIntoFailure()
    {
        var clock = new ManualClock();
        var first = WindowsNativeSourceTests.Aep(true);
        var second = first with { Id = "aep-second", Properties = new Dictionary<string, object?>(first.Properties)
        { [WindowsPropertyNames.Address] = "112233445566" } };
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [first, second],
                WindowsQueryKind.PnpDevice => [WindowsNativeSourceTests.Pnp("AABBCCDDEEFF"), WindowsNativeSourceTests.Pnp("112233445566")],
                _ => []
            }),
            Read = id => Task.FromResult(id.Contains("112233445566", StringComparison.Ordinal)
                ? new RawPnpProperty(0x17, 0, 0, []) : WindowsNativeSourceTests.Good())
        };
        var store = new StateStore();
        store.BeginScan(1, true);
        await new WindowsNativeSource(io, clock).ReadAsync(new(1, true), batch => store.Apply(1, batch, clock.GetUtcNow()), CancellationToken.None);
        var successful = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, device => device.BatteryPercent == 73);
        Assert.Null(successful.LastError);
        Assert.Equal(ReadStatus.Success, Assert.Single(successful.Readings).Attempt);
    }

    internal sealed class ManualClock : TimeProvider
    {
        private static readonly DateTimeOffset Initial = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        private DateTimeOffset now = Initial;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => (now - Initial).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan amount) => now += amount;
    }
}
