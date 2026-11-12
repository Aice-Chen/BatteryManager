using System.Runtime.Versioning;
using BatteryManager.Application.Installation;
using Xunit;

namespace BatteryManager.Application.Tests;

[SupportedOSPlatform("windows")]
public sealed class InstallerHandoffTests
{
    [Fact]
    public async Task Accepted_is_required_before_ready_and_dispose_does_not_cancel_a_ready_handoff()
    {
        var handoff = new InstallerHandoff();
        using var accepted = EventWaitHandle.OpenExisting(handoff.EventPrefix + ".Accepted");
        using var ready = EventWaitHandle.OpenExisting(handoff.EventPrefix + ".Ready");
        using var cancel = EventWaitHandle.OpenExisting(handoff.EventPrefix + ".Cancel");
        Assert.Throws<InvalidOperationException>(handoff.SignalReady);
        var waiting = handoff.WaitForAcceptanceAsync(() => false);
        Assert.False(waiting.IsCompleted);
        Assert.False(ready.WaitOne(0));
        accepted.Set();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(ready.WaitOne(0)); // Accepted permits normal drain; it does not start exit timeout.
        handoff.SignalReady();
        Assert.True(ready.WaitOne(0));
        handoff.Dispose();
        Assert.False(cancel.WaitOne(0));
    }

    [Theory]
    [InlineData("exited")]
    [InlineData("canceled")]
    [InlineData("timeout")]
    public async Task Failure_before_acceptance_cancels_installer_and_never_signals_ready(string reason)
    {
        var handoff = new InstallerHandoff();
        using var ready = EventWaitHandle.OpenExisting(handoff.EventPrefix + ".Ready");
        using var cancel = EventWaitHandle.OpenExisting(handoff.EventPrefix + ".Cancel");
        using var source = new CancellationTokenSource();
        if (reason == "canceled") source.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => handoff.WaitForAcceptanceAsync(() => reason == "exited",
            source.Token, TimeSpan.FromMilliseconds(40)));
        handoff.Dispose();
        Assert.False(ready.WaitOne(0));
        Assert.True(cancel.WaitOne(0));
    }

    [Fact]
    public async Task Failure_after_acceptance_signals_cancel_before_any_error_dialog_or_disposal()
    {
        using var handoff = new InstallerHandoff();
        using var accepted = EventWaitHandle.OpenExisting(handoff.EventPrefix + ".Accepted");
        using var cancel = EventWaitHandle.OpenExisting(handoff.EventPrefix + ".Cancel");
        using var ready = EventWaitHandle.OpenExisting(handoff.EventPrefix + ".Ready");
        accepted.Set();
        await handoff.WaitForAcceptanceAsync(() => false);
        handoff.Cancel();
        Assert.True(cancel.WaitOne(0));
        Assert.False(ready.WaitOne(0));
        Assert.Throws<InvalidOperationException>(handoff.SignalReady);
    }
}
