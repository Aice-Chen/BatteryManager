using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace BatteryManager.App;

// Used only by the scenario + test-pipe commands. Captures this app's own HWND,
// never the desktop or another process. PrintWindow success does not prove that
// a compositor supplied all content; the PNG must still be visually inspected.
internal static class VisualCaptureDiagnostics
{
    internal static async Task CapturePopupAsync(Window main, Window panel, string directory, string name)
    {
        var mainHandle = WinRT.Interop.WindowNative.GetWindowHandle(main);
        var panelHandle = WinRT.Interop.WindowNative.GetWindowHandle(panel);
        var candidates = new List<nint>();
        EnumWindowsCallback callback = (handle, _) =>
        {
            if (handle != mainHandle && handle != panelHandle && IsWindowVisible(handle)
                && NativeMethods.GetWindowThreadProcessId(handle, out var process) != 0 && process == Environment.ProcessId)
                candidates.Add(handle);
            return true;
        };
        if (!EnumWindows(callback, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (candidates.Count != 1) throw new InvalidOperationException($"Expected exactly one visible popup owned by this process, found {candidates.Count}.");
        var capture = await Task.Run(() => Capture(candidates[0]));
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                (uint)capture.Width, (uint)capture.Height, capture.Dpi, capture.Dpi, capture.Pixels);
            await encoder.FlushAsync();
        }
        await File.WriteAllTextAsync(Path.Combine(directory, Path.ChangeExtension(name, ".json")),
            JsonSerializer.Serialize(new
            {
                kind = "PrintWindow(PW_RENDERFULLCONTENT), unique current-process popup, physical pixels, PNG without resizing",
                warning = "Check actual popup content; this excludes the main window, tray panel, desktop and other processes.",
                capture.Width, capture.Height, capture.Dpi, capture.ClientX, capture.ClientY
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
    internal static async Task CaptureWindowAsync(Window window, FrameworkElement root, ScrollViewer scroll, string directory, string name)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var before = ScrollState(scroll);
        var capture = await Task.Run(() => Capture(hwnd));
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                (uint)capture.Width, (uint)capture.Height, capture.Dpi, capture.Dpi, capture.Pixels);
            await encoder.FlushAsync();
        }
        var report = new
        {
            kind = "PrintWindow(PW_RENDERFULLCONTENT), per-monitor-DPI-aware physical pixels, PNG without resizing",
            warning = "Check actual image content: a successful PrintWindow may omit composited surfaces.",
            capture.Width, capture.Height, capture.Dpi, capture.ClientX, capture.ClientY,
            rasterizationScale = root.XamlRoot.RasterizationScale,
            rootWidth = root.ActualWidth, rootHeight = root.ActualHeight,
            scrollBefore = before, scrollAfter = ScrollState(scroll)
        };
        await File.WriteAllTextAsync(Path.Combine(directory, Path.ChangeExtension(name, ".json")),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static object ScrollState(ScrollViewer scroll)
    {
        var scale = scroll.XamlRoot.RasterizationScale;
        var bounds = scroll.TransformToVisual(scroll.XamlRoot.Content).TransformBounds(
            new Windows.Foundation.Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight));
        return new
        {
            scroll.VerticalOffset, scroll.ScrollableHeight, scroll.ExtentHeight, scroll.ViewportHeight,
            scroll.HorizontalOffset, scroll.ExtentWidth, scroll.ViewportWidth,
            scale, rootLeft = bounds.Left, rootTop = bounds.Top,
            rootPixelLeft = bounds.Left * scale, rootPixelTop = bounds.Top * scale
        };
    }

    private sealed record Frame(int Width, int Height, uint Dpi, int ClientX, int ClientY, byte[] Pixels);
    private static Frame Capture(nint hwnd)
    {
        var previousDpi = SetThreadDpiAwarenessContext(new nint(-4));
        if (previousDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint screen = 0, dc = 0, bitmap = 0, previousBitmap = 0;
        try
        {
            if (NativeMethods.GetWindowThreadProcessId(hwnd, out var process) == 0 || process != Environment.ProcessId)
                throw new InvalidOperationException("Only this scenario application's window can be captured.");
            if (!GetWindowRect(hwnd, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width <= 0 || height <= 0 || (long)width * height > 64_000_000) throw new InvalidOperationException("Invalid window bounds.");
            var client = new NativeMethods.Point();
            if (!ClientToScreen(hwnd, ref client)) throw new Win32Exception(Marshal.GetLastWin32Error());
            screen = NativeMethods.GetDC(0);
            if (screen == 0) throw new Win32Exception();
            dc = NativeMethods.CreateCompatibleDC(screen);
            if (dc == 0) throw new Win32Exception();
            var info = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(), Width = width, Height = -height,
                    Planes = 1, BitCount = 32, SizeImage = checked((uint)(width * height * 4))
                }
            };
            bitmap = NativeMethods.CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new Win32Exception();
            previousBitmap = NativeMethods.SelectObject(dc, bitmap);
            if (previousBitmap == 0 || previousBitmap == new nint(-1)) throw new Win32Exception();
            if (!PrintWindow(hwnd, dc, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.GdiFlush();
            var bytes = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            return new(width, height, NativeMethods.GetDpiForWindow(hwnd), client.X - bounds.Left, client.Y - bounds.Top, bytes);
        }
        finally
        {
            if (previousBitmap != 0 && previousBitmap != new nint(-1) && dc != 0) NativeMethods.SelectObject(dc, previousBitmap);
            if (bitmap != 0) NativeMethods.DeleteObject(bitmap);
            if (dc != 0) NativeMethods.DeleteDC(dc);
            if (screen != 0) NativeMethods.ReleaseDC(0, screen);
            SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetThreadDpiAwarenessContext(nint context);
    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeMethods.Rect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref NativeMethods.Point point);
}
