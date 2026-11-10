namespace BatteryManager.Application.Management;

internal sealed record ManagementSettings(bool AutoCheckUpdates = true,
    DateTimeOffset? LastCheckUtc = null, DateTimeOffset? LastSuccessfulCheckUtc = null);

internal sealed record ManagementStoreResult(bool Success, ManagementSettings Settings, string? Error = null, bool CanSave = true);

internal interface IManagementStore
{
    ManagementStoreResult Load();
    Task<ManagementStoreResult> SetAutomaticChecksAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<ManagementStoreResult> RecordCheckAttemptAsync(DateTimeOffset at, CancellationToken cancellationToken = default);
    Task<ManagementStoreResult> RecordSuccessfulCheckAsync(DateTimeOffset at, CancellationToken cancellationToken = default);
}
