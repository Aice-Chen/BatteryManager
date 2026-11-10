using BatteryManager.WindowsNative;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class AtkCompxProtocolTests
{
    [Theory]
    [InlineData("080400000002500010E600000000000001", 80, false, 4326)]
    // Captured from the verified receiver after the user woke the mouse on 2026-11-05.
    [InlineData("08040000000264001118000000000000BA", 100, false, 4376)]
    [InlineData("040000000050000E74000000000000F9", 80, false, 3700)]
    [InlineData("080400000002500110E600000000000001", 80, true, 4326)]
    [InlineData("08040000000250FF10E600000000000001", 80, null, 4326)]
    public void CompleteVerifiedShapePreservesBatteryAndUnknownCharging(string hex, int percent, bool? charging, int millivolts)
    {
        Assert.True(AtkCompxProtocol.TryParseBattery(Convert.FromHexString(hex), out var battery));
        Assert.Equal(percent, battery.Percent);
        Assert.Equal(charging, battery.Charging);
        Assert.Equal(millivolts, battery.Millivolts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("08040000000050")]
    [InlineData("0600006464648244")]
    [InlineData("080400000002FF0010E600000000000001")]
    [InlineData("080500000002500010E600000000000001")]
    [InlineData("090400000002500010E600000000000001")]
    [InlineData("080401000002500010E600000000000001")]
    public void UntrustedReportsAreRejected(string hex) => Assert.False(AtkCompxProtocol.TryParseBattery(Convert.FromHexString(hex), out _));

    [Theory]
    [InlineData("0803000000000100000000000000000000", true)]
    [InlineData("0803000000000000000000000000000000", false)]
    [InlineData("0803000000000200000000000000000000", null)]
    [InlineData("080300000000FF00000000000000000000", null)]
    public void OnlineReportDecodesOnlyEstablishedBodyStatuses(string hex, bool? online)
    {
        Assert.True(AtkCompxProtocol.TryParseOnline(Convert.FromHexString(hex), out var decoded));
        Assert.Equal(online, decoded);
    }

    [Theory]
    [InlineData("0803010000000100000000000000000000")]
    [InlineData("08030000000001")]
    [InlineData("080400000002500010E600000000000001")]
    public void OnlineReportRejectsCommandErrorsShortFramesAndBatteryReplies(string hex) =>
        Assert.False(AtkCompxProtocol.TryParseOnline(Convert.FromHexString(hex), out _));
}
