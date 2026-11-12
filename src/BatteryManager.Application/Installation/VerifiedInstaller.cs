using System.Security.Cryptography;
using System.Diagnostics;
using BatteryManager.Application.Updates;

namespace BatteryManager.Application.Installation;

// Keep a read-only lease through Process.Start so the verified file cannot be
// replaced between the final checksum and the OS opening its executable image.
internal sealed class VerifiedInstaller : IDisposable
{
    private readonly object gate = new();
    private FileStream? lease;
    private VerifiedInstaller(string path, FileStream lease) { Path = path; this.lease = lease; }
    public string Path { get; }

    public static ProcessStartInfo CreateStartInfo(string path, string appDirectory, int processId,
        bool automatic, string? fixtureLog = null, string? waitToken = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processId, 1);
        if (automatic && (waitToken is null || waitToken.Length != 32 || waitToken.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
            throw new ArgumentException("Automatic installation requires its exact handoff token.", nameof(waitToken));
        if (!automatic && waitToken is not null) throw new ArgumentException("A handoff token requires automatic installation.", nameof(waitToken));
        var start = new ProcessStartInfo(path) { UseShellExecute = true };
        start.ArgumentList.Add("/CURRENTUSER"); start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/NOCLOSEAPPLICATIONS");
        start.ArgumentList.Add("/WAITPID=" + processId);
        start.ArgumentList.Add("/DIR=" + appDirectory);
        if (automatic) { start.ArgumentList.Add("/AUTOUPDATE=1"); start.ArgumentList.Add("/WAITTOKEN=" + waitToken); }
        if (automatic || fixtureLog is not null)
        {
            start.ArgumentList.Add("/VERYSILENT"); start.ArgumentList.Add("/SUPPRESSMSGBOXES"); start.ArgumentList.Add("/SP-");
        }
        if (fixtureLog is not null) start.ArgumentList.Add("/LOG=" + fixtureLog);
        return start;
    }

    public static async Task<VerifiedInstaller> OpenAsync(string path, string cacheDirectory, UpdatePackage package,
        CancellationToken token = default)
    {
        _ = ReleaseVersion.ParseStable(package.Version);
        var expectedName = $"BatteryManager-{package.Version}-win-x64-setup.exe";
        var expectedPath = System.IO.Path.Combine(System.IO.Path.GetFullPath(cacheDirectory), expectedName);
        if (package.Kind != UpdatePackageKind.Installer || package.FileName != expectedName ||
            !string.Equals(System.IO.Path.GetFullPath(path), expectedPath, StringComparison.OrdinalIgnoreCase) ||
            package.Size <= 0 || package.Size > 1024L * 1024 * 1024 || package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("安装包位置或版本不匹配，请重新下载。");
        var input = new FileStream(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (input.Length != package.Size || !CryptographicOperations.FixedTimeEquals(
                await SHA256.HashDataAsync(input, token), Convert.FromHexString(package.Sha256)))
                throw new InvalidDataException("安装包已发生变化，请重新下载。");
            return new(expectedPath, input);
        }
        catch { await input.DisposeAsync(); throw; }
    }
    public void Launch(Action<string> launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(lease is null, this);
            // Dispose on another thread must not release the lease before the OS opens the image.
            launcher(Path);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            var current = lease;
            lease = null;
            current?.Dispose();
        }
    }
}
