using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class MchoseG3ProtocolTests
{
    [Fact]
    public void ReadQueriesMatchOfficialWholeReportBytesAndZeroPadding()
    {
        var online = MchoseG3Protocol.CreateOnlineQuery();
        Assert.Equal(65, online.Length);
        Assert.Equal("0055ED", Convert.ToHexString(online.AsSpan(0, 3)));
        Assert.All(online[3..], value => Assert.Equal(0, value));
        var battery = MchoseG3Protocol.CreateBatteryQuery();
        Assert.Equal(65, battery.Length);
        Assert.Equal("005530A50B2E0101010000", Convert.ToHexString(battery.AsSpan(0, 11)));
        Assert.All(battery[11..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void OnlyObservedUsbReceiverIdentityAndCollectionAreEligible()
    {
        var receiver = Receiver();
        Assert.True(MchoseG3Protocol.Supports(receiver));
        Assert.True(MchoseG3Protocol.Supports(receiver with
        {
            Path = receiver.Path.ToUpperInvariant(), ProductName = "mchose g3 a", Manufacturer = "yjx-chip"
        }));
        var unsupported = new[]
        {
            receiver with { VendorId = 0xA8A4 },
            receiver with { ProductId = 0x2256 },
            receiver with { ProductName = "MCHOSE G3 V2" },
            receiver with { ProductName = null },
            receiver with { Manufacturer = "Other" },
            receiver with { Manufacturer = null },
            receiver with { Path = @"\\?\hid#vid_a8a5&pid_2255&col01#9&synthetic-bt&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}" },
            receiver with { Path = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02a8a5_pid&2255_rev&0100_112233445566&col02#9&synthetic-ble&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}" },
            receiver with { Path = receiver.Path.Replace("mi_02#", "mi_01&col05#", StringComparison.Ordinal) },
            receiver with { UsagePage = 0xFF06 },
            receiver with { Usage = 2 },
            receiver with { InputReportLength = 64 },
            receiver with { OutputReportLength = 64 },
            receiver with { FeatureReportLength = 65 }
        };
        Assert.All(unsupported, candidate => Assert.False(MchoseG3Protocol.Supports(candidate)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void OnlineStatusIsPreservedAsRawByte(byte status)
    {
        var report = Reply(0xED);
        report[9] = status;
        Assert.True(MchoseG3Protocol.TryParseOnline(report, out var decoded));
        Assert.Equal(status, decoded);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 1)]
    [InlineData(100, 255)]
    public void BatteryIncludesGenuineZeroAndPreservesUninterpretedCharging(byte percent, byte charging)
    {
        var report = Reply(0x30);
        report[9] = percent;
        report[10] = charging;
        Assert.True(MchoseG3Protocol.TryParseBattery(report, out var battery));
        Assert.Equal(new MchoseG3BatteryReport(percent, charging), battery);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(255)]
    public void OutOfRangeBatteryIsRejected(byte percent)
    {
        var report = Reply(0x30);
        report[9] = percent;
        Assert.False(MchoseG3Protocol.TryParseBattery(report, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(64)]
    [InlineData(66)]
    public void ShortAndOversizedReportsCannotSupplyBatteryOrConnection(int length)
    {
        var online = Reply(0xED);
        var battery = Reply(0x30);
        Array.Resize(ref online, length);
        Array.Resize(ref battery, length);
        Assert.False(MchoseG3Protocol.TryParseOnline(online, out _));
        Assert.False(MchoseG3Protocol.TryParseBattery(battery, out _));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0x55)]
    [InlineData(2, 0xFA)]
    public void WrongReportIdHeaderAndOpcodeAreRejected(int offset, byte replacement)
    {
        var online = Reply(0xED);
        var battery = Reply(0x30);
        online[offset] = replacement;
        battery[offset] = replacement;
        Assert.False(MchoseG3Protocol.TryParseOnline(online, out _));
        Assert.False(MchoseG3Protocol.TryParseBattery(battery, out _));
    }

    [Fact]
    public void BatteryAndOnlineRepliesCannotBeSubstitutedForEachOther()
    {
        Assert.False(MchoseG3Protocol.TryParseOnline(Reply(0x30), out _));
        Assert.False(MchoseG3Protocol.TryParseBattery(Reply(0xED), out _));
    }

    // Synthetic frames deliberately contain nonzero unspecified fields. The
    // protocol evidence does not justify checking those fields or a checksum.
    private static byte[] Reply(byte opcode)
    {
        var report = Enumerable.Range(0, 65).Select(index => (byte)(index * 3)).ToArray();
        report[0] = 0;
        report[1] = 0xAA;
        report[2] = opcode;
        report[9] = 50;
        return report;
    }

    private static HidInterface Receiver() => new(
        @"\\?\hid#vid_a8a5&pid_2255&mi_02#7&synthetic-receiver&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        0xA8A5, 0x2255, 0xFF01, 0x0010, 65, 65, 0, "MCHOSE G3 A", "YJX-CHIP");
}
