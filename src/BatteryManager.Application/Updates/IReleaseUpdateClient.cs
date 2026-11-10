namespace BatteryManager.Application.Updates;

// Completion means the HTTP/file operation and its cleanup have ended, including cancellation.
internal interface IReleaseUpdateClient
{
    Task<AvailableUpdate?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);
    Task<string> DownloadAsync(UpdatePackage package, string cacheDirectory,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default);
}
