using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using BatteryManager.Core;

namespace BatteryManager.App;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 41;
    private const uint IconId = 1;
    private const uint OpenCommand = 1001;
    private const uint RefreshCommand = 1002;
    private const uint ExitCommand = 1003;
    private const uint PreferencesCommand = 1004;
    private readonly nint _window;
    private readonly Action _open;
    private readonly Action _refresh;
    private readonly Action _exit;
    private readonly Action _preferences;
    private readonly string _applicationName;
    private readonly NativeMethods.SubclassProc _procedure;
    private readonly uint _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private nint _icon;
    private bool _registered;
    private bool _disposed;
    private string? _selectedDeviceId;
    private int? _lastPercent;
    private string _lastTip = "";
    private SessionSnapshot? _snapshot;
    private IReadOnlyList<DeviceDisplay> _devices = [];
    private BatteryTone _tone = BatteryTone.None;
    private uint _color = 0xFFFFFF;
    private bool _dark;
    private int _iconSize;
    private TrayGlyph _glyph;
    public Action<bool>? TogglePanel { get; set; }
    public Action? ThemeChanged { get; set; }
    public bool IsAvailable => _registered && !_disposed;
    /// <summary>The device whose battery the tray number shows, if any.</summary>
    public string? SelectedDeviceId => _selectedDeviceId;

    public TrayIcon(nint window, Action open, Action refresh, Action exit, Action preferences, bool scenarioMode = false)
    {
        _window = window;
        _open = open;
        _refresh = refresh;
        _exit = exit;
        _preferences = preferences;
        _applicationName = scenarioMode ? "Battery Manager · 演示数据" : "Battery Manager";
        _procedure = WindowProcedure;
        if (!NativeMethods.SetWindowSubclass(window, _procedure, 41, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the notification-area window handler.");
        _iconSize = TaskbarIconSize();
        _icon = CreateBatteryIcon(null, 0x202020, _iconSize, out _glyph);
        Register();
    }

    public AppTestTray CaptureTestView() => new(IsAvailable, _selectedDeviceId, _lastPercent, _lastTip, _tone.ToString(), _dark, _color,
        _glyph.IconSize, _glyph.Width, _glyph.Height, _glyph.Face, TrayNumber.BundledFontLoaded);

    public void Apply(SessionSnapshot snapshot, IReadOnlyList<TrayPreference> priorities,
        IReadOnlyList<DeviceDisplay>? displays = null, bool dark = false)
    {
        if (_disposed) return;
        _snapshot = snapshot;
        _devices = displays ?? snapshot.State.Devices.Select(d => DeviceDisplayMapper.Map(d)).OfType<DeviceDisplay>().ToArray();
        var available = _devices.Where(d => d.TrayEligible).ToArray();
        var selected = snapshot.IsSuspended ? null : priorities.Select(p => available.FirstOrDefault(d => d.Id == p.DeviceId)).FirstOrDefault(d => d is not null)
            ?? available.FirstOrDefault(d => d.Id == _selectedDeviceId) ?? available.OrderBy(d => d.Id, StringComparer.Ordinal).FirstOrDefault();
        if (snapshot.IsSuspended) selected = null;
        _selectedDeviceId = selected?.Id;
        int? percent = selected?.Battery.Value;
        var tone = selected?.Battery.Tone ?? BatteryTone.None;
        uint color = tone == BatteryTone.Stale ? (dark ? 0xA0A0A0u : 0x707070u)
            : selected?.Battery.Low == true ? (dark ? 0xFFB454u : 0x9D4700u) : (dark ? 0xF2F2F2u : 0x202020u);
        var tip = selected is not null
            ? $"{(_applicationName.Contains("演示数据", StringComparison.Ordinal) ? "演示数据 · " : "")}{selected.Name} {percent}%"
            : _applicationName + "\n" + (snapshot.IsSuspended ? UiText.Pick("系统睡眠 · 读取已暂停", "System sleep · Reading paused")
                : snapshot.IsRefreshing ? UiText.Pick("正在读取…", "Reading…") : UiText.Pick("暂无可用电量", "No battery reading"));
        if (tip.Length > 127) tip = tip[..127];
        // The taskbar's scale can change while the app runs; redraw at its new icon size.
        var size = TaskbarIconSize();
        bool iconChanged = percent != _lastPercent || color != _color || size != _iconSize;
        _tone = tone;
        _dark = dark;
        _color = color;
        if (!iconChanged && tip == _lastTip) return;
        nint oldIcon = 0;
        if (iconChanged)
        {
            oldIcon = _icon;
            _icon = CreateBatteryIcon(percent, color, size, out _glyph);
            _iconSize = size;
            _lastPercent = percent;
        }
        _lastTip = tip;
        var data = CreateData();
        if (!NativeMethods.ShellNotifyIcon(1, ref data)) _registered = false;
        if (!_registered) Register();
        if (oldIcon != 0) NativeMethods.DestroyIcon(oldIcon);
    }

    private NativeMethods.NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        Window = _window,
        Id = IconId,
        Flags = 1 | 2 | 4 | 0x80,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = string.IsNullOrEmpty(_lastTip) ? _applicationName : _lastTip,
        Info = "",
        InfoTitle = ""
    };

    private void Register()
    {
        if (_disposed) return;
        var data = CreateData();
        _registered = NativeMethods.ShellNotifyIcon(0, ref data);
        if (_registered)
        {
            data.Version = 4;
            NativeMethods.ShellNotifyIcon(4, ref data);
        }
    }

    private nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint subclassId, nuint reference)
    {
        // Settings, theme, colorization, display and DPI changes: re-read the theme and the taskbar icon size.
        if (message is 0x001A or 0x031A or 0x0320 or 0x007E or 0x02E0) ThemeChanged?.Invoke();
        if (message == SingleInstance.ActivateMessage)
        {
            _open();
            return 0;
        }
        if (message == _taskbarCreated)
        {
            _registered = false;
            Register();
            ThemeChanged?.Invoke(); // A restarted taskbar may use another scale.
            return 0;
        }
        if (message == CallbackMessage)
        {
            uint action = (uint)((long)lParam & 0xFFFF);
            if (action is 0x0400 or 0x0401) TogglePanel?.Invoke(action == 0x0401); // Preserve keyboard activation for the panel's focus visual.
            else if (action == 0x007B) ShowMenu();
            return 0;
        }
        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        if (_disposed) return;
        nint menu = NativeMethods.CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            var devices = _devices.ToArray();
            if (devices.Length == 0)
                NativeMethods.AppendMenu(menu, 0x0002, 0, UiText.Pick("暂无在线设备", "No connected devices"));
            for (int index = 0; index < devices.Length; index++)
            {
                var device = devices[index];
                var percent = device.Battery.Value is { } value ? $"{value}%" : "—";
                var label = $"{device.Name.Replace("&", "&&")}  ·  {percent}";
                NativeMethods.AppendMenu(menu, 0, (nuint)(2000 + index), label);
            }
            NativeMethods.AppendMenu(menu, 0x0800, 0, "");
            NativeMethods.AppendMenu(menu, 0, OpenCommand, UiText.Pick("打开 " + _applicationName, "Open " + _applicationName));
            NativeMethods.AppendMenu(menu, 0, PreferencesCommand, UiText.Pick("托盘设置", "Tray settings"));
            var refreshUnavailable = _snapshot is { IsRefreshing: true } or { IsSuspended: true } or { Status: "Resuming" } or { OutstandingOperations: > 0 };
            NativeMethods.AppendMenu(menu, refreshUnavailable ? 0x0002u : 0u, RefreshCommand, _snapshot?.IsRefreshing == true ? UiText.Pick("正在读取…", "Reading…") : UiText.Pick("刷新全部", "Refresh all"));
            NativeMethods.AppendMenu(menu, 0x0800, 0, "");
            NativeMethods.AppendMenu(menu, 0, ExitCommand, UiText.Pick("退出", "Quit"));
            NativeMethods.GetCursorPos(out var position);
            NativeMethods.SetForegroundWindow(_window);
            uint command = NativeMethods.TrackPopupMenu(menu, 0x0100 | 0x0002, position.X, position.Y, 0, _window, 0);
            NativeMethods.PostMessage(_window, 0, 0, 0);
            switch (command)
            {
                case OpenCommand: _open(); break;
                case RefreshCommand: _refresh(); break;
                case ExitCommand: _exit(); break;
                case PreferencesCommand: _preferences(); break;
                default:
                    if (command >= 2000 && command < 2000 + devices.Length) _open();
                    break;
            }
        }
        finally { NativeMethods.DestroyMenu(menu); }
    }

    public bool TryGetBounds(out NativeMethods.Rect bounds)
    {
        var identifier = new NativeMethods.NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconIdentifier>(), Window = _window, Id = IconId };
        return NativeMethods.ShellNotifyIconGetRect(ref identifier, out bounds) == 0;
    }

    public void RestoreFocus()
    {
        var data = CreateData();
        NativeMethods.ShellNotifyIcon(3, ref data); // NIM_SETFOCUS
    }

    // Notification icons are small icons: 16 px at 100 %, 20 at 125 %, 24 at 150 %, 32 at 200 %.
    // Drawing at exactly that size keeps the shell from rescaling (and blurring) the number.
    private static int TaskbarIconSize()
    {
        uint dpi = 0;
        var taskbar = NativeMethods.FindWindowByClass("Shell_TrayWnd", 0);
        if (taskbar != 0) dpi = NativeMethods.GetDpiForWindow(taskbar);
        if (dpi == 0) dpi = NativeMethods.GetDpiForSystem();
        if (dpi == 0) dpi = 96;
        var size = NativeMethods.GetSystemMetricsForDpi(49, dpi); // SM_CXSMICON
        if (size <= 0) size = (int)Math.Round(16 * dpi / 96.0);
        return Math.Clamp(size, 16, 64);
    }

    private static nint CreateBatteryIcon(int? percent, uint rgb, int size, out TrayGlyph glyph)
    {
        var coverage = TrayNumber.Draw(percent?.ToString(CultureInfo.InvariantCulture) ?? "—", size, out glyph);
        nint screen = NativeMethods.GetDC(0);
        nint canvas = NativeMethods.CreateCompatibleDC(screen);
        NativeMethods.ReleaseDC(0, screen);
        var info = new NativeMethods.BitmapInfo
        {
            Header = new NativeMethods.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(), Width = size, Height = -size,
                Planes = 1, BitCount = 32, Compression = 0, SizeImage = (uint)(size * size * 4)
            }
        };
        nint bitmap = NativeMethods.CreateDIBSection(canvas, ref info, 0, out nint pixels, 0, 0);
        try
        {
            if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the tray battery icon.");
            var bytes = new int[size * size];
            for (int i = 0; i < bytes.Length; i++)
            {
                // Glyph coverage becomes premultiplied alpha; the background stays transparent.
                uint alpha = coverage[i];
                bytes[i] = unchecked((int)((alpha << 24) | (((rgb >> 16) & 255) * alpha / 255 << 16)
                    | (((rgb >> 8) & 255) * alpha / 255 << 8) | ((rgb & 255) * alpha / 255)));
            }
            Marshal.Copy(bytes, 0, pixels, bytes.Length);
            nint mask = NativeMethods.CreateBitmap(size, size, 1, 1, 0);
            try
            {
                var iconInfo = new NativeMethods.IconInfo { IsIcon = true, Color = bitmap, Mask = mask };
                nint icon = NativeMethods.CreateIconIndirect(ref iconInfo);
                if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the tray battery icon.");
                return icon;
            }
            finally { NativeMethods.DeleteObject(mask); }
        }
        finally
        {
            if (bitmap != 0) NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(canvas);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = CreateData();
        NativeMethods.ShellNotifyIcon(2, ref data);
        NativeMethods.RemoveWindowSubclass(_window, _procedure, 41);
        if (_icon != 0) NativeMethods.DestroyIcon(_icon);
        _icon = 0;
        GC.KeepAlive(_procedure);
    }
}

internal readonly record struct TrayGlyph(int IconSize, int Width, int Height, string Face = "");

// Draws the tray number in Barlow Semi Condensed Medium, which ships inside the app (SIL Open Font
// License 1.1, licenses/Barlow-OFL.txt) and is added for this process only; nothing is installed.
// Its digits are tall by design, so two of them fit the icon without squeezing Segoe UI out of shape.
// Text is drawn at the icon's real pixel size so the font's hinting keeps stems on whole pixels.
// Digits are at most 80 % of the icon height (16 px at 125 %); 100 steps down a size instead of
// being squeezed; the unknown mark is a drawn bar.
internal static class TrayNumber
{
    private const double MaximumHeight = 0.8;

    // The bundled face first. If it cannot be loaded, Windows' own Bahnschrift SemiCondensed (regular
    // weight, close to Medium), then Segoe UI. Each candidate is verified with GetTextFace.
    // (EnumFontFamiliesEx truncates Bahnschrift instance names to 31 characters, but CreateFont accepts
    // the full name and GetTextFace returns it, so full names are compared.)
    private const string BundledFace = "Barlow Semi Condensed Medium";
    private const string BundledResource = "BatteryManager.App.TrayDigits.ttf";
    private static readonly (string Name, int Weight)[] Candidates =
    [
        (BundledFace, 0),
        ("Bahnschrift SemiCondensed", 0),
        ("Segoe UI", 400)
    ];
    private static bool? _bundledLoaded;
    private static readonly object Gate = new();
    private static readonly Dictionary<int, int> Sizes = [];
    private static (string Name, int Weight)? _face;

    private readonly record struct Ink(byte[] Coverage, int Stride, int Left, int Top, int Width, int Height);

    public static string FontName => Face().Name;

    public static byte[] Draw(string text, int size, out TrayGlyph glyph)
    {
        var result = new byte[size * size];
        var face = Face();
        if (text.Length == 0 || !text.All(char.IsAsciiDigit))
        {
            // Unknown: a centred bar, about as thick as a digit stem. Width and thickness share the icon's
            // parity, so the bar has the same margin on both sides and above and below.
            var width = (int)Math.Round(size * 0.6);
            if ((size - width) % 2 != 0) width++;
            var thickness = Math.Max(2, (int)Math.Round(size / 10.0));
            if ((size - thickness) % 2 != 0) thickness++;
            var left = (size - width) / 2;
            var top = (size - thickness) / 2;
            for (var y = top; y < top + thickness; y++) Array.Fill(result, byte.MaxValue, y * size + left, width);
            glyph = new(size, width, thickness, face.Name);
            return result;
        }
        var em = EmFor(size, face);
        var ink = Render(text, em, face);
        while (ink.Width > size && em > 6) ink = Render(text, --em, face); // 100: a smaller size, not a squeeze
        var inkWidth = Math.Min(ink.Width, size);
        var inkHeight = Math.Min(ink.Height, size);
        var x0 = (size - inkWidth) / 2;
        var y0 = (size - inkHeight) / 2;
        for (var y = 0; y < inkHeight; y++)
            Array.Copy(ink.Coverage, (ink.Top + y) * ink.Stride + ink.Left, result, (y0 + y) * size + x0, inkWidth);
        glyph = new(size, inkWidth, inkHeight, face.Name);
        return result;
    }

    // The largest size at which "88" fits the icon width and the height limit. Sizes do not depend
    // on the value, so 11 and 88 share one size; the result is kept per icon size.
    private static int EmFor(int size, (string Name, int Weight) face)
    {
        lock (Gate) if (Sizes.TryGetValue(size, out var known)) return known;
        var margin = Math.Max(1, (int)Math.Round(size / 16.0, MidpointRounding.AwayFromZero));
        var limit = Math.Min(size - 2 * margin, (int)Math.Round(size * MaximumHeight));
        var em = (int)Math.Ceiling(limit / 0.55) + 2;
        for (; em > 6; em--)
        {
            var ink = Render("88", em, face);
            if (ink.Width == 0 || ink.Height == 0) throw new InvalidOperationException("The tray font produced no visible digits.");
            if (ink.Width <= size && ink.Height <= limit) break;
        }
        lock (Gate) Sizes[size] = em;
        return em;
    }

    /// <summary>Whether the bundled font was added to this process (for diagnostics).</summary>
    public static bool BundledFontLoaded => _bundledLoaded == true;

    // GDI copies the input bytes. Its private font resource remains available until the process exits.
    private static bool LoadBundledFont()
    {
        nint memory = 0;
        try
        {
            using var stream = typeof(TrayNumber).Assembly.GetManifestResourceStream(BundledResource);
            if (stream is null) return false;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            memory = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            uint fonts = 0;
            var handle = NativeMethods.AddFontMemResourceEx(memory, (uint)bytes.Length, 0, ref fonts);
            return handle != 0 && fonts > 0;
        }
        catch (Exception) { return false; }
        finally { if (memory != 0) Marshal.FreeHGlobal(memory); }
    }

    private static (string Name, int Weight) Face()
    {
        lock (Gate)
        {
            if (_face is { } known) return known;
            _bundledLoaded ??= LoadBundledFont();
        }
        (string Name, int Weight)? chosen = null;
        nint screen = NativeMethods.GetDC(0);
        nint canvas = NativeMethods.CreateCompatibleDC(screen);
        NativeMethods.ReleaseDC(0, screen);
        if (canvas == 0) throw new InvalidOperationException("Could not create the tray font measurement context.");
        try
        {
            foreach (var candidate in Candidates)
            {
                nint font = NativeMethods.CreateFont(-32, 0, 0, 0, candidate.Weight, 0, 0, 0, 1, 0, 0, 4, 0, candidate.Name);
                if (font == 0) continue;
                nint previous = NativeMethods.SelectObject(canvas, font);
                if (previous == 0 || previous == -1) { NativeMethods.DeleteObject(font); continue; }
                var buffer = new char[64];
                int length;
                try { length = NativeMethods.GetTextFace(canvas, buffer.Length, buffer); }
                finally { NativeMethods.SelectObject(canvas, previous); NativeMethods.DeleteObject(font); }
                if (length <= 0) continue;
                var end = Array.IndexOf(buffer, '\0');
                var actual = new string(buffer, 0, end >= 0 ? end : Math.Clamp(length, 0, buffer.Length));
                if (string.Equals(actual, candidate.Name, StringComparison.OrdinalIgnoreCase)) { chosen = candidate; break; }
            }
        }
        finally { NativeMethods.DeleteDC(canvas); }
        if (chosen is not { } verified) throw new InvalidOperationException("No tray font could be selected and verified.");
        lock (Gate) _face = verified;
        return verified;
    }

    private static Ink Render(string text, int em, (string Name, int Weight) face)
    {
        var width = (int)Math.Ceiling(em * (0.7 * text.Length + 0.6)) + 8;
        var height = (int)Math.Ceiling(em * 1.6) + 8;
        nint screen = NativeMethods.GetDC(0);
        nint canvas = NativeMethods.CreateCompatibleDC(screen);
        NativeMethods.ReleaseDC(0, screen);
        if (canvas == 0) throw new InvalidOperationException("Could not create the tray drawing context.");
        var info = new NativeMethods.BitmapInfo
        {
            Header = new NativeMethods.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32, Compression = 0, SizeImage = (uint)(width * height * 4)
            }
        };
        nint bitmap = NativeMethods.CreateDIBSection(canvas, ref info, 0, out nint bits, 0, 0);
        if (bitmap == 0)
        {
            NativeMethods.DeleteDC(canvas);
            throw new InvalidOperationException("Could not create the tray drawing bitmap.");
        }
        nint previousBitmap = NativeMethods.SelectObject(canvas, bitmap);
        // ANTIALIASED_QUALITY: grayscale smoothing on hinted outlines (ClearType colour fringes cannot be used in an icon).
        nint font = 0;
        nint previousFont = 0;
        try
        {
            if (previousBitmap == 0 || previousBitmap == -1) throw new InvalidOperationException("Could not select the tray drawing bitmap.");
            font = NativeMethods.CreateFont(-em, 0, 0, 0, face.Weight, 0, 0, 0, 1, 0, 0, 4, 0, face.Name);
            if (font == 0) throw new InvalidOperationException("Could not create the tray font.");
            previousFont = NativeMethods.SelectObject(canvas, font);
            if (previousFont == 0 || previousFont == -1) throw new InvalidOperationException("Could not select the tray font.");
            var raw = new int[width * height];
            Marshal.Copy(raw, 0, bits, raw.Length);
            NativeMethods.SetBkMode(canvas, 1);
            NativeMethods.SetTextColor(canvas, 0x00FFFFFF);
            var rect = new NativeMethods.Rect(0, 0, width, height);
            if (NativeMethods.DrawText(canvas, text, text.Length, ref rect, 0x0001 | 0x0004 | 0x0020 | 0x0800) == 0)
                throw new InvalidOperationException("Could not render the tray number."); // centered, single line, no prefix
            NativeMethods.GdiFlush();
            Marshal.Copy(bits, raw, 0, raw.Length);
            var coverage = new byte[raw.Length];
            int left = width, top = height, right = -1, bottom = -1;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var value = (byte)(raw[y * width + x] & 255); // white text: every channel holds the coverage
                    coverage[y * width + x] = value;
                    if (value == 0) continue;
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            return right < 0 ? new(coverage, width, 0, 0, 0, 0) : new(coverage, width, left, top, right - left + 1, bottom - top + 1);
        }
        finally
        {
            if (previousFont != 0 && previousFont != -1) NativeMethods.SelectObject(canvas, previousFont);
            if (previousBitmap != 0 && previousBitmap != -1) NativeMethods.SelectObject(canvas, previousBitmap);
            if (font != 0) NativeMethods.DeleteObject(font);
            NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(canvas);
        }
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct NotifyIconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")] internal static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier id, out Rect rect);
    internal delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect(int left, int top, int right, int bottom) { public int Left = left, Top = top, Right = right, Bottom = bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public uint XHotspot, YHotspot; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShellNotifyIcon(uint action, ref NotifyIconData data);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowSubclass(nint window, SubclassProc procedure, nuint id, nuint reference);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveWindowSubclass(nint window, SubclassProc procedure, nuint id);
    [DllImport("comctl32.dll")] internal static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")] internal static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] internal static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] internal static extern nint FindWindowByClass(string className, nint title);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] internal static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] internal static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] internal static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] internal static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")] internal static extern int DrawText(nint dc, string text, int count, ref Rect rect, uint format);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyIcon(nint icon);
    [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] internal static extern nint GetStockObject(int value);
    [DllImport("gdi32.dll")] internal static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] internal static extern nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, nint bits);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")] internal static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint outputPrecision, uint clipPrecision, uint quality, uint pitch, string family);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RoundRect(nint dc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] internal static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextFaceW")] internal static extern int GetTextFace(nint dc, int count, [Out] char[] name);
    [DllImport("gdi32.dll")] internal static extern nint AddFontMemResourceEx(nint font, uint size, nint reserved, ref uint fonts);
    [DllImport("gdi32.dll")] internal static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(nint dc);
}
