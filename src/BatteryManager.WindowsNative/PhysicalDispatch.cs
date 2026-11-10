namespace BatteryManager.WindowsNative;

internal sealed class PhysicalDispatch : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim slots;
    private readonly CancellationToken cancellationToken;
    private readonly CancellationTokenRegistration registration;
    private bool closed;

    public PhysicalDispatch(int concurrency, CancellationToken cancellationToken)
    {
        slots = new(concurrency);
        this.cancellationToken = cancellationToken;
        registration = cancellationToken.Register(() => { lock (gate) closed = true; });
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                if (closed || cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                // Admission precedes physical dispatch; canceling later still owns its real completion.
            }
            return await operation().ConfigureAwait(false);
        }
        finally { slots.Release(); }
    }

    public void Dispose() { registration.Dispose(); slots.Dispose(); }
}
