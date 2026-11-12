namespace BatteryManager.Application.Updates;

internal enum UpdatePhase { Idle, Checking, UpToDate, Available, Downloading, Downloaded, PreparingInstallation, Failed, Canceled, Cancelling, Stopping, Stopped }
internal enum UpdateOperationOutcome { Completed, Busy, Skipped, Canceled, Failed, Stopped }
internal sealed record UpdateOperationResult(UpdateOperationOutcome Outcome, string? Error = null)
{
    public bool Success => Outcome == UpdateOperationOutcome.Completed;
}

internal sealed record UpdateSnapshot(long Version, UpdatePhase Phase, bool AutoCheckEnabled,
    DateTimeOffset? LastCheckUtc, DateTimeOffset? LastSuccessfulCheckUtc,
    AvailableUpdate? Available = null, UpdatePackage? SelectedPackage = null,
    long DownloadedBytes = 0, string? DownloadedFile = null,
    string? Error = null, string? SettingsError = null, bool CanSaveSettings = true)
{
    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.PreparingInstallation or UpdatePhase.Cancelling;
}
