using System.Text;
using System.Text.Json;
using BatteryManager.Application.Management;
using Xunit;

namespace BatteryManager.Application.Tests;

public sealed class ManagementStoreTests
{
    [Fact]
    public void Missing_file_defaults_to_automatic_checks_without_creating_anything()
    {
        var path = NewPath();
        var result = new ManagementStore(path).Load();
        Assert.True(result.Success);
        Assert.True(result.Settings.AutoCheckUpdates);
        Assert.Null(result.Settings.LastCheckUtc);
        Assert.Null(result.Settings.LastSuccessfulCheckUtc);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task Preferences_and_attempt_and_success_times_survive_restart_as_separate_fields()
    {
        var path = NewPath();
        var store = new ManagementStore(path);
        var at = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        Assert.True((await store.SetAutomaticChecksAsync(false)).Success);
        Assert.True((await store.RecordCheckAttemptAsync(at)).Success);
        Assert.Null(new ManagementStore(path).Load().Settings.LastSuccessfulCheckUtc);
        Assert.True((await store.RecordSuccessfulCheckAsync(at.AddSeconds(2))).Success);
        var loaded = new ManagementStore(path).Load();
        Assert.True(loaded.Success);
        Assert.False(loaded.Settings.AutoCheckUpdates);
        Assert.Equal(at, loaded.Settings.LastCheckUtc);
        Assert.Equal(at.AddSeconds(2), loaded.Settings.LastSuccessfulCheckUtc);
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { "autoCheckUpdates", "lastCheckUtc", "lastSuccessfulCheckUtc", "schemaVersion" },
            json.RootElement.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schemaVersion\":2,\"autoCheckUpdates\":false,\"lastCheckUtc\":null,\"lastSuccessfulCheckUtc\":null}")]
    [InlineData("{\"schemaVersion\":\"1\",\"autoCheckUpdates\":false,\"lastCheckUtc\":null,\"lastSuccessfulCheckUtc\":null}")]
    [InlineData("{\"schemaVersion\":1,\"autoCheckUpdates\":false,\"autoCheckUpdates\":true,\"lastCheckUtc\":null,\"lastSuccessfulCheckUtc\":null}")]
    [InlineData("{\"schemaVersion\":1,\"autoCheckUpdates\":false,\"lastCheckUtc\":\"yesterday\",\"lastSuccessfulCheckUtc\":null}")]
    [InlineData("{\"schemaVersion\":1,\"autoCheckUpdates\":false,\"lastCheckUtc\":null,\"lastSuccessfulCheckUtc\":null,\"extra\":1}")]
    public async Task Invalid_or_unknown_file_cannot_be_overwritten_by_preferences_or_check_results(string original)
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(original);
        await File.WriteAllBytesAsync(path, bytes);
        var store = new ManagementStore(path);
        var load = store.Load();
        Assert.False(load.Success);
        Assert.False(load.CanSave);
        Assert.False((await store.SetAutomaticChecksAsync(true)).Success);
        Assert.False((await store.RecordCheckAttemptAsync(DateTimeOffset.UtcNow)).Success);
        Assert.False((await store.RecordSuccessfulCheckAsync(DateTimeOffset.UtcNow)).Success);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task Different_store_instances_preserve_each_others_fields()
    {
        var path = NewPath();
        var first = new ManagementStore(path);
        var second = new ManagementStore(path);
        first.Load(); second.Load();
        Assert.True((await first.SetAutomaticChecksAsync(false)).Success);
        var at = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        Assert.True((await second.RecordCheckAttemptAsync(at)).Success);
        Assert.True((await first.RecordSuccessfulCheckAsync(at.AddSeconds(1))).Success);
        var final = new ManagementStore(path).Load().Settings;
        Assert.False(final.AutoCheckUpdates);
        Assert.Equal(at, final.LastCheckUtc);
        Assert.Equal(at.AddSeconds(1), final.LastSuccessfulCheckUtc);
    }

    [Fact]
    public async Task Failed_atomic_replace_keeps_original_bytes_and_removes_temporary_file()
    {
        var path = NewPath();
        var store = new ManagementStore(path);
        Assert.True((await store.SetAutomaticChecksAsync(false)).Success);
        var original = await File.ReadAllBytesAsync(path);
        using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = await store.SetAutomaticChecksAsync(true);
            Assert.False(failed.Success);
            Assert.False(failed.Settings.AutoCheckUpdates);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task A_pre_canceled_save_does_not_create_a_directory()
    {
        var path = NewPath();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ManagementStore(path).SetAutomaticChecksAsync(false, stop.Token));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    internal static string NewPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Project root not found for test artifacts.");
        return Path.Combine(root.FullName, ".artifacts", "tests", "management", Guid.NewGuid().ToString("N"), "management.json");
    }
}
