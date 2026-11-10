using System.Text.RegularExpressions;
using BatteryManager.Core;

namespace BatteryManager.WindowsNative;

public static partial class WindowsDeviceMapping
{
    public static string? NormalizeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim();
        return normalized.Length == 12 && normalized.All(Uri.IsHexDigit) && normalized != "000000000000" && !normalized.Equals("FFFFFFFFFFFF", StringComparison.OrdinalIgnoreCase)
            ? normalized.ToUpperInvariant() : null;
    }

    public static string? NormalizeContainer(string? value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty && id != new Guid("00000000-0000-0000-ffff-ffffffffffff") && id != new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff")
            ? id.ToString("D") : null;

    public static string? Address(RawWindowsDevice raw)
    {
        var direct = NormalizeAddress(Text(raw.Properties, WindowsPropertyNames.Address));
        if (direct is not null) return direct;
        var id = Text(raw.Properties, WindowsPropertyNames.InstanceId) ?? raw.Id;
        var match = AddressInInstance().Match(id);
        if (!match.Success) match = AddressInClassicService().Match(id);
        return match.Success ? NormalizeAddress(match.Groups[1].Value) : null;
    }

    public static bool IsBluetoothLe(RawWindowsDevice raw, WindowsQueryKind kind) =>
        kind == WindowsQueryKind.BluetoothLe || raw.Id.Contains("BTHLE", StringComparison.OrdinalIgnoreCase) ||
        (Text(raw.Properties, WindowsPropertyNames.InstanceId)?.Contains("BTHLE", StringComparison.OrdinalIgnoreCase) ?? false);

    public static bool IsBluetooth(RawWindowsDevice raw, WindowsQueryKind kind) =>
        kind is WindowsQueryKind.BluetoothClassic or WindowsQueryKind.BluetoothLe ||
        NormalizeAddress(Text(raw.Properties, WindowsPropertyNames.Address)) is not null ||
        raw.Id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ||
        (Text(raw.Properties, WindowsPropertyNames.InstanceId)?.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ?? false);

    public static bool IsRelevant(RawWindowsDevice raw, WindowsQueryKind kind) =>
        IsBluetooth(raw, kind) && Address(raw) is not null;

    public static string Endpoint(RawWindowsDevice raw, WindowsQueryKind kind)
    {
        var address = Address(raw);
        if (address is not null && IsBluetooth(raw, kind))
            return $"{(IsBluetoothLe(raw, kind) ? "bt-le" : "bt-classic")}:{address}";
        var id = Text(raw.Properties, WindowsPropertyNames.InstanceId) ?? raw.Id;
        return $"path:{id.Trim().ToUpperInvariant()}";
    }

    public static DeviceKind Kind(RawWindowsDevice raw)
    {
        var text = string.Join(' ', Text(raw.Properties, "System.Devices.Category"),
            Text(raw.Properties, "System.Devices.Aep.Category"), raw.Name).ToLowerInvariant();
        if (ContainsAny(text, "keyboard", "键盘")) return DeviceKind.Keyboard;
        if (ContainsAny(text, "mouse", "鼠标")) return DeviceKind.Mouse;
        if (ContainsAny(text, "headphone", "headset", "earbud", "airpods", "buds", "耳机", "audio")) return DeviceKind.Headphones;
        return DeviceKind.Unknown;
    }

    public static string? Text(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value)) return null;
        return value switch
        {
            Guid id => id.ToString("D"),
            string text when !string.IsNullOrWhiteSpace(text) => text,
            IEnumerable<string> texts => string.Join(';', texts),
            _ => null
        };
    }

    public static bool? Boolean(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) && value is bool boolean ? boolean : null;

    private static bool ContainsAny(string text, params string[] terms) => terms.Any(text.Contains);

    [GeneratedRegex(@"(?:DEV_|BLUETOOTHDEVICE_|^|[_\\])([0-9A-Fa-f]{12})(?:$|[^0-9A-Fa-f])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressInInstance();

    // Classic service nodes (including HFP battery) encode the remote address
    // after '&'. Restrict this to the BTHENUM service instance structure.
    [GeneratedRegex(@"^BTHENUM\\\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}[^\\]*\\[^\\]*&([0-9A-Fa-f]{12})_C[0-9A-Fa-f]{8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressInClassicService();
}
