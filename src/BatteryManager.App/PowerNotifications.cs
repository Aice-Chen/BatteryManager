using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BatteryManager.App;

internal sealed record AppTestPower(bool Registered, string? LastEvent, DateTimeOffset? ReceivedAt, bool Simulated, long Events);

// The window callback only closes logical work / schedules recovery. It never waits
// for native reads to drain while Windows is trying to suspend the process.
internal sealed class PowerNotifications : IDisposable
{
    private const uint PowerBroadcast = 0x0218;
    private const uint Suspend = 4;
    private const uint ResumeAutomatic = 0x12;
    private const uint ResumeInteractive = 7;
    private readonly nint _window;
    private readonly Action<bool> _powerChanged;
    private readonly Action<Exception, string> _report;
    private readonly NativeMethods.SubclassProc _procedure;
    private nint _registration;
    private bool _disposed;
    private string? _lastEvent;
    private DateTimeOffset? _receivedAt;
    private bool _simulated;
    private long _events;

    public PowerNotifications(nint window, Action<bool> powerChanged, Action<Exception, string> report)
    {
        _window = window;
        _powerChanged = powerChanged;
        _report = report;
        _procedure = WindowProcedure;
        if (!NativeMethods.SetWindowSubclass(window, _procedure, 42, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the power notification handler.");
        _registration = RegisterSuspendResumeNotification(window, 0); // DEVICE_NOTIFY_WINDOW_HANDLE
        if (_registration == 0)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error(), "Could not register suspend/resume notifications.");
            NativeMethods.RemoveWindowSubclass(window, _procedure, 42);
            throw error;
        }
    }

    public AppTestPower Capture() => new(!_disposed && _registration != 0, _lastEvent, _receivedAt, _simulated, _events);

    // Available only through the opt-in app test host. This injects an app event;
    // it does not put Windows to sleep or claim to validate OS delivery.
    public void InjectForTest(string action) => Dispatch(action switch
    {
        "suspend" => Suspend,
        "resume" => ResumeAutomatic,
        "interactive-resume" => ResumeInteractive,
        _ => throw new ArgumentException("Use suspend, resume or interactive-resume.")
    }, simulated: true);

    private nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        if (message == PowerBroadcast && Dispatch((uint)wParam, simulated: false)) return 1;
        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private bool Dispatch(uint value, bool simulated)
    {
        if (_disposed || value is not (Suspend or ResumeAutomatic or ResumeInteractive)) return false;
        _lastEvent = value switch { Suspend => "Suspend", ResumeAutomatic => "ResumeAutomatic", _ => "ResumeInteractive" };
        _receivedAt = DateTimeOffset.UtcNow;
        _simulated = simulated;
        _events++;
        try
        {
            // Windows sends the interactive notification after ResumeAutomatic.
            if (value != ResumeInteractive) _powerChanged(value == Suspend);
        }
        catch (Exception error) { _report(error, "Power notification handling failed"); }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registration != 0 && !UnregisterSuspendResumeNotification(_registration))
            _report(new Win32Exception(Marshal.GetLastWin32Error()), "Power notification unregister failed");
        _registration = 0;
        NativeMethods.RemoveWindowSubclass(_window, _procedure, 42);
        GC.KeepAlive(_procedure);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterSuspendResumeNotification(nint recipient, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterSuspendResumeNotification(nint handle);
}
