namespace BatteryManager.Core;

/// <summary>Independent sources share delivery and lifecycle without waiting to publish each other's results.</summary>
public sealed class CompositeDeviceSource(params IDeviceSource[] sources) : IDeviceSource
{
    public string Name => string.Join("+", sources.Select(x => x.Name));

    public Task ReadAsync(ScanContext context, Action<SourceBatch> publish, CancellationToken cancellationToken) =>
        Task.WhenAll(sources.Select(source => Task.Run(async () =>
        {
            try { await source.ReadAsync(context, publish, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!cancellationToken.IsCancellationRequested)
                    publish(new(source.Name, [], Error: error.Message, FailureStatus: ReadStatus.Error));
            }
        })));
}
