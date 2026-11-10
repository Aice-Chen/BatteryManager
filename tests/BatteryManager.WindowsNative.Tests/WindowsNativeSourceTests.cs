using System.Collections.Concurrent;
using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class WindowsNativeSourceTests
{
    [Fact]
    public async Task UnattributedNonBluetoothInterfacesStayOutOfProductDevices()
    {
        var io = new FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind == WindowsQueryKind.DeviceInterface
                ? [new("HID\\VID_1234&PID_5678", "Gaming Keyboard", new Dictionary<string, object?>
                    { ["System.Devices.Connected"] = true, [WindowsPropertyNames.BatteryLife] = 100u }),
                    new("BTH\\MS_BTHBRB\\LOCAL", "Bluetooth local stack", new Dictionary<string, object?> { ["System.Devices.Connected"] = true })] : [])
        };
        var source = new WindowsNativeSource(io);
        var batches = new ConcurrentBag<SourceBatch>();
        await source.ReadAsync(new(1, true), batches.Add, CancellationToken.None);
        Assert.Empty(batches.SelectMany(batch => batch.Observations));
        Assert.Equal(2, source.DiagnosticOnlyCounts[WindowsQueryKind.DeviceInterface]);
    }

    [Fact]
    public async Task RawAepAndPnpHaveSameEndpointAndKeepUnknownBatterySourceTime()
    {
        var io = new FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [Aep(true)],
                WindowsQueryKind.PnpDevice => [Pnp("AABBCCDDEEFF")],
                _ => []
            })
        };
        var batches = new ConcurrentBag<SourceBatch>();
        await new WindowsNativeSource(io).ReadAsync(new(1, true), batches.Add, CancellationToken.None);

        var connected = Assert.Single(batches, b => b.Source == "windows.aep.classic" && !b.Complete).Observations.Single();
        var battery = Assert.Single(batches, b => b.Source == "windows.pnp" && !b.Complete).Observations.Single();
        Assert.Equal("bt-classic:AABBCCDDEEFF", battery.EndpointId);
        Assert.Equal(connected.EndpointId, battery.EndpointId);
        Assert.True(connected.Connected);
        Assert.Equal(73, battery.Battery!.Percent);
        Assert.Equal(BatteryEvidence.SystemCache, battery.Battery.Evidence);
        Assert.Null(battery.Battery.ObservedAt);
        Assert.All(batches.Where(b => b.Complete), b => Assert.True(b.Authoritative));
    }

    [Fact]
    public async Task DisconnectedAepAndOtherEndpointRemainInCompleteMembership()
    {
        var first = Aep(false);
        var second = Aep(true) with { Id = "aep-second", Properties = new Dictionary<string, object?>(Aep(true).Properties)
        { [WindowsPropertyNames.Address] = "112233445566" } };
        var io = new FakeIo { Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(
            kind == WindowsQueryKind.BluetoothClassic ? [first, second] : []) };
        var batches = new ConcurrentBag<SourceBatch>();
        await new WindowsNativeSource(io).ReadAsync(new(1, true), batches.Add, CancellationToken.None);
        var final = Assert.Single(batches, b => b.Source == "windows.aep.classic" && b.Complete);
        Assert.Equal(2, final.Observations.Count);
        Assert.Contains(final.Observations, d => d.Connected == false);
        Assert.Contains(final.Observations, d => d.Connected == true);
        Assert.All(final.Observations, d => Assert.Null(d.ContainerId));
    }

    [Fact]
    public async Task SuccessfulDevicePublishesWhileAnotherDeviceIsPhysicallyBlocked()
    {
        var release = new TaskCompletionSource<RawPnpProperty>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(
                kind == WindowsQueryKind.PnpDevice ? [Pnp("000000000001"), Pnp("000000000002"), Pnp("000000000003")] : []),
            Read = id => id.Contains("000000000002", StringComparison.Ordinal) ? release.Task : Task.FromResult(Good())
        };
        var scan = new WindowsNativeSource(io).ReadAsync(new(1, true), batch =>
        {
            if (batch.Observations.Any(d => d.EndpointId.EndsWith("000000000003", StringComparison.Ordinal)))
                thirdPublished.TrySetResult();
        }, CancellationToken.None);
        try
        {
            await thirdPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(scan.IsCompleted);
        }
        finally { release.TrySetResult(Good()); await scan.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task QueryFailureDoesNotSwallowSuccessfulQueryOrClaimAuthoritativeAbsence()
    {
        var io = new FakeIo { Enumerate = kind => kind == WindowsQueryKind.DeviceInterface
            ? Task.FromException<IReadOnlyList<RawWindowsDevice>>(new UnauthorizedAccessException("test"))
            : Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind == WindowsQueryKind.BluetoothClassic ? [Aep(true)] : []) };
        var batches = new ConcurrentBag<SourceBatch>();
        await new WindowsNativeSource(io).ReadAsync(new(1, true), batches.Add, CancellationToken.None);
        Assert.Contains(batches, b => b.Source == "windows.aep.classic" && b.Observations.Any(d => d.Connected == true));
        var failure = Assert.Single(batches, b => b.Source == "windows.interface");
        Assert.False(failure.Authoritative);
        Assert.NotNull(failure.Error);
    }

    [Fact]
    public async Task CancellationKeepsPhysicalOwnershipAndNeverStartsQueuedNodeWork()
    {
        var release = new TaskCompletionSource<RawPnpProperty>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slotsFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var io = new FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind == WindowsQueryKind.PnpDevice
                ? Enumerable.Range(1, 8).Select(i => Pnp(i.ToString("X12"))).ToArray() : []),
            Read = _ => { if (Interlocked.Increment(ref calls) == 4) slotsFull.TrySetResult(); return release.Task; }
        };
        using var cancellation = new CancellationTokenSource();
        var source = new WindowsNativeSource(io);
        var scan = source.ReadAsync(new(1, true), _ => { }, cancellation.Token);
        try
        {
            await slotsFull.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(scan.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(new(2, true), _ => { }, CancellationToken.None));
        }
        finally { release.TrySetResult(Good()); await scan.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData(0x12, 0x64)]
    [InlineData(0x06, 0x64)]
    [InlineData(0x07, 0x64)]
    public void UnknownTypeOrShortBytesNeverBecomeBattery(int type, byte value)
    {
        var result = WindowsPropertyDecoder.DecodePnp(new(0, 0, (uint)type, [value]));
        Assert.Null(result.Sample);
        Assert.Equal(ReadStatus.Error, result.Status);
    }

    [Theory]
    [InlineData(0x17, ReadStatus.Error)]
    [InlineData(0x0D, ReadStatus.Missing)]
    public void LocateErrorsRetainTheirMeaning(int error, ReadStatus status)
    {
        var result = WindowsPropertyDecoder.DecodePnp(new(error, 0, 0, []));
        Assert.Equal(status, result.Status);
        Assert.Equal($"CR_0x{error:X8}", result.NativeCode);
        if (status == ReadStatus.Missing) Assert.Null(result.Error);
        else Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task OrdinaryMissingPnpPropertyDoesNotTurnSourceIntoFailure()
    {
        var io = new FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind == WindowsQueryKind.PnpDevice ? [Pnp("AABBCCDDEEFF")] : []),
            Read = _ => Task.FromResult(new RawPnpProperty(0, 0x25, 0, []))
        };
        var batches = new ConcurrentBag<SourceBatch>();
        await new WindowsNativeSource(io).ReadAsync(new(1, true), batches.Add, CancellationToken.None);
        var final = Assert.Single(batches, batch => batch.Source == "windows.pnp" && batch.Complete);
        Assert.True(final.Authoritative);
        Assert.Null(final.Error);
        var missing = Assert.Single(final.Observations);
        Assert.Equal(ReadStatus.Missing, missing.Status);
        Assert.Null(missing.Error);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(100, 100, false)]
    [InlineData(101, 1, true)]
    [InlineData(160, 60, true)]
    [InlineData(200, 100, true)]
    public void CombinedBatteryUsesPercentEncoding(int raw, int percent, bool charging)
    {
        var value = WindowsPropertyDecoder.DecodeNamed(new Dictionary<string, object?> { [WindowsPropertyNames.BatteryPlusCharging] = (byte)raw });
        Assert.Equal(percent, value.Sample!.Percent);
        Assert.Equal(charging, value.Sample.Charging);
    }

    [Fact]
    public void NamedPropertyOnlyAcceptsIntegersAndPreservesRealZero()
    {
        Assert.Null(WindowsPropertyDecoder.DecodeNamed(new Dictionary<string, object?> { [WindowsPropertyNames.BatteryLife] = "64" }).Sample);
        Assert.Null(WindowsPropertyDecoder.DecodeNamed(new Dictionary<string, object?> { [WindowsPropertyNames.BatteryLife] = true }).Sample);
        Assert.Equal(0, WindowsPropertyDecoder.DecodeNamed(new Dictionary<string, object?> { [WindowsPropertyNames.BatteryLife] = 0u }).Sample!.Percent);
        var charging = WindowsPropertyDecoder.DecodeNamed(new Dictionary<string, object?> { [WindowsPropertyNames.BatteryPlusCharging] = 160u });
        Assert.Equal(60, charging.Sample!.Percent);
        Assert.True(charging.Sample.Charging);
    }

    internal static RawWindowsDevice Aep(bool connected) => new("aep-test", "Test Buds", new Dictionary<string, object?>
    {
        [WindowsPropertyNames.Address] = "AA:BB:CC:DD:EE:FF",
        ["System.Devices.Aep.IsConnected"] = connected,
        ["System.Devices.Aep.IsPresent"] = true,
        ["System.Devices.Aep.ContainerId"] = "00000000-0000-0000-ffff-ffffffffffff"
    });

    internal static RawWindowsDevice Pnp(string address) => new($"BTHENUM\\DEV_{address}\\TEST", "Test Buds", new Dictionary<string, object?>
    { ["System.Devices.Present"] = true });
    internal static RawPnpProperty Good() => new(0, 0, 0x03, [73]);

    internal sealed class FakeIo : IWindowsNativeIo
    {
        public Func<WindowsQueryKind, Task<IReadOnlyList<RawWindowsDevice>>> Enumerate { get; init; } = _ => Task.FromResult<IReadOnlyList<RawWindowsDevice>>([]);
        public Func<string, Task<RawPnpProperty>> Read { get; init; } = _ => Task.FromResult(Good());
        public Task<IReadOnlyList<RawWindowsDevice>> EnumerateAsync(WindowsQueryKind kind) => Enumerate(kind);
        public Task<RawPnpProperty> ReadBatteryPropertyAsync(string instanceId) => Read(instanceId);
    }
}
