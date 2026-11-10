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
    private readonly AppDataPaths paths;
    private readonly UpdateCoordinator updates;
    private readonly IReleaseUpdateClient client;
    private readonly StartupService startup;
    private readonly ManagementSimulation? simulation;
    private readonly bool testMode;
    private readonly bool localUpdateFixture;
    private readonly Func<UpdatePackage, string, bool, Task> install;
    private readonly Func<string, Task<string?>> importSettings;
    private readonly CancellationTokenSource lifetime = new();
    private StartupState startupState;
    private Task automaticLoop = Task.CompletedTask;
    private string? actionError;
    private bool stopping;
    private Task? stopTask;
    private bool importing;
    private bool installing;
    private Windows.Foundation.IAsyncOperation<Windows.Storage.StorageFolder>? folderSelection;

    public AppManagementController(MainWindow window, AppDataPaths paths, bool simulated,
        Func<UpdatePackage, string, bool, Task> install, Func<string, Task<string?>> importSettings,
        bool testMode = false, LocalUpdateFixture? updateFixture = null, Action<string, int>? recordFixtureResponse = null)
    {
        this.window = window; this.paths = paths; this.install = install; this.importSettings = importSettings;
        if (updateFixture is not null && (!testMode || simulated)) throw new ArgumentException("A local update fixture requires a real-data test-pipe session.");
        this.testMode = testMode; localUpdateFixture = updateFixture is not null;
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
        window.Management.OpenFolder = OpenDownloadFolder;
        window.Management.OpenSystemStartup = OpenSystemStartup;
        window.Management.Import = ImportAsync;
        window.Management.EnableImport(paths.IsInstalled);
        Render();
    }
    public void Start() { if (simulation is null) automaticLoop = AutomaticChecksAsync(); }
    public void RefreshStartup() { if (!stopping) { startupState = startup.Read(); Render(); } }
    private async Task CheckAsync() { SetResult(await updates.CheckAsync()); Render(); }
    private async Task DownloadAsync()
    {
        if (!window.CanManageApplication || stopping) return;
        SetResult(await updates.DownloadAsync(paths.IsInstalled || simulation is not null ? UpdatePackageKind.Installer : UpdatePackageKind.Portable));
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
    private void OpenDownloadFolder()
    {
        if (simulation is not null || stopping) return;
        var snapshot = updates.Snapshot;
        if (snapshot.Phase != UpdatePhase.Downloaded || snapshot.DownloadedFile is null) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(Path.GetDirectoryName(snapshot.DownloadedFile)!); Process.Start(start)?.Dispose();
    }
    private void OpenSystemStartup()
    {
        if (simulation is null && !stopping)
            Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true })?.Dispose();
    }
    private async Task ImportAsync()
    {
        if (stopping || importing || simulation is not null || !window.CanManageApplication || !paths.IsInstalled) return;
        if (File.Exists(paths.SettingsPath))
        { actionError = UiText.Pick("当前安装版已有配置，未覆盖现有设置。", "This installation already has settings. They were not replaced."); Render(); return; }
        importing = true;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            picker.FileTypeFilter.Add("*");
            folderSelection = picker.PickSingleFolderAsync();
            var folder = await folderSelection;
            if (folder is null || stopping || !window.CanManageApplication) return;
            await ApplyImportAsync(folder.Path);
        }
        catch (OperationCanceledException) when (stopping) { }
        finally { folderSelection = null; importing = false; }
    }
    private async Task ImportDirectoryForTestAsync(string? directory)
    {
        if (!testMode || simulation is not null) throw new InvalidOperationException("Directory import requires a real-data test-pipe session.");
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Provide the absolute portable settings directory.");
        if (stopping || importing || !window.CanManageApplication || !paths.IsInstalled) return;
        if (File.Exists(paths.SettingsPath))
        { actionError = UiText.Pick("当前安装版已有配置，未覆盖现有设置。", "This installation already has settings. They were not replaced."); Render(); return; }
        importing = true;
        try { await ApplyImportAsync(Path.GetFullPath(directory)); }
        finally { importing = false; }
    }
    private async Task ApplyImportAsync(string directory)
    {
        actionError = await importSettings(directory);
        if (!stopping) { window.Management.ShowImportResult(actionError is null); Render(); }
    }
    public async Task ApplyTestActionAsync(string action, string? value)
    {
        if (action == "management-import-directory") { await ImportDirectoryForTestAsync(value); return; }
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
        folderSelection?.Cancel();
        await lifetime.CancelAsync();
        await updates.CancelAndDrainAsync(); await automaticLoop;
        (client as IDisposable)?.Dispose(); lifetime.Dispose();
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
