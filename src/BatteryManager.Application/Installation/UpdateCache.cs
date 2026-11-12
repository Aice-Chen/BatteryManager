using System.Text.RegularExpressions;
using BatteryManager.Application.Updates;

namespace BatteryManager.Application.Installation;

internal sealed record UpdateCacheCleanupResult(IReadOnlyList<string> RemovedFiles, IReadOnlyList<string> RemainingFiles, string? Error);

internal static partial class UpdateCache
{
    [GeneratedRegex(@"\ABatteryManager-((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))-win-x64(?:-setup\.exe|\.zip)\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackagePattern();

    public static async Task<UpdateCacheCleanupResult> CleanupAsync(string cacheDirectory, string currentVersion,
        CancellationToken token = default, int maxAttempts = 10, TimeSpan? retryDelay = null)
    {
        _ = ReleaseVersion.ParseStable(currentVersion.Split('+', 2)[0].Split('-', 2)[0]);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        var delay = retryDelay ?? TimeSpan.FromSeconds(3);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        token.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(cacheDirectory);
        var removed = new List<string>();
        var remaining = new List<string>();
        try
        {
            EnsureOrdinaryPath(directory);
            if (!Directory.Exists(directory)) return new(removed, remaining, null);
            // Snapshot once. A download started while a locked installer is being retried
            // owns its newly created .partial file and must never enter this cleanup pass.
            remaining.AddRange(Directory.EnumerateFiles(directory).Where(path => IsObsolete(Path.GetFileName(path), currentVersion)));
            for (var attempt = 0; attempt < maxAttempts && remaining.Count > 0; attempt++)
            {
                if (attempt > 0) await Task.Delay(delay, token);
                EnsureOrdinaryPath(directory);
                foreach (var path in remaining.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        EnsureOrdinaryPath(path);
                        File.Delete(path);
                        remaining.Remove(path);
                        removed.Add(path);
                    }
                    catch (IOException) { /* The exiting installer can still hold its image open. */ }
                    catch (UnauthorizedAccessException) { /* Report, never claim a locked file was removed. */ }
                }
            }
            return new(removed, remaining, null);
        }
        catch (IOException error) { return new(removed, remaining, error.Message); }
        catch (UnauthorizedAccessException error) { return new(removed, remaining, error.Message); }
    }

    private static bool IsObsolete(string name, string currentVersion)
    {
        if (name.EndsWith(".partial", StringComparison.Ordinal) && Guid.TryParseExact(name[..^8], "N", out _)) return true;
        var match = PackagePattern().Match(name);
        if (!match.Success) return false;
        try { return !ReleaseVersion.IsNewer(match.Groups[1].Value, currentVersion); }
        catch (InvalidDataException) { return false; }
    }

    private static void EnsureOrdinaryPath(string path)
    {
        // Do not traverse a junction/symlink at the cache, a candidate, or an ancestor.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Update cache cleanup refused a redirected path: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
