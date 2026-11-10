using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class ClassicServiceIntegrationTests
{
    private const string Address = "AABBCCDDEEFF";
    private const string ProductName = "Original Earphones";
    private const string Hfp = "0000111E";
    private const string Avrcp = "0000110E";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServiceBatterySurvivesMissingSiblingsAndEitherConnectionArrivalOrder(bool connectionFirst)
    {
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var releaseConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pnpCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nodes = new[] { Root(), Service(Hfp), Service(Avrcp) };
        if (!connectionFirst) Array.Reverse(nodes);
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = async kind =>
            {
                if (kind == WindowsQueryKind.BluetoothClassic)
                {
                    if (!connectionFirst) await releaseConnection.Task;
                    return [Aep(true)];
                }
                return kind == WindowsQueryKind.PnpDevice ? nodes : [];
            },
            Read = id => Task.FromResult(id.Contains(Hfp, StringComparison.Ordinal) ? Battery(50) : Missing())
        };
        var source = new WindowsNativeSource(io, clock);
        store.BeginScan(1, true);
        var scan = source.ReadAsync(new(1, true), batch =>
        {
            store.Apply(1, batch, clock.GetUtcNow());
            if (batch.Source == WindowsNativeSource.SourceName(WindowsQueryKind.PnpDevice) && batch.Complete)
                pnpCompleted.TrySetResult();
        }, CancellationToken.None);
        try
        {
            await pnpCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!connectionFirst)
            {
                var pending = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
                Assert.Null(pending.BatteryPercent);
            }
        }
        finally
        {
            releaseConnection.TrySetResult();
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
            store.EndScan(1);
        }

        AssertUsable(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices), 50, ProductName);
    }

    [Fact]
    public async Task SuccessfulServiceBatteryIsUsableBeforeItsSiblingNativeOperationCompletes()
    {
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var releaseRoot = new TaskCompletionSource<RawPnpProperty>(TaskCreationOptions.RunContinuationsAsynchronously);
        var usable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [Aep(true)],
                WindowsQueryKind.PnpDevice => [Root(), Service(Hfp), Service(Avrcp)],
                _ => []
            }),
            Read = id => id == Root().Id ? releaseRoot.Task : Task.FromResult(
                id.Contains(Hfp, StringComparison.Ordinal) ? Battery(50) : Missing())
        };
        store.BeginScan(1, true);
        var scan = new WindowsNativeSource(io, clock).ReadAsync(new(1, true), batch =>
        {
            store.Apply(1, batch, clock.GetUtcNow());
            if (store.Snapshot(clock.GetUtcNow()).Devices.Any(device => device.BatteryPercent == 50))
                usable.TrySetResult();
        }, CancellationToken.None);
        try
        {
            await usable.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(scan.IsCompleted);
            AssertUsable(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices), 50, ProductName);
        }
        finally
        {
            releaseRoot.TrySetResult(Missing());
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
            store.EndScan(1);
        }
        AssertUsable(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices), 50, ProductName);
    }

    [Fact]
    public async Task MissingNextReadRetainsValueWithoutRenewalAndExplicitDisconnectHidesDevice()
    {
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var connected = true;
        var missing = false;
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [Aep(connected)],
                WindowsQueryKind.PnpDevice => [Service(Avrcp), Service(Hfp), Root()],
                _ => []
            }),
            Read = id => Task.FromResult(!missing && id.Contains(Hfp, StringComparison.Ordinal) ? Battery(50) : Missing())
        };
        var source = new WindowsNativeSource(io, clock);
        await Scan(source, store, clock, 1);
        var initial = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        AssertUsable(initial, 50, ProductName);
        var initialReading = Assert.Single(initial.Readings);

        missing = true;
        clock.Advance(TimeSpan.FromSeconds(30));
        await Scan(source, store, clock, 2);
        var retained = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, retained.Connection);
        Assert.Equal(50, retained.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, retained.Freshness);
        var retainedReading = Assert.Single(retained.Readings);
        Assert.Equal(initialReading.ExpiresAt, retainedReading.ExpiresAt);
        Assert.Equal(initialReading.ReceivedAt, retainedReading.ReceivedAt);
        Assert.Equal(ReadStatus.Missing, retainedReading.Attempt);

        connected = false;
        clock.Advance(TimeSpan.FromSeconds(30));
        await Scan(source, store, clock, 3);
        var offline = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Offline, offline.Connection);
        Assert.False(offline.Visible);
    }

    [Fact]
    public async Task ServiceBatteriesFromDifferentRemoteAddressesRemainSeparate()
    {
        const string secondAddress = "112233445566";
        const string secondName = "Second Earphones";
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [Aep(true), Aep(true, secondAddress, secondName)],
                WindowsQueryKind.PnpDevice => [Service(Hfp), Root(secondAddress, secondName),
                    Service(Avrcp), Service(Hfp, secondAddress, secondName), Root()],
                _ => []
            }),
            Read = id => Task.FromResult(!id.Contains(Hfp, StringComparison.Ordinal) ? Missing() :
                Battery(id.Contains(secondAddress, StringComparison.Ordinal) ? (byte)80 : (byte)50))
        };
        await Scan(new WindowsNativeSource(io, clock), store, clock, 1);
        var devices = store.Snapshot(clock.GetUtcNow()).Devices;
        Assert.Equal(2, devices.Count);
        AssertUsable(Assert.Single(devices, device => device.Name == ProductName), 50, ProductName);
        AssertUsable(Assert.Single(devices, device => device.Name == secondName), 80, secondName);
    }

    [Fact]
    public async Task SimilarNonBluetoothPathsAndInvalidServiceAddressesNeverCreateDevices()
    {
        var valid = Service(Hfp).Id;
        var ids = new[]
        {
            valid.Replace("BTHENUM", "USB", StringComparison.Ordinal),
            valid.Replace(Address, "000000000000", StringComparison.Ordinal),
            valid.Replace(Address, "FFFFFFFFFFFF", StringComparison.Ordinal),
            valid.Replace("_C00000000", "_C00", StringComparison.Ordinal),
            @"BTH\MS_BTHBRB\LOCAL&" + Address + "_C00000000"
        };
        var readCount = 0;
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind == WindowsQueryKind.PnpDevice
                ? ids.Select(id => new RawWindowsDevice(id, "Unattributed node", new Dictionary<string, object?>())).ToArray() : []),
            Read = _ => { Interlocked.Increment(ref readCount); return Task.FromResult(Battery(50)); }
        };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new WindowsNativeSource(io, clock), store, clock, 1);
        Assert.Empty(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(0, readCount);
    }

    [Fact]
    public async Task SuccessfulWindowsReadsContinuePastFifteenMinutesAndAdoptChangedValues()
    {
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        byte percent = 50;
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [Aep(true)],
                WindowsQueryKind.PnpDevice => [Root(), Service(Hfp), Service(Avrcp)],
                _ => []
            }),
            Read = id => Task.FromResult(id.Contains(Hfp, StringComparison.Ordinal) ? Battery(percent) : Missing())
        };
        var source = new WindowsNativeSource(io, clock);
        await Scan(source, store, clock, 1);
        clock.Advance(TimeSpan.FromMinutes(14));
        await Scan(source, store, clock, 2);
        AssertUsable(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices), 50, ProductName);
        clock.Advance(TimeSpan.FromMinutes(2));
        percent = 40;
        await Scan(source, store, clock, 3);
        var changed = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        AssertUsable(changed, 40, ProductName);
        var reading = Assert.Single(changed.Readings);
        Assert.Equal(clock.GetUtcNow(), reading.ReceivedAt);
        Assert.Equal(clock.GetUtcNow().AddMinutes(15), reading.ExpiresAt);
        clock.Advance(TimeSpan.FromMinutes(14));
        await Scan(source, store, clock, 4);
        AssertUsable(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices), 40, ProductName);
    }

    [Fact]
    public async Task MissingWindowsReadExpiresIndependentlyAndRecoversAfterSuccess()
    {
        const string otherAddress = "112233445566";
        const string otherName = "Other Earphones";
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        bool missing = false;
        byte percent = 50;
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [Aep(true), Aep(true, otherAddress, otherName)],
                WindowsQueryKind.PnpDevice => [Root(), Service(Hfp), Root(otherAddress, otherName), Service(Hfp, otherAddress, otherName)],
                _ => []
            }),
            Read = id => Task.FromResult(!id.Contains(Hfp, StringComparison.Ordinal) ? Missing()
                : id.Contains(otherAddress, StringComparison.Ordinal) ? Battery(80) : missing ? Missing() : Battery(percent))
        };
        var source = new WindowsNativeSource(io, clock);
        await Scan(source, store, clock, 1);
        var initial = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, d => d.Name == ProductName).Readings.Single();
        missing = true;
        clock.Advance(TimeSpan.FromMinutes(14));
        await Scan(source, store, clock, 2);
        var retained = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, d => d.Name == ProductName);
        Assert.Equal(50, retained.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, retained.Freshness);
        Assert.Equal(initial.ReceivedAt, Assert.Single(retained.Readings).ReceivedAt);
        Assert.Equal(initial.ExpiresAt, Assert.Single(retained.Readings).ExpiresAt);

        clock.Advance(TimeSpan.FromMinutes(1));
        await Scan(source, store, clock, 3);
        var expired = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, d => d.Name == ProductName);
        Assert.Equal(ConnectionState.Online, expired.Connection);
        Assert.Null(expired.BatteryPercent);
        Assert.Equal(BatteryFreshness.Expired, expired.Freshness);
        AssertUsable(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, d => d.Name == otherName), 80, otherName);

        clock.Advance(TimeSpan.FromMinutes(1));
        missing = false;
        percent = 40;
        await Scan(source, store, clock, 4);
        AssertUsable(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, d => d.Name == ProductName), 40, ProductName);
    }

    [Fact]
    public async Task LateSiblingCompletionAndFinalBatchKeepActualBatteryReadTime()
    {
        var clock = new NativeStateIntegrationTests.ManualClock();
        var firstReadAt = clock.GetUtcNow();
        var store = new StateStore();
        var releaseRoot = new TaskCompletionSource<RawPnpProperty>(TaskCreationOptions.RunContinuationsAsynchronously);
        var usable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [Aep(true)],
                WindowsQueryKind.PnpDevice => [Root(), Service(Hfp)],
                _ => []
            }),
            Read = id => id == Root().Id ? releaseRoot.Task : Task.FromResult(Battery(50))
        };
        store.BeginScan(1, true);
        var scan = new WindowsNativeSource(io, clock).ReadAsync(new(1, true), batch =>
        {
            store.Apply(1, batch, clock.GetUtcNow());
            if (store.Snapshot(clock.GetUtcNow()).Devices.Any(d => d.BatteryPercent == 50)) usable.TrySetResult();
        }, CancellationToken.None);
        try
        {
            await usable.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseRoot.TrySetResult(Missing());
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
            store.EndScan(1);
        }
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        AssertUsable(device, 50, ProductName);
        var reading = Assert.Single(device.Readings);
        Assert.Equal(firstReadAt, reading.ReceivedAt);
        Assert.Equal(firstReadAt.AddMinutes(15), reading.ExpiresAt);
    }

    [Fact]
    public async Task PartialSourceCompletionMarksUnseenDeviceBatteryAsNotUpdated()
    {
        const string second = "112233445566";
        const string third = "66778899AABB";
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        bool partial = false;
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [Aep(true), Aep(true, second, "Second Earphones"), Aep(true, third, "Third Earphones")],
                WindowsQueryKind.PnpDevice => partial ? [Service(Hfp), Service(Hfp, second, "Second Earphones")]
                    : [Service(Hfp), Service(Hfp, second, "Second Earphones"), Service(Hfp, third, "Third Earphones")],
                _ => []
            }),
            Read = id => Task.FromResult(id.Contains(second, StringComparison.Ordinal)
                ? partial ? new RawPnpProperty(0x17, 0, 0, []) : Battery(80)
                : id.Contains(third, StringComparison.Ordinal) ? Battery(60) : Battery(partial ? (byte)40 : (byte)50))
        };
        var source = new WindowsNativeSource(io, clock);
        await Scan(source, store, clock, 1);
        var previous = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, device => device.BatteryPercent == 60).Readings.Single();
        clock.Advance(TimeSpan.FromSeconds(30));
        partial = true;
        await Scan(source, store, clock, 2);
        var devices = store.Snapshot(clock.GetUtcNow()).Devices;
        Assert.Contains(devices, device => device.BatteryPercent == 40 && device.Freshness == BatteryFreshness.Recent);
        Assert.Contains(devices, device => device.BatteryPercent == 80 && device.Freshness == BatteryFreshness.Retained);
        var unseen = Assert.Single(devices, device => device.BatteryPercent == 60);
        Assert.Equal(ConnectionState.Online, unseen.Connection);
        Assert.Equal(BatteryFreshness.Retained, unseen.Freshness);
        var retained = Assert.Single(unseen.Readings);
        Assert.Equal(ReadStatus.Missing, retained.Attempt);
        Assert.Equal(previous.ReceivedAt, retained.ReceivedAt);
        Assert.Equal(previous.ExpiresAt, retained.ExpiresAt);
    }

    private static async Task Scan(WindowsNativeSource source, StateStore store, TimeProvider clock, long generation)
    {
        store.BeginScan(generation, true);
        await source.ReadAsync(new(generation, true), batch => store.Apply(generation, batch, clock.GetUtcNow()), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        store.EndScan(generation);
    }

    private static void AssertUsable(DeviceState device, int percentage, string name)
    {
        Assert.Equal(name, device.Name);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.True(device.Visible);
        Assert.Equal(percentage, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Recent, device.Freshness);
        var reading = Assert.Single(device.Readings);
        Assert.Equal(ReadStatus.Success, reading.Attempt);
        Assert.Equal(BatteryEvidence.SystemCache, reading.Evidence);
        Assert.Null(reading.ObservedAt);
        Assert.Null(device.LastError);
    }

    private static RawWindowsDevice Aep(bool connected, string address = Address, string name = ProductName) =>
        new("aep-" + address, name, new Dictionary<string, object?>
        {
            [WindowsPropertyNames.Address] = address,
            ["System.Devices.Aep.IsConnected"] = connected,
            ["System.Devices.Aep.IsPresent"] = true,
            ["System.Devices.Aep.Category"] = new[] { "Audio.Headphone" }
        });

    private static RawWindowsDevice Root(string address = Address, string name = ProductName) =>
        new($@"BTHENUM\DEV_{address}\7&1234567&0&BLUETOOTHDEVICE_{address}", name,
            new Dictionary<string, object?>
            {
                ["System.Devices.Present"] = true,
                ["System.Devices.Category"] = new[] { "Audio.Headphone" }
            });

    private static RawWindowsDevice Service(string service, string address = Address, string name = ProductName) =>
        new($@"BTHENUM\{{{service}-0000-1000-8000-00805F9B34FB}}_VID&00011234_PID&0000\7&1234567&0&{address}_C00000000",
            name + (service == Hfp ? " Hands-Free AG" : " AVRCP Transport"),
            new Dictionary<string, object?>
            {
                ["System.Devices.Present"] = true,
                ["System.Devices.Category"] = new[] { "Audio.Headphone" }
            });

    private static RawPnpProperty Battery(byte percent) => new(0, 0, 0x03, [percent]);
    private static RawPnpProperty Missing() => new(0, 0x25, 0, []);
}
