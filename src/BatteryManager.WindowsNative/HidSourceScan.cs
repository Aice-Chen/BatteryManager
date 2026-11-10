using System.Collections.Concurrent;
using BatteryManager.Core;

namespace BatteryManager.WindowsNative;

/// <summary>Owns one HID adapter's inventory and physical scan lifetime. Device
/// identification, request ordering and connection semantics remain in the adapter.</summary>
internal sealed class HidSourceScan(IWindowsHidIo io, string sourceId, string label)
{
    private int active;

    public async Task ReadAsync<T>(Func<IReadOnlyList<HidInterface>, IReadOnlyList<T>> select,
        Func<T, CancellationToken, Task<DeviceObservation?>> read, Action<SourceBatch> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException($"The previous {label} scan has not physically completed.");
        try
        {
            var inventory = await io.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var complete = inventory.Complete && inventory.Errors.Count == 0;
            if (!complete)
                publish(new(sourceId, [], Error: inventory.Errors.Count > 0
                    ? string.Join("; ", inventory.Errors) : "HID enumeration was incomplete.", FailureStatus: ReadStatus.Error));
            var candidates = select(inventory.Interfaces);
            var observations = new ConcurrentBag<DeviceObservation>();
            await Parallel.ForEachAsync(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
                async (device, token) =>
                {
                    var observation = await read(device, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (observation is null) return;
                    observations.Add(observation);
                    publish(new(sourceId, [observation]));
                }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Incomplete inventories never revoke unseen devices. Only final
            // observations are repeated; adapters' earlier connection evidence
            // has already entered the state's seen set with its original time.
            if (complete) publish(new(sourceId, observations.ToArray(), Complete: true, Authoritative: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!cancellationToken.IsCancellationRequested)
                publish(new(sourceId, [], Error: Describe(error), FailureStatus: ReadStatus.Error));
        }
        // read includes channel disposal. Do not release this barrier on a
        // logical timeout while an adapter still owns native I/O.
        finally { Volatile.Write(ref active, 0); }
    }

    internal static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message} (0x{error.HResult:X8})";
}
