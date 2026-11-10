using BatteryManager.App;
using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class TrayPreferenceIdentityTests
{
    private const string PreferredAddress = "AABBCCDDEEFF";
    private const string FallbackAddress = "112233445566";
    private const string SharedContainer = "1e390823-b97a-4108-a94d-d236208e615e";
    private const string PreferredName = "Bluetooth Keyboard";
    private const string FallbackName = "Fallback Mouse";
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 1, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(WindowsQueryKind.BluetoothClassic, WindowsQueryKind.BluetoothClassic)]
    [InlineData(WindowsQueryKind.BluetoothClassic, WindowsQueryKind.PnpDevice)]
    [InlineData(WindowsQueryKind.PnpDevice, WindowsQueryKind.BluetoothClassic)]
    [InlineData(WindowsQueryKind.PnpDevice, WindowsQueryKind.PnpDevice)]
    [InlineData(null, WindowsQueryKind.BluetoothClassic)]
    [InlineData(null, WindowsQueryKind.PnpDevice)]
    public async Task OptionalWindowsContainerAndPublicationOrderPreserveSavedPriorityAcrossRestart(
        WindowsQueryKind? containerOn, WindowsQueryKind firstSource)
    {
        // These scans own separate sources and StateStores, as after closing and reopening the app.
        var beforeRestart = await ScanAsync(PreferredAddress, WindowsQueryKind.BluetoothClassic,
            WindowsQueryKind.BluetoothClassic);
        var chosen = Assert.Single(beforeRestart.Devices, device => device.Name == PreferredName);
        var path = SettingsPath();
        var saved = await new TrayPreferencesStore(path).SaveAsync([new(chosen.Id, chosen.Name)]);
        Assert.True(saved.Success, saved.Error);

        var afterRestart = await ScanAsync(PreferredAddress, containerOn, firstSource);
        var restored = Assert.Single(afterRestart.Devices, device => device.Name == PreferredName);
        var fallback = Assert.Single(afterRestart.Devices, device => device.Name == FallbackName);
        Assert.Equal(chosen.Id, restored.Id);
        Assert.Equal(ConnectionState.Online, restored.Connection);
        Assert.Equal(73, restored.BatteryPercent);
        Assert.Equal(BatteryFreshness.Recent, restored.Freshness);

        var loaded = new TrayPreferencesStore(path).Load();
        Assert.True(loaded.Success, loaded.Error);
        // Supplying an available fallback proves the stored ID actually matched the preferred device.
        Assert.Same(restored, TraySelection.Select(afterRestart,
            loaded.Priorities.Select(preference => preference.DeviceId).ToArray(), fallback.Id));
        Assert.Equal(chosen.Id, Assert.Single(loaded.Priorities).DeviceId);
    }

    [Theory]
    [InlineData(WindowsQueryKind.BluetoothClassic)]
    [InlineData(WindowsQueryKind.PnpDevice)]
    public async Task DifferentFullBluetoothAddressDoesNotInheritSameNameAndContainerPreference(WindowsQueryKind firstSource)
    {
        var original = await ScanAsync(PreferredAddress, WindowsQueryKind.BluetoothClassic,
            WindowsQueryKind.BluetoothClassic);
        var oldDevice = Assert.Single(original.Devices, device => device.Name == PreferredName);
        var path = SettingsPath();
        Assert.True((await new TrayPreferencesStore(path).SaveAsync([new(oldDevice.Id, oldDevice.Name)])).Success);

        // All descriptive metadata is deliberately equal; only the complete Bluetooth address differs.
        var different = await ScanAsync("AABBCCDDEE00", WindowsQueryKind.BluetoothClassic, firstSource);
        var newDevice = Assert.Single(different.Devices, device => device.Name == PreferredName);
        var fallback = Assert.Single(different.Devices, device => device.Name == FallbackName);
        Assert.NotEqual(oldDevice.Id, newDevice.Id);
        Assert.Equal(ConnectionState.Online, newDevice.Connection);
        Assert.Equal(73, newDevice.BatteryPercent);
        var loaded = new TrayPreferencesStore(path).Load();
        Assert.True(loaded.Success, loaded.Error);
        Assert.Same(fallback, TraySelection.Select(different,
            loaded.Priorities.Select(preference => preference.DeviceId).ToArray(), fallback.Id));
        Assert.Equal(oldDevice.Id, Assert.Single(loaded.Priorities).DeviceId);
    }

    private static async Task<StateSnapshot> ScanAsync(
        string preferredAddress, WindowsQueryKind? containerOn, WindowsQueryKind firstSource)
    {
        var io = new OrderedIo(preferredAddress, containerOn);
        var store = new StateStore();
        store.BeginScan(1, manual: false);
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new WindowsNativeSource(io, new FixedClock());
        var scan = source.ReadAsync(new(1, false), batch =>
        {
            store.Apply(1, batch, Now);
            if (batch.Source == WindowsNativeSource.SourceName(firstSource) &&
                batch.Observations.Any(observation => observation.BluetoothAddress == preferredAddress))
                firstPublished.TrySetResult();
        }, CancellationToken.None);
        try
        {
            io.Release(firstSource);
            await firstPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The other Windows node cannot publish until its I/O boundary is released below.
            io.Release(firstSource == WindowsQueryKind.BluetoothClassic
                ? WindowsQueryKind.PnpDevice : WindowsQueryKind.BluetoothClassic);
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            io.Release(WindowsQueryKind.BluetoothClassic);
            io.Release(WindowsQueryKind.PnpDevice);
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
        }
        store.EndScan(1);
        var snapshot = store.Snapshot(Now);
        Assert.Equal(2, snapshot.Devices.Count);
        Assert.All(snapshot.Devices, device => Assert.Equal(ConnectionState.Online, device.Connection));
        return snapshot;
    }

    private sealed class OrderedIo(string preferredAddress, WindowsQueryKind? containerOn) : IWindowsNativeIo
    {
        private readonly TaskCompletionSource _aep = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _pnp = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(WindowsQueryKind kind)
        {
            if (kind == WindowsQueryKind.BluetoothClassic) _aep.TrySetResult();
            if (kind == WindowsQueryKind.PnpDevice) _pnp.TrySetResult();
        }

        public async Task<IReadOnlyList<RawWindowsDevice>> EnumerateAsync(WindowsQueryKind kind)
        {
            if (kind == WindowsQueryKind.BluetoothClassic)
            {
                await _aep.Task;
                return [Aep(preferredAddress, PreferredName, containerOn == kind), Aep(FallbackAddress, FallbackName, false)];
            }
            if (kind == WindowsQueryKind.PnpDevice)
            {
                await _pnp.Task;
                return [Pnp(preferredAddress, PreferredName, containerOn == kind), Pnp(FallbackAddress, FallbackName, false)];
            }
            return [];
        }

        public Task<RawPnpProperty> ReadBatteryPropertyAsync(string instanceId) =>
            Task.FromResult(new RawPnpProperty(0, 0, 0x03,
                [instanceId.Contains(preferredAddress, StringComparison.Ordinal) ? (byte)73 : (byte)62]));

        private static RawWindowsDevice Aep(string address, string name, bool hasContainer)
        {
            var properties = new Dictionary<string, object?>
            {
                [WindowsPropertyNames.Address] = string.Join(':', Enumerable.Range(0, 6)
                    .Select(index => address.Substring(index * 2, 2))).ToLowerInvariant(),
                ["System.Devices.Aep.IsConnected"] = true,
                ["System.Devices.Aep.IsPresent"] = true
            };
            if (hasContainer) properties["System.Devices.Aep.ContainerId"] = SharedContainer;
            return new("aep-" + address, name, properties);
        }

        private static RawWindowsDevice Pnp(string address, string name, bool hasContainer)
        {
            var properties = new Dictionary<string, object?> { ["System.Devices.Present"] = true };
            if (hasContainer) properties[WindowsPropertyNames.ContainerId] = SharedContainer;
            return new($"BTHENUM\\DEV_{address}\\TEST", name, properties);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => 0;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private static string SettingsPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Project root not found for test artifacts.");
        var directory = Path.Combine(root.FullName, ".artifacts", "tests", "tray-preference-identity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "settings.json");
    }
}
