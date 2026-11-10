namespace BatteryManager.Core;

public enum DeviceKind { Unknown, Headphones, Keyboard, Mouse, Other }
public enum ConnectionState { Online, Recent, Pending, Offline }
public enum BatteryEvidence { SystemCache, SourceTimestamp, ActiveRead }
public enum BatteryFreshness { Unknown, Current, Recent, Retained, Expired }
public enum ReadStatus { Success, Missing, Unsupported, Error, Timeout, Canceled }
public enum DeviceTransport { Unknown, Bluetooth, Receiver, Wired, Mixed }

public sealed record BatterySample(
    int Percent,
    BatteryEvidence Evidence,
    DateTimeOffset? ObservedAt = null,
    string Property = "Battery",
    bool? Charging = null,
    string Precision = "percent",
    string Component = "device",
    // Software read completion, independent of the source's measurement time.
    DateTimeOffset? ReadAt = null,
    // Captured with ReadAt at I/O completion; never renewed by replaying a batch.
    TimeSpan? ReadElapsed = null);

public sealed record DeviceObservation(
    string EndpointId,
    string? ContainerId,
    string? BluetoothAddress,
    string Name,
    DeviceKind Kind,
    string Source,
    DateTimeOffset ReceivedAt,
    bool? Connected = null,
    bool PathPresent = false,
    BatterySample? Battery = null,
    ReadStatus Status = ReadStatus.Success,
    string? Error = null,
    TimeSpan? ReceivedElapsed = null,
    // Verified by the adapter. Descriptive metadata never confirms a connection.
    DeviceTransport Transport = DeviceTransport.Unknown);

// A complete authoritative batch covers membership in its Source, not all devices.
public sealed record SourceBatch(
    string Source,
    IReadOnlyList<DeviceObservation> Observations,
    bool Complete = false,
    bool Authoritative = false,
    string? Error = null,
    ReadStatus? FailureStatus = null);

public sealed record BatteryReading(
    string EndpointId,
    string Source,
    int? Percent,
    BatteryFreshness Freshness,
    BatteryEvidence Evidence,
    DateTimeOffset? ObservedAt,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ExpiresAt,
    ReadStatus Attempt,
    string? Error,
    string Property,
    string Precision,
    string Component,
    bool? Charging,
    DateTimeOffset? AttemptedAt = null);

public sealed record DeviceState(
    string Id,
    string Name,
    DeviceKind Kind,
    ConnectionState Connection,
    bool Visible,
    int? BatteryPercent,
    BatteryFreshness Freshness,
    string? BatterySource,
    string Reason,
    IReadOnlyList<BatteryReading> Readings,
    string? LastError)
{
    // Includes attempted sources even when no battery sample has ever arrived.
    public IReadOnlyList<DeviceSourceAttempt> SourceAttempts { get; init; } = Array.Empty<DeviceSourceAttempt>();
    // Exact selected evidence; a source/percentage pair is not unique across
    // linked endpoints or multiple properties from the same source.
    public BatteryReading? SelectedReading { get; init; }
    // True only after a completed scan proves every endpoint explicitly absent
    // or disconnected. Natural evidence expiry is not proof of removal.
    public bool IsExplicitlyOffline { get; init; }
}

public sealed record DeviceSourceAttempt(
    string EndpointId, string Source, ReadStatus Status, string? Error,
    bool IsCurrentRound, bool Coarse, ConnectionState Connection,
    DeviceTransport Transport = DeviceTransport.Unknown);

public sealed record StateSnapshot(
    long Version,
    DateTimeOffset DecidedAt,
    IReadOnlyList<DeviceState> Devices)
{
    // Raw hardware identities remain independent of explicit user links. This
    // list is for managing those links; ordinary consumers use Devices.
    public IReadOnlyList<DeviceState> DetectedDevices { get; init; } = Array.Empty<DeviceState>();
}

public sealed record StateOptions(
    TimeSpan ConnectionLifetime,
    TimeSpan ConnectionGrace,
    TimeSpan BatteryLifetime,
    TimeSpan ActiveBatteryLifetime)
{
    public TimeSpan PendingVisibilityLifetime { get; init; } = TimeSpan.FromMinutes(15);

    public static StateOptions Default { get; } = new(
        TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(45));
}

public sealed record ScanContext(long Generation, bool Manual);

public interface IDeviceSource
{
    string Name { get; }

    // Completion proves physical work ended. Cancellation must not merely cancel an await wrapper.
    Task ReadAsync(ScanContext context, Action<SourceBatch> publish, CancellationToken cancellationToken);
}
