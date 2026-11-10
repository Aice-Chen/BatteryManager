using BatteryManager.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BatteryManager.WindowsNative;

public sealed class WindowsGattIo : IWindowsGattIo
{
    private readonly WindowsNativeIo native = new();
    private readonly TimeProvider clock;
    public WindowsGattIo(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;
    public Task<IReadOnlyList<RawWindowsDevice>> EnumerateCandidatesAsync() => native.EnumerateAsync(WindowsQueryKind.BluetoothLe);

    public async Task<RawGattBattery> ReadBatteryAsync(RawWindowsDevice candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var device = await BluetoothLEDevice.FromIdAsync(candidate.Id).AsTask().ConfigureAwait(false);
        if (device is null) return new(null, ReadStatus.Error, Error: "The LE device could not be opened.");
        cancellationToken.ThrowIfCancellationRequested();
        var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (services.Status != GattCommunicationStatus.Success)
                return Failure("services", services.Status, services.ProtocolError);
            if (services.Services.Count == 0)
                return new(null, ReadStatus.Unsupported, Error: null);

            RawGattBattery last = new(null, ReadStatus.Missing);
            foreach (var service in services.Services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var characteristics = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
                if (characteristics.Status != GattCommunicationStatus.Success)
                { last = Failure("characteristics", characteristics.Status, characteristics.ProtocolError); continue; }
                foreach (var characteristic in characteristics.Characteristics)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var value = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
                    if (value.Status != GattCommunicationStatus.Success)
                    { last = Failure("value", value.Status, value.ProtocolError); continue; }
                    if (value.Value is null)
                    { last = new(null, ReadStatus.Error, Error: "BAS returned no value buffer."); continue; }
                    using var reader = DataReader.FromBuffer(value.Value);
                    var bytes = new byte[value.Value.Length];
                    reader.ReadBytes(bytes);
                    var completedAt = ClockInstant.Read(clock);
                    return new(bytes, ReadStatus.Success, completedAt.Utc, ReadElapsed: completedAt.Elapsed);
                }
            }
            return last;
        }
        finally
        {
            foreach (var service in services.Services) service.Dispose();
        }
    }

    private static RawGattBattery Failure(string operation, GattCommunicationStatus status, byte? protocolError) =>
        new(null, ReadStatus.Error, Error: $"BAS {operation}: {status}; protocol={protocolError?.ToString("X2") ?? "none"}");
}
