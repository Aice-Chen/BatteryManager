namespace BatteryManager.WindowsNative;

public sealed record HidInterface(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    ushort InputReportLength,
    ushort OutputReportLength,
    ushort FeatureReportLength,
    string? ProductName = null,
    string? Manufacturer = null,
    string? ContainerId = null,
    string? SerialNumber = null);

public sealed record HidInventory(
    IReadOnlyList<HidInterface> Interfaces,
    bool Complete,
    IReadOnlyList<string> Errors);

// Completion means the physical operation is finished; cancellation must not detach native work.
public interface IWindowsHidIo
{
    Task<HidInventory> EnumerateAsync(CancellationToken cancellationToken);
    Task<IHidChannel> OpenAsync(HidInterface device, CancellationToken cancellationToken);
}

// A channel exclusively owns its handles until all operations have ended and it is disposed.
public interface IHidChannel : IAsyncDisposable
{
    // Control-transfer output, retained for receivers that require HidD_SetOutputReport.
    Task WriteOutputReportAsync(byte[] report, CancellationToken cancellationToken);
    // Explicit interrupt-transfer output; unsupported implementations must not silently fall back.
    Task WriteInterruptReportAsync(byte[] report, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("This HID channel does not support interrupt output reports."));
    Task<byte[]> ReadInputReportAsync(CancellationToken cancellationToken);
}
