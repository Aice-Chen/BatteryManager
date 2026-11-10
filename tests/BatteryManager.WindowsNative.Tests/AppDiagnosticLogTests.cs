using System.Text;
using System.Text.Json;
using BatteryManager.App;
using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class AppDiagnosticLogTests
{
    [Fact]
    public void ClockOnlyRendersDoNotConsumeCaptureSpaceButBatteryChangesRemainRecorded()
    {
        using var output = new MemoryStream();
        using var log = new AppDiagnosticLog(new FakeIo(new FakeChannel()), output);
        log.RecordView(new { percent = 9, name = "G3 A" }, 1, 1);
        for (var version = 2; version < 1000; version++)
            log.RecordView(new { percent = 9, name = "G3 A" }, version, version);
        log.RecordView(new { percent = 80, name = "G3 A" }, 1000, 1000);
        log.RecordView(new { percent = 9, name = "G3 A" }, 1001, 1001);
        var views = Events(output).Where(e => Kind(e) == "view-applied").ToArray();
        Assert.Equal(new[] { 9, 80, 9 }, views.Select(e => e.GetProperty("data").GetProperty("details").GetProperty("percent").GetInt32()));
        Assert.Equal(new long[] { 1, 1000, 1001 }, views.Select(e => e.GetProperty("data").GetProperty("stateVersion").GetInt64()));
    }

    [Fact]
    public async Task ExistingChannelCarriesOriginalReportsTokensAndTransportWithoutAdditionalIo()
    {
        using var output = new MemoryStream();
        var raw = Convert.FromHexString("00AA30A50B0A0101010900");
        var inner = new FakeChannel { Read = _ => Task.FromResult(raw) };
        var io = new FakeIo(inner);
        using var log = new AppDiagnosticLog(io, output);
        using var cancellation = new CancellationTokenSource();
        Assert.Same(io.Inventory, await log.EnumerateAsync(cancellation.Token));
        await using (var channel = await log.OpenAsync(Receiver(), cancellation.Token))
        {
            var control = new byte[] { 8, 3, 4 };
            var interrupt = MchoseG3Protocol.CreateBatteryQuery();
            await channel.WriteOutputReportAsync(control, cancellation.Token);
            await channel.WriteInterruptReportAsync(interrupt, cancellation.Token);
            Assert.Same(raw, await channel.ReadInputReportAsync(cancellation.Token));
            Assert.Same(control, Assert.Single(inner.ControlWrites));
            Assert.Same(interrupt, Assert.Single(inner.InterruptWrites));
            Assert.Equal(cancellation.Token, io.EnumerateToken);
            Assert.Equal(cancellation.Token, io.OpenToken);
            Assert.All(inner.Tokens, token => Assert.Equal(cancellation.Token, token));
        }
        Assert.Equal(1, io.OpenCount);
        Assert.Equal(1, inner.ReadCount);
        Assert.True(inner.Disposed);
        var events = Events(output);
        Assert.Equal(Convert.ToHexString(raw), Assert.Single(events, e => Kind(e) == "hid-read-complete").GetProperty("data").GetProperty("hex").GetString());
        Assert.Contains(events, e => Kind(e) == "hid-closed");
    }

    [Theory]
    [InlineData(2)] // Generic Desktop mouse
    [InlineData(6)] // Generic Desktop keyboard
    public async Task OrdinaryInputCollectionPassesThroughWithoutCapturingItsBytes(ushort usage)
    {
        using var output = new MemoryStream();
        var raw = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var inner = new FakeChannel { Read = _ => Task.FromResult(raw) };
        using var log = new AppDiagnosticLog(new FakeIo(inner), output);
        var ordinary = Receiver() with { UsagePage = 1, Usage = usage };
        await using (var channel = await log.OpenAsync(ordinary, CancellationToken.None))
        {
            Assert.Same(inner, channel);
            Assert.Same(raw, await channel.ReadInputReportAsync(CancellationToken.None));
        }
        Assert.DoesNotContain(Events(output), e => Kind(e).StartsWith("hid-", StringComparison.Ordinal));
        Assert.DoesNotContain(Convert.ToHexString(raw), Encoding.UTF8.GetString(output.ToArray()));
    }

    [Theory]
    [InlineData(1, 7, true)]
    [InlineData(2, 20, true)]
    [InlineData(6, 9, false)]
    public async Task K380CapturesOnlyItsVerifiedVendorCollections(ushort usage, ushort length, bool vendor)
    {
        using var output = new MemoryStream();
        var raw = vendor ? Convert.FromHexString("11FF060C5A320000000000000000000000000000") : new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var inner = new FakeChannel { Read = _ => Task.FromResult(raw) };
        using var log = new AppDiagnosticLog(new FakeIo(inner), output);
        var device = new HidInterface(
            @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002046d_pid&b342&col07#test",
            0x046D, 0xB342, vendor ? (ushort)0xFF00 : (ushort)1, usage, length, length, 0,
            "Multi-Device Keyboard K380", "Logitech", "11111111-2222-3333-4444-555555555555", "AABBCCDDEEFF");
        await using (var channel = await log.OpenAsync(device, CancellationToken.None))
        {
            Assert.Same(raw, await channel.ReadInputReportAsync(CancellationToken.None));
            Assert.Equal(vendor, !ReferenceEquals(channel, inner));
        }
        Assert.Equal(vendor, Events(output).Any(e => Kind(e) == "hid-read-complete"));
        if (!vendor) Assert.DoesNotContain(Convert.ToHexString(raw), Encoding.UTF8.GetString(output.ToArray()));
    }

    [Theory]
    [InlineData(0)] // read
    [InlineData(1)] // control output
    [InlineData(2)] // interrupt output
    public async Task CancellationAndDisposalWaitForTheUnderlyingPhysicalOperations(int operation)
    {
        using var output = new MemoryStream();
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physicalResult = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physicalDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = new byte[] { 1, 2, 3 };
        var inner = new FakeChannel
        {
            Read = _ => { began.TrySetResult(); return physicalResult.Task; },
            Write = (_, _) => { began.TrySetResult(); return physicalResult.Task; },
            Dispose = async () => { disposalBegan.TrySetResult(); await physicalResult.Task; await physicalDisposal.Task; }
        };
        using var log = new AppDiagnosticLog(new FakeIo(inner), output);
        using var cancellation = new CancellationTokenSource();
        var channel = await log.OpenAsync(Receiver(), cancellation.Token);
        Task operationTask = operation switch
        {
            0 => channel.ReadInputReportAsync(cancellation.Token),
            1 => channel.WriteOutputReportAsync(raw, cancellation.Token),
            _ => channel.WriteInterruptReportAsync(raw, cancellation.Token)
        };
        Task? disposalTask = null;
        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(operationTask.IsCompleted);
            disposalTask = channel.DisposeAsync().AsTask();
            await disposalBegan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disposalTask.IsCompleted);
            Assert.DoesNotContain(Events(output), e => Kind(e).EndsWith("-complete", StringComparison.Ordinal) || Kind(e) == "hid-closed");
            physicalResult.TrySetResult(raw);
            await operationTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disposalTask.IsCompleted);
            Assert.False(inner.Disposed);
            if (operation == 0) Assert.Same(raw, await (Task<byte[]>)operationTask);
        }
        finally
        {
            physicalResult.TrySetResult(raw);
            physicalDisposal.TrySetResult();
            await operationTask.WaitAsync(TimeSpan.FromSeconds(5));
            await (disposalTask ?? channel.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(inner.Disposed);
        Assert.Contains(Events(output), e => Kind(e) == "hid-closed");
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("dispose")]
    public async Task FailedDiagnosticOutputPreservesSuccessfulReadsAndOriginalIoErrors(string failure)
    {
        using var output = new FailingStream();
        var raw = new byte[] { 0, 0xAA, 0x30, 9 };
        var inner = new FakeChannel { Read = _ => Task.FromResult(raw) };
        var log = new AppDiagnosticLog(new FakeIo(inner), output);
        var channel = await log.OpenAsync(Receiver(), CancellationToken.None);
        output.Failure = failure;
        Assert.Same(raw, await channel.ReadInputReportAsync(CancellationToken.None));
        var readError = new IOException("physical read failed");
        inner.Read = _ => Task.FromException<byte[]>(readError);
        Assert.Same(readError, await Assert.ThrowsAsync<IOException>(() => channel.ReadInputReportAsync(CancellationToken.None)));
        var writeError = new IOException("physical output failed");
        inner.Write = (_, _) => Task.FromException(writeError);
        Assert.Same(writeError, await Assert.ThrowsAsync<IOException>(() => channel.WriteInterruptReportAsync(raw, CancellationToken.None)));
        await channel.DisposeAsync();
        log.Dispose();
        output.Failure = null;
        Assert.True(inner.Disposed);
    }

    [Fact]
    public async Task ExceptionFormattingFailureCannotReplaceTheOriginalIoError()
    {
        using var output = new MemoryStream();
        var original = new UnformattableException();
        var inner = new FakeChannel
        {
            Read = _ => Task.FromException<byte[]>(original),
            Write = (_, _) => Task.FromException(original)
        };
        using var log = new AppDiagnosticLog(new FakeIo(inner), output);
        await using var channel = await log.OpenAsync(Receiver(), CancellationToken.None);
        Assert.Same(original, await Assert.ThrowsAsync<UnformattableException>(() => channel.ReadInputReportAsync(CancellationToken.None)));
        Assert.Same(original, await Assert.ThrowsAsync<UnformattableException>(() => channel.WriteOutputReportAsync([8], CancellationToken.None)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormalReaderAndStatePreserveBatteryWhenDiagnosticWritingFails(bool failLog)
    {
        using var output = new FailingStream();
        byte opcode = 0;
        var inner = new FakeChannel
        {
            Write = (report, _) => { opcode = report[2]; return Task.CompletedTask; },
            Read = _ =>
            {
                var reply = new byte[65];
                reply[1] = 0xAA;
                reply[2] = opcode;
                reply[9] = opcode == 0xED ? (byte)2 : (byte)9;
                return Task.FromResult(reply);
            }
        };
        using var log = new AppDiagnosticLog(new FakeIo(inner), output);
        if (failLog) output.Failure = "write";
        var store = new StateStore();
        var clock = new NativeStateIntegrationTests.ManualClock();
        store.BeginScan(1, true);
        await new MchoseBatterySource(log, clock).ReadAsync(new(1, true), batch => store.Apply(1, batch, clock.GetUtcNow()), CancellationToken.None);
        store.EndScan(1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(9, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, device.Freshness);
        Assert.Null(device.LastError);
        Assert.True(inner.Disposed);
    }

    [Fact]
    public async Task CaptureLimitStopsLoggingWhileTheExistingChannelStillWorks()
    {
        using var output = new MemoryStream();
        var raw = new byte[] { 0xAA, 9 };
        var inner = new FakeChannel { Read = _ => Task.FromResult(raw) };
        using var log = new AppDiagnosticLog(new FakeIo(inner), output);
        await using var channel = await log.OpenAsync(Receiver(), CancellationToken.None);
        log.Record("large", new { text = new string('x', 8 * 1024 * 1024) });
        var stoppedAt = output.Length;
        Assert.False(Assert.Single(Events(output), e => Kind(e) == "capture-limit").GetProperty("data").GetProperty("complete").GetBoolean());
        Assert.Same(raw, await channel.ReadInputReportAsync(CancellationToken.None));
        log.Record("later", new { });
        Assert.Equal(stoppedAt, output.Length);
    }

    private static HidInterface Receiver() => new(
        @"\\?\hid#vid_a8a5&pid_2255&mi_02#test", 0xA8A5, 0x2255, 0xFF01, 0x10, 65, 65, 0, "MCHOSE G3 A", "YJX-CHIP");
    private static string Kind(JsonElement item) => item.GetProperty("kind").GetString()!;
    private static JsonElement[] Events(MemoryStream output) => Encoding.UTF8.GetString(output.ToArray())
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();

    private sealed class FakeIo(FakeChannel channel) : IWindowsHidIo
    {
        public HidInventory Inventory { get; } = new([Receiver()], true, []);
        public CancellationToken EnumerateToken { get; private set; }
        public CancellationToken OpenToken { get; private set; }
        public int OpenCount { get; private set; }
        public Task<HidInventory> EnumerateAsync(CancellationToken token) { EnumerateToken = token; return Task.FromResult(Inventory); }
        public Task<IHidChannel> OpenAsync(HidInterface device, CancellationToken token) { OpenToken = token; OpenCount++; return Task.FromResult<IHidChannel>(channel); }
    }

    private sealed class FakeChannel : IHidChannel
    {
        public Func<CancellationToken, Task<byte[]>> Read { get; set; } = _ => Task.FromResult(Array.Empty<byte>());
        public Func<byte[], CancellationToken, Task> Write { get; set; } = (_, _) => Task.CompletedTask;
        public Func<Task> Dispose { get; init; } = () => Task.CompletedTask;
        public List<byte[]> ControlWrites { get; } = [];
        public List<byte[]> InterruptWrites { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public int ReadCount { get; private set; }
        public bool Disposed { get; private set; }
        public Task<byte[]> ReadInputReportAsync(CancellationToken token) { Tokens.Add(token); ReadCount++; return Read(token); }
        public Task WriteOutputReportAsync(byte[] report, CancellationToken token) { Tokens.Add(token); ControlWrites.Add(report); return Write(report, token); }
        public Task WriteInterruptReportAsync(byte[] report, CancellationToken token) { Tokens.Add(token); InterruptWrites.Add(report); return Write(report, token); }
        public async ValueTask DisposeAsync() { await Dispose(); Disposed = true; }
    }

    private sealed class FailingStream : MemoryStream
    {
        public string? Failure { get; set; }
        public override void Write(ReadOnlySpan<byte> buffer) { if (Failure == "write") throw new IOException("diagnostic disk unavailable"); base.Write(buffer); }
        public override void Flush() { if (Failure == "flush") throw new IOException("diagnostic flush failed"); base.Flush(); }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (Failure == "dispose") throw new IOException("diagnostic close failed"); }
    }

    private sealed class UnformattableException : IOException
    {
        public override string ToString() => throw new InvalidOperationException("formatting failed");
    }
}
