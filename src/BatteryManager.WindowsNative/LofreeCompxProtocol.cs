using System.Security.Cryptography;

namespace BatteryManager.WindowsNative;

// The USB collection and identity whitelist cover the tested OE922 receiver only.
// Request checksums follow the official keyboard implementation. Response checksum
// semantics are not established and are not presented as verified integrity checks.
public static class LofreeCompxProtocol
{
    public const int ReportLength = 17;
    private const string ReceiverPathPrefix = @"\\?\hid#vid_05ac&pid_024f&mi_01&col";

    public static bool Supports(HidInterface device) =>
        device.VendorId == 0x05AC && device.ProductId == 0x024F &&
        string.Equals(device.Manufacturer?.Trim(), "CX", StringComparison.OrdinalIgnoreCase) &&
        device.UsagePage == 0xFF02 && device.Usage == 2 &&
        device.InputReportLength == ReportLength && device.OutputReportLength == ReportLength &&
        device.Path.StartsWith(ReceiverPathPrefix, StringComparison.OrdinalIgnoreCase) &&
        device.Path.Length > ReceiverPathPrefix.Length + 2 &&
        Uri.IsHexDigit(device.Path[ReceiverPathPrefix.Length]) && Uri.IsHexDigit(device.Path[ReceiverPathPrefix.Length + 1]) &&
        device.Path[ReceiverPathPrefix.Length + 2] == '#';

    public static byte[] CreateOnlineQuery() => CreateQuery(0x03);
    public static byte[] CreateBatteryQuery() => CreateQuery(0x04);
    public static byte[] CreateIdentityQuery() => CreateIdentityQuery(RandomNumberGenerator.GetBytes(4));

    public static byte[] CreateIdentityQuery(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != 4) throw new ArgumentException("The identity query requires exactly four challenge bytes.", nameof(challenge));
        var report = CreateQuery(0x01);
        report[5] = 0x88;
        challenge.CopyTo(report.AsSpan(6, 4));
        SetChecksum(report);
        return report;
    }

    public static bool TryParseOnline(ReadOnlySpan<byte> report, out bool? online)
    {
        online = null;
        if (!Matches(report, 0x03, 1)) return false;
        online = report[6] switch { 0 => false, 1 => true, _ => (bool?)null };
        return true;
    }

    public static bool TryParseIdentity(ReadOnlySpan<byte> report, out LofreeIdentity identity)
    {
        identity = default;
        if (!Matches(report, 0x01, 7)) return false;
        identity = new(report[10], report[11], report[12]);
        return true;
    }

    public static bool IsVerifiedIdentity(LofreeIdentity identity) => identity is { Cid: 1, Mid: 2, Type: 0 };

    public static bool TryParseBattery(ReadOnlySpan<byte> report, out LofreeBatteryReport battery)
    {
        battery = default;
        if (!Matches(report, 0x04, 4) || report[6] > 100) return false;
        var charging = report[7] switch { 0 => (bool?)false, 1 => true, _ => null };
        battery = new(report[6], charging, (report[8] << 8) | report[9]);
        return true;
    }

    private static bool Matches(ReadOnlySpan<byte> report, byte command, int minimumDataLength) =>
        report.Length == ReportLength && report[0] == 0x08 && report[1] == command && report[2] == 0 &&
        (report[5] & 0x80) != 0 && (report[5] & 0x7F) >= minimumDataLength && (report[5] & 0x7F) <= 10;

    private static byte[] CreateQuery(byte command)
    {
        var report = new byte[ReportLength];
        report[0] = 0x08;
        report[1] = command;
        report[5] = 0x80;
        SetChecksum(report);
        return report;
    }

    private static void SetChecksum(byte[] report)
    {
        var sum = 0;
        for (var index = 0; index < ReportLength - 1; index++) sum += report[index];
        report[^1] = (byte)((0x55 - sum) & 0xFF);
    }
}

public readonly record struct LofreeIdentity(byte Cid, byte Mid, byte Type);
public readonly record struct LofreeBatteryReport(int Percent, bool? Charging, int Millivolts);
