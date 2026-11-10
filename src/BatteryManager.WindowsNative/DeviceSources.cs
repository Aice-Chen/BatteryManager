using BatteryManager.Core;

namespace BatteryManager.WindowsNative;

public static class DeviceSources
{
    // The app and CLI use the same production composition.
    public static IDeviceSource CreateDefault(IWindowsHidIo? hidIo = null, TimeProvider? clock = null) => new CompositeDeviceSource(
        new WindowsNativeSource(clock: clock), new GattBatterySource(clock: clock), new AtkBatterySource(hidIo, clock),
        new LofreeBatterySource(hidIo, clock), new MchoseBatterySource(hidIo, clock), new LogitechK380BatterySource(hidIo, clock));
}
