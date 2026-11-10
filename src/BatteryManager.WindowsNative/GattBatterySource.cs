using System.Collections.Concurrent;
using BatteryManager.Core;

namespace BatteryManager.WindowsNative;

public sealed record RawGattBattery(byte[]? Value, ReadStatus Status, DateTimeOffset? ObservedAt = null, string? Error = null,
    TimeSpan? ReadElapsed = null);

public interface IWindowsGattIo
{
    Task<IReadOnlyList<RawWindowsDevice>> EnumerateCandidatesAsync();
    // Completion also proves all native objects used by the read have been released.
    Task<RawGattBattery> ReadBatteryAsync(RawWindowsDevice candidate, CancellationToken cancellationToken);
}

public sealed class GattBatterySource : IDeviceSource
{
    private readonly IWindowsGattIo io;
    private readonly TimeProvider clock;
    private readonly int concurrency;
    private int active;
    public string Name => "BluetoothGatt";
    public const string SourceId = "bluetooth.gatt.bas";

    public GattBatterySource(IWindowsGattIo? io = null, TimeProvider? clock = null, int maximumConcurrency = 2)
    {
        if (maximumConcurrency is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        this.clock = clock ?? TimeProvider.System;
        this.io = io ?? new WindowsGattIo(this.clock);
        concurrency = maximumConcurrency;
    }

    public async Task ReadAsync(ScanContext context, Action<SourceBatch> publish, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("The previous GATT scan has not physically completed.");
        var observations = new ConcurrentBag<DeviceObservation>();
        try
        {
            using var dispatch = new PhysicalDispatch(concurrency, cancellationToken);
            var all = await dispatch.RunAsync(io.EnumerateCandidatesAsync).ConfigureAwait(false);
            var candidates = all.Where(raw => WindowsDeviceMapping.Boolean(raw.Properties, "System.Devices.Aep.IsConnected") == true &&
                WindowsDeviceMapping.Address(raw) is not null)
                .GroupBy(raw => WindowsDeviceMapping.Endpoint(raw, WindowsQueryKind.BluetoothLe), StringComparer.Ordinal)
                .Select(group => group.First()).ToArray();
            var next = -1;
            async Task Worker()
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= candidates.Length) return;
                    var candidate = candidates[index];
                    RawGattBattery read;
                    try { read = await dispatch.RunAsync(() => io.ReadBatteryAsync(candidate, cancellationToken)).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                    catch (Exception exception) { read = new(null, ReadStatus.Error, Error: $"{exception.GetType().Name}: 0x{exception.HResult:X8}"); }
                    if (cancellationToken.IsCancellationRequested) return;
                    var receivedAt = ClockInstant.Read(clock);
                    var status = read.Status;
                    var error = read.Error;
                    BatterySample? sample = null;
                    if (read.Status == ReadStatus.Success)
                    {
                        if (read.Value is { Length: 1 } && read.Value[0] <= 100)
                            sample = new(read.Value[0], BatteryEvidence.ActiveRead, read.ObservedAt ?? receivedAt.Utc, "GATT:180F/2A19",
                                ReadAt: read.ObservedAt ?? receivedAt.Utc,
                                ReadElapsed: read.ReadElapsed ?? (read.ObservedAt is null ? receivedAt.Elapsed : null));
                        else { status = ReadStatus.Error; error = "BAS battery level must be one byte in the range 0–100."; }
                    }
                    var observation = new DeviceObservation(WindowsDeviceMapping.Endpoint(candidate, WindowsQueryKind.BluetoothLe),
                        WindowsDeviceMapping.NormalizeContainer(WindowsDeviceMapping.Text(candidate.Properties, "System.Devices.Aep.ContainerId")),
                        WindowsDeviceMapping.Address(candidate), candidate.Name, WindowsDeviceMapping.Kind(candidate), SourceId,
                        receivedAt.Utc, Connected: sample is not null ? true : null, Battery: sample, Status: status, Error: error,
                        ReceivedElapsed: receivedAt.Elapsed);
                    observations.Add(observation);
                    publish(new(SourceId, [observation]));
                }
            }
            await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrency, candidates.Length)).Select(_ => Worker())).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
                publish(new(SourceId, observations.ToArray(), Complete: true, Authoritative: false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
                publish(new(SourceId, observations.ToArray(), Complete: true, Error: $"{exception.GetType().Name}: 0x{exception.HResult:X8}"));
        }
        finally { Volatile.Write(ref active, 0); }
    }
}
