namespace BatteryManager.WindowsNative;

// The command and decoding are verified only for receiver VID 373B / PID 11D9.
// Response checksum semantics have not been established by the hardware evidence.
public static class AtkCompxProtocol
{
    public const int ReportLength = 17;
    private const string ReceiverPathPrefix = @"\\?\hid#vid_373b&pid_11d9&mi_01&col";

    public static bool Supports(HidInterface device) =>
        device.VendorId == 0x373B && device.ProductId == 0x11D9 &&
        IsVerifiedReceiverInterface(device.Path) &&
        device.Usage == 2 && device.UsagePage is 0xFF02 or 0xFF04 &&
        device.OutputReportLength >= ReportLength && device.InputReportLength >= ReportLength;

    private static bool IsVerifiedReceiverInterface(string path) =>
        // Scope this protocol to the verified USB composite receiver interface.
        // Matching VID/PID alone also admits Bluetooth HID collections on Windows.
        path.StartsWith(ReceiverPathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.Length > ReceiverPathPrefix.Length + 2 &&
        Uri.IsHexDigit(path[ReceiverPathPrefix.Length]) && Uri.IsHexDigit(path[ReceiverPathPrefix.Length + 1]) &&
        path[ReceiverPathPrefix.Length + 2] == '#';

    public static byte[] CreateOnlineQuery(int outputReportLength) => CreateQuery(outputReportLength, 0x03);

    public static byte[] CreateBatteryQuery(int outputReportLength) => CreateQuery(outputReportLength, 0x04);

    private static byte[] CreateQuery(int outputReportLength, byte command)
    {
        if (outputReportLength < ReportLength || outputReportLength > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(outputReportLength));
        var report = new byte[outputReportLength];
        report[0] = 0x08;
        report[1] = command;
        report[16] = (byte)(0x55 - 0x08 - command);
        return report;
    }

    public static bool TryParseOnline(ReadOnlySpan<byte> report, out bool? online)
    {
        online = null;
        var offset = PayloadOffset(report, 0x03);
        if (offset < 0) return false;
        // ATK HUB HNt.mouseStatus reads payload[5] and recognizes 1 as online.
        // No meaning for the other nonzero status bytes has been established.
        online = report[offset + 5] switch { 0 => false, 1 => true, _ => (bool?)null };
        return true;
    }

    public static bool TryParseBattery(ReadOnlySpan<byte> report, out AtkBatteryReport battery)
    {
        battery = default;
        var offset = PayloadOffset(report, 0x04);
        if (offset < 0 || report[offset + 5] > 100) return false;
        var charging = report[offset + 6] switch { 0 => (bool?)false, 1 => true, _ => null };
        battery = new(report[offset + 5], charging, (report[offset + 7] << 8) | report[offset + 8]);
        return true;
    }

    private static int PayloadOffset(ReadOnlySpan<byte> report, byte command) =>
        // The native path includes a report ID. Captured vectors may contain only
        // the complete 16-byte payload after removal of that ID.
        report.Length >= ReportLength && report[0] == 0x08 && report[1] == command && report[2] == 0 ? 1 :
        report.Length == ReportLength - 1 && report[0] == command && report[1] == 0 ? 0 : -1;
}

public readonly record struct AtkBatteryReport(int Percent, bool? Charging, int Millivolts);
