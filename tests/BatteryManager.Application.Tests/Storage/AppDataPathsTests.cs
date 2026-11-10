using BatteryManager.Application;
using Xunit;

namespace BatteryManager.Application.Tests;

public sealed class AppDataPathsTests
{
    internal const string Marker = "{\"schemaVersion\":1,\"product\":\"BatteryManager\",\"scope\":\"currentUser\"}";

    [Fact]
    public void Portable_and_installed_paths_are_selected_without_creating_directories()
    {
        var root = NewRoot();
        var app = Path.Combine(root, "app");
        var local = Path.Combine(root, "local");
        var portable = AppDataPaths.Resolve(app, local);
        Assert.False(portable.IsInstalled);
        Assert.Equal(app, portable.DataDirectory);
        Assert.Equal(Path.Combine(app, "settings.json"), portable.SettingsPath);
        var installed = AppDataPaths.Parse(app, local, Marker);
        Assert.True(installed.IsInstalled);
        Assert.Equal(Path.Combine(local, "BatteryManager"), installed.DataDirectory);
        Assert.Equal(Path.Combine(installed.DataDirectory, "management.json"), installed.ManagementPath);
        Assert.Equal(Path.Combine(installed.DataDirectory, "logs"), installed.LogsDirectory);
        Assert.Equal(Path.Combine(installed.DataDirectory, "updates"), installed.UpdatesDirectory);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Resolve_reads_only_the_explicit_app_directory_marker()
    {
        var root = NewRoot();
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "installed.json"), Marker);
        var local = Path.Combine(root, "local");
        var paths = AppDataPaths.Resolve(app, local);
        Assert.True(paths.IsInstalled);
        Assert.False(Directory.Exists(local));
        Assert.Single(Directory.GetFiles(app));
    }

    [Fact]
    public void Shipped_installer_marker_selects_installed_storage_without_writing_any_data()
    {
        var marker = File.ReadAllText(Path.Combine(ProjectRoot(), "packaging", "windows", "installed.json"));
        var root = NewRoot();
        var paths = AppDataPaths.Parse(Path.Combine(root, "app"), Path.Combine(root, "local"), marker);
        Assert.True(paths.IsInstalled);
        Assert.Equal(Path.Combine(root, "local", "BatteryManager"), paths.DataDirectory);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"product\":\"BatteryManager\",\"scope\":\"currentUser\"}")]
    [InlineData("{\"schemaVersion\":1,\"product\":\"Other\",\"scope\":\"currentUser\"}")]
    [InlineData("{\"schemaVersion\":1,\"product\":\"BatteryManager\",\"scope\":\"allUsers\"}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"product\":\"BatteryManager\",\"scope\":\"currentUser\"}")]
    [InlineData("{\"schemaVersion\":1,\"product\":\"BatteryManager\",\"scope\":\"currentUser\",\"path\":\"elsewhere\"}")]
    public void Invalid_marker_never_silently_switches_to_portable_storage(string marker)
    {
        var root = NewRoot();
        Assert.Throws<InvalidDataException>(() => AppDataPaths.Parse(Path.Combine(root, "app"), Path.Combine(root, "local"), marker));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Oversized_or_invalid_utf8_marker_is_rejected_without_creating_user_data()
    {
        var root = NewRoot();
        var app = Path.Combine(root, "app");
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(app);
        var path = Path.Combine(app, "installed.json");
        File.WriteAllBytes(path, new byte[4097]);
        Assert.Throws<InvalidDataException>(() => AppDataPaths.Resolve(app, local));
        File.WriteAllBytes(path, [0xFF, 0xFE, 0xFF]);
        Assert.Throws<InvalidDataException>(() => AppDataPaths.Resolve(app, local));
        Assert.False(Directory.Exists(local));
    }

    [Fact]
    public void Relative_paths_are_rejected_instead_of_depending_on_working_directory()
    {
        var root = NewRoot();
        Assert.Throws<ArgumentException>(() => AppDataPaths.Parse("relative", root, null));
        Assert.Throws<ArgumentException>(() => AppDataPaths.Parse(root, "relative", Marker));
    }

    internal static string NewRoot() => Path.Combine(ProjectRoot(), ".artifacts", "tests", "storage", Guid.NewGuid().ToString("N"));

    private static string ProjectRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Project root not found for test artifacts.");
        return root.FullName;
    }
}
