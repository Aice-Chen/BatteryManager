using System.Collections.Concurrent;
using BatteryManager.Application.Management;
using BatteryManager.Application.Updates;
using Xunit;

namespace BatteryManager.Application.Tests;

public sealed class UpdateCoordinatorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Successful_check_exposes_offer_and_times_but_does_not_download()
    {
        var client = new FakeClient { Check = _ => Task.FromResult<AvailableUpdate?>(Offer()) };
        var clock = new Clock();
        var store = new MemoryStore();
        await using var coordinator = Create(client, store, clock);
        Assert.True(coordinator.ShouldCheckAutomatically());
        Assert.Equal(0, client.CheckCount);
        Assert.True((await coordinator.CheckAsync()).Success);
        Assert.Equal(UpdatePhase.Available, coordinator.Snapshot.Phase);
        Assert.Equal("0.2.0", coordinator.Snapshot.Available!.Version);
        Assert.Equal(clock.GetUtcNow(), coordinator.Snapshot.LastCheckUtc);
        Assert.Equal(clock.GetUtcNow(), coordinator.Snapshot.LastSuccessfulCheckUtc);
        Assert.Equal(0, client.DownloadCount);
        Assert.False(coordinator.ShouldCheckAutomatically());
        Assert.Equal(UpdateOperationOutcome.Skipped, (await coordinator.CheckAsync(automatic: true)).Outcome);
        await coordinator.CancelAndDrainAsync();
        Assert.Equal(clock.GetUtcNow(), store.Settings.LastCheckUtc);
        Assert.Equal(clock.GetUtcNow(), store.Settings.LastSuccessfulCheckUtc);
    }

    [Fact]
    public async Task Concurrent_requests_are_busy_until_the_original_client_finishes()
    {
        var entered = Signal(); var release = Signal();
        var client = new FakeClient { Check = async _ => { entered.TrySetResult(); await release.Task; return Offer(); } };
        await using var coordinator = Create(client);
        var first = coordinator.CheckAsync();
        await entered.Task.WaitAsync(Deadline);
        Assert.Equal(UpdateOperationOutcome.Busy, (await coordinator.CheckAsync()).Outcome);
        Assert.Equal(UpdateOperationOutcome.Busy, (await coordinator.DownloadAsync(UpdatePackageKind.Installer)).Outcome);
        Assert.Equal(1, client.CheckCount);
        release.TrySetResult();
        Assert.True((await first.WaitAsync(Deadline)).Success);
    }

    [Fact]
    public async Task Download_progress_and_cancellation_wait_for_physical_completion_and_reject_late_success()
    {
        var entered = Signal(); var cancelObserved = Signal(); var release = Signal();
        IProgress<long>? progress = null;
        var client = new FakeClient
        {
            Check = _ => Task.FromResult<AvailableUpdate?>(Offer()),
            Download = async (_, report, token) =>
            {
                using var registration = token.Register(() => cancelObserved.TrySetResult());
                progress = report; report!.Report(10); entered.TrySetResult();
                await release.Task; // Deliberately retains physical ownership after cancellation.
                report.Report(100);
                return "late-download";
            }
        };
        await using var coordinator = Create(client);
        await coordinator.CheckAsync();
        var download = coordinator.DownloadAsync(UpdatePackageKind.Installer);
        await entered.Task.WaitAsync(Deadline);
        Assert.Equal(10, coordinator.Snapshot.DownloadedBytes);
        var canceled = coordinator.CancelCurrentAsync();
        await cancelObserved.Task.WaitAsync(Deadline);
        Assert.False(canceled.IsCompleted);
        Assert.Equal(UpdateOperationOutcome.Busy, (await coordinator.CheckAsync()).Outcome);
        progress!.Report(20);
        Assert.Equal(10, coordinator.Snapshot.DownloadedBytes);
        release.TrySetResult();
        Assert.Equal(UpdateOperationOutcome.Canceled, (await download.WaitAsync(Deadline)).Outcome);
        await canceled.WaitAsync(Deadline);
        Assert.Equal(UpdatePhase.Canceled, coordinator.Snapshot.Phase);
        Assert.Null(coordinator.Snapshot.DownloadedFile);
        var version = coordinator.Snapshot.Version;
        progress.Report(90);
        Assert.Equal(version, coordinator.Snapshot.Version);
    }

    [Fact]
    public async Task Manual_check_requires_explicit_download_to_produce_a_verified_file_result()
    {
        var client = new FakeClient
        {
            Check = _ => Task.FromResult<AvailableUpdate?>(Offer()),
            Download = (_, progress, _) => { progress!.Report(30); progress.Report(25); progress.Report(101); return Task.FromResult("verified-package"); }
        };
        await using var coordinator = Create(client);
        Assert.Equal(UpdateOperationOutcome.Failed, (await coordinator.DownloadAsync(UpdatePackageKind.Installer)).Outcome);
        await coordinator.CheckAsync();
        Assert.Equal(0, client.DownloadCount);
        Assert.True((await coordinator.DownloadAsync(UpdatePackageKind.Installer)).Success);
        Assert.Equal(UpdatePhase.Downloaded, coordinator.Snapshot.Phase);
        Assert.Equal("verified-package", coordinator.Snapshot.DownloadedFile);
        Assert.Equal(100, coordinator.Snapshot.DownloadedBytes);
        Assert.Equal(1, client.DownloadCount);
    }

    [Fact]
    public async Task Automatic_check_downloads_installer_in_the_same_reserved_operation()
    {
        var entered = Signal(); var release = Signal();
        var client = new FakeClient
        {
            Check = _ => Task.FromResult<AvailableUpdate?>(Offer()),
            Download = async (_, _, _) => { entered.TrySetResult(); await release.Task; return "verified-package"; }
        };
        await using var coordinator = Create(client);
        var cycle = coordinator.CheckAsync(automatic: true);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.Equal(UpdateOperationOutcome.Busy, (await coordinator.CheckAsync()).Outcome);
            Assert.Equal(UpdateOperationOutcome.Busy, (await coordinator.DownloadAsync(UpdatePackageKind.Installer)).Outcome);
        }
        finally { release.TrySetResult(); }
        Assert.True((await cycle.WaitAsync(Deadline)).Success);
        Assert.Equal(UpdatePhase.Downloaded, coordinator.Snapshot.Phase);
        Assert.Equal("verified-package", coordinator.Snapshot.DownloadedFile);
        Assert.Equal(1, client.CheckCount);
        Assert.Equal(1, client.DownloadCount);
        Assert.False(coordinator.ShouldCheckAutomatically());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabling_during_automatic_work_rejects_late_success_and_keeps_false_after_restart(bool duringDownload)
    {
        var entered = Signal(); var release = Signal();
        var store = new MemoryStore();
        var client = new FakeClient
        {
            Check = async _ => { if (!duringDownload) { entered.TrySetResult(); await release.Task; } return Offer(); },
            Download = async (_, _, _) => { entered.TrySetResult(); await release.Task; return "late-file"; }
        };
        await using (var coordinator = Create(client, store))
        {
            var cycle = coordinator.CheckAsync(automatic: true);
            try
            {
                await entered.Task.WaitAsync(Deadline);
                Assert.True((await coordinator.SetAutomaticChecksAsync(false)).Success);
                Assert.False(cycle.IsCompleted);
            }
            finally { release.TrySetResult(); }
            Assert.Equal(UpdateOperationOutcome.Canceled, (await cycle.WaitAsync(Deadline)).Outcome);
            Assert.Null(coordinator.Snapshot.DownloadedFile);
            Assert.Equal(duringDownload ? 1 : 0, client.DownloadCount);
        }
        await using var restarted = Create(client, store);
        Assert.False(restarted.ShouldCheckAutomatically());
        Assert.Equal(UpdateOperationOutcome.Skipped, (await restarted.CheckAsync(automatic: true)).Outcome);
    }

    [Fact]
    public async Task Automatic_download_error_is_visible_and_throttled_but_manual_retry_remains_possible()
    {
        var client = new FakeClient
        {
            Check = _ => Task.FromResult<AvailableUpdate?>(Offer()),
            Download = (_, _, _) => throw new IOException("checksum failure")
        };
        await using var coordinator = Create(client);
        Assert.Equal(UpdateOperationOutcome.Failed, (await coordinator.CheckAsync(automatic: true)).Outcome);
        Assert.Equal("checksum failure", coordinator.Snapshot.Error);
        Assert.Equal(UpdatePhase.Failed, coordinator.Snapshot.Phase);
        Assert.Null(coordinator.Snapshot.DownloadedFile);
        Assert.False(coordinator.ShouldCheckAutomatically());
        Assert.True((await coordinator.CheckAsync()).Success);
    }

    [Fact]
    public async Task Disabling_does_not_cancel_an_explicit_manual_download()
    {
        var entered = Signal(); var release = Signal();
        var client = new FakeClient
        {
            Check = _ => Task.FromResult<AvailableUpdate?>(Offer()),
            Download = async (_, _, token) => { entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); return "manual-file"; }
        };
        await using var coordinator = Create(client);
        await coordinator.CheckAsync();
        var download = coordinator.DownloadAsync(UpdatePackageKind.Installer);
        try { await entered.Task.WaitAsync(Deadline); await coordinator.SetAutomaticChecksAsync(false); }
        finally { release.TrySetResult(); }
        Assert.True((await download.WaitAsync(Deadline)).Success);
    }

    [Fact]
    public async Task Installation_preflight_reserves_requests_and_shutdown_does_not_await_its_own_handoff()
    {
        var client = new FakeClient { Check = _ => Task.FromResult<AvailableUpdate?>(Offer()), Download = (_, _, _) => Task.FromResult("installer") };
        await using var coordinator = Create(client);
        await coordinator.CheckAsync(automatic: true);
        await using var installation = coordinator.TryPrepareInstallation(automatic: true);
        Assert.NotNull(installation);
        Assert.Equal("installer", installation.File);
        Assert.Equal("0.2.0", installation.Package.Version);
        Assert.Equal(UpdateOperationOutcome.Busy, (await coordinator.CheckAsync()).Outcome);
        Assert.Equal(UpdateOperationOutcome.Busy, (await coordinator.DownloadAsync(UpdatePackageKind.Installer)).Outcome);
        Assert.True(installation.TryCommit());
        // This is the call graph used by App: install -> normal shutdown -> stop management.
        // Stop may drain cancellation and HTTP, but cannot await installation's own completion.
        await coordinator.CancelAndDrainAsync().WaitAsync(Deadline);
        Assert.Equal(UpdatePhase.Stopped, coordinator.Snapshot.Phase);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("cancel")]
    [InlineData("stop")]
    public async Task Automatic_preflight_cannot_commit_after_disable_cancel_or_shutdown(string reason)
    {
        var client = new FakeClient { Check = _ => Task.FromResult<AvailableUpdate?>(Offer()), Download = (_, _, _) => Task.FromResult("installer") };
        await using var coordinator = Create(client);
        await coordinator.CheckAsync(automatic: true);
        await using var installation = coordinator.TryPrepareInstallation(automatic: true);
        Assert.NotNull(installation);
        if (reason == "disable") await coordinator.SetAutomaticChecksAsync(false);
        else if (reason == "cancel") await coordinator.CancelCurrentAsync();
        else await coordinator.CancelAndDrainAsync().WaitAsync(Deadline);
        Assert.True(installation.Token.IsCancellationRequested);
        Assert.False(installation.TryCommit());
    }

    [Fact]
    public async Task A_manual_download_is_not_silently_promoted_to_an_automatic_installation()
    {
        var client = new FakeClient { Check = _ => Task.FromResult<AvailableUpdate?>(Offer()), Download = (_, _, _) => Task.FromResult("installer") };
        await using var coordinator = Create(client);
        await coordinator.CheckAsync();
        await coordinator.DownloadAsync(UpdatePackageKind.Installer);
        Assert.Null(coordinator.TryPrepareInstallation(automatic: true));
        await using var manual = coordinator.TryPrepareInstallation(automatic: false);
        Assert.NotNull(manual);
        await coordinator.SetAutomaticChecksAsync(false);
        Assert.True(manual.TryCommit());
    }

    [Fact]
    public async Task Failed_installation_preparation_releases_reservation_for_a_manual_retry()
    {
        var client = new FakeClient { Check = _ => Task.FromResult<AvailableUpdate?>(Offer()), Download = (_, _, _) => Task.FromResult("installer") };
        await using var coordinator = Create(client);
        await coordinator.CheckAsync(automatic: true);
        var installation = coordinator.TryPrepareInstallation(automatic: true);
        Assert.NotNull(installation);
        await installation.DisposeAsync(); // Verification or Process.Start failed; app stays open.
        Assert.Equal(UpdatePhase.Downloaded, coordinator.Snapshot.Phase);
        Assert.False(coordinator.ShouldCheckAutomatically());
        await using var retry = coordinator.TryPrepareInstallation(automatic: false);
        Assert.NotNull(retry);
        Assert.True(retry.TryCommit());
    }

    [Fact]
    public async Task Automatic_download_waits_for_persisted_attempt_and_stops_on_store_failure()
    {
        var entered = Signal(); var release = Signal();
        var store = new MemoryStore { SaveError = "disk unavailable", BeforeAttempt = async () => { entered.TrySetResult(); await release.Task; } };
        var client = new FakeClient { Check = _ => Task.FromResult<AvailableUpdate?>(Offer()) };
        await using var coordinator = Create(client, store);
        var cycle = coordinator.CheckAsync(automatic: true);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.False(cycle.IsCompleted);
            Assert.Equal(0, client.DownloadCount);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(UpdateOperationOutcome.Failed, (await cycle.WaitAsync(Deadline)).Outcome);
        Assert.Equal(0, client.DownloadCount);
        Assert.Equal("disk unavailable", coordinator.Snapshot.Error);
        Assert.Null(coordinator.TryPrepareInstallation(automatic: true));
    }

    [Fact]
    public async Task Stop_during_automatic_download_drains_client_and_never_produces_an_installation()
    {
        var entered = Signal(); var release = Signal();
        var client = new FakeClient
        {
            Check = _ => Task.FromResult<AvailableUpdate?>(Offer()),
            Download = async (_, _, _) => { entered.TrySetResult(); await release.Task; return "late-file"; }
        };
        await using var coordinator = Create(client);
        var cycle = coordinator.CheckAsync(automatic: true);
        await entered.Task.WaitAsync(Deadline);
        var stopped = coordinator.CancelAndDrainAsync();
        Assert.False(stopped.IsCompleted);
        Assert.Null(coordinator.TryPrepareInstallation(automatic: true));
        release.TrySetResult();
        await stopped.WaitAsync(Deadline);
        Assert.Equal(UpdateOperationOutcome.Canceled, (await cycle).Outcome);
        Assert.Null(coordinator.Snapshot.DownloadedFile);
    }

    [Fact]
    public async Task Failed_check_is_not_success_and_is_throttled_across_restart()
    {
        var clock = new Clock(); var store = new MemoryStore();
        var client = new FakeClient { Check = _ => throw new IOException("offline") };
        await using (var coordinator = Create(client, store, clock))
        {
            Assert.Equal(UpdateOperationOutcome.Failed, (await coordinator.CheckAsync(automatic: true)).Outcome);
            Assert.Equal("offline", coordinator.Snapshot.Error);
            Assert.Null(coordinator.Snapshot.LastSuccessfulCheckUtc);
            Assert.False(coordinator.ShouldCheckAutomatically());
        }
        await using var restarted = Create(client, store, clock);
        Assert.False(restarted.ShouldCheckAutomatically());
        clock.Advance(TimeSpan.FromHours(24));
        Assert.True(restarted.ShouldCheckAutomatically());
        Assert.Equal(UpdateOperationOutcome.Failed, (await restarted.CheckAsync(automatic: true)).Outcome);
        Assert.Null(store.Settings.LastSuccessfulCheckUtc);
    }

    [Fact]
    public async Task Future_persisted_time_can_recover_but_monotonic_time_prevents_repeated_attempts_after_clock_rollback()
    {
        var clock = new Clock();
        var store = new MemoryStore { Settings = new(LastCheckUtc: clock.GetUtcNow().AddDays(2)) };
        var client = new FakeClient { Check = _ => throw new IOException("offline") };
        await using var coordinator = Create(client, store, clock);
        Assert.True(coordinator.ShouldCheckAutomatically());
        await coordinator.CheckAsync(automatic: true);
        clock.Utc -= TimeSpan.FromDays(3);
        Assert.False(coordinator.ShouldCheckAutomatically());
        clock.Advance(TimeSpan.FromHours(24));
        Assert.True(coordinator.ShouldCheckAutomatically());
    }

    [Fact]
    public async Task An_in_process_automatic_check_becomes_due_after_24_monotonic_hours_even_when_utc_moved_back_one_hour()
    {
        var clock = new Clock();
        await using var coordinator = Create(new(), clock: clock);
        Assert.True((await coordinator.CheckAsync(automatic: true)).Success);
        clock.Utc -= TimeSpan.FromHours(1);
        clock.Advance(TimeSpan.FromHours(24));
        Assert.True(coordinator.ShouldCheckAutomatically());
        Assert.True((await coordinator.CheckAsync(automatic: true)).Success);
        clock.Utc += TimeSpan.FromDays(2);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.False(coordinator.ShouldCheckAutomatically());
    }

    [Fact]
    public async Task Slow_timestamp_persistence_does_not_block_manual_checks_but_shutdown_drains_it()
    {
        var writeEntered = Signal(); var releaseWrite = Signal();
        var store = new MemoryStore { BeforeAttempt = async () => { writeEntered.TrySetResult(); await releaseWrite.Task; } };
        var client = new FakeClient();
        await using var coordinator = Create(client, store);
        Assert.True((await coordinator.CheckAsync().WaitAsync(Deadline)).Success);
        await writeEntered.Task.WaitAsync(Deadline);
        Assert.True((await coordinator.CheckAsync().WaitAsync(Deadline)).Success);
        Assert.Equal(2, client.CheckCount);
        var stopped = coordinator.CancelAndDrainAsync();
        Assert.False(stopped.IsCompleted);
        releaseWrite.TrySetResult();
        await stopped.WaitAsync(Deadline);
        Assert.Equal(UpdatePhase.Stopped, coordinator.Snapshot.Phase);
    }

    [Fact]
    public async Task Corrupt_management_disables_automatic_checks_but_manual_check_still_runs_and_preserves_warning()
    {
        var store = new MemoryStore { LoadError = "unknown schema", SaveError = "unknown schema", CanSave = false };
        var client = new FakeClient();
        await using var coordinator = Create(client, store);
        Assert.False(coordinator.ShouldCheckAutomatically());
        Assert.Equal(UpdateOperationOutcome.Skipped, (await coordinator.CheckAsync(automatic: true)).Outcome);
        Assert.True((await coordinator.CheckAsync()).Success);
        Assert.Equal(1, client.CheckCount);
        Assert.Equal("unknown schema", coordinator.Snapshot.SettingsError);
        Assert.False(coordinator.Snapshot.CanSaveSettings);
        Assert.Equal(UpdateOperationOutcome.Failed, (await coordinator.SetAutomaticChecksAsync(false)).Outcome);
        Assert.True(coordinator.Snapshot.AutoCheckEnabled);
    }

    [Fact]
    public async Task Failed_queued_preference_changes_return_to_last_successfully_saved_choice()
    {
        var entered = Signal(); var release = Signal();
        var store = new MemoryStore { SaveError = "locked", BeforePreference = async () => { entered.TrySetResult(); await release.Task; } };
        await using var coordinator = Create(new(), store);
        var off = coordinator.SetAutomaticChecksAsync(false);
        await entered.Task.WaitAsync(Deadline);
        Assert.False(coordinator.Snapshot.AutoCheckEnabled);
        var on = coordinator.SetAutomaticChecksAsync(true);
        release.TrySetResult();
        Assert.Equal(UpdateOperationOutcome.Failed, (await off.WaitAsync(Deadline)).Outcome);
        Assert.Equal(UpdateOperationOutcome.Failed, (await on.WaitAsync(Deadline)).Outcome);
        Assert.True(coordinator.Snapshot.AutoCheckEnabled);
        Assert.Equal("locked", coordinator.Snapshot.SettingsError);
    }

    [Fact]
    public async Task Shutdown_waits_for_ignored_cancellation_and_drops_late_results_and_all_future_requests()
    {
        var entered = Signal(); var cancellationObserved = Signal(); var release = Signal();
        var client = new FakeClient { Check = async token =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            entered.TrySetResult(); await release.Task; return Offer();
        } };
        await using var coordinator = Create(client);
        var events = new ConcurrentQueue<UpdateSnapshot>();
        coordinator.Changed += events.Enqueue;
        var check = coordinator.CheckAsync();
        await entered.Task.WaitAsync(Deadline);
        var stopped = coordinator.CancelAndDrainAsync();
        await cancellationObserved.Task.WaitAsync(Deadline);
        Assert.False(stopped.IsCompleted);
        Assert.Equal(UpdateOperationOutcome.Stopped, (await coordinator.CheckAsync()).Outcome);
        Assert.Equal(UpdateOperationOutcome.Stopped, (await coordinator.DownloadAsync(UpdatePackageKind.Installer)).Outcome);
        release.TrySetResult();
        await stopped.WaitAsync(Deadline);
        Assert.Equal(UpdateOperationOutcome.Canceled, (await check).Outcome);
        Assert.Null(coordinator.Snapshot.Available);
        Assert.Null(coordinator.Snapshot.LastSuccessfulCheckUtc);
        Assert.Equal(UpdatePhase.Stopped, coordinator.Snapshot.Phase);
        Assert.DoesNotContain(events, item => item.Phase == UpdatePhase.Available);
        Assert.Equal(events.Select(item => item.Version).Order(), events.Select(item => item.Version));
        Assert.Equal(UpdatePhase.Stopped, events.Last().Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_cancel_and_shutdown_share_the_first_cancellation_task_until_its_callbacks_finish(bool throughCaller)
    {
        var clientEntered = Signal(); var returnClient = Signal(); var clientReturned = Signal(); var callbackEntered = Signal();
        using var releaseCallback = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        CancellationTokenRegistration registration = default;
        var client = new FakeClient { Check = async token =>
        {
            registration = token.UnsafeRegister(_ =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Wait();
            }, null);
            clientEntered.TrySetResult();
            await returnClient.Task;
            clientReturned.TrySetResult();
            return Offer();
        } };
        var coordinator = Create(client);
        try
        {
            var check = coordinator.CheckAsync(cancellationToken: caller.Token);
            await clientEntered.Task.WaitAsync(Deadline);
            Task firstCancel;
            if (throughCaller)
            {
                // Keep a linked-CTS regression bounded: a synchronous blocked caller cancellation
                // must not prevent finally from releasing the deliberately blocked callback.
                firstCancel = Task.Run(() => caller.Cancel());
            }
            else firstCancel = coordinator.CancelCurrentAsync();
            await callbackEntered.Task.WaitAsync(Deadline);
            if (throughCaller) await firstCancel.WaitAsync(Deadline);
            returnClient.TrySetResult();
            await clientReturned.Task.WaitAsync(Deadline);

            // The client task has returned, but the first cancellation still owns a running callback.
            await Assert.ThrowsAsync<TimeoutException>(() => check.WaitAsync(TimeSpan.FromMilliseconds(100)));
            var repeatedCancel = coordinator.CancelCurrentAsync();
            Assert.False(repeatedCancel.IsCompleted);
            Assert.Equal(UpdateOperationOutcome.Busy, (await coordinator.CheckAsync()).Outcome);
            var stop = coordinator.CancelAndDrainAsync();
            await Assert.ThrowsAsync<TimeoutException>(() => stop.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.Equal(UpdateOperationOutcome.Stopped, (await coordinator.CheckAsync()).Outcome);
            releaseCallback.Set();
            await Task.WhenAll(firstCancel, repeatedCancel, stop).WaitAsync(Deadline);
            Assert.Equal(UpdateOperationOutcome.Canceled, (await check.WaitAsync(Deadline)).Outcome);
            Assert.Equal(UpdatePhase.Stopped, coordinator.Snapshot.Phase);
            Assert.Null(coordinator.Snapshot.Available);
        }
        finally
        {
            returnClient.TrySetResult();
            releaseCallback.Set();
            await coordinator.CancelAndDrainAsync().WaitAsync(Deadline);
            await registration.DisposeAsync();
        }
    }

    [Fact]
    public async Task Subscribers_can_read_state_and_request_another_operation_without_holding_the_state_lock()
    {
        var observed = Signal();
        await using var coordinator = Create(new());
        coordinator.Changed += _ => throw new InvalidOperationException("a broken subscriber");
        coordinator.Changed += state =>
        {
            _ = coordinator.Snapshot;
            if (state.Phase == UpdatePhase.Checking)
            {
                // Another thread must be able to acquire the state gate while this callback runs.
                using var unlocked = new ManualResetEventSlim();
                _ = Task.Run(() => { _ = coordinator.Snapshot; unlocked.Set(); });
                Assert.True(unlocked.Wait(Deadline));
                observed.TrySetResult();
            }
        };
        await coordinator.CheckAsync();
        await observed.Task.WaitAsync(Deadline);
    }

    private static UpdateCoordinator Create(FakeClient client, MemoryStore? store = null, Clock? clock = null) =>
        new("0.1.0", Path.Combine(Path.GetDirectoryName(ManagementStoreTests.NewPath())!, "updates"), client, store ?? new(), clock ?? new());
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AvailableUpdate Offer() => new("0.2.0", new("https://github.com/Aice-Chen/BatteryManager/releases/tag/v0.2.0"),
        Array.AsReadOnly(new[] { new UpdatePackage("0.2.0", UpdatePackageKind.Installer, "BatteryManager-0.2.0-win-x64-setup.exe", 100,
            new string('a', 64), new("https://github.com/Aice-Chen/BatteryManager/releases/download/v0.2.0/BatteryManager-0.2.0-win-x64-setup.exe")) }));

    private sealed class FakeClient : IReleaseUpdateClient
    {
        private int _checks, _downloads;
        public int CheckCount => Volatile.Read(ref _checks);
        public int DownloadCount => Volatile.Read(ref _downloads);
        public Func<CancellationToken, Task<AvailableUpdate?>> Check { get; init; } = _ => Task.FromResult<AvailableUpdate?>(null);
        public Func<UpdatePackage, IProgress<long>?, CancellationToken, Task<string>> Download { get; init; } = (_, _, _) => throw new InvalidOperationException("Unexpected download");
        public Task<AvailableUpdate?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _checks); return Check(cancellationToken); }
        public Task<string> DownloadAsync(UpdatePackage package, string cacheDirectory, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _downloads); return Download(package, progress, cancellationToken); }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan amount) { Utc += amount; _ticks += amount.Ticks; }
    }

    private sealed class MemoryStore : IManagementStore
    {
        public ManagementSettings Settings { get; set; } = new();
        public string? LoadError { get; init; }
        public string? SaveError { get; init; }
        public bool CanSave { get; init; } = true;
        public Func<Task>? BeforeAttempt { get; init; }
        public Func<Task>? BeforePreference { get; init; }
        public ManagementStoreResult Load() => new(LoadError is null, Settings, LoadError, CanSave);
        public async Task<ManagementStoreResult> SetAutomaticChecksAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (BeforePreference is not null) await BeforePreference();
            return Save(settings => settings with { AutoCheckUpdates = enabled });
        }
        public async Task<ManagementStoreResult> RecordCheckAttemptAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
        {
            if (BeforeAttempt is not null) await BeforeAttempt();
            return Save(settings => settings with { LastCheckUtc = at });
        }
        public Task<ManagementStoreResult> RecordSuccessfulCheckAsync(DateTimeOffset at, CancellationToken cancellationToken = default) =>
            Task.FromResult(Save(settings => settings with { LastSuccessfulCheckUtc = at }));
        private ManagementStoreResult Save(Func<ManagementSettings, ManagementSettings> change)
        {
            if (SaveError is not null) return new(false, Settings, SaveError, CanSave);
            Settings = change(Settings);
            return new(true, Settings);
        }
    }
}
