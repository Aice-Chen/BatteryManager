using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace BatteryManager.Core;

public sealed record SessionOptions(TimeSpan ScanTimeout, TimeSpan PollInterval, TimeSpan ShutdownTimeout, int MaxWaiters = 32)
{
    public static SessionOptions Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));
}

public sealed record SessionSnapshot(StateSnapshot State, bool IsRefreshing, string Status, int OutstandingOperations, long Generation, bool IsSuspended = false);
public sealed record RefreshResult(string Outcome, long Generation, string? Error = null);
public sealed record StopResult(bool Drained, int OutstandingOperations);

/// <summary>Owns logical requests, the physical scan, expiration and bounded snapshot delivery.</summary>
public sealed class BatterySession : IAsyncDisposable
{
    private sealed class Scan(long generation, bool manual, TimeSpan deadline)
    {
        public long Generation { get; } = generation;
        public bool Manual { get; } = manual;
        public TimeSpan Deadline { get; } = deadline;
        public bool Accepting = true;
        public int Waiters;
        public bool HadError;
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CompletedSources { get; } = new(StringComparer.Ordinal);
        public HashSet<(string Source, string Endpoint)> CompletedEndpoints { get; } = [];
        public CancellationTokenSource Cancellation { get; } = new();
        public Task CancellationCompletion = Task.CompletedTask;
        public TaskCompletionSource<RefreshResult> Logical { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Physical { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ResumeRequest
    {
        public TaskCompletionSource<RefreshResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly IDeviceSource _source;
    private readonly StateStore _store = new();
    private readonly TimeProvider _clock;
    private readonly SessionOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Channel<SessionSnapshot>> _subscribers = [];
    private readonly Channel<bool> _expiryWake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private Scan? _active;
    private long _generation;
    private string _status = "Idle";
    private bool _stopping;
    private bool _suspended;
    private ResumeRequest? _resumeRequest;
    private Task? _resumeWorker;
    private bool _resumeWorkerRunning;
    private Task? _polling;
    private readonly Task _expiration;
    private Task<StopResult>? _stopTask;

    public BatterySession(IDeviceSource source, TimeProvider? clock = null, SessionOptions? options = null)
    {
        _source = source;
        _clock = clock ?? TimeProvider.System;
        _options = options ?? SessionOptions.Default;
        if (_options.ScanTimeout <= TimeSpan.Zero || _options.PollInterval <= TimeSpan.Zero || _options.ShutdownTimeout < TimeSpan.Zero || _options.MaxWaiters < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        _expiration = Task.Run(ExpireAsync);
    }

    public SessionSnapshot Snapshot
    {
        get { lock (_gate) return CaptureLocked(); }
    }

    public void SetDeviceLinks(IReadOnlyList<DeviceLinkGroup> groups)
    {
        lock (_gate)
        {
            if (_stopping) throw new ObjectDisposedException(nameof(BatterySession));
            _store.SetDeviceLinks(groups, ClockInstant.Read(_clock));
            PublishLocked();
            _expiryWake.Writer.TryWrite(true);
        }
    }

    public void StartPolling()
    {
        lock (_gate)
        {
            if (_stopping) throw new ObjectDisposedException(nameof(BatterySession));
            _polling ??= Task.Run(PollAsync);
        }
    }

    // The power-message handler must return promptly; native cancellation drains later.
    public void Suspend()
    {
        lock (_gate)
        {
            if (_stopping || _suspended) return;
            _suspended = true;
            CancelResumeLocked("Canceled", "The computer is suspending.");
            if (_active is { } active)
                CloseLocked(active, "Canceled", "The computer is suspending.", ReadStatus.Canceled);
            _status = "Suspended";
            PublishLocked();
            _expiryWake.Writer.TryWrite(true);
        }
    }

    public Task<RefreshResult> ResumeAsync()
    {
        lock (_gate)
        {
            if (_stopping) return Task.FromResult(new RefreshResult("Stopped", _generation));
            if (_resumeRequest is { } pending) return pending.Completion.Task;
            _suspended = false;
            // Windows can report a resume without a preceding suspend notification.
            // Discard the old logical round in that case as well.
            if (_active is { } active)
                CloseLocked(active, "Canceled", "The computer resumed; the previous scan is no longer current.", ReadStatus.Canceled);
            var request = _resumeRequest = new ResumeRequest();
            _status = "Resuming";
            PublishLocked();
            _expiryWake.Writer.TryWrite(true);
            if (!_resumeWorkerRunning)
            {
                _resumeWorkerRunning = true;
                _resumeWorker = Task.Run(ResumeWorkerAsync);
            }
            return request.Completion.Task;
        }
    }

    public async Task<RefreshResult> RefreshAsync(bool manual = true, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return new("Canceled", 0);
        Scan scan;
        lock (_gate)
        {
            if (_stopping) return new("Stopped", _generation);
            if (_suspended) return new("Suspended", _generation);
            if (_resumeRequest is not null)
                return new("Busy", _generation, "A fresh scan after system resume is pending.");
            if (_active is { } current)
            {
                if (!current.Accepting || current.Manual != manual || current.Waiters >= _options.MaxWaiters)
                    return new("Busy", current.Generation, "The previous physical operation has not finished or is incompatible.");
                scan = current;
            }
            else
            {
                scan = StartScanLocked(manual);
            }
            scan.Waiters++;
            PublishLocked();
        }

        return await WaitForScanAsync(scan, cancellationToken).ConfigureAwait(false);
    }

    private Scan StartScanLocked(bool manual)
    {
        var scan = new Scan(++_generation, manual, ClockInstant.Read(_clock).Elapsed + _options.ScanTimeout);
        _active = scan;
        _status = "Reading";
        _store.BeginScan(scan.Generation, manual);
        // A source may enter a synchronous native call before its first await.
        _ = Task.Run(() => RunAsync(scan));
        return scan;
    }

    private async Task<RefreshResult> WaitForScanAsync(Scan scan, CancellationToken cancellationToken)
    {
        try { return await scan.Logical.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new("Canceled", scan.Generation); }
        finally
        {
            lock (_gate)
            {
                scan.Waiters--;
                if (scan.Waiters == 0 && scan.Accepting)
                    CloseLocked(scan, "Canceled", "All callers canceled.", ReadStatus.Canceled);
            }
        }
    }

    private async Task ResumeWorkerAsync()
    {
        while (true)
        {
            ResumeRequest request;
            Task physical;
            lock (_gate)
            {
                if (_stopping || _suspended || _resumeRequest is null)
                {
                    _resumeWorkerRunning = false;
                    return;
                }
                request = _resumeRequest;
                physical = _active?.Physical.Task ?? Task.CompletedTask;
            }

            // This wait has no logical timeout: only the source can prove that its
            // hardware handles and operations are actually free for another scan.
            await physical.ConfigureAwait(false);
            Scan scan;
            lock (_gate)
            {
                if (_stopping || _suspended || !ReferenceEquals(request, _resumeRequest)) continue;
                scan = StartScanLocked(manual: false);
                scan.Waiters++;
                PublishLocked();
            }
            var result = await WaitForScanAsync(scan, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(request, _resumeRequest)) _resumeRequest = null;
                request.Completion.TrySetResult(result);
            }
        }
    }

    private void CancelResumeLocked(string outcome, string error)
    {
        _resumeRequest?.Completion.TrySetResult(new(outcome, _generation, error));
        _resumeRequest = null;
    }

    public async IAsyncEnumerable<SessionSnapshot> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<SessionSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
        lock (_gate)
        {
            if (_subscribers.Count >= 32) throw new InvalidOperationException("Too many snapshot subscribers.");
            channel.Writer.TryWrite(CaptureLocked());
            if (_stopping) channel.Writer.TryComplete();
            else _subscribers.Add(channel);
        }
        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return snapshot;
        }
        finally { lock (_gate) _subscribers.Remove(channel); }
    }

    public Task<StopResult> StopAsync()
    {
        lock (_gate) return _stopTask ??= StopCoreAsync();
    }

    // StopAsync reports whether the shutdown budget was sufficient. An application
    // releasing its process/resources must additionally await physical completion.
    public async Task StopAndDrainAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Task physical;
        Task? polling;
        Task? resuming;
        lock (_gate)
        {
            physical = _active?.Physical.Task ?? Task.CompletedTask;
            polling = _polling;
            resuming = _resumeWorker;
        }
        await physical.ConfigureAwait(false);
        if (polling is not null) await polling.ConfigureAwait(false);
        if (resuming is not null) await resuming.ConfigureAwait(false);
        await _expiration.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(Scan scan)
    {
        // This task is the physical completion proof supplied by the source. Never use
        // WaitAsync(cancellationToken) as a replacement for observing its actual completion.
        Task physical = Task.Run(() =>
        {
            // A suspend may arrive after queuing this round but before its worker runs.
            scan.Cancellation.Token.ThrowIfCancellationRequested();
            return _source.ReadAsync(new(scan.Generation, scan.Manual), batch => Accept(scan, batch), scan.Cancellation.Token);
        });
        using var deadlineCancellation = new CancellationTokenSource();
        var remaining = scan.Deadline - ClockInstant.Read(_clock).Elapsed;
        var deadline = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _clock, deadlineCancellation.Token);
        try
        {
            var first = await Task.WhenAny(physical, deadline, scan.Logical.Task).ConfigureAwait(false);
            if (first == deadline)
                lock (_gate) CloseLocked(scan, "TimedOut", "The scan exceeded its time budget.", ReadStatus.Timeout);

            // The scan remains in _active until this await finishes, even after timeout.
            await physical.ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_active, scan)) _active = null;
                if (scan.Accepting) CloseLocked(scan, scan.HadError ? "Partial" : "Completed", null, null);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, scan)) _active = null;
                CloseLocked(scan, "Canceled", "The source canceled its scan.", ReadStatus.Canceled);
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, scan)) _active = null;
                CloseLocked(scan, "Failed", error.Message, ReadStatus.Error);
            }
        }
        finally
        {
            deadlineCancellation.Cancel();
            lock (_gate)
            {
                if (ReferenceEquals(_active, scan))
                {
                    _active = null;
                }
                PublishLocked();
                scan.Physical.TrySetResult();
            }
            _ = DisposeScanCancellationAsync(scan);
        }
    }

    private void Accept(Scan scan, SourceBatch batch)
    {
        lock (_gate)
        {
            if (_stopping || _suspended || !ReferenceEquals(_active, scan) || !scan.Accepting) return;
            var now = ClockInstant.Read(_clock);
            if (now.Elapsed >= scan.Deadline)
            {
                CloseLocked(scan, "TimedOut", "The scan exceeded its time budget.", ReadStatus.Timeout);
                return;
            }
            scan.Sources.Add(batch.Source);
            if (batch.Complete) scan.CompletedSources.Add(batch.Source);
            foreach (var observation in batch.Observations)
                scan.CompletedEndpoints.Add((batch.Source, observation.EndpointId.Trim().ToLowerInvariant()));
            scan.HadError |= batch.Error is not null || batch.Observations.Any(x => x.Error is not null || x.Status is ReadStatus.Error or ReadStatus.Timeout);
            _store.Apply(scan.Generation, batch, now);
            PublishLocked();
            _expiryWake.Writer.TryWrite(true);
        }
    }

    private void CloseLocked(Scan scan, string outcome, string? error, ReadStatus? failure)
    {
        if (!scan.Accepting) return;
        scan.Accepting = false;
        if (failure is not null)
        {
            var now = ClockInstant.Read(_clock);
            // Device observations are already completed results. An unfinished sibling
            // must not overwrite their attempts merely because they share an adapter.
            var pending = _store.Snapshot(now).DetectedDevices.SelectMany(x => x.Readings)
                .Where(x => !scan.CompletedSources.Contains(x.Source) &&
                    !scan.CompletedEndpoints.Contains((x.Source, x.EndpointId.Trim().ToLowerInvariant())))
                .Select(x => (x.Source, x.EndpointId)).Distinct().GroupBy(x => x.Source);
            foreach (var source in pending)
            {
                var failed = source.Select(x => new DeviceObservation(x.EndpointId, null, null, "", DeviceKind.Unknown,
                    x.Source, now.Utc, Status: failure.Value, Error: error, ReceivedElapsed: now.Elapsed)).ToArray();
                _store.Apply(scan.Generation, new(source.Key, failed), now);
            }
        }
        _store.EndScan(scan.Generation);
        _status = outcome;
        scan.Logical.TrySetResult(new(outcome, scan.Generation, error));
        if (failure is not null)
        {
            // CancelAsync runs callbacks away from this lock; blocked callbacks cannot own completion.
            scan.CancellationCompletion = ObserveCancellationAsync(scan.Cancellation.CancelAsync());
        }
        PublishLocked();
        _expiryWake.Writer.TryWrite(true);
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation.ConfigureAwait(false); } catch { /* Result is already terminal; observe callback failures. */ }
    }

    private static async Task DisposeScanCancellationAsync(Scan scan)
    {
        // Physical completion alone does not prove that arbitrary cancellation
        // callbacks have returned. Dispose only after both boundaries have settled.
        await scan.CancellationCompletion.ConfigureAwait(false);
        scan.Cancellation.Dispose();
    }

    private SessionSnapshot CaptureLocked() => new(_store.Snapshot(ClockInstant.Read(_clock)), _active?.Accepting == true, _status, _active is null ? 0 : 1, _generation, _suspended);

    private void PublishLocked()
    {
        var snapshot = CaptureLocked();
        foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(snapshot);
    }

    private async Task PollAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_options.PollInterval, _clock);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                await RefreshAsync(false, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task ExpireAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TimeSpan? due;
                lock (_gate) due = _store.NextExpiryDelay(ClockInstant.Read(_clock));
                using var iteration = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                Task delay = due is null ? Task.Delay(Timeout.InfiniteTimeSpan, _clock, iteration.Token)
                    : Task.Delay(due.Value > TimeSpan.Zero ? due.Value : TimeSpan.FromMilliseconds(1), _clock, iteration.Token);
                Task wake = _expiryWake.Reader.ReadAsync(iteration.Token).AsTask();
                var finished = await Task.WhenAny(delay, wake).ConfigureAwait(false);
                await iteration.CancelAsync().ConfigureAwait(false);
                try { await finished.ConfigureAwait(false); } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                lock (_gate) if (!_stopping) PublishLocked();
                // Observe the canceled loser without leaving a channel waiter behind.
                try { await (finished == delay ? wake : delay).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task<StopResult> StopCoreAsync()
    {
        Scan? scan;
        lock (_gate)
        {
            _stopping = true;
            CancelResumeLocked("Stopped", "The application is stopping.");
            scan = _active;
            if (scan is not null) CloseLocked(scan, "Canceled", "The application is stopping.", ReadStatus.Canceled);
            _status = "Stopped";
            _ = ObserveCancellationAsync(_lifetime.CancelAsync());
            PublishLocked();
            foreach (var subscriber in _subscribers) subscriber.Writer.TryComplete();
            _subscribers.Clear();
        }
        if (scan is not null)
        {
            try { await scan.Physical.Task.WaitAsync(_options.ShutdownTimeout, _clock).ConfigureAwait(false); }
            catch (TimeoutException) { return new(false, 1); }
        }
        return new(true, 0);
    }
}
