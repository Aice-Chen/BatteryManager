using System.Text.Json;
using BatteryManager.Application;
using BatteryManager.Core;
using Xunit;

namespace BatteryManager.Application.Tests;

public sealed class TrayPreferencesStoreTests
{
    private static readonly TrayPreference First = new("device-00000000000000000001", "键盘");
    private static readonly TrayPreference Second = new("device-00000000000000000002", "鼠标");

    [Fact]
    public async Task MissingFileUsesAutomaticSelectionWithoutWritingDeviceHistory()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        var load = store.Load();
        Assert.True(load.Success);
        Assert.True(load.CanSave);
        Assert.Empty(load.Priorities);
        AssertSaved(await store.SaveAsync([]));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OrderedChoicesAndLabelsPersistAcrossRestartAndOnlyChosenFieldsAreWritten()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        var save = await store.SaveAsync([Second, First]);
        Assert.True(save.Success, save.Error);
        Assert.Equal(new[] { Second, First }, save.Priorities);
        var restart = new TrayPreferencesStore(path).Load();
        Assert.True(restart.Success);
        Assert.Equal(new[] { Second, First }, restart.Priorities);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = document.RootElement;
        Assert.Equal(new[] { "version", "priorityDevices", "mainColorMode", "deviceCustomizations", "deviceLinks", "suspendedPriorities" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(3, root.GetProperty("version").GetInt32());
        Assert.All(root.GetProperty("priorityDevices").EnumerateArray(), item =>
            Assert.Equal(new[] { "id", "name" }, item.EnumerateObject().Select(p => p.Name)));

        AssertSaved(await store.SaveAsync([First]));
        Assert.Equal(new[] { First }, new TrayPreferencesStore(path).Load().Priorities);
        AssertSaved(await store.SaveAsync([]));
        Assert.Empty(new TrayPreferencesStore(path).Load().Priorities);
        Assert.False(File.Exists(Path.ChangeExtension(path, ".v1.bak")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task UnchangedSettingsDoNotTouchFileAndListMutationDoesNotChangeCurrent()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        var choices = new List<TrayPreference> { First };
        AssertSaved(await store.SaveAsync(choices));
        choices.Add(Second);
        Assert.Equal(new[] { First }, store.Current);
        var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, timestamp);
        var savedTime = File.GetLastWriteTimeUtc(path);
        var original = await File.ReadAllBytesAsync(path);
        AssertSaved(await store.SaveAsync([First]));
        Assert.Equal(savedTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        AssertSaved(await store.SaveAsync([First with { Name = "用户重新选择后的名称" }]));
        Assert.Equal("用户重新选择后的名称", Assert.Single(new TrayPreferencesStore(path).Load().Priorities).Name);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"priorityDevices\":[]}")]
    [InlineData("{\"version\":\"1\",\"priorityDevices\":[]}")]
    [InlineData("{\"version\":null,\"priorityDevices\":[]}")]
    [InlineData("{\"version\":1,\"priorityDevices\":[{\"id\":\"device-1\",\"name\":\"\\uD800\"}]}")]
    [InlineData("{\"version\":1,\"version\":1,\"priorityDevices\":[]}")]
    [InlineData("{\"version\":1,\"priorityDevices\":[],\"futureSettings\":true}")]
    [InlineData("{\"version\":1,\"priorityDevices\":[{\"id\":\"\",\"name\":\"Mouse\"}]}")]
    public async Task InvalidOrUnknownFileIsPreservedUntilExplicitResetWithBackup(string contents)
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, contents);
        var original = await File.ReadAllBytesAsync(path);
        var store = new TrayPreferencesStore(path);
        var load = store.Load();
        Assert.False(load.Success);
        Assert.False(load.CanSave);
        Assert.False(string.IsNullOrWhiteSpace(load.Error));
        Assert.Empty(load.Priorities);
        var save = await store.SaveAsync([First]);
        Assert.False(save.Success);
        Assert.False(save.CanSave);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(store.Current);

        var reset = await store.SaveAsync([], replaceInvalid: true);
        Assert.True(reset.Success, reset.Error);
        Assert.NotNull(reset.BackupPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(reset.BackupPath));
        Assert.Empty(new TrayPreferencesStore(path).Load().Priorities);
        Assert.False(File.Exists(Path.ChangeExtension(path, ".v1.bak")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task CorruptionAfterLoadCannotBeOverwrittenAndKeepsInMemoryChoices()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        AssertSaved(await store.SaveAsync([First]));
        await File.WriteAllTextAsync(path, "{damaged");
        var save = await store.SaveAsync([Second]);
        Assert.False(save.Success);
        Assert.False(save.CanSave);
        Assert.Equal(new[] { First }, store.Current);
        Assert.Equal(new[] { First }, store.Load().Priorities);
        Assert.Equal("{damaged", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FailedAtomicReplacementPreservesOldFileAndEffectiveChoices()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        AssertSaved(await store.SaveAsync([First]));
        var original = await File.ReadAllBytesAsync(path);
        // Readers are allowed, so validation succeeds; omission of FileShare.Delete blocks the commit.
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await store.SaveAsync([Second]);
            Assert.False(result.Success);
            Assert.Contains("保存失败", result.Error);
            Assert.Equal(new[] { First }, result.Priorities);
            Assert.Equal(new[] { First }, store.Current);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        AssertSaved(await store.SaveAsync([Second]));
        Assert.Equal(new[] { Second }, new TrayPreferencesStore(path).Load().Priorities);
    }

    [Fact]
    public async Task UnreadableFileCannotBeResetWithoutReadingAndPreservingItsContents()
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, "keep me");
        var store = new TrayPreferencesStore(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(store.Load().Success);
            var result = await store.SaveAsync([], replaceInvalid: true);
            Assert.False(result.Success);
            Assert.False(result.CanSave);
            Assert.Contains("读取失败", result.Error);
            Assert.Null(result.BackupPath);
        }
        Assert.Equal("keep me", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InvalidCandidateDoesNotReplaceExistingChoicesOrFile()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        AssertSaved(await store.SaveAsync([First]));
        var original = await File.ReadAllBytesAsync(path);
        IReadOnlyList<TrayPreference>[] invalid =
        [
            [First, First],
            [First with { DeviceId = "" }],
            [First with { DeviceId = " " }],
            [First with { DeviceId = new string('x', 257) }],
            [First with { DeviceId = "device\n123" }],
            [First with { DeviceId = "device\ud800" }],
            [First with { Name = new string('x', 129) }],
            [First with { Name = " " }],
            [First with { Name = "mouse\nkeyboard" }],
            [First with { Name = "mouse\udc00" }],
            Enumerable.Range(0, 65).Select(i => new TrayPreference($"device-{i:x20}", "Mouse")).ToArray()
        ];
        foreach (var candidate in invalid)
        {
            var result = await store.SaveAsync(candidate);
            Assert.False(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.Equal(new[] { First }, store.Current);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
    }

    [Fact]
    public async Task MaximumAcceptedListRoundTripsAndOversizedFileRemainsUntouched()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        var choices = Enumerable.Range(0, 64).Select(i => new TrayPreference(new string('器', 254) + $"{i:d2}", new string('键', 128))).ToArray();
        AssertSaved(await store.SaveAsync(choices));
        Assert.Equal(choices, new TrayPreferencesStore(path).Load().Priorities);
        var excessive = new string(' ', 256 * 1024 + 1);
        await File.WriteAllTextAsync(path, excessive);
        var result = await store.SaveAsync([]);
        Assert.False(result.Success);
        Assert.Contains("过大", result.Error);
        Assert.Equal(excessive, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ConcurrentSavesAreSerializedAndCancellationDoesNotCommit()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        var saves = Enumerable.Range(1, 16).Select(i =>
            store.SaveAsync([new TrayPreference($"device-{i:x20}", $"设备{i}")])).ToArray();
        Assert.All(await Task.WhenAll(saves), result => Assert.True(result.Success, result.Error));
        var final = new TrayPreference("device-00000000000000000010", "设备16");
        Assert.Equal(new[] { final }, store.Current);
        Assert.Equal(new[] { final }, new TrayPreferencesStore(path).Load().Priorities);
        var original = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync([First], cancellationToken: cancellation.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { final }, store.Current);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task VersionOneLoadsWithoutWritingThenMigratesOnExplicitSave()
    {
        var path = NewPath();
        const string original = "{\n  \"version\":1,\"priorityDevices\":[{\"id\":\"device-1\",\"name\":\"键盘\"}]}\n";
        await File.WriteAllTextAsync(path, original);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var backup = Path.ChangeExtension(path, ".v1.bak");
        var store = new TrayPreferencesStore(path);
        var loaded = store.Load();
        Assert.True(loaded.Success);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(backup));
        Assert.Equal(MainColorMode.System, loaded.Preferences!.MainColorMode);
        Assert.Empty(loaded.Preferences.DeviceCustomizations);
        var saved = await store.SaveAsync(loaded.Priorities);
        AssertSaved(saved);
        Assert.Equal(backup, saved.BackupPath);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backup));
        using var migrated = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(3, migrated.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("键盘", Assert.Single(new TrayPreferencesStore(path).Load().Priorities).Name);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Theory]
    [InlineData("theme")]
    [InlineData("name")]
    [InlineData("kind")]
    [InlineData("customization")]
    public async Task EveryPartialSavePreservesVersionOneBeforeMigrating(string section)
    {
        var path = Path.Combine(Path.GetDirectoryName(NewPath())!, "custom-preferences.json");
        const string original = "{\"version\":1,\"priorityDevices\":[{\"id\":\"original\",\"name\":\"原设备\"}]}";
        await File.WriteAllTextAsync(path, original);
        var store = new TrayPreferencesStore(path);
        var saved = await (section switch
        {
            "theme" => store.SaveColorModeAsync(MainColorMode.Dark),
            "name" => store.SaveDeviceNameAsync(First.DeviceId, "我的键盘"),
            "kind" => store.SaveDeviceKindAsync(First.DeviceId, DisplayDeviceKind.Keyboard),
            _ => store.SaveCustomizationAsync(new(First.DeviceId, "我的键盘", DisplayDeviceKind.Keyboard))
        });
        AssertSaved(saved);
        Assert.Equal("custom-preferences.v1.bak", Path.GetFileName(saved.BackupPath));
        Assert.Equal(original, await File.ReadAllTextAsync(saved.BackupPath!));
        Assert.Equal(new TrayPreference("original", "原设备"), Assert.Single(new TrayPreferencesStore(path).Load().Priorities));
        using var migrated = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(3, migrated.RootElement.GetProperty("version").GetInt32());
        Assert.Null((await store.SaveAsync([First])).BackupPath);
        Assert.Equal(original, await File.ReadAllTextAsync(saved.BackupPath!));
    }

    [Fact]
    public async Task ExistingVersionOneBackupIsNeverReplaced()
    {
        var path = NewPath();
        var backup = Path.ChangeExtension(path, ".v1.bak");
        const string firstCopy = "{\"version\":1,\"priorityDevices\":[]}\n";
        const string laterVersionOne = "{\"version\":1,\"priorityDevices\":[{\"id\":\"later\",\"name\":\"新选择\"}]}";
        await File.WriteAllTextAsync(backup, firstCopy);
        var firstBytes = await File.ReadAllBytesAsync(backup);
        var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(backup, timestamp);
        await File.WriteAllTextAsync(path, laterVersionOne);
        var saved = await new TrayPreferencesStore(path).SaveColorModeAsync(MainColorMode.Dark);
        AssertSaved(saved);
        Assert.Equal(backup, saved.BackupPath);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(backup));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(backup));
        Assert.Equal("新选择", Assert.Single(new TrayPreferencesStore(path).Load().Priorities).Name);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task BackupCreationFailureBlocksMigrationAndPreservesEffectiveSettings()
    {
        var path = NewPath();
        const string original = "{\"version\":1,\"priorityDevices\":[{\"id\":\"original\",\"name\":\"原设备\"}]}";
        await File.WriteAllTextAsync(path, original);
        var store = new TrayPreferencesStore(path);
        var loaded = store.Load();
        Directory.CreateDirectory(Path.ChangeExtension(path, ".v1.bak"));
        var saved = await store.SaveColorModeAsync(MainColorMode.Dark);
        Assert.False(saved.Success);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(loaded.Preferences, store.Preferences);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task CancelMigrationKeepsOriginalSettingsAndAnyCompleteBackup()
    {
        var path = NewPath();
        var backup = Path.ChangeExtension(path, ".v1.bak");
        const string original = "{\"version\":1,\"priorityDevices\":[]}";
        await File.WriteAllTextAsync(path, original);
        var store = new TrayPreferencesStore(path);
        Assert.True(store.Load().Success);
        using (var alreadyCanceled = new CancellationTokenSource())
        {
            alreadyCanceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveColorModeAsync(MainColorMode.Dark, alreadyCanceled.Token));
            Assert.False(File.Exists(backup));
        }
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancellation = new CancellationTokenSource();
        var saving = store.SaveColorModeAsync(MainColorMode.Dark, cancellation.Token);
        await WaitForBackupAsync(backup);
        Assert.False(saving.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(original, await File.ReadAllTextAsync(backup));
        Assert.Equal(MainColorMode.System, store.Preferences.MainColorMode);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task ExternalChangeAfterBackupIsPreservedWhenMigrationCannotCommit()
    {
        var path = NewPath();
        var backup = Path.ChangeExtension(path, ".v1.bak");
        const string original = "{\"version\":1,\"priorityDevices\":[]}";
        const string external = "{\"version\":1,\"priorityDevices\":[{\"id\":\"external\",\"name\":\"外部编辑\"}]}";
        await File.WriteAllTextAsync(path, original);
        var store = new TrayPreferencesStore(path);
        Assert.True(store.Load().Success);
        TrayPreferencesResult saved;
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var saving = store.SaveColorModeAsync(MainColorMode.Dark);
            await WaitForBackupAsync(backup);
            Assert.False(saving.IsCompleted);
            // The store briefly opens a read-only shared handle while checking
            // the original bytes. Retry only that transient sharing conflict;
            // the outer handle keeps replacement blocked throughout this edit.
            var editWait = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                Assert.False(saving.IsCompleted);
                try { await File.WriteAllTextAsync(path, external); break; }
                catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33 && editWait.Elapsed < TimeSpan.FromSeconds(2))
                { await Task.Delay(5); }
            }
            // Keep replacement blocked until a retry observes the edit. Releasing
            // this lock before the check returns would race the native commit.
            saved = await saving;
        }
        Assert.False(saved.Success);
        Assert.Contains("已改变", saved.Error);
        Assert.Equal(external, await File.ReadAllTextAsync(path));
        Assert.Equal(original, await File.ReadAllTextAsync(backup));
        Assert.Equal(MainColorMode.System, store.Preferences.MainColorMode);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task PartialChangesPreserveUnrelatedPreferencesAcrossRestart()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        AssertSaved(await store.SaveAsync([Second, First]));
        AssertSaved(await store.SaveDeviceNameAsync(First.DeviceId, "  我的键盘  "));
        AssertSaved(await store.SaveDeviceKindAsync(First.DeviceId, DisplayDeviceKind.Keyboard));
        AssertSaved(await store.SaveColorModeAsync(MainColorMode.Dark));
        AssertSaved(await store.SaveAsync([First]));

        var restart = new TrayPreferencesStore(path);
        var loaded = restart.Load();
        Assert.True(loaded.Success);
        Assert.Equal(MainColorMode.Dark, loaded.Preferences!.MainColorMode);
        Assert.Equal(new[] { First }, loaded.Priorities);
        Assert.Equal(new DeviceCustomization(First.DeviceId, "我的键盘", DisplayDeviceKind.Keyboard), Assert.Single(loaded.Preferences.DeviceCustomizations));
        // A new instance can update one section without a preceding Load.
        AssertSaved(await new TrayPreferencesStore(path).SaveColorModeAsync(MainColorMode.Light));
        var reloaded = restart.Load();
        Assert.Equal(MainColorMode.Light, reloaded.Preferences!.MainColorMode);
        Assert.Equal(loaded.Preferences.DeviceCustomizations, reloaded.Preferences.DeviceCustomizations);
        Assert.Equal(loaded.Priorities, reloaded.Priorities);
    }

    [Fact]
    public async Task RestoringNameAndAutomaticTypeRemovesOnlyExplicitOverrides()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        AssertSaved(await store.SaveAsync([First]));
        AssertSaved(await store.SaveCustomizationAsync(new(First.DeviceId, "客厅", DisplayDeviceKind.Speaker)));
        AssertSaved(await store.SaveDeviceNameAsync(First.DeviceId, "  "));
        var typeOnly = Assert.Single(store.Preferences.DeviceCustomizations);
        Assert.Null(typeOnly.Name);
        Assert.Equal(DisplayDeviceKind.Speaker, typeOnly.Kind);
        AssertSaved(await store.SaveDeviceKindAsync(First.DeviceId, null));
        Assert.Empty(store.Preferences.DeviceCustomizations);
        Assert.Equal(new[] { First }, store.Current);
        Assert.Empty(new TrayPreferencesStore(path).Load().Preferences!.DeviceCustomizations);
    }

    [Fact]
    public async Task ConcurrentDifferentSectionSavesDoNotOverwriteEachOther()
    {
        var path = NewPath();
        const string original = "{\"version\":1,\"priorityDevices\":[]}";
        await File.WriteAllTextAsync(path, original);
        var store = new TrayPreferencesStore(path);
        var results = await Task.WhenAll(
            store.SaveColorModeAsync(MainColorMode.Dark), store.SaveAsync([First]),
            store.SaveDeviceNameAsync(First.DeviceId, "我的鼠标"),
            store.SaveDeviceKindAsync(First.DeviceId, DisplayDeviceKind.Mouse));
        Assert.All(results, result => Assert.True(result.Success, result.Error));
        var restart = new TrayPreferencesStore(path).Load();
        Assert.Equal(new[] { First }, restart.Priorities);
        Assert.Equal(MainColorMode.Dark, restart.Preferences!.MainColorMode);
        Assert.Equal(new DeviceCustomization(First.DeviceId, "我的鼠标", DisplayDeviceKind.Mouse), Assert.Single(restart.Preferences.DeviceCustomizations));
        Assert.Equal(original, await File.ReadAllTextAsync(Path.ChangeExtension(path, ".v1.bak")));
    }

    [Theory]
    [InlineData("{\"version\":3,\"priorityDevices\":[]}")]
    [InlineData("{\"version\":2,\"priorityDevices\":[],\"mainColorMode\":\"dark\",\"deviceCustomizations\":[]}")]
    [InlineData("{\"version\":2,\"priorityDevices\":[],\"mainColorMode\":\"1\",\"deviceCustomizations\":[]}")]
    [InlineData("{\"version\":2,\"priorityDevices\":[],\"mainColorMode\":\"System\",\"deviceCustomizations\":[{\"id\":\"d\",\"kind\":\"Unknown\"}]}")]
    [InlineData("{\"version\":2,\"priorityDevices\":[],\"mainColorMode\":\"System\",\"deviceCustomizations\":[{\"id\":\"d\",\"kind\":null}]}")]
    [InlineData("{\"version\":2,\"priorityDevices\":[],\"mainColorMode\":\"System\",\"deviceCustomizations\":[{\"id\":\"d\",\"name\":\"a\"},{\"id\":\"d\",\"name\":\"b\"}]}")]
    [InlineData("{\"version\":2,\"priorityDevices\":[],\"mainColorMode\":\"System\",\"deviceCustomizations\":[{\"id\":\"d\",\"name\":\"a\",\"name\":\"b\"}]}")]
    [InlineData("{\"version\":2,\"priorityDevices\":[],\"mainColorMode\":\"System\",\"deviceCustomizations\":[{\"id\":\"d\"}]}")]
    public async Task InvalidExtendedSettingsBlockEveryPartialSaveUntilExplicitReset(string contents)
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, contents);
        var store = new TrayPreferencesStore(path);
        Assert.False(store.Load().Success);
        Assert.False((await store.SaveColorModeAsync(MainColorMode.Dark)).Success);
        Assert.False((await store.SaveDeviceNameAsync(First.DeviceId, "我的键盘")).Success);
        Assert.False((await store.SaveDeviceKindAsync(First.DeviceId, DisplayDeviceKind.Keyboard)).Success);
        Assert.False((await store.SaveAsync([First])).Success);
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
        var reset = await store.SaveAsync([], replaceInvalid: true);
        Assert.True(reset.Success);
        Assert.Equal(contents, await File.ReadAllTextAsync(reset.BackupPath!));
        Assert.Equal(MainColorMode.System, reset.Preferences!.MainColorMode);
        Assert.Empty(reset.Preferences.DeviceCustomizations);
    }

    [Fact]
    public async Task FailedColorAndNameCommitsKeepBothFileAndEffectiveSettings()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        await store.SaveAsync([First]);
        await store.SaveDeviceNameAsync(First.DeviceId, "原名");
        var original = await File.ReadAllBytesAsync(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False((await store.SaveColorModeAsync(MainColorMode.Dark)).Success);
            Assert.False((await store.SaveDeviceNameAsync(First.DeviceId, "新名")).Success);
            Assert.Equal(MainColorMode.System, store.Preferences.MainColorMode);
            Assert.Equal("原名", Assert.Single(store.Preferences.DeviceCustomizations).Name);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
    }

    [Fact]
    public async Task InvalidOverrideAndColorCandidatesCannotCorruptExistingFile()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        await store.SaveColorModeAsync(MainColorMode.Dark);
        var original = await File.ReadAllBytesAsync(path);
        var failures = new[]
        {
            await store.SaveColorModeAsync((MainColorMode)100),
            await store.SaveDeviceKindAsync(First.DeviceId, (DisplayDeviceKind)100),
            await store.SaveDeviceKindAsync(First.DeviceId, DisplayDeviceKind.Unknown),
            await store.SaveDeviceNameAsync(First.DeviceId, new string('a', 129)),
            await store.SaveDeviceNameAsync(First.DeviceId, "bad\nname"),
            await store.SaveDeviceNameAsync(First.DeviceId, "bad\ud800"),
            await store.SaveDeviceNameAsync("bad\nid", "Name")
        };
        Assert.All(failures, result => Assert.False(result.Success));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(store.Preferences.DeviceCustomizations);
    }

    [Fact]
    public async Task ExplicitFieldsOnlyAndEveryAllowedTypeRoundTrips()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        foreach (var kind in Enum.GetValues<DisplayDeviceKind>().Where(kind => kind != DisplayDeviceKind.Unknown))
        {
            var result = await store.SaveDeviceKindAsync("device-" + kind, kind);
            Assert.True(result.Success, $"{kind}: {result.Error}");
        }
        var loaded = new TrayPreferencesStore(path).Load().Preferences!;
        Assert.All(loaded.DeviceCustomizations, item => Assert.Null(item.Name));
        Assert.Equal(Enum.GetValues<DisplayDeviceKind>().Length - 1, loaded.DeviceCustomizations.Count);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.All(json.RootElement.GetProperty("deviceCustomizations").EnumerateArray(), item =>
            Assert.Equal(new[] { "id", "kind" }, item.EnumerateObject().Select(property => property.Name)));
    }

    [Fact]
    public async Task BriefDeleteSharingConflictRecoversWithoutLosingOtherSettings()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        AssertSaved(await store.SaveAsync([First]));
        Task<TrayPreferencesResult> saving;
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            saving = store.SaveDeviceNameAsync(First.DeviceId, "我的键盘");
            await Task.Delay(80);
            Assert.False(saving.IsCompleted);
            Assert.Empty(store.Preferences.DeviceCustomizations);
        }
        AssertSaved(await saving);
        var restart = new TrayPreferencesStore(path).Load();
        Assert.Equal(new[] { First }, restart.Priorities);
        Assert.Equal("我的键盘", Assert.Single(restart.Preferences!.DeviceCustomizations).Name);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task CancelDuringDeleteConflictLeavesOriginalAndEffectiveSettingsUntouched()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        AssertSaved(await store.SaveAsync([First]));
        var original = await File.ReadAllBytesAsync(path);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancellation = new CancellationTokenSource();
        var saving = store.SaveColorModeAsync(MainColorMode.Dark, cancellation.Token);
        await Task.Delay(80);
        Assert.False(saving.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(MainColorMode.System, store.Preferences.MainColorMode);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task ChangedFileDuringDeleteConflictIsNeverOverwrittenByRetry()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        AssertSaved(await store.SaveAsync([First]));
        const string replacement = "{modified externally";
        TrayPreferencesResult result;
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var saving = store.SaveColorModeAsync(MainColorMode.Dark);
            await Task.Delay(80);
            Assert.False(saving.IsCompleted);
            // This handle prevents delete/replace while permitting an external
            // editor to change the contents between commit attempts.
            await File.WriteAllTextAsync(path, replacement);
            result = await saving;
        }
        Assert.False(result.Success);
        Assert.Contains("已改变", result.Error);
        Assert.Equal(replacement, await File.ReadAllTextAsync(path));
        Assert.Equal(MainColorMode.System, store.Preferences.MainColorMode);
        Assert.Equal(new[] { First }, store.Current);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    private static void AssertSaved(TrayPreferencesResult result) => Assert.True(result.Success, result.Error);

    private static async Task WaitForBackupAsync(string path)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(path) && started.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
        Assert.True(File.Exists(path), "The migration did not publish its v1 backup.");
    }

    private static string NewPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Project root not found for test artifacts.");
        var directory = Path.Combine(root.FullName, ".artifacts", "tests", "tray-preferences", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "settings.json");
    }
}
