namespace BatteryManager.WindowsNative;

public enum WindowsQueryKind { BluetoothClassic, BluetoothLe, DeviceInterface, PnpDevice }

public sealed record RawWindowsDevice(
    string Id,
    string Name,
    IReadOnlyDictionary<string, object?> Properties);

public sealed record RawPnpProperty(int LocateResult, int PropertyResult, uint PropertyType, byte[] Bytes);

// Tasks complete only after the actual OS operation has reached its terminal state.
// Cancellation is handled before dispatch; in-flight calls remain owned until completion.
public interface IWindowsNativeIo
{
    Task<IReadOnlyList<RawWindowsDevice>> EnumerateAsync(WindowsQueryKind kind);
    Task<RawPnpProperty> ReadBatteryPropertyAsync(string instanceId);
}

public static class WindowsPropertyNames
{
    public const string BatteryLife = "System.Devices.BatteryLife";
    public const string BatteryPlusCharging = "System.Devices.BatteryPlusCharging";
    public const string PnpBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    public const string InstanceId = "System.Devices.DeviceInstanceId";
    public const string ContainerId = "System.Devices.ContainerId";
    public const string Address = "System.Devices.Aep.DeviceAddress";
}
