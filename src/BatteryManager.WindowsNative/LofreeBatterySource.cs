using System.Collections.Concurrent;
using BatteryManager.Core;

namespace BatteryManager.WindowsNative;

public sealed class LofreeBatterySource : IDeviceSource
{
    public const string SourceId = "hid.lofree.compx";
    private readonly IWindowsHidIo io;
    private readonly TimeProvider clock;
    private readonly TimeSpan attemptTimeout;
    private readonly object knownGate = new();
    private readonly Dictionary<string, long> knownReceivers = new(StringComparer.Ordinal);
    private long lastVerification;
    private int active;
    public string Name => "LofreeHid";

    public LofreeBatterySource(IWindowsHidIo? io = null, TimeProvider? clock = null, TimeSpan? attemptTimeout = null)
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
            throw new InvalidOperationException("The previous Lofree scan has not physically completed.");
        try
        {
            var inventory = await io.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var complete = inventory.Complete && inventory.Errors.Count == 0;
            if (!complete)
                publish(new(SourceId, [], Error: inventory.Errors.Count > 0
                    ? string.Join("; ", inventory.Errors) : "HID enumeration was incomplete.", FailureStatus: ReadStatus.Error));
            var candidates = inventory.Interfaces.Where(LofreeCompxProtocol.Supports)
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
                    if (observation is null) return;
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

    private async Task<DeviceObservation?> ReadDeviceAsync(HidInterface device, Action<SourceBatch> publish, CancellationToken cancellationToken)
    {
        try
        {
            await using var channel = await io.OpenAsync(device, cancellationToken).ConfigureAwait(false);
            var onlineReply = await QueryAsync(channel, LofreeCompxProtocol.CreateOnlineQuery(),
                report => LofreeCompxProtocol.TryParseOnline(report, out _), cancellationToken).ConfigureAwait(false);
            var onlineAt = onlineReply?.ReceivedAt ?? ClockInstant.Read(clock);
            if (onlineReply is null)
                return Failure(device, ReadStatus.Timeout, "The receiver did not confirm the keyboard's connection state.", publish);
            LofreeCompxProtocol.TryParseOnline(onlineReply.Report, out var online);
            if (online == false)
                // Hardware also returns 0 for an idle keyboard that is still switched
                // on in receiver mode. It revokes no connection fact and renews none.
                return IsKnown(device) ? Observation(device, onlineAt, ReadStatus.Missing) : null;
            if (online is null)
                return Failure(device, ReadStatus.Error, "The receiver returned an unknown keyboard connection status.", publish);

            var identityReply = await QueryAsync(channel, LofreeCompxProtocol.CreateIdentityQuery(),
                report => LofreeCompxProtocol.TryParseIdentity(report, out _), cancellationToken).ConfigureAwait(false);
            if (identityReply is null)
                return Failure(device, ReadStatus.Timeout, "The receiver did not confirm the keyboard model.", publish);
            LofreeCompxProtocol.TryParseIdentity(identityReply.Report, out var identity);
            if (!LofreeCompxProtocol.IsVerifiedIdentity(identity))
            {
                // A receiver can be paired with a different keyboard. Revoke the old
                // association explicitly, including during automatic scans with grace.
                var wasKnown = Forget(device);
                return wasKnown ? Observation(device, identityReply.ReceivedAt, ReadStatus.Unsupported, connected: false,
                    error: $"The receiver now reports an unverified keyboard identity ({identity.Cid}/{identity.Mid}/{identity.Type}).") : null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Remember(device);
            // Identity names the keyboard; only the preceding online reply proves
            // connection, and its time must not advance while later queries finish.
            publish(new(SourceId, [Observation(device, onlineAt, ReadStatus.Missing, connected: true)]));
            var batteryReply = await QueryAsync(channel, LofreeCompxProtocol.CreateBatteryQuery(),
                report => LofreeCompxProtocol.TryParseBattery(report, out _), cancellationToken).ConfigureAwait(false);
            if (batteryReply is null)
                return Failure(device, ReadStatus.Timeout, "The keyboard is connected, but no valid battery response arrived.", publish);
            LofreeCompxProtocol.TryParseBattery(batteryReply.Report, out var battery);
            var receivedAt = batteryReply.ReceivedAt;
            return Observation(device, receivedAt, ReadStatus.Success,
                new(battery.Percent, BatteryEvidence.ActiveRead, receivedAt.Utc, "LOFREE-COMPX:08/04",
                    Charging: battery.Charging, ReadAt: receivedAt.Utc, ReadElapsed: receivedAt.Elapsed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Failure(device, ReadStatus.Error, Describe(error), publish); }
    }

    private Task<HidReportReply?> QueryAsync(IHidChannel channel, byte[] request, Func<byte[], bool> accepts, CancellationToken cancellationToken) =>
        HidReportQuery.ReadAsync(channel, request, accepts, attemptTimeout, 2, cancellationToken, clock: clock);

    private DeviceObservation? Failure(HidInterface device, ReadStatus status, string error, Action<SourceBatch> publish)
    {
        if (IsKnown(device)) return Observation(device, ClockInstant.Read(clock), status, error: error);
        // Discovery failures remain diagnostic; an unverified receiver must not
        // manufacture a visible Lofree keyboard or fail another receiver's fields.
        publish(new(SourceId + ".discovery", [], Error: error, FailureStatus: status));
        return null;
    }

    private bool IsKnown(HidInterface device) { lock (knownGate) return knownReceivers.ContainsKey(Endpoint(device)); }
    private bool Forget(HidInterface device) { lock (knownGate) return knownReceivers.Remove(Endpoint(device)); }
    private void Remember(HidInterface device)
    {
        lock (knownGate)
        {
            knownReceivers[Endpoint(device)] = ++lastVerification;
            // Bound retained receiver identities without persisting private hardware IDs.
            if (knownReceivers.Count > 64) knownReceivers.Remove(knownReceivers.MinBy(pair => pair.Value).Key);
        }
    }

    private static DeviceObservation Observation(HidInterface device, ClockInstant at, ReadStatus status,
        BatterySample? battery = null, string? error = null, bool? connected = null) =>
        new(Endpoint(device), null, null, "Lofree Flow Lite100", DeviceKind.Keyboard, SourceId, at.Utc,
            Connected: connected, PathPresent: true, Battery: battery, Status: status, Error: error, ReceivedElapsed: at.Elapsed);

    private static string Endpoint(HidInterface device)
    {
        var container = WindowsDeviceMapping.NormalizeContainer(device.ContainerId);
        return container is not null ? $"hid:lofree:05ac:024f:receiver:{container}" : $"hid:lofree:path:{device.Path.Trim().ToLowerInvariant()}";
    }

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message} (0x{error.HResult:X8})";
}
