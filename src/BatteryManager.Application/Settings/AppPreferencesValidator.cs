using BatteryManager.Core;

namespace BatteryManager.Application;

// Pure settings rules shared by in-memory operations and file persistence.
internal static class AppPreferencesValidator
{
    internal const int MaximumDevices = 64;
    internal const int MaximumNameLength = 128;

    internal static string? Validate(AppPreferences settings)
    {
        var priorityError = ValidatePriorities(settings.Priorities);
        if (priorityError is not null) return priorityError;
        if (!Enum.IsDefined(settings.MainColorMode)) return "颜色模式无效。";
        if (settings.DeviceCustomizations.Count > MaximumDevices) return $"最多允许 {MaximumDevices} 台设备的名称与类型设置。";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in settings.DeviceCustomizations)
        {
            if (item is null || !ValidText(item.DeviceId, 256)) return "设备标识无效。";
            if (!ids.Add(item.DeviceId)) return "设备设置包含重复标识。";
            if (item.Name is not null && !ValidText(item.Name, MaximumNameLength)) return $"设备名称须为 1–{MaximumNameLength} 个有效字符，且不能包含控制字符。";
            if (item.Kind is { } kind && (!Enum.IsDefined(kind) || kind == DisplayDeviceKind.Unknown)) return "设备类型无效。";
            if (item.Name is null && item.Kind is null) return "设备设置没有用户选择。";
        }
        var linksError = DeviceLinks.Validate(settings.DeviceLinks);
        if (linksError is not null) return linksError;
        if (settings.SuspendedPriorities is null) return "关联前的优先级记录无效。";
        var hiddenMembers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in settings.DeviceLinks)
        {
            foreach (var member in group.Members)
            {
                if (member.DeviceId != group.PrimaryDeviceId) hiddenMembers.Add(member.DeviceId, group.PrimaryDeviceId);
            }
        }
        if (settings.Priorities.Any(item => hiddenMembers.ContainsKey(item.DeviceId))) return "优先级只能选择关联后的主设备。";
        if (settings.SuspendedPriorities.Count + settings.Priorities.Count > MaximumDevices) return $"最多保留 {MaximumDevices} 个当前及关联前优先设备。";
        var suspendedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in settings.SuspendedPriorities)
        {
            if (item is null || item.Preference is null || ValidatePriorities([item.Preference]) is not null ||
                item.Index is < 0 or >= MaximumDevices || !suspendedIds.Add(item.Preference.DeviceId) ||
                !hiddenMembers.TryGetValue(item.Preference.DeviceId, out var primary) || primary != item.PrimaryDeviceId)
                return "关联前的优先级记录无效。";
        }
        return null;
    }

    internal static string? ValidatePriorities(IReadOnlyList<TrayPreference> priorities)
    {
        if (priorities.Count > MaximumDevices) return $"最多允许 {MaximumDevices} 个优先设备。";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preference in priorities)
        {
            if (preference is null || !ValidText(preference.DeviceId, 256)) return "优先设备标识无效。";
            if (!ids.Add(preference.DeviceId)) return "优先设备列表包含重复标识。";
            if (!ValidText(preference.Name, MaximumNameLength)) return $"优先设备名称须为 1–{MaximumNameLength} 个有效字符，且不能包含控制字符。";
        }
        return null;
    }

    internal static bool ValidText(string? text, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength) return false;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsControl(text[index])) return false;
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index == text.Length || !char.IsLowSurrogate(text[index])) return false;
            }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }

}
