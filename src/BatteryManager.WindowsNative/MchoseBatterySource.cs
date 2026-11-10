using System.Collections.Concurrent;
using BatteryManager.Core;

namespace BatteryManager.WindowsNative;

public sealed class MchoseBatterySource : IDeviceSource
{
    public const string SourceId = "hid.mchose.g3";
    private readonly IWindowsHidIo io;
    private readonly TimeProvider clock;
    private readonly TimeSpan attemptTimeout;
    private int active;
    public string Name => "MchoseHid";

    public MchoseBatterySource(IWindowsHidIo? io = null, TimeProvider? clock = null, TimeSpan? attemptTimeout = null)
    {
        this.io = io ?? new WindowsHidIo();
        this.clock = clock ?? TimeProvider.System;
        this.attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(1);
        if (this.attemptTimeout <= TimeSpan.Zero || this.attemptTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
    }

    public async Task ReadAsync(ScanContext context, Action<SourceBatch> publish, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("The previous MCHOSE scan has not physically completed.");
        try
        {
            var inventory = await io.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var complete = inventory.Complete && inventory.Errors.Count == 0;
            if (!complete)
                publish(new(SourceId, [], Error: inventory.Errors.Count > 0
                    ? string.Join("; ", inventory.Errors) : "HID enumeration was incomplete.", FailureStatus: ReadStatus.Error));
            var candidates = inventory.Interfaces.Where(MchoseG3Protocol.Supports)
                .DistinctBy(device => device.Path, StringComparer.OrdinalIgnoreCase)
                .GroupBy(Endpoint, StringComparer.Ordinal)
                .Select(group => group.OrderBy(device => device.Path, StringComparer.OrdinalIgnoreCase).First()).ToArray();
            var observations = new ConcurrentBag<DeviceObservation>();
            await Parallel.ForEachAsync(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
                async (device, token) =>
                {
                    var observation = await ReadDeviceAsync(device, publish, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    observations.Add(observation);
                    publish(new(SourceId, [observation]));
                }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (complete) publish(new(SourceId, observations.ToArray(), Complete: true, Authoritative: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!cancellationToken.IsCancellationRequested)
                publish(new(SourceId, [], Error: Describe(error), FailureStatus: ReadStatus.Error));
        }
        finally { Volatile.Write(ref active, 0); }
    }

    private async Task<DeviceObservation> ReadDeviceAsync(HidInterface device, Action<SourceBatch> publish, CancellationToken cancellationToken)
    {
        try
        {
            await using var channel = await io.OpenAsync(device, cancellationToken).ConfigureAwait(false);
            var onlineReply = await QueryAsync(channel, MchoseG3Protocol.CreateOnlineQuery(),
                report => MchoseG3Protocol.TryParseOnline(report, out _), cancellationToken).ConfigureAwait(false);
            var onlineAt = onlineReply?.ReceivedAt ?? ClockInstant.Read(clock);
            if (onlineReply is null)
                return Observation(device, onlineAt, ReadStatus.Timeout,
                    error: "The receiver did not confirm the mouse's connection state.");
            MchoseG3Protocol.TryParseOnline(onlineReply.Report, out var status);
            if (status == 1)
                // Controlled hardware testing also observes 1 while the powered mouse
                // is idle. It cannot revoke previous connection evidence or renew it.
                return Observation(device, onlineAt, ReadStatus.Missing);
            if (status != 2)
                return Observation(device, onlineAt, ReadStatus.Error,
                    error: $"The receiver returned an unverified mouse connection status ({status}).");

            publish(new(SourceId, [Observation(device, onlineAt, ReadStatus.Missing, connected: true)]));
            var batteryReply = await QueryAsync(channel, MchoseG3Protocol.CreateBatteryQuery(),
                report => MchoseG3Protocol.TryParseBattery(report, out _), cancellationToken).ConfigureAwait(false);
            if (batteryReply is null)
                return Observation(device, ClockInstant.Read(clock), ReadStatus.Timeout,
                    error: "The mouse is active, but the receiver did not return a valid battery response.");
            MchoseG3Protocol.TryParseBattery(batteryReply.Report, out var battery);
            var receivedAt = batteryReply.ReceivedAt;
            var charging = battery.ChargingFlag switch { 0 => (bool?)false, 1 => true, _ => null };
            return Observation(device, receivedAt, ReadStatus.Success,
                new(battery.Percent, BatteryEvidence.ActiveRead, receivedAt.Utc, "MCHOSE-G3:00/30",
                    Charging: charging, ReadAt: receivedAt.Utc, ReadElapsed: receivedAt.Elapsed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Observation(device, ClockInstant.Read(clock), ReadStatus.Error, error: Describe(error)); }
    }

    private Task<HidReportReply?> QueryAsync(IHidChannel channel, byte[] request, Func<byte[], bool> accepts, CancellationToken cancellationToken) =>
        HidReportQuery.ReadAsync(channel, request, accepts, attemptTimeout, 2, cancellationToken, useInterruptOutput: true, clock: clock);

    private static DeviceObservation Observation(HidInterface device, ClockInstant at, ReadStatus status,
        BatterySample? battery = null, string? error = null, bool? connected = null) =>
        new(Endpoint(device), null, null, "MCHOSE G3 A", DeviceKind.Mouse, SourceId, at.Utc,
            Connected: connected, PathPresent: true, Battery: battery, Status: status, Error: error, ReceivedElapsed: at.Elapsed);

    private static string Endpoint(HidInterface device)
    {
        var container = WindowsDeviceMapping.NormalizeContainer(device.ContainerId);
        return container is not null ? $"hid:mchose:a8a5:2255:receiver:{container}" : $"hid:mchose:path:{device.Path.Trim().ToLowerInvariant()}";
    }

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message} (0x{error.HResult:X8})";
}
