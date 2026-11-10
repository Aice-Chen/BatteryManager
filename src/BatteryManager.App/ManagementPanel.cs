using BatteryManager.Application.Startup;
using BatteryManager.Application.Updates;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BatteryManager.App;

internal sealed record ManagementPanelState(string UpdatePhase, string StatusText, bool AutoCheck,
    bool CheckEnabled, bool DownloadVisible, bool InstallVisible, bool Busy,
    string StartupStatus, bool StartupEnabled, string? Error, bool Simulated, bool ConfirmationOpen);

// Owns only the settings controls. HTTP, persistence and registry work live in services.
internal sealed class ManagementPanel : StackPanel
{
    private readonly ToggleSwitch _startup = new() { OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly ToggleSwitch _automatic = new() { OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly TextBlock _startupHint = Text();
    private readonly TextBlock _status = Text();
    private readonly TextBlock _lastCheck = Text();
    private readonly TextBlock _error = Text();
    private readonly Button _check = new() { Content = UiText.Pick("检查更新", "Check for updates") };
    private readonly Button _download = new() { Content = UiText.Pick("下载新版", "Download update") };
    private readonly Button _install = new() { Content = UiText.Pick("安装更新…", "Install update…") };
    private readonly Button _openFolder = new() { Content = UiText.Pick("查看下载文件", "Show downloaded file") };
    private readonly Button _cancel = new() { Content = UiText.Pick("取消", "Cancel") };
    private readonly Button _import = new() { Content = UiText.Pick("导入便携版配置…", "Import portable settings…") };
    private readonly Button _removeStartup = new() { Content = UiText.Pick("移除启动项", "Remove startup entry") };
    private readonly HyperlinkButton _systemStartup = new() { Content = UiText.Pick("Windows 启动设置", "Windows startup settings") };
    private readonly List<Border> _cards = [];
    private readonly List<TextBlock> _labels = [];
    private bool _rendering;
    private bool _stopped;
    private bool _preparingInstallation;
    private bool _changingStartup;
    private bool _changingAutomatic;
    private UpdateSnapshot? _update;
    private StartupState? _startupState;
    private string? _actionError;
    private bool _simulated;
    private ContentDialog? _confirmation;

    public Func<bool, Task>? ChangeStartup { get; set; }
    public Func<bool, Task>? ChangeAutomatic { get; set; }
    public Func<Task>? Check { get; set; }
    public Func<Task>? Download { get; set; }
    public Func<Task>? Install { get; set; }
    public Action? OpenFolder { get; set; }
    public Action? OpenSystemStartup { get; set; }
    public Func<Task>? Cancel { get; set; }
    public Func<Task>? Import { get; set; }

    public ManagementPanel()
    {
        Spacing = 4;
        var heading = Text(); heading.Text = UiText.Pick("启动与更新", "Startup and updates");
        heading.FontSize = 14; heading.FontWeight = FontWeights.SemiBold; heading.Margin = new(1, 24, 0, 4);
        _labels.Add(heading); Children.Add(heading);
        AutomationProperties.SetName(_startup, UiText.Pick("登录 Windows 时启动", "Start when I sign in"));
        AutomationProperties.SetName(_automatic, UiText.Pick("自动检查更新", "Check for updates automatically"));
        var startupActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        startupActions.Children.Add(_removeStartup); startupActions.Children.Add(_systemStartup);
        var startupBody = new StackPanel { Spacing = 8 }; startupBody.Children.Add(_startupHint); startupBody.Children.Add(startupActions);
        Children.Add(Card(UiText.Pick("登录 Windows 时启动", "Start when I sign in"), _startup, startupBody));
        var hint = Text(); hint.Text = UiText.Pick("每天检查一次；下载和安装由你确认。", "Checks daily. You choose when to download and install.");
        Children.Add(Card(UiText.Pick("自动检查更新", "Check for updates automatically"), _automatic, hint));
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(_status); body.Children.Add(_lastCheck);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in new[] { _download, _install, _openFolder, _cancel }) buttons.Children.Add(button);
        body.Children.Add(buttons); body.Children.Add(_error);
        Children.Add(Card(UiText.Pick("软件更新", "Software updates"), _check, body));
        _import.HorizontalAlignment = HorizontalAlignment.Left;
        _import.Visibility = Visibility.Collapsed; Children.Add(_import);
        _import.Click += async (_, _) => await InvokeAsync(Import);
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
        _check.Click += async (_, _) => await InvokeAsync(Check);
        _download.Click += async (_, _) => await InvokeAsync(Download);
        _install.Click += async (_, _) => await InvokeAsync(Install);
        _cancel.Click += async (_, _) => await InvokeAsync(Cancel);
        _removeStartup.Click += async (_, _) => await InvokeAsync(() => ChangeStartup?.Invoke(false));
        _openFolder.Click += (_, _) => InvokeAction(OpenFolder);
        _systemStartup.Click += (_, _) => InvokeAction(OpenSystemStartup);
        Render();
    }

    public void Apply(UpdateSnapshot update, StartupState startup, bool simulated, string? actionError = null)
    {
        if (_update is not null && update.Version < _update.Version) return;
        _update = update; _startupState = startup; _simulated = simulated; _actionError = actionError; Render();
    }
    public void Stop() { _stopped = true; HideConfirmation(); Render(); }
    public void SetPreparingInstallation(bool value) { _preparingInstallation = value; Render(); }
    public void EnableImport(bool installed) => _import.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    public void ShowImportResult(bool success)
    {
        if (success) _import.Content = UiText.Pick("配置已导入", "Settings imported");
    }
    public void HideConfirmation() => _confirmation?.Hide();
    public async Task<bool> ConfirmInstallAsync(string version)
    {
        if (_confirmation is not null || _stopped) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
            Title = UiText.Pick($"安装 {version}？", $"Install {version}?"),
            Content = UiText.Pick("程序将保存设置并退出，然后打开安装程序。升级将沿用当前安装位置。", "The app will save settings and exit, then open the installer in the current installation location."),
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
            Content = message + Environment.NewLine + UiText.Pick("程序将退出；重新打开后可再次尝试。", "The app will exit. Reopen it to try again."),
            CloseButtonText = UiText.Pick("关闭", "Close")
        };
        await dialog.ShowAsync();
    }
    public void RefreshTheme()
    {
        foreach (var card in _cards) { card.Background = UiText.CardFor(this); card.BorderBrush = UiText.LineFor(this); }
        foreach (var label in _labels) label.Foreground = UiText.InkFor(this);
        _startupHint.Foreground = _status.Foreground = _lastCheck.Foreground = UiText.MutedFor(this);
        _error.Foreground = UiText.LowFor(this);
    }
    public ManagementPanelState Capture() => new(_update?.Phase.ToString() ?? "Idle", _status.Text,
        _automatic.IsOn, _check.IsEnabled, _download.Visibility == Visibility.Visible,
        _install.Visibility == Visibility.Visible, _update?.IsBusy == true,
        _startupState?.Status.ToString() ?? "Unknown", _startup.IsOn, _error.Text, _simulated, _confirmation is not null);

    private void Render()
    {
        _rendering = true;
        try
        {
            var update = _update; var startup = _startupState; var busy = update?.IsBusy == true;
            _startup.IsOn = startup?.IsEnabled == true;
            _startup.IsEnabled = !_stopped && !_changingStartup && (startup?.IsEnabled == true ? startup.CanDisable : startup?.CanEnable == true);
            _startupHint.Text = startup?.Message ?? UiText.Pick("启动项以 Windows 当前状态为准。", "Reflects the current Windows startup registration.");
            _removeStartup.Visibility = startup?.CanDisable == true && !startup.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            _removeStartup.IsEnabled = !_stopped && !_changingStartup;
            _systemStartup.Visibility = startup?.Status is StartupStatus.BlockedByWindows or StartupStatus.Unknown ? Visibility.Visible : Visibility.Collapsed;
            _systemStartup.IsEnabled = !_stopped;
            _automatic.IsOn = update?.AutoCheckEnabled == true;
            _automatic.IsEnabled = !_stopped && !_changingAutomatic && update?.CanSaveSettings == true;
            _check.IsEnabled = !_stopped && !_preparingInstallation && !busy && update is not null;
            _status.Text = update?.Phase switch
            {
                UpdatePhase.Checking => UiText.Pick("正在检查…", "Checking…"),
                UpdatePhase.UpToDate => UiText.Pick("没有可用的新版正式发布。", "No newer stable release is available."),
                UpdatePhase.Available => UiText.Pick($"发现新版本 {update.Available?.Version}", $"Version {update.Available?.Version} is available"),
                UpdatePhase.Downloading => UiText.Pick($"正在下载：{update.DownloadedBytes / 1048576d:F1} MB", $"Downloading: {update.DownloadedBytes / 1048576d:F1} MB"),
                UpdatePhase.Downloaded => update.SelectedPackage?.Kind == UpdatePackageKind.Installer
                    ? UiText.Pick("下载已校验，可以安装。", "Download verified. Ready to install.")
                    : UiText.Pick("便携包已校验。请退出程序后解压，并保留原来的配置文件。", "Portable archive verified. Exit before extracting and keep your existing settings."),
                UpdatePhase.Canceled => UiText.Pick("已取消。", "Canceled."),
                UpdatePhase.Cancelling => UiText.Pick("正在取消…", "Canceling…"),
                UpdatePhase.Failed => UiText.Pick("更新操作未完成，可以重试。", "The update operation did not finish. You can retry."),
                UpdatePhase.Stopping or UpdatePhase.Stopped => UiText.Pick("正在退出…", "Exiting…"),
                _ => UiText.Pick("尚未检查更新。", "Updates have not been checked yet.")
            };
            if (_preparingInstallation && !_stopped)
                _status.Text = UiText.Pick("正在保存设置并检查安装文件…", "Saving settings and checking the installer…");
            _lastCheck.Text = update?.LastSuccessfulCheckUtc is { } at
                ? UiText.Pick($"最近成功检查：{at.ToLocalTime():g}", $"Last successful check: {at.ToLocalTime():g}") : "";
            var downloaded = update?.Phase == UpdatePhase.Downloaded && update.DownloadedFile is not null;
            _download.Visibility = update?.Available is not null && !downloaded && !busy ? Visibility.Visible : Visibility.Collapsed;
            _download.IsEnabled = !_stopped;
            _install.Visibility = downloaded && update?.SelectedPackage?.Kind == UpdatePackageKind.Installer ? Visibility.Visible : Visibility.Collapsed;
            _install.IsEnabled = !_stopped && !_preparingInstallation;
            _openFolder.Visibility = downloaded && update?.SelectedPackage?.Kind == UpdatePackageKind.Portable ? Visibility.Visible : Visibility.Collapsed;
            _openFolder.IsEnabled = !_stopped;
            _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; _cancel.IsEnabled = !_stopped;
            _import.IsEnabled = !_stopped && !busy;
            _error.Text = string.Join(Environment.NewLine, new[] { _actionError, update?.Error, update?.SettingsError }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
            _error.Visibility = _error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _rendering = false; }
        RefreshTheme();
    }

    private async Task InvokeAsync(Func<Task?>? action)
    {
        if (_stopped) return;
        try { if (action?.Invoke() is { } task) await task; }
        catch (Exception error) { _actionError = error.Message; Render(); }
    }
    private void InvokeAction(Action? action)
    { try { if (!_stopped) action?.Invoke(); } catch (Exception error) { _actionError = error.Message; Render(); } }
    private Border Card(string title, FrameworkElement action, FrameworkElement body)
    {
        var grid = new Grid { Padding = new(16), RowSpacing = 8, ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var label = Text(); label.Text = title; label.FontSize = 14; label.VerticalAlignment = VerticalAlignment.Center;
        _labels.Add(label); grid.Children.Add(label); Grid.SetColumn(action, 1); grid.Children.Add(action);
        Grid.SetRow(body, 1); Grid.SetColumnSpan(body, 2); grid.Children.Add(body);
        var card = new Border { Child = grid, CornerRadius = new(4), BorderThickness = new(1) }; _cards.Add(card); return card;
    }
    private static TextBlock Text() => new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
}
