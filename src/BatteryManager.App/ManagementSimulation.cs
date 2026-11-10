using BatteryManager.Application.Management;
using BatteryManager.Application.Startup;
using BatteryManager.Application.Updates;

namespace BatteryManager.App;

// Scenario mode keeps all management settings, startup entries and downloads fictional.
internal sealed class ManagementSimulation : IManagementStore, IStartupRegistry, IReleaseUpdateClient
{
    private ManagementSettings settings = new();
    private StartupRegistrySnapshot startup = new(false, true, null, StartupApproval.NotRecorded, "0");
    private int revision;
    public string Mode { get; set; } = "available";
    public bool NetworkUsed => false;
    public int StartupWrites { get; private set; }
    public ManagementStoreResult Load() => new(true, settings);
    public Task<ManagementStoreResult> SetAutomaticChecksAsync(bool enabled, CancellationToken cancellationToken = default)
    { settings = settings with { AutoCheckUpdates = enabled }; return Task.FromResult(Load()); }
    public Task<ManagementStoreResult> RecordCheckAttemptAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
    { settings = settings with { LastCheckUtc = at }; return Task.FromResult(Load()); }
    public Task<ManagementStoreResult> RecordSuccessfulCheckAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
    { settings = settings with { LastSuccessfulCheckUtc = at }; return Task.FromResult(Load()); }
    public StartupRegistrySnapshot Read() => startup;
    public bool TrySetRunValue(StartupRegistrySnapshot expected, string? command)
    {
        if (expected != startup) return false;
        startup = startup with { ValuePresent = command is not null, Command = command, Revision = (++revision).ToString() };
        StartupWrites++; return true;
    }
    public void BlockStartup() => startup = startup with { Approval = StartupApproval.Blocked, Revision = (++revision).ToString() };
    public async Task<AvailableUpdate?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        await Task.Delay(50, cancellationToken);
        if (Mode == "error") throw new HttpRequestException("模拟网络不可用");
        if (Mode == "none") return null;
        UpdatePackage[] packages = [new("9.9.9", UpdatePackageKind.Installer, "BatteryManager-9.9.9-win-x64-setup.exe",
            1024, new string('a', 64), new Uri("https://github.com/Aice-Chen/BatteryManager/releases/download/v9.9.9/fixture"))];
        return new("9.9.9", new("https://github.com/Aice-Chen/BatteryManager/releases/tag/v9.9.9"), packages);
    }
    public async Task<string> DownloadAsync(UpdatePackage package, string cacheDirectory, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        for (var i = 1; i <= 8; i++) { await Task.Delay(80, cancellationToken); progress?.Report(i * 128); }
        return Path.Combine(cacheDirectory, package.FileName); // No file is written or executable launched.
    }
}
