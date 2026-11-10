using System.Globalization;
using BatteryManager.Core;
using Xunit;

namespace BatteryManager.Core.Tests;

public sealed class DeviceDisplayOrderTests
{
    private static DeviceDisplay Device(string id, string name, DisplayDeviceKind kind, DisplayDeviceKind? detected = null,
        int? battery = 50) => new(
        id, name, name, false, kind, detected ?? kind, detected is not null && detected != kind,
        DisplayPresence.Online, DeviceTransport.Unknown,
        new BatteryDisplay(battery, battery is null ? BatteryTone.None : BatteryTone.Normal, false, battery is <= 20),
        false, null, Array.Empty<SourceDisplay>(), Array.Empty<PartDisplay>(), false, battery is not null);

    private static string[] Ids(IEnumerable<DeviceDisplay> devices) => devices.Select(device => device.Id).ToArray();

    [Fact]
    public void Type_group_comes_before_name()
    {
        var sorted = DeviceDisplayOrder.Sort(new[]
        {
            Device("h", "A headphones", DisplayDeviceKind.Headphones),
            Device("k", "Z keyboard", DisplayDeviceKind.Keyboard),
            Device("m", "M mouse", DisplayDeviceKind.Mouse)
        }, CultureInfo.InvariantCulture);
        Assert.Equal(new[] { "m", "k", "h" }, Ids(sorted));
    }

    [Fact]
    public void Same_type_sorts_by_shown_name_then_identity()
    {
        var sorted = DeviceDisplayOrder.Sort(new[]
        {
            Device("2", "beta", DisplayDeviceKind.Mouse),
            Device("3", "Alpha", DisplayDeviceKind.Mouse),
            Device("1", "beta", DisplayDeviceKind.Mouse)
        }, CultureInfo.InvariantCulture);
        Assert.Equal(new[] { "3", "1", "2" }, Ids(sorted));
    }

    [Fact]
    public void Unrecognized_and_other_devices_come_last()
    {
        var sorted = DeviceDisplayOrder.Sort(new[]
        {
            Device("u", "A", DisplayDeviceKind.Unknown),
            Device("o", "A", DisplayDeviceKind.Other),
            Device("s", "Z", DisplayDeviceKind.Speaker)
        }, CultureInfo.InvariantCulture);
        Assert.Equal(new[] { "s", "o", "u" }, Ids(sorted));
    }

    [Fact]
    public void User_chosen_type_decides_the_group()
    {
        var sorted = DeviceDisplayOrder.Sort(new[]
        {
            Device("k", "Keyboard", DisplayDeviceKind.Keyboard),
            Device("g", "Pad", DisplayDeviceKind.Mouse, detected: DisplayDeviceKind.Unknown)
        }, CultureInfo.InvariantCulture);
        Assert.Equal(new[] { "g", "k" }, Ids(sorted));
    }

    [Fact]
    public void Battery_values_and_input_order_do_not_change_the_result()
    {
        var a = Device("a", "Mouse", DisplayDeviceKind.Mouse, battery: 5);
        var b = Device("b", "Keyboard", DisplayDeviceKind.Keyboard, battery: null);
        var c = Device("c", "Headphones", DisplayDeviceKind.Headphones, battery: 100);
        var first = Ids(DeviceDisplayOrder.Sort(new[] { c, b, a }, CultureInfo.InvariantCulture));
        var second = Ids(DeviceDisplayOrder.Sort(new[]
        {
            b with { Battery = new BatteryDisplay(1, BatteryTone.Stale, false, true) },
            a with { Battery = new BatteryDisplay(100, BatteryTone.Normal, true, false) },
            c with { Battery = new BatteryDisplay(null, BatteryTone.None, false, false) }
        }, CultureInfo.InvariantCulture));
        Assert.Equal(new[] { "a", "b", "c" }, first);
        Assert.Equal(first, second);
    }
}
