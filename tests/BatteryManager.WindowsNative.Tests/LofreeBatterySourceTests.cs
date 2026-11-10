using System.Collections.Concurrent;
using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class LofreeBatterySourceTests
{
    [Fact]
    public async Task RawCapturedReportsThroughSourceAndStateProduceVerifiedKeyboard()
    {
        var channel = new FakeChannel();
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock), store, clock, 1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal("Lofree Flow Lite100", device.Name);
        Assert.Equal(DeviceKind.Keyboard, device.Kind);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(85, device.BatteryPercent);
        Assert.Equal(DeviceTransport.Receiver, DeviceDisplayMapper.Map(device)!.Transport);
        Assert.Equal(BatteryFreshness.Current, device.Freshness);
        Assert.False(Assert.Single(device.Readings).Charging);
        Assert.Equal(new byte[] { 3, 1, 4 }, channel.Writes.Select(x => x[1]));
        var identityRequest = channel.Writes.Single(x => x[1] == 1);
        Assert.Equal(0x88, identityRequest[5]);
        Assert.All(identityRequest[10..14], value => Assert.Equal(0, value));
        Assert.Equal(0x55, identityRequest.Sum(x => x) & 0xFF);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task SameUsbIdsOnBluetoothAndUnverifiedManufacturerAreNeverOpened()
    {
        var receiver = Receiver();
        var io = new FakeIo { Inventory = new([
            receiver with { Path = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&0205ac_pid&024f_rev&0100_112233445566&col02#9&test&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}" },
            receiver with { Path = UsbPath("other-maker"), Manufacturer = "Other" },
            receiver with { Path = UsbPath("missing-maker"), Manufacturer = null },
            receiver with { Path = UsbPath("other-product"), ProductId = 0x0250 },
            receiver with { Path = UsbPath("other-usage"), UsagePage = 0xFF04 },
            receiver with { Path = UsbPath("short"), InputReportLength = 8 },
            receiver, receiver
        ], true, []) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock), store, clock, 1);
        Assert.Equal(receiver.Path, Assert.Single(io.Opened));
        Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(1, 2, 2)]
    [InlineData(87, 4, 0)]
    public async Task FirstUnknownModelDoesNotCreateDeviceOrSendBatteryQuery(byte cid, byte mid, byte type)
    {
        var channel = new FakeChannel { IdentityRead = _ => Task.FromResult(Identity(cid, mid, type)) };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock), store, clock, 1);
        Assert.Empty(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.DoesNotContain(channel.Writes, x => x[1] == 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(255)]
    public async Task FirstNonconfirmingReceiverDoesNotRunIdentityOrBattery(byte online)
    {
        var channel = new FakeChannel { OnlineRead = _ => Task.FromResult(Online(online)) };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock), store, clock, 1);
        Assert.Empty(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.All(channel.Writes, x => Assert.Equal(3, x[1]));
    }

    [Fact]
    public async Task KnownIdleReceiverAgesToPendingAndOldBatteryExpiresWithoutFalseDisconnect()
    {
        byte online = 1;
        FakeChannel? latest = null;
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(latest = new() { OnlineRead = _ => Task.FromResult(Online(online)) }) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new LofreeBatterySource(io, clock);
        await Scan(source, store, clock, 1);
        var original = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        var expires = Assert.Single(original.Readings).ExpiresAt;
        online = 0;
        for (var elapsedSeconds = 30; elapsedSeconds <= 900; elapsedSeconds += 30)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Scan(source, store, clock, elapsedSeconds / 30 + 1, manual: false);
            var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
            Assert.True(device.Visible);
            Assert.Equal(elapsedSeconds < 45 ? ConnectionState.Online : elapsedSeconds < 105 ? ConnectionState.Recent : ConnectionState.Pending,
                device.Connection);
            Assert.Equal(expires, Assert.Single(device.Readings).ExpiresAt);
            Assert.Equal(ReadStatus.Missing, Assert.Single(device.Readings).Attempt);
            Assert.Null(device.LastError);
            Assert.Null(Assert.Single(device.Readings).Error);
            Assert.Equal(elapsedSeconds < 900 ? 85 : (int?)null, device.BatteryPercent);
            Assert.Equal(elapsedSeconds < 900 ? BatteryFreshness.Retained : BatteryFreshness.Expired, device.Freshness);
            Assert.All(latest!.Writes, x => Assert.Equal(3, x[1]));
        }
    }

    [Fact]
    public async Task IdleIsNotAScanFailureButRealIoErrorsRemainVisibleAndWakeRecovers()
    {
        byte online = 1;
        byte percent = 85;
        var fail = false;
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(new FakeChannel
        {
            OnlineRead = _ => fail ? Task.FromException<byte[]>(new IOException("Receiver I/O failed.")) : Task.FromResult(Online(online)),
            BatteryRead = _ => Task.FromResult(Battery(percent))
        }) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        await using var session = new BatterySession(new LofreeBatterySource(io, clock), clock);
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var original = Assert.Single(session.Snapshot.State.Devices);
        var expiry = Assert.Single(original.Readings).ExpiresAt;

        clock.Advance(TimeSpan.FromSeconds(30));
        online = 0;
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var idle = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(85, idle.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, idle.Freshness);
        Assert.Equal(expiry, Assert.Single(idle.Readings).ExpiresAt);
        Assert.Null(idle.LastError);

        fail = true;
        Assert.Equal("Partial", (await session.RefreshAsync()).Outcome);
        var failed = Assert.Single(session.Snapshot.State.Devices);
        Assert.Contains("Receiver I/O failed", failed.LastError);
        Assert.Equal(85, failed.BatteryPercent);
        Assert.Equal(expiry, Assert.Single(failed.Readings).ExpiresAt);

        fail = false;
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var expired = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(ConnectionState.Pending, expired.Connection);
        Assert.Null(expired.BatteryPercent);
        Assert.Equal(BatteryFreshness.Expired, expired.Freshness);
        Assert.Null(expired.LastError);

        online = 1;
        percent = 84;
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var recovered = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(original.Id, recovered.Id);
        Assert.Equal(84, recovered.BatteryPercent);
        Assert.Equal(ConnectionState.Online, recovered.Connection);
        Assert.Equal(BatteryFreshness.Current, recovered.Freshness);
        Assert.Null(recovered.LastError);
        Assert.True(Assert.Single(recovered.Readings).ExpiresAt > expiry);
    }

    [Fact]
    public async Task ChangedValidIdentityRevokesOldAssociationAndBatteryIsNotRead()
    {
        byte mid = 2;
        byte online = 1;
        FakeChannel? latest = null;
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(latest = new()
        {
            OnlineRead = _ => Task.FromResult(Online(online)), IdentityRead = _ => Task.FromResult(Identity(mid: mid))
        }) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new LofreeBatterySource(io, clock);
        await Scan(source, store, clock, 1);
        clock.Advance(TimeSpan.FromSeconds(30));
        mid = 1;
        await Scan(source, store, clock, 2, manual: false);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.False(device.Visible);
        Assert.Equal(ConnectionState.Offline, device.Connection);
        Assert.DoesNotContain(latest!.Writes, x => x[1] == 4);
        clock.Advance(TimeSpan.FromSeconds(1));
        online = 0;
        await Scan(source, store, clock, 3);
        Assert.Equal(ConnectionState.Offline, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Connection);
        Assert.All(latest!.Writes, x => Assert.Equal(3, x[1]));
        clock.Advance(TimeSpan.FromSeconds(1));
        online = 1;
        mid = 2;
        await Scan(source, store, clock, 4);
        Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Connection);
        Assert.Contains(latest!.Writes, x => x[1] == 4);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task ConnectionOrIdentityTimeoutRetainsOldValueWithoutRenewal(byte failedCommand)
    {
        var responds = true;
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(new FakeChannel
        {
            OnlineRead = token => responds || failedCommand != 3 ? Task.FromResult(Online()) : Silence(token),
            IdentityRead = token => responds || failedCommand != 1 ? Task.FromResult(Identity()) : Silence(token)
        }) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new LofreeBatterySource(io, clock, TimeSpan.FromMilliseconds(20));
        await Scan(source, store, clock, 1);
        var expires = Assert.Single(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Readings).ExpiresAt;
        responds = false;
        clock.Advance(TimeSpan.FromMinutes(2));
        await Scan(source, store, clock, 2);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Pending, device.Connection);
        Assert.Equal(85, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, device.Freshness);
        Assert.Equal(expires, Assert.Single(device.Readings).ExpiresAt);
        Assert.Equal(ReadStatus.Timeout, Assert.Single(device.Readings).Attempt);
        clock.Advance(TimeSpan.FromMinutes(15));
        await Scan(source, store, clock, 3);
        Assert.Null(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).BatteryPercent);
    }

    [Fact]
    public async Task FirstIdentityTimeoutProducesDiagnosticWithoutVisibleKeyboard()
    {
        var channel = new FakeChannel { IdentityRead = Silence };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        await using var session = new BatterySession(new LofreeBatterySource(io, attemptTimeout: TimeSpan.FromMilliseconds(20)));
        Assert.Equal("Partial", (await session.RefreshAsync()).Outcome);
        Assert.Empty(session.Snapshot.State.Devices);
        Assert.DoesNotContain(channel.Writes, x => x[1] == 4);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task IncompleteDeclaredDataCannotDisconnectChangeIdentityOrInventZeroBattery(byte malformedCommand)
    {
        var malformed = false;
        var servedMalformed = false;
        Task<byte[]> Reply(byte command, CancellationToken token)
        {
            if (malformed && command == malformedCommand)
            {
                if (servedMalformed) return Silence(token);
                servedMalformed = true;
                var incomplete = new byte[17];
                incomplete[0] = 8;
                incomplete[1] = command;
                incomplete[5] = 0x80; // A complete transport frame with no declared data.
                return Task.FromResult(incomplete);
            }
            return Task.FromResult(command switch { 3 => Online(), 1 => Identity(), _ => Battery() });
        }
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(new FakeChannel
        {
            OnlineRead = token => Reply(3, token), IdentityRead = token => Reply(1, token), BatteryRead = token => Reply(4, token)
        }) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new LofreeBatterySource(io, clock, TimeSpan.FromMilliseconds(20));
        await Scan(source, store, clock, 1);
        var expires = Assert.Single(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Readings).ExpiresAt;
        malformed = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        await Scan(source, store, clock, 2);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(85, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, device.Freshness);
        Assert.Equal(expires, Assert.Single(device.Readings).ExpiresAt);
        Assert.Equal(ReadStatus.Timeout, Assert.Single(device.Readings).Attempt);
    }

    [Fact]
    public async Task BatteryFailurePreservesSeparatelyVerifiedOnlineStateAndOldExpiry()
    {
        var responds = true;
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(new FakeChannel { BatteryRead = token =>
            responds ? Task.FromResult(Battery()) : Silence(token) }) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new LofreeBatterySource(io, clock, TimeSpan.FromMilliseconds(20));
        await Scan(source, store, clock, 1);
        var expires = Assert.Single(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Readings).ExpiresAt;
        responds = false;
        clock.Advance(TimeSpan.FromMinutes(2));
        await Scan(source, store, clock, 2);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(85, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, device.Freshness);
        Assert.Equal(expires, Assert.Single(device.Readings).ExpiresAt);
    }

    [Fact]
    public async Task IdentityAndBatteryDelaysDoNotAdvanceOriginalConnectionTime()
    {
        var identityBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var identityRelease = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var batteryBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batteryRelease = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeChannel
        {
            IdentityRead = _ => { identityBegan.TrySetResult(); return identityRelease.Task; },
            BatteryRead = _ => { batteryBegan.TrySetResult(); return batteryRelease.Task; }
        };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        store.BeginScan(1, true);
        var scan = new LofreeBatterySource(io, clock).ReadAsync(new(1, true), batch => store.Apply(1, batch, clock.GetUtcNow()), CancellationToken.None);
        try
        {
            await identityBegan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(store.Snapshot(clock.GetUtcNow()).Devices);
            clock.Advance(TimeSpan.FromSeconds(10));
            identityRelease.TrySetResult(Identity());
            await batteryBegan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Connection);
            clock.Advance(TimeSpan.FromSeconds(30));
        }
        finally
        {
            identityRelease.TrySetResult(Identity());
            batteryRelease.TrySetResult(Battery());
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
            store.EndScan(1);
        }
        clock.Advance(TimeSpan.FromSeconds(6));
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Recent, device.Connection);
        Assert.Equal(85, device.BatteryPercent);
    }

    [Fact]
    public async Task InvalidRepliesAreFilteredAndAQueryCanRecoverOnItsSecondAttempt()
    {
        var batteryAttempts = 0;
        var replies = new Queue<byte[]>([
            Convert.FromHexString("080400000004550010C0000000000000A0"),
            Convert.FromHexString("080401000084550010C0000000000000A0"),
            Convert.FromHexString("08040000008455"), Battery(84)
        ]);
        var channel = new FakeChannel
        {
            Write = (report, _) => { if (report[1] == 4) Interlocked.Increment(ref batteryAttempts); return Task.CompletedTask; },
            BatteryRead = token => batteryAttempts == 1 ? Silence(token) : Task.FromResult(replies.Dequeue())
        };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock, TimeSpan.FromMilliseconds(30)), store, clock, 1);
        Assert.Equal(84, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).BatteryPercent);
    }

    [Fact]
    public async Task CancellationKeepsPhysicalOwnershipAndDoesNotAcceptLateIdentity()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeChannel { IdentityRead = _ => { began.TrySetResult(); return release.Task; } };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var source = new LofreeBatterySource(io);
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
        finally { release.TrySetResult(Identity()); await scan.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(channel.Disposed);
        Assert.Empty(batches.SelectMany(x => x.Observations));
        Assert.DoesNotContain(channel.Writes, x => x[1] == 4);
    }

    [Fact]
    public async Task ReceiverRemovalIsLocalAndReconnectRereadsIdentityAndBattery()
    {
        var first = Receiver();
        var second = Receiver("second", "22222222-2222-2222-2222-222222222222");
        var io = new FakeIo
        {
            Inventory = new([first, second], true, []),
            Open = (device, _) => Task.FromResult<IHidChannel>(new FakeChannel { BatteryRead = _ => Task.FromResult(Battery(device.Path == first.Path ? (byte)85 : (byte)60)) })
        };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new LofreeBatterySource(io, clock);
        await Scan(source, store, clock, 1);
        var firstId = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.BatteryPercent == 85).Id;
        clock.Advance(TimeSpan.FromSeconds(1));
        io.Inventory = new([second], true, []);
        await Scan(source, store, clock, 2);
        Assert.False(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Id == firstId).Visible);
        Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Id != firstId).Connection);
        clock.Advance(TimeSpan.FromSeconds(1));
        io.Inventory = new([first, second], true, []);
        await Scan(source, store, clock, 3);
        Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Id == firstId).Connection);
    }

    [Fact]
    public async Task IdleExpiredKeyboardSurvivesUntilReceiverRemovalAndReconnectCannotReuseItsExpiredBattery()
    {
        byte online = 1;
        byte percent = 85;
        var failBattery = false;
        var channels = new ConcurrentBag<FakeChannel>();
        var io = new FakeIo { Open = (_, _) =>
        {
            var channel = new FakeChannel
            {
                OnlineRead = _ => Task.FromResult(Online(online)),
                BatteryRead = _ => failBattery ? Task.FromException<byte[]>(new IOException("Battery read failed")) : Task.FromResult(Battery(percent))
            };
            channels.Add(channel);
            return Task.FromResult<IHidChannel>(channel);
        } };
        var clock = new NativeStateIntegrationTests.ManualClock();
        await using var session = new BatterySession(new LofreeBatterySource(io, clock), clock);
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        var first = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(85, first.BatteryPercent);

        clock.Advance(TimeSpan.FromMinutes(16));
        online = 0;
        await session.RefreshAsync();
        var idle = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(first.Id, idle.Id);
        Assert.Equal(ConnectionState.Pending, idle.Connection);
        Assert.True(idle.Visible);
        Assert.Null(idle.BatteryPercent);
        Assert.Equal(BatteryFreshness.Expired, idle.Freshness);

        io.Inventory = new([], true, []);
        await session.RefreshAsync();
        Assert.Empty(session.Snapshot.State.Devices);

        io.Inventory = new([Receiver()], true, []);
        online = 1;
        failBattery = true;
        Assert.Equal("Partial", (await session.RefreshAsync()).Outcome);
        var returned = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(first.Id, returned.Id);
        Assert.Equal(ConnectionState.Online, returned.Connection);
        Assert.Null(returned.BatteryPercent);
        Assert.Empty(returned.Readings);

        failBattery = false;
        percent = 60;
        Assert.Equal("Completed", (await session.RefreshAsync()).Outcome);
        Assert.Equal(60, Assert.Single(session.Snapshot.State.Devices).BatteryPercent);
        Assert.All(channels, channel => Assert.True(channel.Disposed));
    }

    [Fact]
    public async Task ReceiverContainerCannotMergeLofreeWithBluetoothDevice()
    {
        var receiver = Receiver();
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        store.BeginScan(1, true);
        store.Apply(1, new("bluetooth.test", [new("bt-le:112233445566", receiver.ContainerId, "112233445566", "Bluetooth keyboard",
            DeviceKind.Keyboard, "bluetooth.test", clock.GetUtcNow(), Connected: true)]), clock.GetUtcNow());
        await new LofreeBatterySource(new FakeIo(), clock).ReadAsync(new(1, true), batch => store.Apply(1, batch, clock.GetUtcNow()), CancellationToken.None);
        Assert.Equal(2, store.Snapshot(clock.GetUtcNow()).Devices.Count);
    }

    [Fact]
    public async Task IncompleteInventoryDoesNotRevokeUnseenReceiverOrFailSuccessfulSibling()
    {
        var first = Receiver();
        var second = Receiver("second", "22222222-2222-2222-2222-222222222222");
        var io = new FakeIo
        {
            Inventory = new([first, second], true, []),
            Open = (device, _) => Task.FromResult<IHidChannel>(new FakeChannel { BatteryRead = _ => Task.FromResult(Battery(device.Path == first.Path ? (byte)85 : (byte)60)) })
        };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new LofreeBatterySource(io, clock);
        await Scan(source, store, clock, 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        io.Inventory = new([first], false, ["HID inventory incomplete: access denied."]);
        await Scan(source, store, clock, 2);
        var good = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.BatteryPercent == 85);
        Assert.Equal(BatteryFreshness.Current, good.Freshness);
        Assert.Null(good.LastError);
        var unseen = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.BatteryPercent == 60);
        Assert.Equal(ConnectionState.Online, unseen.Connection);
        Assert.Equal(BatteryFreshness.Retained, unseen.Freshness);
        Assert.Contains("access denied", unseen.LastError);
    }

    [Fact]
    public async Task DiscoveryErrorDoesNotInvalidateAnotherVerifiedReceiversBattery()
    {
        var first = Receiver();
        var second = Receiver("second", "22222222-2222-2222-2222-222222222222");
        var io = new FakeIo
        {
            Inventory = new([first, second], true, []),
            Open = (device, _) => Task.FromResult<IHidChannel>(device.Path == first.Path
                ? new FakeChannel { IdentityRead = _ => throw new IOException("Identity channel failed.") }
                : new FakeChannel())
        };
        await using var session = new BatterySession(new LofreeBatterySource(io));
        Assert.Equal("Partial", (await session.RefreshAsync()).Outcome);
        var good = Assert.Single(session.Snapshot.State.Devices);
        Assert.Equal(85, good.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, good.Freshness);
        Assert.Null(good.LastError);
    }

    [Fact]
    public async Task FastVerifiedReceiverPublishesWhileAnotherIdentityReadIsBlocked()
    {
        var first = Receiver();
        var second = Receiver("second", "22222222-2222-2222-2222-222222222222");
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new FakeIo
        {
            Inventory = new([first, second], true, []),
            Open = (device, _) => Task.FromResult<IHidChannel>(device.Path == first.Path
                ? new FakeChannel { IdentityRead = _ => { blocked.TrySetResult(); return release.Task; } }
                : new FakeChannel())
        };
        var batches = new ConcurrentBag<SourceBatch>();
        using var cancel = new CancellationTokenSource();
        var scan = new LofreeBatterySource(io).ReadAsync(new(1, true), batch =>
        {
            batches.Add(batch);
            if (batch.Observations.Any(x => x.Battery is not null)) fast.TrySetResult();
        }, cancel.Token);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fast.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(scan.IsCompleted);
        }
        finally { cancel.Cancel(); release.TrySetResult(Identity()); await scan.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Single(batches.SelectMany(x => x.Observations), x => x.Battery is not null);
    }

    private static async Task Scan(LofreeBatterySource source, StateStore store, TimeProvider clock, long generation, bool manual = true)
    {
        store.BeginScan(generation, manual);
        await source.ReadAsync(new(generation, manual), batch => store.Apply(generation, batch, clock.GetUtcNow()), CancellationToken.None);
        store.EndScan(generation);
    }

    private static HidInterface Receiver(string instance = "first", string? container = "11111111-1111-1111-1111-111111111111") =>
        new(UsbPath(instance), 0x05AC, 0x024F, 0xFF02, 2, 17, 17, 0, "2.4G Wireless Receiver", "CX", container);

    private static string UsbPath(string instance) => $@"\\?\hid#vid_05ac&pid_024f&mi_01&col05#7&{instance}&0&0004#{{4d1e55b2-f16f-11cf-88cb-001111000030}}";
    private static byte[] Online(byte online = 1)
    {
        var reply = Convert.FromHexString("080300000081014139140000000000003A");
        reply[6] = online;
        return reply;
    }
    private static byte[] Identity(byte cid = 1, byte mid = 2, byte type = 0)
    {
        var reply = Convert.FromHexString("080100000088AF45679B010200000000CB");
        reply[10] = cid; reply[11] = mid; reply[12] = type;
        return reply;
    }
    private static byte[] Battery(byte percent = 85)
    {
        var reply = Convert.FromHexString("080400000084550010C0000000000000A0");
        reply[6] = percent;
        return reply;
    }
    private static async Task<byte[]> Silence(CancellationToken token) { await Task.Delay(Timeout.Infinite, token); return []; }

    private sealed class FakeIo : IWindowsHidIo
    {
        public HidInventory Inventory { get; set; } = new([Receiver()], true, []);
        public ConcurrentQueue<string> Opened { get; } = new();
        public Func<HidInterface, CancellationToken, Task<IHidChannel>> Open { get; set; } = (_, _) => Task.FromResult<IHidChannel>(new FakeChannel());
        public Task<HidInventory> EnumerateAsync(CancellationToken cancellationToken) => Task.FromResult(Inventory);
        public Task<IHidChannel> OpenAsync(HidInterface device, CancellationToken cancellationToken) { Opened.Enqueue(device.Path); return Open(device, cancellationToken); }
    }

    private sealed class FakeChannel : IHidChannel
    {
        private byte command;
        public bool Disposed { get; private set; }
        public ConcurrentQueue<byte[]> Writes { get; } = new();
        public Func<byte[], CancellationToken, Task> Write { get; init; } = (_, _) => Task.CompletedTask;
        public Func<CancellationToken, Task<byte[]>> OnlineRead { get; init; } = _ => Task.FromResult(Online());
        public Func<CancellationToken, Task<byte[]>> IdentityRead { get; init; } = _ => Task.FromResult(Identity());
        public Func<CancellationToken, Task<byte[]>> BatteryRead { get; init; } = _ => Task.FromResult(Battery());
        public Task WriteOutputReportAsync(byte[] report, CancellationToken token) { command = report[1]; Writes.Enqueue(report.ToArray()); return Write(report, token); }
        public Task<byte[]> ReadInputReportAsync(CancellationToken token) => command switch
        {
            3 => OnlineRead(token), 1 => IdentityRead(token), 4 => BatteryRead(token), _ => throw new InvalidOperationException("Unexpected command.")
        };
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
