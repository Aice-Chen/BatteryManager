using System.Collections.Concurrent;
using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class ReaderClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WindowsMergedAndCompleteObservationsKeepTheOriginalPropertyReadInstantAcrossClockCorrection()
    {
        var clock = new AdjustableClock();
        var root = WindowsNativeSourceTests.Pnp("AABBCCDDEEFF");
        var sibling = root with { Id = root.Id + "-sibling" };
        var reads = 0;
        var io = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind == WindowsQueryKind.PnpDevice ? [root, sibling] : []),
            Read = _ => Task.FromResult(++reads == 1 ? WindowsNativeSourceTests.Good() : new RawPnpProperty(0, 0x25, 0, []))
        };
        var observations = new List<DeviceObservation>();
        await new WindowsNativeSource(io, clock, maximumConcurrency: 1).ReadAsync(new(1, true), batch =>
        {
            if (batch.Source != "windows.pnp") return;
            observations.AddRange(batch.Observations);
            if (observations.Count == 1) clock.Advance(TimeSpan.FromSeconds(7), TimeSpan.FromHours(-1));
        }, CancellationToken.None);

        Assert.Equal(2, reads);
        Assert.True(observations.Count >= 2);
        Assert.All(observations, observation =>
        {
            var sample = Assert.IsType<BatterySample>(observation.Battery);
            Assert.Equal(73, sample.Percent);
            Assert.Equal(Start, sample.ReadAt);
            Assert.Equal(TimeSpan.Zero, sample.ReadElapsed);
        });
        Assert.Equal(Start.AddHours(-1).AddSeconds(7), observations[^1].ReceivedAt);
        Assert.Equal(TimeSpan.FromSeconds(7), observations[^1].ReceivedElapsed);
        Assert.True(observations[^1].Battery!.ReadAt > observations[^1].ReceivedAt);
    }

    [Fact]
    public async Task GattPreservesIoCompletionTimeWhenReleaseAndPublicationFollowAClockCorrection()
    {
        var clock = new AdjustableClock();
        var io = new GattIo(() =>
        {
            var completed = ClockInstant.Read(clock);
            clock.Advance(TimeSpan.FromSeconds(9), TimeSpan.FromHours(-1));
            return new([64], ReadStatus.Success, completed.Utc, ReadElapsed: completed.Elapsed);
        });
        var batches = new ConcurrentBag<SourceBatch>();
        await new GattBatterySource(io, clock).ReadAsync(new(1, true), batches.Add, CancellationToken.None);
        var observation = Assert.Single(Assert.Single(batches, batch => batch.Complete).Observations);
        Assert.Equal(Start, observation.Battery!.ObservedAt);
        Assert.Equal(Start, observation.Battery.ReadAt);
        Assert.Equal(TimeSpan.Zero, observation.Battery.ReadElapsed);
        Assert.Equal(Start.AddHours(-1).AddSeconds(9), observation.ReceivedAt);
        Assert.Equal(TimeSpan.FromSeconds(9), observation.ReceivedElapsed);
    }

    [Fact]
    public async Task AtkReplyTimeDoesNotMoveToTheLaterChannelDisposalTime()
    {
        var clock = new AdjustableClock();
        var receiver = new HidInterface(@"\\?\hid#vid_373b&pid_11d9&mi_01&col05#7&clock&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            0x373B, 0x11D9, 0xFF02, 2, 17, 17, 0, ProductName: "ATK clock test",
            ContainerId: "11111111-1111-1111-1111-111111111111");
        var channel = new AtkChannel(() => clock.Advance(TimeSpan.FromSeconds(8), TimeSpan.FromHours(-1)));
        var io = new HidIo([receiver], _ => channel);
        var batches = new ConcurrentBag<SourceBatch>();
        await new AtkBatterySource(io, clock).ReadAsync(new(1, true), batches.Add, CancellationToken.None);
        var observation = Assert.Single(Assert.Single(batches, batch => batch.Complete).Observations);
        Assert.True(channel.Disposed);
        Assert.Equal(80, observation.Battery!.Percent);
        Assert.Equal(Start, observation.ReceivedAt);
        Assert.Equal(TimeSpan.Zero, observation.ReceivedElapsed);
        Assert.Equal(Start, observation.Battery.ReadAt);
        Assert.Equal(TimeSpan.Zero, observation.Battery.ReadElapsed);
        Assert.Equal(Start.AddHours(-1).AddSeconds(8), clock.GetUtcNow());
    }

    [Fact]
    public async Task K380BatteryKeepsMatchingReplyTimeWhileTheOtherInputDrainsAfterClockCorrection()
    {
        var clock = new AdjustableClock();
        var shortDevice = K380Device(1);
        var longDevice = K380Device(2);
        byte[] request = [];
        var shortChannel = new K380Channel(
            _ => throw new InvalidOperationException("The short channel must not write."),
            async token =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (request[2] == 6 && (request[3] >> 4) == 0)
                        clock.Advance(TimeSpan.FromSeconds(5), TimeSpan.FromHours(-1));
                    throw;
                }
                return [];
            });
        var longChannel = new K380Channel(report => request = report.ToArray(), _ =>
        {
            var parameters = request[2] == 0
                ? (request[3] >> 4) == 1 ? new byte[] { 4, 5, request[6] } : new byte[] { 6, 0, 0 }
                : (request[3] >> 4) == 1 ? new byte[] { 4, 0, 0 } : new byte[] { 90, 50, 0 };
            var reply = request.ToArray();
            parameters.CopyTo(reply, 4);
            return Task.FromResult(reply);
        });
        var io = new HidIo([shortDevice, longDevice], device => device.Usage == 1 ? shortChannel : longChannel);
        var batches = new ConcurrentBag<SourceBatch>();
        await new LogitechK380BatterySource(io, clock).ReadAsync(new(1, true), batches.Add, CancellationToken.None);
        var observation = Assert.Single(Assert.Single(batches, batch => batch.Complete).Observations);
        Assert.Equal(90, observation.Battery!.Percent);
        Assert.Equal(Start, observation.Battery.ReadAt);
        Assert.Equal(Start, observation.Battery.ObservedAt);
        Assert.Equal(TimeSpan.Zero, observation.Battery.ReadElapsed);
        Assert.Equal(TimeSpan.Zero, observation.ReceivedElapsed);
        Assert.Equal(Start.AddHours(-1).AddSeconds(5), clock.GetUtcNow());
        Assert.True(shortChannel.Disposed && longChannel.Disposed);
    }

    private static HidInterface K380Device(ushort usage)
    {
        var length = usage == 1 ? (ushort)7 : (ushort)20;
        return new($@"\\?\hid#{{00001124-0000-1000-8000-00805f9b34fb}}_vid&0002046d_pid&b342&col0{usage + 5}#8&clock&3&000{usage}#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
            0x046D, 0xB342, 0xFF00, usage, length, length, 0, "Multi-Device Keyboard K380", "Logitech",
            "11111111-1111-1111-1111-111111111111", "AABBCCDDEEFF");
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private readonly object gate = new();
        private DateTimeOffset utc = Start;
        private long elapsed;
        public override DateTimeOffset GetUtcNow() { lock (gate) return utc; }
        public override long GetTimestamp() { lock (gate) return elapsed; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration, TimeSpan correction) { lock (gate) { elapsed += duration.Ticks; utc += duration + correction; } }
    }

    private sealed class GattIo(Func<RawGattBattery> read) : IWindowsGattIo
    {
        public Task<IReadOnlyList<RawWindowsDevice>> EnumerateCandidatesAsync() =>
            Task.FromResult<IReadOnlyList<RawWindowsDevice>>([WindowsNativeSourceTests.Aep(true)]);
        public Task<RawGattBattery> ReadBatteryAsync(RawWindowsDevice device, CancellationToken token) => Task.FromResult(read());
    }

    private sealed class HidIo(IReadOnlyList<HidInterface> devices, Func<HidInterface, IHidChannel> channel) : IWindowsHidIo
    {
        public Task<HidInventory> EnumerateAsync(CancellationToken token) => Task.FromResult(new HidInventory(devices, true, []));
        public Task<IHidChannel> OpenAsync(HidInterface device, CancellationToken token) => Task.FromResult(channel(device));
    }

    private sealed class AtkChannel(Action dispose) : IHidChannel
    {
        private byte command;
        public bool Disposed;
        public Task WriteOutputReportAsync(byte[] report, CancellationToken token) { command = report[1]; return Task.CompletedTask; }
        public Task<byte[]> ReadInputReportAsync(CancellationToken token)
        {
            var report = new byte[17];
            report[0] = 8; report[1] = command; report[6] = command == 3 ? (byte)1 : (byte)80;
            return Task.FromResult(report);
        }
        public ValueTask DisposeAsync() { Disposed = true; dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class K380Channel(Action<byte[]> write, Func<CancellationToken, Task<byte[]>> read) : IHidChannel
    {
        public bool Disposed;
        public Task WriteOutputReportAsync(byte[] report, CancellationToken token) => throw new NotSupportedException();
        public Task WriteInterruptReportAsync(byte[] report, CancellationToken token) { write(report); return Task.CompletedTask; }
        public Task<byte[]> ReadInputReportAsync(CancellationToken token) => read(token);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
