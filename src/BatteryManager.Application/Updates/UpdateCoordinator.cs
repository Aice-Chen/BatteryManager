using System.Threading.Channels;
using BatteryManager.Application.Management;

namespace BatteryManager.Application.Updates;

/// <summary>Serializes manual requests and automatic check/download cycles.
/// The app owns scheduling and installation. Client completion is the physical drain boundary.</summary>
internal sealed class UpdateCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FutureClockTolerance = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly string _currentVersion;
    private readonly string _cacheDirectory;
    private readonly IReleaseUpdateClient _client;
    private readonly IManagementStore _store;
    private readonly TimeProvider _clock;
    private readonly Channel<UpdateSnapshot> _notifications = Channel.CreateBounded<UpdateSnapshot>(new BoundedChannelOptions(32)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _notificationLoop;
    private UpdateSnapshot _snapshot;
    private Operation? _active;
    private InstallationLease? _installation;
    private bool _downloadWasAutomatic;
    private Task _persistence = Task.CompletedTask;
    private Task? _stopTask;
    private bool _stopping;
    private long? _lastAttemptStamp;
    private int _pendingPreferenceChanges;
    private bool _savedAutoCheckEnabled;

    public UpdateCoordinator(string currentVersion, string cacheDirectory, IReleaseUpdateClient client,
        IManagementStore store, TimeProvider? clock = null)
    {
        _ = ReleaseVersion.IsNewer("0.0.0", currentVersion);
        _currentVersion = currentVersion;
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _client = client;
        _store = store;
        _clock = clock ?? TimeProvider.System;
        var loaded = store.Load();
        _savedAutoCheckEnabled = loaded.Settings.AutoCheckUpdates;
        _snapshot = new(1, UpdatePhase.Idle, loaded.Settings.AutoCheckUpdates,
            loaded.Settings.LastCheckUtc, loaded.Settings.LastSuccessfulCheckUtc,
            SettingsError: loaded.Success ? null : loaded.Error, CanSaveSettings: loaded.CanSave);
        _notificationLoop = Task.Run(DeliverNotificationsAsync);
    }

    public UpdateSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public event Action<UpdateSnapshot>? Changed;

    public bool ShouldCheckAutomatically()
    {
        lock (_gate) return ShouldCheckLocked();
    }

    private bool ShouldCheckLocked()
    {
        if (_stopping || _active is not null || _installation is not null || !AutomaticEnabledLocked())
            return false;
        if (_lastAttemptStamp is { } stamp)
            return _clock.GetElapsedTime(stamp, _clock.GetTimestamp()) >= AutomaticCheckInterval;
        if (_snapshot.LastCheckUtc is not { } at) return true;
        var age = _clock.GetUtcNow() - at;
        // A persisted timestamp far ahead of the clock must not suppress updates forever.
        return age >= AutomaticCheckInterval || age < -FutureClockTolerance;
    }

    public Task<UpdateOperationResult> CheckAsync(bool automatic = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_stopping) return Result(UpdateOperationOutcome.Stopped);
            if (_active is not null || _installation is not null) return Result(UpdateOperationOutcome.Busy);
            if (cancellationToken.IsCancellationRequested) return Result(UpdateOperationOutcome.Canceled);
            if (automatic && !ShouldCheckLocked()) return Result(UpdateOperationOutcome.Skipped);
            var operation = new Operation(check: true, null, cancellationToken, automatic);
            _active = operation;
            _downloadWasAutomatic = false;
            _lastAttemptStamp = _clock.GetTimestamp();
            var at = _clock.GetUtcNow().ToUniversalTime();
            ChangeLocked(_snapshot with { Phase = UpdatePhase.Checking, LastCheckUtc = at, Error = null });
            _ = QueueWriteLocked(() => _store.RecordCheckAttemptAsync(at));
            _ = Task.Run(() => RunAsync(operation));
            return operation.Completion.Task;
        }
    }

    // Manual downloads remain explicit even when automatic updates are enabled.
    public Task<UpdateOperationResult> DownloadAsync(UpdatePackageKind kind, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_stopping) return Result(UpdateOperationOutcome.Stopped);
            if (_active is not null || _installation is not null) return Result(UpdateOperationOutcome.Busy);
            if (cancellationToken.IsCancellationRequested) return Result(UpdateOperationOutcome.Canceled);
            var package = _snapshot.Available?.Packages.SingleOrDefault(item => item.Kind == kind);
            if (package is null) return Result(UpdateOperationOutcome.Failed, "当前没有对应版本的下载包。");
            var operation = new Operation(check: false, package, cancellationToken);
            _active = operation;
            _downloadWasAutomatic = false;
            ChangeLocked(_snapshot with { Phase = UpdatePhase.Downloading, SelectedPackage = package,
                DownloadedBytes = 0, DownloadedFile = null, Error = null });
            _ = Task.Run(() => RunAsync(operation));
            return operation.Completion.Task;
        }
    }

    public Task<UpdateOperationResult> SetAutomaticChecksAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_stopping) return Result(UpdateOperationOutcome.Stopped);
            if (cancellationToken.IsCancellationRequested) return Result(UpdateOperationOutcome.Canceled);
            _pendingPreferenceChanges++;
            ChangeLocked(_snapshot with { AutoCheckEnabled = enabled });
            if (!enabled && _active is { Automatic: true } automatic)
            {
                automatic.CancelRequested = true;
                ChangeLocked(_snapshot with { Phase = UpdatePhase.Cancelling });
                _ = RequestCancellationAsync(automatic);
            }
            if (!enabled && _installation is { Automatic: true, Committed: false } installation)
                _ = installation.RequestCancellationAsync();
            var task = QueueWriteLocked(() => _store.SetAutomaticChecksAsync(enabled, cancellationToken), result =>
            {
                _pendingPreferenceChanges--;
            });
            return PreferenceResultAsync(task);
        }
    }

    public async Task CancelCurrentAsync()
    {
        Operation? operation;
        InstallationLease? installation;
        lock (_gate)
        {
            operation = _active;
            installation = _installation;
            if (_stopping || (operation is null && installation is null)) return;
            if (operation is not null) operation.CancelRequested = true;
            if (installation is { Committed: false }) installation.CancelRequested = true;
            ChangeLocked(_snapshot with { Phase = UpdatePhase.Cancelling });
        }
        if (installation is { Committed: false }) await installation.RequestCancellationAsync().ConfigureAwait(false);
        if (operation is null) return;
        await RequestCancellationAsync(operation).ConfigureAwait(false);
        await operation.Completion.Task.ConfigureAwait(false);
    }

    private bool AutomaticEnabledLocked() => _snapshot.AutoCheckEnabled && _savedAutoCheckEnabled &&
        _pendingPreferenceChanges == 0 && _snapshot.CanSaveSettings && _snapshot.SettingsError is null;

    public InstallationLease? TryPrepareInstallation(bool automatic)
    {
        lock (_gate)
        {
            if (_stopping || _active is not null || _installation is not null ||
                _snapshot.Phase != UpdatePhase.Downloaded || _snapshot.SelectedPackage is not { Kind: UpdatePackageKind.Installer } package ||
                _snapshot.DownloadedFile is not { } file || (automatic && (!_downloadWasAutomatic || !AutomaticEnabledLocked()))) return null;
            _installation = new(this, package, file, automatic);
            ChangeLocked(_snapshot with { Phase = UpdatePhase.PreparingInstallation });
            return _installation;
        }
    }

    private bool CommitInstallation(InstallationLease installation)
    {
        lock (_gate)
        {
            if (_stopping || !ReferenceEquals(_installation, installation) || installation.Committed ||
                installation.CancelRequested || installation.Token.IsCancellationRequested || (installation.Automatic && !AutomaticEnabledLocked())) return false;
            installation.Committed = true;
            return true;
        }
    }

    private void ReleaseInstallation(InstallationLease installation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_installation, installation)) return;
            _installation = null;
            if (!_stopping) ChangeLocked(_snapshot with { Phase = installation.Token.IsCancellationRequested ? UpdatePhase.Canceled : UpdatePhase.Downloaded });
        }
    }

    public Task CancelAndDrainAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            _stopping = true;
            if (_active is { } operation) operation.CancelRequested = true;
            ChangeLocked(_snapshot with { Phase = UpdatePhase.Stopping });
            return _stopTask = Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        Operation? active;
        InstallationLease? installation;
        Task persistence;
        lock (_gate) { active = _active; installation = _installation; persistence = _persistence; }
        // Installation calls App's normal shutdown. Waiting for its completion here would await ourselves.
        if (installation is not null) await installation.RequestCancellationAsync().ConfigureAwait(false);
        if (active is not null)
        {
            await RequestCancellationAsync(active).ConfigureAwait(false);
            await active.Completion.Task.ConfigureAwait(false);
        }
        await persistence.ConfigureAwait(false);
        lock (_gate)
        {
            ChangeLocked(_snapshot with { Phase = UpdatePhase.Stopped });
            _notifications.Writer.TryComplete();
        }
        await _notificationLoop.ConfigureAwait(false);
    }

    private async Task RunAsync(Operation operation)
    {
        AvailableUpdate? update = null;
        string? file = null;
        string? error = null;
        var canceled = false;
        var checkPublished = false;
        try
        {
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            if (operation.Check)
            {
                var found = await _client.CheckAsync(_currentVersion, operation.Cancellation.Token).ConfigureAwait(false);
                update = found is null ? null : found with { Packages = Array.AsReadOnly(found.Packages.ToArray()) };
                if (operation.Automatic)
                {
                    Task persistence;
                    lock (_gate)
                    {
                        ThrowIfAutomaticCanceledLocked(operation);
                        PublishSuccessfulCheckLocked(update);
                        checkPublished = true;
                        persistence = _persistence;
                    }
                    // Persist the throttle and observe save failures before an unattended download.
                    await persistence.ConfigureAwait(false);
                    lock (_gate)
                    {
                        ThrowIfAutomaticCanceledLocked(operation);
                        if (_snapshot.SettingsError is { } settingsError) throw new IOException(settingsError);
                        if (update is not null)
                        {
                            operation.Package = update.Packages.SingleOrDefault(item => item.Kind == UpdatePackageKind.Installer)
                                ?? throw new InvalidDataException("当前正式版本没有安装包。");
                            ChangeLocked(_snapshot with { Phase = UpdatePhase.Downloading, SelectedPackage = operation.Package,
                                DownloadedBytes = 0, DownloadedFile = null });
                        }
                    }
                    if (operation.Package is not null)
                        file = await _client.DownloadAsync(operation.Package, _cacheDirectory,
                            new ProgressCallback(value => ReportProgress(operation, value)), operation.Cancellation.Token).ConfigureAwait(false);
                }
            }
            else
            {
                file = await _client.DownloadAsync(operation.Package!, _cacheDirectory,
                    new ProgressCallback(value => ReportProgress(operation, value)), operation.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested) { canceled = true; }
        catch (Exception exception) { error = exception.Message; }
        finally
        {
            canceled |= operation.Cancellation.IsCancellationRequested;
            // Complete cancellation callbacks before disposing the CTS or admitting another request.
            await operation.DrainCancellationAsync().ConfigureAwait(false);
        }
        UpdateOperationResult result;
        lock (_gate)
        {
            canceled |= operation.CancelRequested || operation.CallerToken.IsCancellationRequested || _stopping;
            if (ReferenceEquals(_active, operation)) _active = null;
            result = new(canceled ? UpdateOperationOutcome.Canceled : error is not null ? UpdateOperationOutcome.Failed : UpdateOperationOutcome.Completed, canceled ? null : error);
            if (!_stopping)
            {
                if (canceled) ChangeLocked(_snapshot with { Phase = UpdatePhase.Canceled, Error = null });
                else if (error is not null) ChangeLocked(_snapshot with { Phase = UpdatePhase.Failed, Error = error });
                else if (file is not null)
                {
                    _downloadWasAutomatic = operation.Automatic;
                    ChangeLocked(_snapshot with { Phase = UpdatePhase.Downloaded, DownloadedFile = file,
                        DownloadedBytes = operation.Package!.Size, Error = null });
                }
                else if (operation.Check && !checkPublished) PublishSuccessfulCheckLocked(update);
            }
        }
        operation.Cancellation.Dispose();
        operation.Completion.TrySetResult(result);
    }

    private void ThrowIfAutomaticCanceledLocked(Operation operation)
    {
        if (_stopping || operation.CancelRequested || operation.CallerToken.IsCancellationRequested || !_snapshot.AutoCheckEnabled)
        {
            operation.CancelRequested = true;
            throw new OperationCanceledException(operation.Cancellation.Token);
        }
        operation.Cancellation.Token.ThrowIfCancellationRequested();
    }

    private void PublishSuccessfulCheckLocked(AvailableUpdate? update)
    {
        var at = _clock.GetUtcNow().ToUniversalTime();
        ChangeLocked(_snapshot with { Phase = update is null ? UpdatePhase.UpToDate : UpdatePhase.Available,
            Available = update, LastSuccessfulCheckUtc = at, SelectedPackage = null,
            DownloadedBytes = 0, DownloadedFile = null, Error = null });
        _ = QueueWriteLocked(() => _store.RecordSuccessfulCheckAsync(at));
    }

    private void ReportProgress(Operation operation, long value)
    {
        lock (_gate)
        {
            if (_stopping || !ReferenceEquals(_active, operation) || operation.CancelRequested || operation.Cancellation.IsCancellationRequested ||
                value < _snapshot.DownloadedBytes || value > operation.Package!.Size) return;
            ChangeLocked(_snapshot with { DownloadedBytes = value });
        }
    }

    // Writes are ordered independently from network work. A slow preference disk cannot stall manual checking.
    private Task<ManagementStoreResult> QueueWriteLocked(Func<Task<ManagementStoreResult>> save, Action<ManagementStoreResult>? apply = null)
    {
        var previous = _persistence;
        var work = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            ManagementStoreResult result;
            try { result = await save().ConfigureAwait(false); }
            catch (Exception error)
            {
                var state = Snapshot;
                result = new(false, new(state.AutoCheckEnabled, state.LastCheckUtc, state.LastSuccessfulCheckUtc), "更新设置保存失败：" + error.Message);
            }
            lock (_gate)
            {
                if (!_stopping)
                {
                    if (result.Success) _savedAutoCheckEnabled = result.Settings.AutoCheckUpdates;
                    apply?.Invoke(result);
                    ChangeLocked(_snapshot with { SettingsError = result.Success ? null : result.Error, CanSaveSettings = result.CanSave,
                        AutoCheckEnabled = _pendingPreferenceChanges == 0 ? _savedAutoCheckEnabled : _snapshot.AutoCheckEnabled });
                }
            }
            return result;
        });
        _persistence = work;
        return work;
    }

    private void ChangeLocked(UpdateSnapshot next)
    {
        _snapshot = next with { Version = _snapshot.Version + 1 };
        _notifications.Writer.TryWrite(_snapshot);
    }

    private async Task DeliverNotificationsAsync()
    {
        await foreach (var snapshot in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (_stopping && snapshot.Phase is not (UpdatePhase.Stopping or UpdatePhase.Stopped)) continue;
            }
            // Subscriber failures do not interrupt HTTP cleanup or other subscribers. No callback holds _gate.
            var handlers = Changed;
            if (handlers is null) continue;
            foreach (Action<UpdateSnapshot> callback in handlers.GetInvocationList())
            {
                try { callback(snapshot); }
                catch (Exception) { }
            }
        }
    }

    private static async Task RequestCancellationAsync(Operation operation)
    {
        try { await operation.RequestCancellationAsync().ConfigureAwait(false); }
        catch (AggregateException) { }
    }

    private static async Task<UpdateOperationResult> PreferenceResultAsync(Task<ManagementStoreResult> work)
    {
        var saved = await work.ConfigureAwait(false);
        return new(saved.Success ? UpdateOperationOutcome.Completed : UpdateOperationOutcome.Failed, saved.Error);
    }
    private static Task<UpdateOperationResult> Result(UpdateOperationOutcome outcome, string? error = null) => Task.FromResult(new UpdateOperationResult(outcome, error));
    public ValueTask DisposeAsync() => new(CancelAndDrainAsync());

    internal sealed class InstallationLease : IAsyncDisposable
    {
        private readonly UpdateCoordinator _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly object _gate = new();
        private Task? _cancel;
        private Task? _dispose;
        internal InstallationLease(UpdateCoordinator owner, UpdatePackage package, string file, bool automatic)
        { _owner = owner; Package = package; File = file; Automatic = automatic; }
        public UpdatePackage Package { get; }
        public string File { get; }
        public bool Automatic { get; }
        public CancellationToken Token => _cancellation.Token;
        internal bool Committed { get; set; }
        internal bool CancelRequested { get; set; }
        public bool TryCommit() => _owner.CommitInstallation(this);
        internal Task RequestCancellationAsync()
        { lock (_gate) return _cancel ??= _cancellation.CancelAsync(); }
        public ValueTask DisposeAsync()
        { lock (_gate) return new(_dispose ??= Task.Run(DisposeCoreAsync)); }
        private async Task DisposeCoreAsync()
        {
            _owner.ReleaseInstallation(this);
            await RequestCancellationAsync().ConfigureAwait(false);
            _cancellation.Dispose();
        }
    }

    private sealed class Operation
    {
        private readonly object _cancellationGate = new();
        private readonly CancellationTokenRegistration _callerRegistration;
        private Task? _cancellationTask;

        public Operation(bool check, UpdatePackage? package, CancellationToken callerToken, bool automatic = false)
        {
            Check = check;
            Package = package;
            Automatic = automatic;
            CallerToken = callerToken;
            // A linked CTS could start its own synchronous cancellation outside our ownership.
            // Bridge caller cancellation into the same tracked asynchronous request instead.
            _callerRegistration = callerToken.UnsafeRegister(static state =>
            {
                _ = ((Operation)state!).RequestCancellationAsync();
            }, this);
        }

        public bool Check { get; }
        public UpdatePackage? Package { get; set; }
        public bool Automatic { get; }
        public CancellationToken CallerToken { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<UpdateOperationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancelRequested { get; set; }

        public Task RequestCancellationAsync()
        {
            // A second CTS.CancelAsync call can finish while the first call's callbacks still run.
            // Every caller must await exactly the first task rather than invoke CancelAsync again.
            lock (_cancellationGate) return _cancellationTask ??= Cancellation.CancelAsync();
        }

        public async Task DrainCancellationAsync()
        {
            try { await RequestCancellationAsync().ConfigureAwait(false); }
            catch (AggregateException) { }
            finally { await _callerRegistration.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private sealed class ProgressCallback(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
