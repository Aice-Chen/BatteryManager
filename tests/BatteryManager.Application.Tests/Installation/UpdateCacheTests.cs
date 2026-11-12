using System.Diagnostics;
using System.Text;
using BatteryManager.Application.Installation;
using Xunit;

namespace BatteryManager.Application.Tests;

public sealed class UpdateCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redirected_cache_or_ancestor_does_not_delete_files_at_its_target(bool ancestor)
    {
        var directory = Fixture();
        var target = Path.Combine(directory, "target");
        var targetCache = ancestor ? Path.Combine(target, "cache") : target;
        Directory.CreateDirectory(targetCache);
        var file = Path.Combine(targetCache, "BatteryManager-0.2.1-win-x64-setup.exe");
        File.WriteAllText(file, "outside");
        var link = Path.Combine(directory, "link");
        var command = $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        var result = await UpdateCache.CleanupAsync(ancestor ? Path.Combine(link, "cache") : link, "0.2.1");
        Assert.NotNull(result.Error);
        Assert.Empty(result.RemovedFiles);
        Assert.Equal("outside", File.ReadAllText(file));
        // Remove only the junction itself. The target remains an evidence fixture.
        Directory.Delete(link);
    }

    [Fact]
    public async Task Removes_completed_and_abandoned_downloads_but_preserves_future_packages_and_unowned_files()
    {
        var directory = Fixture();
        string[] obsolete = ["BatteryManager-0.1.0-win-x64.zip", "BatteryManager-0.2.1-win-x64-setup.exe", Guid.NewGuid().ToString("N") + ".partial"];
        string[] preserved = ["BatteryManager-0.3.0-win-x64-setup.exe", "notes.txt", "other.partial", "BatteryManager-01.2.1-win-x64-setup.exe", "BatteryManager-0.2.1-win-arm64-setup.exe"];
        foreach (var name in obsolete.Concat(preserved)) File.WriteAllText(Path.Combine(directory, name), name);
        var nested = Path.Combine(directory, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, obsolete[0]), "unowned");

        var result = await UpdateCache.CleanupAsync(directory, "0.2.1");

        Assert.Null(result.Error);
        Assert.Empty(result.RemainingFiles);
        Assert.Equal(obsolete.Length, result.RemovedFiles.Count);
        foreach (var name in obsolete) Assert.False(File.Exists(Path.Combine(directory, name)));
        foreach (var name in preserved) Assert.True(File.Exists(Path.Combine(directory, name)));
        Assert.True(File.Exists(Path.Combine(nested, obsolete[0])));
    }

    [Fact]
    public async Task Locked_installer_is_reported_then_removed_after_it_releases_its_handle()
    {
        var directory = Fixture();
        var path = Path.Combine(directory, "BatteryManager-0.2.1-win-x64-setup.exe");
        File.WriteAllText(path, "installer");
        using (var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var blocked = await UpdateCache.CleanupAsync(directory, "0.2.1", maxAttempts: 2, retryDelay: TimeSpan.FromMilliseconds(1));
            Assert.Equal([path], blocked.RemainingFiles);
            Assert.Empty(blocked.RemovedFiles);
            Assert.True(File.Exists(path));
        }
        var retried = await UpdateCache.CleanupAsync(directory, "0.2.1");
        Assert.Empty(retried.RemainingFiles);
        Assert.Equal([path], retried.RemovedFiles);
    }

    [Fact]
    public async Task Retry_uses_initial_snapshot_and_does_not_remove_a_new_active_download()
    {
        var directory = Fixture();
        var oldPartial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        File.WriteAllText(oldPartial, "old");
        using var lease = new FileStream(oldPartial, FileMode.Open, FileAccess.Read, FileShare.Read);
        var cleanup = UpdateCache.CleanupAsync(directory, "0.2.1", maxAttempts: 3, retryDelay: TimeSpan.FromMilliseconds(100));
        var activePartial = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        File.WriteAllText(activePartial, "new");
        lease.Dispose();

        var result = await cleanup;
        Assert.Empty(result.RemainingFiles);
        Assert.False(File.Exists(oldPartial));
        Assert.True(File.Exists(activePartial));
    }

    [Fact]
    public async Task Cancellation_interrupts_retries_and_preserves_the_locked_file()
    {
        var directory = Fixture();
        var path = Path.Combine(directory, "BatteryManager-0.2.1-win-x64-setup.exe");
        File.WriteAllText(path, "installer");
        using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var stop = new CancellationTokenSource();
        var cleanup = UpdateCache.CleanupAsync(directory, "0.2.1", stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Missing_cache_does_not_create_a_directory()
    {
        var directory = Path.Combine(Fixture(), "missing");
        var result = await UpdateCache.CleanupAsync(directory, "0.2.1");
        Assert.Null(result.Error);
        Assert.Empty(result.RemovedFiles);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Preview_does_not_delete_an_installer_for_its_future_stable_release()
    {
        var directory = Fixture();
        var path = Path.Combine(directory, "BatteryManager-0.2.1-win-x64-setup.exe");
        File.WriteAllText(path, "stable");
        await UpdateCache.CleanupAsync(directory, "0.2.1-preview.1");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Invalid_current_version_fails_before_deleting_any_files()
    {
        var directory = Fixture();
        var path = Path.Combine(directory, "BatteryManager-0.2.1-win-x64-setup.exe");
        File.WriteAllText(path, "installer");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateCache.CleanupAsync(directory, "unknown"));
        Assert.True(File.Exists(path));
    }

    private static string Fixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Project root not found.");
        var directory = Path.Combine(root.FullName, ".artifacts", "tests", "update-cache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
