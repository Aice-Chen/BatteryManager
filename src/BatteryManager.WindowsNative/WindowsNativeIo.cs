using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BatteryManager.WindowsNative;

public sealed class WindowsNativeIo : IWindowsNativeIo
{
    private static readonly string[] InterfaceProperties =
    [
        WindowsPropertyNames.BatteryLife, WindowsPropertyNames.BatteryPlusCharging,
        "System.Devices.Connected", WindowsPropertyNames.ContainerId,
        "System.Devices.Category", "System.Devices.InterfaceClassGuid",
        WindowsPropertyNames.InstanceId, WindowsPropertyNames.Address
    ];
    private static readonly string[] AepProperties =
    [
        "System.Devices.Aep.IsConnected", "System.Devices.Aep.IsPresent",
        WindowsPropertyNames.Address, "System.Devices.Aep.ContainerId",
        "System.Devices.Aep.Category", "System.Devices.Aep.ProtocolId", "System.ItemNameDisplay"
    ];
    private static readonly string[] PnpProperties =
    [
        WindowsPropertyNames.ContainerId, WindowsPropertyNames.InstanceId,
        "System.Devices.Present", "System.Devices.Category", "System.ItemNameDisplay",
        WindowsPropertyNames.Address, WindowsPropertyNames.BatteryLife,
        WindowsPropertyNames.BatteryPlusCharging
    ];

    public async Task<IReadOnlyList<RawWindowsDevice>> EnumerateAsync(WindowsQueryKind kind)
    {
        var (selector, properties, informationKind) = kind switch
        {
            WindowsQueryKind.BluetoothClassic => (BluetoothDevice.GetDeviceSelector(), AepProperties, DeviceInformationKind.AssociationEndpoint),
            WindowsQueryKind.BluetoothLe => (BluetoothLEDevice.GetDeviceSelector(), AepProperties, DeviceInformationKind.AssociationEndpoint),
            WindowsQueryKind.PnpDevice => (string.Empty, PnpProperties, DeviceInformationKind.Device),
            _ => (string.Empty, InterfaceProperties, DeviceInformationKind.DeviceInterface)
        };
        // No cancelable await wrapper: completion is the WinRT operation's completion.
        var devices = await DeviceInformation.FindAllAsync(selector, properties, informationKind).AsTask().ConfigureAwait(false);
        return devices.Select(device =>
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in device.Properties)
                values[property.Key] = property.Value;
            return new RawWindowsDevice(device.Id, device.Name, values);
        }).ToArray();
    }

    public Task<RawPnpProperty> ReadBatteryPropertyAsync(string instanceId) =>
        Task.Run(() => ReadPnpBattery(instanceId));

    private static RawPnpProperty ReadPnpBattery(string instanceId)
    {
        var locate = CM_Locate_DevNodeW(out var node, instanceId, 0);
        if (locate != 0)
            return new(locate, 0, 0, []);

        var key = new DevPropKey { FormatId = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), PropertyId = 2 };
        uint size = 0;
        var result = CM_Get_DevNode_PropertyW(node, ref key, out var type, null, ref size, 0);
        if (result != 0x1A || size == 0)
            return new(0, result, type, []);
        if (size > 65536)
            return new(0, 0x1F, type, []);

        // Device properties can change size between the sizing call and read.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var bytes = new byte[size];
            result = CM_Get_DevNode_PropertyW(node, ref key, out type, bytes, ref size, 0);
            if (result == 0)
            {
                if (size < bytes.Length) Array.Resize(ref bytes, checked((int)size));
                return new(0, result, type, bytes);
            }
            if (result != 0x1A || size > 65536) break;
        }
        return new(0, result, type, []);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey { public Guid FormatId; public uint PropertyId; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_DevNode_PropertyW(uint node, ref DevPropKey key, out uint type,
        [Out] byte[]? buffer, ref uint size, uint flags);
}
