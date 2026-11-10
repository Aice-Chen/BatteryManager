using System.Collections.Concurrent;
using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class GattBatterySourceTests
{
    [Fact]
    public async Task ActiveBASValueDrivesCurrentCoreStateWithoutReadingDisconnectedCandidates()
    {
        var clock = new NativeStateIntegrationTests.ManualClock();
        var called = new List<string>();
        var disconnected = WindowsNativeSourceTests.Aep(false) with { Id = "disconnected" };
        var io = new FakeGattIo
        {
            Candidates = [WindowsNativeSourceTests.Aep(true), disconnected],
            Read = (raw, _) => { called.Add(raw.Id); return Task.FromResult(new RawGattBattery([60], ReadStatus.Success, clock.GetUtcNow())); }
        };
        var store = new StateStore();
        store.BeginScan(1, true);
        await new GattBatterySource(io, clock).ReadAsync(new(1, true), batch => store.Apply(1, batch, clock.GetUtcNow()), CancellationToken.None);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(60, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, device.Freshness);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(clock.GetUtcNow(), Assert.Single(device.Readings).ObservedAt);
        Assert.Single(called);
        Assert.DoesNotContain("disconnected", called);
    }

    [Fact]
    public async Task InvalidBASPayloadAndReadFailureRemainEndpointScoped()
    {
        var second = WindowsNativeSourceTests.Aep(true) with { Id = "second", Properties = new Dictionary<string, object?>(WindowsNativeSourceTests.Aep(true).Properties)
        { [WindowsPropertyNames.Address] = "112233445566" } };
        var io = new FakeGattIo
        {
            Candidates = [WindowsNativeSourceTests.Aep(true), second],
            Read = (raw, _) => Task.FromResult(raw.Id == "second"
                ? new RawGattBattery([255], ReadStatus.Success) : new RawGattBattery([80], ReadStatus.Success))
        };
        var batches = new ConcurrentBag<SourceBatch>();
        await new GattBatterySource(io).ReadAsync(new(1, true), batches.Add, CancellationToken.None);
        var final = Assert.Single(batches, batch => batch.Complete);
        Assert.False(final.Authoritative);
        Assert.Null(final.Error);
        Assert.Contains(final.Observations, observation => observation.Battery?.Percent == 80 && observation.Status == ReadStatus.Success);
        Assert.Contains(final.Observations, observation => observation.Battery is null && observation.Status == ReadStatus.Error);
    }

    [Fact]
    public async Task CanceledReadRetainsOwnershipUntilPhysicalTaskFinishes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<RawGattBattery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new FakeGattIo { Candidates = [WindowsNativeSourceTests.Aep(true)], Read = (_, _) => { started.TrySetResult(); return finished.Task; } };
        var source = new GattBatterySource(io);
        using var cancellation = new CancellationTokenSource();
        var published = new ConcurrentBag<SourceBatch>();
        var scan = source.ReadAsync(new(1, true), published.Add, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(scan.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(new(2, true), published.Add, CancellationToken.None));
        }
        finally { finished.TrySetResult(new([60], ReadStatus.Success)); await scan.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Empty(published);
    }

    private sealed class FakeGattIo : IWindowsGattIo
    {
        public IReadOnlyList<RawWindowsDevice> Candidates { get; init; } = [];
        public Func<RawWindowsDevice, CancellationToken, Task<RawGattBattery>> Read { get; init; } = (_, _) => Task.FromResult(new RawGattBattery(null, ReadStatus.Missing));
        public Task<IReadOnlyList<RawWindowsDevice>> EnumerateCandidatesAsync() => Task.FromResult(Candidates);
        public Task<RawGattBattery> ReadBatteryAsync(RawWindowsDevice candidate, CancellationToken cancellationToken) => Read(candidate, cancellationToken);
    }
}
