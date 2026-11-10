using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class LofreeCompxProtocolTests
{
    [Fact]
    public void QueriesUseOfficialKeyboardFlagChallengeAndChecksum()
    {
        Assert.Equal("08030000008000000000000000000000CA", Convert.ToHexString(LofreeCompxProtocol.CreateOnlineQuery()));
        Assert.Equal("08040000008000000000000000000000C9", Convert.ToHexString(LofreeCompxProtocol.CreateBatteryQuery()));
        Assert.Equal("08010000008801020304000000000000BA", Convert.ToHexString(LofreeCompxProtocol.CreateIdentityQuery([1, 2, 3, 4])));
    }

    [Fact]
    public void CapturedResponsesDecodeOnlineVerifiedIdentityAndBattery()
    {
        Assert.True(LofreeCompxProtocol.TryParseOnline(Convert.FromHexString("080300000081014139140000000000003A"), out var online));
        Assert.True(online);
        Assert.True(LofreeCompxProtocol.TryParseIdentity(Convert.FromHexString("080100000088AF45679B010200000000CB"), out var identity));
        Assert.Equal(new LofreeIdentity(1, 2, 0), identity);
        Assert.True(LofreeCompxProtocol.IsVerifiedIdentity(identity));
        Assert.True(LofreeCompxProtocol.TryParseBattery(Convert.FromHexString("080400000084550010C0000000000000A0"), out var battery));
        Assert.Equal(new LofreeBatteryReport(85, false, 4288), battery);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, null)]
    [InlineData(255, null)]
    public void UnknownOnlineValuesRemainUnknown(byte value, bool? expected)
    {
        var report = Convert.FromHexString("080300000081014139140000000000003A");
        report[6] = value;
        Assert.True(LofreeCompxProtocol.TryParseOnline(report, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnknownChargingAndOtherKeyboardIdentitiesDoNotGainSupportedMeaning()
    {
        var report = Convert.FromHexString("080400000084550010C0000000000000A0");
        report[7] = 255;
        Assert.True(LofreeCompxProtocol.TryParseBattery(report, out var battery));
        Assert.Null(battery.Charging);
        Assert.False(LofreeCompxProtocol.IsVerifiedIdentity(new(1, 1, 0)));
        Assert.False(LofreeCompxProtocol.IsVerifiedIdentity(new(1, 2, 2)));
        Assert.False(LofreeCompxProtocol.IsVerifiedIdentity(new(87, 4, 0)));
    }

    [Theory]
    [InlineData("08040000008455")]
    [InlineData("080401000084550010C0000000000000A0")]
    [InlineData("080400000004550010C0000000000000A0")]
    [InlineData("080300000084550010C0000000000000A0")]
    [InlineData("090400000084550010C0000000000000A0")]
    [InlineData("080400000084FF0010C0000000000000A0")]
    [InlineData("080400000084550010C0000000000000A000")]
    [InlineData("080400000083550010C0000000000000A0")]
    [InlineData("0804000000FF550010C0000000000000A0")]
    public void BatteryRejectsShortErrorWrongFamilyWrongCommandAndInvalidValueFrames(string hex) =>
        Assert.False(LofreeCompxProtocol.TryParseBattery(Convert.FromHexString(hex), out _));

    [Theory]
    [InlineData("080301000081014139140000000000003A")]
    [InlineData("080300000001014139140000000000003A")]
    [InlineData("08030000008101")]
    [InlineData("080300000080014139140000000000003A")]
    public void OnlineRejectsErrorWrongFamilyAndShortFrames(string hex) =>
        Assert.False(LofreeCompxProtocol.TryParseOnline(Convert.FromHexString(hex), out _));

    [Theory]
    [InlineData("080101000088AF45679B010200000000CB")]
    [InlineData("080100000008AF45679B010200000000CB")]
    [InlineData("080100000088AF45679B0102")]
    [InlineData("080100000086AF45679B010200000000CB")]
    public void IdentityRejectsErrorWrongFamilyAndShortFrames(string hex) =>
        Assert.False(LofreeCompxProtocol.TryParseIdentity(Convert.FromHexString(hex), out _));
}
