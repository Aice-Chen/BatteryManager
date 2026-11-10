using BatteryManager.Core;
using BatteryManager.WindowsNative;
using Microsoft.UI.Xaml;

namespace BatteryManager.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private BatterySession? _session;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private TrayPanel? _panel;
    private ThemeCoordinator? _themes;
    private AppPreferences _settings = AppPreferences.Default;
    private PowerNotifications? _power;
    private AppTestHost? _testHost;
    private AppDiagnosticLog? _diagnostics;
    private TimeProvider _clock = TimeProvider.System;
    private AppTestClock? _testClock;
    private AppScenarios? _scenarios;
    private TrayPreferencesStore? _preferencesStore;
    private IReadOnlyList<TrayPreference> _priorities = [];
    private string? _preferencesWarning;
    private Task<string?>? _savePreferences;
    // The settings just before and just after the last merge change, for "撤销". Cleared once undone.
    private (AppPreferences Before, AppPreferences After)? _linkUndo;
    private PrioritySaveTestControl? _prioritySaveTest;
    private bool _drainingPreferences;
    private readonly CancellationTokenSource _updates = new();
    private bool _quitting;
    private SessionSnapshot? _pendingSnapshot;
    private long _lastPresentationVersion = -1;
    private int _renderQueued;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => RecordCrash(args.Exception, args.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => RecordCrash(args.ExceptionObject as Exception, "AppDomain unhandled exception");
    }

    private static void RecordCrash(Exception? exception, string message)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
            System.IO.Directory.CreateDirectory(path);
            var file = System.IO.Path.Combine(path, "last-error.txt");
            System.IO.File.WriteAllText(file, $"{DateTimeOffset.UtcNow:O}\n{message}\n{exception}");
        }
        catch { }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (arguments.Contains("--quit", StringComparer.Ordinal))
        {
            Environment.ExitCode = arguments.Length == 2 && arguments[0] == "--quit" &&
                uint.TryParse(arguments[1], out var targetProcessId) && targetProcessId > 0 &&
                await SingleInstance.RequestQuitAsync(targetProcessId) ? 0 : 2;
            Exit();
            return;
        }
        string? testPipe;
        string? scenarioName;
        try
        {
            testPipe = AppTestHost.PipeName(arguments);
            scenarioName = AppScenarios.NameFromArguments(arguments);
        }
        catch (Exception error)
        {
            RecordCrash(error, "Invalid app test arguments");
            Environment.ExitCode = 2;
            Exit();
            return;
        }
        _instance = new SingleInstance();
        if (!_instance.IsPrimary)
        {
            await SingleInstance.ActivateExistingAsync();
            _instance.Dispose();
            Exit();
            return;
        }

        if (scenarioName is not null)
        {
            _scenarios = new AppScenarios();
            _clock = _scenarios.Clock;
            if (testPipe is not null) _prioritySaveTest = new();
        }
        if (testPipe is not null)
        {
            if (_scenarios is null) _clock = _testClock = new AppTestClock();
            try
            {
                var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
                System.IO.Directory.CreateDirectory(directory);
                var path = System.IO.Path.Combine(directory, $"diagnostic-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.jsonl");
                _diagnostics = new AppDiagnosticLog(_scenarios?.HidIo ?? new WindowsHidIo(), path);
            }
            catch (Exception error) { RecordCrash(error, "Diagnostic log startup failed"); }
        }
        if (_scenarios is null)
        {
            _preferencesStore = new TrayPreferencesStore(System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json"));
            var loaded = _preferencesStore.Load();
            _priorities = loaded.Priorities;
            _preferencesWarning = loaded.Error;
            _settings = loaded.Preferences ?? AppPreferences.Default;
        }
        _session = new BatterySession(_scenarios?.CreateSource(_diagnostics) ?? DeviceSources.CreateDefault(_diagnostics, _clock), _clock);
        _session.SetDeviceLinks(_settings.DeviceLinks);
        _window = new MainWindow(_session, scenarioMode: _scenarios is not null);
        // Win32 callbacks must return before re-entering WinUI window activation/layout.
        _tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(_window),
            () => _window.DispatcherQueue.TryEnqueue(_window.ShowWindow),
            () => _window.DispatcherQueue.TryEnqueue(() => _ = _window.RefreshAsync()),
            () => _window.DispatcherQueue.TryEnqueue(() => _ = QuitAsync()),
            () => _window.DispatcherQueue.TryEnqueue(OpenTrayPreferences), scenarioMode: _scenarios is not null);
        _instance.ExitRequested = () => _window.DispatcherQueue.TryEnqueue(() => _ = QuitAsync());
        _instance.Attach(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        try { _power = new PowerNotifications(WinRT.Interop.WindowNative.GetWindowHandle(_window), OnPowerChanged, RecordCrash); }
        catch (Exception error) { RecordCrash(error, "Power notification startup failed"); }
        _window.CanHideToTray = () => _tray.IsAvailable;
        _window.ExitRequested = () => _ = QuitAsync();
        _window.TrayPreferencesRequested = OpenTrayPreferences;
        _window.SaveColorModeRequested = SaveColorModeAsync;
        _window.SaveCustomizationRequested = SaveCustomizationAsync;
        _window.MergeRequested = SaveDeviceLinkAsync;
        _window.UnmergeRequested = RemoveDeviceLinkAsync;
        _window.UndoMergeRequested = UndoDeviceLinkAsync;
        // Tray priority is edited in place on the settings page and saved on every change.
        var aliases = _settings.DeviceCustomizations.Where(c => c.Name is not null).ToDictionary(c => c.DeviceId, c => c.Name!, StringComparer.Ordinal);
        _window.TrayPriority.Initialize(_priorities.Select(p => p with { Name = aliases.GetValueOrDefault(p.DeviceId, p.Name) }).ToArray(),
            _preferencesWarning, SaveTrayPreferencesAsync);
        _themes = new ThemeCoordinator(_window.DispatcherQueue, ApplyThemes) { MainMode = _settings.MainColorMode };
        _panel = new TrayPanel(_tray, _themes, _window.ShowWindow,
            () => { _window.ShowWindow(); _ = _window.ApplyUiTestActionAsync("settings", null, null); },
            () => _ = _window.RefreshAsync(), _scenarios is not null);
        _tray.TogglePanel = keyboard => _window.DispatcherQueue.TryEnqueue(() => _panel.ToggleFromTray(keyboard));
        _tray.ThemeChanged = _themes.QueueRefresh;
        ApplyThemes();
        RenderPresentation(_session.Snapshot);
        _window.Activate();
        _ = ObserveAsync(_session, _updates.Token);
        if (_scenarios is null)
        {
            _session.StartPolling();
            _ = _session.RefreshAsync(manual: false);
        }
        else
        {
            try { await ApplyScenarioAsync(scenarioName!, CancellationToken.None); }
            catch (Exception error) { RecordCrash(error, "Scenario startup failed"); await QuitAsync(); return; }
        }
        if (_quitting) return;
        if (testPipe is not null)
        {
            try
            {
                _testHost = new AppTestHost(testPipe, _window.DispatcherQueue,
                    () => new(DateTimeOffset.UtcNow, _session.Snapshot, _window.CaptureTestView(), _diagnostics?.FilePath,
                        _tray.CaptureTestView(), new(_priorities, _preferencesWarning, _window.TrayPriority.CaptureDraft()), _power?.Capture(), _testClock?.Capture(),
                        _scenarios?.Capture(), AppManagedMemory.Capture(), AppBuildInfo.InformationalVersion,
                        _panel?.Capture(), _themes?.Capture(), _window.CaptureUiState(), _settings, _window.CaptureMergeState()),
                    token => _window.RefreshAsync(token), QuitAsync, RecordCrash, ApplyPreferencesTestActionAsync,
                    action => (_power ?? throw new InvalidOperationException("Power notifications are unavailable.")).InjectForTest(action),
                    action => (_testClock ?? throw new InvalidOperationException("Test clock is unavailable.")).SetOffset(action), ApplyScenarioAsync, ApplyUiActionAsync);
            }
            catch (Exception error)
            {
                RecordCrash(error, "App test host startup failed");
                await QuitAsync();
            }
        }
    }

    private void OnPowerChanged(bool suspended)
    {
        if (_quitting || _session is null) return;
        if (suspended) _session.Suspend();
        else _ = ResumeAfterPowerChangeAsync();
        // Power messages have a short OS deadline. Disk logging must happen only
        // after the native callback returns, even in diagnostic mode.
        if (_power is not null && _diagnostics is not null)
        {
            var notification = _power.Capture();
            _window?.DispatcherQueue.TryEnqueue(() => _diagnostics?.Record("power-event", notification));
        }
    }

    private async Task ApplyScenarioAsync(string name, CancellationToken cancellationToken)
    {
        var scenarios = _scenarios ?? throw new InvalidOperationException("Start with --scenario to use fictional device examples.");
        if (_quitting || _session is null) throw new InvalidOperationException("The app is not available for a scenario change.");
        if (_window?.CaptureMergeState().IsSaving == true) throw new InvalidOperationException("Wait for the merge save before changing scenarios.");
        AppScenarios.Validate(name);
        _window?.ShowScenario(name, changing: true);
        try { await scenarios.ApplyAsync(name, _session, cancellationToken); }
        finally { _window?.ShowScenario(scenarios.Capture().Name, changing: false); }
    }

    private async Task ResumeAfterPowerChangeAsync()
    {
        try { await _session!.ResumeAsync(); }
        catch (Exception error) { RecordCrash(error, "Resume refresh failed"); }
    }

    // The tray menu's "托盘设置" and the settings entry open the settings page at the tray priority.
    private void OpenTrayPreferences()
    {
        if (_quitting || _window is null || _session is null) return;
        _window.ShowWindow();
        _window.ShowTrayPriority();
    }

    private Task<string?> SaveTrayPreferencesAsync(IReadOnlyList<TrayPreference> priorities, bool replaceInvalid, bool clearSuspendedPriorities)
    {
        if (_quitting && !_drainingPreferences) return Task.FromResult<string?>(UiText.Pick("程序正在退出，请稍后重试。", "The app is quitting. Try again after restarting."));
        if (_savePreferences is { IsCompleted: false }) return Task.FromResult<string?>(UiText.Pick("正在保存，请稍后重试", "Saving. Please try again shortly."));
        return _savePreferences = SaveTrayPreferencesCoreAsync(priorities, replaceInvalid, clearSuspendedPriorities);
    }

    private async Task<string?> SaveTrayPreferencesCoreAsync(IReadOnlyList<TrayPreference> priorities, bool replaceInvalid, bool clearSuspendedPriorities)
    {
        try
        {
            if (_prioritySaveTest is not null)
            {
                _diagnostics?.Record("priority-save-started", new { simulated = true, order = priorities.Select(p => p.DeviceId).ToArray() });
                if (await _prioritySaveTest.BeforeSaveAsync() is { } injectedError)
                {
                    _diagnostics?.Record("priority-save-failed", new { simulated = true, error = injectedError });
                    return injectedError;
                }
            }
            if (_scenarios is not null)
            {
                if (replaceInvalid) _settings = AppPreferences.Default;
                if (clearSuspendedPriorities) _settings = AppPreferenceLinks.ClearPriorities(_settings);
                _priorities = Array.AsReadOnly(priorities.ToArray());
                _settings = _settings with { Priorities = _priorities };
            }
            else
            {
                var saved = await _preferencesStore!.SaveAsync(priorities, replaceInvalid, clearSuspendedPriorities: clearSuspendedPriorities);
                if (!saved.Success) return saved.Error ?? UiText.Pick("保存失败，请重试。", "Could not save. Try again.");
                _priorities = saved.Priorities;
                _settings = saved.Preferences ?? _settings;
            }
            _preferencesWarning = null;
            _session?.SetDeviceLinks(_settings.DeviceLinks);
            if (_prioritySaveTest is not null)
                _diagnostics?.Record("priority-save-applied", new { simulated = true, order = _priorities.Select(p => p.DeviceId).ToArray() });
            if (!_quitting && _session is not null)
            {
                try { ApplyThemes(); }
                catch (Exception error) { RecordCrash(error, "Tray update after saving settings failed"); }
            }
            return null;
        }
        catch (Exception error)
        {
            RecordCrash(error, "Tray settings save failed");
            return UiText.Pick("保存未完成，请稍后重试。", "Saving did not complete. Please try again.");
        }
    }

    private async Task ApplyPreferencesTestActionAsync(string action, string? deviceId)
    {
        if (_quitting) throw new InvalidOperationException("The app is quitting.");
        if (action == "open") { OpenTrayPreferences(); return; }
        var dialog = _window?.TrayPriority ?? throw new InvalidOperationException("The main window is unavailable.");
        bool changed;
        switch (action)
        {
            case "add": changed = dialog.AddDevice(deviceId ?? ""); break;
            case "up": changed = dialog.MoveDevice(deviceId ?? "", -1); break;
            case "down": changed = dialog.MoveDevice(deviceId ?? "", 1); break;
            case "reorder": changed = dialog.ReorderDevices(System.Text.Json.JsonSerializer.Deserialize<string[]>(deviceId ?? "[]") ?? []); break;
            case "reorder-remove-insert":
            case "reorder-remove-insert-cancel":
                if (_scenarios is null) throw new InvalidOperationException("Collection-operation checks require a fictional scenario.");
                changed = dialog.ReorderRemoveInsertForTest(System.Text.Json.JsonSerializer.Deserialize<string[]>(deviceId ?? "[]") ?? [],
                    commit: action == "reorder-remove-insert"); break;
            case "drag-begin":
            case "drag-remove-insert":
            case "drag-end":
            case "drag-cancel":
                if (_prioritySaveTest is null) throw new InvalidOperationException("Stepped collection-operation checks require both a fictional scenario and a test pipe.");
                changed = action switch
                {
                    "drag-begin" => dialog.BeginDragForTest(deviceId),
                    "drag-remove-insert" => dialog.ReorderDuringDragForTest(System.Text.Json.JsonSerializer.Deserialize<string[]>(deviceId ?? "[]") ?? []),
                    _ => dialog.EndDragForTest(action == "drag-end")
                };
                break;
            case "remove": changed = dialog.RemoveDevice(deviceId ?? ""); break;
            case "reset": changed = dialog.RequestReset(); break;
            case "confirm-reset": changed = dialog.ConfirmReset(); break;
            case "reset-cancel": changed = dialog.CancelResetConfirmation(); break;
            // Every change is saved at once; "save" waits for the save in progress.
            case "save":
                if (!await dialog.SaveDraftAsync()) throw new InvalidOperationException(dialog.CaptureDraft().Error ?? "The order is not saved.");
                return;
            default: throw new InvalidOperationException("Use open, add, up, down, reorder, remove, reset, confirm-reset, reset-cancel or save.");
        }
        if (!changed) throw new InvalidOperationException("That change is not available for this device or dialog state.");
    }

    private async Task ObserveAsync(BatterySession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var snapshot in session.Subscribe(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _pendingSnapshot, snapshot);
                ScheduleRender();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { RecordCrash(error, "Snapshot delivery failed"); }
    }

    private void ScheduleRender()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) != 0) return;
        var queued = false;
        try { queued = _window?.DispatcherQueue.TryEnqueue(RenderLatest) == true; }
        catch (Exception error) { RecordCrash(error, "Snapshot dispatch failed"); }
        finally
        {
            if (!queued) Interlocked.Exchange(ref _renderQueued, 0);
        }
    }

    private void RenderLatest()
    {
        try
        {
            var snapshot = Interlocked.Exchange(ref _pendingSnapshot, null);
            if (!_quitting && snapshot is not null)
            {
                RenderPresentation(snapshot);
                if (_diagnostics is not null && _window is not null && snapshot.State.Version == _lastPresentationVersion)
                {
                    var view = _window.CaptureTestView();
                    _diagnostics.RecordView(new
                    {
                        snapshot.Generation, snapshot.Status, snapshot.IsRefreshing, snapshot.OutstandingOperations,
                        devices = snapshot.State.Devices.Where(device => device.Visible).Select(device => new
                        {
                            device.Id, device.Name, device.Connection, device.BatteryPercent,
                            device.Freshness, device.BatterySource, device.LastError
                        }).ToArray(),
                        view.Summary, view.RefreshEnabled, view.RefreshText, view.Cards
                    }, snapshot.State.Version, view.Version);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            if (!_quitting && Volatile.Read(ref _pendingSnapshot) is not null) ScheduleRender();
        }
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _drainingPreferences = true;
        _power?.Dispose();
        _themes?.Dispose();
        _panel?.HidePanel();
        _window?.PrepareToExit();
        _prioritySaveTest?.ReleaseForExit();
        try
        {
            if (_testHost is not null)
            {
                try { await _testHost.StopAsync(); }
                catch (Exception error) { RecordCrash(error, "App test host shutdown failed"); }
            }
            // The panel can have a newer accepted draft queued behind the current file write.
            // Stop new edits first, then allow that entire queue to finish during shutdown.
            if (_window is not null) await _window.TrayPriority.FlushPendingChangesAsync();
            if (_savePreferences is not null) await _savePreferences;
            if (_window is not null) await _window.FlushDeviceChangesAsync();
            _drainingPreferences = false;
            if (_prioritySaveTest is not null)
                _diagnostics?.Record("priority-save-drained", new { simulated = true, order = _priorities.Select(p => p.DeviceId).ToArray() });
            if (_session is not null) await _session.StopAndDrainAsync();
            _diagnostics?.Record("session-stopped", new { physicallyDrained = true });
        }
        finally
        {
            _drainingPreferences = false;
            _updates.Cancel();
            _diagnostics?.Dispose();
            _tray?.Dispose();
            _panel?.CloseForExit();
            _window?.CloseForExit();
            _instance?.Dispose();
            _updates.Dispose();
            Exit();
        }
    }

    private void RenderPresentation(SessionSnapshot snapshot)
    {
        if (snapshot.State.Version < _lastPresentationVersion) return;
        _lastPresentationVersion = snapshot.State.Version;
        var choices = _settings.DeviceCustomizations.ToDictionary(c => c.DeviceId, StringComparer.Ordinal);
        _window?.Merge.Update(snapshot.State.DetectedDevices, _settings.DeviceLinks, _settings.DeviceCustomizations);
        var displays = snapshot.State.Devices.Select(d => DeviceDisplayMapper.Map(d, choices.GetValueOrDefault(d.Id)))
            .OfType<DeviceDisplay>().Select(display => _window?.Merge.Enrich(display) ?? display).ToArray();
        _window?.SetPreferences(_settings.DeviceCustomizations, _settings.MainColorMode, _preferencesWarning);
        // Merge groups first: each card needs to know whether it is merged when it is built.
        _window?.Apply(snapshot, displays);
        _panel?.Apply(displays, snapshot);
        _tray?.Apply(snapshot, _priorities, displays, _themes?.WindowsDark == true);
        _window?.TrayPriority.UpdateDevices(NamedDevices(snapshot.State.Devices), displays);
        _window?.TrayPriority.SetCurrent(_tray?.SelectedDeviceId);
    }

    private Task<string?> SaveDeviceLinkAsync(DeviceLinkMember primary, DeviceLinkMember other) =>
        BeginDeviceLinkSave(primary, other, null);

    private Task<string?> RemoveDeviceLinkAsync(string primaryId) => BeginDeviceLinkSave(null, null, primaryId);

    private Task<string?> UndoDeviceLinkAsync() => _linkUndo is null
        ? Task.FromResult<string?>(UiText.Pick("没有可撤销的操作。", "There is nothing to undo."))
        : BeginDeviceLinkSave(null, null, null, undo: true);

    // Records present right now. A full merge group forgets its earliest connection that is not among them.
    private IReadOnlySet<string>? PresentDeviceIds()
    {
        var snapshot = _session?.Snapshot;
        // An incomplete or failed scan cannot authorize forgetting an old connection.
        // Natural evidence expiry is also not proof of absence; retain those raw records.
        if (snapshot is null || snapshot.IsRefreshing || snapshot.IsSuspended || snapshot.Status != "Completed") return null;
        return snapshot.State.DetectedDevices.Where(device => !device.IsExplicitlyOffline)
            .Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
    }

    private Task<string?> BeginDeviceLinkSave(DeviceLinkMember? primary, DeviceLinkMember? other, string? removeId, bool undo = false)
    {
        if (_quitting) return Task.FromResult<string?>(UiText.Pick("程序正在退出", "The app is quitting"));
        if (_preferencesWarning is not null) return Task.FromResult<string?>(_preferencesWarning);
        if (_scenarios?.Capture().Applying == true) return Task.FromResult<string?>(UiText.Pick("请等待演示场景切换完成。", "Wait for the scenario change to finish."));
        if (_savePreferences is { IsCompleted: false } || _window!.TrayPriority.CaptureDraft() is { IsSaving: true } or { IsDragging: true })
            return Task.FromResult<string?>(UiText.Pick("请等待当前设置操作完成后重试。", "Wait for the current settings change to finish."));
        _window.TrayPriority.IsEnabled = false;
        return _savePreferences = SaveDeviceLinkCoreAsync(primary, other, removeId, undo);
    }

    private async Task<string?> SaveDeviceLinkCoreAsync(DeviceLinkMember? primary, DeviceLinkMember? other, string? removeId, bool undo)
    {
        var committed = false;
        try
        {
            if (_prioritySaveTest is not null && await _prioritySaveTest.BeforeSaveAsync() is { } injectedError)
                return injectedError;
            var before = _settings;
            var restore = _linkUndo;
            var knownMembers = _window!.Merge.GroupsWithKnownTransports().SelectMany(group => group.Members).ToArray();
            if (undo && restore is null) return UiText.Pick("没有可撤销的操作。", "There is nothing to undo.");
            if (_scenarios is not null)
            {
                var result = undo ? AppPreferenceLinks.Restore(_settings, restore!.Value.After, restore.Value.Before)
                    : removeId is not null ? AppPreferenceLinks.Unlink(_settings, removeId)
                    : AppPreferenceLinks.Link(_settings, primary!, other!, PresentDeviceIds(), knownMembers);
                if (!result.Success) return result.Error;
                _settings = result.Preferences!;
            }
            else
            {
                var result = undo ? await _preferencesStore!.RestoreDeviceLinksAsync(restore!.Value.After, restore.Value.Before)
                    : removeId is not null ? await _preferencesStore!.UnlinkDevicesAsync(removeId)
                    : await _preferencesStore!.LinkDevicesAsync(primary!, other!, present: PresentDeviceIds(), knownMembers: knownMembers);
                if (!result.Success) return result.Error;
                before = result.PreviousPreferences ?? before;
                _settings = result.Preferences!;
            }
            committed = true;
            _linkUndo = undo ? null : (before, _settings);
            _priorities = _settings.Priorities;
            _session!.SetDeviceLinks(_settings.DeviceLinks);
            var aliases = _settings.DeviceCustomizations.Where(c => c.Name is not null).ToDictionary(c => c.DeviceId, c => c.Name!, StringComparer.Ordinal);
            _window!.TrayPriority.Initialize(_priorities.Select(p => p with { Name = aliases.GetValueOrDefault(p.DeviceId, p.Name) }).ToArray(),
                _preferencesWarning, SaveTrayPreferencesAsync);
            if (!_quitting) RenderPresentation(_session.Snapshot);
            return null;
        }
        catch (Exception error)
        {
            RecordCrash(error, "Device merge save failed");
            return committed ? UiText.Pick("已保存，但显示没有更新。请重新启动程序。", "Saved, but the display did not update. Restart the app.")
                : UiText.Get("SaveFailed");
        }
        finally { if (_window is not null) _window.TrayPriority.IsEnabled = !_quitting && _scenarios?.Capture().Applying != true; }
    }

    private IReadOnlyList<DeviceState> NamedDevices(IReadOnlyList<DeviceState> devices)
    {
        var names = _settings.DeviceCustomizations.Where(c => c.Name is not null).ToDictionary(c => c.DeviceId, c => c.Name!, StringComparer.Ordinal);
        return devices.Select(d => names.TryGetValue(d.Id, out var name) ? d with { Name = name } : d).ToArray();
    }

    private void ApplyThemes()
    {
        if (_themes is null || _window is null || _quitting) return;
        _themes.MainMode = _settings.MainColorMode;
        _window.ThemeRoot.RequestedTheme = _themes.MainTheme;
        var darkCaption = _themes.MainTheme == ElementTheme.Dark ? 1 : 0;
        _ = PanelNative.DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(_window), 20, ref darkCaption, sizeof(int));
        _window.RefreshTheme();
        _panel?.ApplyTheme();
        if (_session is not null) RenderPresentation(_session.Snapshot);
    }

    private async Task<string?> SaveColorModeAsync(MainColorMode mode)
    {
        if (_quitting) return UiText.Pick("程序正在退出", "The app is quitting");
        if (_preferencesWarning is not null) return _preferencesWarning;
        if (_savePreferences is { IsCompleted: false }) return UiText.Pick("正在保存", "Saving");
        return await (_savePreferences = SaveAppearanceCoreAsync(mode, null));
    }

    private async Task<string?> SaveCustomizationAsync(DeviceCustomization customization)
    {
        if (_quitting) return UiText.Pick("程序正在退出", "The app is quitting");
        if (_preferencesWarning is not null) return _preferencesWarning;
        if (_savePreferences is { IsCompleted: false }) return UiText.Pick("正在保存", "Saving");
        return await (_savePreferences = SaveAppearanceCoreAsync(null, customization));
    }

    private async Task<string?> SaveAppearanceCoreAsync(MainColorMode? mode, DeviceCustomization? customization)
    {
        try
        {
            if (_scenarios is not null)
            {
                if (mode is { } color) _settings = _settings with { MainColorMode = color };
                if (customization is not null)
                {
                    var updated = _settings.DeviceCustomizations.Where(c => c.DeviceId != customization.DeviceId).ToList();
                    var name = string.IsNullOrWhiteSpace(customization.Name) ? null : customization.Name.Trim();
                    if (name is not null || customization.Kind is not null) updated.Add(customization with { Name = name });
                    _settings = _settings with { DeviceCustomizations = updated.AsReadOnly() };
                }
            }
            else
            {
                var result = mode is { } color ? await _preferencesStore!.SaveColorModeAsync(color)
                    : await _preferencesStore!.SaveCustomizationAsync(customization!);
                if (!result.Success) return result.Error;
                _settings = result.Preferences!;
            }
            ApplyThemes();
            return null;
        }
        catch (Exception error)
        {
            RecordCrash(error, "Appearance settings save failed");
            return UiText.Pick("保存未完成，请稍后重试", "Saving did not complete. Please try again.");
        }
    }

    private async Task ApplyUiActionAsync(string action, string? value, string? deviceId)
    {
        if (_quitting) throw new InvalidOperationException("The app is quitting.");
        switch (action)
        {
            case "priority-save-test":
                if (_prioritySaveTest is null) throw new InvalidOperationException("Save controls require both a fictional scenario and a test pipe.");
                _prioritySaveTest.Configure(value ?? ""); break;
            case "window-hide":
            case "window-show":
                if (_prioritySaveTest is null) throw new InvalidOperationException("Window lifecycle checks require both a fictional scenario and a test pipe.");
                if (action == "window-hide") _window!.HideToTray(); else _window!.ShowWindow();
                break;
            case "capture-tray-glyphs":
                if (_scenarios is null) throw new InvalidOperationException("Glyph diagnostics require an isolated scenario.");
                await TrayGlyphDiagnostics.ExportAsync(Path.Combine(AppContext.BaseDirectory, "logs", "tray-glyphs")); break;
            case "animations":
                if (_scenarios is null) throw new InvalidOperationException("Animation overrides require an isolated scenario.");
                Motion.TestEnabled = value switch { "on" => true, "off" => false, "reset" => null, _ => throw new ArgumentException("Use on, off or reset.") }; break;
            case "resize-main":
                if (_scenarios is null) throw new InvalidOperationException("Window resize tests require an isolated scenario.");
                var dimensions = (value ?? "").Split(',');
                if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out var width) || !int.TryParse(dimensions[1], out var height)
                    || width is < 400 or > 2000 || height is < 400 or > 1600) throw new ArgumentException("Use a supported width,height in pixels.");
                _window!.ResizeForTest(width, height); break;
            case "read-delay":
                if (_scenarios is null || !int.TryParse(value, out var delay) || delay is < 0 or > 1000) throw new ArgumentException("Use a scenario I/O delay between 0 and 1000 ms.");
                _scenarios.SetReadDelay(delay); break;
            case "receiver-step":
                if (_scenarios is null || _session is null) throw new InvalidOperationException("Receiver steps require a fictional scenario.");
                await _scenarios.StepReceiverAsync(value ?? "", _session); break;
            case "connection-step":
                if (_scenarios is null || _session is null) throw new InvalidOperationException("Connection steps require a fictional scenario.");
                await _scenarios.StepConnectionAsync(value ?? "", _session); break;
            case "panel-open": _panel!.ShowPanel(); break;
            case "panel-open-keyboard": _panel!.ShowPanel(keyboard: true); break;
            case "panel-close": _panel!.HidePanel(true); break;
            case "panel-toggle": _panel!.Toggle(); break;
            case "panel-scroll":
                if (_scenarios is null || value is not ("top" or "bottom")) throw new ArgumentException("Use top or bottom in a fictional scenario.");
                _panel!.ScrollForTest(value == "bottom"); break;
            case "system-theme": _themes!.SetTestModes(value ?? "reset"); break;
            case "capture-main": await CaptureScenarioAsync(_window!.ThemeRoot, value); break;
            case "capture-dialog": await CaptureScenarioAsync(_window!.DialogForTest ?? throw new InvalidOperationException("Open a dialog first."), value); break;
            case "capture-tray-priority": await CaptureScenarioAsync(_window!.TrayPriority, value); break;
            case "capture-panel": await CaptureScenarioAsync(_panel!.ThemeRoot, value); break;
            case "capture-main-card": await CaptureScenarioAsync(_window!.CardForTest(deviceId ?? "") ?? throw new ArgumentException("Select a visible device."), value); break;
            case "capture-main-glyph": await CaptureScenarioAsync((_window!.CardForTest(deviceId ?? "") ?? throw new ArgumentException("Select a visible device.")).GlyphForTest, value); break;
            case "capture-card-menu":
            case "capture-card-menu-native":
                var menuDirectory = CaptureDirectory(value);
                var menuPresenter = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(_window!.ThemeRoot.XamlRoot)
                    .Select(popup => popup.Child).OfType<Microsoft.UI.Xaml.Controls.MenuFlyoutPresenter>().SingleOrDefault()
                    ?? throw new InvalidOperationException("Open exactly one card menu before capturing it.");
                if (action == "capture-card-menu") await CaptureScenarioAsync(menuPresenter, value);
                else await VisualCaptureDiagnostics.CapturePopupAsync(_window, _panel!, menuDirectory, value!);
                break;
            case "capture-main-cards": await CaptureScenarioAsync(_window!.CardsForTest, value); break;
            case "capture-main-scroll": await CaptureScenarioAsync(_window!.DeviceScrollForTest, value); break;
            case "capture-main-native":
            case "capture-panel-native":
                var directory = CaptureDirectory(value);
                if (action == "capture-main-native") await VisualCaptureDiagnostics.CaptureWindowAsync(_window!, _window!.ThemeRoot, _window.PageScrollForTest, directory, value!);
                else await VisualCaptureDiagnostics.CaptureWindowAsync(_panel!, _panel!.ThemeRoot, _panel.DeviceScrollForTest, directory, value!);
                break;
            case "main-scroll":
                if (_scenarios is null || value is not ("top" or "bottom")) throw new ArgumentException("Use top or bottom in a fictional scenario.");
                _window!.DeviceScrollForTest.ChangeView(null, value == "bottom" ? _window.DeviceScrollForTest.ScrollableHeight : 0, null, true);
                break;
            case "settings-scroll":
                if (_scenarios is null || value is not ("top" or "bottom") || _window!.CaptureUiState().Page != "settings") throw new ArgumentException("Use top or bottom on the scenario settings page.");
                var settingsScroll = _window.PageScrollForTest;
                settingsScroll.ChangeView(null, value == "bottom" ? settingsScroll.ScrollableHeight : 0, null, true);
                break;
            case "scenario-banner":
                if (_scenarios is null || value is not ("hidden" or "visible")) throw new ArgumentException("Use hidden or visible in a fictional scenario.");
                _window!.SetScenarioBannerForTest(value == "visible");
                break;
            case "card-menu-open":
            case "card-menu-close":
                if (_scenarios is null) throw new InvalidOperationException("Menu capture requires a fictional scenario.");
                (_window!.CardForTest(deviceId ?? "") ?? throw new ArgumentException("Select a visible device.")).ShowMenuForTest(action == "card-menu-open");
                break;
            default: await _window!.ApplyUiTestActionAsync(action, value, deviceId); break;
        }
    }

    private async Task CaptureScenarioAsync(FrameworkElement element, string? name)
    {
        var path = CaptureDirectory(name);
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) throw new InvalidOperationException("The view has not rendered yet.");
        var buffer = await bitmap.GetPixelsAsync();
        var bytes = new byte[buffer.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
        var file = await folder.CreateFileAsync(name, Windows.Storage.CreationCollisionOption.ReplaceExisting);
        using var output = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        var bounds = element.TransformToVisual(element.XamlRoot.Content).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        await File.WriteAllTextAsync(Path.Combine(path, Path.ChangeExtension(name!, ".json")),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = "RenderTargetBitmap element export, not a native-window capture",
                bitmap.PixelWidth, bitmap.PixelHeight, element.ActualWidth, element.ActualHeight,
                element.XamlRoot.RasterizationScale, rootLeft = bounds.Left, rootTop = bounds.Top
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private string CaptureDirectory(string? name)
    {
        if (_scenarios is null) throw new InvalidOperationException("Image capture is available only for fictional scenarios.");
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !name.EndsWith(".png", StringComparison.Ordinal)
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.'))) throw new ArgumentException("Use a simple PNG filename.");
        var path = Path.Combine(AppContext.BaseDirectory, "logs", "screenshots");
        Directory.CreateDirectory(path);
        return path;
    }
}
