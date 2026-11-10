using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class PowerReaderLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WindowsReaderResumeWaitsForActualPropertyCompletionAndRejectsSleepingScanValue()
    {
        var io = new GatedIo();
        await using var session = new BatterySession(new WindowsNativeSource(io));
        Assert.Equal("Completed", (await session.RefreshAsync().WaitAsync(Deadline)).Outcome);
        await Settled(session);
        var initial = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(73, initial.BatteryPercent);
        var oldRead = session.RefreshAsync();
        await io.OldReadEntered.Task.WaitAsync(Deadline);
        try
        {
            session.Suspend();
            Assert.Equal("Canceled", (await oldRead.WaitAsync(Deadline)).Outcome);
            var callsBeforeResume = io.Enumerations;
            var resume = session.ResumeAsync();
            Assert.False(resume.IsCompleted);
            Assert.Equal(1, session.Snapshot.OutstandingOperations);
            Assert.Equal("Busy", (await session.RefreshAsync()).Outcome);
            Assert.Equal(callsBeforeResume, io.Enumerations);
            Assert.Equal(2, io.PropertyReads);

            io.ReleaseOldRead.TrySetResult();
            await io.FreshReadEntered.Task.WaitAsync(Deadline);
            // The old native property call did physically return 1%. Both the
            // real reader's cancellation boundary and the session fence apply.
            Assert.Equal(1, io.OldReturnedPercent);
            var waiting = Assert.Single(session.Snapshot.State.Devices);
            Assert.Equal(initial.Id, waiting.Id);
            Assert.Equal(73, waiting.BatteryPercent);
            Assert.DoesNotContain(waiting.Readings, reading => reading.Percent == 1);
            Assert.Equal(1, io.MaximumConcurrentProperties);

            io.ReleaseFreshRead.TrySetResult();
            Assert.Equal("Completed", (await resume.WaitAsync(Deadline)).Outcome);
            await Settled(session);
            var restored = Assert.Single(session.Snapshot.State.Devices);
            Assert.Equal(initial.Id, restored.Id);
            Assert.True(restored.Visible);
            Assert.Equal(ConnectionState.Online, restored.Connection);
            Assert.Equal(64, restored.BatteryPercent);
            Assert.Equal(BatteryFreshness.Recent, restored.Freshness);
            Assert.Equal("windows.pnp", restored.BatterySource);
            Assert.Equal(3, io.PropertyReads);
            Assert.Equal(1, io.MaximumConcurrentProperties);
        }
        finally
        {
            io.ReleaseOldRead.TrySetResult();
            io.ReleaseFreshRead.TrySetResult();
            await session.StopAndDrainAsync().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task WindowsReaderShutdownDrainsOldPropertyAndPreventsQueuedResumeFromOpeningNewIo()
    {
        var io = new GatedIo();
        await using var session = new BatterySession(new WindowsNativeSource(io),
            options: SessionOptions.Default with { ShutdownTimeout = TimeSpan.FromMilliseconds(20) });
        Assert.Equal("Completed", (await session.RefreshAsync().WaitAsync(Deadline)).Outcome);
        await Settled(session);
        var oldRead = session.RefreshAsync();
        await io.OldReadEntered.Task.WaitAsync(Deadline);
        try
        {
            session.Suspend();
            var resume = session.ResumeAsync();
            var callsBeforeStop = io.Enumerations;
            var stopped = session.StopAndDrainAsync();
            Assert.False(stopped.IsCompleted);
            Assert.Equal(1, session.Snapshot.OutstandingOperations);
            Assert.Equal("Stopped", (await session.ResumeAsync()).Outcome);
            Assert.Equal("Canceled", (await oldRead.WaitAsync(Deadline)).Outcome);

            io.ReleaseOldRead.TrySetResult();
            await stopped.WaitAsync(Deadline);
            Assert.NotEqual("Completed", (await resume.WaitAsync(Deadline)).Outcome);
            Assert.False(io.FreshReadEntered.Task.IsCompleted);
            Assert.Equal(callsBeforeStop, io.Enumerations);
            Assert.Equal(2, io.PropertyReads);
            Assert.Equal(1, io.MaximumConcurrentProperties);
            Assert.Equal(0, session.Snapshot.OutstandingOperations);
            Assert.Equal(73, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
        }
        finally
        {
            io.ReleaseOldRead.TrySetResult();
            io.ReleaseFreshRead.TrySetResult();
            await session.StopAndDrainAsync().WaitAsync(Deadline);
        }
    }

    private static async Task Settled(BatterySession session)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (session.Snapshot.IsRefreshing || session.Snapshot.OutstandingOperations != 0)
            await Task.Delay(5, deadline.Token);
    }

    private sealed class GatedIo : IWindowsNativeIo
    {
        private int _enumerations;
        private int _reads;
        private int _active;
        private int _maximum;
        private int _oldReturnedPercent;
        public int Enumerations => Volatile.Read(ref _enumerations);
        public int PropertyReads => Volatile.Read(ref _reads);
        public int MaximumConcurrentProperties => Volatile.Read(ref _maximum);
        public int OldReturnedPercent => Volatile.Read(ref _oldReturnedPercent);
        public TaskCompletionSource OldReadEntered { get; } = Signal();
        public TaskCompletionSource FreshReadEntered { get; } = Signal();
        public TaskCompletionSource ReleaseOldRead { get; } = Signal();
        public TaskCompletionSource ReleaseFreshRead { get; } = Signal();

        public Task<IReadOnlyList<RawWindowsDevice>> EnumerateAsync(WindowsQueryKind kind)
        {
            Interlocked.Increment(ref _enumerations);
            return Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind switch
            {
                WindowsQueryKind.BluetoothClassic => [new("power-aep", "Power test keyboard", new Dictionary<string, object?>
                {
                    [WindowsPropertyNames.Address] = "AA:BB:CC:DD:EE:FF",
                    ["System.Devices.Aep.IsConnected"] = true,
                    ["System.Devices.Aep.IsPresent"] = true
                })],
                WindowsQueryKind.PnpDevice => [new(@"BTHENUM\DEV_AABBCCDDEEFF\POWER", "Power test keyboard", new Dictionary<string, object?>
                {
                    ["System.Devices.Present"] = true
                })],
                _ => []
            });
        }

        public async Task<RawPnpProperty> ReadBatteryPropertyAsync(string instanceId)
        {
            var call = Interlocked.Increment(ref _reads);
            var active = Interlocked.Increment(ref _active);
            int previous;
            do { previous = Volatile.Read(ref _maximum); }
            while (active > previous && Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
            try
            {
                if (call == 1) return new(0, 0, 0x03, [73]);
                if (call == 2)
                {
                    OldReadEntered.TrySetResult();
                    await ReleaseOldRead.Task;
                    Volatile.Write(ref _oldReturnedPercent, 1);
                    return new(0, 0, 0x03, [1]);
                }
                FreshReadEntered.TrySetResult();
                await ReleaseFreshRead.Task;
                return new(0, 0, 0x03, [64]);
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
