using System.Collections.Concurrent;
using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class AtkBatterySourceTests
{
    [Fact]
    public async Task HistoricalResponseFlowsThroughRawChannelToCurrentStateAndUsesVerifiedQuery()
    {
        var clock = new NativeStateIntegrationTests.ManualClock();
        var channel = new FakeChannel { Read = _ => Task.FromResult(Convert.FromHexString("080400000002500010E600000000000001")) };
        var io = new FakeIo { Inventory = new([Receiver()], true, []), Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var store = new StateStore();
        await Scan(new(io, clock), store, clock, 1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(80, device.BatteryPercent);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(BatteryFreshness.Current, device.Freshness);
        var reading = Assert.Single(device.Readings);
        Assert.Equal(clock.GetUtcNow(), reading.ObservedAt);
        Assert.False(reading.Charging);
        Assert.Equal(["080300000000000000000000000000004A", "0804000000000000000000000000000049"],
            channel.Writes.Select(Convert.ToHexString).ToArray());
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task UnknownHardwareIsNotOpenedAndReceiverCollectionsAreReadOnce()
    {
        var receiver = Receiver();
        var io = new FakeIo
        {
            Inventory = new([
                receiver with { Path = UsbPath("unknown-vendor"), VendorId = 0x1234 },
                receiver with { Path = UsbPath("unknown-product"), ProductId = 0xFFFF },
                receiver with { Path = UsbPath("wrong-usage"), Usage = 1 },
                receiver with { Path = UsbPath("short-report"), OutputReportLength = 8 },
                receiver with { Path = UsbPath("feature-sibling", "06"), UsagePage = 0xFF04 },
                receiver, receiver
            ], true, [])
        };
        var store = new StateStore();
        var clock = new NativeStateIntegrationTests.ManualClock();
        await Scan(new(io, clock), store, clock, 1);
        Assert.Equal(receiver.Path, Assert.Single(io.Opened));
        Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
    }

    [Fact]
    public async Task BluetoothHidWithSameVendorProductAndCapabilitiesIsNotOpened()
    {
        var receiver = Receiver();
        var bluetooth = receiver with
        {
            Path = @"\\?\hid#vid_373b&pid_11d9&col05#9&11111111&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            ContainerId = "33333333-3333-3333-3333-333333333333"
        };
        var io = new FakeIo { Inventory = new([bluetooth, receiver], true, []) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock), store, clock, 1);
        Assert.Equal(receiver.Path, Assert.Single(io.Opened));
        Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
    }

    [Fact]
    public async Task InvalidAndUnrelatedReportsAreSkippedAndATimedOutAttemptCanRecover()
    {
        var attempt = 0;
        var replies = new Queue<byte[]>([
            Convert.FromHexString("08040000000050"),
            Convert.FromHexString("0600006464648244"),
            Response(81)
        ]);
        var channel = new FakeChannel
        {
            Write = (report, _) => { if (report[1] == 0x04) Interlocked.Increment(ref attempt); return Task.CompletedTask; },
            Read = async token =>
            {
                if (Volatile.Read(ref attempt) < 3) { await Task.Delay(Timeout.Infinite, token); return []; }
                return replies.Dequeue();
            }
        };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock, TimeSpan.FromMilliseconds(50)), store, clock, 1);
        Assert.Equal(81, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).BatteryPercent);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task NoResponseRetainsBatteryWithoutRenewalAndReceiverPresenceCannotKeepMouseOnline()
    {
        var responds = true;
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(responds ? new FakeChannel() : SilentChannel()) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new AtkBatterySource(io, clock, TimeSpan.FromMilliseconds(20));
        await Scan(source, store, clock, 1);
        var expires = Assert.Single(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Readings).ExpiresAt;
        responds = false;
        clock.Advance(TimeSpan.FromSeconds(30));
        await Scan(source, store, clock, 2);
        var retained = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(80, retained.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, retained.Freshness);
        Assert.Equal(ReadStatus.Timeout, Assert.Single(retained.Readings).Attempt);
        Assert.Equal(expires, Assert.Single(retained.Readings).ExpiresAt);

        clock.Advance(TimeSpan.FromMinutes(2));
        await Scan(source, store, clock, 3);
        Assert.Equal(ConnectionState.Pending, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Connection);
        clock.Advance(TimeSpan.FromMinutes(15));
        await Scan(source, store, clock, 4);
        var expired = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Pending, expired.Connection);
        Assert.Null(expired.BatteryPercent);
        responds = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        await Scan(source, store, clock, 5);
        var recovered = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, recovered.Connection);
        Assert.Equal(80, recovered.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, recovered.Freshness);
    }

    [Theory]
    [InlineData(0, ConnectionState.Offline)]
    [InlineData(2, ConnectionState.Pending)]
    [InlineData(255, ConnectionState.Pending)]
    public async Task OfflineAndUnknownBodyStatusDoNotQueryBattery(byte onlineStatus, ConnectionState expected)
    {
        var channel = new FakeChannel { OnlineRead = _ => Task.FromResult(OnlineResponse(onlineStatus)) };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock), store, clock, 1);
        if (expected == ConnectionState.Offline)
            // No useful battery has ever been read for this confirmed-offline body.
            Assert.Empty(store.Snapshot(clock.GetUtcNow()).Devices);
        else
        {
            var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
            Assert.Equal(expected, device.Connection);
            Assert.Null(device.BatteryPercent);
        }
        Assert.All(channel.Writes, report => Assert.Equal(0x03, report[1]));
    }

    [Fact]
    public async Task ExplicitBodyDisconnectHidesDeviceWhileReceiverRemainsPresent()
    {
        byte onlineStatus = 1;
        var channels = new List<FakeChannel>();
        var io = new FakeIo { Open = (_, _) =>
        {
            var channel = new FakeChannel { OnlineRead = _ => Task.FromResult(OnlineResponse(onlineStatus)) };
            channels.Add(channel);
            return Task.FromResult<IHidChannel>(channel);
        } };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new AtkBatterySource(io, clock);
        await Scan(source, store, clock, 1);
        var originalExpiry = Assert.Single(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Readings).ExpiresAt;
        clock.Advance(TimeSpan.FromSeconds(30));
        onlineStatus = 0;
        await Scan(source, store, clock, 2);
        var offline = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Offline, offline.Connection);
        Assert.False(offline.Visible);
        Assert.Equal(originalExpiry, Assert.Single(offline.Readings).ExpiresAt);
        Assert.All(channels[^1].Writes, report => Assert.Equal(0x03, report[1]));
    }

    [Fact]
    public async Task ConfirmedBodyConnectionSurvivesBatteryFailureWithoutRenewingOldBattery()
    {
        var batteryResponds = true;
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(new FakeChannel
        {
            Read = async token =>
            {
                if (batteryResponds) return Response();
                await Task.Delay(Timeout.Infinite, token);
                return [];
            }
        }) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new AtkBatterySource(io, clock, TimeSpan.FromMilliseconds(20));
        await Scan(source, store, clock, 1);
        var expires = Assert.Single(Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices).Readings).ExpiresAt;
        batteryResponds = false;
        clock.Advance(TimeSpan.FromMinutes(2));
        await Scan(source, store, clock, 2);
        var online = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Online, online.Connection);
        Assert.Equal(80, online.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, online.Freshness);
        Assert.Equal(ReadStatus.Timeout, Assert.Single(online.Readings).Attempt);
        Assert.Equal(expires, Assert.Single(online.Readings).ExpiresAt);
    }

    [Fact]
    public async Task OnlineEvidencePublishesBeforeBatteryAndDelayedBatteryDoesNotRenewConnection()
    {
        var beganBatteryRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBattery = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeChannel { Read = _ =>
        {
            beganBatteryRead.TrySetResult();
            return releaseBattery.Task;
        } };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        store.BeginScan(1, true);
        var scan = new AtkBatterySource(io, clock).ReadAsync(new(1, true),
            batch => store.Apply(1, batch, clock.GetUtcNow()), CancellationToken.None);
        try
        {
            await beganBatteryRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var awaitingBattery = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
            Assert.Equal(ConnectionState.Online, awaitingBattery.Connection);
            Assert.Null(awaitingBattery.BatteryPercent);
            Assert.False(scan.IsCompleted);

            clock.Advance(TimeSpan.FromSeconds(40));
        }
        finally
        {
            releaseBattery.TrySetResult(Response());
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
            store.EndScan(1);
        }

        var batteryReadAt = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(6));
        var later = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Recent, later.Connection);
        Assert.Equal(80, later.BatteryPercent);
        Assert.Equal(batteryReadAt, Assert.Single(later.Readings).ObservedAt);
    }

    [Fact]
    public async Task BatteryReportWithoutOnlineResponseCannotConfirmBodyConnection()
    {
        var suppliedBattery = false;
        var channel = new FakeChannel { OnlineRead = async token =>
        {
            if (!suppliedBattery) { suppliedBattery = true; return Response(); }
            await Task.Delay(Timeout.Infinite, token);
            return [];
        } };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock, TimeSpan.FromMilliseconds(20)), store, clock, 1);
        var device = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices);
        Assert.Equal(ConnectionState.Pending, device.Connection);
        Assert.Null(device.BatteryPercent);
        Assert.All(channel.Writes, report => Assert.Equal(0x03, report[1]));
    }

    [Fact]
    public async Task ReceiverRemovalOnlyRevokesItsOwnEndpointAndReplugRereads()
    {
        var first = Receiver();
        var second = Receiver("second", "22222222-2222-2222-2222-222222222222");
        var io = new FakeIo { Inventory = new([first, second], true, []) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new AtkBatterySource(io, clock);
        await Scan(source, store, clock, 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        io.Inventory = new([second], true, []);
        await Scan(source, store, clock, 2);
        var removed = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Name == first.ProductName);
        Assert.False(removed.Visible);
        Assert.Equal(ConnectionState.Offline, removed.Connection);
        Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Name == second.ProductName).Connection);

        clock.Advance(TimeSpan.FromSeconds(1));
        io.Inventory = new([first, second], true, []);
        io.Open = (device, _) => Task.FromResult<IHidChannel>(new FakeChannel { Read = _ => Task.FromResult(Response(device.Path == first.Path ? (byte)75 : (byte)80)) });
        await Scan(source, store, clock, 3);
        var reconnected = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Name == first.ProductName);
        Assert.True(reconnected.Visible);
        Assert.Equal(75, reconnected.BatteryPercent);
        Assert.Equal(ConnectionState.Online, reconnected.Connection);
    }

    [Fact]
    public async Task ReceiverContainerDoesNotMergeWithBluetoothDeviceAndUnknownContainersDoNotMergeByName()
    {
        var first = Receiver();
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        store.BeginScan(1, true);
        store.Apply(1, new("bluetooth.test", [new("bt-le:112233445566", first.ContainerId, "112233445566", "Keyboard",
            DeviceKind.Keyboard, "bluetooth.test", clock.GetUtcNow(), Connected: true)]), clock.GetUtcNow());
        var io = new FakeIo { Inventory = new([
            first,
            first with { Path = UsbPath("unknown-container-A"), ContainerId = null },
            first with { Path = UsbPath("unknown-container-B"), ContainerId = "00000000-0000-0000-ffff-ffffffffffff" }
        ], true, []) };
        await new AtkBatterySource(io, clock).ReadAsync(new(1, true), batch => store.Apply(1, batch, clock.GetUtcNow()), CancellationToken.None);
        var devices = store.Snapshot(clock.GetUtcNow()).Devices;
        Assert.Equal(4, devices.Count);
        Assert.Single(devices, x => x.Kind == DeviceKind.Keyboard);
        Assert.All(devices.Where(x => x.Kind == DeviceKind.Mouse), x => Assert.Equal(80, x.BatteryPercent));
    }

    [Fact]
    public async Task IncompleteInventoryKeepsUnseenEvidenceAndDoesNotMarkSuccessfulSiblingFailed()
    {
        var first = Receiver();
        var second = Receiver("second", "22222222-2222-2222-2222-222222222222");
        var io = new FakeIo { Inventory = new([first, second], true, []) };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        var source = new AtkBatterySource(io, clock);
        await Scan(source, store, clock, 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        io.Inventory = new([first], false, ["Enumeration failed with Win32 error 5."]);
        await Scan(source, store, clock, 2);
        var good = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Name == first.ProductName);
        Assert.Equal(ReadStatus.Success, Assert.Single(good.Readings).Attempt);
        Assert.Null(good.LastError);
        var unseen = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Name == second.ProductName);
        Assert.Equal(ConnectionState.Online, unseen.Connection);
        Assert.Equal(BatteryFreshness.Retained, unseen.Freshness);
        Assert.Equal(ReadStatus.Error, Assert.Single(unseen.Readings).Attempt);
        Assert.Contains("error 5", unseen.LastError);
    }

    [Fact]
    public async Task OpenFailureIsDeviceScopedAndDoesNotEraseAnotherDevicesSuccess()
    {
        var first = Receiver();
        var second = Receiver("second", "22222222-2222-2222-2222-222222222222");
        var io = new FakeIo
        {
            Inventory = new([first, second], true, []),
            Open = (device, _) => device.Path == first.Path ? throw new UnauthorizedAccessException("Access denied.") : Task.FromResult<IHidChannel>(new FakeChannel())
        };
        var clock = new NativeStateIntegrationTests.ManualClock();
        var store = new StateStore();
        await Scan(new(io, clock), store, clock, 1);
        var unavailable = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Name == first.ProductName);
        Assert.Null(unavailable.BatteryPercent);
        Assert.Equal(ConnectionState.Pending, unavailable.Connection);
        Assert.Contains("Access denied", unavailable.LastError);
        var available = Assert.Single(store.Snapshot(clock.GetUtcNow()).Devices, x => x.Name == second.ProductName);
        Assert.Equal(80, available.BatteryPercent);
        Assert.Null(available.LastError);
    }

    [Fact]
    public async Task FastReceiverPublishesBeforeBlockedReceiverAndCancellationOwnsPhysicalCompletion()
    {
        var first = Receiver();
        var second = Receiver("second", "22222222-2222-2222-2222-222222222222");
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedChannel = new FakeChannel { Read = _ => { began.TrySetResult(); return release.Task; } };
        var io = new FakeIo
        {
            Inventory = new([first, second], true, []),
            Open = (device, _) => Task.FromResult<IHidChannel>(device.Path == first.Path ? blockedChannel : new FakeChannel())
        };
        var source = new AtkBatterySource(io);
        var batches = new ConcurrentBag<SourceBatch>();
        using var cancel = new CancellationTokenSource();
        var scan = source.ReadAsync(new(1, true), batch =>
        {
            batches.Add(batch);
            if (batch.Observations.Any(x => x.Name == second.ProductName && x.Battery is not null)) fastPublished.TrySetResult();
        }, cancel.Token);
        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fastPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            Assert.False(scan.IsCompleted);
            Assert.False(blockedChannel.Disposed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(new(2, true), _ => { }, CancellationToken.None));
        }
        finally { release.TrySetResult(Response()); await scan.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(blockedChannel.Disposed);
        Assert.DoesNotContain(batches.SelectMany(x => x.Observations), x => x.Name == first.ProductName && x.Battery is not null);
    }

    [Fact]
    public async Task SessionTimeoutRejectsOverlappingScanUntilHidOperationPhysicallyEnds()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeChannel { Read = _ => { began.TrySetResult(); return release.Task; } };
        var io = new FakeIo { Open = (_, _) => Task.FromResult<IHidChannel>(channel) };
        await using var session = new BatterySession(new AtkBatterySource(io), options:
            new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2)));
        var scan = session.RefreshAsync();
        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("TimedOut", (await scan.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
            Assert.Equal("Busy", (await session.RefreshAsync()).Outcome);
            Assert.False(channel.Disposed);
        }
        finally { release.TrySetResult(Response()); }
        var stop = await session.StopAsync();
        Assert.True(stop.Drained);
        Assert.True(channel.Disposed);
        Assert.DoesNotContain(session.Snapshot.State.Devices, x => x.BatteryPercent is not null);
    }

    private static async Task Scan(AtkBatterySource source, StateStore store, TimeProvider clock, long generation)
    {
        store.BeginScan(generation, true);
        await source.ReadAsync(new(generation, true), batch => store.Apply(generation, batch, clock.GetUtcNow()), CancellationToken.None);
        store.EndScan(generation);
    }

    private static HidInterface Receiver(string path = "receiver-A", string? container = "11111111-1111-1111-1111-111111111111") =>
        new(UsbPath(path), 0x373B, 0x11D9, 0xFF02, 2, 17, 17, 0, ProductName: "ATK " + path, ContainerId: container);

    private static string UsbPath(string instance, string collection = "05") =>
        $@"\\?\hid#vid_373b&pid_11d9&mi_01&col{collection}#7&{instance}&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}";

    private static byte[] Response(byte percent = 80)
    {
        var report = Convert.FromHexString("080400000002500010E600000000000001");
        report[6] = percent;
        return report;
    }

    private static byte[] OnlineResponse(byte status = 1)
    {
        var report = new byte[17];
        report[0] = 0x08;
        report[1] = 0x03;
        report[6] = status;
        return report;
    }

    private static FakeChannel SilentChannel() => new()
    {
        OnlineRead = async token => { await Task.Delay(Timeout.Infinite, token); return []; },
        Read = async token => { await Task.Delay(Timeout.Infinite, token); return []; }
    };

    private sealed class FakeIo : IWindowsHidIo
    {
        public HidInventory Inventory { get; set; } = new([Receiver()], true, []);
        public Func<HidInterface, CancellationToken, Task<IHidChannel>> Open { get; set; } = (_, _) => Task.FromResult<IHidChannel>(new FakeChannel());
        public ConcurrentQueue<string> Opened { get; } = new();
        public Task<HidInventory> EnumerateAsync(CancellationToken cancellationToken) => Task.FromResult(Inventory);
        public Task<IHidChannel> OpenAsync(HidInterface device, CancellationToken cancellationToken)
        {
            Opened.Enqueue(device.Path);
            return Open(device, cancellationToken);
        }
    }

    private sealed class FakeChannel : IHidChannel
    {
        public ConcurrentQueue<byte[]> Writes { get; } = new();
        public bool Disposed { get; private set; }
        private byte lastCommand;
        public Func<byte[], CancellationToken, Task> Write { get; init; } = (_, _) => Task.CompletedTask;
        public Func<CancellationToken, Task<byte[]>> Read { get; init; } = _ => Task.FromResult(Response());
        public Func<CancellationToken, Task<byte[]>> OnlineRead { get; init; } = _ => Task.FromResult(OnlineResponse());
        public Task WriteOutputReportAsync(byte[] report, CancellationToken cancellationToken)
        {
            Writes.Enqueue(report.ToArray());
            lastCommand = report[1];
            return Write(report, cancellationToken);
        }
        public Task<byte[]> ReadInputReportAsync(CancellationToken cancellationToken) =>
            lastCommand == 0x03 ? OnlineRead(cancellationToken) : Read(cancellationToken);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
