using System.Text;
using BatteryManager.Application;
using Xunit;

namespace BatteryManager.Application.Tests;

public sealed class SettingsImporterTests
{
    // Whitespace deliberately differs from the serializer; import must preserve source bytes.
    private const string V1 = "{\n  \"version\" : 1, \"priorityDevices\" : [ { \"id\" : \"keyboard\", \"name\" : \"键盘\" } ]\n}\n";

    [Fact]
    public async Task Explicit_import_preserves_settings_and_original_version_backups_byte_for_byte()
    {
        var (source, destination) = Fixture();
        var files = new Dictionary<string, byte[]>
        {
            ["settings.json"] = Encoding.UTF8.GetBytes(V1),
            ["settings.v1.bak"] = Encoding.UTF8.GetBytes("original v1 backup\n"),
            ["settings.v2.bak"] = Encoding.UTF8.GetBytes("original v2 backup\r\n")
        };
        foreach (var file in files) await File.WriteAllBytesAsync(Path.Combine(source, file.Key), file.Value);
        var result = await SettingsImporter.ImportAsync(source, destination);
        Assert.True(result.Success, result.Error);
        foreach (var file in files)
        {
            Assert.Equal(file.Value, await File.ReadAllBytesAsync(Path.Combine(destination.DataDirectory, file.Key)));
            Assert.Equal(file.Value, await File.ReadAllBytesAsync(Path.Combine(source, file.Key)));
        }
        Assert.Equal(files.Count, Directory.GetFiles(destination.DataDirectory).Length);
        Assert.Equal("keyboard", Assert.Single(new TrayPreferencesStore(destination.SettingsPath).Load().Priorities).DeviceId);
        Assert.Equal(files["settings.json"], await File.ReadAllBytesAsync(destination.SettingsPath));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":99,\"priorityDevices\":[]}")]
    [InlineData("{\"version\":1,\"priorityDevices\":[{\"id\":\"same\",\"name\":\"one\"},{\"id\":\"same\",\"name\":\"two\"}]}")]
    public async Task Invalid_source_is_rejected_before_creating_the_destination(string text)
    {
        var (source, destination) = Fixture();
        await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), text);
        var result = await SettingsImporter.ImportAsync(source, destination);
        Assert.False(result.Success);
        Assert.False(Directory.Exists(destination.DataDirectory));
        Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(source, "settings.json")));
    }

    [Fact]
    public async Task Missing_source_settings_are_not_imported_as_defaults()
    {
        var (source, destination) = Fixture();
        var result = await SettingsImporter.ImportAsync(source, destination);
        Assert.False(result.Success);
        Assert.False(Directory.Exists(destination.DataDirectory));
    }

    [Fact]
    public async Task Existing_target_settings_are_never_overwritten_even_if_invalid()
    {
        var (source, destination) = Fixture();
        await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), V1);
        Directory.CreateDirectory(destination.DataDirectory);
        await File.WriteAllTextAsync(destination.SettingsPath, "keep these original bytes");
        var result = await SettingsImporter.ImportAsync(source, destination);
        Assert.False(result.Success);
        Assert.Equal("keep these original bytes", await File.ReadAllTextAsync(destination.SettingsPath));
        Assert.Single(Directory.GetFiles(destination.DataDirectory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Matching_backups_can_resume_an_import_but_conflicting_backups_are_preserved(bool same)
    {
        var (source, destination) = Fixture();
        await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), V1);
        await File.WriteAllTextAsync(Path.Combine(source, "settings.v1.bak"), "source backup");
        Directory.CreateDirectory(destination.DataDirectory);
        var old = same ? "source backup" : "different target backup";
        await File.WriteAllTextAsync(Path.Combine(destination.DataDirectory, "settings.v1.bak"), old);
        var result = await SettingsImporter.ImportAsync(source, destination);
        Assert.Equal(same, result.Success);
        Assert.Equal(same, File.Exists(destination.SettingsPath));
        Assert.Equal(old, await File.ReadAllTextAsync(Path.Combine(destination.DataDirectory, "settings.v1.bak")));
        Assert.Empty(Directory.GetFiles(destination.DataDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Cancellation_and_same_directory_do_not_change_source_or_create_a_destination()
    {
        var (source, destination) = Fixture();
        await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), V1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SettingsImporter.ImportAsync(source, destination, cancellation.Token));
        Assert.False(Directory.Exists(destination.DataDirectory));
        var same = AppDataPaths.Parse(source, Path.Combine(source, "unused"), null);
        Assert.False((await SettingsImporter.ImportAsync(source, same)).Success);
        Assert.Equal(V1, await File.ReadAllTextAsync(Path.Combine(source, "settings.json")));
        Assert.Single(Directory.GetFiles(source));
    }

    private static (string Source, AppDataPaths Destination) Fixture()
    {
        var root = AppDataPathsTests.NewRoot();
        var source = Path.Combine(root, "old-portable");
        Directory.CreateDirectory(source);
        return (source, AppDataPaths.Parse(Path.Combine(root, "installed-app"), Path.Combine(root, "local-data"), AppDataPathsTests.Marker));
    }
}
