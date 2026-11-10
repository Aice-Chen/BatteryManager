using System.Collections.Concurrent;
using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class MchoseBatterySourceTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(9, 0, false)]
    [InlineData(100, 1, true)]
    [InlineData(9, 255, null)]
    public async Task ConfirmedActiveMouseUsesInterruptQueriesAndPreservesZeroAndUnknownCharging(byte percent, byte charge, bool? expectedCharging)
    {
        var channel = new FakeChannel { BatteryRead = _ => Task.FromResult(Battery(percent, charge)) };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock), store, clock, 1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal("MCHOSE G3 A", device.Name);
        Assert.Equal(DeviceTransport.Receiver, DeviceDisplayMapper.Map(device)!.Transport);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(percent, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, device.Freshness);
        Assert.Equal(expectedCharging, Assert.Single(device.Readings).Charging);
        Assert.Equal(new byte[] { 0xED, 0x30 }, channel.InterruptWrites.Select(x => x[2]));
        Assert.Empty(channel.ControlWrites);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task UnsupportedMetadataAndBluetoothWithSameIdsAreNotOpened()
    {
        var receiver = Receiver();
        var io = new FakeIo { Inventory = new([
            receiver with { Path = receiver.Path.Replace("&mi_02#", "&col01#", StringComparison.Ordinal) },
            receiver with { Path = UsbPath("other-product"), ProductName = "MCHOSE G3 V2" },
            receiver with { Path = UsbPath("other-maker"), Manufacturer = "Other" },
            receiver with { Path = UsbPath("wrong-usage"), UsagePage = 0xFF06 },
            receiver, receiver
        ], true, []) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock), store, clock, 1);
        Assert.Equal(receiver.Path, Assert.Single(io.Opened));
        Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(255)]
    public async Task InitialInactiveOrUnknownStatusStaysHiddenPendingWithoutBatteryQuery(byte status)
    {
        var channel = new FakeChannel { OnlineRead = _ => Task.FromResult(Online(status)) };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        await using var session = new BatterySession(new MchoseBatterySource(io));
        Assert.Equal(status == 1 ? "Completed" : "Partial", (await session.RefreshAsync()).Outcome);
        var device = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(ConnectionState.Pending, device.Connection);
        Assert.False(device.Visible);
        Assert.Null(DeviceDisplayMapper.Map(device));
        Assert.Null(device.BatteryPercent);
        if (status == 1) Assert.Null(device.LastError);
        else Assert.NotNull(device.LastError);
        Assert.All(channel.InterruptWrites, report => Assert.Equal(0xED, report[2]));
    }

    [Fact]
    public async Task InactiveReceiverKeepsOriginalTimesExpiresAtFifteenMinutesAndRecoversOnWake()
    {
        byte online = 2;
        byte percent = 9;
        FakeChannel? latest = null;
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(latest = new()
        {
            OnlineRead = _ => Task.FromResult(Online(online)), BatteryRead = _ => Task.FromResult(Battery(percent))
        }) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new MchoseBatterySource(io, clock);
        await Scan(source, store, clock, 1);
        var original = Assert.Single(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Readings);
        online = 1;
        for (var seconds = 30; seconds <= 900; seconds += 30)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Scan(source, store, clock, seconds / 30 + 1, manual: false);
            var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
            Assert.Equal(seconds < 45 ? ConnectionState.Online : seconds < 105 ? ConnectionState.Recent : ConnectionState.Pending, device.Connection);
            Assert.True(device.Visible);
            Assert.Equal(seconds < 900 ? 9 : (int?)null, device.BatteryPercent);
            var reading = Assert.Single(device.Readings);
            Assert.Equal(original.ObservedAt, reading.ObservedAt);
            Assert.Equal(original.ExpiresAt, reading.ExpiresAt);
            Assert.Equal(ReadStatus.Missing, reading.Attempt);
            Assert.Null(device.LastError);
            Assert.All(latest!.InterruptWrites, report => Assert.Equal(0xED, report[2]));
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        online = 2;
        percent = 8;
        await Scan(source, store, clock, 32);
        var awake = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, awake.Connection);
        Assert.Equal(8, awake.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, awake.Freshness);
        Assert.Equal(clock.GetUtcNow(), Assert.Single(awake.Readings).ObservedAt);
    }

    [Fact]
    public async Task InactiveReceiverHidesAfterPendingWindowAndSameDeviceRecoversWithFreshBattery()
    {
        byte status = 2;
        byte percent = 9;
        var channels = new List<FakeChannel>();
        var io = new FakeIo { Open = (_, _) =>
        {
            var channel = new FakeChannel
            {
                OnlineRead = _ => Task.FromResult(Online(status)),
                BatteryRead = _ => Task.FromResult(Battery(percent))
            };
            channels.Add(channel);
            return Task.FromResult<IHidChannel>(channel);
        } };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new MchoseBatterySource(io, clock);
        await ScanElapsed(source, store, clock, 1);
        var original = Assert.Single(store.Snapshot(ClockInstant.Read(clock)).Devices);
        var originalBattery = Assert.Single(original.Readings);

        status = 1;
        // Online and Recent last 105 seconds. The Pending visibility window starts
        // after that connection grace, independently of the battery's 900 seconds.
        for (var seconds = 30; seconds <= 990; seconds += 30)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await ScanElapsed(source, store, clock, seconds / 30 + 1, manual: seconds % 60 == 0);
            var device = Assert.Single(store.Snapshot(ClockInstant.Read(clock)).Devices);
            Assert.Equal(original.Id, device.Id);
            Assert.True(device.Visible);
            Assert.Equal(seconds < 900 ? 9 : (int?)null, device.BatteryPercent);
            Assert.Equal(originalBattery.ExpiresAt, Assert.Single(device.Readings).ExpiresAt);
            Assert.All(channels[^1].InterruptWrites, report => Assert.Equal(0xED, report[2]));
        }
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.True(Assert.Single(store.Snapshot(ClockInstant.Read(clock)).Devices).Visible);
        clock.Advance(TimeSpan.FromSeconds(1));
        var hidden = Assert.Single(store.Snapshot(ClockInstant.Read(clock)).Devices);
        Assert.Equal(original.Id, hidden.Id);
        Assert.Equal(ConnectionState.Pending, hidden.Connection);
        Assert.False(hidden.Visible);
        Assert.Null(hidden.BatteryPercent);

        await ScanElapsed(source, store, clock, 35, manual: true);
        Assert.False(Assert.Single(store.Snapshot(ClockInstant.Read(clock)).Devices).Visible);
        Assert.All(channels[^1].InterruptWrites, report => Assert.Equal(0xED, report[2]));

        clock.Advance(TimeSpan.FromSeconds(1));
        status = 2;
        percent = 8;
        await ScanElapsed(source, store, clock, 36);
        var recovered = Assert.Single(store.Snapshot(ClockInstant.Read(clock)).Devices);
        Assert.Equal(original.Id, recovered.Id);
        Assert.Equal(ConnectionState.Online, recovered.Connection);
        Assert.True(recovered.Visible);
        Assert.Equal(8, recovered.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, recovered.Freshness);
        Assert.Equal(clock.GetUtcNow(), Assert.Single(recovered.Readings).ObservedAt);
        Assert.Equal(new byte[] { 0xED, 0x30 }, channels[^1].InterruptWrites.Select(report => report[2]));
        Assert.All(channels, channel => Assert.True(channel.Disposed));
    }

    [Fact]
    public async Task InitiallyInactiveReceiverStaysHiddenAcrossRefreshesAndSessionRecreationUntilActivityIsConfirmed()
    {
        byte status = 1;
        var channels = new List<FakeChannel>();
        var io = new FakeIo { Open = (_, _) =>
        {
            var channel = new FakeChannel { OnlineRead = _ => Task.FromResult(Online(status)) };
            channels.Add(channel);
            return Task.FromResult<IHidChannel>(channel);
        } };
        var clock = new NativeStateIntegrationTests.ManualClock();
        string? id = null;
        for (var restart = 0; restart < 2; restart++)
        {
            await using var session = new BatterySession(new MchoseBatterySource(io, clock), clock);
            foreach (var delay in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(16) })
            {
                clock.Advance(delay);
                Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
                var device = Assert.Single(session.Snapshot.State.Devices);
                id ??= device.Id;
                Assert.Equal(id, device.Id);
                Assert.Equal(ConnectionState.Pending, device.Connection);
                Assert.False(device.Visible);
                Assert.Null(DeviceDisplayMapper.Map(device));
                Assert.Null(device.BatteryPercent);
                Assert.Empty(device.Readings);
                Assert.Null(device.LastError);
                Assert.All(channels[^1].InterruptWrites, report => Assert.Equal(0xED, report[2]));
            }
            if (restart == 0) continue;
            status = 2;
            Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
            var recovered = Assert.Single(session.Snapshot.State.Devices);
            Assert.Equal(id, recovered.Id);
            Assert.True(recovered.Visible);
            Assert.Equal(ConnectionState.Online, recovered.Connection);
            Assert.Equal(9, recovered.BatteryPercent);
            Assert.NotNull(DeviceDisplayMapper.Map(recovered));
        }
        Assert.All(channels, channel => Assert.True(channel.Disposed));
    }

    [Fact]
    public async Task FirstActiveReplyAdmitsMouseEvenWhenItsBatteryQueryTimesOut()
    {
        var channel = new FakeChannel { BatteryRead = Silence };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        await using var session = new BatterySession(new MchoseBatterySource(io, attemptTimeout: TimeSpan.FromMilliseconds(20)));
        Assert.Equal("Partial", (await session.RefreshAsync()).Outcome);
        var device = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.True(device.Visible);
        Assert.Null(device.BatteryPercent);
        Assert.Empty(device.Readings);
        Assert.Equal(ReadStatus.Timeout, Assert.Single(device.SourceAttempts).Status);
        var display = Assert.IsType<DeviceDisplay>(DeviceDisplayMapper.Map(device));
        Assert.Equal(DisplayPresence.Online, display.Presence);
        Assert.Null(display.Battery.Value);
        Assert.True(display.ReadFailed);
        Assert.False(display.TrayEligible);
        Assert.Contains(channel.InterruptWrites, report => report[2] == 0x30);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task ActiveReceiverWithPersistentBatteryFailuresStaysVisibleAfterBatteryExpires()
    {
        var failBattery = false;
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(new FakeChannel
        {
            BatteryRead = _ => failBattery ? Task.FromException<byte[]>(new IOException("Battery reply unavailable."))
                : Task.FromResult(Battery())
        }) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new MchoseBatterySource(io, clock);
        await ScanElapsed(source, store, clock, 1);
        var original = Assert.Single(store.Snapshot(ClockInstant.Read(clock)).Devices);
        var originalBattery = Assert.Single(original.Readings);
        failBattery = true;
        for (var seconds = 30; seconds <= 1200; seconds += 30)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await ScanElapsed(source, store, clock, seconds / 30 + 1);
            var device = Assert.Single(store.Snapshot(ClockInstant.Read(clock)).Devices);
            Assert.Equal(original.Id, device.Id);
            Assert.Equal(ConnectionState.Online, device.Connection);
            Assert.True(device.Visible);
            Assert.Equal(seconds < 900 ? 9 : (int?)null, device.BatteryPercent);
            Assert.Equal(originalBattery.ExpiresAt, Assert.Single(device.Readings).ExpiresAt);
            Assert.Contains("Battery reply unavailable", device.LastError);
        }
    }

    [Fact]
    public async Task AReceiverBatteryReplyWithoutAnOnlineReplyCannotConfirmTheMouse()
    {
        var emitted = false;
        var channel = new FakeChannel { OnlineRead = token =>
        {
            if (emitted) return Silence(token);
            emitted = true;
            return Task.FromResult(Battery());
        } };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock, TimeSpan.FromMilliseconds(20)), store, clock, 1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Pending, device.Connection);
        Assert.False(device.Visible);
        Assert.Null(device.BatteryPercent);
        Assert.All(channel.InterruptWrites, report => Assert.Equal(0xED, report[2]));
    }

    [Theory]
    [InlineData(0xED)]
    [InlineData(0x30)]
    public async Task WrongFramesAndTimeoutDoNotRenewBatteryOrInventDisconnectedState(byte failedCommand)
    {
        var fails = false;
        var emitted = false;
        Task<byte[]> Reply(byte command, CancellationToken token)
        {
            if (fails && command == failedCommand)
            {
                if (emitted) return Silence(token);
                emitted = true;
                var reply = command == 0xED ? Online() : Battery();
                reply[0] = 1;
                return Task.FromResult(reply);
            }
            return Task.FromResult(command == 0xED ? Online() : Battery());
        }
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(new FakeChannel
        {
            OnlineRead = token => Reply(0xED, token), BatteryRead = token => Reply(0x30, token)
        }) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new MchoseBatterySource(io, clock, TimeSpan.FromMilliseconds(20));
        await Scan(source, store, clock, 1);
        var original = Assert.Single(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Readings);
        clock.Advance(TimeSpan.FromMinutes(2));
        fails = true;
        await Scan(source, store, clock, 2);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(failedCommand == 0xED ? ConnectionState.Pending : ConnectionState.Online, device.Connection);
        Assert.Equal(9, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, device.Freshness);
        Assert.Equal(original.ExpiresAt, Assert.Single(device.Readings).ExpiresAt);
        Assert.Equal(ReadStatus.Timeout, Assert.Single(device.Readings).Attempt);
    }

    [Fact]
    public async Task UnsupportedInterruptOutputNeverFallsBackToControlTransfer()
    {
        var channel = new ControlOnlyChannel();
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock), store, clock, 1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Pending, device.Connection);
        Assert.False(device.Visible);
        Assert.Null(device.BatteryPercent);
        Assert.Contains("NotSupportedException", device.LastError);
        Assert.Equal(0, channel.ControlWrites);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task DelayedBatteryAndFinalBatchCannotAdvanceTheConnectionTimestamp()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeChannel { BatteryRead = _ => { began.TrySetResult(); return release.Task; } };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        store.BeginScan(1, true);
        var scan = new MchoseBatterySource(io, clock).ReadAsync(new(1, true), batch => store.Apply(1, batch, clock.GetUtcNow()), CancellationToken.None);
        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Connection);
            clock.Advance(TimeSpan.FromSeconds(40));
        }
        finally { release.TrySetResult(Battery()); await scan.WaitAsync(TimeSpan.FromSeconds(5)); store.EndScan(1); }
        clock.Advance(TimeSpan.FromSeconds(6));
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Recent, device.Connection);
        Assert.Equal(9, device.BatteryPercent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledWriteOrReadKeepsOwnershipUntilPhysicalOperationAndDisposalEnd(bool blockWrite)
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeChannel
        {
            Write = (_, _) => { if (!blockWrite) return Task.CompletedTask; began.TrySetResult(); return releaseWrite.Task; },
            OnlineRead = _ => { began.TrySetResult(); return releaseRead.Task; }
        };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var source = new MchoseBatterySource(io, attemptTimeout: TimeSpan.FromMilliseconds(20));
        var batches = new ConcurrentBag<SourceBatch>();
        using var cancel = new CancellationTokenSource();
        var scan = source.ReadAsync(new(1, true), batches.Add, cancel.Token);
        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            Assert.False(scan.IsCompleted);
            Assert.False(channel.Disposed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(new(2, true), batches.Add, CancellationToken.None));
        }
        finally { releaseWrite.TrySetResult(); releaseRead.TrySetResult(Online()); await scan.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(channel.Disposed);
        Assert.Empty(batches);
        Assert.Single(channel.InterruptWrites);
    }

    [Fact]
    public async Task IncompleteInventoryKeepsUnseenReceiverAndCompleteRemovalRevokesOnlyThatReceiver()
    {
        var first = Receiver();
        var second = Receiver("second", "22222222-2222-2222-2222-222222222222");
        var io = new FakeIo
        {
            Inventory = new([first, second], true, []),
            Open = (device, _) => Task.FromResult<IHidChannel>(new FakeChannel { BatteryRead = _ => Task.FromResult(Battery(device.Path == first.Path ? (byte)9 : (byte)60)) })
        };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new MchoseBatterySource(io, clock);
        await Scan(source, store, clock, 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        io.Inventory = new([first], false, ["Enumeration access denied."]);
        await Scan(source, store, clock, 2);
        var good = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.BatteryPercent == 9);
        Assert.Null(good.LastError);
        Assert.Equal(BatteryFreshness.Current, good.Freshness);
        var unseen = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.BatteryPercent == 60);
        Assert.Equal(ConnectionState.Online, unseen.Connection);
        Assert.Equal(BatteryFreshness.Retained, unseen.Freshness);
        clock.Advance(TimeSpan.FromSeconds(1));
        io.Inventory = new([first], true, []);
        await Scan(source, store, clock, 3);
        Assert.False(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Id == unseen.Id).Visible);
        Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Id == good.Id).Connection);
    }

    [Fact]
    public async Task FastSuccessIsDeliveredBeforeAnotherReceiverFails()
    {
        var first = Receiver();
        var second = Receiver("second", "22222222-2222-2222-2222-222222222222");
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new FakeIo
        {
            Inventory = new([first, second], true, []),
            Open = (device, _) => Task.FromResult<IHidChannel>(device.Path == first.Path
                ? new FakeChannel { OnlineRead = _ => { blocked.TrySetResult(); return release.Task; } } : new FakeChannel())
        };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        store.BeginScan(1, true);
        var scan = new MchoseBatterySource(io, clock).ReadAsync(new(1, true), batch =>
        {
            store.Apply(1, batch, clock.GetUtcNow());
            if (batch.Observations.Any(x => x.Battery is not null)) published.TrySetResult();
        }, CancellationToken.None);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(scan.IsCompleted);
        }
        finally { release.TrySetException(new IOException("Receiver read failed.")); await scan.WaitAsync(TimeSpan.FromSeconds(5)); store.EndScan(1); }
        var good = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.BatteryPercent == 9);
        Assert.Equal(BatteryFreshness.Current, good.Freshness);
        Assert.Null(good.LastError);
        Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.LastError is not null && x.BatteryPercent is null);
    }

    [Fact]
    public async Task AtkLofreeAndMchoseCoexistAndUseTheirVerifiedOutputTransports()
    {
        var channels = new ConcurrentBag<MixedChannel>();
        var io = new FakeIo
        {
            Inventory = MixedInventory(),
            Open = (device, _) => { var channel = new MixedChannel(device.VendorId); channels.Add(channel); return Task.FromResult<IHidChannel>(channel); }
        };
        await using var session = new BatterySession(new CompositeDeviceSource(new AtkBatterySource(io), new LofreeBatterySource(io), new MchoseBatterySource(io)));
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        AssertMixedDevices(session.Snapshot.State, mchose: 9, atk: 80, lofree: 85);
        Assert.All(session.Snapshot.State.Devices, device => Assert.Equal(ConnectionState.Online, device.Connection));
        foreach (var channel in channels)
        {
            if (channel.VendorId == 0xA8A5) { Assert.NotEmpty(channel.InterruptWrites); Assert.Empty(channel.ControlWrites); }
            else { Assert.NotEmpty(channel.ControlWrites); Assert.Empty(channel.InterruptWrites); }
        }
    }

    [Fact]
    public async Task InterleavedRefreshesKeepBatteryJumpsAndFailuresWithTheirOwnDevice()
    {
        static Dictionary<ushort, TaskCompletionSource<byte[]>> NewReplies() =>
            new ushort[] { 0xA8A5, 0x373B, 0x05AC }.ToDictionary(vendor => vendor,
                _ => new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));

        var replies = NewReplies();
        var io = new FakeIo
        {
            Inventory = MixedInventory(),
            Open = (device, _) =>
            {
                var reply = replies[device.VendorId];
                return Task.FromResult<IHidChannel>(new MixedChannel(device.VendorId)
                    { BatteryRead = token => reply.Task.WaitAsync(token) });
            }
        };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var timeout = TimeSpan.FromSeconds(5);
        await using var session = new BatterySession(new CompositeDeviceSource(
            new AtkBatterySource(io, clock, timeout), new LofreeBatterySource(io, clock, timeout),
            new MchoseBatterySource(io, clock, timeout)), clock,
            SessionOptions.Default with { ScanTimeout = TimeSpan.FromSeconds(30) });

        // Each round uses the real readers and shared session; only raw HID replies are controlled.
        replies[0xA8A5].SetResult(MixedBattery(0xA8A5, 9));
        replies[0x373B].SetResult(MixedBattery(0x373B, 80));
        replies[0x05AC].SetResult(MixedBattery(0x05AC, 85));
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var original = AssertMixedDevices(session.Snapshot.State, mchose: 9, atk: 80, lofree: 85);
        var ids = original.ToDictionary(pair => pair.Key, pair => pair.Value.Id);

        clock.Advance(TimeSpan.FromSeconds(1));
        replies = NewReplies();
        var scan = session.RefreshAsync();
        try
        {
            replies[0xA8A5].SetResult(MixedBattery(0xA8A5, 80));
            await WaitForDeviceAsync(session, "MCHOSE G3 A", device => device.BatteryPercent == 80);
            AssertMixedDevices(session.Snapshot.State, mchose: 80, atk: 80, lofree: 85, ids);
            replies[0x05AC].SetResult(MixedBattery(0x05AC, 84));
            await WaitForDeviceAsync(session, "Lofree Flow Lite100", device => device.BatteryPercent == 84);
            AssertMixedDevices(session.Snapshot.State, mchose: 80, atk: 80, lofree: 84, ids);
            replies[0x373B].SetResult(MixedBattery(0x373B, 79));
            Assert.Equal("Completed", (await scan).Outcome);
        }
        finally
        {
            foreach (var reply in replies.Values) reply.TrySetException(new IOException("Test cleanup."));
            await scan;
        }
        var high = AssertMixedDevices(session.Snapshot.State, mchose: 80, atk: 79, lofree: 84, ids);
        var atkBeforeFailure = Assert.Single(high["ATK receiver"].Readings);

        clock.Advance(TimeSpan.FromSeconds(1));
        replies = NewReplies();
        scan = session.RefreshAsync();
        try
        {
            replies[0x373B].SetException(new IOException("ATK battery read failed."));
            await WaitForDeviceAsync(session, "ATK receiver", device => device.LastError is not null);
            AssertMixedDevices(session.Snapshot.State, mchose: 80, atk: 79, lofree: 84, ids);
            replies[0xA8A5].SetResult(MixedBattery(0xA8A5, 9));
            await WaitForDeviceAsync(session, "MCHOSE G3 A", device => device.BatteryPercent == 9);
            AssertMixedDevices(session.Snapshot.State, mchose: 9, atk: 79, lofree: 84, ids);
            replies[0x05AC].SetResult(MixedBattery(0x05AC, 83));
            Assert.Equal("Partial", (await scan).Outcome);
        }
        finally
        {
            foreach (var reply in replies.Values) reply.TrySetException(new IOException("Test cleanup."));
            await scan;
        }
        var after = AssertMixedDevices(session.Snapshot.State, mchose: 9, atk: 79, lofree: 83, ids);
        Assert.Equal(BatteryFreshness.Retained, after["ATK receiver"].Freshness);
        var retained = Assert.Single(after["ATK receiver"].Readings);
        Assert.Equal(ReadStatus.Error, retained.Attempt);
        Assert.Equal(atkBeforeFailure.ObservedAt, retained.ObservedAt);
        Assert.Equal(atkBeforeFailure.ExpiresAt, retained.ExpiresAt);
        foreach (var name in new[] { "MCHOSE G3 A", "Lofree Flow Lite100" })
        {
            Assert.Equal(BatteryFreshness.Current, after[name].Freshness);
            Assert.Null(after[name].LastError);
            Assert.Equal(ReadStatus.Success, Assert.Single(after[name].Readings).Attempt);
        }
    }

    private static Dictionary<string, DeviceState> AssertMixedDevices(StateSnapshot snapshot, int mchose, int atk,
        int lofree, IReadOnlyDictionary<string, string>? ids = null)
    {
        Assert.Equal(3, snapshot.Devices.Count);
        var devices = snapshot.Devices.ToDictionary(device => device.Name);
        foreach (var (name, percent, source, endpointPrefix) in new[]
        {
            ("MCHOSE G3 A", mchose, MchoseBatterySource.SourceId, "hid:mchose:"),
            ("ATK receiver", atk, AtkBatterySource.SourceId, "hid:atk:"),
            ("Lofree Flow Lite100", lofree, LofreeBatterySource.SourceId, "hid:lofree:")
        })
        {
            Assert.True(devices.TryGetValue(name, out var device), $"Missing device '{name}'.");
            Assert.Equal(percent, device.BatteryPercent);
            Assert.Equal(source, device.BatterySource);
            var reading = Assert.Single(device.Readings);
            Assert.Equal(source, reading.Source);
            Assert.StartsWith(endpointPrefix, reading.EndpointId);
            if (ids is not null) Assert.Equal(ids[name], device.Id);
        }
        Assert.Equal(3, devices.Values.Select(device => device.Id).Distinct().Count());
        return devices;
    }

    private static async Task WaitForDeviceAsync(BatterySession session, string name, Func<DeviceState, bool> accepts)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!session.Snapshot.State.Devices.Any(device => device.Name == name && accepts(device)))
            await Task.Delay(10, deadline.Token);
    }

    private static HidInventory MixedInventory()
    {
        var mchose = Receiver();
        var atk = new HidInterface(@"\\?\hid#vid_373b&pid_11d9&mi_01&col05#7&synthetic&0&0004#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            0x373B, 0x11D9, 0xFF02, 2, 17, 17, 0, "ATK receiver", ContainerId: mchose.ContainerId);
        var lofree = new HidInterface(@"\\?\hid#vid_05ac&pid_024f&mi_01&col05#7&synthetic&0&0004#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            0x05AC, 0x024F, 0xFF02, 2, 17, 17, 0, "2.4G Wireless Receiver", "CX", mchose.ContainerId);
        // Deliberately share a receiver container to exercise cross-brand identity isolation.
        return new([mchose, atk, lofree], true, []);
    }

    private static byte[] MixedBattery(ushort vendor, byte percent)
    {
        if (vendor == 0xA8A5) return Battery(percent);
        var report = Convert.FromHexString(vendor == 0x373B
            ? "080400000002500010E600000000000001" : "080400000084550010C0000000000000A0");
        report[6] = percent;
        report[16] = unchecked((byte)(0x55 - report.Take(16).Sum(value => value)));
        return report;
    }

    private static async Task Scan(MchoseBatterySource source, StateStore store, TimeProvider clock, long generation, bool manual = true)
    {
        store.BeginScan(generation, manual);
        await source.ReadAsync(new(generation, manual), batch => store.Apply(generation, batch, clock.GetUtcNow()), CancellationToken.None);
        store.EndScan(generation);
    }
    private static async Task ScanElapsed(MchoseBatterySource source, StateStore store, TimeProvider clock, long generation, bool manual = false)
    {
        store.BeginScan(generation, manual);
        await source.ReadAsync(new(generation, manual), batch => store.Apply(generation, batch, ClockInstant.Read(clock)), CancellationToken.None);
        store.EndScan(generation);
    }
    private static HidInterface Receiver(string instance = "first", string? container = "11111111-1111-1111-1111-111111111111") =>
        new(UsbPath(instance), 0xA8A5, 0x2255, 0xFF01, 0x10, 65, 65, 0, "MCHOSE G3 A", "YJX-CHIP", container);
    private static string UsbPath(string instance) => $@"\\?\hid#vid_a8a5&pid_2255&mi_02#7&{instance}&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}";
    private static byte[] Online(byte status = 2) { var response = new byte[65]; response[1] = 0xAA; response[2] = 0xED; response[9] = status; return response; }
    private static byte[] Battery(byte percent = 9, byte charge = 0) { var response = new byte[65]; response[1] = 0xAA; response[2] = 0x30; response[9] = percent; response[10] = charge; return response; }
    private static async Task<byte[]> Silence(CancellationToken token) { await Task.Delay(Timeout.Infinite, token); return []; }

    private sealed class FakeIo : IWindowsHidIo
    {
        public HidInventory Inventory { get; set; } = new([Receiver()], true, []);
        public ConcurrentQueue<string> Opened { get; } = new();
        public Func<HidInterface, CancellationToken, Task<IHidChannel>> Open { get; set; } = (_, _) => Task.FromResult<IHidChannel>(new FakeChannel());
        public Task<HidInventory> EnumerateAsync(CancellationToken token) => Task.FromResult(Inventory);
        public Task<IHidChannel> OpenAsync(HidInterface device, CancellationToken token) { Opened.Enqueue(device.Path); return Open(device, token); }
    }

    private sealed class FakeChannel : IHidChannel
    {
        private byte command;
        public bool Disposed { get; private set; }
        public ConcurrentQueue<byte[]> InterruptWrites { get; } = new();
        public ConcurrentQueue<byte[]> ControlWrites { get; } = new();
        public Func<byte[], CancellationToken, Task> Write { get; init; } = (_, _) => Task.CompletedTask;
        public Func<CancellationToken, Task<byte[]>> OnlineRead { get; init; } = _ => Task.FromResult(Online());
        public Func<CancellationToken, Task<byte[]>> BatteryRead { get; init; } = _ => Task.FromResult(Battery());
        public Task WriteOutputReportAsync(byte[] report, CancellationToken token) { ControlWrites.Enqueue(report.ToArray()); throw new InvalidOperationException("Control output is not supported by this test receiver."); }
        public Task WriteInterruptReportAsync(byte[] report, CancellationToken token) { command = report[2]; InterruptWrites.Enqueue(report.ToArray()); return Write(report, token); }
        public Task<byte[]> ReadInputReportAsync(CancellationToken token) => command == 0xED ? OnlineRead(token) : BatteryRead(token);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ControlOnlyChannel : IHidChannel
    {
        public int ControlWrites { get; private set; }
        public bool Disposed { get; private set; }
        public Task WriteOutputReportAsync(byte[] report, CancellationToken token) { ControlWrites++; return Task.CompletedTask; }
        public Task<byte[]> ReadInputReportAsync(CancellationToken token) => Task.FromResult(Online());
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class MixedChannel(ushort vendorId) : IHidChannel
    {
        private byte command;
        public ushort VendorId { get; } = vendorId;
        public Func<CancellationToken, Task<byte[]>>? BatteryRead { get; init; }
        public ConcurrentQueue<byte[]> ControlWrites { get; } = new();
        public ConcurrentQueue<byte[]> InterruptWrites { get; } = new();
        public Task WriteOutputReportAsync(byte[] report, CancellationToken token) { command = report[1]; ControlWrites.Enqueue(report.ToArray()); return Task.CompletedTask; }
        public Task WriteInterruptReportAsync(byte[] report, CancellationToken token) { command = report[2]; InterruptWrites.Enqueue(report.ToArray()); return Task.CompletedTask; }
        public Task<byte[]> ReadInputReportAsync(CancellationToken token)
        {
            if (command == (VendorId == 0xA8A5 ? 0x30 : 4) && BatteryRead is not null) return BatteryRead(token);
            if (VendorId == 0xA8A5) return Task.FromResult(command == 0xED ? Online() : Battery());
            if (VendorId == 0x373B)
                return Task.FromResult(Convert.FromHexString(command == 3 ? "0803000000000100000000000000000000" : "080400000002500010E600000000000001"));
            return Task.FromResult(Convert.FromHexString(command switch
            {
                3 => "080300000081014139140000000000003A", 1 => "080100000088AF45679B010200000000CB", _ => "080400000084550010C0000000000000A0"
            }));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
