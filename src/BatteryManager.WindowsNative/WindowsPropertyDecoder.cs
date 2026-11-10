using System.Buffers.Binary;
using BatteryManager.Core;

namespace BatteryManager.WindowsNative;

public sealed record DecodedBattery(BatterySample? Sample, ReadStatus Status, string? Error = null, string? NativeCode = null);

public static class WindowsPropertyDecoder
{
    public static DecodedBattery DecodePnp(RawPnpProperty raw)
    {
        if (raw.LocateResult != 0)
            return new(null, raw.LocateResult == 0x0D ? ReadStatus.Missing : ReadStatus.Error,
                raw.LocateResult == 0x0D ? null : $"CM_Locate_DevNodeW: CR_0x{raw.LocateResult:X8}", $"CR_0x{raw.LocateResult:X8}");
        if (raw.PropertyResult != 0)
            return new(null, raw.PropertyResult == 0x25 ? ReadStatus.Missing : ReadStatus.Error,
                raw.PropertyResult == 0x25 ? null : $"CM_Get_DevNode_PropertyW: CR_0x{raw.PropertyResult:X8}", $"CR_0x{raw.PropertyResult:X8}");

        int? value = raw.PropertyType switch
        {
            0x03 when raw.Bytes.Length == 1 => raw.Bytes[0],
            0x06 when raw.Bytes.Length == 4 => BinaryPrimitives.ReadInt32LittleEndian(raw.Bytes),
            0x07 when raw.Bytes.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(raw.Bytes) <= int.MaxValue =>
                (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.Bytes),
            _ => null
        };
        if (value is null)
            return new(null, ReadStatus.Error, $"Unsupported DEVPROP type/length: 0x{raw.PropertyType:X8}/{raw.Bytes.Length}");
        return DecodeValue(value.Value, WindowsPropertyNames.PnpBattery);
    }

    public static DecodedBattery DecodeNamed(IReadOnlyDictionary<string, object?> properties)
    {
        var first = ReadNamed(properties, WindowsPropertyNames.BatteryLife);
        if (first.Sample is not null) return first;
        var second = ReadNamed(properties, WindowsPropertyNames.BatteryPlusCharging);
        if (second.Sample is not null) return second;
        return first.Status == ReadStatus.Error ? first : second;
    }

    private static DecodedBattery ReadNamed(IReadOnlyDictionary<string, object?> properties, string key)
    {
        if (!properties.TryGetValue(key, out var raw) || raw is null)
            return new(null, ReadStatus.Missing);
        long? value = raw switch
        {
            byte v => v, sbyte v => v, short v => v, ushort v => v,
            int v => v, uint v => v, long v => v,
            ulong v when v <= long.MaxValue => (long)v,
            _ => null
        };
        if (value is null || value is < int.MinValue or > int.MaxValue)
            return new(null, ReadStatus.Error, $"{key}: unsupported property type or integer range");
        return DecodeValue((int)value.Value, key);
    }

    private static DecodedBattery DecodeValue(int value, string key)
    {
        var combined = key == WindowsPropertyNames.BatteryPlusCharging;
        if (value < 0 || value > (combined ? 200 : 100))
            return new(null, ReadStatus.Error, $"{key}: value outside supported range");
        bool? charging = combined ? value > 100 : null;
        return new(new(value > 100 ? value - 100 : value, BatteryEvidence.SystemCache,
            Property: key, Charging: charging), ReadStatus.Success);
    }
}
