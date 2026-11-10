using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BatteryManager.App;

/// <summary>The app icon: a graphite tile carrying the in-app battery glyph. Assets\AppIcon.ico is the exe icon
/// (&lt;ApplicationIcon&gt;); Windows shows it for the exe file, the taskbar and Alt+Tab. Every frame below 48 px is
/// drawn on whole pixels at its own size (tools/app-icon); supported scale steps display those frames 1:1.</summary>
internal static class AppIcon
{
    /// <summary>Embedded PNG frames for a 16 DIP icon at 100%–300% scale.</summary>
    public static readonly int[] Frames = [16, 20, 24, 28, 32, 36, 40, 48];

    // The compiler stores <ApplicationIcon> as icon group 32512, and the build copies it into the apphost exe.
    private const int IconGroup = 32512;

    /// <summary>The frame drawn for <paramref name="pixels"/>, or the next larger one at a scale that has none.</summary>
    public static int FrameFor(double pixels)
    {
        foreach (var frame in Frames) if (frame >= pixels - 0.01) return frame;
        return Frames[^1];
    }

    public static BitmapImage Frame(int pixels)
    {
        using var resource = typeof(AppIcon).Assembly.GetManifestResourceStream($"BatteryManager.App.AppIcon.{pixels}.png")
            ?? throw new InvalidOperationException($"The {pixels} px app icon frame is not embedded.");
        // SetSource completes its stream read synchronously; release both the adapter and backing copy afterwards.
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        bytes.Position = 0;
        using var stream = bytes.AsRandomAccessStream();
        var image = new BitmapImage();
        image.SetSource(stream);
        return image;
    }

    /// <summary>A decorative app icon <paramref name="size"/> DIP square. It shows the frame drawn for the current
    /// scale and switches frames when the window moves to a monitor with another scale.</summary>
    public static Image CreateImage(double size = 16)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
        var shown = 0;
        XamlRoot? root = null;
        void Refresh()
        {
            var frame = FrameFor(size * (image.XamlRoot?.RasterizationScale ?? 1));
            if (frame == shown) return;
            image.Source = Frame(frame);
            shown = frame;
        }
        void Changed(XamlRoot sender, XamlRootChangedEventArgs args) => Refresh();
        image.Loaded += (_, _) =>
        {
            if (root is not null) root.Changed -= Changed;
            root = image.XamlRoot;
            if (root is not null) root.Changed += Changed;
            Refresh();
        };
        image.Unloaded += (_, _) =>
        {
            if (root is not null) root.Changed -= Changed;
            root = null;
        };
        return image;
    }

    /// <summary>Gives a window its big and small icons at the sizes Windows uses on the window's monitor. Both come
    /// from the exe's own icon resource, so each is that size's hand-drawn frame and the shell can load other frames
    /// from the same resource. Call again after a DPI change; nothing happens while the DPI is unchanged.</summary>
    internal sealed class WindowIcons : IDisposable
    {
        private nint _big, _small;
        private uint _dpi;
        private bool _disposed;

        public void Apply(nint window)
        {
            if (_disposed) return;
            var dpi = GetDpiForWindow(window);
            if (dpi == 0 || dpi == _dpi) return;
            var module = GetModuleHandleW(null);
            var big = LoadImageW(module, IconGroup, ImageIcon, GetSystemMetricsForDpi(SmCxIcon, dpi), GetSystemMetricsForDpi(SmCyIcon, dpi), 0);
            var small = LoadImageW(module, IconGroup, ImageIcon, GetSystemMetricsForDpi(SmCxSmIcon, dpi), GetSystemMetricsForDpi(SmCySmIcon, dpi), 0);
            if (big == 0 || small == 0)
            {
                // No icon resource (for example a build without the .ico): leave the window as Windows shows it.
                Destroy(big); Destroy(small);
                return;
            }
            _dpi = dpi;
            SendMessageW(window, WmSetIcon, IconBig, big);
            SendMessageW(window, WmSetIcon, IconSmall, small);
            Destroy(_big); Destroy(_small);
            (_big, _small) = (big, small);
        }

        /// <summary>The DPI the current icons were loaded for (0 before the first success), for diagnostics.</summary>
        public uint Dpi => _dpi;

        // Call after the owning window closes, when it no longer uses the icon handles.
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Destroy(_big); Destroy(_small);
            (_big, _small) = (0, 0);
            _dpi = 0;
        }

        private static void Destroy(nint icon)
        {
            if (icon != 0) DestroyIcon(icon);
        }
    }

    private const uint ImageIcon = 1, WmSetIcon = 0x0080;
    private const int SmCxIcon = 11, SmCyIcon = 12, SmCxSmIcon = 49, SmCySmIcon = 50;
    private const nuint IconSmall = 0, IconBig = 1;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint LoadImageW(nint instance, nint name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
}
