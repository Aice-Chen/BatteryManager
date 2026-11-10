namespace BatteryManager.WindowsNative;

// Read-only commands from the official MCHOSE G3 WebHID implementation.
// The allowlist covers the observed G3 A USB receiver, not other G3 families.
// Connection and charging bytes remain raw until their hardware semantics are verified.
public static class MchoseG3Protocol
{
    public const int ReportLength = 65;
    private const string ReceiverPathPrefix = @"\\?\hid#vid_a8a5&pid_2255&mi_02#";

    public static bool Supports(HidInterface device) =>
        device.VendorId == 0xA8A5 && device.ProductId == 0x2255 &&
        string.Equals(device.ProductName?.Trim(), "MCHOSE G3 A", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(device.Manufacturer?.Trim(), "YJX-CHIP", StringComparison.OrdinalIgnoreCase) &&
        device.Path.StartsWith(ReceiverPathPrefix, StringComparison.OrdinalIgnoreCase) &&
        device.Path.Length > ReceiverPathPrefix.Length &&
        device.UsagePage == 0xFF01 && device.Usage == 0x0010 &&
        device.InputReportLength == ReportLength && device.OutputReportLength == ReportLength &&
        device.FeatureReportLength == 0;

    public static byte[] CreateOnlineQuery()
    {
        var report = new byte[ReportLength];
        report[1] = 0x55;
        report[2] = 0xED;
        return report;
    }

    public static byte[] CreateBatteryQuery()
    {
        var report = new byte[ReportLength];
        new byte[] { 0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01, 0x00, 0x00 }.CopyTo(report, 0);
        return report;
    }

    public static bool TryParseOnline(ReadOnlySpan<byte> report, out byte rawStatus)
    {
        rawStatus = 0;
        if (!Matches(report, 0xED)) return false;
        rawStatus = report[9];
        return true;
    }

    public static bool TryParseBattery(ReadOnlySpan<byte> report, out MchoseG3BatteryReport battery)
    {
        battery = default;
        if (!Matches(report, 0x30) || report[9] > 100) return false;
        battery = new(report[9], report[10]);
        return true;
    }

    // No response checksum or meaning for the remaining bytes is established.
    private static bool Matches(ReadOnlySpan<byte> report, byte opcode) =>
        report.Length == ReportLength && report[0] == 0 && report[1] == 0xAA && report[2] == opcode;
}

public readonly record struct MchoseG3BatteryReport(int Percent, byte ChargingFlag);
