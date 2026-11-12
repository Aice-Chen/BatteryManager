using System.Security.Cryptography;
using BatteryManager.Application.Installation;
using BatteryManager.Application.Updates;
using Xunit;

namespace BatteryManager.Application.Tests;

public sealed class VerifiedInstallerTests
{
    private static readonly byte[] Payload = [1, 2, 3, 4, 5, 6]; // A test fixture, never an executable image.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Installer_arguments_require_automatic_opt_in_and_preserve_exact_wait_target(bool automatic)
    {
        var start = VerifiedInstaller.CreateStartInfo(@"C:\cache\BatteryManager-0.3.0-win-x64-setup.exe",
            @"D:\My Apps\BatteryManager", 12345, automatic, waitToken: automatic ? new string('a', 32) : null);
        Assert.Equal(@"C:\cache\BatteryManager-0.3.0-win-x64-setup.exe", start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Contains("/WAITPID=12345", start.ArgumentList);
        Assert.Contains(@"/DIR=D:\My Apps\BatteryManager", start.ArgumentList);
        Assert.Contains("/NOCLOSEAPPLICATIONS", start.ArgumentList);
        Assert.Contains("/NORESTART", start.ArgumentList);
        Assert.Equal(automatic, start.ArgumentList.Contains("/AUTOUPDATE=1"));
        Assert.Equal(automatic, start.ArgumentList.Contains("/VERYSILENT"));
        Assert.Equal(automatic, start.ArgumentList.Contains("/SUPPRESSMSGBOXES"));
        Assert.Equal(automatic, start.ArgumentList.Contains("/SP-"));
        Assert.Equal(automatic, start.ArgumentList.Contains("/WAITTOKEN=" + new string('a', 32)));
    }

    [Fact]
    public void Silent_manual_fixture_does_not_opt_into_automatic_restart()
    {
        var start = VerifiedInstaller.CreateStartInfo(@"C:\cache\setup.exe", @"D:\Apps\BatteryManager", 23456,
            automatic: false, fixtureLog: @"C:\test evidence\setup.log");
        Assert.Contains("/VERYSILENT", start.ArgumentList);
        Assert.Contains(@"/LOG=C:\test evidence\setup.log", start.ArgumentList);
        Assert.DoesNotContain("/AUTOUPDATE=1", start.ArgumentList);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Automatic_launch_requires_the_exact_lowercase_handshake_token(string? token)
    {
        Assert.Throws<ArgumentException>(() => VerifiedInstaller.CreateStartInfo(@"C:\cache\setup.exe",
            @"D:\Apps\BatteryManager", 23456, automatic: true, waitToken: token));
    }

    [Theory]
    [InlineData("same-length-replacement")]
    [InlineData("changed-length")]
    [InlineData("wrong-hash")]
    public async Task Changed_files_or_checksums_are_rejected_and_the_read_handle_is_released(string change)
    {
        var fixture = Fixture();
        var descriptor = fixture.Package;
        if (change == "same-length-replacement")
        {
            var replacement = Path.Combine(fixture.Directory, "replacement.tmp");
            await File.WriteAllBytesAsync(replacement, [6, 5, 4, 3, 2, 1]);
            File.Move(replacement, fixture.Path, overwrite: true);
        }
        else if (change == "changed-length") await File.WriteAllBytesAsync(fixture.Path, [1, 2]);
        else descriptor = descriptor with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifiedInstaller.OpenAsync(fixture.Path, fixture.Directory, descriptor));
        File.Delete(fixture.Path);
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task A_verified_lease_prevents_writes_deletion_and_actual_file_replacement_through_the_launch_callback()
    {
        var fixture = Fixture();
        var replacement = Path.Combine(fixture.Directory, "replacement.tmp");
        await File.WriteAllBytesAsync(replacement, [6, 5, 4, 3, 2, 1]);
        using (var verified = await VerifiedInstaller.OpenAsync(fixture.Path, fixture.Directory, fixture.Package))
        {
            verified.Launch(path =>
            {
                Assert.Equal(fixture.Path, path);
                Assert.Equal(Payload, File.ReadAllBytes(path));
                Assert.Throws<IOException>(() => File.WriteAllBytes(path, [9]));
                Assert.Throws<IOException>(() => File.Delete(path));
                Assert.Throws<IOException>(() => File.Replace(replacement, path, null));
                Assert.Equal(Payload, File.ReadAllBytes(path));
            });
        }
        File.Move(replacement, fixture.Path, overwrite: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifiedInstaller.OpenAsync(fixture.Path, fixture.Directory, fixture.Package));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("prefix-sibling")]
    [InlineData("wrong-name")]
    public async Task Matching_file_contents_do_not_admit_paths_outside_the_exact_cache_filename(string location)
    {
        var fixture = Fixture();
        var path = location switch
        {
            "outside" => Path.Combine(Path.GetDirectoryName(fixture.Directory)!, "outside", fixture.Package.FileName),
            "prefix-sibling" => Path.Combine(fixture.Directory + "-other", fixture.Package.FileName),
            _ => Path.Combine(fixture.Directory, "other-setup.exe")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Payload);
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifiedInstaller.OpenAsync(path, fixture.Directory, fixture.Package));
        File.Delete(path);
    }

    [Fact]
    public async Task Canonical_aliases_of_the_same_cache_file_are_accepted()
    {
        var fixture = Fixture();
        var path = Path.Combine(fixture.Directory, ".", fixture.Package.FileName);
        using var verified = await VerifiedInstaller.OpenAsync(path, fixture.Directory + Path.DirectorySeparatorChar, fixture.Package);
        Assert.Equal(fixture.Path, verified.Path);
    }

    [Theory]
    [InlineData("zero-size")]
    [InlineData("oversized")]
    [InlineData("malformed-hash")]
    [InlineData("portable")]
    [InlineData("wrong-filename")]
    [InlineData("wrong-version")]
    public async Task Invalid_descriptors_are_rejected_before_granting_a_launch_lease(string invalid)
    {
        var fixture = Fixture();
        var descriptor = invalid switch
        {
            "zero-size" => fixture.Package with { Size = 0 },
            "oversized" => fixture.Package with { Size = 1024L * 1024 * 1024 + 1 },
            "malformed-hash" => fixture.Package with { Sha256 = new string('x', 64) },
            "portable" => fixture.Package with { Kind = UpdatePackageKind.Portable },
            "wrong-filename" => fixture.Package with { FileName = "elsewhere.exe" },
            _ => fixture.Package with { Version = "0.3.0" }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifiedInstaller.OpenAsync(fixture.Path, fixture.Directory, descriptor));
        File.Delete(fixture.Path);
    }

    [Fact]
    public async Task Pre_canceled_verification_releases_the_file_without_a_launchable_object()
    {
        var fixture = Fixture();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifiedInstaller.OpenAsync(fixture.Path, fixture.Directory, fixture.Package, stop.Token));
        File.Delete(fixture.Path);
    }

    [Fact]
    public async Task Launch_failure_retains_the_lock_until_dispose_and_disposed_objects_cannot_launch()
    {
        var fixture = Fixture();
        var verified = await VerifiedInstaller.OpenAsync(fixture.Path, fixture.Directory, fixture.Package);
        try
        {
            Assert.Throws<InvalidOperationException>(() => verified.Launch(_ => throw new InvalidOperationException("simulated launch failure")));
            Assert.Throws<IOException>(() => File.WriteAllBytes(fixture.Path, [9]));
        }
        finally { verified.Dispose(); }
        verified.Dispose();
        Assert.Throws<ObjectDisposedException>(() => verified.Launch(_ => throw new Exception("Must not invoke a launcher after disposal")));
        await File.WriteAllBytesAsync(fixture.Path, [9]);
    }

    [Fact]
    public async Task Concurrent_dispose_cannot_release_the_file_while_the_launcher_is_still_opening_it()
    {
        var fixture = Fixture();
        using var verified = await VerifiedInstaller.OpenAsync(fixture.Path, fixture.Directory, fixture.Package);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launch = Task.Run(() => verified.Launch(_ => { entered.TrySetResult(); release.Wait(); }));
        Task dispose = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            dispose = Task.Run(() => { disposeEntered.TrySetResult(); verified.Dispose(); });
            await disposeEntered.Task.WaitAsync(Deadline);
            await Assert.ThrowsAsync<TimeoutException>(() => dispose.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.Throws<IOException>(() => File.WriteAllBytes(fixture.Path, [9]));
            release.Set();
            await Task.WhenAll(launch, dispose).WaitAsync(Deadline);
            await File.WriteAllBytesAsync(fixture.Path, [9]);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(launch, dispose).WaitAsync(Deadline);
        }
    }

    private static (string Directory, string Path, UpdatePackage Package) Fixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Project root not found for test artifacts.");
        var directory = Path.Combine(root.FullName, ".artifacts", "tests", "installer-validation", Guid.NewGuid().ToString("N"), "cache");
        Directory.CreateDirectory(directory);
        var package = new UpdatePackage("0.2.0", UpdatePackageKind.Installer, "BatteryManager-0.2.0-win-x64-setup.exe",
            Payload.Length, Convert.ToHexString(SHA256.HashData(Payload)),
            new("https://github.com/Aice-Chen/BatteryManager/releases/download/v0.2.0/BatteryManager-0.2.0-win-x64-setup.exe"));
        var path = Path.Combine(directory, package.FileName);
        File.WriteAllBytes(path, Payload);
        return (directory, path, package);
    }
}
