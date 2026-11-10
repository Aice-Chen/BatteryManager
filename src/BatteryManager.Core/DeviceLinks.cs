namespace BatteryManager.Core;

// These are explicit user relationships between stable detected device IDs.
// Names label saved choices; they are never evidence of hardware identity.
public sealed record DeviceLinkMember(string DeviceId, string Name, DeviceKind Kind = DeviceKind.Unknown)
{
    // Static, verified transport metadata for presentation. It never supplies
    // evidence that a member exists, is connected, or may enter the device list.
    public IReadOnlyList<DeviceTransport> Transports { get; init; } = Array.Empty<DeviceTransport>();
}
public sealed record DeviceLinkGroup(string PrimaryDeviceId, IReadOnlyList<DeviceLinkMember> Members);

public static class DeviceLinks
{
    public static string? Validate(IReadOnlyList<DeviceLinkGroup> groups)
    {
        if (groups is null || groups.Count > 64) return "设备关联数量无效。";
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group is null || !ValidText(group.PrimaryDeviceId, 256) || group.Members is null || group.Members.Count is < 2 or > 8)
                return "每组关联需要 2 至 8 个设备，并指定保留设置的设备。";
            var primaryFound = false;
            foreach (var member in group.Members)
            {
                if (member is null || !ValidText(member.DeviceId, 256) || !ValidText(member.Name, 128, allowOuterWhitespace: true) || !Enum.IsDefined(member.Kind))
                    return "关联设备的标识、名称或类型无效。";
                if (member.Transports is null || member.Transports.Count > 3 || member.Transports.Distinct().Count() != member.Transports.Count ||
                    member.Transports.Any(transport => transport is not (DeviceTransport.Bluetooth or DeviceTransport.Receiver or DeviceTransport.Wired)))
                    return "关联设备的连接方式无效。";
                if (!members.Add(member.DeviceId)) return "同一设备只能出现在一个关联组中。";
                primaryFound |= member.DeviceId == group.PrimaryDeviceId;
            }
            if (!primaryFound) return "保留设置的设备必须属于该关联组。";
        }
        return null;
    }

    public static IReadOnlyList<DeviceLinkGroup> CopyValidated(IReadOnlyList<DeviceLinkGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (Validate(groups) is { } error) throw new ArgumentException(error, nameof(groups));
        return Array.AsReadOnly(groups.Select(group => group with
        {
            Members = Array.AsReadOnly(group.Members.Select(member => member with
            {
                Transports = member.Transports.Count == 0 ? Array.Empty<DeviceTransport>() : Array.AsReadOnly(member.Transports.Order().ToArray())
            }).ToArray())
        }).ToArray());
    }

    private static bool ValidText(string? value, int maximum, bool allowOuterWhitespace = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || (!allowOuterWhitespace && value.Trim() != value)) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]) || char.IsLowSurrogate(value[index])) return false;
            if (!char.IsHighSurrogate(value[index])) continue;
            if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
}
