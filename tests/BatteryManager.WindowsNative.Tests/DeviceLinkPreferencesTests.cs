using System.Text.Json;
using BatteryManager.App;
using BatteryManager.Core;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class DeviceLinkPreferencesTests
{
    private static readonly DeviceLinkMember Receiver = new("receiver", "Keyboard 2.4G", DeviceKind.Keyboard);
    private static readonly DeviceLinkMember Bluetooth = new("bluetooth", "Keyboard Bluetooth");
    private static readonly TrayPreference ReceiverPriority = new(Receiver.DeviceId, Receiver.Name);
    private static readonly TrayPreference BluetoothPriority = new(Bluetooth.DeviceId, Bluetooth.Name);
    private static readonly TrayPreference MousePriority = new("mouse", "Mouse");
    private const string V2 = "{\n  \"version\":2,\"priorityDevices\":[{\"id\":\"bluetooth\",\"name\":\"Keyboard Bluetooth\"}],\"mainColorMode\":\"Dark\",\"deviceCustomizations\":[]\n}\n";

    [Fact]
    public async Task LinkRestartAndUnlinkKeepPrimaryChoicesAndRestoreSecondaryChoicesAtomically()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        var original = new[] { BluetoothPriority, MousePriority, ReceiverPriority };
        Saved(await store.SaveAsync(original));
        Saved(await store.SaveCustomizationAsync(new(Receiver.DeviceId, "桌面键盘", DisplayDeviceKind.Keyboard)));
        Saved(await store.SaveCustomizationAsync(new(Bluetooth.DeviceId, "蓝牙键盘", DisplayDeviceKind.Other)));
        var linked = await store.LinkDevicesAsync(Receiver, Bluetooth);
        Saved(linked);
        Assert.Equal(new[] { MousePriority, ReceiverPriority }, linked.Priorities);
        var group = Assert.Single(linked.Preferences!.DeviceLinks);
        Assert.Equal(Receiver.DeviceId, group.PrimaryDeviceId);
        Assert.Equal(new[] { Receiver, Bluetooth }, group.Members);
        Assert.Equal(0, Assert.Single(linked.Preferences.SuspendedPriorities).Index);
        Assert.Equal(2, linked.Preferences.DeviceCustomizations.Count);
        var restarted = new TrayPreferencesStore(path);
        Assert.True(restarted.Load().Success);
        Assert.Equal(new[] { Receiver, Bluetooth }, Assert.Single(restarted.Preferences.DeviceLinks).Members);
        Saved(await restarted.SaveColorModeAsync(MainColorMode.Light));
        Saved(await restarted.SaveDeviceNameAsync(Receiver.DeviceId, "新键盘名称"));
        var unlinked = await restarted.UnlinkDevicesAsync(Receiver.DeviceId);
        Saved(unlinked);
        Assert.Equal(original, unlinked.Priorities);
        Assert.Empty(unlinked.Preferences!.DeviceLinks);
        Assert.Empty(unlinked.Preferences.SuspendedPriorities);
        Assert.Equal("新键盘名称", Assert.Single(unlinked.Preferences.DeviceCustomizations, item => item.DeviceId == Receiver.DeviceId).Name);
        Assert.Equal("蓝牙键盘", Assert.Single(unlinked.Preferences.DeviceCustomizations, item => item.DeviceId == Bluetooth.DeviceId).Name);
        Assert.Equal(MainColorMode.Light, unlinked.Preferences.MainColorMode);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(3, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(new[] { "version", "priorityDevices", "mainColorMode", "deviceCustomizations", "deviceLinks", "suspendedPriorities" },
            json.RootElement.EnumerateObject().Select(item => item.Name));
    }

    [Fact]
    public async Task MemberTypesRoundTripWithoutCreatingUserOverrides()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        Saved(await store.LinkDevicesAsync(Receiver, Bluetooth));
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            var members = json.RootElement.GetProperty("deviceLinks")[0].GetProperty("members");
            Assert.Equal("Keyboard", members[0].GetProperty("kind").GetString());
            Assert.Equal("Unknown", members[1].GetProperty("kind").GetString());
        }
        var restarted = new TrayPreferencesStore(path);
        Assert.True(restarted.Load().Success);
        Assert.Equal(new[] { Receiver, Bluetooth }, Assert.Single(restarted.Preferences.DeviceLinks).Members);
        Assert.Empty(restarted.Preferences.DeviceCustomizations);
        Saved(await restarted.SaveColorModeAsync(MainColorMode.Dark));
        var reloaded = new TrayPreferencesStore(path);
        Assert.True(reloaded.Load().Success);
        Assert.Equal(new[] { Receiver, Bluetooth }, Assert.Single(reloaded.Preferences.DeviceLinks).Members);
        Saved(await reloaded.UnlinkDevicesAsync(Receiver.DeviceId));
        Assert.Empty(reloaded.Preferences.DeviceCustomizations);
    }

    [Fact]
    public async Task MembersWithoutKindRemainReadableAsUnknownWithoutWritingDuringLoad()
    {
        var path = NewPath();
        const string original = "{\"version\":3,\"priorityDevices\":[],\"mainColorMode\":\"System\",\"deviceCustomizations\":[],\"deviceLinks\":[{\"primaryId\":\"receiver\",\"members\":[{\"id\":\"receiver\",\"name\":\"Keyboard 2.4G\"},{\"id\":\"bluetooth\",\"name\":\"Keyboard Bluetooth\"}]}],\"suspendedPriorities\":[]}";
        await File.WriteAllTextAsync(path, original);
        var store = new TrayPreferencesStore(path);
        Assert.True(store.Load().Success);
        Assert.All(Assert.Single(store.Preferences.DeviceLinks).Members, member => Assert.Equal(DeviceKind.Unknown, member.Kind));
        Assert.All(Assert.Single(store.Preferences.DeviceLinks).Members, member => Assert.Empty(member.Transports));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Saved(await store.SaveColorModeAsync(MainColorMode.Dark));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.All(json.RootElement.GetProperty("deviceLinks")[0].GetProperty("members").EnumerateArray(),
            member => Assert.Equal("Unknown", member.GetProperty("kind").GetString()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"1\"")]
    [InlineData("\"keyboard\"")]
    [InlineData("\"Invalid\"")]
    public async Task InvalidMemberKindProtectsOriginalFile(string kind)
    {
        var path = NewPath();
        var original = "{\"version\":3,\"priorityDevices\":[],\"mainColorMode\":\"System\",\"deviceCustomizations\":[],\"deviceLinks\":[{\"primaryId\":\"receiver\",\"members\":[{\"id\":\"receiver\",\"name\":\"Keyboard 2.4G\",\"kind\":" + kind + "},{\"id\":\"bluetooth\",\"name\":\"Keyboard Bluetooth\"}]}],\"suspendedPriorities\":[]}";
        await File.WriteAllTextAsync(path, original);
        var store = new TrayPreferencesStore(path);
        var loaded = store.Load();
        Assert.False(loaded.Success);
        Assert.False(loaded.CanSave);
        Assert.False((await store.SaveColorModeAsync(MainColorMode.Dark)).Success);
        Assert.False((await store.LinkDevicesAsync(Receiver, Bluetooth)).Success);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadingLegacySettingsDoesNotWriteAndFirstLinkSaveBacksUpOriginalBytes(int version)
    {
        var path = NewPath();
        var original = version == 2 ? V2
            : "{\n  \"version\":1,\"priorityDevices\":[{\"id\":\"bluetooth\",\"name\":\"Keyboard Bluetooth\"}]\n}\n";
        await File.WriteAllTextAsync(path, original);
        var bytes = await File.ReadAllBytesAsync(path);
        var store = new TrayPreferencesStore(path);
        Assert.True(store.Load().Success);
        Assert.Empty(store.Preferences.DeviceLinks);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(Path.ChangeExtension(path, $".v{version}.bak")));
        var linked = await store.LinkDevicesAsync(Receiver, Bluetooth);
        Saved(linked);
        Assert.Equal(Path.ChangeExtension(path, $".v{version}.bak"), linked.BackupPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(linked.BackupPath!));
        Assert.Equal(version == 2 ? MainColorMode.Dark : MainColorMode.System, linked.Preferences!.MainColorMode);
        Assert.Empty(linked.Priorities);
        var restarted = new TrayPreferencesStore(path);
        Assert.True(restarted.Load().Success);
        Assert.Equal(new[] { Receiver, Bluetooth }, Assert.Single(restarted.Preferences.DeviceLinks).Members);
        Saved(await store.SaveColorModeAsync(MainColorMode.Light));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(linked.BackupPath!));
    }

    [Fact]
    public async Task ExistingV2BackupIsNeverOverwritten()
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, V2);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".v2.bak"), "earlier original");
        Saved(await new TrayPreferencesStore(path).LinkDevicesAsync(Receiver, Bluetooth));
        Assert.Equal("earlier original", await File.ReadAllTextAsync(Path.ChangeExtension(path, ".v2.bak")));
    }

    [Fact]
    public async Task RequiredV2BackupFailureLeavesBothFileAndEffectiveLinkUnchanged()
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, V2);
        Directory.CreateDirectory(Path.ChangeExtension(path, ".v2.bak"));
        var store = new TrayPreferencesStore(path);
        Assert.True(store.Load().Success);
        var result = await store.LinkDevicesAsync(Receiver, Bluetooth);
        Assert.False(result.Success);
        Assert.Equal(V2, await File.ReadAllTextAsync(path));
        Assert.Empty(store.Preferences.DeviceLinks);
        Assert.Equal(BluetoothPriority, Assert.Single(store.Current));
    }

    [Fact]
    public async Task LockedCommitCannotApplyOnlyHalfOfAssociationAndPriorityChange()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        Saved(await store.SaveAsync([BluetoothPriority, ReceiverPriority]));
        var bytes = await File.ReadAllBytesAsync(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await store.LinkDevicesAsync(Receiver, Bluetooth);
            Assert.False(result.Success);
            Assert.Empty(store.Preferences.DeviceLinks);
            Assert.Empty(store.Preferences.SuspendedPriorities);
            Assert.Equal(new[] { BluetoothPriority, ReceiverPriority }, store.Current);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        Saved(await store.LinkDevicesAsync(Receiver, Bluetooth));
        var linkedBytes = await File.ReadAllBytesAsync(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False((await store.UnlinkDevicesAsync(Receiver.DeviceId)).Success);
            Assert.Single(store.Preferences.DeviceLinks);
            Assert.Single(store.Preferences.SuspendedPriorities);
            Assert.Equal(ReceiverPriority, Assert.Single(store.Current));
            Assert.Equal(linkedBytes, await File.ReadAllBytesAsync(path));
        }
    }

    [Fact]
    public async Task ExplicitAutomaticSelectionClearsPausedPreferencesButOrdinaryRemovalDoesNot()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        Saved(await store.SaveAsync([BluetoothPriority, ReceiverPriority]));
        Saved(await store.LinkDevicesAsync(Receiver, Bluetooth));
        Saved(await store.SaveAsync([]));
        Assert.Single(store.Preferences.SuspendedPriorities);
        Saved(await store.UnlinkDevicesAsync(Receiver.DeviceId));
        Assert.Equal(BluetoothPriority, Assert.Single(store.Current));
        Saved(await store.LinkDevicesAsync(Receiver, Bluetooth));
        Saved(await store.SaveAsync([], clearSuspendedPriorities: true));
        Assert.Empty(store.Preferences.SuspendedPriorities);
        Saved(await store.UnlinkDevicesAsync(Receiver.DeviceId));
        Assert.Empty(store.Current);
    }

    [Fact]
    public void ThirdConnectionAndSeparateGroupRestoreOriginalOrderWithoutReactivatingOtherGroup()
    {
        var third = new DeviceLinkMember("third", "Wired");
        var otherPrimary = new DeviceLinkMember("other-primary", "Other keyboard");
        var otherMember = new DeviceLinkMember("other-member", "Other Bluetooth");
        var original = new[] { ReceiverPriority, BluetoothPriority, MousePriority, new TrayPreference(third.DeviceId, third.Name),
            new TrayPreference(otherPrimary.DeviceId, otherPrimary.Name), new TrayPreference(otherMember.DeviceId, otherMember.Name) };
        var settings = new AppPreferences(original, MainColorMode.System, []);
        settings = Changed(AppPreferenceLinks.Link(settings, Receiver, Bluetooth));
        settings = Changed(AppPreferenceLinks.Link(settings, Receiver, third));
        settings = Changed(AppPreferenceLinks.Link(settings, otherPrimary, otherMember));
        Assert.Equal(3, settings.Priorities.Count);
        settings = Changed(AppPreferenceLinks.Unlink(settings, Receiver.DeviceId));
        Assert.Equal(original.Where(item => item.DeviceId != otherMember.DeviceId), settings.Priorities);
        Assert.Equal(otherPrimary.DeviceId, Assert.Single(settings.DeviceLinks).PrimaryDeviceId);
        settings = Changed(AppPreferenceLinks.Unlink(settings, otherPrimary.DeviceId));
        Assert.Equal(original, settings.Priorities);
    }

    [Fact]
    public void UnlinkPreservesCurrentOrderingAndPrimaryNameEdits()
    {
        var settings = new AppPreferences([BluetoothPriority, ReceiverPriority, MousePriority], MainColorMode.System,
            [new(Receiver.DeviceId, "Primary", null), new(Bluetooth.DeviceId, "Secondary", null)]);
        settings = Changed(AppPreferenceLinks.Link(settings, Receiver, Bluetooth));
        settings = settings with { Priorities = [MousePriority, ReceiverPriority], DeviceCustomizations = [new(Receiver.DeviceId, "Edited", null), new(Bluetooth.DeviceId, "Secondary", null)] };
        var unlinked = Changed(AppPreferenceLinks.Unlink(settings, Receiver.DeviceId));
        Assert.Equal(new[] { BluetoothPriority, MousePriority, ReceiverPriority }, unlinked.Priorities);
        Assert.Equal("Edited", unlinked.DeviceCustomizations[0].Name);
        Assert.Equal("Secondary", unlinked.DeviceCustomizations[1].Name);
    }

    [Fact]
    public void PureScenarioChangesDoNotMutateOriginalSettingsAndClearAllDoesNotResurrectPausedRows()
    {
        var original = new AppPreferences([ReceiverPriority, BluetoothPriority], MainColorMode.Dark, []);
        var linked = Changed(AppPreferenceLinks.Link(original, Receiver, Bluetooth));
        Assert.Empty(original.DeviceLinks);
        Assert.Equal(2, original.Priorities.Count);
        Assert.Single(linked.DeviceLinks);
        var cleared = AppPreferenceLinks.ClearPriorities(linked);
        Assert.Empty(cleared.Priorities);
        Assert.Empty(cleared.SuspendedPriorities);
        Assert.Single(cleared.DeviceLinks);
        Assert.Empty(Changed(AppPreferenceLinks.Unlink(cleared, Receiver.DeviceId)).Priorities);
    }

    [Fact]
    public async Task SelfOverlapNonPrimaryAndHiddenPriorityChoicesAreRejectedWithoutWriting()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        Assert.False((await store.LinkDevicesAsync(Receiver, Receiver)).Success);
        Assert.False(File.Exists(path));
        Saved(await store.LinkDevicesAsync(Receiver, Bluetooth));
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False((await store.LinkDevicesAsync(Receiver, Bluetooth)).Success);
        Assert.False((await store.LinkDevicesAsync(Bluetooth, new("other", "Other"))).Success);
        Assert.False((await store.SaveAsync([BluetoothPriority])).Success);
        Assert.False((await store.UnlinkDevicesAsync(Bluetooth.DeviceId)).Success);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Single(store.Preferences.DeviceLinks);
    }

    [Fact]
    public void SharedLinkValidatorRejectsNinthMemberMalformedUnicodeAndUndefinedKind()
    {
        var settings = AppPreferences.Default;
        for (var i = 1; i <= 7; i++) settings = Changed(AppPreferenceLinks.Link(settings, Receiver, new("member-" + i, "Member " + i)));
        Assert.False(AppPreferenceLinks.Link(settings, Receiver, new("ninth", "Ninth")).Success);
        Assert.False(AppPreferenceLinks.Link(AppPreferences.Default, Receiver, new("bad\uD800", "Bad")).Success);
        Assert.False(AppPreferenceLinks.Link(AppPreferences.Default, Receiver, new("valid", "Bad\nname")).Success);
        Assert.False(AppPreferenceLinks.Link(AppPreferences.Default, Receiver, new("valid", "Invalid kind", (DeviceKind)999)).Success);
    }

    [Fact]
    public async Task ConcurrentAppearanceSavePreservesNewLinkAndCancellationCannotCommit()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        Saved(await store.SaveAsync([BluetoothPriority, ReceiverPriority]));
        var linked = store.LinkDevicesAsync(Receiver, Bluetooth);
        var color = store.SaveColorModeAsync(MainColorMode.Dark);
        Saved(await linked);
        Saved(await color);
        var loaded = new TrayPreferencesStore(path).Load();
        Assert.True(loaded.Success);
        Assert.Single(loaded.Preferences!.DeviceLinks);
        Assert.Equal(MainColorMode.Dark, loaded.Preferences.MainColorMode);
        var bytes = await File.ReadAllBytesAsync(path);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UnlinkDevicesAsync(Receiver.DeviceId, canceled.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Single(store.Preferences.DeviceLinks);
    }

    [Fact]
    public void FullGroupForgetsOldestAbsentNonPrimaryAndRebasesAllPausedPriorityPositions()
    {
        var forgotten = Member(2);
        var kept = Member(3);
        var incoming = new DeviceLinkMember("incoming", "New port");
        var anotherPrimary = new DeviceLinkMember("another", "Another keyboard");
        var anotherMember = new DeviceLinkMember("another-member", "Another connection");
        var original = new[] { Priority(forgotten), Priority(kept), Priority(anotherMember), Priority(incoming), ReceiverPriority, Priority(anotherPrimary), MousePriority };
        var settings = FullGroup(original) with
        { DeviceCustomizations = new[] { new DeviceCustomization(forgotten.DeviceId, "Retained personal name", DisplayDeviceKind.Other) } };
        settings = Changed(AppPreferenceLinks.Link(settings, anotherPrimary, anotherMember));
        var protectedIds = settings.DeviceLinks.SelectMany(group => group.Members).Select(member => member.DeviceId).ToHashSet(StringComparer.Ordinal);
        protectedIds.Remove(forgotten.DeviceId);
        protectedIds.Remove(Member(4).DeviceId);
        var changed = Changed(AppPreferenceLinks.Link(settings, Receiver, incoming, protectedIds));
        var group = Assert.Single(changed.DeviceLinks, item => item.PrimaryDeviceId == Receiver.DeviceId);
        Assert.Equal(8, group.Members.Count);
        Assert.DoesNotContain(group.Members, member => member.DeviceId == forgotten.DeviceId);
        Assert.Contains(group.Members, member => member.DeviceId == Member(4).DeviceId);
        Assert.DoesNotContain(changed.SuspendedPriorities, item => item.Preference.DeviceId == forgotten.DeviceId);
        Assert.Equal(settings.DeviceCustomizations, changed.DeviceCustomizations);
        AssertLinkState(settings, Changed(AppPreferenceLinks.Restore(changed, changed, settings)));
        var separated = Changed(AppPreferenceLinks.Unlink(changed, Receiver.DeviceId));
        separated = Changed(AppPreferenceLinks.Unlink(separated, anotherPrimary.DeviceId));
        Assert.Equal(original.Where(item => item.DeviceId != forgotten.DeviceId), separated.Priorities);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullGroupWithoutAbsenceEvidenceNeverEvictsOrWrites(bool provideProtectedIds)
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        for (var i = 1; i <= 7; i++) Saved(await store.LinkDevicesAsync(Receiver, Member(i)));
        var bytes = await File.ReadAllBytesAsync(path);
        // Even an absent primary cannot be evicted. All other records are protected,
        // including those that the app is still monitoring without an active response.
        var protectedIds = provideProtectedIds
            ? Enumerable.Range(1, 7).Select(i => Member(i).DeviceId).ToHashSet(StringComparer.Ordinal) : null;
        var result = await store.LinkDevicesAsync(Receiver, new("ninth", "Ninth"), present: protectedIds);
        Assert.False(result.Success);
        Assert.Null(result.PreviousPreferences);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(8, Assert.Single(store.Preferences.DeviceLinks).Members.Count);
    }

    [Fact]
    public async Task EvictionCommitFailureKeepsMembershipAndPausedPrioritiesTogether()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        Saved(await store.SaveAsync([Priority(Member(1)), ReceiverPriority]));
        for (var i = 1; i <= 7; i++) Saved(await store.LinkDevicesAsync(Receiver, Member(i)));
        var before = store.Preferences;
        var bytes = await File.ReadAllBytesAsync(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await store.LinkDevicesAsync(Receiver, new("ninth", "Ninth"), present: new HashSet<string> { Receiver.DeviceId });
            Assert.False(result.Success);
            Assert.Null(result.PreviousPreferences);
            AssertLinkState(before, store.Preferences);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
    }

    [Fact]
    public async Task UndoRestoresMergeStructureExactlyAndKeepsLaterAppearanceChanges()
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        Saved(await store.SaveAsync([BluetoothPriority, MousePriority, ReceiverPriority]));
        var linked = await store.LinkDevicesAsync(Receiver, Bluetooth);
        Saved(linked);
        var before = Assert.IsType<AppPreferences>(linked.PreviousPreferences);
        Saved(await store.SaveColorModeAsync(MainColorMode.Dark));
        Saved(await store.SaveDeviceNameAsync(Receiver.DeviceId, "New name"));
        var undo = await store.RestoreDeviceLinksAsync(linked.Preferences!, before);
        Saved(undo);
        AssertLinkState(before, undo.Preferences!);
        Assert.Equal(MainColorMode.Dark, undo.Preferences!.MainColorMode);
        Assert.Equal("New name", Assert.Single(undo.Preferences.DeviceCustomizations).Name);
        var mergedAgain = await store.LinkDevicesAsync(Receiver, Bluetooth);
        Saved(mergedAgain);
        var unlinked = await store.UnlinkDevicesAsync(Receiver.DeviceId);
        Saved(unlinked);
        Saved(await store.RestoreDeviceLinksAsync(unlinked.Preferences!, unlinked.PreviousPreferences!));
        AssertLinkState(mergedAgain.Preferences!, store.Preferences);
    }

    [Theory]
    [InlineData("priority")]
    [InlineData("group")]
    [InlineData("invalid")]
    public async Task UndoCannotOverwriteLaterPriorityGroupOrUnreadableSettings(string change)
    {
        var path = NewPath();
        var store = new TrayPreferencesStore(path);
        Saved(await store.SaveAsync([BluetoothPriority, ReceiverPriority, MousePriority]));
        var linked = await store.LinkDevicesAsync(Receiver, Bluetooth);
        Saved(linked);
        if (change == "priority") Saved(await store.SaveAsync([MousePriority, ReceiverPriority]));
        else if (change == "group") Saved(await store.LinkDevicesAsync(Receiver, new("third", "Third")));
        else await File.WriteAllTextAsync(path, "{broken settings");
        var bytes = await File.ReadAllBytesAsync(path);
        var current = store.Preferences;
        var result = await store.RestoreDeviceLinksAsync(linked.Preferences!, linked.PreviousPreferences!);
        Assert.False(result.Success);
        Assert.Null(result.PreviousPreferences);
        AssertLinkState(current, store.Preferences);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UndoBaselineIncludesExternalEditsThatPrecedeTheAtomicMerge()
    {
        var path = NewPath();
        var staleStore = new TrayPreferencesStore(path);
        Saved(await staleStore.SaveAsync([ReceiverPriority]));
        var external = new TrayPreferencesStore(path);
        Saved(await external.SaveAsync([BluetoothPriority, MousePriority, ReceiverPriority]));
        Saved(await external.LinkDevicesAsync(new("separate", "Separate"), new("separate-other", "Separate other")));
        var actualBefore = external.Preferences;
        var saved = await staleStore.LinkDevicesAsync(Receiver, Bluetooth);
        Saved(saved);
        AssertLinkState(actualBefore, Assert.IsType<AppPreferences>(saved.PreviousPreferences));
        Saved(await staleStore.RestoreDeviceLinksAsync(saved.Preferences!, saved.PreviousPreferences!));
        var reloaded = new TrayPreferencesStore(path);
        Assert.True(reloaded.Load().Success);
        AssertLinkState(actualBefore, reloaded.Preferences);
    }

    [Fact]
    public async Task VerifiedTransportMetadataRoundTripsIsCopiedAndDoesNotForceNoOpWrites()
    {
        var path = NewPath();
        var transports = new List<DeviceTransport> { DeviceTransport.Receiver };
        var primary = Receiver with { Transports = transports };
        var other = Bluetooth with { Transports = new[] { DeviceTransport.Bluetooth, DeviceTransport.Wired } };
        var store = new TrayPreferencesStore(path);
        Saved(await store.LinkDevicesAsync(primary, other));
        transports.Clear();
        Assert.Equal(new[] { DeviceTransport.Receiver }, Assert.Single(store.Preferences.DeviceLinks).Members[0].Transports);
        var restarted = new TrayPreferencesStore(path);
        Assert.True(restarted.Load().Success);
        AssertLinkState(store.Preferences, restarted.Preferences);
        var bytes = await File.ReadAllBytesAsync(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Saved(await restarted.SaveColorModeAsync(MainColorMode.System));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Saved(await restarted.LinkDevicesAsync(Receiver with { Transports = new[] { DeviceTransport.Bluetooth } }, new("third", "Third")));
        Assert.Equal(new[] { DeviceTransport.Bluetooth, DeviceTransport.Receiver }, Assert.Single(restarted.Preferences.DeviceLinks).Members[0].Transports);
    }

    [Fact]
    public async Task LegacyV3MemberKindWithoutTransportMetadataLoadsWithoutWriting()
    {
        var path = NewPath();
        const string original = "{\"version\":3,\"priorityDevices\":[],\"mainColorMode\":\"System\",\"deviceCustomizations\":[],\"deviceLinks\":[{\"primaryId\":\"receiver\",\"members\":[{\"id\":\"receiver\",\"name\":\"Keyboard\",\"kind\":\"Keyboard\"},{\"id\":\"bluetooth\",\"name\":\"Bluetooth\"}]}],\"suspendedPriorities\":[]}";
        await File.WriteAllTextAsync(path, original);
        var store = new TrayPreferencesStore(path);
        Assert.True(store.Load().Success);
        Assert.Equal(DeviceKind.Keyboard, Assert.Single(store.Preferences.DeviceLinks).Members[0].Kind);
        Assert.All(Assert.Single(store.Preferences.DeviceLinks).Members, member => Assert.Empty(member.Transports));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExplicitMergeEnrichesExistingMembersOnlyAndUndoUsesOriginalMetadata()
    {
        var path = NewPath();
        const string original = "{\"version\":3,\"priorityDevices\":[],\"mainColorMode\":\"System\",\"deviceCustomizations\":[],\"deviceLinks\":[{\"primaryId\":\"receiver\",\"members\":[{\"id\":\"receiver\",\"name\":\"Keyboard 2.4G\",\"kind\":\"Keyboard\"},{\"id\":\"bluetooth\",\"name\":\"Keyboard Bluetooth\"}]},{\"primaryId\":\"separate\",\"members\":[{\"id\":\"separate\",\"name\":\"Saved name\"},{\"id\":\"separate-other\",\"name\":\"Saved other\"}]}],\"suspendedPriorities\":[]}";
        await File.WriteAllTextAsync(path, original);
        var store = new TrayPreferencesStore(path);
        Assert.True(store.Load().Success);
        var before = store.Preferences;
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        var observed = new[]
        {
            Receiver with { Name = "Observed new name", Kind = DeviceKind.Mouse, Transports = new[] { DeviceTransport.Receiver } },
            Bluetooth with { Transports = new[] { DeviceTransport.Bluetooth } },
            new DeviceLinkMember("separate", "Observed separate", DeviceKind.Mouse) { Transports = new[] { DeviceTransport.Wired } },
            new DeviceLinkMember("not-saved", "Unrelated") { Transports = new[] { DeviceTransport.Bluetooth } }
        };
        var saved = await store.LinkDevicesAsync(Receiver, new("third", "Third"), knownMembers: observed);
        Saved(saved);
        AssertLinkState(before, saved.PreviousPreferences!);
        var receiverGroup = Assert.Single(saved.Preferences!.DeviceLinks, group => group.PrimaryDeviceId == Receiver.DeviceId);
        Assert.Equal(new[] { "receiver", "bluetooth", "third" }, receiverGroup.Members.Select(member => member.DeviceId));
        Assert.Equal(Receiver.Name, receiverGroup.Members[0].Name);
        Assert.Equal(DeviceKind.Keyboard, receiverGroup.Members[0].Kind);
        Assert.Equal(new[] { DeviceTransport.Receiver }, receiverGroup.Members[0].Transports);
        Assert.Equal(new[] { DeviceTransport.Bluetooth }, receiverGroup.Members[1].Transports);
        var separate = Assert.Single(saved.Preferences.DeviceLinks, group => group.PrimaryDeviceId == "separate").Members[0];
        Assert.Equal("Saved name", separate.Name);
        Assert.Equal(DeviceKind.Unknown, separate.Kind);
        Assert.Equal(new[] { DeviceTransport.Wired }, separate.Transports);
        Assert.DoesNotContain(saved.Preferences.DeviceLinks.SelectMany(group => group.Members), member => member.DeviceId == "not-saved");
        var reloaded = new TrayPreferencesStore(path);
        Assert.True(reloaded.Load().Success);
        AssertLinkState(saved.Preferences, reloaded.Preferences);
        Saved(await reloaded.RestoreDeviceLinksAsync(saved.Preferences, saved.PreviousPreferences!));
        AssertLinkState(before, reloaded.Preferences);
    }

    [Fact]
    public void InvalidObservedTransportsCannotChangeExistingMembers()
    {
        var settings = Changed(AppPreferenceLinks.Link(AppPreferences.Default, Receiver, Bluetooth));
        var invalid = new IReadOnlyList<DeviceTransport>[]
        {
            [DeviceTransport.Unknown], [DeviceTransport.Mixed], [(DeviceTransport)999],
            [DeviceTransport.Bluetooth, DeviceTransport.Bluetooth]
        };
        foreach (var transports in invalid)
        {
            var result = AppPreferenceLinks.Link(settings, Receiver, new("third", "Third"),
                knownMembers: [Receiver with { Transports = transports }]);
            Assert.False(result.Success);
            AssertLinkState(settings, result.Preferences);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"Bluetooth\"")]
    [InlineData("[\"Unknown\"]")]
    [InlineData("[\"Mixed\"]")]
    [InlineData("[\"Bluetooth\",\"Bluetooth\"]")]
    [InlineData("[1]")]
    [InlineData("[\"1\"]")]
    [InlineData("[\"bluetooth\"]")]
    [InlineData("[\"Invalid\"]")]
    [InlineData("[\"Bluetooth\",\"Receiver\",\"Wired\",\"Bluetooth\"]")]
    public async Task InvalidTransportMetadataCannotBeLoadedOrOverwritten(string transports)
    {
        var path = NewPath();
        var original = "{\"version\":3,\"priorityDevices\":[],\"mainColorMode\":\"System\",\"deviceCustomizations\":[],\"deviceLinks\":[{\"primaryId\":\"a\",\"members\":[{\"id\":\"a\",\"name\":\"A\",\"transports\":" + transports + "},{\"id\":\"b\",\"name\":\"B\"}]}],\"suspendedPriorities\":[]}";
        await File.WriteAllTextAsync(path, original);
        var store = new TrayPreferencesStore(path);
        Assert.False(store.Load().Success);
        Assert.False((await store.LinkDevicesAsync(Receiver, Bluetooth)).Success);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    private static DeviceLinkMember Member(int index) => new($"member-{index}", $"Member {index}");
    private static TrayPreference Priority(DeviceLinkMember member) => new(member.DeviceId, member.Name);
    private static AppPreferences FullGroup(IReadOnlyList<TrayPreference> priorities)
    {
        var settings = new AppPreferences(priorities, MainColorMode.System, []);
        for (var i = 1; i <= 7; i++) settings = Changed(AppPreferenceLinks.Link(settings, Receiver, Member(i)));
        return settings;
    }
    private static void AssertLinkState(AppPreferences expected, AppPreferences actual)
    {
        static string State(AppPreferences settings) => JsonSerializer.Serialize(new { settings.DeviceLinks, settings.Priorities, settings.SuspendedPriorities });
        Assert.Equal(State(expected), State(actual));
    }

    [Theory]
    [InlineData("[{\"primaryId\":\"a\",\"members\":[{\"id\":\"a\",\"name\":\"A\"}]}]", "[]")]
    [InlineData("[{\"primaryId\":\"missing\",\"members\":[{\"id\":\"a\",\"name\":\"A\"},{\"id\":\"b\",\"name\":\"B\"}]}]", "[]")]
    [InlineData("[{\"primaryId\":\"a\",\"members\":[{\"id\":\"a\",\"name\":\"A\"},{\"id\":\"a\",\"name\":\"A\"}]}]", "[]")]
    [InlineData("[{\"primaryId\":\"a\",\"members\":[{\"id\":\"a\",\"name\":\"A\"},{\"id\":\"b\",\"name\":\"B\"}]},{\"primaryId\":\"c\",\"members\":[{\"id\":\"c\",\"name\":\"C\"},{\"id\":\"b\",\"name\":\"B\"}]}]", "[]")]
    [InlineData("[{\"primaryId\":\"a\",\"members\":[{\"id\":\"a\",\"name\":\"A\"},{\"id\":\"\",\"name\":\"B\"}]}]", "[]")]
    [InlineData("[]", "[{\"primaryId\":\"a\",\"id\":\"b\",\"name\":\"B\",\"index\":0}]")]
    [InlineData("[{\"primaryId\":\"a\",\"members\":[{\"id\":\"a\",\"name\":\"A\"},{\"id\":\"b\",\"name\":\"B\"}]}]", "[{\"primaryId\":\"a\",\"id\":\"b\",\"name\":\"B\",\"index\":-1}]")]
    public async Task InvalidPersistedLinksOrPausedPrioritiesRemainProtectedUntilExplicitReset(string links, string suspended)
    {
        var path = NewPath();
        var original = "{\"version\":3,\"priorityDevices\":[],\"mainColorMode\":\"System\",\"deviceCustomizations\":[],\"deviceLinks\":" + links + ",\"suspendedPriorities\":" + suspended + "}";
        await File.WriteAllTextAsync(path, original);
        var store = new TrayPreferencesStore(path);
        Assert.False(store.Load().Success);
        Assert.False((await store.LinkDevicesAsync(Receiver, Bluetooth)).Success);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        var reset = await store.SaveAsync([], replaceInvalid: true, clearSuspendedPriorities: true);
        Saved(reset);
        Assert.Equal(original, await File.ReadAllTextAsync(reset.BackupPath!));
        Assert.Empty(reset.Preferences!.DeviceLinks);
        Assert.Empty(reset.Preferences.SuspendedPriorities);
    }

    private static void Saved(TrayPreferencesResult result) => Assert.True(result.Success, result.Error);
    private static AppPreferences Changed(DeviceLinkChangeResult result) { Assert.True(result.Success, result.Error); return result.Preferences; }
    private static string NewPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Project root not found for test artifacts.");
        var directory = Path.Combine(root.FullName, ".artifacts", "tests", "device-link-preferences", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "settings.json");
    }
}
