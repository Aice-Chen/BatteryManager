using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using BatteryManager.Core;

namespace BatteryManager.WindowsNative;

public sealed class LogitechK380BatterySource : IDeviceSource
{
    public const string SourceId = "hid.logitech.k380";
    private readonly IWindowsHidIo io;
    private readonly TimeProvider clock;
    private readonly TimeSpan attemptTimeout;
    private int softwareCounter = RandomNumberGenerator.GetInt32(15);
    private readonly HidSourceScan scan;
    public string Name => "LogitechK380Hid";

    public LogitechK380BatterySource(IWindowsHidIo? io = null, TimeProvider? clock = null, TimeSpan? attemptTimeout = null)
    {
        this.io = io ?? new WindowsHidIo();
        scan = new(this.io, SourceId, "K380");
        this.clock = clock ?? TimeProvider.System;
        this.attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(2);
        if (this.attemptTimeout <= TimeSpan.Zero || this.attemptTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
    }

    public Task ReadAsync(ScanContext context, Action<SourceBatch> publish, CancellationToken cancellationToken) =>
        scan.ReadAsync(devices => PairInterfaces(devices, publish),
            async (device, token) => await ReadDeviceAsync(device, publish, token).ConfigureAwait(false), publish, cancellationToken);

    private static IReadOnlyList<InterfacePair> PairInterfaces(IReadOnlyList<HidInterface> inventory, Action<SourceBatch> publish)
    {
        var candidates = inventory.Where(LogitechK380Protocol.Supports)
            .Select(device => new { Device = device, Address = WindowsDeviceMapping.NormalizeAddress(device.SerialNumber),
                Container = WindowsDeviceMapping.NormalizeContainer(device.ContainerId) }).ToArray();
        // One native path cannot safely identify multiple keyboards or collections,
        // even when both advertised address/container pairs are individually valid.
        var ambiguousPaths = candidates.GroupBy(candidate => candidate.Device.Path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(candidate => (candidate.Address, candidate.Container, candidate.Device.Usage)).Distinct().Count() != 1)
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var valid = candidates.Where(candidate => candidate.Address is not null && candidate.Container is not null).ToArray();
        var ambiguousAddresses = valid.GroupBy(candidate => candidate.Address!, StringComparer.Ordinal)
            .Where(group => group.Select(candidate => candidate.Container).Distinct(StringComparer.Ordinal).Count() != 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var ambiguousContainers = valid.GroupBy(candidate => candidate.Container!, StringComparer.Ordinal)
            .Where(group => group.Select(candidate => candidate.Address).Distinct(StringComparer.Ordinal).Count() != 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var pairs = new List<InterfacePair>();
        var rejected = candidates.Length != valid.Length;
        foreach (var group in valid.GroupBy(candidate => (candidate.Address!, candidate.Container!)))
        {
            var devices = group.Select(candidate => candidate.Device)
                .DistinctBy(device => device.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var shortDevices = devices.Where(device => device.Usage == 1).ToArray();
            var longDevices = devices.Where(device => device.Usage == 2).ToArray();
            if (ambiguousAddresses.Contains(group.Key.Item1) || ambiguousContainers.Contains(group.Key.Item2) ||
                group.Any(candidate => ambiguousPaths.Contains(candidate.Device.Path)) ||
                shortDevices.Length != 1 || longDevices.Length != 1 ||
                string.Equals(shortDevices[0].Path, longDevices[0].Path, StringComparison.OrdinalIgnoreCase))
            {
                rejected = true;
                continue;
            }
            pairs.Add(new(shortDevices[0], longDevices[0], group.Key.Item1, group.Key.Item2));
        }
        if (rejected)
            publish(new(SourceId + ".discovery", [],
                Error: "A K380 vendor-interface pair had missing or ambiguous Bluetooth identity; it was not queried.",
                FailureStatus: ReadStatus.Error));
        return pairs;
    }

    private async Task<DeviceObservation> ReadDeviceAsync(InterfacePair pair, Action<SourceBatch> publish, CancellationToken cancellationToken)
    {
        try
        {
            await using var shortChannel = await io.OpenAsync(pair.Short, cancellationToken).ConfigureAwait(false);
            await using var longChannel = await io.OpenAsync(pair.Long, cancellationToken).ConfigureAwait(false);
            async Task<HidReportReply?> Query(byte[] request) =>
                await QueryAsync(shortChannel, longChannel, request, cancellationToken).ConfigureAwait(false);

            var nonce = RandomNumberGenerator.GetBytes(1)[0];
            var ping = await Query(LogitechK380Protocol.CreatePingQuery(NextSoftwareId(), nonce)).ConfigureAwait(false);
            if (ping is null)
                return Observation(pair, ClockInstant.Read(clock), ReadStatus.Timeout,
                    error: "The K380 did not confirm its connection with a matching ping response.");
            if (ping.Report[0] < 2)
                return Observation(pair, ClockInstant.Read(clock), ReadStatus.Unsupported,
                    error: "The K380 reported an unsupported HID++ protocol version.");
            var onlineAt = ping.ReceivedAt;
            publish(new(SourceId, [Observation(pair, onlineAt, ReadStatus.Missing, connected: true)]));

            var feature = await Query(LogitechK380Protocol.CreateFeatureQuery(NextSoftwareId())).ConfigureAwait(false);
            if (feature is null)
                return Observation(pair, ClockInstant.Read(clock), ReadStatus.Timeout, error: "The K380 battery-feature lookup timed out.");
            if (feature.Report[0] is 0 or 0x8F or 0xFF)
                return Observation(pair, ClockInstant.Read(clock), ReadStatus.Unsupported, error: "The K380 did not expose a supported battery feature.");

            var capabilityReply = await Query(LogitechK380Protocol.CreateCapabilityQuery(feature.Report[0], NextSoftwareId())).ConfigureAwait(false);
            if (capabilityReply is null)
                return Observation(pair, ClockInstant.Read(clock), ReadStatus.Timeout, error: "The K380 battery-capability query timed out.");
            if (!LogitechK380Protocol.TryParseCapability(capabilityReply.Report, out var capability))
                return Observation(pair, ClockInstant.Read(clock), ReadStatus.Error, error: "The K380 returned an invalid battery capability.");

            var batteryReply = await Query(LogitechK380Protocol.CreateBatteryQuery(feature.Report[0], NextSoftwareId())).ConfigureAwait(false);
            if (batteryReply is null)
                return Observation(pair, ClockInstant.Read(clock), ReadStatus.Timeout, error: "The K380 battery query timed out.");
            if (!LogitechK380Protocol.TryParseBattery(batteryReply.Report, out var battery))
                return Observation(pair, ClockInstant.Read(clock), ReadStatus.Error, error: "The K380 returned invalid battery fields.");
            if (battery.Status != 0)
                return Observation(pair, ClockInstant.Read(clock), battery.Status >= 5 ? ReadStatus.Error : ReadStatus.Unsupported,
                    error: $"The non-rechargeable K380 reported an unverified charging status ({battery.Status}).");
            // Feature 1000 defines zero as unknown, not an empty battery.
            if (battery.Percent == 0) return Observation(pair, ClockInstant.Read(clock), ReadStatus.Missing);
            var receivedAt = batteryReply.ReceivedAt;
            return Observation(pair, receivedAt, ReadStatus.Success,
                new(battery.Percent, BatteryEvidence.ActiveRead, receivedAt.Utc, "HID++:1000",
                    Charging: false, Precision: capability.Precision, ReadAt: receivedAt.Utc, ReadElapsed: receivedAt.Elapsed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Observation(pair, ClockInstant.Read(clock), ReadStatus.Error, error: HidSourceScan.Describe(error)); }
    }

    private async Task<HidReportReply?> QueryAsync(IHidChannel shortChannel, IHidChannel longChannel, byte[] request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(attemptTimeout);
        var outcome = new TaskCompletionSource<(byte[]? Parameters, Exception? Error, ClockInstant At)>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Each HID channel serializes operations. Write before either pending
            // read, and drain both reads before the next query can take this gate.
            await longChannel.WriteInterruptReportAsync(request, deadline.Token).ConfigureAwait(false);
            async Task ReadAsync(IHidChannel channel)
            {
                await Task.Yield();
                try
                {
                    while (true)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        var report = await channel.ReadInputReportAsync(deadline.Token).ConfigureAwait(false);
                        var receivedAt = ClockInstant.Read(clock);
                        deadline.Token.ThrowIfCancellationRequested();
                        if (report.Length == 0) throw new IOException("The K380 HID input channel ended before a response arrived.");
                        if (!LogitechK380Protocol.TryMatchReply(report, request, out var parameters, out var errorCode)) continue;
                        var error = errorCode is { } code ? new IOException($"The K380 rejected HID++ request {request[2]:X2}/{request[3]:X2} with error {code:X2}.") : null;
                        if (outcome.TrySetResult((parameters, error, receivedAt))) await deadline.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                catch (Exception error)
                {
                    if (outcome.TrySetResult((null, error, ClockInstant.Read(clock)))) await deadline.CancelAsync().ConfigureAwait(false);
                }
            }
            await Task.WhenAll(ReadAsync(shortChannel), ReadAsync(longChannel)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        if (!outcome.Task.IsCompletedSuccessfully) return null;
        var result = await outcome.Task.ConfigureAwait(false);
        if (result.Error is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
        return result.Parameters is null ? null : new(result.Parameters, result.At);
    }

    private byte NextSoftwareId() => (byte)((uint)Interlocked.Increment(ref softwareCounter) % 15 + 1);

    private static DeviceObservation Observation(InterfacePair pair, ClockInstant at, ReadStatus status,
        BatterySample? battery = null, string? error = null, bool? connected = null) =>
        new("bt-classic:" + pair.Address, pair.Container, pair.Address, "Keyboard K380", DeviceKind.Keyboard, SourceId, at.Utc,
            Connected: connected, PathPresent: true, Battery: battery, Status: status, Error: error, ReceivedElapsed: at.Elapsed, Transport: DeviceTransport.Bluetooth);

    private sealed record InterfacePair(HidInterface Short, HidInterface Long, string Address, string Container);
}
