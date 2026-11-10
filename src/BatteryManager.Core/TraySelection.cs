namespace BatteryManager.Core;

// Only an explicit user choice is saved. Name is a label for editing an offline choice.
public sealed record TrayPreference(string DeviceId, string Name);

public static class TraySelection
{
    public static DeviceState? Select(
        StateSnapshot snapshot,
        IReadOnlyList<string> prioritizedIds,
        string? previousFallbackId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(prioritizedIds);

        var available = snapshot.Devices.Where(IsAvailable).ToArray();
        foreach (var id in prioritizedIds)
        {
            var preferred = available.FirstOrDefault(device =>
                string.Equals(device.Id, id, StringComparison.Ordinal));
            if (preferred is not null) return preferred;
        }

        return available.FirstOrDefault(device =>
                   string.Equals(device.Id, previousFallbackId, StringComparison.Ordinal))
               ?? available.OrderBy(device => device.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    private static bool IsAvailable(DeviceState device) =>
        device.Connection is ConnectionState.Online or ConnectionState.Recent &&
        device.BatteryPercent is >= 0 and <= 100 &&
        device.Freshness is BatteryFreshness.Current or BatteryFreshness.Recent or BatteryFreshness.Retained;
}
