using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class LogitechK380BatterySourceTests
{
    private const string Address = "AABBCCDDEEFF";
    private const string Container = "c5c7fb9e-4a18-444d-9fa6-22ad153bd001";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RealReaderUsesDiscoveredFeatureAndEitherVendorCollectionToProduceCoarseBattery(bool shortReplies, bool zeroDeviceIndex)
    {
        var io = new FakeIo { ShortReplies = shortReplies, ZeroDeviceIndex = zeroDeviceIndex, Feature = 9 };
        var clock = new NativeStateIntegrationTests.ManualClock();
        await using var session = new BatterySession(new LogitechK380BatterySource(io, clock), clock);
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var device = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(DeviceKind.Keyboard, device.Kind);
        Assert.Equal(DeviceTransport.Bluetooth, DeviceDisplayMapper.Map(device)!.Transport);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(90, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, device.Freshness);
        var reading = Assert.Single(device.Readings);
        Assert.Equal(LogitechK380BatterySource.SourceId, reading.Source);
        Assert.Equal("coarse:4", reading.Precision);
        Assert.False(reading.Charging);
        Assert.Contains(io.Writes, report => report[2] == 9 && (report[3] >> 4) == 0);
        Assert.All(io.Writes, report =>
        {
            Assert.Equal(20, report.Length);
            Assert.Equal(0x11, report[0]);
            Assert.Equal(0xFF, report[1]);
            Assert.InRange(report[3] & 15, 1, 15);
        });
        Assert.All(io.Channels, channel => Assert.True(channel.Disposed));
    }

    [Fact]
    public async Task UnrelatedFramesAndWrongNonceDoNotBecomeBatteryOrConnectionEvidence()
    {
        var io = new FakeIo { InjectWrongFrames = true };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new LogitechK380BatterySource(io, clock), store, clock, 1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(90, device.BatteryPercent);
        Assert.Null(device.LastError);
    }

    [Fact]
    public async Task ZeroMeansUnknownAndDoesNotRenewOldValueBeforeExpiryOrPreventRecovery()
    {
        var io = new FakeIo();
        var clock = new NativeStateIntegrationTests.ManualClock();
        await using var session = new BatterySession(new LogitechK380BatterySource(io, clock), clock);
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var first = Assert.Single(session.Snapshot.State.Devices);
        var expiry = Assert.Single(first.Readings).ExpiresAt;
        clock.Advance(TimeSpan.FromSeconds(30));
        io.Level = 0;
        io.NextLevel = 0;
        await session.RefreshAsync();
        var retained = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(90, retained.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, retained.Freshness);
        Assert.Equal(expiry, Assert.Single(retained.Readings).ExpiresAt);
        Assert.Equal(ConnectionState.Online, retained.Connection);

        clock.Advance(TimeSpan.FromMinutes(15));
        await session.RefreshAsync();
        var expired = Assert.Single(session.Snapshot.State.Devices);
        Assert.Null(expired.BatteryPercent);
        Assert.Equal(BatteryFreshness.Expired, expired.Freshness);
        Assert.Equal(expiry, Assert.Single(expired.Readings).ExpiresAt);

        clock.Advance(TimeSpan.FromSeconds(1));
        io.Level = 50;
        io.NextLevel = 20;
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var recovered = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(first.Id, recovered.Id);
        Assert.Equal(50, recovered.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, recovered.Freshness);
        Assert.True(Assert.Single(recovered.Readings).ExpiresAt > expiry);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(101, 50, 0)]
    [InlineData(20, 50, 0)]
    [InlineData(90, 50, 1)]
    [InlineData(90, 50, 3)]
    [InlineData(90, 50, 7)]
    [InlineData(90, 50, 8)]
    public async Task UnknownMalformedOrUnverifiedChargingReportsNeverInventAValidPercentage(byte level, byte next, byte status)
    {
        var io = new FakeIo { Level = level, NextLevel = next, BatteryStatus = status };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new LogitechK380BatterySource(io, clock, TimeSpan.FromMilliseconds(75)), store, clock, 1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Null(device.BatteryPercent);
        Assert.DoesNotContain(device.Readings, reading => reading.Percent is not null);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(101, 0)]
    [InlineData(4, 8)]
    [InlineData(4, 128)]
    public async Task InvalidCapabilityDoesNotPublishAnUnverifiedBattery(byte count, byte flags)
    {
        var io = new FakeIo { LevelCount = count, CapabilityFlags = flags };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new LogitechK380BatterySource(io, clock), store, clock, 1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Null(device.BatteryPercent);
        Assert.NotNull(device.LastError);
    }

    [Theory]
    [InlineData(2, 0, "coarse:2")]
    [InlineData(100, 0, "coarse:100")]
    [InlineData(100, 2, "percent")]
    [InlineData(4, 7, "coarse:4")]
    public async Task ReportedPrecisionRequiresBothEnoughLevelsAndTheMileageCapability(byte count, byte flags, string precision)
    {
        var io = new FakeIo { LevelCount = count, CapabilityFlags = flags };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new LogitechK380BatterySource(io, clock), store, clock, 1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(90, device.BatteryPercent);
        Assert.Equal(precision, Assert.Single(device.Readings).Precision);
    }

    [Fact]
    public async Task SlowBatteryFailureDoesNotMoveTheEarlierPingConnectionTime()
    {
        var clock = new NativeStateIntegrationTests.ManualClock();
        var io = new FakeIo { NoBatteryReply = true, OnBatteryQuery = () => clock.Advance(TimeSpan.FromSeconds(100)) };
        var store = new StateStore();
        await Scan(new LogitechK380BatterySource(io, clock, TimeSpan.FromMilliseconds(75)), store, clock, 1);
        Assert.Equal(ConnectionState.Recent, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Connection);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(ConnectionState.Pending, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Connection);
    }

    [Fact]
    public async Task WindowsAndHidBecomeOneKeyboardAndExplicitDisconnectHidesItUntilARealReadRecovers()
    {
        var io = new FakeIo();
        var connected = true;
        var windowsIo = new WindowsNativeSourceTests.FakeIo
        {
            Enumerate = kind => Task.FromResult<IReadOnlyList<RawWindowsDevice>>(kind == WindowsQueryKind.BluetoothClassic
                ? [new("k380-aep", "Keyboard K380", new Dictionary<string, object?>
                {
                    [WindowsPropertyNames.Address] = Address,
                    ["System.Devices.Aep.IsConnected"] = connected,
                    ["System.Devices.Aep.ContainerId"] = Container
                })] : [])
        };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var source = new CompositeDeviceSource(new WindowsNativeSource(windowsIo, clock),
            new LogitechK380BatterySource(io, clock, TimeSpan.FromMilliseconds(75)));
        await using var session = new BatterySession(source, clock);
        await session.RefreshAsync();
        var first = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(90, first.BatteryPercent);

        clock.Advance(TimeSpan.FromSeconds(30));
        connected = false;
        io.NoReplies = true;
        await session.RefreshAsync();
        var offline = Assert.Single(session.Snapshot.State.Devices);
        Assert.False(offline.Visible);
        Assert.Equal(ConnectionState.Offline, offline.Connection);

        clock.Advance(TimeSpan.FromSeconds(30));
        connected = true;
        // Windows online alone must not promote the pre-disconnect value to current.
        await session.RefreshAsync();
        var waiting = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(ConnectionState.Online, waiting.Connection);
        Assert.NotEqual(BatteryFreshness.Current, waiting.Freshness);

        clock.Advance(TimeSpan.FromSeconds(1));
        io.NoReplies = false;
        io.Level = 50;
        io.NextLevel = 20;
        await session.RefreshAsync();
        var recovered = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(first.Id, recovered.Id);
        Assert.True(recovered.Visible);
        Assert.Equal(ConnectionState.Online, recovered.Connection);
        Assert.Equal(50, recovered.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, recovered.Freshness);
    }

    [Fact]
    public async Task OnlyVerifiedBluetoothVendorCollectionsAreOpened()
    {
        var shortDevice = Device(1);
        var longDevice = Device(2);
        var io = new FakeIo
        {
            Inventory = new([
                shortDevice, longDevice, shortDevice,
                shortDevice with { Path = shortDevice.Path.Replace("test", "ordinary-keys"), UsagePage = 1, Usage = 6 },
                longDevice with { Path = longDevice.Path.Replace("test", "other-pid"), ProductId = 0xB3B6 },
                longDevice with { Path = longDevice.Path.Replace("test", "other-maker"), Manufacturer = "Other" },
                longDevice with { Path = longDevice.Path.Replace("test", "missing-maker"), Manufacturer = null },
                longDevice with { Path = longDevice.Path.Replace("test", "other-model"), ProductName = "K380s" },
                longDevice with { Path = @"\\?\hid#vid_046d&pid_b342&col07#usb", },
                longDevice with { Path = longDevice.Path.Replace("test", "bad-length"), InputReportLength = 7 }
            ], true, [])
        };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new LogitechK380BatterySource(io, clock), store, clock, 1);
        Assert.Equal(new[] { shortDevice.Path, longDevice.Path }.Order(), io.Opened.Order());
        Assert.Equal(90, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).BatteryPercent);
    }

    [Theory]
    [InlineData("missing-serial")]
    [InlineData("missing-container")]
    [InlineData("mismatched-serial")]
    [InlineData("mismatched-container")]
    [InlineData("duplicate-short")]
    [InlineData("one-address-two-containers")]
    [InlineData("one-container-two-addresses")]
    [InlineData("one-path-two-identities")]
    [InlineData("one-path-invalid-serial")]
    public async Task MissingOrAmbiguousIdentityNeverCombinesUnrelatedCollections(string scenario)
    {
        var first = Device(1);
        var second = Device(2);
        HidInterface[] inventory = scenario switch
        {
            "missing-serial" => [first with { SerialNumber = null }, second with { SerialNumber = null }],
            "missing-container" => [first with { ContainerId = null }, second with { ContainerId = null }],
            "mismatched-serial" => [first, second with { SerialNumber = "112233445566" }],
            "mismatched-container" => [first, second with { ContainerId = "b5c7fb9e-4a18-444d-9fa6-22ad153bd002" }],
            "duplicate-short" => [first, first with { Path = first.Path.Replace("test", "another", StringComparison.Ordinal) }, second],
            "one-address-two-containers" => [first, second,
                first with { Path = first.Path.Replace("test", "another", StringComparison.Ordinal), ContainerId = "b5c7fb9e-4a18-444d-9fa6-22ad153bd002" },
                second with { Path = second.Path.Replace("test", "another", StringComparison.Ordinal), ContainerId = "b5c7fb9e-4a18-444d-9fa6-22ad153bd002" }],
            "one-path-two-identities" => [first, second,
                first with { SerialNumber = "112233445566", ContainerId = "b5c7fb9e-4a18-444d-9fa6-22ad153bd002" },
                second with { SerialNumber = "112233445566", ContainerId = "b5c7fb9e-4a18-444d-9fa6-22ad153bd002" }],
            "one-path-invalid-serial" => [first, second, first with { SerialNumber = null }],
            _ => [first, second,
                first with { Path = first.Path.Replace("test", "another", StringComparison.Ordinal), SerialNumber = "112233445566" },
                second with { Path = second.Path.Replace("test", "another", StringComparison.Ordinal), SerialNumber = "112233445566" }]
        };
        var io = new FakeIo { Inventory = new(inventory, true, []) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new LogitechK380BatterySource(io, clock), store, clock, 1);
        Assert.Empty(io.Opened);
        Assert.Empty(store.Snapshot(clock.GetUtcNow()).Devices);
    }

    [Fact]
    public async Task ContinuousNoOperationInputCannotExtendDeadlineOrProveOnline()
    {
        var io = new FakeIo { NoReplies = true, ContinuousNoOperation = true };
        var batches = new ConcurrentBag<SourceBatch>();
        var elapsed = Stopwatch.StartNew();
        await new LogitechK380BatterySource(io, attemptTimeout: TimeSpan.FromMilliseconds(100))
            .ReadAsync(new(1, true), batches.Add, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(batches.SelectMany(batch => batch.Observations), observation => observation.Connected == true || observation.Battery is not null);
        Assert.All(io.Channels, channel => Assert.True(channel.Disposed));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MatchedProtocolErrorEndsThatReadAndRetainsPreviousBattery(bool shortReplies, bool hid1Error)
    {
        var io = new FakeIo { ShortReplies = shortReplies, Hid1Error = hid1Error };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new LogitechK380BatterySource(io, clock);
        await Scan(source, store, clock, 1);
        var expiry = Assert.Single(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Readings).ExpiresAt;
        clock.Advance(TimeSpan.FromSeconds(30));
        io.BatteryError = true;
        await Scan(source, store, clock, 2);
        var failed = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(90, failed.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, failed.Freshness);
        Assert.Equal(expiry, Assert.Single(failed.Readings).ExpiresAt);
        Assert.NotNull(failed.LastError);
        Assert.All(io.Channels, channel => Assert.True(channel.Disposed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationOrTimeoutWaitsForBothPhysicalReadsAndDisposalAndRejectsLateReply(bool callerCanceled)
    {
        var io = new FakeIo { NoReplies = true, HoldCanceledReads = true, HoldDisposal = true, ReturnLateAfterCancel = true };
        var source = new LogitechK380BatterySource(io, attemptTimeout: callerCanceled ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100));
        var batches = new ConcurrentBag<SourceBatch>();
        using var cancel = new CancellationTokenSource();
        var scan = source.ReadAsync(new(1, true), batches.Add, cancel.Token);
        try
        {
            await io.BothReadsStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (callerCanceled) cancel.Cancel();
            await io.ReadCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(scan.IsCompleted);
            Assert.DoesNotContain(io.Channels, channel => channel.Disposed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(new(2, true), _ => { }, CancellationToken.None));
            io.ReleaseReads.TrySetResult();
            await io.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(scan.IsCompleted);
            io.ReleaseDisposal.TrySetResult();
            await scan.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(io.Channels, channel => Assert.True(channel.Disposed));
            Assert.DoesNotContain(batches.SelectMany(batch => batch.Observations), observation => observation.Battery is not null || observation.Connected == true);
        }
        finally
        {
            cancel.Cancel();
            io.ReleaseReads.TrySetResult();
            io.ReleaseDisposal.TrySetResult();
            await scan.WaitAsync(TimeSpan.FromSeconds(3));
        }
        io.NoReplies = false;
        io.HoldCanceledReads = false;
        io.HoldDisposal = false;
        await source.ReadAsync(new(3, true), batches.Add, CancellationToken.None);
        Assert.Contains(batches.SelectMany(batch => batch.Observations), observation => observation.Battery?.Percent == 90);
    }

    [Fact]
    public async Task MissingFeatureDoesNotSendBatteryQueriesOrInventBattery()
    {
        var io = new FakeIo { Feature = 0 };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new LogitechK380BatterySource(io, clock), store, clock, 1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Null(device.BatteryPercent);
        Assert.All(io.Writes, report => Assert.Equal(0, report[2]));
    }

    private static async Task Scan(IDeviceSource source, StateStore store, TimeProvider clock, long generation)
    {
        store.BeginScan(generation, true);
        await source.ReadAsync(new(generation, true), batch => store.Apply(generation, batch, clock.GetUtcNow()), CancellationToken.None);
        store.EndScan(generation);
    }

    private static HidInterface Device(ushort usage)
    {
        var col = usage == 1 ? "06" : "07";
        var length = usage == 1 ? (ushort)7 : (ushort)20;
        return new($@"\\?\hid#{{00001124-0000-1000-8000-00805f9b34fb}}_vid&0002046d_pid&b342&col{col}#8&test&3&000{usage}#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
            0x046D, 0xB342, 0xFF00, usage, length, length, 0,
            "Multi-Device Keyboard K380", "Logitech", Container, Address);
    }

    private sealed class FakeIo : IWindowsHidIo
    {
        public HidInventory Inventory = new([Device(1), Device(2)], true, []);
        public byte Feature = 6;
        public byte Level = 90;
        public byte NextLevel = 50;
        public byte BatteryStatus;
        public byte LevelCount = 4;
        public byte CapabilityFlags;
        public bool ShortReplies;
        public bool ZeroDeviceIndex;
        public bool NoReplies;
        public bool InjectWrongFrames;
        public bool ContinuousNoOperation;
        public bool BatteryError;
        public bool Hid1Error;
        public bool NoBatteryReply;
        public Action? OnBatteryQuery;
        public bool HoldCanceledReads;
        public bool ReturnLateAfterCancel;
        public bool HoldDisposal;
        public ConcurrentBag<string> Opened = [];
        public ConcurrentQueue<byte[]> Writes = [];
        public ConcurrentBag<FakeChannel> Channels = [];
        public TaskCompletionSource BothReadsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadCancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<ushort, FakeChannel> current = [];
        private int activeReads;

        public Task<HidInventory> EnumerateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current.Clear();
            return Task.FromResult(Inventory);
        }

        public Task<IHidChannel> OpenAsync(HidInterface device, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(device.Path);
            var channel = new FakeChannel(this, device.Usage);
            current.Add(device.Usage, channel);
            Channels.Add(channel);
            return Task.FromResult<IHidChannel>(channel);
        }

        public void Write(byte[] report, ushort usage)
        {
            Assert.Equal(2, usage);
            Assert.Equal(20, report.Length);
            Assert.Equal(0x11, report[0]);
            Assert.Equal(0xFF, report[1]);
            Assert.InRange(report[3] & 15, 1, 15);
            Writes.Enqueue(report.ToArray());
            if (NoReplies) return;
            var function = report[3] >> 4;
            byte[] parameters;
            if (report[2] == 0 && function == 1)
            {
                Assert.Equal(0, report[4]);
                Assert.Equal(0, report[5]);
                parameters = [4, 5, report[6]];
            }
            else if (report[2] == 0 && function == 0)
            {
                Assert.Equal(new byte[] { 0x10, 0, 0 }, report[4..7]);
                parameters = [Feature, 0, 0];
            }
            else if (report[2] == Feature && function == 0)
            {
                OnBatteryQuery?.Invoke();
                if (NoBatteryReply) return;
                parameters = [Level, NextLevel, BatteryStatus];
            }
            else if (report[2] == Feature && function == 1) parameters = [LevelCount, CapabilityFlags, 0];
            else throw new InvalidOperationException("Unexpected non-battery command.");

            var reply = new byte[ShortReplies ? 7 : 20];
            reply[0] = ShortReplies ? (byte)0x10 : (byte)0x11;
            reply[1] = ZeroDeviceIndex ? (byte)0 : (byte)0xFF;
            reply[2] = report[2];
            reply[3] = report[3];
            parameters.CopyTo(reply, 4);
            var target = current[ShortReplies ? (ushort)1 : (ushort)2];
            if (InjectWrongFrames)
            {
                target.Input.Writer.TryWrite([0x10, 0xFF, 0, 0, 0, 0, 0]);
                // Mismatched report id/length and another device's frame are not replies.
                var badLength = reply.ToArray();
                badLength[0] = ShortReplies ? (byte)0x11 : (byte)0x10;
                target.Input.Writer.TryWrite(badLength);
                var wrongDevice = reply.ToArray();
                wrongDevice[1] = 1;
                target.Input.Writer.TryWrite(wrongDevice);
                var wrongSwid = reply.ToArray();
                wrongSwid[3] = (byte)((reply[3] & 0xF0) | ((reply[3] & 15) % 15 + 1));
                wrongSwid[4] = 1;
                target.Input.Writer.TryWrite(wrongSwid);
                if (report[2] == 0 && function == 1)
                {
                    var wrongNonce = reply.ToArray();
                    wrongNonce[4] = 1;
                    wrongNonce[6] ^= 0xFF;
                    target.Input.Writer.TryWrite(wrongNonce);
                }
            }
            if (BatteryError && report[2] == Feature && function == 0)
            {
                reply[2] = Hid1Error ? (byte)0x8F : (byte)0xFF;
                reply[3] = report[2];
                reply[4] = report[3];
                reply[5] = 0x02;
            }
            target.Input.Writer.TryWrite(reply);
        }

        public sealed class FakeChannel(FakeIo owner, ushort usage) : IHidChannel
        {
            public readonly Channel<byte[]> Input = Channel.CreateUnbounded<byte[]>();
            public bool Disposed;
            private int reading;

            public Task WriteOutputReportAsync(byte[] report, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("K380 must use the verified interrupt output path.");

            public Task WriteInterruptReportAsync(byte[] report, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal(0, Volatile.Read(ref reading));
                Assert.False(Disposed);
                owner.Write(report, usage);
                return Task.CompletedTask;
            }

            public async Task<byte[]> ReadInputReportAsync(CancellationToken cancellationToken)
            {
                Assert.False(Disposed);
                Assert.Equal(1, Interlocked.Increment(ref reading));
                if (Interlocked.Increment(ref owner.activeReads) == 2) owner.BothReadsStarted.TrySetResult();
                try
                {
                    if (owner.ContinuousNoOperation)
                    {
                        await Task.Delay(2, cancellationToken);
                        return [0x10, 0xFF, 0, 0, 0, 0, 0];
                    }
                    return await Input.Reader.ReadAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    owner.ReadCancellationObserved.TrySetResult();
                    if (owner.HoldCanceledReads) await owner.ReleaseReads.Task;
                    if (owner.ReturnLateAfterCancel)
                    {
                        var request = owner.Writes.Last();
                        var late = new byte[usage == 1 ? 7 : 20];
                        late[0] = usage == 1 ? (byte)0x10 : (byte)0x11;
                        late[1] = 0xFF;
                        late[2] = request[2];
                        late[3] = request[3];
                        late[4] = 4;
                        late[5] = 5;
                        late[6] = request[6];
                        return late;
                    }
                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref reading);
                    Interlocked.Decrement(ref owner.activeReads);
                }
            }

            public async ValueTask DisposeAsync()
            {
                owner.DisposalStarted.TrySetResult();
                Assert.Equal(0, Volatile.Read(ref reading));
                if (owner.HoldDisposal) await owner.ReleaseDisposal.Task;
                Disposed = true;
            }
        }
    }
}
