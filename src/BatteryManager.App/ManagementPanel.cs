using BatteryManager.Application.Startup;
using BatteryManager.Application.Updates;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BatteryManager.App;

internal sealed record ManagementPanelState(string UpdatePhase, string StatusText, bool AutoCheck,
    bool CheckEnabled, bool DownloadVisible, bool InstallVisible, bool Busy,
    string StartupStatus, bool StartupEnabled, string? Error, bool Simulated, bool ConfirmationOpen, bool LocalUpdateFixture = false,
    bool AutomaticControlEnabled = false, bool CancelControlEnabled = false,
    string? PrimaryActionText = null, bool PrimaryActionEnabled = false);

// The "通用" and "关于" sections of the settings page. Owns only the controls; HTTP, persistence and
// registry work live in services. Rows use the settings-card layout of MainWindow.SettingCard.
internal sealed class ManagementPanel : StackPanel
{
    private enum UpdateAction { Check, Download, Install, Cancel }

    private readonly ToggleSwitch _startup = new() { OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly ToggleSwitch _automatic = new() { OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly TextBlock _startupHint = Hint();
    private readonly TextBlock _automaticHint = Hint();
    private readonly TextBlock _status = Hint();
    private readonly TextBlock _error = new() { FontSize = 12, LineHeight = 16, TextWrapping = TextWrapping.Wrap, Margin = new(1, 4, 0, 0) };
    // One update button; its text and action follow the update phase.
    private readonly Button _action = new() { MinWidth = 96 };
    private readonly ProgressRing _checking = new() { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new(0, 0, 3, 3), Visibility = Visibility.Collapsed };
    private readonly Button _removeStartup = new() { Content = UiText.Pick("移除", "Remove") };
    private readonly HyperlinkButton _systemStartup = new() { Content = UiText.Pick("打开设置", "Open settings") };
    private readonly List<Border> _cards = [];
    private readonly List<TextBlock> _labels = [];
    private readonly List<TextBlock> _hints = [];
    private UpdateAction _next = UpdateAction.Check;
    private bool _startupWarning;
    private bool _statusWarning;
    private bool _rendering;
    private bool _stopped;
    private bool _preparingInstallation;
    private bool _changingStartup;
    private bool _changingAutomatic;
    private UpdateSnapshot? _update;
    private StartupState? _startupState;
    private string? _actionError;
    private bool _simulated;
    private bool _localUpdateFixture;
    private ContentDialog? _confirmation;

    public Func<bool, Task>? ChangeStartup { get; set; }
    public Func<bool, Task>? ChangeAutomatic { get; set; }
    public Func<Task>? Check { get; set; }
    public Func<Task>? Download { get; set; }
    public Func<Task>? Install { get; set; }
    public Action? OpenSystemStartup { get; set; }
    public Func<Task>? Cancel { get; set; }

    public ManagementPanel()
    {
        Spacing = 4;
        _hints.AddRange([_startupHint, _automaticHint, _status]);
        Children.Add(Section(UiText.Get("General")));
        AutomationProperties.SetName(_startup, UiText.Pick("登录 Windows 时启动", "Start when I sign in"));
        AutomationProperties.SetName(_automatic, UiText.Pick("自动更新", "Automatic updates"));
        Children.Add(Row(Glyph(""), UiText.Pick("登录 Windows 时启动", "Start when I sign in"), _startupHint, _systemStartup, _removeStartup, _startup));
        _automaticHint.Text = UiText.Pick("有新版本时自动安装", "Installs new versions automatically");
        Children.Add(Row(Glyph(""), UiText.Pick("自动更新", "Automatic updates"), _automaticHint, _automatic));
        Children.Add(Section(UiText.Get("About")));
        // The 16 DIP app icon is drawn 1:1 per scale; it sits centred in the 20 DIP icon column.
        var appIcon = AppIcon.CreateImage();
        appIcon.Margin = new(2, 0, 2, 0);
        var about = Row(appIcon, "Battery Manager", _status, _checking, _action);
        ((Grid)about.Child).Children.Add(_progress);
        Children.Add(about);
        Children.Add(_error);
        _startup.Toggled += async (_, _) =>
        {
            if (_rendering || _stopped || _changingStartup) return;
            _changingStartup = true;
            try { await InvokeAsync(() => ChangeStartup?.Invoke(_startup.IsOn)); }
            finally { _changingStartup = false; Render(); }
        };
        _automatic.Toggled += async (_, _) =>
        {
            if (_rendering || _stopped || _changingAutomatic) return;
            _changingAutomatic = true;
            try { await InvokeAsync(() => ChangeAutomatic?.Invoke(_automatic.IsOn)); }
            finally { _changingAutomatic = false; Render(); }
        };
        _action.Click += async (_, _) => await InvokeAsync(_next switch
        {
            UpdateAction.Download => Download,
            UpdateAction.Install => Install,
            UpdateAction.Cancel => Cancel,
            _ => Check
        });
        _removeStartup.Click += async (_, _) => await InvokeAsync(() => ChangeStartup?.Invoke(false));
        _systemStartup.Click += (_, _) => InvokeAction(OpenSystemStartup);
        Render();
    }

    public void Apply(UpdateSnapshot update, StartupState startup, bool simulated, string? actionError = null)
    {
        if (_update is not null && update.Version < _update.Version) return;
        _update = update; _startupState = startup; _simulated = simulated; _actionError = actionError; Render();
    }
    public void Stop() { _stopped = true; HideConfirmation(); Render(); }
    public void MarkLocalUpdateFixture()
    {
        if (_localUpdateFixture) return;
        _localUpdateFixture = true;
        var notice = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(1, 12, 0, 4) };
        notice.Text = UiText.Pick("本地更新验收：响应来自指定文件，不表示 GitHub 已发布此版本。下载、校验和安装仍会真实执行。",
            "Local update acceptance: responses come from selected files, not a published GitHub release. Downloads, verification and installation are real.");
        _labels.Add(notice); Children.Insert(0, notice);
        Render();
    }
    public void SetPreparingInstallation(bool value) { _preparingInstallation = value; Render(); }
    internal void ToggleAutomaticForTest(bool enabled)
    {
        if (!_automatic.IsEnabled) throw new InvalidOperationException("Automatic update control is disabled.");
        _automatic.IsOn = enabled;
    }
    internal void InvokePrimaryForTest()
    {
        if (!_simulated && !_localUpdateFixture) throw new InvalidOperationException("Button invocation requires an isolated management test.");
        // Invoke the real button's automation peer so the Click handler and its current action dispatch run.
        var peer = new ButtonAutomationPeer(_action);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
    public void HideConfirmation() => _confirmation?.Hide();
    public async Task<bool> ConfirmInstallAsync(string version)
    {
        if (_confirmation is not null || _stopped) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
            Title = UiText.Pick($"安装 {version}？", $"Install {version}?"),
            Content = UiText.Pick("程序会先退出，再打开安装程序。", "The app exits first, then the installer opens."),
            PrimaryButtonText = UiText.Pick("退出并安装", "Exit and install"),
            CloseButtonText = UiText.Pick("取消", "Cancel"), DefaultButton = ContentDialogButton.Close
        };
        _confirmation = dialog;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { _confirmation = null; }
    }
    public async Task ShowInstallErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
            Title = UiText.Pick("安装程序未能启动", "Could not start installation"),
            Content = message + Environment.NewLine + UiText.Pick("程序将退出，重新打开后可再试。", "The app will exit. Reopen it to try again."),
            CloseButtonText = UiText.Pick("关闭", "Close")
        };
        await dialog.ShowAsync();
    }
    public void RefreshTheme()
    {
        foreach (var card in _cards) { card.Background = UiText.CardFor(this); card.BorderBrush = UiText.LineFor(this); }
        foreach (var label in _labels) label.Foreground = UiText.InkFor(this);
        foreach (var hint in _hints) hint.Foreground = UiText.MutedFor(this);
        if (_startupWarning) _startupHint.Foreground = UiText.LowFor(this);
        if (_statusWarning) _status.Foreground = UiText.LowFor(this);
        _error.Foreground = UiText.LowFor(this);
    }
    public ManagementPanelState Capture() => new(_update?.Phase.ToString() ?? "Idle", _status.Text,
        _automatic.IsOn, _next == UpdateAction.Check && _action.IsEnabled, _next == UpdateAction.Download,
        _next == UpdateAction.Install, _update?.IsBusy == true,
        _startupState?.Status.ToString() ?? "Unknown", _startup.IsOn, Errors(), _simulated, _confirmation is not null, _localUpdateFixture,
        _automatic.IsEnabled, _next == UpdateAction.Cancel && _action.IsEnabled,
        _action.Content?.ToString(), _action.IsEnabled);

    private void Render()
    {
        _rendering = true;
        try
        {
            var startup = _startupState;
            _startup.IsOn = startup?.IsEnabled == true;
            _startup.IsEnabled = !_stopped && !_changingStartup && (startup?.IsEnabled == true ? startup.CanDisable : startup?.CanEnable == true);
            _startupWarning = startup?.Message is not null;
            _startupHint.Text = startup?.Message ?? UiText.Pick("只在托盘运行", "Runs in the tray only");
            _removeStartup.Visibility = startup?.CanDisable == true && !startup.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            _removeStartup.IsEnabled = !_stopped && !_changingStartup;
            _systemStartup.Visibility = startup?.Status is StartupStatus.BlockedByWindows or StartupStatus.Unknown ? Visibility.Visible : Visibility.Collapsed;
            _systemStartup.IsEnabled = !_stopped;
            _automatic.IsOn = _update?.AutoCheckEnabled == true;
            _automatic.IsEnabled = !_stopped && !_changingAutomatic && _update?.CanSaveSettings == true;
            RenderUpdate();
            // A failed update already names its reason in the status line; other errors go below the card.
            var shown = _update?.Phase == UpdatePhase.Failed ? _update.Error : null;
            _error.Text = string.Join(Environment.NewLine, new[] { _actionError, _update?.Error, _update?.SettingsError }
                .Where(value => !string.IsNullOrWhiteSpace(value) && value != shown).Distinct());
            _error.Visibility = _error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _rendering = false; }
        RefreshTheme();
    }

    private void RenderUpdate()
    {
        var update = _update;
        var phase = update?.Phase ?? UpdatePhase.Idle;
        var current = AppBuildInfo.Version;
        var next = update?.SelectedPackage?.Version ?? update?.Available?.Version;
        var downloaded = phase == UpdatePhase.Downloaded && update?.DownloadedFile is not null;
        var installer = downloaded && update?.SelectedPackage?.Kind == UpdatePackageKind.Installer;
        _statusWarning = phase == UpdatePhase.Failed;
        _status.Text = _preparingInstallation && !_stopped ? UiText.Pick("正在准备安装…", "Preparing to install…") : phase switch
        {
            UpdatePhase.Checking => UiText.Pick($"版本 {current} · 正在检查…", $"Version {current} · Checking…"),
            UpdatePhase.UpToDate => UiText.Pick($"版本 {current} · 已是最新", $"Version {current} · Up to date"),
            UpdatePhase.Available => UiText.Pick($"版本 {current} · 新版本 {next} 可用", $"Version {current} · {next} is available"),
            UpdatePhase.Downloading => Downloading(update!, next),
            UpdatePhase.Downloaded => UiText.Pick($"{next} 已下载", $"{next} downloaded"),
            UpdatePhase.PreparingInstallation => UiText.Pick("正在准备安装…", "Preparing to install…"),
            UpdatePhase.Canceled => UiText.Pick($"版本 {current} · 已取消", $"Version {current} · Canceled"),
            UpdatePhase.Cancelling => UiText.Pick("正在取消…", "Canceling…"),
            UpdatePhase.Failed => update?.Error is { Length: > 0 } reason
                ? UiText.Pick($"更新失败：{reason}", $"Update failed: {reason}") : UiText.Pick("更新失败", "Update failed"),
            UpdatePhase.Stopping or UpdatePhase.Stopped => UiText.Pick("正在退出…", "Exiting…"),
            _ => UiText.Pick($"版本 {current}", $"Version {current}")
        };

        var busy = update?.IsBusy == true;
        var accent = false;
        var enabled = !_stopped;
        var waiting = false;
        string label;
        if (phase == UpdatePhase.Checking) { _next = UpdateAction.Check; label = UiText.Pick("检查更新", "Check for updates"); enabled = false; waiting = true; }
        else if (phase == UpdatePhase.Cancelling) { _next = UpdateAction.Cancel; label = UiText.Get("Cancel"); enabled = false; waiting = true; }
        else if (busy) { _next = UpdateAction.Cancel; label = UiText.Get("Cancel"); }
        else if (installer)
        {
            _next = UpdateAction.Install; label = UiText.Pick("安装", "Install"); accent = true;
            enabled = !_stopped && !_preparingInstallation;
        }
        else if (update?.Available is not null && !downloaded)
        {
            _next = UpdateAction.Download;
            accent = phase != UpdatePhase.Failed;
            label = accent ? UiText.Pick("下载", "Download") : UiText.Pick("重试", "Retry");
        }
        else
        {
            _next = UpdateAction.Check;
            label = phase == UpdatePhase.Failed ? UiText.Pick("重试", "Retry") : UiText.Pick("检查更新", "Check for updates");
            enabled = !_stopped && !_preparingInstallation && update is not null;
        }
        _action.Content = label;
        _action.IsEnabled = enabled;
        _action.Style = accent ? AccentStyle() : null;
        AutomationProperties.SetName(_action, label);
        _checking.IsActive = waiting;
        _checking.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;

        var total = update?.SelectedPackage?.Size ?? 0;
        var downloading = phase == UpdatePhase.Downloading;
        _progress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = downloading && total <= 0;
        _progress.Value = downloading && total > 0 ? Math.Clamp(update!.DownloadedBytes * 100d / total, 0, 100) : 0;
    }

    private static string Downloading(UpdateSnapshot update, string? next)
    {
        var done = update.DownloadedBytes / 1048576d;
        var total = (update.SelectedPackage?.Size ?? 0) / 1048576d;
        return total > 0
            ? UiText.Pick($"正在下载 {next} · {done:F1} / {total:F1} MB", $"Downloading {next} · {done:F1} / {total:F1} MB")
            : UiText.Pick($"正在下载 {next} · {done:F1} MB", $"Downloading {next} · {done:F1} MB");
    }

    // Every current error, for the test view; the screen shows a failed update's reason in the status line instead.
    private string Errors() => string.Join(Environment.NewLine, new[] { _actionError, _update?.Error, _update?.SettingsError }
        .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());

    private static Style? AccentStyle()
    {
        try { return Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] as Style; }
        catch (Exception) { return null; }
    }

    private async Task InvokeAsync(Func<Task?>? action)
    {
        if (_stopped) return;
        try { if (action?.Invoke() is { } task) await task; }
        catch (Exception error) { _actionError = error.Message; Render(); }
    }
    private void InvokeAction(Action? action)
    { try { if (!_stopped) action?.Invoke(); } catch (Exception error) { _actionError = error.Message; Render(); } }

    private TextBlock Section(string title)
    {
        var text = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new(1, 24, 0, 4) };
        _labels.Add(text);
        return text;
    }

    // Windows 11 settings card, as MainWindow.SettingCard: 66 minimum height, 16 padding, 20 icon with 20 before
    // the text, title 14/20 and one line of 12/16 below it, controls centred on the right.
    private Border Row(FrameworkElement icon, string title, TextBlock hint, params FrameworkElement[] actions)
    {
        var grid = new Grid { MinHeight = 66, Padding = new(16) };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new(icon.Margin.Left + 2, 0, icon.Margin.Right + 20, 0);
        grid.Children.Add(icon);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 16, 0) };
        var name = new TextBlock { Text = title, FontSize = 14, LineHeight = 20 };
        _labels.Add(name); labels.Children.Add(name); labels.Children.Add(hint);
        Grid.SetColumn(labels, 1); grid.Children.Add(labels);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in actions) { action.VerticalAlignment = VerticalAlignment.Center; controls.Children.Add(action); }
        Grid.SetColumn(controls, 2); grid.Children.Add(controls);
        // The outer grid lets a progress bar run along the card's bottom edge, outside the padding.
        var card = new Border { Child = new Grid { Children = { grid } }, CornerRadius = new(4), BorderThickness = new(1) };
        _cards.Add(card);
        return card;
    }

    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph, FontSize = 20 };
    private static TextBlock Hint() => new() { FontSize = 12, LineHeight = 16, TextWrapping = TextWrapping.Wrap };
}
