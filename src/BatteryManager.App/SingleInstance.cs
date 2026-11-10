using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BatteryManager.App;

internal sealed class SingleInstance : IDisposable
{
    private const string WindowMarker = "BatteryManager.NewApp.PrimaryWindow.2026";
    private const nuint SubclassId = 42;
    private readonly Mutex _mutex;
    private readonly NativeMethods.SubclassProc _procedure;
    private nint _window;
    private bool _disposed;
    public bool IsPrimary { get; }
    public Action? ExitRequested { get; set; }
    public static uint ActivateMessage { get; } = NativeMethods.RegisterWindowMessage("BatteryManager.NewApp.Activate.2026");
    private static uint QuitMessage { get; } = NativeMethods.RegisterWindowMessage("BatteryManager.NewApp.Quit.2026");

    public SingleInstance()
    {
        _procedure = WindowProcedure;
        _mutex = new Mutex(true, @"Local\BatteryManager.NewApp.2026", out bool created);
        IsPrimary = created;
    }

    public void Attach(nint window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary || window == 0 || _window != 0)
            throw new InvalidOperationException("Only the primary instance can attach one activation window.");
        if (!SetProp(window, WindowMarker, Environment.ProcessId))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not mark the application window.");
        if (!NativeMethods.SetWindowSubclass(window, _procedure, SubclassId, 0))
        {
            var error = Marshal.GetLastWin32Error();
            RemoveProp(window, WindowMarker);
            throw new Win32Exception(error, "Could not attach window-marker cleanup.");
        }
        _window = window;
    }

    public static async Task ActivateExistingAsync()
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            nint window = FindPrimaryWindow();
            if (window != 0)
            {
                NativeMethods.GetWindowThreadProcessId(window, out uint processId);
                NativeMethods.AllowSetForegroundWindow(processId);
                if (NativeMethods.PostMessage(window, ActivateMessage, 0, 0)) return;
            }
            await Task.Delay(100);
        }
    }

    public static async Task<bool> RequestQuitAsync(uint processId)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            nint window = FindPrimaryWindow(processId);
            if (window != 0 && NativeMethods.PostMessage(window, QuitMessage, processId, 0)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window != 0)
        {
            RemoveProp(_window, WindowMarker);
            NativeMethods.RemoveWindowSubclass(_window, _procedure, SubclassId);
            _window = 0;
        }
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        GC.KeepAlive(_procedure);
    }

    private static nint FindPrimaryWindow(uint? expectedProcessId = null)
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            nint marker = GetProp(window, WindowMarker);
            if (marker == 0) return true;
            NativeMethods.GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0 || marker != (nint)processId) return true;
            if (expectedProcessId is { } expected && processId != expected) return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        if (message == QuitMessage && wParam == (nuint)Environment.ProcessId)
        {
            // The owner queues the same drain-and-quit path as the tray command.
            ExitRequested?.Invoke();
            return 0;
        }
        if (message == 0x0082) // WM_NCDESTROY: remove our property before the window is destroyed.
        {
            RemoveProp(window, WindowMarker);
            NativeMethods.RemoveWindowSubclass(window, _procedure, SubclassId);
            _window = 0;
        }
        return NativeMethods.DefSubclassProc(window, message, wParam, lParam);
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(nint window, string key, nint value);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string key);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)]
    private static extern nint RemoveProp(nint window, string key);
}
