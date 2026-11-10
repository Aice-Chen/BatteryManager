using System.Globalization;
using BatteryManager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace BatteryManager.App;

internal static class UiText
{
    public static bool Chinese { get; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
    public static string Pick(string chinese, string english) => Chinese ? chinese : english;
    private static readonly IReadOnlyDictionary<string, (string Zh, string En)> Words = new Dictionary<string, (string, string)>
    {
        ["Devices"] = ("设备", "Devices"), ["Refresh"] = ("刷新", "Refresh"), ["Settings"] = ("设置", "Settings"),
        ["Back"] = ("返回", "Back"), ["Tray"] = ("托盘", "Tray"), ["TrayIcon"] = ("托盘图标", "Tray icon"),
        ["Appearance"] = ("外观", "Appearance"), ["About"] = ("关于", "About"), ["ColorMode"] = ("颜色模式", "Color mode"),
        ["MergeConnections"] = ("合并连接…", "Merge connection…"), ["MergeTitle"] = ("合并连接", "Merge connection"),
        ["MergeHint"] = ("选择同一设备的另一个连接。", "Choose another connection of the same device."),
        ["MergeKeep"] = ("合并后沿用“{0}”的名称和设置。", "“{0}” keeps its name and settings after merging."),
        ["MergeNone"] = ("暂无可合并的连接。请先用另一种方式连接这台设备。", "Nothing to merge yet. Connect this device another way first."),
        ["Merge"] = ("合并", "Merge"), ["Unmerge"] = ("取消合并", "Unmerge"),
        ["Merged"] = ("已合并", "Merged"), ["Unmerged"] = ("已取消合并", "Unmerged"), ["Undo"] = ("撤销", "Undo"),
        ["SameName"] = ("同名", "Same name"), ["ConnectionMethod"] = ("连接方式", "Connection"),
        ["Connected"] = ("已连接", "Connected"), ["NotConnected"] = ("未连接", "Not connected"),
        ["System"] = ("跟随系统", "System"), ["Light"] = ("浅色", "Light"), ["Dark"] = ("深色", "Dark"),
        ["ThemeHint"] = ("主窗口的颜色；托盘面板始终跟随任务栏", "Main window color; the tray panel always follows the taskbar"),
        ["Empty"] = ("没有已连接的设备", "No connected devices"), ["NoResponse"] = ("暂无响应", "Not responding"),
        ["ReadFailed"] = ("读取失败", "Read failed"), ["More"] = ("更多选项", "More options"),
        ["Rename"] = ("重命名", "Rename"), ["RenameDevice"] = ("重命名设备", "Rename device"),
        ["ChangeType"] = ("更改设备类型", "Change device type"), ["RestoreName"] = ("恢复原名", "Restore original name"),
        ["Save"] = ("保存", "Save"), ["Cancel"] = ("取消", "Cancel"), ["Saving"] = ("正在保存…", "Saving…"),
        ["Automatic"] = ("自动", "Automatic"), ["Unknown"] = ("未识别", "Not recognized"),
        ["Headphones"] = ("耳机", "Headphones"), ["Earbuds"] = ("真无线耳机", "Earbuds"),
        ["Keyboard"] = ("键盘", "Keyboard"), ["Mouse"] = ("鼠标", "Mouse"),
        ["GameController"] = ("游戏手柄", "Game controller"), ["Speaker"] = ("音箱", "Speaker"),
        ["Pen"] = ("触控笔", "Pen"), ["Other"] = ("其他设备", "Other device"),
        ["Bluetooth"] = ("蓝牙", "Bluetooth"), ["Receiver"] = ("2.4G", "2.4 GHz"), ["Wired"] = ("有线", "Wired"),
        ["Mixed"] = ("蓝牙 / 2.4G", "Bluetooth / 2.4 GHz"),
        ["Windows"] = ("Windows 设备属性", "Windows device property"), ["Bas"] = ("蓝牙电池服务", "Bluetooth Battery Service"),
        ["Atk"] = ("ATK 协议", "ATK protocol"), ["Lofree"] = ("Lofree 协议", "Lofree protocol"),
        ["Mchose"] = ("迈从协议", "MCHOSE protocol"), ["Logitech"] = ("罗技协议", "Logitech protocol"),
        ["Success"] = ("成功", "Read"), ["NoData"] = ("无数据", "No data"),
        ["Failed"] = ("失败", "Failed"), ["Unsupported"] = ("不支持", "Not supported"),
        ["Coarse"] = ("分档", "Approximate"), ["Copy"] = ("复制", "Copy"), ["Charging"] = ("充电中", "Charging"),
        ["Left"] = ("左耳", "Left"), ["Right"] = ("右耳", "Right"), ["Case"] = ("充电盒", "Case"),
        ["Edit"] = ("编辑", "Edit"), ["EditTrayIcon"] = ("编辑托盘图标", "Edit tray icon"),
        ["TrayIconHint"] = ("选择托盘数字优先显示哪台设备", "Choose which device the tray number shows first"),
        ["AboutHint"] = ("本地读取外设电量，不上传设备数据", "Reads accessory batteries locally. Device data is not uploaded."),
        ["BatterySource"] = ("电量来源", "Battery source"), ["OpenMain"] = ("打开主窗口", "Open main window"),
        ["Up"] = ("上移", "Move up"), ["Down"] = ("下移", "Move down"), ["Remove"] = ("移除", "Remove"),
        ["SaveFailed"] = ("保存未完成，请稍后重试。", "Saving did not complete. Please try again."),
        ["Unavailable"] = ("当前无法更改设置。", "Settings cannot be changed right now.")
    };
    public static string Get(string key) => Words.TryGetValue(key, out var words) ? Pick(words.Zh, words.En) : key;
    public static string Kind(DisplayDeviceKind kind) => Get(kind.ToString());
    public static string Kind(DeviceKind kind) => kind switch
    {
        DeviceKind.Headphones => Get("Headphones"), DeviceKind.Keyboard => Get("Keyboard"),
        DeviceKind.Mouse => Get("Mouse"), DeviceKind.Other => Get("Other"), _ => Get("Unknown")
    };
    public static string Connection(ConnectionState state) => state switch
    {
        ConnectionState.Pending => Get("NoResponse"), ConnectionState.Offline => Pick("离线", "Offline"), _ => Pick("在线", "Online")
    };
    public static string Source(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        if (source.Contains("atk", StringComparison.OrdinalIgnoreCase)) return Get("Atk");
        if (source.Contains("lofree", StringComparison.OrdinalIgnoreCase)) return Get("Lofree");
        if (source.Contains("mchose", StringComparison.OrdinalIgnoreCase)) return Get("Mchose");
        if (source.Contains("logitech", StringComparison.OrdinalIgnoreCase)) return Get("Logitech");
        if (source.Contains("gatt", StringComparison.OrdinalIgnoreCase) || source.Contains("bas", StringComparison.OrdinalIgnoreCase)) return Get("Bas");
        if (source.Contains("windows", StringComparison.OrdinalIgnoreCase) || source.Contains("property", StringComparison.OrdinalIgnoreCase)) return Get("Windows");
        return source;
    }
    public static string Transport(DeviceTransport transport) => transport == DeviceTransport.Unknown ? "" : Get(transport.ToString());
    public static string Presence(ConnectionDisplayState state) => state switch
    {
        ConnectionDisplayState.Connected => Get("Connected"), ConnectionDisplayState.NoResponse => Get("NoResponse"), _ => Get("NotConnected")
    };
    public static string Result(SourceDisplayResult result) => Get(result.ToString());
    public static string Icon(DeviceKind kind) => Icon(kind switch
    {
        DeviceKind.Headphones => DisplayDeviceKind.Headphones, DeviceKind.Keyboard => DisplayDeviceKind.Keyboard,
        DeviceKind.Mouse => DisplayDeviceKind.Mouse, _ => DisplayDeviceKind.Other
    });
    public static string Icon(DisplayDeviceKind kind) => kind switch
    {
        DisplayDeviceKind.Headphones => "\uE7F6", DisplayDeviceKind.Earbuds => "\uE967", DisplayDeviceKind.Keyboard => "\uE765",
        DisplayDeviceKind.Mouse => "\uE962", DisplayDeviceKind.GameController => "\uE7FC", DisplayDeviceKind.Speaker => "\uE7F5",
        DisplayDeviceKind.Pen => "\uEDFB", _ => "\uE772"
    };
    public static string TransportIcon(DeviceTransport transport) => transport switch
    { DeviceTransport.Bluetooth => "\uE702", DeviceTransport.Receiver => "\uE957", DeviceTransport.Wired => "\uE88E", _ => "\uE772" };
    public static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Color.FromArgb(255, red, green, blue));
    private static bool Dark(FrameworkElement? element) => element?.ActualTheme == ElementTheme.Dark ||
        (element is null && Microsoft.UI.Xaml.Application.Current.RequestedTheme == ApplicationTheme.Dark);
    public static SolidColorBrush InkFor(FrameworkElement? element) => Dark(element) ? Brush(255, 255, 255) : Brush(27, 27, 27);
    public static SolidColorBrush MutedFor(FrameworkElement? element) => Dark(element) ? Brush(173, 173, 173) : Brush(100, 100, 100);
    public static SolidColorBrush BackgroundFor(FrameworkElement? element) => Dark(element) ? Brush(32, 32, 32) : Brush(243, 243, 243);
    public static SolidColorBrush CardFor(FrameworkElement? element) => Dark(element) ? Brush(43, 43, 43) : Brush(251, 251, 251);
    public static SolidColorBrush LineFor(FrameworkElement? element) => Dark(element) ? Brush(58, 58, 58) : Brush(225, 225, 225);
    public static SolidColorBrush LowFor(FrameworkElement? element) => Dark(element) ? Brush(255, 180, 84) : Brush(166, 78, 0);
    public static SolidColorBrush AccentFor(FrameworkElement? element)
    {
        try { return new(new UISettings().GetColorValue(Dark(element) ? UIColorType.AccentLight2 : UIColorType.AccentDark1)); }
        catch { return Dark(element) ? Brush(96, 205, 255) : Brush(0, 95, 184); }
    }
    public static SolidColorBrush BatteryBrush(FrameworkElement element, BatteryDisplay battery) => battery.Tone != BatteryTone.Normal
        ? MutedFor(element) : battery.Low ? LowFor(element) : InkFor(element);
    public static SolidColorBrush SuccessFor(FrameworkElement? element) => Dark(element) ? Brush(108, 203, 95) : Brush(15, 123, 15);
    public static FontFamily Mono() => new("Cascadia Mono, Consolas");

    // Windows 11 subtle fill values (SubtleFillColorSecondary / Tertiary).
    private static Color Subtle(bool dark, bool pressed) => dark
        ? Color.FromArgb(pressed ? (byte)0x0A : (byte)0x0F, 255, 255, 255)
        : Color.FromArgb(pressed ? (byte)0x06 : (byte)0x09, 0, 0, 0);

    /// <summary>Lightweight styling: a transparent button that shows only the Windows 11 subtle hover and press fills.
    /// Theme dictionaries keep the fills correct when the element theme changes.</summary>
    public static T MakeSubtle<T>(T button) where T : Button
    {
        var resources = new ResourceDictionary();
        resources.ThemeDictionaries["Light"] = SubtleSet(false);
        resources.ThemeDictionaries["Dark"] = SubtleSet(true);
        button.Resources = resources;
        button.BorderThickness = new Thickness(0);
        return button;
    }

    private static ResourceDictionary SubtleSet(bool dark)
    {
        var set = new ResourceDictionary();
        var none = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        set["ButtonBackground"] = none;
        set["ButtonBackgroundPointerOver"] = new SolidColorBrush(Subtle(dark, false));
        set["ButtonBackgroundPressed"] = new SolidColorBrush(Subtle(dark, true));
        set["ButtonBackgroundDisabled"] = none;
        set["ButtonBorderBrush"] = none;
        set["ButtonBorderBrushPointerOver"] = none;
        set["ButtonBorderBrushPressed"] = none;
        set["ButtonBorderBrushDisabled"] = none;
        return set;
    }

    public static SolidColorBrush Ink => InkFor(null);
    public static SolidColorBrush Muted => MutedFor(null);
    public static SolidColorBrush Accent => AccentFor(null);
}

// Windows 11 motion: durations 83 / 167 / 250 / 333 ms. "Decelerate" (fast out, slow in) is
// cubic-bezier(0,0,0,1) for things that open or arrive; "accelerate" is cubic-bezier(1,0,1,1) for things that leave.
internal static class Motion
{
    // Set only by the isolated scenario test command; never changes Windows preferences.
    internal static bool? TestEnabled { get; set; }
    /// <summary>Windows "Animation effects". Read when an animation starts, so a change applies to the next one.</summary>
    public static bool Enabled()
    {
        if (TestEnabled is { } enabled) return enabled;
        try { return new UISettings().AnimationsEnabled; }
        catch (Exception) { return true; }
    }

    public static DoubleAnimationUsingKeyFrames Decelerate(double from, double to, double milliseconds) =>
        Spline(from, to, milliseconds, new(0, 0), new(0, 1));

    public static DoubleAnimationUsingKeyFrames Accelerate(double from, double to, double milliseconds) =>
        Spline(from, to, milliseconds, new(1, 0), new(1, 1));

    /// <summary>Progress (0..1) of the decelerate curve at time fraction <paramref name="x"/>, for frame-driven layout tweens.</summary>
    public static double Decelerate(double x) => Bezier(0, 0, 0, 1, x);

    // y at time x on cubic-bezier(x1, y1, x2, y2), the same curve a XAML KeySpline describes.
    private static double Bezier(double x1, double y1, double x2, double y2, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double low = 0, high = 1, t = x;
        for (var i = 0; i < 30; i++)
        {
            var current = Curve(x1, x2, t);
            if (Math.Abs(current - x) < 1e-6) break;
            if (current < x) low = t; else high = t;
            t = (low + high) / 2;
        }
        return Curve(y1, y2, t);
    }

    private static double Curve(double first, double second, double t) =>
        3 * (1 - t) * (1 - t) * t * first + 3 * (1 - t) * t * t * second + t * t * t;

    private static DoubleAnimationUsingKeyFrames Spline(double from, double to, double milliseconds,
        Windows.Foundation.Point first, Windows.Foundation.Point second)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = from });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(1, milliseconds))), Value = to,
            KeySpline = new KeySpline { ControlPoint1 = first, ControlPoint2 = second }
        });
        return animation;
    }

    public static Storyboard Run(params (DependencyObject Target, string Property, Timeline Animation)[] parts)
    {
        var storyboard = new Storyboard();
        foreach (var (target, property, animation) in parts)
        {
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
        return storyboard;
    }
}

/// <summary>Turns a refresh glyph while a refresh runs. When it ends, the glyph coasts to rest at its
/// starting angle instead of snapping back, so even a short refresh reads as one clear turn.</summary>
internal sealed record RefreshMotionState(bool Spinning, bool Coasting, double Angle);

internal sealed class RefreshSpin
{
    private const double TurnMilliseconds = 900;
    private readonly RotateTransform _rotation = new();
    private readonly Storyboard _loop;
    private Storyboard? _coast;
    private bool _spinning;

    public RefreshSpin(FrameworkElement glyph)
    {
        glyph.RenderTransformOrigin = new Windows.Foundation.Point(.5, .5);
        glyph.RenderTransform = _rotation;
        var turn = new DoubleAnimation { From = 0, To = 360, Duration = new Duration(TimeSpan.FromMilliseconds(TurnMilliseconds)), RepeatBehavior = RepeatBehavior.Forever };
        _loop = Motion.Run((_rotation, "Angle", turn));
    }

    public bool Spinning => _spinning;
    public RefreshMotionState Capture() => new(_spinning,
        _coast?.GetCurrentState() == ClockState.Active, _rotation.Angle);

    public void Set(bool spinning)
    {
        if (spinning == _spinning) return;
        _spinning = spinning;
        if (spinning)
        {
            StopCoast();
            _rotation.Angle = 0;
            _loop.Begin();
            return;
        }
        var elapsed = _loop.GetCurrentState() == ClockState.Active ? _loop.GetCurrentTime().TotalMilliseconds : 0;
        _loop.Stop();
        _rotation.Angle = 0;
        var angle = elapsed % TurnMilliseconds / TurnMilliseconds * 360;
        if (angle < 1 || !Motion.Enabled()) return;
        // Quadratic ease-out over twice the remaining turn time starts at the loop's own speed.
        var remaining = (360 - angle) / 360 * TurnMilliseconds;
        var coast = new DoubleAnimation
        {
            From = angle, To = 360, Duration = new Duration(TimeSpan.FromMilliseconds(2 * remaining)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var storyboard = Motion.Run((_rotation, "Angle", coast));
        storyboard.Completed += (_, _) => { if (ReferenceEquals(_coast, storyboard)) StopCoast(); };
        _coast = storyboard;
        storyboard.Begin();
    }

    public void Stop()
    {
        _spinning = false;
        _loop.Stop();
        StopCoast();
    }

    private void StopCoast()
    {
        _coast?.Stop();
        _coast = null;
        _rotation.Angle = 0;
    }
}

/// <summary>Shows the bottom <see cref="Progress"/> fraction of its child. The child keeps its full height,
/// so while the panel grows the content slides down from under whatever sits above it, as in the
/// Windows 11 expander, and everything below moves with the panel's height.</summary>
internal sealed class RevealPanel : Panel
{
    private readonly RectangleGeometry _clip = new();
    private double _progress = 1;

    public RevealPanel() => Clip = _clip;

    public double Progress
    {
        get => _progress;
        set
        {
            var next = Math.Clamp(value, 0, 1);
            if (next == _progress) return;
            _progress = next;
            InvalidateMeasure();
        }
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(availableSize.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new(width, height * _progress);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        foreach (var child in Children)
        {
            var height = child.DesiredSize.Height;
            child.Arrange(new Windows.Foundation.Rect(0, finalSize.Height - height, finalSize.Width, height));
        }
        _clip.Rect = new Windows.Foundation.Rect(0, 0, finalSize.Width, finalSize.Height);
        return finalSize;
    }
}

internal enum BatteryGlyphState { Level, NoReading, NoResponse }

/// <summary>The "—" shown in place of a battery number. The font's em dash sits well below the middle of the
/// digits, so the dash is drawn instead: a bar as long as the em dash and about as thick, in whole device
/// pixels, centred where the digits are centred (the battery glyph's centre). It shares the number's cell,
/// right-aligned with room for the transparent "%" after it, so it lines up with the digits above and below.</summary>
internal sealed class ValueDash : UserControl
{
    private readonly Rectangle _bar = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly double _digitSize;
    private readonly double _percentSize;
    private XamlRoot? _root;
    private double _scale;

    public ValueDash(double digitSize, double percentSize)
    {
        _digitSize = digitSize;
        _percentSize = percentSize;
        IsHitTestVisible = false;
        IsTabStop = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;
        Content = _bar;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        Layout(1);
    }

    private void Attach()
    {
        Detach();
        if (XamlRoot is not { } root) return;
        _root = root;
        _root.Changed += RootChanged;
        if (root.RasterizationScale != _scale) Layout(root.RasterizationScale);
    }

    private void Detach()
    {
        if (_root is not null) _root.Changed -= RootChanged;
        _root = null;
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale != _scale) Layout(sender.RasterizationScale);
    }

    private void Layout(double scale)
    {
        _scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        double Snap(double dips) => Math.Max(1, Math.Round(dips * _scale, MidpointRounding.AwayFromZero)) / _scale;
        _bar.Width = Snap(Advance("\u2014", _digitSize, Microsoft.UI.Text.FontWeights.SemiBold));
        _bar.Height = Snap(_digitSize * 0.08);
        Margin = new Thickness(0, 0, Advance("%", _percentSize, Microsoft.UI.Text.FontWeights.Normal), 0);
    }

    // The width the text itself would take, measured with the same size and weight as the number's runs.
    private static double Advance(string text, double size, Windows.UI.Text.FontWeight weight)
    {
        var probe = new TextBlock { Text = text, FontSize = size, FontWeight = weight };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    public void Paint(Brush brush) => _bar.Fill = brush;
    public string? InkColor => (_bar.Fill as SolidColorBrush)?.Color.ToString();
    /// <summary>For tests: the bar in DIPs, "width x height".</summary>
    public string Describe() => FormattableString.Invariant($"{_bar.Width:0.##}x{_bar.Height:0.##}@{_scale}");
    internal AppTestGlyphRootBounds? CaptureRootBounds()
    {
        if (Visibility != Visibility.Visible || XamlRoot is not { Content: UIElement content } root || _bar.ActualWidth <= 0 || _bar.ActualHeight <= 0) return null;
        var bounds = _bar.TransformToVisual(content).TransformBounds(new Windows.Foundation.Rect(0, 0, _bar.ActualWidth, _bar.ActualHeight));
        return new(bounds.Left, bounds.Top, bounds.Width, bounds.Height, root.RasterizationScale);
    }
}

/// <summary>The battery glyph used in the tray panel and the main window. One shell, one stroke and one
/// colour per state; only the mark inside changes: the level fill (ten steps), the fill plus a bolt while
/// charging, a dash for "no reading" (an empty shell would read as 0 %), a question mark for "not responding".
/// Everything is laid out in whole device pixels for the current display scale: the gaps above and below
/// the fill are the same, straight edges fall on whole pixels when the control itself does, and the bolt,
/// the dash and the question mark are centred on the shell.</summary>
internal sealed class BatteryGlyph : UserControl
{
    // The bolt on a 20.5 x 11.5 shell, relative to the shell centre: top tip, the lower bar (outer, inner),
    // bottom tip, the upper bar (outer, inner). The bars' horizontal edges are placed on whole pixels.
    private static readonly (double X, double Y)[] BoltShape = [(1.4, -5.4), (-3.4, 0), (-0.2, 0), (-1.4, 5.4), (3.4, 0), (0.2, 0)];
    private readonly double _nominalWidth;
    private readonly Canvas _canvas = new();
    private readonly Border _shell = new();
    private readonly Rectangle _tip = new();
    // The fill is a path, so the space around the bolt can be a real hole: the card or the acrylic behind the
    // glyph shows through instead of a painted colour that only approximates it.
    private readonly Microsoft.UI.Xaml.Shapes.Path _fill = new();
    private readonly Rectangle _dash = new();
    private readonly Polygon _bolt = new();
    private readonly Microsoft.UI.Xaml.Shapes.Path _question = new() { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Flat };
    private readonly Rectangle _dot = new();
    private readonly RectangleGeometry _fillClip = new();
    private Windows.Foundation.Point[] _boltPixels = [];
    private int _fillPixels;
    private XamlRoot? _root;
    private double _scale = 1;
    private Metrics _metrics;
    private string _marks = "";

    // Device pixels: stroke, gap between stroke and fill, shell outer size, fill box, corner radius, tip and dash.
    private readonly record struct Metrics(int Stroke, int Gap, int ShellWidth, int ShellHeight, int FillWidth, int FillHeight,
        int Radius, int TipGap, int TipWidth, int TipHeight, int DashWidth);

    public BatteryGlyph(double width)
    {
        _nominalWidth = width;
        IsHitTestVisible = false;
        IsTabStop = false;
        // The cut-out around the bolt belongs to the fill only; the shell stays whole where the bolt crosses it.
        _fill.Clip = _fillClip;
        foreach (var element in new UIElement[] { _shell, _tip, _fill, _dash, _bolt, _question, _dot }) _canvas.Children.Add(element);
        Content = _canvas;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        Layout(1);
        Show(BatteryGlyphState.NoReading, null, false);
    }

    internal AppTestGlyphRootBounds? CaptureRootBounds()
    {
        if (XamlRoot is not { Content: UIElement content } root || ActualWidth <= 0 || ActualHeight <= 0) return null;
        var rect = TransformToVisual(content).TransformBounds(new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight));
        return new(rect.Left, rect.Top, rect.Width, rect.Height, root.RasterizationScale);
    }

    private void Attach()
    {
        Detach();
        if (XamlRoot is not { } root) return;
        _root = root;
        _root.Changed += RootChanged;
        if (root.RasterizationScale != _scale) Layout(root.RasterizationScale);
    }

    private void Detach()
    {
        if (_root is not null) _root.Changed -= RootChanged;
        _root = null;
    }

    // A move to a monitor with another scale lays the glyph out again in that monitor's pixels.
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale != _scale) Layout(sender.RasterizationScale);
    }

    private void Layout(double scale)
    {
        _scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        var unit = _nominalWidth * _scale / 24; // device pixels per design-grid unit
        int Pixels(double units) => Math.Max(1, (int)Math.Round(units * unit, MidpointRounding.AwayFromZero));
        var stroke = Pixels(1.5);
        var gap = Pixels(1.25);
        var shellHeight = Math.Max(Pixels(11.5), 2 * stroke + 2 * gap + 2);
        // The fill box keeps an even remainder around a stroke-high dash, so the dash centres on whole pixels.
        if ((shellHeight - 2 * stroke - 2 * gap - stroke) % 2 != 0) shellHeight++;
        var shellWidth = Pixels(20.5);
        var fillWidth = shellWidth - 2 * stroke - 2 * gap;
        var dashWidth = Pixels(5);
        if ((fillWidth - dashWidth) % 2 != 0) dashWidth++;
        var tipHeight = Pixels(4.5);
        if ((shellHeight - tipHeight) % 2 != 0) tipHeight++;
        _metrics = new(stroke, gap, shellWidth, shellHeight, fillWidth, shellHeight - 2 * stroke - 2 * gap,
            Pixels(3.25), Pixels(1), Pixels(1.5), tipHeight, dashWidth);
        var m = _metrics;
        double Dip(double pixels) => pixels / _scale;
        Width = _canvas.Width = Dip(m.ShellWidth + m.TipGap + m.TipWidth);
        Height = _canvas.Height = Dip(m.ShellHeight);
        _shell.Width = Dip(m.ShellWidth);
        _shell.Height = Dip(m.ShellHeight);
        _shell.BorderThickness = new Thickness(Dip(m.Stroke));
        _shell.CornerRadius = new CornerRadius(Dip(m.Radius));
        Canvas.SetLeft(_tip, Dip(m.ShellWidth + m.TipGap));
        Canvas.SetTop(_tip, Dip((m.ShellHeight - m.TipHeight) / 2));
        _tip.Width = Dip(m.TipWidth);
        _tip.Height = Dip(m.TipHeight);
        _tip.RadiusX = _tip.RadiusY = Dip(m.TipWidth) / 2;
        var inset = m.Stroke + m.Gap;
        Canvas.SetLeft(_dash, Dip(inset + (m.FillWidth - m.DashWidth) / 2));
        Canvas.SetTop(_dash, Dip(inset + (m.FillHeight - m.Stroke) / 2));
        _dash.Width = Dip(m.DashWidth);
        _dash.Height = Dip(m.Stroke);
        _dash.RadiusX = _dash.RadiusY = Dip(m.Stroke) / 2;
        var bar = LayoutBolt(Dip);
        var question = LayoutQuestion(Dip);
        _marks = FormattableString.Invariant($"bolt bar {bar}; {question}");
        UpdateFill();
    }

    // The bolt follows its design outline, scaled to the shell's pixel width and height separately (so its
    // proportions can change slightly with the size and scale) and centred on the shell. The bar across its
    // middle is a whole number of pixels high with the same parity as the shell, so its two horizontal edges
    // land on whole pixels. Around it, a band as wide as the gap between the fill and the shell is cut out of
    // the fill (see UpdateFill).
    private int LayoutBolt(Func<double, double> dip)
    {
        var m = _metrics;
        var unitX = m.ShellWidth / 20.5;
        var unitY = m.ShellHeight / 11.5;
        var ideal = 1.2 * unitY;
        var bar = Math.Max(1, (int)Math.Round(ideal, MidpointRounding.AwayFromZero));
        if ((m.ShellHeight - bar) % 2 != 0)
            bar = bar > 1 && ideal / (bar - 1) < (bar + 1) / ideal ? bar - 1 : bar + 1;
        var top = (m.ShellHeight - bar) / 2.0;
        double centreX = m.ShellWidth / 2.0, centreY = m.ShellHeight / 2.0;
        _bolt.Points.Clear();
        _boltPixels = new Windows.Foundation.Point[BoltShape.Length];
        for (var i = 0; i < BoltShape.Length; i++)
        {
            var (x, y) = BoltShape[i];
            // Points 1-2 sit on the lower edge of the bar, points 4-5 on its upper edge.
            var py = i is 1 or 2 ? top + bar : i is 4 or 5 ? top : centreY + y * unitY;
            _boltPixels[i] = new(centreX + x * unitX, py);
            _bolt.Points.Add(new Windows.Foundation.Point(dip(_boltPixels[i].X), dip(py)));
        }
        return bar;
    }

    // The question mark is drawn in pixels with the shell's stroke: a round hook, a straight stem and a square
    // dot, with the same clearance above it and below it inside the shell. Where the shell is too small for
    // a hook in that stroke (100 % scale), the stroke steps down to one pixel rather than touch the shell.
    private string LayoutQuestion(Func<double, double> dip)
    {
        var m = _metrics;
        var inner = m.ShellHeight - 2 * m.Stroke;
        var clear = Math.Max(1, (int)Math.Round(m.Gap / 2.0, MidpointRounding.AwayFromZero));
        var height = inner - 2 * clear;
        int stroke = m.Stroke, dotGap, radius;
        while (true)
        {
            dotGap = Math.Max(1, (int)Math.Round(stroke / 2.0, MidpointRounding.AwayFromZero));
            // Hook radius: room left after the dot, the gap above it and a stem of at least half a stroke,
            // and no wider than three quarters of the mark's height.
            radius = (int)Math.Floor(Math.Min((height - 2.0 * stroke - dotGap) / 2, (0.75 * height - stroke) / 2));
            if (radius >= stroke || stroke == 1) break;
            stroke--;
        }
        radius = Math.Max(1, radius);
        var radiusX = stroke == 1 ? Math.Max(radius, 2) : radius;
        var stemLeft = (m.ShellWidth - stroke) / 2;
        var stemX = stemLeft + stroke / 2.0;
        var top = m.Stroke + clear;
        var bottom = m.ShellHeight - m.Stroke - clear;
        var centreY = top + stroke / 2.0 + radius;
        var stemBottom = Math.Max(bottom - stroke - dotGap, centreY + radius);
        Windows.Foundation.Point P(double x, double y) => new(dip(x), dip(y));
        var figure = new PathFigure { StartPoint = P(stemX - radiusX, centreY), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment { Point = P(stemX + radiusX, centreY), Size = new(dip(radiusX), dip(radius)), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new BezierSegment { Point1 = P(stemX + radiusX, centreY + radius / 2.0), Point2 = P(stemX, centreY + radius / 2.0), Point3 = P(stemX, centreY + radius) });
        figure.Segments.Add(new LineSegment { Point = P(stemX, stemBottom) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _question.Data = geometry;
        _question.StrokeThickness = dip(stroke);
        Canvas.SetLeft(_dot, dip(stemLeft));
        Canvas.SetTop(_dot, dip(bottom - stroke));
        _dot.Width = _dot.Height = dip(stroke);
        _dot.RadiusX = _dot.RadiusY = stroke >= 3 ? dip(stroke) / 2 : 0;
        return FormattableString.Invariant($"question stroke {stroke}, hook radius {radiusX}x{radius}, clear {clear}, dot gap {dotGap}");
    }

    private void UpdateFill()
    {
        var m = _metrics;
        _fillPixels = (int)Math.Round(m.FillWidth * Steps / 10.0, MidpointRounding.AwayFromZero);
        var visible = State == BatteryGlyphState.Level && _fillPixels > 0;
        _fill.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) { _fill.Data = null; return; }
        double Dip(double pixels) => pixels / _scale;
        double left = m.Stroke + m.Gap, top = left, right = left + _fillPixels, bottom = top + m.FillHeight;
        _fillClip.Rect = new Windows.Foundation.Rect(Dip(left), Dip(top), Dip(_fillPixels), Dip(m.FillHeight));
        // One-DIP rounded corners, including the noncharging fill. While charging, the band around the bolt is a hole (even-odd with the bolt's
        // outline, clipped to the fill). The right end is square then: the hole can reach it, and a rounded
        // corner there would leave a speck of fill outside the arc but inside the hole.
        var radius = Math.Min(_scale, Math.Min(_fillPixels, m.FillHeight) / 2.0);
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(RoundedFill(left, top, right, bottom, radius, Charging ? 0 : radius, Dip));
        if (Charging) group.Children.Add(Outline(_boltPixels, m.Gap, Dip));
        _fill.Data = group;
    }

    private static PathGeometry RoundedFill(double left, double top, double right, double bottom, double leftRadius, double rightRadius,
        Func<double, double> dip)
    {
        Windows.Foundation.Point P(double x, double y) => new(dip(x), dip(y));
        var figure = new PathFigure { StartPoint = P(left + leftRadius, top), IsClosed = true, IsFilled = true };
        void Line(double x, double y) => figure.Segments.Add(new LineSegment { Point = P(x, y) });
        void Corner(double x, double y, double r)
        {
            if (r > 0) figure.Segments.Add(new ArcSegment { Point = P(x, y), Size = new(dip(r), dip(r)), SweepDirection = SweepDirection.Clockwise });
        }
        Line(right - rightRadius, top); Corner(right, top + rightRadius, rightRadius);
        Line(right, bottom - rightRadius); Corner(right - rightRadius, bottom, rightRadius);
        Line(left + leftRadius, bottom); Corner(left, bottom - leftRadius, leftRadius);
        Line(left, top + leftRadius); Corner(left + leftRadius, top, leftRadius);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    // The bolt's outline pushed out by `distance` pixels with round outer corners: the area a stroke twice that
    // wide with round joins would cover. Inner corners meet where the two pushed-out edges cross.
    private static PathGeometry Outline(IReadOnlyList<Windows.Foundation.Point> points, double distance, Func<double, double> dip)
    {
        var count = points.Count;
        double area = 0;
        for (var i = 0; i < count; i++) { var a = points[i]; var b = points[(i + 1) % count]; area += a.X * b.Y - b.X * a.Y; }
        var sign = area > 0 ? 1 : -1;
        (double X, double Y) Direction(int i) { var a = points[i]; var b = points[(i + 1) % count]; return (b.X - a.X, b.Y - a.Y); }
        (double X, double Y) Normal(int i)
        {
            var (x, y) = Direction(i);
            var length = Math.Sqrt(x * x + y * y);
            return sign > 0 ? (y / length, -x / length) : (-y / length, x / length);
        }
        Windows.Foundation.Point P(double x, double y) => new(dip(x), dip(y));
        var joins = new List<(Windows.Foundation.Point Start, Windows.Foundation.Point? End)>();
        for (var k = 0; k < count; k++)
        {
            var previous = (k - 1 + count) % count;
            var vertex = points[k];
            var (n1x, n1y) = Normal(previous); var (n2x, n2y) = Normal(k);
            var (d1x, d1y) = Direction(previous); var (d2x, d2y) = Direction(k);
            var cross = d1x * d2y - d1y * d2x;
            if (cross * sign > 0)
                joins.Add((P(vertex.X + distance * n1x, vertex.Y + distance * n1y), P(vertex.X + distance * n2x, vertex.Y + distance * n2y)));
            else
            {
                var wx = distance * (n2x - n1x); var wy = distance * (n2y - n1y);
                var t = Math.Abs(cross) < 1e-9 ? 0 : (wx * d2y - wy * d2x) / cross;
                joins.Add((P(vertex.X + distance * n1x + t * d1x, vertex.Y + distance * n1y + t * d1y), null));
            }
        }
        var sweep = sign > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        var figure = new PathFigure { StartPoint = joins[0].End ?? joins[0].Start, IsClosed = true, IsFilled = true };
        for (var i = 1; i <= count; i++)
        {
            var join = joins[i % count];
            figure.Segments.Add(new LineSegment { Point = join.Start });
            if (join.End is { } end) figure.Segments.Add(new ArcSegment { Point = end, Size = new(dip(distance), dip(distance)), SweepDirection = sweep });
        }
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    public BatteryGlyphState State { get; private set; }
    /// <summary>Fill steps shown, 0 to 10 (0 for the dash and question-mark states).</summary>
    public int Steps { get; private set; }
    public bool Charging { get; private set; }
    public string? InkColor => (_shell.BorderBrush as SolidColorBrush)?.Color.ToString();
    /// <summary>For tests: the device-pixel layout in use, "stroke,gap,shell WxH,fill WxH,fill shown@scale".
    /// The gap is the same above and below the fill by construction.</summary>
    public string PixelLayout => FormattableString.Invariant(
        $"{_metrics.Stroke},{_metrics.Gap},{_metrics.ShellWidth}x{_metrics.ShellHeight},{_metrics.FillWidth}x{_metrics.FillHeight},{_fillPixels}@{_scale}");
    /// <summary>For tests: the device-pixel layout of the bolt and the question mark at the current scale.</summary>
    public string MarkLayout => _marks;

    public void Show(BatteryGlyphState state, int? percent, bool charging)
    {
        State = state;
        Steps = state == BatteryGlyphState.Level ? (int)Math.Round(Math.Clamp(percent ?? 0, 0, 100) / 10.0, MidpointRounding.AwayFromZero) : 0;
        Charging = state == BatteryGlyphState.Level && charging;
        UpdateFill();
        _bolt.Visibility = Charging ? Visibility.Visible : Visibility.Collapsed;
        _question.Visibility = _dot.Visibility = state == BatteryGlyphState.NoResponse ? Visibility.Visible : Visibility.Collapsed;
        _dash.Visibility = state == BatteryGlyphState.NoReading ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Colours the glyph. The cut-out around the bolt is transparent, so it needs no colour of its own.</summary>
    public void Paint(Brush ink)
    {
        _shell.BorderBrush = ink;
        _tip.Fill = _fill.Fill = _bolt.Fill = _dot.Fill = _dash.Fill = ink;
        _question.Stroke = ink;
    }

    public string Describe() => State switch
    {
        BatteryGlyphState.NoResponse => "NoResponse",
        BatteryGlyphState.NoReading => "NoReading",
        _ => Charging ? "Charging" : "Level"
    };
}
