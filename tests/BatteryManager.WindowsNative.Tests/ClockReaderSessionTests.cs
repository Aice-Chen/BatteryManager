using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class ClockReaderSessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task ActualWindowsReaderAndSessionAcceptNewPropertyAfterUtcCorrection(int hours)
    {
        var clock = new AdjustableClock();
        var io = new PropertyIo();
        await using var session = new BatterySession(new WindowsNativeSource(io, clock), clock);
        await Refresh(session);
        var original = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(73, original.BatteryPercent);
        clock.Advance(TimeSpan.FromSeconds(30));
        clock.ShiftUtc(TimeSpan.FromHours(hours));
        io.Percent = 64;
        await Refresh(session);
        var current = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(original.Id, current.Id);
        Assert.Equal(ConnectionState.Online, current.Connection);
        Assert.Equal(64, current.BatteryPercent);
        Assert.Equal(BatteryFreshness.Recent, current.Freshness);
        Assert.Equal("windows.pnp", current.BatterySource);
        var reading = Assert.Single(current.Readings);
        Assert.Equal(clock.GetUtcNow(), reading.ReceivedAt);
        Assert.Null(reading.ObservedAt);
        Assert.Equal(clock.GetUtcNow().AddMinutes(15), reading.ExpiresAt);
        await session.StopAndDrainAsync().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task FailedWindowsPropertyAfterUtcCorrectionPreservesOldReadButNotBeyondElapsedLifetime(int hours)
    {
        var clock = new AdjustableClock();
        var io = new PropertyIo();
        await using var session = new BatterySession(new WindowsNativeSource(io, clock), clock);
        await Refresh(session);
        clock.Advance(TimeSpan.FromMinutes(10));
        clock.ShiftUtc(TimeSpan.FromHours(hours));
        io.FailProperty = true;
        var result = await session.RefreshAsync().WaitAsync(Deadline);
        Assert.Equal("Partial", result.Outcome);
        await Settled(session);
        var retained = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(ConnectionState.Online, retained.Connection);
        Assert.Equal(73, retained.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, retained.Freshness);
        var reading = Assert.Single(retained.Readings);
        Assert.Equal(Start, reading.ReceivedAt);
        Assert.Equal(clock.GetUtcNow(), reading.AttemptedAt);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), reading.ExpiresAt);
        clock.Advance(TimeSpan.FromMinutes(5));
        var expired = Assert.Single(session.Snapshot.State.Devices);
        Assert.Null(expired.BatteryPercent);
        Assert.Equal(BatteryFreshness.Expired, expired.Freshness);
        await session.StopAndDrainAsync().WaitAsync(Deadline);
    }

    private static async Task Refresh(BatterySession session)
    {
        Assert.Equal("Completed", (await session.RefreshAsync().WaitAsync(Deadline)).Outcome);
        await Settled(session);
    }

    private static async Task Settled(BatterySession session)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (session.Snapshot.IsRefreshing || session.Snapshot.OutstandingOperations != 0)
            await Task.Delay(5, deadline.Token);
    }

    private sealed class PropertyIo : IWindowsNativeIo
    {
        public byte Percent { get; set; } = 73;
        public bool FailProperty { get; set; }
        public Task<IReadOnlyList<RawWindowsDevice>> EnumerateAsync(WindowsQueryKind kind) =>
            Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [new("clock-aep", "Clock test keyboard", new Dictionary<string, object?>
                {
                    [WindowsPropertyNames.Address] = "AA:BB:CC:DD:EE:FF",
                    ["System.Devices.Aep.IsConnected"] = true,
                    ["System.Devices.Aep.IsPresent"] = true
                })],
                WindowsQueryKind.PnpDevice => [new(@"BTHENUM\DEV_AABBCCDDEEFF\CLOCK", "Clock test keyboard", new Dictionary<string, object?>
                {
                    ["System.Devices.Present"] = true
                })],
                _ => []
            });

        public Task<RawPnpProperty> ReadBatteryPropertyAsync(string instanceId) =>
            Task.FromResult(FailProperty ? new RawPnpProperty(0x17, 0, 0, []) : new(0, 0, 0x03, [Percent]));
    }

    // Timer callbacks keep their ordinary real-time implementation for these fast
    // reader tests. Advancing elapsed affects the shared reader/session policy clock.
    private sealed class AdjustableClock : TimeProvider
    {
        private long _elapsedTicks;
        private long _offsetTicks;
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _elapsedTicks, elapsed.Ticks);
        public void ShiftUtc(TimeSpan difference) => Interlocked.Add(ref _offsetTicks, difference.Ticks);
        public override DateTimeOffset GetUtcNow() => Start.AddTicks(Interlocked.Read(ref _elapsedTicks) + Interlocked.Read(ref _offsetTicks));
        public override long GetTimestamp() => Interlocked.Read(ref _elapsedTicks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
