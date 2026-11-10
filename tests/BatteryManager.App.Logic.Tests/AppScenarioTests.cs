using BatteryManager.App;
using BatteryManager.Core;
using Xunit;

namespace BatteryManager.App.Logic.Tests;

public sealed class AppScenarioTests
{
    [Theory]
    [InlineData("overview", BatteryTone.Normal, 73, DisplayPresence.Online, false)]
    [InlineData("retained", BatteryTone.Stale, 73, DisplayPresence.Online, false)]
    [InlineData("pending", BatteryTone.Stale, 73, DisplayPresence.NoResponse, false)]
    [InlineData("unknown", BatteryTone.None, null, DisplayPresence.Online, false)]
    [InlineData("error", BatteryTone.Stale, 73, DisplayPresence.Online, true)]
    [InlineData("expired", BatteryTone.None, null, DisplayPresence.Online, false)]
    [InlineData("offline", BatteryTone.None, null, DisplayPresence.Online, false)]
    [InlineData("recovered", BatteryTone.Normal, 79, DisplayPresence.Online, false)]
    public async Task Shared_display_uses_real_reader_session_and_state_scenarios(string name, BatteryTone tone,
        int? percentage, DisplayPresence presence, bool failed)
    {
        var scenario = new AppScenarios();
        await using var session = new BatterySession(scenario.CreateSource(), scenario.Clock);
        await scenario.ApplyAsync(name, session);
        var headphones = Assert.Single(session.Snapshot.State.Devices, device => device.Name == "演示耳机");
        var display = DeviceDisplayMapper.Map(headphones);
        if (name == "offline") Assert.Null(display);
        else
        {
            Assert.NotNull(display);
            Assert.Equal(tone, display.Battery.Tone);
            Assert.Equal(percentage, display.Battery.Value);
            Assert.Equal(presence, display.Presence);
            Assert.Equal(failed, display.ReadFailed);
            Assert.Equal(DeviceTransport.Bluetooth, display.Transport);
            Assert.Equal(percentage is null ? null : "windows", display.Source);
            Assert.Empty(display.Parts);
            var mouse = DeviceDisplayMapper.Map(Assert.Single(session.Snapshot.State.Devices, device => device.Kind == DeviceKind.Mouse))!;
            Assert.Equal(DeviceTransport.Receiver, mouse.Transport);
            Assert.True(mouse.Battery.Charging);
        }
        AssertChannelsDrained(scenario);
    }

    [Fact]
    public async Task OverviewUsesRealReadersForWindowsBatteryRawHidAndUnknownBattery()
    {
        var scenario = new AppScenarios();
        await using var session = new BatterySession(scenario.CreateSource(), scenario.Clock);
        await scenario.ApplyAsync("overview", session);
        var devices = session.Snapshot.State.Devices;
        Assert.Equal(3, devices.Count);
        var windows = Assert.Single(devices, x => x.Name == "演示耳机");
        Assert.Equal(73, windows.BatteryPercent);
        Assert.Equal("windows.pnp", windows.BatterySource);
        Assert.Equal(BatteryEvidence.SystemCache, Assert.Single(windows.Readings).Evidence);
        var hid = Assert.Single(devices, x => x.Kind == DeviceKind.Mouse);
        Assert.Equal(42, hid.BatteryPercent);
        Assert.Equal(BatteryFreshness.Current, hid.Freshness);
        Assert.Equal("hid.atk.compx", hid.BatterySource);
        Assert.True(Assert.Single(hid.Readings).Charging);
        var unknown = Assert.Single(devices, x => x.Kind == DeviceKind.Keyboard);
        Assert.True(unknown.Visible);
        Assert.Null(unknown.BatteryPercent);
        Assert.Equal(BatteryFreshness.Unknown, unknown.Freshness);
        AssertChannelsDrained(scenario);
    }

    [Theory]
    [InlineData("panel-one", 1)]
    [InlineData("panel-two", 2)]
    [InlineData("panel-three", 3)]
    [InlineData("panel-many", 5)]
    public async Task PanelSizeScenariosStartAndRefreshWithOnlyFictionalDevices(string name, int count)
    {
        Assert.Equal(name, AppScenarios.NameFromArguments(["--scenario", name]));
        var scenario = new AppScenarios();
        await using var session = new BatterySession(scenario.CreateSource(), scenario.Clock);
        await scenario.ApplyAsync(name, session);
        AssertPanelDevices(session, count);
        var originalIds = session.Snapshot.State.Devices.Select(device => device.Id).Order().ToArray();

        await session.RefreshAsync();
        AssertPanelDevices(session, count);
        Assert.Equal(originalIds, session.Snapshot.State.Devices.Select(device => device.Id).Order().ToArray());
        var state = scenario.Capture();
        Assert.Equal(name, state.Name);
        Assert.False(state.Applying);
        Assert.False(state.UsesRealHardware);
        Assert.False(state.SavesPreferences);
        Assert.Equal(state.OpenedHidChannels, state.ClosedHidChannels);
        Assert.Equal(count >= 3, state.OpenedHidChannels > 0);
    }

    [Fact]
    public async Task PanelSizeTransitionsRemovePreviousRecordsAndRestoreTheOriginalOverview()
    {
        var scenario = new AppScenarios();
        await using var session = new BatterySession(scenario.CreateSource(), scenario.Clock);
        var sequence = new[]
        {
            ("panel-many", 5), ("panel-one", 1), ("panel-two", 2),
            ("panel-three", 3), ("panel-many", 5), ("overview", 3)
        };
        for (var repeat = 0; repeat < 2; repeat++)
        {
            foreach (var (name, count) in sequence)
            {
                await scenario.ApplyAsync(name, session);
                // Total state count also excludes hidden records left by the previous scenario.
                AssertPanelDevices(session, count);
                Assert.Equal(name, scenario.Capture().Name);
                Assert.False(scenario.Capture().Applying);
                AssertChannelsDrained(scenario);
            }
        }
    }

    private static void AssertPanelDevices(BatterySession session, int count)
    {
        var devices = session.Snapshot.State.Devices;
        var expected = new (string Name, int? Percent)[]
        {
            ("演示耳机", 73), ("演示键盘（未提供电量）", null),
            ("演示鼠标（充电中）", 42), ("演示键盘 2", 64), ("演示耳机 2", 91)
        }.Take(count).ToArray();
        Assert.Equal(count, devices.Count);
        Assert.Equal(expected.Select(device => device.Name).Order(), devices.Select(device => device.Name).Order());
        foreach (var (name, percentage) in expected)
        {
            var device = Assert.Single(devices, device => device.Name == name);
            Assert.True(device.Visible);
            Assert.Equal(ConnectionState.Online, device.Connection);
            Assert.Equal(percentage, device.BatteryPercent);
            var display = DeviceDisplayMapper.Map(device);
            Assert.NotNull(display);
            Assert.Equal(DisplayPresence.Online, display.Presence);
            Assert.Equal(percentage, display.Battery.Value);
            Assert.Equal(percentage is null ? BatteryTone.None : BatteryTone.Normal, display.Battery.Tone);
            Assert.False(display.ReadFailed);
            if (percentage is not null)
            {
                var receiver = name == "演示鼠标（充电中）";
                Assert.Equal(receiver ? "hid.atk.compx" : "windows.pnp", device.BatterySource);
                Assert.Equal(receiver ? DeviceTransport.Receiver : DeviceTransport.Bluetooth, display.Transport);
                Assert.Equal(receiver, display.Battery.Charging);
            }
        }
        Assert.False(session.Snapshot.IsRefreshing);
        Assert.Equal(0, session.Snapshot.OutstandingOperations);
    }

    [Theory]
    [InlineData("retained", ConnectionState.Online, BatteryFreshness.Retained, 73)]
    [InlineData("pending", ConnectionState.Pending, BatteryFreshness.Retained, 73)]
    [InlineData("unknown", ConnectionState.Online, BatteryFreshness.Unknown, null)]
    [InlineData("error", ConnectionState.Online, BatteryFreshness.Retained, 73)]
    [InlineData("expired", ConnectionState.Online, BatteryFreshness.Expired, null)]
    [InlineData("offline", ConnectionState.Offline, BatteryFreshness.Retained, 73)]
    [InlineData("recovered", ConnectionState.Online, BatteryFreshness.Recent, 79)]
    public async Task ScenarioTransitionCanBeRepeatedAfterAnotherScenario(string name,
        ConnectionState connection, BatteryFreshness freshness, int? battery)
    {
        var scenario = new AppScenarios();
        await using var session = new BatterySession(scenario.CreateSource(), scenario.Clock);
        foreach (var prior in new[] { "overview", "offline", "expired" })
        {
            await scenario.ApplyAsync(prior, session);
            await scenario.ApplyAsync(name, session);
            var device = Assert.Single(session.Snapshot.State.Devices, x => x.Name == "演示耳机");
            Assert.Equal(connection, device.Connection);
            Assert.Equal(freshness, device.Freshness);
            Assert.Equal(battery, device.BatteryPercent);
            Assert.Equal(connection != ConnectionState.Offline, device.Visible);
            Assert.Equal(name == "error", device.LastError is not null);
            Assert.Equal(name, scenario.Capture().Name);
            Assert.False(scenario.Capture().Applying);
            AssertChannelsDrained(scenario);
        }
    }

    [Fact]
    public async Task MissingAndErrorExamplesPreserveTheOriginalBatteryDeadline()
    {
        foreach (var name in new[] { "retained", "pending", "error" })
        {
            var scenario = new AppScenarios();
            await using var session = new BatterySession(scenario.CreateSource(), scenario.Clock);
            await scenario.ApplyAsync(name, session);
            var reading = Assert.Single(Assert.Single(session.Snapshot.State.Devices, x => x.Name == "演示耳机").Readings);
            Assert.Equal(TimeSpan.FromMinutes(15), reading.ExpiresAt - reading.ReceivedAt);
            var readAt = reading.ReceivedAt;
            var expires = reading.ExpiresAt;
            await session.RefreshAsync();
            var again = Assert.Single(Assert.Single(session.Snapshot.State.Devices, x => x.Name == "演示耳机").Readings);
            Assert.Equal(readAt, again.ReceivedAt);
            Assert.Equal(expires, again.ExpiresAt);
            AssertChannelsDrained(scenario);
        }
    }

    [Fact]
    public async Task RecoveredExampleActuallyReadsAnErrorThenAcceptsANewSample()
    {
        var scenario = new AppScenarios();
        var recording = new RecordingSource(scenario.CreateSource());
        await using var session = new BatterySession(recording, scenario.Clock);
        await scenario.ApplyAsync("recovered", session);
        var observations = recording.Observations.ToArray();
        var failure = observations.Last(x => x.Name == "演示耳机" && x.Source == "windows.pnp" && x.Status == ReadStatus.Error);
        var recovery = observations.Last(x => x.Name == "演示耳机" && x.Source == "windows.pnp" && x.Battery?.Percent == 79);
        Assert.True(recovery.ReceivedElapsed > failure.ReceivedElapsed);
        Assert.Equal(scenario.Clock.GetUtcNow(), recovery.Battery!.ReadAt);
        var device = Assert.Single(session.Snapshot.State.Devices, x => x.Name == "演示耳机");
        Assert.Null(device.LastError);
        Assert.Equal(79, device.BatteryPercent);
        AssertChannelsDrained(scenario);
    }

    [Fact]
    public void ExplicitScenarioArgumentIsRequiredAndValidated()
    {
        Assert.Null(AppScenarios.NameFromArguments([]));
        Assert.Null(AppScenarios.NameFromArguments(["--test-pipe", "example"]));
        Assert.Equal("overview", AppScenarios.NameFromArguments(["--scenario", "overview", "--test-pipe", "example"]));
        Assert.Throws<ArgumentException>(() => AppScenarios.NameFromArguments(["--scenario"]));
        Assert.Throws<ArgumentException>(() => AppScenarios.NameFromArguments(["--scenario", "unknown-name"]));
        Assert.Throws<ArgumentException>(() => AppScenarios.NameFromArguments(["--scenario", "overview", "--scenario", "error"]));
        Assert.Throws<ArgumentException>(() => AppScenarios.NameFromArguments(["--scenaro", "overview"]));
        Assert.Throws<ArgumentException>(() => AppScenarios.NameFromArguments(["--scenario", "overview", "extra"]));
        Assert.Throws<ArgumentException>(() => AppScenarios.NameFromArguments(["--test-pipe"]));
        Assert.Throws<ArgumentException>(() => AppScenarios.NameFromArguments(["--test-pipe", "--scenario"]));
        Assert.Throws<ArgumentException>(() => AppScenarios.NameFromArguments(["--test-pipe", "a", "--test-pipe", "b"]));
    }

    private sealed class RecordingSource(IDeviceSource inner) : IDeviceSource
    {
        public string Name => inner.Name;
        public System.Collections.Concurrent.ConcurrentQueue<DeviceObservation> Observations { get; } = new();
        public Task ReadAsync(ScanContext context, Action<SourceBatch> publish, CancellationToken cancellationToken) =>
            inner.ReadAsync(context, batch =>
            {
                foreach (var observation in batch.Observations) Observations.Enqueue(observation);
                publish(batch);
            }, cancellationToken);
    }

    private static void AssertChannelsDrained(AppScenarios scenario)
    {
        var state = scenario.Capture();
        Assert.True(state.OpenedHidChannels > 0);
        Assert.Equal(state.OpenedHidChannels, state.ClosedHidChannels);
        Assert.False(state.UsesRealHardware);
        Assert.False(state.SavesPreferences);
    }
}
