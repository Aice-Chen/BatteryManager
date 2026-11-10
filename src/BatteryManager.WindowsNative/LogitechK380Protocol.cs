namespace BatteryManager.WindowsNative;

// Only the tested classic-Bluetooth K380 vendor collections are supported. The
// ordinary keyboard, consumer-control and system-input collections are excluded.
public static class LogitechK380Protocol
{
    private const string BluetoothPathPrefix = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002046d_pid&b342&col";

    public static bool Supports(HidInterface device)
    {
        if (device.VendorId != 0x046D || device.ProductId != 0xB342 || device.UsagePage != 0xFF00 ||
            device.FeatureReportLength != 0 ||
            !string.Equals(device.Manufacturer?.Trim(), "Logitech", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(device.ProductName?.Trim(), "Multi-Device Keyboard K380", StringComparison.OrdinalIgnoreCase) ||
            !device.Path.StartsWith(BluetoothPathPrefix, StringComparison.OrdinalIgnoreCase) ||
            device.Path.Length <= BluetoothPathPrefix.Length + 2 ||
            !Uri.IsHexDigit(device.Path[BluetoothPathPrefix.Length]) ||
            !Uri.IsHexDigit(device.Path[BluetoothPathPrefix.Length + 1]) ||
            device.Path[BluetoothPathPrefix.Length + 2] != '#') return false;
        return (device.Usage == 1 && device.InputReportLength == 7 && device.OutputReportLength == 7) ||
               (device.Usage == 2 && device.InputReportLength == 20 && device.OutputReportLength == 20);
    }

    public static byte[] CreatePingQuery(byte softwareId, byte nonce) => CreateQuery(0, 1, softwareId, [0, 0, nonce]);
    public static byte[] CreateFeatureQuery(byte softwareId) => CreateQuery(0, 0, softwareId, [0x10, 0, 0]);
    public static byte[] CreateBatteryQuery(byte featureIndex, byte softwareId) => CreateBatteryFeatureQuery(featureIndex, 0, softwareId);
    public static byte[] CreateCapabilityQuery(byte featureIndex, byte softwareId) => CreateBatteryFeatureQuery(featureIndex, 1, softwareId);

    // A response may arrive on either collection. Notifications have SWID zero;
    // errors carry the original feature and function/SWID after their error marker.
    public static bool TryMatchReply(byte[] report, byte[] request, out byte[] parameters, out byte? errorCode)
    {
        parameters = [];
        errorCode = null;
        if (request.Length != 20 || request[0] != 0x11 || request[1] != 0xFF || (request[3] & 0x0F) == 0 ||
            !((report.Length == 7 && report[0] == 0x10) || (report.Length == 20 && report[0] == 0x11)) ||
            report[1] is not (0xFF or 0)) return false;
        if (report[2] is 0xFF or 0x8F && report[3] == request[2] && report[4] == request[3])
        {
            errorCode = report[5];
            return true;
        }
        if (report[2] != request[2] || report[3] != request[3]) return false;
        if (request[2] == 0 && (request[3] & 0xF0) == 0x10 && report[6] != request[6]) return false;
        parameters = report[4..];
        return true;
    }

    public static bool TryParseBattery(byte[] parameters, out LogitechK380Battery battery)
    {
        battery = default;
        if (parameters.Length < 3 || parameters[0] > 100 || parameters[1] > 100 || parameters[2] > 7 ||
            (parameters[0] != 0 && parameters[1] > parameters[0])) return false;
        battery = new(parameters[0], parameters[1], parameters[2]);
        return true;
    }

    public static bool TryParseCapability(byte[] parameters, out LogitechK380Capability capability)
    {
        capability = default;
        if (parameters.Length < 2 || parameters[0] is < 2 or > 100 || (parameters[1] & 0xF8) != 0) return false;
        capability = new(parameters[0], parameters[1]);
        return true;
    }

    private static byte[] CreateBatteryFeatureQuery(byte featureIndex, byte function, byte softwareId)
    {
        if (featureIndex is 0 or 0x8F or 0xFF) throw new ArgumentOutOfRangeException(nameof(featureIndex));
        return CreateQuery(featureIndex, function, softwareId, []);
    }

    private static byte[] CreateQuery(byte featureIndex, byte function, byte softwareId, byte[] parameters)
    {
        if (softwareId is 0 or > 15) throw new ArgumentOutOfRangeException(nameof(softwareId));
        var report = new byte[20];
        report[0] = 0x11;
        report[1] = 0xFF;
        report[2] = featureIndex;
        report[3] = (byte)((function << 4) | softwareId);
        parameters.CopyTo(report, 4);
        return report;
    }
}

public readonly record struct LogitechK380Battery(int Percent, int NextPercent, byte Status);
public readonly record struct LogitechK380Capability(int LevelCount, byte Flags)
{
    public string Precision => LevelCount < 10 || (Flags & 2) == 0 ? $"coarse:{LevelCount}" : "percent";
}
