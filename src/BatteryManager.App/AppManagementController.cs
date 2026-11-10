using System.Diagnostics;
using BatteryManager.Application.Management;
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
    private readonly Func<UpdatePackage, string, bool, Task> install;
    private readonly CancellationTokenSource lifetime = new();
    private StartupState startupState;
    private Task automaticLoop = Task.CompletedTask;
    private string? actionError;
    private bool stopping;
    private Task? stopTask;
    private bool installing;

    public AppManagementController(MainWindow window, AppDataPaths paths, bool simulated,
        Func<UpdatePackage, string, bool, Task> install,
        bool testMode = false, LocalUpdateFixture? updateFixture = null, Action<string, int>? recordFixtureResponse = null)
    {
        this.window = window; this.install = install;
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
    public void Start() { if (simulation is null) automaticLoop = AutomaticChecksAsync(); }
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
    private async Task InstallAsync(bool confirmedForTest)
    {
        if (!window.CanManageApplication || stopping || installing) return;
        var snapshot = updates.Snapshot;
        if (snapshot.Phase != UpdatePhase.Downloaded || snapshot.SelectedPackage is not { Kind: UpdatePackageKind.Installer } package || snapshot.DownloadedFile is not { } file)
            return;
        if (confirmedForTest && simulation is null && !localUpdateFixture)
            throw new InvalidOperationException("Confirmation injection requires a fictional scenario or an explicit local update fixture.");
        if (!confirmedForTest && !await window.Management.ConfirmInstallAsync(package.Version)) return;
        var current = updates.Snapshot;
        if (!window.CanManageApplication || stopping || current.Phase != UpdatePhase.Downloaded ||
            current.SelectedPackage != package || current.DownloadedFile != file) return;
        installing = true; window.Management.SetPreparingInstallation(true);
        try { await install(package, file, simulation is not null); }
        catch (Exception error) { actionError = error.Message; Render(); }
        finally { installing = false; if (!stopping) window.Management.SetPreparingInstallation(false); }
    }
    private async Task AutomaticChecksAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                if (updates.ShouldCheckAutomatically()) await updates.CheckAsync(automatic: true, lifetime.Token);
                await Task.Delay(TimeSpan.FromHours(1), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private void UpdateChanged(UpdateSnapshot snapshot)
    {
        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (stopping) return;
            window.Management.Apply(updates.Snapshot, startupState, simulation is not null, actionError);
            window.SetUpdateAvailable(updates.Snapshot.Available?.Version);
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
        await updates.CancelAndDrainAsync(); await automaticLoop;
        (client as IDisposable)?.Dispose(); lifetime.Dispose();
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
