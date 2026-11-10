using System.Collections.Concurrent;
using BatteryManager.Core;

namespace BatteryManager.WindowsNative;

public sealed class WindowsNativeSource : IDeviceSource
{
    private readonly IWindowsNativeIo io;
    private readonly TimeProvider clock;
    private readonly int concurrency;
    private readonly ConcurrentDictionary<WindowsQueryKind, int> diagnosticOnlyCounts = new();
    private int active;

    public string Name => "WindowsNative";
    public IReadOnlyDictionary<WindowsQueryKind, int> DiagnosticOnlyCounts => new Dictionary<WindowsQueryKind, int>(diagnosticOnlyCounts);

    public WindowsNativeSource(IWindowsNativeIo? io = null, TimeProvider? clock = null, int maximumConcurrency = 4)
    {
        if (maximumConcurrency is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        this.io = io ?? new WindowsNativeIo();
        this.clock = clock ?? TimeProvider.System;
        concurrency = maximumConcurrency;
    }

    public async Task ReadAsync(ScanContext context, Action<SourceBatch> publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("The previous native scan has not physically completed.");
        try
        {
            diagnosticOnlyCounts.Clear();
            using var dispatch = new PhysicalDispatch(concurrency, cancellationToken);
            var tasks = Enum.GetValues<WindowsQueryKind>().Select(kind => ReadQueryAsync(kind, dispatch, publish, cancellationToken));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref active, 0); }
    }

    private async Task ReadQueryAsync(WindowsQueryKind kind, PhysicalDispatch dispatch,
        Action<SourceBatch> publish, CancellationToken cancellationToken)
    {
        var source = SourceName(kind);
        var observations = new Dictionary<string, DeviceObservation>(StringComparer.Ordinal);
        var nodeResults = new Dictionary<string, Dictionary<string, DeviceObservation>>(StringComparer.Ordinal);
        var observationGate = new object();
        var errors = new ConcurrentQueue<string>();
        try
        {
            var inventory = await dispatch.RunAsync(async () =>
            {
                var devices = await io.EnumerateAsync(kind).ConfigureAwait(false);
                return (Devices: devices, At: ClockInstant.Read(clock));
            }).ConfigureAwait(false);
            var raw = inventory.Devices;
            var devices = raw.Where(item => WindowsDeviceMapping.IsRelevant(item, kind)).ToArray();
            diagnosticOnlyCounts[kind] = raw.Count - devices.Length;
            var descriptions = devices.GroupBy(item => WindowsDeviceMapping.Endpoint(item, kind), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderBy(item => IsDeviceRoot(item) ? 0 : 1)
                    .ThenBy(item => item.Name.Length).ThenBy(item => item.Id, StringComparer.Ordinal).First(), StringComparer.Ordinal);

            void PublishNode(RawWindowsDevice device, DeviceObservation observation)
            {
                lock (observationGate)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    if (!nodeResults.TryGetValue(observation.EndpointId, out var nodes))
                        nodeResults[observation.EndpointId] = nodes = new(StringComparer.Ordinal);
                    nodes[device.Id] = observation;
                    // A root, HFP and AVRCP node can all describe one endpoint. Missing
                    // sibling properties must not erase an already delivered battery.
                    var ordered = nodes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray();
                    var selected = ordered.FirstOrDefault(item => item.Battery is not null && item.Status == ReadStatus.Success)
                        ?? ordered.FirstOrDefault(item => item.Status == ReadStatus.Error)
                        ?? ordered[0];
                    var description = descriptions[observation.EndpointId];
                    var mergedAt = ClockInstant.Read(clock);
                    var merged = selected with
                    {
                        Name = WindowsDeviceMapping.Text(description.Properties, "System.ItemNameDisplay") ?? description.Name,
                        Kind = WindowsDeviceMapping.Kind(description),
                        ReceivedAt = mergedAt.Utc,
                        ReceivedElapsed = mergedAt.Elapsed,
                        PathPresent = ordered.Any(item => item.PathPresent),
                        Connected = ordered.Any(item => item.Connected == false) ? false
                            : ordered.Any(item => item.Connected == true) ? true : null,
                        Error = ordered.Select(item => item.Error).FirstOrDefault(error => error is not null)
                    };
                    observations[observation.EndpointId] = merged;
                    publish(new(source, [merged]));
                }
            }
            var index = -1;
            async Task ProcessNextAsync()
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var current = Interlocked.Increment(ref index);
                    if (current >= devices.Length) return;
                    var device = devices[current];
                    try
                    {
                        var observation = await ReadDeviceAsync(device, kind, source, dispatch, inventory.At).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested) return;
                        if (observation.Status == ReadStatus.Error || observation.Error is not null)
                            errors.Enqueue(observation.Error ?? "Property read failed.");
                        PublishNode(device, observation);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                    catch (Exception exception)
                    {
                        var observation = Describe(device, kind, source, null, ReadStatus.Error, Error(exception));
                        errors.Enqueue(Error(exception));
                        PublishNode(device, observation);
                    }
                }
            }
            // At most this many node workers; no Task per enumerated device.
            await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrency, devices.Length)).Select(_ => ProcessNextAsync())).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
                publish(new(source, observations.Values.ToArray(), Complete: true, Authoritative: errors.IsEmpty));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
                publish(new(source, observations.Values.ToArray(), Complete: true, Authoritative: false, Error: Error(exception)));
        }
    }

    private static bool IsDeviceRoot(RawWindowsDevice device) =>
        (WindowsDeviceMapping.Text(device.Properties, WindowsPropertyNames.InstanceId) ?? device.Id)
            .StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase);

    private async Task<DeviceObservation> ReadDeviceAsync(RawWindowsDevice raw, WindowsQueryKind kind,
        string source, PhysicalDispatch dispatch, ClockInstant inventoryAt)
    {
        var battery = WindowsPropertyDecoder.DecodeNamed(raw.Properties);
        var batteryAt = inventoryAt;
        if (kind == WindowsQueryKind.PnpDevice)
        {
            var instanceId = WindowsDeviceMapping.Text(raw.Properties, WindowsPropertyNames.InstanceId) ?? raw.Id;
            var read = await dispatch.RunAsync(async () =>
            {
                var property = await io.ReadBatteryPropertyAsync(instanceId).ConfigureAwait(false);
                return (Property: property, At: ClockInstant.Read(clock));
            }).ConfigureAwait(false);
            var native = WindowsPropertyDecoder.DecodePnp(read.Property);
            if (native.Sample is not null) { battery = native; batteryAt = read.At; }
            else if (battery.Sample is null) battery = native;
            else if (native.Status == ReadStatus.Error) battery = battery with { Error = native.Error };
        }
        // Missing battery is an ordinary capability result, including on connected AEPs.
        var sample = battery.Sample is { } value ? value with { ReadAt = batteryAt.Utc, ReadElapsed = batteryAt.Elapsed } : null;
        return Describe(raw, kind, source, sample, battery.Status, battery.Error);
    }

    private DeviceObservation Describe(RawWindowsDevice raw, WindowsQueryKind kind, string source,
        BatterySample? battery, ReadStatus status, string? error)
    {
        var aep = kind is WindowsQueryKind.BluetoothClassic or WindowsQueryKind.BluetoothLe;
        var container = WindowsDeviceMapping.Text(raw.Properties, aep ? "System.Devices.Aep.ContainerId" : WindowsPropertyNames.ContainerId);
        var connected = aep ? WindowsDeviceMapping.Boolean(raw.Properties, "System.Devices.Aep.IsConnected") : null;
        var present = WindowsDeviceMapping.Boolean(raw.Properties, aep ? "System.Devices.Aep.IsPresent" : "System.Devices.Present") == true;
        if (kind == WindowsQueryKind.DeviceInterface)
            present = WindowsDeviceMapping.Boolean(raw.Properties, "System.Devices.Connected") == true;
        var at = ClockInstant.Read(clock);
        return new(WindowsDeviceMapping.Endpoint(raw, kind), WindowsDeviceMapping.NormalizeContainer(container),
            WindowsDeviceMapping.Address(raw), WindowsDeviceMapping.Text(raw.Properties, "System.ItemNameDisplay") ?? raw.Name,
            WindowsDeviceMapping.Kind(raw), source, at.Utc, connected, present, battery, status, error, at.Elapsed,
            Transport: WindowsDeviceMapping.IsRelevant(raw, kind) ? DeviceTransport.Bluetooth : DeviceTransport.Unknown);
    }

    public static string SourceName(WindowsQueryKind kind) => kind switch
    {
        WindowsQueryKind.BluetoothClassic => "windows.aep.classic",
        WindowsQueryKind.BluetoothLe => "windows.aep.le",
        WindowsQueryKind.DeviceInterface => "windows.interface",
        _ => "windows.pnp"
    };

    private static string Error(Exception exception) => $"{exception.GetType().Name}: 0x{exception.HResult:X8}: {exception.Message}";

}
