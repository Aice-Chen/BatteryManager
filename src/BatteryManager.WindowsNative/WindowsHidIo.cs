using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BatteryManager.WindowsNative;

public sealed class WindowsHidIo : IWindowsHidIo
{
    private static readonly object PathsGate = new();
    private static readonly HashSet<string> OpenPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IntPtr InvalidHandle = new(-1);
    private static readonly PropertyKey ContainerKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private const int HidSuccess = 0x00110000;

    public Task<HidInventory> EnumerateAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Enumerate(cancellationToken), CancellationToken.None);

    public async Task<IHidChannel> OpenAsync(HidInterface device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Path);
        if (device.InputReportLength == 0) throw new ArgumentException("The HID collection has no input reports.", nameof(device));
        cancellationToken.ThrowIfCancellationRequested();
        lock (PathsGate)
            if (!OpenPaths.Add(device.Path))
                throw new IOException("This HID collection already has an active channel in this process.");
        try
        {
            // CreateFile and HID control calls are synchronous. The task remains owned until they return.
            return await Task.Run<IHidChannel>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SafeFileHandle? inputHandle = null;
                SafeFileHandle? outputHandle = null;
                FileStream? input = null;
                try
                {
                    inputHandle = OpenHandle(device.Path, 0xC0000000, 0x40000080);
                    CheckHandle(inputHandle, "Opening the HID input channel");
                    VerifyCollection(inputHandle, device);
                    // A one-byte buffer disables FileStream buffering: each read is one HID report.
                    input = new FileStream(inputHandle, FileAccess.ReadWrite, 1, isAsync: true);
                    inputHandle = null;
                    cancellationToken.ThrowIfCancellationRequested();
                    outputHandle = OpenHandle(device.Path, 0xC0000000, 0x80);
                    CheckHandle(outputHandle, "Opening the HID output channel");
                    VerifyCollection(outputHandle, device);
                    cancellationToken.ThrowIfCancellationRequested();
                    var channel = new Channel(device, input, outputHandle);
                    input = null;
                    outputHandle = null;
                    return channel;
                }
                finally
                {
                    input?.Dispose();
                    inputHandle?.Dispose();
                    outputHandle?.Dispose();
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            ReleasePath(device.Path);
            throw;
        }
    }

    private static HidInventory Enumerate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        if (set == InvalidHandle) return new([], false, [LastError("Enumerating HID devices").Message]);
        var devices = new List<HidInterface>();
        var errors = new List<string>();
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 259) errors.Add(NativeError("Enumerating HID interfaces", error).Message);
                    break;
                }
                try
                {
                    var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                    var path = ReadPath(set, ref data, ref info);
                    cancellationToken.ThrowIfCancellationRequested();
                    // Metadata does not require opening keyboard/mouse input streams.
                    using var handle = OpenHandle(path, 0, 0x80);
                    CheckHandle(handle, "Opening HID metadata");
                    var attributes = ReadAttributes(handle);
                    var caps = ReadCaps(handle);
                    devices.Add(new(path, attributes.Vendor, attributes.Product, caps.UsagePage, caps.Usage,
                        caps.InputLength, caps.OutputLength, caps.FeatureLength,
                        ReadString(handle, HidD_GetProductString), ReadString(handle, HidD_GetManufacturerString),
                        ReadContainer(set, ref info), ReadString(handle, HidD_GetSerialNumberString)));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is Win32Exception or IOException)
                {
                    errors.Add($"HID interface {index}: {error.Message}");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(devices, errors.Count == 0, errors);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static string ReadPath(IntPtr set, ref InterfaceData data, ref DeviceInfo info)
    {
        var sized = SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, ref info);
        var error = Marshal.GetLastPInvokeError();
        if (!sized && error != 122) throw NativeError("Sizing HID interface detail", error);
        if (required is < 6 or > 1024 * 1024) throw new InvalidDataException("Invalid HID interface detail size.");
        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required, out var returned, ref info))
                throw LastError("Reading HID interface detail");
            if (returned < 6 || returned > required) throw new InvalidDataException("Invalid HID interface detail length.");
            // DevicePath starts after the DWORD even though cbSize is 8 on x64.
            var text = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4), (returned - 4) / 2) ?? "";
            var zero = text.IndexOf('\0');
            if (zero < 0) throw new InvalidDataException("HID interface path is not terminated.");
            var path = text[..zero];
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("HID interface path is empty.");
            return path;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? ReadContainer(IntPtr set, ref DeviceInfo info)
    {
        var key = ContainerKey;
        var bytes = new byte[16];
        return SetupDiGetDevicePropertyW(set, ref info, ref key, out var type, bytes, bytes.Length, out var length, 0) &&
            type == 0x0D && length == 16 ? new Guid(bytes).ToString("D") : null;
    }

    private static Attributes ReadAttributes(SafeFileHandle handle)
    {
        var result = new Attributes { Size = Marshal.SizeOf<Attributes>() };
        if (!HidD_GetAttributes(handle, ref result)) throw LastError("Reading HID attributes");
        return result;
    }

    private static Caps ReadCaps(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var data)) throw LastError("Reading HID descriptor");
        try
        {
            var status = HidP_GetCaps(data, out var caps);
            if (status != HidSuccess) throw new IOException($"Reading HID capabilities failed with HID status 0x{status:X8}.");
            return caps;
        }
        finally { HidD_FreePreparsedData(data); }
    }

    private static void VerifyCollection(SafeFileHandle handle, HidInterface device)
    {
        var attributes = ReadAttributes(handle);
        var caps = ReadCaps(handle);
        if (attributes.Vendor != device.VendorId || attributes.Product != device.ProductId ||
            caps.UsagePage != device.UsagePage || caps.Usage != device.Usage ||
            caps.InputLength != device.InputReportLength || caps.OutputLength != device.OutputReportLength)
            throw new IOException("The HID collection changed after enumeration.");
    }

    private static string? ReadString(SafeFileHandle handle, Func<SafeFileHandle, byte[], int, bool> read)
    {
        var buffer = new byte[256];
        if (!read(handle, buffer, buffer.Length)) return null;
        var value = Encoding.Unicode.GetString(buffer).Split('\0', 2)[0].Trim();
        return value.Length == 0 ? null : value;
    }

    private static SafeFileHandle OpenHandle(string path, uint access, uint flags) =>
        CreateFileW(path, access, 3, IntPtr.Zero, 3, flags, IntPtr.Zero);

    private static void CheckHandle(SafeFileHandle handle, string operation)
    {
        if (handle.IsInvalid) throw LastError(operation);
    }

    private static Win32Exception LastError(string operation) => NativeError(operation, Marshal.GetLastPInvokeError());
    private static Win32Exception NativeError(string operation, int code) => new(code, $"{operation} failed with Win32 error {code}.");
    private static void ReleasePath(string path) { lock (PathsGate) OpenPaths.Remove(path); }

    private sealed class Channel(HidInterface device, FileStream input, SafeFileHandle output) : IHidChannel
    {
        private readonly object gate = new();
        private readonly SemaphoreSlim operationGate = new(1, 1);
        private readonly CancellationTokenSource lifetime = new();
        private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool closing;
        private int operations;
        private Task? disposal;

        public Task WriteOutputReportAsync(byte[] report, CancellationToken cancellationToken)
        {
            var copy = CopyOutputReport(report);
            return ExecuteAsync(async token =>
            {
                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    // This synchronous control call cannot be abandoned on logical cancellation.
                    if (!HidD_SetOutputReport(output, copy, copy.Length)) throw LastError("Writing HID output report");
                }, CancellationToken.None).ConfigureAwait(false);
                return true;
            }, cancellationToken);
        }

        public Task WriteInterruptReportAsync(byte[] report, CancellationToken cancellationToken)
        {
            var copy = CopyOutputReport(report);
            return ExecuteAsync(async token =>
            {
                // The unbuffered overlapped handle completes only after Windows finishes the write.
                await input.WriteAsync(copy.AsMemory(), token).ConfigureAwait(false);
                return true;
            }, cancellationToken);
        }

        private byte[] CopyOutputReport(byte[] report)
        {
            ArgumentNullException.ThrowIfNull(report);
            if (device.OutputReportLength == 0 || report.Length != device.OutputReportLength)
                throw new ArgumentException("The output report must have the collection's declared byte length.", nameof(report));
            return (byte[])report.Clone();
        }

        public Task<byte[]> ReadInputReportAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
        {
            var bytes = new byte[device.InputReportLength];
            var count = await input.ReadAsync(bytes, token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The HID input channel ended without a report.");
            return count == bytes.Length ? bytes : bytes[..count];
        }, cancellationToken);

        private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
                operations++;
            }
            var admitted = false;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                await operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
                admitted = true;
                linked.Token.ThrowIfCancellationRequested();
                return await operation(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                if (admitted) operationGate.Release();
                lock (gate)
                {
                    operations--;
                    if (closing && operations == 0) drained.TrySetResult();
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (disposal is not null) return new(disposal);
                closing = true;
                if (operations == 0) drained.TrySetResult();
                disposal = DisposeCoreAsync();
                return new(disposal);
            }
        }

        private async Task DisposeCoreAsync()
        {
            Exception? cancellationError = null;
            try { await lifetime.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { cancellationError = error; }
            // In particular, do not release the path lease while a native output call is still running.
            await drained.Task.ConfigureAwait(false);
            try { await input.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                output.Dispose();
                lifetime.Dispose();
                operationGate.Dispose();
                ReleasePath(device.Path);
            }
            if (cancellationError is not null) throw cancellationError;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public int Size; public Guid Class; public uint Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public int Size; public Guid Class; public uint DeviceInstance; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes { public int Size; public ushort Vendor; public ushort Product; public ushort Version; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Caps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputLength;
        public ushort OutputLength;
        public ushort FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkCollectionNodes;
        public ushort InputButtonCaps;
        public ushort InputValueCaps;
        public ushort InputDataIndices;
        public ushort OutputButtonCaps;
        public ushort OutputValueCaps;
        public ushort OutputDataIndices;
        public ushort FeatureButtonCaps;
        public ushort FeatureValueCaps;
        public ushort FeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int required, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DeviceInfo info, ref PropertyKey key, out uint type, byte[] buffer, int size, out int required, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetManufacturerString(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_SetOutputReport(SafeFileHandle handle, byte[] buffer, int length);
}
