using System.Diagnostics;
using BatteryManager.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace BatteryManager.App;

// Geometry follows the Windows 11 settings expander (WinUI Community Toolkit SettingsExpander):
// a fixed 68-high header, the icon 18 from the edge and 20 before the text (text starts at 58),
// 4 right padding with 32x32 "more options" and chevron targets. Expanded items are at least 52 high,
// indented 58 to line up with the name, and their values line up with the battery column.
internal sealed class DeviceCard : UserControl
{
    private const double HeaderHeight = 68;
    private const double TrailingWidth = 80; // 8 gap + 32 more options + 4 gap + 32 chevron + 4 padding
    private readonly Border _frame = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    private readonly Grid _header = new() { Height = HeaderHeight, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Button _expand = UiText.MakeSubtle(new Button
    {
        Padding = new Thickness(18, 0, TrailingWidth, 0), CornerRadius = new CornerRadius(3),
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center
    });
    private readonly Button _more = UiText.MakeSubtle(new Button
    {
        Content = new FontIcon { Glyph = "", FontSize = 16 }, Width = 32, Height = 32, Padding = new Thickness(0),
        Margin = new Thickness(8, 0, 4, 0), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center
    });
    private readonly FontIcon _icon = new() { FontSize = 20, Width = 20, Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _warning = new() { Glyph = "", FontSize = 16, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _chevron = new() { Glyph = "", FontSize = 12, Width = 32, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _name = new() { FontSize = 14, LineHeight = 20, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    // The number is large and the percent sign small; both share one baseline.
    private readonly Run _value = new() { FontSize = 20, FontWeight = FontWeights.SemiBold };
    private readonly Run _percent = new() { FontSize = 12, FontWeight = FontWeights.Normal, Text = "%" };
    // Tight line bounds: the text box runs from the digits' cap height to their baseline, so centring it centres
    // the digits themselves on the battery glyph (a full line box would put them 2 px low at 125 %).
    private readonly TextBlock _battery = new() { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
    private readonly ValueDash _dash = new(20, 12) { Visibility = Visibility.Collapsed };
    private readonly TextBlock _noResponse = new() { FontSize = 12, Text = UiText.Get("NoResponse"), Visibility = Visibility.Collapsed, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    // Same order and glyph as the tray panel: the number, then the battery glyph at the right edge.
    private readonly BatteryGlyph _glyph = new(26) { VerticalAlignment = VerticalAlignment.Center };
    internal FrameworkElement GlyphForTest => _glyph;
    internal void ShowMenuForTest(bool show)
    {
        if (show) _more.Flyout?.ShowAt(_more);
        else _more.Flyout?.Hide();
    }
    private readonly StackPanel _tags = new() { Orientation = Orientation.Horizontal, Spacing = 6, Height = 18 };
    private readonly StackPanel _details = new();
    // Expand like the Windows 11 expander: the card grows, the items slide down from under the header
    // and the chevron turns, all on the 333 ms decelerate curve (collapse: 200 ms). A click during the
    // motion reverses it from where it is.
    private const double OpenMilliseconds = 333, CloseMilliseconds = 200;
    private readonly RevealPanel _reveal = new() { Progress = 0, Visibility = Visibility.Collapsed };
    private readonly RotateTransform _turn = new();
    private readonly Stopwatch _clock = new();
    private double _from, _to, _duration;
    private bool _tweening;
    private readonly List<TextBlock> _detailTexts = [];
    private readonly List<Action> _detailPaint = [];
    private readonly List<SourceRow> _sourceRows = [];
    private readonly List<(Border Border, TextBlock Text, FontIcon? Glyph)> _tagParts = [];
    private DeviceDisplay _device;
    private bool _merged;
    private bool _expanded;
    private string _detailsSignature = "";
    private readonly MenuFlyoutItem _unmerge = new() { Text = UiText.Get("Unmerge"), Icon = new FontIcon { Glyph = "\uE711" } };
    public Action<DeviceDisplay>? RenameRequested { get; set; }
    public Action<DeviceDisplay>? TypeRequested { get; set; }
    public Action<DeviceDisplay>? MergeRequested { get; set; }
    public Action<DeviceDisplay>? UnmergeRequested { get; set; }

    public DeviceCard(DeviceDisplay device, bool merged = false)
    {
        _device = device;
        _merged = merged;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _battery.Inlines.Add(_value);
        _battery.Inlines.Add(_percent);

        var heading = new Grid();
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto, MinWidth = 84 });
        heading.Children.Add(_icon);

        var description = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var nameRow = new Grid { ColumnSpacing = 6, Height = 22 };
        nameRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        nameRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        nameRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.SizeChanged += (_, _) => _name.MaxWidth = Math.Max(20, nameRow.ActualWidth - 22);
        nameRow.Children.Add(_name);
        Grid.SetColumn(_warning, 1); nameRow.Children.Add(_warning);
        ToolTipService.SetToolTip(_warning, UiText.Get("ReadFailed"));
        AutomationProperties.SetName(_warning, UiText.Get("ReadFailed"));
        description.Children.Add(nameRow);
        description.Children.Add(_tags);
        Grid.SetColumn(description, 1); heading.Children.Add(description);

        // The number and "暂无响应" share one cell, so the 8 DIP gap to the glyph never depends on which is shown.
        var batteryArea = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var valueCell = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _battery.HorizontalAlignment = HorizontalAlignment.Right;
        valueCell.Children.Add(_battery); valueCell.Children.Add(_dash); valueCell.Children.Add(_noResponse);
        batteryArea.Children.Add(valueCell); batteryArea.Children.Add(_glyph);
        Grid.SetColumn(batteryArea, 2); heading.Children.Add(batteryArea);

        // The expand button fills the header; "more options" and the chevron sit on top of its
        // right padding. The chevron ignores input, so clicks there still expand the card.
        _header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new() { Width = new GridLength(32) });
        _header.ColumnDefinitions.Add(new() { Width = new GridLength(4) });
        _expand.Content = heading;
        _expand.Click += (_, _) => SetExpanded(!_expanded, animate: true);
        Grid.SetColumnSpan(_expand, 4); _header.Children.Add(_expand);
        Grid.SetColumn(_more, 1); _header.Children.Add(_more);
        Grid.SetColumn(_chevron, 2); _header.Children.Add(_chevron);
        _chevron.RenderTransformOrigin = new Windows.Foundation.Point(.5, .5);
        _chevron.RenderTransform = _turn;

        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = UiText.Get("Rename"), Icon = new FontIcon { Glyph = "" } };
        var type = new MenuFlyoutItem { Text = UiText.Get("ChangeType"), Icon = new FontIcon { Glyph = "" } };
        rename.Click += (_, _) => RenameRequested?.Invoke(_device);
        type.Click += (_, _) => TypeRequested?.Invoke(_device);
        menu.Items.Add(rename); menu.Items.Add(type);
        // Merging sits apart from the per-card settings: "合并连接…" always, "取消合并" once the card is merged.
        var merge = new MenuFlyoutItem { Text = UiText.Get("MergeConnections"), Icon = new FontIcon { Glyph = "\uE71B" } };
        merge.Click += (_, _) => MergeRequested?.Invoke(_device);
        _unmerge.Click += (_, _) => UnmergeRequested?.Invoke(_device);
        menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(merge); menu.Items.Add(_unmerge);
        _more.Flyout = menu; ContextFlyout = menu;
        ToolTipService.SetToolTip(_more, UiText.Get("More")); AutomationProperties.SetName(_more, UiText.Get("More"));

        var body = new StackPanel();
        _reveal.Children.Add(_details);
        body.Children.Add(_header); body.Children.Add(_reveal);
        _frame.Child = body; Content = _frame;
        ActualThemeChanged += (_, _) => RefreshTheme();
        Unloaded += (_, _) => StopMotion();
        Update(device, merged);
    }

    public bool Merged => _merged;
    // A merged card always has its connection rows to show, even before any battery source has reported.
    private bool Expandable => _device.Expandable || (_merged && _device.Connections.Count > 0);

    public void Update(DeviceDisplay device, bool merged)
    {
        _device = device;
        _merged = merged;
        _unmerge.Visibility = merged ? Visibility.Visible : Visibility.Collapsed;
        _name.Text = device.Name;
        ToolTipService.SetToolTip(_name, device.Name);
        AutomationProperties.SetName(_expand, device.Name);
        _icon.Glyph = UiText.Icon(device.Kind);
        _icon.Opacity = device.Presence == DisplayPresence.NoResponse ? .45 : 1;
        _warning.Visibility = device.ReadFailed ? Visibility.Visible : Visibility.Collapsed;
        var noResponse = device.Presence == DisplayPresence.NoResponse;
        // "—" keeps a transparent % after it, so it lines up with the digits of the other cards.
        _value.Text = device.Battery.Value is { } shown ? shown.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";
        _battery.Visibility = noResponse ? Visibility.Collapsed : Visibility.Visible;
        // No value: the text keeps "—%" for its width, both transparent, and the drawn dash shows instead.
        var dash = !noResponse && device.Battery.Value is null;
        _dash.Visibility = dash ? Visibility.Visible : Visibility.Collapsed;
        if (dash) _value.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        else _value.ClearValue(TextElement.ForegroundProperty);
        _noResponse.Visibility = noResponse ? Visibility.Visible : Visibility.Collapsed;
        _glyph.Show(noResponse ? BatteryGlyphState.NoResponse : device.Battery.Value is null ? BatteryGlyphState.NoReading : BatteryGlyphState.Level,
            device.Battery.Value, device.Battery.Charging);
        // A card with nothing to expand shows no chevron and no hover; its menu stays available.
        _chevron.Opacity = Expandable ? 1 : 0;
        _expand.IsTabStop = Expandable;
        _expand.IsHitTestVisible = Expandable;
        _tags.Children.Clear();
        // These parts belong only to header tags. WinUI may not have attached a
        // newly added Border to its parent until layout; ownership is explicit.
        _tagParts.Clear();
        // A merged card names every connection method it has, in a fixed order and one style; which one
        // is in use is shown in the expanded rows, so the tags never jump around when the device switches.
        if (merged && device.Connections.Count > 0)
            foreach (var connection in device.Connections)
                _tags.Children.Add(MakeTag(UiText.Transport(connection.Transport), UiText.TransportIcon(connection.Transport)));
        else if (device.Transport != DeviceTransport.Unknown) _tags.Children.Add(MakeTag(UiText.Transport(device.Transport), UiText.TransportIcon(device.Transport)));
        if (device.Source is not null) _tags.Children.Add(MakeTag(UiText.Source(device.Source)));
        var signature = System.Text.Json.JsonSerializer.Serialize(new { device.Sources, device.Parts, Merged = merged, device.Connections });
        if (_detailsSignature != signature)
        {
            _detailsSignature = signature;
            BuildDetails(device);
        }
        SetExpanded(_expanded); RefreshTheme();
    }

    public void SetExpanded(bool expanded, bool animate = false)
    {
        var target = expanded && Expandable;
        var changed = target != _expanded;
        _expanded = target;
        if (animate && changed && IsLoaded && Motion.Enabled()) { StartMotion(); return; }
        if (_tweening && !changed) return; // a refresh during the motion: let it finish
        Settle();
    }

    private void StartMotion()
    {
        _reveal.Visibility = Visibility.Visible;
        if (_expanded) _expand.CornerRadius = new CornerRadius(3, 3, 0, 0);
        _from = _reveal.Progress;
        _to = _expanded ? 1 : 0;
        _duration = Math.Max(83, (_expanded ? OpenMilliseconds : CloseMilliseconds) * Math.Abs(_to - _from));
        _clock.Restart();
        if (!_tweening) { CompositionTarget.Rendering += OnMotionFrame; _tweening = true; }
    }

    private void OnMotionFrame(object? sender, object e)
    {
        var time = Math.Min(1, _clock.Elapsed.TotalMilliseconds / _duration);
        var progress = _from + (_to - _from) * Motion.Decelerate(time);
        _reveal.Progress = progress;
        _turn.Angle = 180 * progress;
        if (time >= 1) Settle();
    }

    private void Settle()
    {
        if (_tweening) { CompositionTarget.Rendering -= OnMotionFrame; _tweening = false; }
        _reveal.Progress = _expanded ? 1 : 0;
        _reveal.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _turn.Angle = _expanded ? 180 : 0;
        _expand.CornerRadius = _expanded ? new CornerRadius(3, 3, 0, 0) : new CornerRadius(3);
    }

    internal void StopMotion() => Settle();

    // Expanded rows, one rule for every row: what it is on the left (name, then an optional grey description and
    // any long text such as an error), one short state on the right, lined up with the header's battery column.
    // Order: parts, then connection methods (merged cards only), then battery sources, then device information.
    private void BuildDetails(DeviceDisplay device)
    {
        _details.Children.Clear(); _detailTexts.Clear(); _detailPaint.Clear(); _sourceRows.Clear();
        // Parts are reserved by the display model. Production does not supply them yet.
        foreach (var part in device.Parts)
        {
            var battery = part.Battery;
            var value = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var number = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, TextLineBounds = TextLineBounds.Tight };
            var digits = new Run { FontSize = 14, FontWeight = FontWeights.SemiBold, Text = battery.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—" };
            var percent = new Run { FontSize = 11, Text = "%" };
            number.Inlines.Add(digits); number.Inlines.Add(percent);
            var numberCell = new Grid { VerticalAlignment = VerticalAlignment.Center };
            numberCell.Children.Add(number);
            var partDash = new ValueDash(14, 11) { Visibility = battery.Value is null ? Visibility.Visible : Visibility.Collapsed };
            if (battery.Value is null) digits.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            numberCell.Children.Add(partDash);
            var glyph = new BatteryGlyph(20) { VerticalAlignment = VerticalAlignment.Center };
            glyph.Show(battery.Value is null ? BatteryGlyphState.NoReading : BatteryGlyphState.Level, battery.Value, battery.Charging);
            value.Children.Add(numberCell); value.Children.Add(glyph);
            _detailPaint.Add(() =>
            {
                var quiet = battery.Value is null || battery.Tone == BatteryTone.Stale;
                number.Foreground = quiet ? UiText.MutedFor(this) : UiText.InkFor(this);
                percent.Foreground = battery.Value is null ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : UiText.MutedFor(this);
                partDash.Paint(UiText.MutedFor(this));
                glyph.Paint(quiet ? UiText.MutedFor(this) : battery.Low ? UiText.LowFor(this) : UiText.InkFor(this));
            });
            _details.Children.Add(Item(UiText.Get(part.Part.ToString()), null, value).Row);
        }
        if (_merged)
            foreach (var connection in device.Connections)
            {
                var state = Detail(UiText.Presence(connection.State), 13);
                _detailPaint.Add(() => state.Foreground = UiText.MutedFor(this));
                _details.Children.Add(Item(UiText.Transport(connection.Transport), UiText.Get("ConnectionMethod"), state).Row);
            }
        foreach (var source in device.Sources)
        {
            Grid? errorLine = null;
            TextBlock? error = null;
            HyperlinkButton? copy = null;
            if (!string.IsNullOrWhiteSpace(source.Error))
            {
                // Give the error a finite measure width so wrapping preserves the copy action and the status column.
                var line = new Grid { RowSpacing = 2, Margin = new Thickness(0, 3, 0, 0) };
                line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                line.RowDefinitions.Add(new() { Height = GridLength.Auto });
                line.RowDefinitions.Add(new() { Height = GridLength.Auto });
                error = Detail(source.Error, 12);
                error.FontFamily = UiText.Mono();
                error.IsTextSelectionEnabled = true;
                _detailPaint.Add(() => error.Foreground = UiText.MutedFor(this));
                copy = new HyperlinkButton { Content = UiText.Get("Copy"), FontSize = 12, Padding = new Thickness(0, 1, 0, 1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                AutomationProperties.SetName(copy, UiText.Get("Copy"));
                var text = source.Error;
                copy.Click += (_, _) =>
                {
                    try { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); }
                    catch { error.SelectAll(); }
                };
                line.Children.Add(error); Grid.SetRow(copy, 1); line.Children.Add(copy);
                errorLine = line;
            }
            var description = UiText.Get("BatterySource") + (source.Coarse ? UiText.Pick(" · ", " · ") + UiText.Get("Coarse") : "");
            var status = Result(source.Result);
            var row = Item(UiText.Source(source.Method), description, status, errorLine);
            _details.Children.Add(row.Row);
            _sourceRows.Add(new(source.Method, row.Row, row.Left, status, errorLine, error, copy));
        }
    }

    private (Border Row, StackPanel Left) Item(string label, string? description, FrameworkElement value, FrameworkElement? extra = null)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var key = Detail(label, 14);
        key.TextWrapping = TextWrapping.NoWrap;
        key.TextTrimming = TextTrimming.CharacterEllipsis;
        _detailPaint.Add(() => key.Foreground = UiText.InkFor(this));
        left.Children.Add(key);
        if (description is not null)
        {
            var note = Detail(description, 12);
            _detailPaint.Add(() => note.Foreground = UiText.MutedFor(this));
            left.Children.Add(note);
        }
        if (extra is not null) left.Children.Add(extra);
        grid.Children.Add(left);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 1); grid.Children.Add(value);
        return (new Border { Child = grid, MinHeight = 52, Padding = new Thickness(58, 8, TrailingWidth, 8), BorderThickness = new Thickness(0, 1, 0, 0) }, left);
    }

    private StackPanel Result(SourceDisplayResult result)
    {
        var glyph = result switch
        {
            SourceDisplayResult.Success => "", SourceDisplayResult.Failed => "",
            SourceDisplayResult.Unsupported => "", _ => ""
        };
        var icon = new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var text = Detail(UiText.Result(result), 13);
        _detailPaint.Add(() =>
        {
            icon.Foreground = result switch
            {
                SourceDisplayResult.Success => UiText.SuccessFor(this), SourceDisplayResult.Failed => UiText.LowFor(this), _ => UiText.MutedFor(this)
            };
            text.Foreground = result == SourceDisplayResult.Failed ? UiText.InkFor(this) : UiText.MutedFor(this);
        });
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        panel.Children.Add(icon); panel.Children.Add(text);
        return panel;
    }

    private TextBlock Detail(string text, double size)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _detailTexts.Add(block); return block;
    }

    private Border MakeTag(string text, string? glyph = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        FontIcon? icon = null;
        if (glyph is not null) { icon = new FontIcon { Glyph = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }; content.Children.Add(icon); }
        var label = new TextBlock { Text = text, FontSize = 12, LineHeight = 16, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(label);
        var border = new Border { Child = content, Height = 18, Padding = new Thickness(6, 0, 6, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), MaxWidth = 230, VerticalAlignment = VerticalAlignment.Center };
        _tagParts.Add((border, label, icon));
        return border;
    }

    public void RefreshTheme()
    {
        _frame.Background = UiText.CardFor(this); _frame.BorderBrush = UiText.LineFor(this);
        _name.Foreground = _icon.Foreground = _chevron.Foreground = UiText.InkFor(this);
        _warning.Foreground = UiText.LowFor(this);
        _noResponse.Foreground = UiText.MutedFor(this);
        var quiet = _device.Battery.Value is null || _device.Battery.Tone == BatteryTone.Stale;
        _battery.Foreground = quiet ? UiText.MutedFor(this) : UiText.InkFor(this);
        _percent.Foreground = _device.Battery.Value is null ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : UiText.MutedFor(this);
        _dash.Paint(UiText.MutedFor(this));
        // Low battery colours the glyph only; the number keeps its colour.
        _glyph.Paint(_device.Presence == DisplayPresence.NoResponse || quiet ? UiText.MutedFor(this) : _device.Battery.Low ? UiText.LowFor(this) : UiText.InkFor(this));
        foreach (var paint in _detailPaint) paint();
        foreach (var border in _details.Children.OfType<Border>()) border.BorderBrush = UiText.LineFor(this);
        foreach (var (border, text, glyph) in _tagParts)
        {
            border.BorderBrush = UiText.LineFor(this);
            text.Foreground = UiText.MutedFor(this);
            if (glyph is not null) glyph.Foreground = UiText.MutedFor(this);
        }
    }

    internal AppTestCard CaptureTestState(string id) => new(id, _name.Text,
        _noResponse.Visibility == Visibility.Visible ? _noResponse.Text : "",
        _device.Battery.Value is null || _noResponse.Visibility == Visibility.Visible ? "—" : _value.Text + "%", "",
        _device.Battery.Tone.ToString(), _warning.Visibility == Visibility.Visible,
        UiText.Transport(_device.Transport), UiText.Source(_device.Source), _expanded,
        _expanded ? _detailTexts.Select(x => x.Text).ToArray() : [],
        _glyph.Charging, _header.ActualHeight, (_battery.Foreground as SolidColorBrush)?.Color.ToString(),
        false, 0, _reveal.Progress, _tweening, ActualHeight, BatteryGlyph: _glyph.Describe(), GlyphSteps: _glyph.Steps,
        GlyphLayout: CaptureGlyphLayout(), BatteryGlyphColor: _glyph.InkColor,
        PercentColor: (_percent.Foreground as SolidColorBrush)?.Color.ToString(), SourceLayouts: CaptureSourceLayouts(),
        GlyphPixelLayout: _glyph.PixelLayout, GlyphRootBounds: _glyph.CaptureRootBounds(), Merged: _merged,
        Connections: _device.Connections.Select(connection => UiText.Transport(connection.Transport) + ":" + connection.State).ToArray(),
        Tags: _tags.Children.OfType<Border>().Select(border => _tagParts.FirstOrDefault(part => ReferenceEquals(part.Border, border)).Text?.Text ?? "").ToArray(),
        MarkLayout: _glyph.MarkLayout, ValueDashVisible: _dash.Visibility == Visibility.Visible,
        ValueDashLayout: _dash.Describe(), ValueDashRootBounds: _dash.CaptureRootBounds(), ValueDashColor: _dash.InkColor);

    private Windows.Foundation.Rect Bounds(FrameworkElement element) =>
        element.TransformToVisual(this).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private AppTestGlyphLayout? CaptureGlyphLayout()
    {
        if (XamlRoot is null || _header.ActualWidth <= 0 || _header.ActualHeight <= 0) return null;
        var valueElement = _noResponse.Visibility == Visibility.Visible ? _noResponse : _battery;
        var value = Bounds(valueElement);
        var glyph = Bounds(_glyph);
        var valueParent = VisualTreeHelper.GetParent(valueElement) as UIElement;
        var glyphParent = VisualTreeHelper.GetParent(_glyph) as UIElement;
        // Layout slots are parent-relative and include space assigned around the text/glyph.
        // Preserve them separately from ActualWidth; text's fractional metrics can be narrower than its slot.
        var valueSlot = valueParent?.TransformToVisual(this).TransformBounds(
            Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(valueElement)) ?? default;
        var glyphSlot = glyphParent?.TransformToVisual(this).TransformBounds(
            Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(_glyph)) ?? default;
        return new(value.Left, value.Right, value.Top, value.Bottom, glyph.Left, glyph.Right, glyph.Top, glyph.Bottom,
            _header.ActualWidth, _header.ActualHeight, valueElement.DesiredSize.Width, valueElement.ActualWidth, _glyph.ActualWidth,
            XamlRoot.RasterizationScale, valueSlot.Left, valueSlot.Right, glyphSlot.Left, glyphSlot.Right,
            valueParent is not null && glyphParent is not null);
    }

    private IReadOnlyList<AppTestSourceLayout> CaptureSourceLayouts()
    {
        if (!_expanded || XamlRoot is null || ActualWidth <= 0) return [];
        return _sourceRows.Select(source =>
        {
            var row = Bounds(source.Row);
            var left = Bounds(source.Left);
            var status = Bounds(source.Status);
            var error = source.Error is null ? default : Bounds(source.Error);
            var copy = source.Copy is null ? default : Bounds(source.Copy);
            var errorLine = source.ErrorLine is null ? default : Bounds(source.ErrorLine);
            var errorParentMatches = source.Error is not null && ReferenceEquals(source.Error.Parent, source.ErrorLine);
            var copyParentMatches = source.Copy is not null && ReferenceEquals(source.Copy.Parent, source.ErrorLine);
            var errorLineParentMatches = source.ErrorLine is not null && ReferenceEquals(source.ErrorLine.Parent, source.Left);
            var errorParent = source.Error is null ? null : VisualTreeHelper.GetParent(source.Error) as UIElement;
            var errorSlot = source.Error is not null && errorParent is not null
                ? errorParent.TransformToVisual(this).TransformBounds(Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(source.Error))
                : default;
            const double tolerance = 0.01;
            // The column constrains horizontal placement; the enclosing source row owns vertical space.
            // A StackPanel's rounded child slot can extend past its ActualHeight while remaining inside that row.
            var inside = source.Error is not null && source.Copy is not null && source.ErrorLine is not null
                && errorParentMatches && copyParentMatches && errorLineParentMatches
                && error.Left >= left.Left - tolerance && error.Right <= left.Right + tolerance
                && error.Top >= row.Top - tolerance && error.Bottom <= row.Bottom + tolerance
                && copy.Left >= left.Left - tolerance && copy.Right <= left.Right + tolerance
                && copy.Top >= error.Bottom - tolerance && copy.Bottom <= row.Bottom + tolerance;
            return new AppTestSourceLayout(source.Method, source.Error is not null, source.Error?.Text,
                source.Row.ActualWidth, source.Row.ActualHeight, left.Left, left.Right, status.Left, status.Right,
                error.Left, error.Right, error.Top, error.Bottom, source.Error?.DesiredSize.Width ?? 0, source.Error?.ActualWidth ?? 0,
                copy.Left, copy.Right, inside, left.Top, left.Bottom,
                errorParentMatches, copyParentMatches, errorLineParentMatches,
                errorLine.Left, errorLine.Right, errorLine.Top, errorLine.Bottom,
                errorSlot.Left, errorSlot.Right, errorSlot.Top, errorSlot.Bottom,
                source.Error?.DesiredSize.Height ?? 0, source.Error?.ActualHeight ?? 0, errorParent is not null,
                row.Top, row.Bottom, copy.Top, copy.Bottom);
        }).ToArray();
    }

    private sealed record SourceRow(string Method, Border Row, StackPanel Left, FrameworkElement Status,
        Grid? ErrorLine, TextBlock? Error, HyperlinkButton? Copy);
}
