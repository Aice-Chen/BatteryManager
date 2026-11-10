using System.Collections.Concurrent;
using BatteryManager.Core;

namespace BatteryManager.WindowsNative;

public sealed class AtkBatterySource : IDeviceSource
{
    public const string SourceId = "hid.atk.compx";
    private readonly IWindowsHidIo io;
    private readonly TimeProvider clock;
    private readonly TimeSpan attemptTimeout;
    private int active;

    public string Name => "AtkHid";

    public AtkBatterySource(IWindowsHidIo? io = null, TimeProvider? clock = null, TimeSpan? attemptTimeout = null)
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
            throw new InvalidOperationException("The previous ATK scan has not physically completed.");
        try
        {
            var inventory = await io.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var complete = inventory.Complete && inventory.Errors.Count == 0;
            if (!complete)
                publish(new(SourceId, [], Error: inventory.Errors.Count > 0
                    ? string.Join("; ", inventory.Errors) : "HID enumeration was incomplete.", FailureStatus: ReadStatus.Error));

            var candidates = inventory.Interfaces.Where(AtkCompxProtocol.Supports)
                .DistinctBy(device => device.Path, StringComparer.OrdinalIgnoreCase)
                .GroupBy(Endpoint, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(device => device.UsagePage == 0xFF02 ? 0 : 1)
                    .ThenBy(device => device.Path, StringComparer.OrdinalIgnoreCase).First()).ToArray();
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
            // An incomplete inventory cannot revoke unseen receivers. Its earlier error
            // remains on unseen fields, while successful device reads retain their success.
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
            var onlineResponse = await QueryAsync(channel, AtkCompxProtocol.CreateOnlineQuery(device.OutputReportLength),
                report => AtkCompxProtocol.TryParseOnline(report, out _), cancellationToken).ConfigureAwait(false);
            if (onlineResponse is null)
                return Observation(device, ClockInstant.Read(clock), ReadStatus.Timeout,
                    error: "The receiver did not confirm the mouse's connection state.");
            AtkCompxProtocol.TryParseOnline(onlineResponse.Report, out var online);
            if (online != true)
                return Observation(device, onlineResponse.ReceivedAt, online == false ? ReadStatus.Success : ReadStatus.Error,
                    connected: online, error: online is null ? "The receiver returned an unknown mouse connection status." : null);

            // Deliver actual body connection evidence immediately. A later failed
            // battery query must not remove it or advance its observation time.
            publish(new(SourceId, [Observation(device, onlineResponse.ReceivedAt, ReadStatus.Missing, connected: true)]));
            var batteryResponse = await QueryAsync(channel, AtkCompxProtocol.CreateBatteryQuery(device.OutputReportLength),
                report => AtkCompxProtocol.TryParseBattery(report, out _), cancellationToken).ConfigureAwait(false);
            if (batteryResponse is null)
                return Observation(device, ClockInstant.Read(clock), ReadStatus.Timeout,
                    error: "The mouse is connected, but the receiver did not return a valid battery response.");
            AtkCompxProtocol.TryParseBattery(batteryResponse.Report, out var battery);
            var receivedAt = batteryResponse.ReceivedAt;
            return Observation(device, receivedAt, ReadStatus.Success,
                new(battery.Percent, BatteryEvidence.ActiveRead, receivedAt.Utc,
                    "COMPX:08/04", Charging: battery.Charging, ReadAt: receivedAt.Utc, ReadElapsed: receivedAt.Elapsed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Observation(device, ClockInstant.Read(clock), ReadStatus.Error, error: Describe(error)); }
    }

    private Task<HidReportReply?> QueryAsync(IHidChannel channel, byte[] request, Func<byte[], bool> accepts, CancellationToken cancellationToken) =>
        HidReportQuery.ReadAsync(channel, request, accepts, attemptTimeout, 3, cancellationToken, clock: clock);

    private static DeviceObservation Observation(HidInterface device, ClockInstant at, ReadStatus status,
        BatterySample? battery = null, string? error = null, bool? connected = null) =>
        new(Endpoint(device), null, null,
            string.IsNullOrWhiteSpace(device.ProductName) ? "ATK wireless mouse" : device.ProductName,
            DeviceKind.Mouse, SourceId, at.Utc, Connected: connected,
            PathPresent: true, Battery: battery, Status: status, Error: error, ReceivedElapsed: at.Elapsed);

    private static string Endpoint(HidInterface device)
    {
        // Container identifies the receiver's collections, not a Bluetooth peripheral.
        // Keep it inside this source's endpoint instead of passing it to global identity merging.
        var container = WindowsDeviceMapping.NormalizeContainer(device.ContainerId);
        return container is not null ? $"hid:atk:373b:11d9:receiver:{container}" : $"hid:atk:path:{device.Path.Trim().ToLowerInvariant()}";
    }

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message} (0x{error.HResult:X8})";
}
