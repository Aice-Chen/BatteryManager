using BatteryManager.WindowsNative;

namespace BatteryManager.Cli.Probe;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("empty" or "success" or "hung" or "blocked-output"))
            return Task.FromResult(99);
        string scenario = args[0];
        var source = new WindowsNativeSource(new RawIo(scenario));
        return Cli.Program.RunAsync([scenario is "empty" or "success" ? "scan" : "watch", "--json"], source);
    }

    // The production reader, state, session and command are unchanged. Only OS calls are replaced.
    private sealed class RawIo(string scenario) : IWindowsNativeIo
    {
        private readonly TaskCompletionSource<IReadOnlyList<RawWindowsDevice>> _physicalOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<RawWindowsDevice>> EnumerateAsync(WindowsQueryKind kind)
        {
            if (scenario == "empty" || kind != WindowsQueryKind.BluetoothClassic)
                return Task.FromResult<IReadOnlyList<RawWindowsDevice>>([]);
            if (scenario == "hung")
            {
                Console.Error.WriteLine("PROBE:PHYSICAL_IO_STARTED");
                // No cancellation wrapper: this operation really remains unfinished until process exit.
                return _physicalOperation.Task;
            }
            var name = scenario == "blocked-output" ? new string('X', 4 * 1024 * 1024) : "Process Test Keyboard";
            var device = new RawWindowsDevice("probe-aep", name, new Dictionary<string, object?>
            {
                [WindowsPropertyNames.Address] = "12:34:56:78:9A:BC",
                [WindowsPropertyNames.BatteryLife] = (byte)73,
                ["System.Devices.Aep.IsConnected"] = true,
                ["System.Devices.Aep.IsPresent"] = true,
                ["System.Devices.Aep.Category"] = new[] { "Keyboard" }
            });
            if (scenario == "blocked-output") Console.Error.WriteLine("PROBE:LARGE_RAW_DEVICE_READY");
            return Task.FromResult<IReadOnlyList<RawWindowsDevice>>([device]);
        }

        public Task<RawPnpProperty> ReadBatteryPropertyAsync(string instanceId) =>
            throw new InvalidOperationException("This probe supplies no PnP candidates.");
    }
}
