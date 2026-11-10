using System.Diagnostics;
using System.Runtime.InteropServices;
using BatteryManager.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.System;

namespace BatteryManager.App;

internal sealed record AppTestPanel(bool Visible, string Theme, int RowCount, IReadOnlyList<AppTestCard> Cards,
    double? FirstRenderingMilliseconds, double? MaximumRenderingGapMilliseconds, int RenderingSamples,
    int X, int Y, int Width, int Height, string Measurement = "UI rendering callbacks after scripted or tray activation; not optical first-frame latency",
    string? FocusedDeviceId = null, string? FocusState = null,
    int WindowHeight = 0, int ClientHeight = 0, double RasterizationScale = 1,
    double ViewportHeight = 0, double ContentHeight = 0, double ScrollableHeight = 0, double VerticalOffset = 0,
    string? ScrollBarPolicy = null, IReadOnlyList<string>? FullyVisibleDeviceIds = null,
    string? BackdropType = null, bool? NativeDarkMode = null,
    uint? NativeBorderColor = null, int NativeBorderColorResult = 0,
    int NativeBorderColorQueryResult = 0, uint RequestedBorderColor = 0,
    bool FrameSubclassAttached = false, bool FrameChangeApplied = false, int? FrameChangeError = null,
    RefreshMotionState? RefreshMotion = null, double ContentOffsetY = 0, double ContentOpacity = 1,
    IReadOnlyList<PanelActivationEvent>? ActivationEvents = null);

internal sealed record PanelActivationEvent(string Event, int Opening, double ElapsedMilliseconds,
    bool PanelVisible, bool ForegroundIsPanel, bool ForegroundIsThisProcess,
    string? ForegroundProcessName, bool? ForegroundRequestSucceeded = null);

// One pre-created native window. Toggle only positions and renders the cached display snapshot.
internal sealed class TrayPanel : Window
{
    // Windows 11 system flyouts use acrylic. Set to false to fall back to the solid background
    // if the local animation and first-frame measurements show any stutter.
    private static readonly bool PreferAcrylic = true;
    private readonly Grid _root = new();
    private readonly StackPanel _rows = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly Grid _footer = new() { Padding = new Thickness(4), ColumnSpacing = 0, BorderThickness = new Thickness(0, 1, 0, 0) };
    private readonly bool _acrylic;
    private readonly Dictionary<string, PanelRow> _byId = new(StringComparer.Ordinal);
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty = new() { Text = UiText.Get("Empty"), FontSize = 13, Margin = new Thickness(20), Opacity = .8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _refresh;
    private readonly RefreshSpin _refreshSpin;
    private readonly TrayIcon _tray;
    private readonly ThemeCoordinator _theme;
    private readonly nint _handle;
    private readonly NativeMethods.SubclassProc _frameProcedure;
    private readonly bool _frameRemoved;
    private readonly bool _frameChangeApplied;
    private readonly int? _frameChangeError;
    private readonly Stopwatch _opening = new();
    private readonly List<double> _renderingTimes = [];
    private readonly bool _recordActivation;
    private readonly Stopwatch _activationClock = Stopwatch.StartNew();
    private readonly List<PanelActivationEvent> _activationEvents = [];
    private int _activationOpening;
    private Storyboard? _animation;
    // Windows 11 flyouts rise out of the taskbar. The content rises 16 DIP into place on the 333 ms
    // decelerate curve while it fades in; closing fades out while it sinks 8 DIP.
    private const double RiseDistance = 16;
    private readonly TranslateTransform _rise = new();
    private double _riseSign = 1;
    private int _transition;
    private bool _visible;
    private bool _closing;
    private bool _renderSubscribed;
    private double? _firstRendering;
    private double? _maximumGap;
    private long _trayDismissal;
    private RectInt32 _bounds;
    private int _nativeBorderColorResult;
    private uint _requestedBorderColor;
    public FrameworkElement ThemeRoot => _root;
    internal ScrollViewer DeviceScrollForTest => _scroll;

    public TrayPanel(TrayIcon tray, ThemeCoordinator theme, Action openMain, Action settings, Action refresh, bool scenario)
    {
        _tray = tray;
        _theme = theme;
        _recordActivation = scenario;
        Title = scenario ? "Battery Manager · 演示数据" : "Battery Manager";
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var rounded = 2; // DWMWCP_ROUND; desktop window corners are owned by DWM.
        _ = PanelNative.DwmSetWindowAttribute(_handle, 33, ref rounded, sizeof(int));
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        // The borderless presenter still leaves a thin native frame (measured 6 px at 150%), and Windows
        // paints its inner edge light: that is the bright outline around the panel. Make the client area
        // cover the whole window, so XAML content reaches every edge. DWM keeps the rounded corners,
        // its own 1 px border (attribute 34 below) and the shadow.
        _frameProcedure = FrameProcedure;
        _frameRemoved = NativeMethods.SetWindowSubclass(_handle, _frameProcedure, 42, 0);
        if (_frameRemoved)
        {
            _frameChangeApplied = PanelNative.SetWindowPos(_handle, 0, 0, 0, 0, 0, PanelNative.SwpFrameChanged | PanelNative.SwpNoMove | PanelNative.SwpNoSize |
                PanelNative.SwpNoZOrder | PanelNative.SwpNoActivate);
            if (!_frameChangeApplied) _frameChangeError = Marshal.GetLastWin32Error();
        }
        _acrylic = PreferAcrylic && TryUseAcrylic();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (scenario) _root.Children.Add(new TextBlock { Text = "演示数据", FontSize = 12, Opacity = .7, Margin = new Thickness(16, 8, 16, 4) });
        _scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_scroll, 1);
        _root.Children.Add(_scroll);
        Grid.SetRow(_empty, 1);
        _root.Children.Add(_empty);
        // Footer like the Quick Settings footer: one quiet band, subtle buttons only.
        // 4 + 32 + 4 + 1 border keeps the height inside PanelPlacement's 50 DIP allowance.
        var footer = _footer;
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mainBody = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var appIcon = AppIcon.CreateImage();
        appIcon.VerticalAlignment = VerticalAlignment.Center;
        mainBody.Children.Add(appIcon);
        // Tight line bounds centre the letters on the icon; a full line box sits them about 1.5 px low.
        mainBody.Children.Add(new TextBlock { Text = "Battery Manager", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight });
        var main = UiText.MakeSubtle(new Button { Content = mainBody, Height = 32, Padding = new Thickness(8, 0, 10, 0), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left });
        AutomationProperties.SetName(main, UiText.Get("OpenMain"));
        ToolTipService.SetToolTip(main, UiText.Get("OpenMain"));
        main.Click += (_, _) => { HidePanel(); openMain(); };
        _refresh = FooterIcon("\uE72C", "Refresh");
        _refreshSpin = new RefreshSpin((FrameworkElement)_refresh.Content); // same turn as the main window's refresh
        _refresh.Click += (_, _) => refresh();
        var gear = FooterIcon("\uE713", "Settings");
        gear.Click += (_, _) => { HidePanel(); settings(); };
        footer.Children.Add(main);
        Grid.SetColumn(_refresh, 1); footer.Children.Add(_refresh);
        Grid.SetColumn(gear, 2); footer.Children.Add(gear);
        Grid.SetRow(footer, 2); _root.Children.Add(footer);
        _root.RenderTransform = _rise;
        Content = _root;
        // Handle row navigation before ScrollViewer consumes Up/Down for scrolling.
        _root.PreviewKeyDown += OnKey;
        _scroll.LayoutUpdated += (_, _) => UpdateScrollPolicy();
        Activated += (_, e) =>
        {
            RecordActivation("Activated:" + e.WindowActivationState);
            if (e.WindowActivationState != WindowActivationState.Deactivated || !_visible) return;
            NativeMethods.GetCursorPos(out var cursor);
            if (PanelNative.GetAsyncKeyState(1) < 0 && _tray.TryGetBounds(out var anchor)
                && cursor.X >= anchor.Left && cursor.X < anchor.Right && cursor.Y >= anchor.Top && cursor.Y < anchor.Bottom)
                _trayDismissal = Stopwatch.GetTimestamp();
            HidePanel();
        };
        AppWindow.Closing += (_, e) => { if (!_closing) { e.Cancel = true; HidePanel(); } };
        ApplyTheme();
    }

    public void Apply(IReadOnlyList<DeviceDisplay> devices, SessionSnapshot session)
    {
        devices = DeviceDisplayOrder.Sort(devices); // same order as the main window
        var ids = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _byId.Keys.Where(id => !ids.Contains(id)).ToArray())
        { _rows.Children.Remove(_byId[id].Root); _byId.Remove(id); }
        foreach (var device in devices)
        {
            if (!_byId.TryGetValue(device.Id, out var row))
            { row = new PanelRow(); _byId.Add(device.Id, row); _rows.Children.Add(row.Root); }
            row.Apply(device, _theme.WindowsDark, _theme.Accent);
        }
        for (var index = 0; index < devices.Count; index++)
        {
            var root = _byId[devices[index].Id].Root;
            var current = _rows.Children.IndexOf(root);
            if (current == index) continue;
            _rows.Children.RemoveAt(current);
            _rows.Children.Insert(index, root);
        }
        _empty.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateScrollPolicy();
        _refresh.IsEnabled = !session.IsRefreshing && !session.IsSuspended && session.Status != "Resuming" && session.OutstandingOperations == 0;
        if (!_closing) _refreshSpin.Set(session.IsRefreshing);
        if (_visible) Position();
    }

    public void ApplyTheme()
    {
        _root.RequestedTheme = _theme.TrayTheme;
        var dark = _theme.WindowsDark;
        // DWMWA_USE_IMMERSIVE_DARK_MODE: without it Windows draws the light-mode window border,
        // which shows as a bright outline around the dark panel.
        var immersiveDark = dark ? 1 : 0;
        _ = PanelNative.DwmSetWindowAttribute(_handle, 20, ref immersiveDark, sizeof(int));
        // A borderless acrylic window can retain a light DWM outline even with attribute 20.
        // Reuse the existing dark fallback color; restore the system default in light mode.
        var borderColor = dark ? 0x002C2C2C : unchecked((int)0xFFFFFFFF);
        _requestedBorderColor = unchecked((uint)borderColor);
        _nativeBorderColorResult = PanelNative.DwmSetWindowAttribute(_handle, 34, ref borderColor, sizeof(int));
        _root.Background = _acrylic ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            : dark ? UiText.Brush(44, 44, 44) : UiText.Brush(249, 249, 249);
        _footer.Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x1F, 0, 0, 0) : Windows.UI.Color.FromArgb(0x08, 0, 0, 0));
        _footer.BorderBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x33, 0, 0, 0) : Windows.UI.Color.FromArgb(0x0F, 0, 0, 0));
        foreach (var row in _byId.Values) if (row.Device is { } device) row.Apply(device, _theme.WindowsDark, _theme.Accent);
    }

    private void UpdateScrollPolicy()
    {
        // Normal one-to-three-row panels fit fully. A short work area can still constrain
        // the client area, so retain scrolling instead of hiding inaccessible devices.
        var desired = _byId.Count > PanelPlacement.MaxVisibleRows || _scroll.ScrollableHeight > 0.01
            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        if (_scroll.VerticalScrollBarVisibility != desired) _scroll.VerticalScrollBarVisibility = desired;
    }

    public void Toggle(bool keyboard = false) { if (_visible) HidePanel(); else ShowPanel(keyboard); }
    public void ToggleFromTray(bool keyboard = false)
    {
        // Mouse-down on the notification icon deactivates this window before its
        // mouse-up activation callback. Consume that same click instead of reopening.
        var dismissed = _trayDismissal;
        _trayDismissal = 0;
        if (!keyboard && dismissed != 0 && Stopwatch.GetElapsedTime(dismissed) < TimeSpan.FromSeconds(2)) return;
        Toggle(keyboard);
    }
    public void ShowPanel(bool keyboard = false)
    {
        ++_activationOpening;
        ++_transition;
        _animation?.Stop();
        Position();
        _visible = true;
        _firstRendering = null;
        _maximumGap = null;
        _renderingTimes.Clear();
        _opening.Restart();
        if (!_renderSubscribed) { CompositionTarget.Rendering += OnRendering; _renderSubscribed = true; }
        _root.Opacity = 0;
        RecordActivation("BeforeShow");
        AppWindow.Show();
        RecordActivation("AfterShow");
        Activate();
        RecordActivation("AfterActivate");
        var foregroundRequested = NativeMethods.SetForegroundWindow(_handle);
        RecordActivation("SetForegroundWindow", foregroundRequested);
        Animate(true);
        DispatcherQueue.TryEnqueue(() =>
        {
            // The activation occurred in the shell, outside this XAML root. Carry its input kind
            // explicitly instead of depending on the last input seen by the application.
            if (_visible && _rows.Children.FirstOrDefault() is Control first)
                first.Focus(keyboard ? FocusState.Keyboard : FocusState.Pointer);
        });
    }

    public void HidePanel(bool restoreTrayFocus = false)
    {
        if (!_visible) return;
        RecordActivation("HideRequested");
        _visible = false;
        StopMeasurement();
        var transition = ++_transition;
        Animate(false, () =>
        {
            if (transition != _transition) return;
            AppWindow.Hide();
            if (restoreTrayFocus) _tray.RestoreFocus();
        });
    }

    private void Animate(bool show, Action? finished = null)
    {
        var motion = Motion.TestEnabled ?? _theme.AnimationsEnabled;
        var from = _root.Opacity;
        var offset = _rise.Y;
        var to = show ? 1.0 : 0.0;
        _animation?.Stop();
        _root.Opacity = from;
        _rise.Y = motion && !show ? offset : 0;
        var fade = new DoubleAnimation
        {
            From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(motion ? (show ? 167 : 150) : 80)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var storyboard = motion
            ? Motion.Run((_root, "Opacity", fade), (_rise, "Y", show
                ? Motion.Decelerate(_riseSign * RiseDistance, 0, 333)
                : Motion.Accelerate(offset, _riseSign * RiseDistance / 2, 150)))
            : Motion.Run((_root, "Opacity", fade));
        storyboard.Completed += (_, _) => { _root.Opacity = to; _rise.Y = 0; finished?.Invoke(); };
        _animation = storyboard;
        storyboard.Begin();
    }

    private void RecordActivation(string stage, bool? foregroundRequested = null)
    {
        // Scenario-only evidence: bounded in memory, with no titles, paths, or process IDs.
        // Elapsed time is monotonic from this window's creation; Opening groups show attempts.
        if (!_recordActivation) return;
        var foreground = PanelNative.GetForegroundWindow();
        uint processId = 0;
        if (foreground != 0) NativeMethods.GetWindowThreadProcessId(foreground, out processId);
        string? processName = null;
        if (processId != 0)
        {
            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                processName = process.ProcessName;
                if (processName.Length > 64) processName = processName[..64];
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The foreground process can exit or deny this read during an activation change.
            }
        }
        if (_activationEvents.Count == 16) _activationEvents.RemoveAt(0);
        _activationEvents.Add(new(stage, _activationOpening, _activationClock.Elapsed.TotalMilliseconds,
            _visible, foreground == _handle, processId == (uint)Environment.ProcessId, processName, foregroundRequested));
    }

    private void OnRendering(object? sender, object e)
    {
        var elapsed = _opening.Elapsed.TotalMilliseconds;
        _firstRendering ??= elapsed;
        if (_renderingTimes.Count > 0) _maximumGap = Math.Max(_maximumGap ?? 0, elapsed - _renderingTimes[^1]);
        _renderingTimes.Add(elapsed);
        if (elapsed >= 350) StopMeasurement();
    }
    private void StopMeasurement()
    {
        if (!_renderSubscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderSubscribed = false;
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { HidePanel(true); e.Handled = true; return; }
        if (e.Key is not (VirtualKey.Down or VirtualKey.Up)) return;
        var focused = FocusManager.GetFocusedElement(_root.XamlRoot);
        var index = _rows.Children.IndexOf(focused as UIElement);
        if (index < 0 || _rows.Children.Count == 0) return;
        var next = Math.Clamp(index + (e.Key == VirtualKey.Down ? 1 : -1), 0, _rows.Children.Count - 1);
        if (_rows.Children[next] is Control target) { target.Focus(FocusState.Keyboard); target.StartBringIntoView(); e.Handled = true; }
    }

    private bool TryUseAcrylic()
    {
        try
        {
            if (!Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported()) return false;
            // When Windows transparency effects are off, the system backdrop shows its solid fallback itself.
            SystemBackdrop = new DesktopAcrylicBackdrop();
            return true;
        }
        catch (Exception) { return false; }
    }

    private static Button FooterIcon(string glyph, string key)
    {
        var button = UiText.MakeSubtle(new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(4) });
        AutomationProperties.SetName(button, UiText.Get(key));
        ToolTipService.SetToolTip(button, UiText.Get(key));
        return button;
    }

    private void Position()
    {
        if (!_tray.TryGetBounds(out var anchor))
        { NativeMethods.GetCursorPos(out var point); anchor = new(point.X, point.Y, point.X + 1, point.Y + 1); }
        var monitor = PanelNative.MonitorFromRect(ref anchor, 2);
        var info = new PanelNative.MonitorInfo { Size = (uint)Marshal.SizeOf<PanelNative.MonitorInfo>() };
        if (!PanelNative.GetMonitorInfo(monitor, ref info)) return;
        var scale = 1.0;
        if (PanelNative.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0) scale = dpi / 96d;
        var work = info.Work;
        if (work.Right <= work.Left || work.Bottom <= work.Top) return;
        // Any non-client frame takes height from the client area; add it so the rows fit exactly.
        var frame = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        for (var pass = 0; pass < 2; pass++)
        {
            var positioned = PanelPlacement.Calculate(new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top),
                new(anchor.Left, anchor.Top, anchor.Right - anchor.Left, anchor.Bottom - anchor.Top), scale, _byId.Count, Title.Contains("演示数据"), frame);
            _bounds = new(positioned.X, positioned.Y, positioned.Width, positioned.Height);
            _riseSign = positioned.Y + positioned.Height <= anchor.Top + 1 ? 1 : -1; // rise up from a bottom taskbar
            AppWindow.MoveAndResize(_bounds);
            // Moving to another monitor can change the native frame at its DPI.
            // Correct once using the frame measured on the target monitor.
            var movedFrame = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
            if (movedFrame == frame) break;
            frame = movedFrame;
        }
    }

    internal void ScrollForTest(bool bottom) => _scroll.ChangeView(null, bottom ? _scroll.ScrollableHeight : 0, null, true);

    public AppTestPanel Capture()
    {
        var rows = _rows.Children.Select(element => _byId.First(pair => ReferenceEquals(pair.Value.Root, element))).ToArray();
        var focused = _root.XamlRoot is { } xamlRoot ? FocusManager.GetFocusedElement(xamlRoot) as Control : null;
        var focusedRowId = focused is null ? null : rows.FirstOrDefault(pair => ReferenceEquals(pair.Value.Root, focused)).Key;
        var visible = rows.Where(pair =>
        {
            var row = pair.Value.Root;
            if (row.ActualHeight <= 0 || _scroll.ViewportHeight <= 0) return false;
            var bounds = row.TransformToVisual(_scroll).TransformBounds(new Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
            return bounds.Top >= -0.01 && bounds.Bottom <= _scroll.ViewportHeight + 0.01;
        }).Select(pair => pair.Key).ToArray();
        var darkResult = PanelNative.DwmGetWindowAttribute(_handle, 20, out var nativeDark, sizeof(int));
        var borderResult = PanelNative.DwmGetWindowAttribute(_handle, 34, out var nativeBorder, sizeof(int));
        return new(_visible, _root.RequestedTheme.ToString(), rows.Length,
            rows.Select(pair => pair.Value.Capture(pair.Key)).ToArray(), _firstRendering, _maximumGap, _renderingTimes.Count,
            _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
            FocusedDeviceId: focusedRowId, FocusState: focused?.FocusState.ToString(),
            WindowHeight: AppWindow.Size.Height, ClientHeight: AppWindow.ClientSize.Height,
            RasterizationScale: _root.XamlRoot?.RasterizationScale ?? 1,
            ViewportHeight: _scroll.ViewportHeight, ContentHeight: _scroll.ExtentHeight,
            ScrollableHeight: _scroll.ScrollableHeight, VerticalOffset: _scroll.VerticalOffset,
            ScrollBarPolicy: _scroll.VerticalScrollBarVisibility.ToString(), FullyVisibleDeviceIds: visible,
            BackdropType: SystemBackdrop?.GetType().Name, NativeDarkMode: darkResult == 0 ? nativeDark != 0 : null,
            NativeBorderColor: borderResult == 0 ? unchecked((uint)nativeBorder) : null,
            NativeBorderColorResult: _nativeBorderColorResult,
            NativeBorderColorQueryResult: borderResult, RequestedBorderColor: _requestedBorderColor,
            FrameSubclassAttached: _frameRemoved, FrameChangeApplied: _frameChangeApplied, FrameChangeError: _frameChangeError,
            RefreshMotion: _refreshSpin.Capture(), ContentOffsetY: _rise.Y, ContentOpacity: _root.Opacity,
            ActivationEvents: _recordActivation ? _activationEvents.ToArray() : []);
    }
    public void CloseForExit()
    {
        _closing = true; ++_transition; StopMeasurement(); _animation?.Stop(); _refreshSpin.Stop();
        if (_frameRemoved) NativeMethods.RemoveWindowSubclass(_handle, _frameProcedure, 42);
        Close();
    }

    private nint FrameProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        const uint NcCalcSize = 0x0083, NcHitTest = 0x0084;
        const nint HitClient = 1;
        // wParam TRUE: returning 0 keeps the proposed rectangle, so the client equals the window rectangle.
        if (message == NcCalcSize && wParam != 0) return 0;
        // No resize edges: the panel is never resized by the user.
        if (message == NcHitTest) return HitClient;
        return NativeMethods.DefSubclassProc(window, message, wParam, lParam);
    }

    private sealed class PanelRow
    {
        // 4 DIP inset with 4 DIP corners: concentric inside the 8 DIP rounded panel.
        // One line per device: name on the left, the number and the battery glyph on the right. The glyphs
        // form one column at the right edge; every state is drawn on the glyph, so the number never moves.
        public Button Root { get; } = UiText.MakeSubtle(new Button { Height = PanelPlacement.RowHeight, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(4) });
        private readonly FontIcon _icon = new() { FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name = new() { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
        private readonly Run _value = new() { FontSize = 20, FontWeight = FontWeights.SemiBold };
        private readonly Run _percent = new() { FontSize = 12, FontWeight = FontWeights.Normal, Text = "%" };
        // Tight line bounds centre the digits themselves on the battery glyph, not the full line box.
        private readonly TextBlock _number = new() { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, TextLineBounds = TextLineBounds.Tight };
        private readonly ValueDash _dash = new(20, 12) { Visibility = Visibility.Collapsed };
        private readonly TextBlock _pending = new() { Text = UiText.Get("NoResponse"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly BatteryGlyph _glyph = new(26) { VerticalAlignment = VerticalAlignment.Center };
        private readonly Grid _layout = new() { ColumnSpacing = 14 };
        public DeviceDisplay? Device { get; private set; }
        public PanelRow()
        {
            _number.Inlines.Add(_value); _number.Inlines.Add(_percent);
            var grid = _layout;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(_icon);
            Grid.SetColumn(_name, 1); grid.Children.Add(_name);
            // Express the 8 DIP gap directly. Subtracting a -6 margin from a 14 DIP column gap
            // rounds those two terms independently at 125%, adding a physical pixel to some rows.
            var valueCell = new Grid { VerticalAlignment = VerticalAlignment.Center };
            valueCell.Children.Add(_number); valueCell.Children.Add(_dash); valueCell.Children.Add(_pending);
            var reading = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            reading.Children.Add(valueCell); reading.Children.Add(_glyph);
            Grid.SetColumn(reading, 2); grid.Children.Add(reading);
            Root.Content = grid;
        }
        public void Apply(DeviceDisplay device, bool dark, Windows.UI.Color accent)
        {
            Device = device;
            var noResponse = device.Presence == DisplayPresence.NoResponse;
            var value = device.Battery.Value;
            _name.Text = device.Name;
            _icon.Glyph = UiText.Icon(device.Kind);
            _icon.Opacity = noResponse ? .45 : 1;
            // "—%" stays in the text, transparent, for its width; the drawn dash (ValueDash) shows centred on the glyph.
            _value.Text = value is { } shown ? shown.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";
            var dash = !noResponse && value is null;
            _dash.Visibility = dash ? Visibility.Visible : Visibility.Collapsed;
            if (dash) _value.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            else _value.ClearValue(TextElement.ForegroundProperty);
            var ink = dark ? UiText.Brush(242, 242, 242) : UiText.Brush(32, 32, 32);
            var muted = dark ? UiText.Brush(173, 173, 173) : UiText.Brush(100, 100, 100);
            var low = dark ? UiText.Brush(255, 180, 84) : UiText.Brush(157, 71, 0);
            var quiet = value is null || device.Battery.Tone == BatteryTone.Stale;
            _number.Foreground = quiet ? muted : ink;
            _percent.Foreground = value is null ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : muted;
            _number.Visibility = noResponse ? Visibility.Collapsed : Visibility.Visible;
            _pending.Visibility = noResponse ? Visibility.Visible : Visibility.Collapsed;
            _pending.Foreground = muted;
            _dash.Paint(muted);
            _name.Foreground = _icon.Foreground = ink;
            // Low battery colours the glyph only; the number keeps its colour.
            _glyph.Show(noResponse ? BatteryGlyphState.NoResponse : value is null ? BatteryGlyphState.NoReading : BatteryGlyphState.Level, value, device.Battery.Charging);
            _glyph.Paint(noResponse || quiet ? muted : device.Battery.Low ? low : ink);
            var spoken = noResponse ? _pending.Text : value is null ? "—" : _value.Text + "%";
            AutomationProperties.SetName(Root, device.Name + " " + spoken + (device.Battery.Charging && !noResponse ? " " + UiText.Get("Charging") : ""));
        }
        public AppTestCard Capture(string id) => new(id, _name.Text, _pending.Visibility == Visibility.Visible ? _pending.Text : "",
            Device?.Battery.Value is null || _pending.Visibility == Visibility.Visible ? "—" : _value.Text + "%", "", Device?.Battery.Tone.ToString(),
            Charging: _glyph.Charging, HeaderHeight: Root.ActualHeight,
            BatteryColor: (_number.Foreground as SolidColorBrush)?.Color.ToString(), BarVisible: false, BarValue: 0,
            BatteryGlyph: _glyph.Describe(), GlyphSteps: _glyph.Steps, GlyphLayout: CaptureGlyphLayout(),
            BatteryGlyphColor: _glyph.InkColor, PercentColor: (_percent.Foreground as SolidColorBrush)?.Color.ToString(),
            GlyphPixelLayout: _glyph.PixelLayout, GlyphRootBounds: _glyph.CaptureRootBounds(), MarkLayout: _glyph.MarkLayout,
            ValueDashVisible: _dash.Visibility == Visibility.Visible, ValueDashLayout: _dash.Describe(),
            ValueDashRootBounds: _dash.CaptureRootBounds(), ValueDashColor: _dash.InkColor);

        private AppTestGlyphLayout? CaptureGlyphLayout()
        {
            if (Root.XamlRoot is null || Root.ActualWidth <= 0 || Root.ActualHeight <= 0) return null;
            Windows.Foundation.Rect Bounds(FrameworkElement element) =>
                element.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var valueElement = _pending.Visibility == Visibility.Visible ? _pending : _number;
            var value = Bounds(valueElement);
            var glyph = Bounds(_glyph);
            var valueParent = VisualTreeHelper.GetParent(valueElement) as UIElement;
            var glyphParent = VisualTreeHelper.GetParent(_glyph) as UIElement;
            // Keep the framework-assigned slots (including margins) distinct from actual control bounds.
            var valueSlot = valueParent?.TransformToVisual(Root).TransformBounds(
                Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(valueElement)) ?? default;
            var glyphSlot = glyphParent?.TransformToVisual(Root).TransformBounds(
                Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(_glyph)) ?? default;
            return new(value.Left, value.Right, value.Top, value.Bottom, glyph.Left, glyph.Right, glyph.Top, glyph.Bottom,
                Root.ActualWidth, Root.ActualHeight, valueElement.DesiredSize.Width, valueElement.ActualWidth, _glyph.ActualWidth,
                Root.XamlRoot.RasterizationScale, valueSlot.Left, valueSlot.Right, glyphSlot.Left, glyphSlot.Right,
                valueParent is not null && glyphParent is not null);
        }
    }
}

internal static class PanelNative
{
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public uint Size; public NativeMethods.Rect Monitor; public NativeMethods.Rect Work; public uint Flags; }
    [DllImport("user32.dll")] internal static extern nint MonitorFromRect(ref NativeMethods.Rect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    internal const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
