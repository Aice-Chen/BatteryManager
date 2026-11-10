using BatteryManager.Core;

namespace BatteryManager.Application;

// The same immutable settings operation is used by production disk saves and by
// the fictional, in-memory app scenarios. Hardware identity/evidence is untouched.
internal static class AppPreferenceLinks
{
    private const int MaximumGroupMembers = 8;

    /// <param name="present">Records that must be protected from eviction, including temporarily
    /// unresponsive records. A non-null, complete snapshot allows the earliest absent non-primary
    /// connection to be forgotten; omitting the snapshot preserves the original eight-member limit.</param>
    public static DeviceLinkChangeResult Link(AppPreferences settings, DeviceLinkMember primary, DeviceLinkMember other,
        IReadOnlySet<string>? present = null, IReadOnlyList<DeviceLinkMember>? knownMembers = null)
    {
        var baselineError = AppPreferencesValidator.Validate(settings);
        if (baselineError is not null) return new(false, settings, baselineError);
        if (primary is null || other is null || primary.DeviceId == other.DeviceId)
            return new(false, settings, "请选择两台不同的设备记录。");
        var memberError = DeviceLinks.Validate([new(primary.DeviceId, [primary, other])]);
        if (memberError is not null) return new(false, settings, memberError);
        var primaryGroup = settings.DeviceLinks.FirstOrDefault(group => group.Members.Any(member => member.DeviceId == primary.DeviceId));
        if (primaryGroup is not null && primaryGroup.PrimaryDeviceId != primary.DeviceId)
            return new(false, settings, "请从已关联设备的主记录添加连接方式。");
        if (settings.DeviceLinks.Any(group => group.Members.Any(member => member.DeviceId == other.DeviceId)))
            return new(false, settings, "另一条记录已有关联，请先解除原关联。");
        var members = primaryGroup?.Members.ToList() ?? [primary];
        if (primaryGroup is not null)
        {
            var primaryIndex = members.FindIndex(member => member.DeviceId == primary.DeviceId);
            members[primaryIndex] = members[primaryIndex] with
            {
                Transports = CopyTransports(members[primaryIndex].Transports.Concat(primary.Transports).Distinct().Order().ToArray())
            };
        }
        var suspended = settings.SuspendedPriorities.ToList();
        var restoredPriorities = RestorePriorities(settings);
        if (members.Count >= MaximumGroupMembers)
        {
            var forget = present is null ? null
                : members.FirstOrDefault(member => member.DeviceId != primary.DeviceId && !present.Contains(member.DeviceId));
            if (forget is null) return new(false, settings, "这台设备合并的连接已达上限。");
            // The forgotten record leaves the group for good; its paused tray priority goes with it.
            members.Remove(forget);
            suspended.RemoveAll(item => item.PrimaryDeviceId == primary.DeviceId && item.Preference.DeviceId == forget.DeviceId);
            restoredPriorities.RemoveAll(item => item.DeviceId == forget.DeviceId);
            // Removing a paused row also removes its position from the virtual full list.
            // Rebase the other paused positions, including those in other groups, so
            // later unlinking cannot move them past unrelated active priorities.
            suspended = suspended.Select(item => item with
            {
                Index = restoredPriorities.FindIndex(preference => preference.DeviceId == item.Preference.DeviceId)
            }).ToList();
        }
        members.Add(other);
        var groups = settings.DeviceLinks.Where(group => group.PrimaryDeviceId != primary.DeviceId).ToList();
        groups.Add(new(primary.DeviceId, Array.AsReadOnly(members.ToArray())));
        if (knownMembers is not null)
        {
            var existingIds = settings.DeviceLinks.SelectMany(group => group.Members).Select(member => member.DeviceId).ToHashSet(StringComparer.Ordinal);
            var known = new Dictionary<string, DeviceLinkMember>(StringComparer.Ordinal);
            foreach (var member in knownMembers)
            {
                if (member is null) return new(false, settings, "关联成员的连接方式无效。");
                if (!existingIds.Contains(member.DeviceId)) continue;
                if (member.Transports is null || member.Transports.Count > 3 ||
                    member.Transports.Any(transport => transport is not (DeviceTransport.Bluetooth or DeviceTransport.Receiver or DeviceTransport.Wired)) ||
                    member.Transports.Distinct().Count() != member.Transports.Count || !known.TryAdd(member.DeviceId, member))
                    return new(false, settings, "关联成员的连接方式无效。");
            }
            groups = groups.Select(group => group with
            {
                Members = Array.AsReadOnly(group.Members.Select(member => known.TryGetValue(member.DeviceId, out var observed)
                    ? member with { Transports = CopyTransports(member.Transports.Concat(observed.Transports).Distinct().Order().ToArray()) }
                    : member).ToArray())
            }).ToList();
        }
        var priorities = settings.Priorities.ToList();
        var index = priorities.FindIndex(item => item.DeviceId == other.DeviceId);
        if (index >= 0)
        {
            // Include already paused rows when remembering the position, so
            // adding a third connection does not shift its eventual restoration.
            var originalIndex = restoredPriorities.FindIndex(item => item.DeviceId == other.DeviceId);
            suspended.Add(new(primary.DeviceId, priorities[index], originalIndex));
            priorities.RemoveAt(index);
        }
        var candidate = settings with
        {
            DeviceLinks = Array.AsReadOnly(groups.OrderBy(group => group.PrimaryDeviceId, StringComparer.Ordinal).ToArray()),
            Priorities = Array.AsReadOnly(priorities.ToArray()),
            SuspendedPriorities = Array.AsReadOnly(suspended.ToArray())
        };
        var error = AppPreferencesValidator.Validate(candidate);
        return error is null ? new(true, candidate with { DeviceLinks = DeviceLinks.CopyValidated(candidate.DeviceLinks) }) : new(false, settings, error);
    }

    public static DeviceLinkChangeResult Unlink(AppPreferences settings, string primaryDeviceId)
    {
        var baselineError = AppPreferencesValidator.Validate(settings);
        if (baselineError is not null) return new(false, settings, baselineError);
        if (!settings.DeviceLinks.Any(group => group.PrimaryDeviceId == primaryDeviceId))
            return new(false, settings, "该设备没有可解除的关联。");
        var stillSuspended = settings.SuspendedPriorities.Where(item => item.PrimaryDeviceId != primaryDeviceId).ToArray();
        var stillHidden = stillSuspended.Select(item => item.Preference.DeviceId).ToHashSet(StringComparer.Ordinal);
        var priorities = RestorePriorities(settings).Where(item => !stillHidden.Contains(item.DeviceId)).ToArray();
        var candidate = settings with
        {
            DeviceLinks = Array.AsReadOnly(settings.DeviceLinks.Where(group => group.PrimaryDeviceId != primaryDeviceId).ToArray()),
            SuspendedPriorities = Array.AsReadOnly(stillSuspended),
            Priorities = Array.AsReadOnly(priorities)
        };
        var error = AppPreferencesValidator.Validate(candidate);
        return error is null ? new(true, candidate) : new(false, settings, error);
    }

    /// <summary>Undo of the last merge change: the links and tray priorities go back exactly as they were,
    /// but only while nothing has changed them since that change.</summary>
    public static DeviceLinkChangeResult Restore(AppPreferences settings, AppPreferences expected, AppPreferences previous)
    {
        var baselineError = AppPreferencesValidator.Validate(settings);
        if (baselineError is not null) return new(false, settings, baselineError);
        var snapshotError = AppPreferencesValidator.Validate(expected) ?? AppPreferencesValidator.Validate(previous);
        if (snapshotError is not null) return new(false, settings, snapshotError);
        if (LinkState(settings) != LinkState(expected)) return new(false, settings, "设置已更改，无法撤销。");
        var candidate = settings with
        {
            DeviceLinks = DeviceLinks.CopyValidated(previous.DeviceLinks),
            Priorities = Array.AsReadOnly(previous.Priorities.ToArray()),
            SuspendedPriorities = Array.AsReadOnly(previous.SuspendedPriorities.ToArray())
        };
        var error = AppPreferencesValidator.Validate(candidate);
        return error is null ? new(true, candidate) : new(false, settings, error);
    }

    private static string LinkState(AppPreferences settings) =>
        System.Text.Json.JsonSerializer.Serialize(new { settings.DeviceLinks, settings.Priorities, settings.SuspendedPriorities });

    private static IReadOnlyList<DeviceTransport> CopyTransports(IReadOnlyList<DeviceTransport> transports) =>
        transports.Count == 0 ? Array.Empty<DeviceTransport>() : Array.AsReadOnly(transports.ToArray());

    public static AppPreferences ClearPriorities(AppPreferences settings) => settings with
    {
        Priorities = Array.Empty<TrayPreference>(), SuspendedPriorities = Array.Empty<SuspendedLinkPriority>()
    };

    private static List<TrayPreference> RestorePriorities(AppPreferences settings)
    {
        // Keep all intervening user edits. An unchanged list regains its old
        // order; shortened lists receive the restored item at the nearest end.
        var priorities = settings.Priorities.ToList();
        var restoredAt = new Dictionary<int, int>();
        foreach (var saved in settings.SuspendedPriorities.OrderBy(item => item.Index))
        {
            var offset = restoredAt.GetValueOrDefault(saved.Index);
            priorities.Insert(Math.Min(saved.Index + offset, priorities.Count), saved.Preference);
            restoredAt[saved.Index] = offset + 1;
        }
        return priorities;
    }
}

