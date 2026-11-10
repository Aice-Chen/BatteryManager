using System.Globalization;

namespace BatteryManager.Core;

public enum DisplayPresence { Online, NoResponse }
public enum BatteryTone { Normal, Stale, None }
public enum DeviceTransport { Unknown, Bluetooth, Receiver, Wired, Mixed }
public enum DisplayDeviceKind { Unknown, Headphones, Earbuds, Keyboard, Mouse, GameController, Speaker, Pen, Other }
public enum MainColorMode { System, Light, Dark }
public enum SourceDisplayResult { Success, NoData, Failed, Unsupported }
public enum DevicePart { Left, Right, Case }
/// <summary>One connection method of a device as the user sees it: in use, not answering, or not connected.</summary>
public enum ConnectionDisplayState { Connected, NoResponse, NotConnected }

public sealed record DeviceCustomization(string DeviceId, string? Name = null, DisplayDeviceKind? Kind = null);
public sealed record BatteryDisplay(int? Value, BatteryTone Tone, bool Charging, bool Low);
public sealed record SourceDisplay(string Method, SourceDisplayResult Result, bool Coarse, string? Error);
public sealed record PartDisplay(DevicePart Part, BatteryDisplay Battery);
public sealed record ConnectionDisplay(DeviceTransport Transport, ConnectionDisplayState State);
public sealed record DeviceDisplay(
    string Id, string Name, string OriginalName, bool NameSetByUser,
    DisplayDeviceKind Kind, DisplayDeviceKind KindDetected, bool KindSetByUser,
    DisplayPresence Presence, DeviceTransport Transport, BatteryDisplay Battery,
    bool ReadFailed, string? Source, IReadOnlyList<SourceDisplay> Sources,
    IReadOnlyList<PartDisplay> Parts, bool Expandable, bool TrayEligible)
{
    /// <summary>One entry per known connection method (Bluetooth, 2.4 GHz, wired), in that fixed order. Several
    /// records of the same method (a receiver moved to another USB port) count as one method with the best state.
    /// A merged card lists them; a single record has at most one.</summary>
    public IReadOnlyList<ConnectionDisplay> Connections { get; init; } = Array.Empty<ConnectionDisplay>();
}

/// <summary>Presentation only. Selection, freshness, identity and lifecycle remain StateStore decisions.</summary>
public static class DeviceDisplayMapper
{
    public static DeviceDisplay? Map(DeviceState device, DeviceCustomization? customization = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.Visible || device.Connection == ConnectionState.Offline) return null;
        if (customization?.DeviceId != device.Id) customization = null;
        var nameSet = !string.IsNullOrWhiteSpace(customization?.Name);
        var name = nameSet ? customization!.Name!.Trim() : device.Name;
        var detected = device.Kind switch
        {
            DeviceKind.Headphones => DisplayDeviceKind.Headphones,
            DeviceKind.Keyboard => DisplayDeviceKind.Keyboard,
            DeviceKind.Mouse => DisplayDeviceKind.Mouse,
            DeviceKind.Other => DisplayDeviceKind.Other,
            _ => DisplayDeviceKind.Unknown
        };
        var kindOverride = customization?.Kind is { } requested && requested != DisplayDeviceKind.Unknown && Enum.IsDefined(requested)
            ? requested : (DisplayDeviceKind?)null;
        var tone = device.BatteryPercent is >= 0 and <= 100 ? device.Freshness switch
        {
            BatteryFreshness.Current or BatteryFreshness.Recent => BatteryTone.Normal,
            BatteryFreshness.Retained => BatteryTone.Stale,
            _ => BatteryTone.None
        } : BatteryTone.None;
        var value = tone == BatteryTone.None ? null : device.BatteryPercent;
        var selected = device.SelectedReading ?? device.Readings.FirstOrDefault(reading => reading.Source == device.BatterySource &&
            reading.Component == "device" && reading.Percent == value && reading.Freshness == device.Freshness);
        var battery = new BatteryDisplay(value, tone, tone == BatteryTone.Normal && selected?.Charging == true,
            value is <= 20 && tone != BatteryTone.None);
        var attempts = device.SourceAttempts.Count > 0 ? device.SourceAttempts : LegacyAttempts(device);
        var sources = attempts.GroupBy(attempt => SourceMethod(attempt.Source), StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var relevant = group.ToArray();
                var result = relevant.Any(attempt => attempt.Status == ReadStatus.Success) ? SourceDisplayResult.Success
                    : relevant.Any(attempt => attempt.Status is ReadStatus.Error or ReadStatus.Timeout) ? SourceDisplayResult.Failed
                    : relevant.All(attempt => attempt.Status == ReadStatus.Unsupported) ? SourceDisplayResult.Unsupported
                    : SourceDisplayResult.NoData;
                var errors = relevant.Where(attempt => attempt.Status != ReadStatus.Canceled)
                    .Select(attempt => attempt.Error).Where(error => !string.IsNullOrWhiteSpace(error)).Distinct(StringComparer.Ordinal);
                return new SourceDisplay(group.Key, result, group.Any(attempt => attempt.Coarse),
                    string.Join(Environment.NewLine, errors) is { Length: > 0 } text ? text : null);
            }).ToArray();
        var presence = device.Connection == ConnectionState.Pending ? DisplayPresence.NoResponse : DisplayPresence.Online;
        var readFailed = presence == DisplayPresence.Online && tone != BatteryTone.Normal &&
            attempts.Any(attempt => attempt.Status is ReadStatus.Error or ReadStatus.Timeout);
        return new(device.Id, name, device.Name, nameSet, kindOverride ?? detected, detected, kindOverride is not null,
            presence, Transport(attempts), battery, readFailed, value is null || device.BatterySource is null ? null : SourceMethod(device.BatterySource),
            Array.AsReadOnly(sources), Array.Empty<PartDisplay>(), sources.Length > 0,
            device.Connection is ConnectionState.Online or ConnectionState.Recent && tone != BatteryTone.None)
        { Connections = Connections(attempts) };
    }

    public static string SourceMethod(string source) => source.StartsWith("windows.", StringComparison.Ordinal) ? "windows" : source;

    private static IReadOnlyList<DeviceSourceAttempt> LegacyAttempts(DeviceState device) =>
        device.Readings.Select(reading => new DeviceSourceAttempt(reading.EndpointId, reading.Source, reading.Attempt,
            reading.Error, false, reading.Precision.StartsWith("coarse:", StringComparison.Ordinal), device.Connection)).ToArray();

    private static IReadOnlyList<ConnectionDisplay> Connections(IReadOnlyList<DeviceSourceAttempt> attempts) => Array.AsReadOnly(attempts
        .Select(attempt => (Transport: EndpointTransport(attempt.EndpointId, attempt.Source), attempt.Connection))
        .Where(item => item.Transport is DeviceTransport.Bluetooth or DeviceTransport.Receiver or DeviceTransport.Wired)
        .GroupBy(item => item.Transport).OrderBy(group => group.Key)
        .Select(group => new ConnectionDisplay(group.Key, group.Max(item => item.Connection switch
        {
            ConnectionState.Online or ConnectionState.Recent => 2,
            ConnectionState.Pending => 1,
            _ => 0
        }) switch { 2 => ConnectionDisplayState.Connected, 1 => ConnectionDisplayState.NoResponse, _ => ConnectionDisplayState.NotConnected }))
        .ToArray());

    private static DeviceTransport Transport(IReadOnlyList<DeviceSourceAttempt> attempts)
    {
        var connected = attempts.Where(attempt => attempt.Connection is ConnectionState.Online or ConnectionState.Recent).ToArray();
        if (connected.Length == 0) connected = attempts.Where(attempt => attempt.Connection == ConnectionState.Pending).ToArray();
        return TransportOf(connected);
    }

    public static DeviceTransport DetectedTransport(DeviceState device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TransportOf(device.SourceAttempts);
    }

    /// <summary>Verified connection methods found in actual endpoint/source evidence,
    /// independent of whether the record is presently visible or connected.</summary>
    public static IReadOnlyList<DeviceTransport> DetectedTransports(DeviceState device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Array.AsReadOnly(device.SourceAttempts.Select(attempt => EndpointTransport(attempt.EndpointId, attempt.Source))
            .Where(transport => transport != DeviceTransport.Unknown).Distinct().Order().ToArray());
    }

    private static DeviceTransport TransportOf(IEnumerable<DeviceSourceAttempt> attempts)
    {
        var transports = attempts.Select(attempt => EndpointTransport(attempt.EndpointId, attempt.Source))
            .Where(transport => transport != DeviceTransport.Unknown).Distinct().ToArray();
        return transports.Length switch { 0 => DeviceTransport.Unknown, 1 => transports[0], _ => DeviceTransport.Mixed };
    }

    private static DeviceTransport EndpointTransport(string endpoint, string source)
    {
        if (endpoint.StartsWith("bt-classic:", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("bt-le:", StringComparison.OrdinalIgnoreCase)) return DeviceTransport.Bluetooth;
        // Only the dedicated verified receiver adapters establish a wireless transport.
        // A general HID/USB path says nothing about wired vs receiver communication.
        if ((source == "hid.atk.compx" && endpoint.StartsWith("hid:atk:", StringComparison.OrdinalIgnoreCase)) ||
            (source == "hid.lofree.compx" && endpoint.StartsWith("hid:lofree:", StringComparison.OrdinalIgnoreCase)) ||
            (source == "hid.mchose.g3" && endpoint.StartsWith("hid:mchose:", StringComparison.OrdinalIgnoreCase)))
            return DeviceTransport.Receiver;
        return DeviceTransport.Unknown;
    }
}

/// <summary>Stable presentation order shared by the main window and the tray panel:
/// device type group first, then the shown name, then identity. Battery values never affect order.</summary>
public static class DeviceDisplayOrder
{
    private static readonly DisplayDeviceKind[] Groups =
    [
        DisplayDeviceKind.Mouse, DisplayDeviceKind.Keyboard, DisplayDeviceKind.Pen, DisplayDeviceKind.GameController,
        DisplayDeviceKind.Headphones, DisplayDeviceKind.Earbuds, DisplayDeviceKind.Speaker,
        DisplayDeviceKind.Other, DisplayDeviceKind.Unknown
    ];

    public static int Group(DisplayDeviceKind kind)
    {
        var index = Array.IndexOf(Groups, kind);
        return index < 0 ? Groups.Length : index;
    }

    public static IReadOnlyList<DeviceDisplay> Sort(IEnumerable<DeviceDisplay> devices, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var names = StringComparer.Create(culture ?? CultureInfo.CurrentCulture, ignoreCase: true);
        return devices.OrderBy(device => Group(device.Kind)).ThenBy(device => device.Name, names)
            .ThenBy(device => device.Id, StringComparer.Ordinal).ToArray();
    }
}
