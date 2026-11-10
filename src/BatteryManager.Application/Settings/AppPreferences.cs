using BatteryManager.Core;

namespace BatteryManager.Application;

internal sealed record AppPreferences(
    IReadOnlyList<TrayPreference> Priorities,
    MainColorMode MainColorMode,
    IReadOnlyList<DeviceCustomization> DeviceCustomizations)
{
    public IReadOnlyList<DeviceLinkGroup> DeviceLinks { get; init; } = Array.Empty<DeviceLinkGroup>();
    public IReadOnlyList<SuspendedLinkPriority> SuspendedPriorities { get; init; } = Array.Empty<SuspendedLinkPriority>();
    public static AppPreferences Default { get; } = new(
        Array.AsReadOnly(Array.Empty<TrayPreference>()), MainColorMode.System,
        Array.AsReadOnly(Array.Empty<DeviceCustomization>()));
}

internal sealed record SuspendedLinkPriority(string PrimaryDeviceId, TrayPreference Preference, int Index);
internal sealed record DeviceLinkChangeResult(bool Success, AppPreferences Preferences, string? Error = null);

internal sealed record TrayPreferencesResult(
    bool Success,
    IReadOnlyList<TrayPreference> Priorities,
    string? Error = null,
    bool CanSave = true,
    string? BackupPath = null,
    AppPreferences? Preferences = null)
{
    // A merge undo must restore the baseline read inside the same write transaction,
    // which may include settings changed on disk since the app last rendered.
    public AppPreferences? PreviousPreferences { get; init; }
}

