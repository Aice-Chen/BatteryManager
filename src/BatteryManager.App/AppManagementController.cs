using System.Diagnostics;
using BatteryManager.Application.Management;
using BatteryManager.Application.Installation;
using BatteryManager.Application.Startup;
using BatteryManager.Application.Updates;
using BatteryManager.App.Platform;

namespace BatteryManager.App;

// Coordinates management services and the settings panel. App owns only its lifetime
// and the final installation handoff after normal shutdown has drained.
internal sealed class AppManagementController : IAsyncDisposable
{
    private readonly MainWindow window;
    private readonly UpdateCoordinator updates;
    private readonly IReleaseUpdateClient client;
    private readonly StartupService startup;
    private readonly ManagementSimulation? simulation;
    private readonly bool localUpdateFixture;
    private readonly Func<UpdateCoordinator.InstallationLease, bool, Task> install;
    private readonly AppDataPaths paths;
    private readonly Action<string>? recordCacheCleanup;
    private readonly CancellationTokenSource lifetime = new();
    private StartupState startupState;
    private Task automaticLoop = Task.CompletedTask;
    private Task cacheCleanup = Task.CompletedTask;
    private string? actionError;
    private bool stopping;
    private Task? stopTask;
    private bool installing;

    public AppManagementController(MainWindow window, AppDataPaths paths, bool simulated,
        Func<UpdateCoordinator.InstallationLease, bool, Task> install,
        bool testMode = false, LocalUpdateFixture? updateFixture = null, Action<string, int>? recordFixtureResponse = null,
        Action<string>? recordCacheCleanup = null)
    {
        this.window = window; this.install = install; this.paths = paths; this.recordCacheCleanup = recordCacheCleanup;
        if (updateFixture is not null && (!testMode || simulated)) throw new ArgumentException("A local update fixture requires a real-data test-pipe session.");
        localUpdateFixture = updateFixture is not null;
        simulation = simulated ? new() : null;
        client = simulation is null ? new ReleaseUpdateClient(updateFixture?.CreateHandler(recordFixtureResponse)) : simulation;
        IManagementStore store = simulation is null ? new ManagementStore(paths.ManagementPath) : simulation;
        startup = new(simulation is null ? new WindowsStartupRegistry() : simulation,
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BatteryManager.App.exe"));
        startupState = startup.Read();
        updates = new(AppBuildInfo.Version, paths.UpdatesDirectory, client, store);
        updates.Changed += UpdateChanged;
        window.Management.ChangeStartup = ChangeStartupAsync;
        window.Management.ChangeAutomatic = async enabled => { SetResult(await updates.SetAutomaticChecksAsync(enabled)); Render(); };
        window.Management.Check = CheckAsync;
        window.Management.Download = DownloadAsync;
        window.Management.Cancel = updates.CancelCurrentAsync;
        window.Management.Install = () => InstallAsync(false);
        window.Management.OpenSystemStartup = OpenSystemStartup;
        Render();
    }
    public void Start()
    {
        // Local fixtures are driven explicitly; developer/scenario runs never self-install.
        if (simulation is null && paths.IsInstalled && !localUpdateFixture)
        {
            cacheCleanup = CleanupCacheAsync();
            automaticLoop = AutomaticChecksAsync();
        }
    }
    private async Task CleanupCacheAsync()
    {
        try
        {
            var result = await UpdateCache.CleanupAsync(paths.UpdatesDirectory, AppBuildInfo.Version, lifetime.Token);
            if (result.Error is not null || result.RemainingFiles.Count > 0)
                recordCacheCleanup?.Invoke($"Remaining: {string.Join(", ", result.RemainingFiles)}; {result.Error}");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { recordCacheCleanup?.Invoke(error.Message); }
    }
    public void RefreshStartup() { if (!stopping) { startupState = startup.Read(); Render(); } }
    private async Task CheckAsync() { SetResult(await updates.CheckAsync()); Render(); }
    private async Task DownloadAsync()
    {
        if (!window.CanManageApplication || stopping) return;
        SetResult(await updates.DownloadAsync(UpdatePackageKind.Installer));
        Render();
    }
    private Task ChangeStartupAsync(bool enabled)
    {
        if (!window.CanManageApplication || stopping) return Task.CompletedTask;
        var result = startup.SetEnabled(enabled, startupState);
        startupState = result.State; actionError = result.Error; Render(); return Task.CompletedTask;
    }
    private async Task InstallAsync(bool confirmedForTest, bool automatic = false)
    {
        if ((automatic ? !window.CanUpdateAutomatically : !window.CanManageApplication) || stopping || installing) return;
        var snapshot = updates.Snapshot;
        if (snapshot.Phase != UpdatePhase.Downloaded || snapshot.SelectedPackage is not { Kind: UpdatePackageKind.Installer } package || snapshot.DownloadedFile is not { } file)
            return;
        if (confirmedForTest && simulation is null && !localUpdateFixture)
            throw new InvalidOperationException("Confirmation injection requires a fictional scenario or an explicit local update fixture.");
        if (!automatic && !confirmedForTest && !await window.Management.ConfirmInstallAsync(package.Version)) return;
        var current = updates.Snapshot;
        if ((automatic ? !window.CanUpdateAutomatically : !window.CanManageApplication) || stopping || current.Phase != UpdatePhase.Downloaded ||
            current.SelectedPackage != package || current.DownloadedFile != file) return;
        await using var preparation = updates.TryPrepareInstallation(automatic);
        if (preparation is null) return;
        installing = true; window.Management.SetPreparingInstallation(true);
        try { await install(preparation, simulation is not null); }
        catch (OperationCanceledException) when (preparation.Token.IsCancellationRequested) { }
        catch (Exception error) { actionError = error.Message; Render(); }
        finally { installing = false; if (!stopping) window.Management.SetPreparingInstallation(false); }
    }
    private async Task AutomaticChecksAsync(TimeSpan? startupDelay = null)
    {
        try
        {
            await Task.Delay(startupDelay ?? TimeSpan.FromSeconds(30), lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                await RunAutomaticOnceAsync();
                await Task.Delay(TimeSpan.FromHours(1), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private async Task RunAutomaticOnceAsync()
    {
        if (stopping || installing || !window.CanUpdateAutomatically || !updates.ShouldCheckAutomatically()) return;
        SetResult(await updates.CheckAsync(automatic: true, lifetime.Token));
        Render();
        // Installation stops this controller. It must not be a child awaited by automaticLoop.
        if (!stopping && updates.Snapshot.Phase == UpdatePhase.Downloaded)
            _ = InstallAsync(confirmedForTest: false, automatic: true);
    }
    private void UpdateChanged(UpdateSnapshot snapshot)
    {
        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (stopping) return;
            window.Management.Apply(updates.Snapshot, startupState, simulation is not null, actionError);
            // With automatic updates on, a new version installs by itself; the device page shows no notice.
            window.SetUpdateAvailable(updates.Snapshot.AutoCheckEnabled ? null : updates.Snapshot.Available?.Version);
        });
    }
    private void Render() { if (!stopping) window.Management.Apply(updates.Snapshot, startupState, simulation is not null, actionError); }
    private void SetResult(UpdateOperationResult result) => actionError = result.Error;
    private void OpenSystemStartup()
    {
        if (simulation is null && !stopping)
            Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true })?.Dispose();
    }
    public async Task ApplyTestActionAsync(string action, string? value)
    {
        if (simulation is null && !localUpdateFixture) throw new InvalidOperationException("Management test actions require a fictional scenario or an explicit local update fixture.");
        switch (action)
        {
            case "management-check": await CheckAsync(); break;
            case "management-download": await DownloadAsync(); break;
            case "management-download-start": _ = DownloadAsync(); break;
            case "management-cancel": await updates.CancelCurrentAsync(); Render(); break;
            case "management-auto": SetResult(await updates.SetAutomaticChecksAsync(value == "on")); Render(); break;
            case "management-auto-toggle": window.Management.ToggleAutomaticForTest(value == "on"); break;
            case "management-auto-run": await RunAutomaticOnceAsync(); break;
            case "management-auto-run-start": _ = RunAutomaticOnceAsync(); break;
            case "management-auto-loop-start":
                if (!automaticLoop.IsCompleted) throw new InvalidOperationException("The automatic update loop is already running.");
                automaticLoop = AutomaticChecksAsync(TimeSpan.Zero); break;
            case "management-startup":
                if (simulation is null) throw new InvalidOperationException("Startup test changes require a fictional scenario.");
                await ChangeStartupAsync(value == "on"); break;
            case "management-block-startup":
                if (simulation is null) throw new InvalidOperationException("Startup test changes require a fictional scenario.");
                simulation.BlockStartup(); RefreshStartup(); break;
            case "management-network":
                if (simulation is null) throw new InvalidOperationException("Synthetic update modes require a fictional scenario.");
                simulation.Mode = value is "none" or "error" or "available" ? value : throw new ArgumentException("Unknown network fixture."); break;
            case "management-install-start": await InstallAsync(true); break;
            case "management-install-open": _ = InstallAsync(false); break;
            case "management-install-cancel": window.Management.HideConfirmation(); break;
            default: throw new ArgumentException("Unknown management action.");
        }
    }
    public Task StopAsync() => stopTask ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        stopping = true; updates.Changed -= UpdateChanged; window.Management.Stop();
        await lifetime.CancelAsync();
        await updates.CancelAndDrainAsync(); await automaticLoop; await cacheCleanup;
        (client as IDisposable)?.Dispose(); lifetime.Dispose();
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
