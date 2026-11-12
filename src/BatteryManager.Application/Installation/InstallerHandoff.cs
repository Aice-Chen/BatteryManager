using System.Diagnostics;
using System.Runtime.Versioning;

namespace BatteryManager.Application.Installation;

// Setup acknowledges validated identity before the app commits to shutdown. Its 30-second
// exit timeout starts only after Ready, so configuration/native drain has no installer deadline.
[SupportedOSPlatform("windows")]
internal sealed class InstallerHandoff : IDisposable
{
    private readonly EventWaitHandle accepted;
    private readonly EventWaitHandle ready;
    private readonly EventWaitHandle cancel;
    private bool wasAccepted;
    private bool wasReady;
    private bool wasCanceled;
    private bool disposed;

    public InstallerHandoff()
    {
        Token = Guid.NewGuid().ToString("N");
        EventPrefix = @"Local\BatteryManager.AutoUpdate." + Token;
        accepted = Create(".Accepted");
        try
        {
            ready = Create(".Ready");
            try { cancel = Create(".Cancel"); }
            catch { ready.Dispose(); throw; }
        }
        catch { accepted.Dispose(); throw; }
    }
    private EventWaitHandle Create(string suffix)
    {
        var signal = new EventWaitHandle(false, EventResetMode.ManualReset, EventPrefix + suffix, out var created);
        if (created) return signal;
        signal.Dispose();
        throw new IOException("安装交接标识已被占用，请重试。");
    }
    public string Token { get; }
    public string EventPrefix { get; }

    public async Task WaitForAcceptanceAsync(Func<bool> installerExited, CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (accepted.WaitOne(0)) { wasAccepted = true; return; }
            if (installerExited()) throw new IOException("安装程序未接受更新交接，程序继续运行。请重试。");
            if (elapsed.Elapsed >= (timeout ?? TimeSpan.FromSeconds(30)))
                throw new TimeoutException("安装程序未及时确认更新交接，程序继续运行。请重试。");
            await Task.Delay(50, cancellationToken);
        }
    }
    public void SignalReady()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!wasAccepted) throw new InvalidOperationException("Installer has not accepted the handoff.");
        if (wasCanceled) throw new InvalidOperationException("Installer handoff was canceled.");
        ready.Set();
        wasReady = true;
    }
    public void Cancel()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (wasReady) return;
        cancel.Set();
        wasCanceled = true;
    }
    public void Dispose()
    {
        if (disposed) return;
        Cancel();
        disposed = true;
        accepted.Dispose(); ready.Dispose(); cancel.Dispose();
    }
}
