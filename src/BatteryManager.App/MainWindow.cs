using BatteryManager.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.UI;

namespace BatteryManager.App;

internal sealed record MainUiState(string Page, string MainTheme, string? DialogKind, string? DialogDeviceId,
    string? DialogValue, bool DialogSaving, string? DialogError,
    double? TypeListMaxHeight = null, double? TypeListActualHeight = null, double WindowHeight = 0,
    RefreshMotionState? RefreshMotion = null, bool? TypeLastOptionVisible = null,
    bool PreparingInstallation = false, string? SettingsError = null,
    bool PreferencesEnabled = true, bool AppearanceEnabled = true, bool BackEnabled = true);

internal sealed class MainWindow : Window
{
    private readonly BatterySession _session;
    // Shell = integrated title bar + page. Mica shows through both when Windows supports it.
    private readonly Grid _shell = new();
    private readonly Grid _titleBar = new() { Height = 32, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    // App icon 16 DIP from the left edge, title 12 DIP after it. Tight line bounds centre the capitals and digits on the icon.
    private readonly Image _titleIcon = AppIcon.CreateImage();
    private readonly TextBlock _titleText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(44, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, TextLineBounds = TextLineBounds.Tight };
    private readonly AppIcon.WindowIcons _windowIcons = new();
    private readonly bool _mica;
    private readonly Grid _root = new() { Padding = new Thickness(28, 8, 28, 24), RowSpacing = 20 };
    private readonly StackPanel _cards = new() { Spacing = 4 };
    private readonly Dictionary<string, DeviceCard> _cardById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceDisplay> _devices = new(StringComparer.Ordinal);
    private readonly Grid _devicePage = new();
    private readonly ScrollViewer _deviceScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _settingsContent = new() { Spacing = 4 };
    private readonly ScrollViewer _settingsPage;
    // Let the tight line bounds determine the single-line title height; a fixed LineHeight offsets its ink.
    private readonly TextBlock _title = new() { Text = UiText.Get("Devices"), FontSize = 28, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
    private readonly TextBlock _empty = new() { Text = UiText.Get("Empty"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _settingsError = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button _refresh;
    private readonly Button _settings;
    private readonly Button _back;
    /// <summary>Tray device priority, edited in place on the settings page.</summary>
    public TrayPriorityPanel TrayPriority { get; } = new();
    public ManagementPanel Management { get; } = new();
    public Action? ManagementOpened { get; set; }
    public bool CanManageApplication => _settingsVisible && !_quitting && !_preparingInstallation && AppWindow.IsVisible;
    private readonly InfoBar _updateNotice = new() { IsOpen = false, IsClosable = true, Margin = new Thickness(0, 0, 0, 8) };
    private string? _availableUpdateVersion;
    private string? _dismissedUpdateVersion;
    /// <summary>Session candidates and saved groups for "合并连接". Merging starts from a card's menu;
    /// the result and its undo appear in an info bar above the cards.</summary>
    public DeviceMergeCandidates Merge { get; } = new();
    public Func<DeviceLinkMember, DeviceLinkMember, Task<string?>>? MergeRequested { get; set; }
    public Func<string, Task<string?>>? UnmergeRequested { get; set; }
    public Func<Task<string?>>? UndoMergeRequested { get; set; }
    private readonly InfoBar _mergeNotice = new() { IsOpen = false, IsClosable = true, Margin = new Thickness(0, 0, 0, 8) };
    private readonly HyperlinkButton _undo = new() { Content = UiText.Get("Undo") };
    private ListView? _mergeInput;
    private TextBlock? _mergeNote;
    private TextBlock? _mergeEmpty;
    private IReadOnlyList<DeviceMergeCandidate> _mergeCandidates = [];
    private string _mergeCandidatesSignature = "";
    private bool _updatingMergeChoices;
    private string? _noticeKind;
    private bool _mergeBusy;
    private long _deviceUiRevision;
    private Task<bool> _dialogSave = Task.FromResult(false);
    private Task _mergeOperation = Task.CompletedTask;
    public bool HasPendingDeviceChanges => !_dialogSave.IsCompleted || !_mergeOperation.IsCompleted || _colorSave is { IsCompleted: false };
    private bool CanDeviceActions => !_quitting && !_preparingInstallation && !_scenarioChanging && !_settingsVisible && AppWindow.IsVisible;
    private readonly ComboBox _colorMode = new() { MinWidth = 160 };
    private readonly FontIcon _refreshIcon = new() { Glyph = "\uE72C", FontSize = 16 };
    private readonly RefreshSpin _spin;
    private readonly TextBlock? _scenarioBanner;
    private readonly List<Border> _settingCards = [];
    private readonly List<TextBlock> _settingTexts = [];
    private readonly List<TextBlock> _settingSubTexts = [];
    private IReadOnlyList<DeviceCustomization> _customizations = [];
    private MainColorMode _savedMode;
    private string? _preferencesWarning;
    private string? _installationPreparationError;
    private bool _settingMode;
    private bool _settingsVisible;
    private bool _allowClose;
    private bool _scenarioChanging;
    private bool _quitting;
    private bool _preparingInstallation;
    private long _lastVersion = -1;
    private Task? _colorSave;
    private ContentDialog? _dialog;
    private TextBox? _nameInput;
    private ListView? _typeInput;
    private TextBlock? _dialogErrorText;
    private DeviceDisplay? _dialogDevice;
    private string? _dialogKind;
    private bool _dialogSaving;
    private string? _dialogError;
    public FrameworkElement ThemeRoot => _shell;
    public Func<bool> CanHideToTray { get; set; } = () => false;
    public Action? ExitRequested { get; set; }
    public Action? TrayPreferencesRequested { get; set; }
    public Func<MainColorMode, Task<string?>>? SaveColorModeRequested { get; set; }
    public Func<DeviceCustomization, Task<string?>>? SaveCustomizationRequested { get; set; }

    public MainWindow(BatterySession session, bool scenarioMode = false)
    {
        _session = session;
        Title = $"Battery Manager {AppBuildInfo.Version}";
        AppWindow.Resize(new SizeInt32(880, 720));
        _mica = TryUseMica();
        ExtendsContentIntoTitleBar = true;
        try { AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard; } catch (Exception) { }
        _titleText.Text = Title;
        _titleIcon.HorizontalAlignment = HorizontalAlignment.Left;
        _titleIcon.VerticalAlignment = VerticalAlignment.Center;
        _titleIcon.Margin = new Thickness(16, 0, 0, 0);
        _titleBar.Children.Add(_titleIcon);
        _titleBar.Children.Add(_titleText);
        // Taskbar and Alt+Tab icons; the title bar above is ours, so it draws its own.
        _windowIcons.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this));
        Closed += (_, _) => _windowIcons.Dispose();
        _shell.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _shell.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _shell.Children.Add(_titleBar);
        Grid.SetRow(_root, 1); _shell.Children.Add(_root);
        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            if (CanHideToTray()) HideToTray(); else ExitRequested?.Invoke();
        };
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new Grid { MinHeight = 52 };
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _back = IconButton("\uE72B", "Back"); _back.Visibility = Visibility.Collapsed; _back.Margin = new Thickness(0, 0, 8, 0);
        _back.Click += (_, _) => ShowSettings(false);
        heading.Children.Add(_back);
        var headingText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        headingText.Children.Add(_title);
        if (scenarioMode)
        {
            _scenarioBanner = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            headingText.Children.Add(_scenarioBanner);
        }
        Grid.SetColumn(headingText, 1); heading.Children.Add(headingText);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var refreshBody = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        refreshBody.Children.Add(_refreshIcon); refreshBody.Children.Add(new TextBlock { Text = UiText.Get("Refresh") });
        _refresh = new Button { Content = refreshBody, Padding = new Thickness(11, 5, 11, 6), CornerRadius = new CornerRadius(4) };
        AutomationProperties.SetName(_refresh, UiText.Get("Refresh"));
        _refresh.Click += async (_, _) => await RefreshAsync();
        _settings = IconButton("\uE713", "Settings"); _settings.Click += (_, _) => ShowSettings(true);
        actions.Children.Add(_refresh); actions.Children.Add(_settings);
        Grid.SetColumn(actions, 2); heading.Children.Add(actions); _root.Children.Add(heading);
        _spin = new RefreshSpin(_refreshIcon);
        _devicePage.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _devicePage.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _devicePage.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _mergeNotice.ActionButton = _undo;
        _undo.Click += async (_, _) => await UndoMergeAsync();
        _mergeNotice.Closed += (_, _) => { if (!_mergeNotice.IsOpen) _noticeKind = null; };
        AutomationProperties.SetName(_undo, UiText.Get("Undo"));
        _devicePage.Children.Add(_mergeNotice);
        var showUpdate = new Button { Content = UiText.Pick("查看更新", "View update") };
        showUpdate.Click += (_, _) => ShowSettings(true);
        _updateNotice.ActionButton = showUpdate;
        _updateNotice.Closed += (_, _) => _dismissedUpdateVersion = _availableUpdateVersion;
        Grid.SetRow(_updateNotice, 1); _devicePage.Children.Add(_updateNotice);
        _deviceScroll.Content = _cards;
        Grid.SetRow(_deviceScroll, 2); _devicePage.Children.Add(_deviceScroll);
        Grid.SetRow(_empty, 2); _devicePage.Children.Add(_empty);
        Grid.SetRow(_devicePage, 1); _root.Children.Add(_devicePage);
        _settingsContent.Children.Add(SectionTitle("Tray", first: true));
        _settingsContent.Children.Add(TrayPriority);
        _settingsContent.Children.Add(SectionTitle("Appearance"));
        foreach (var mode in Enum.GetValues<MainColorMode>()) _colorMode.Items.Add(new ComboBoxItem { Tag = mode, Content = UiText.Get(mode.ToString()) });
        _colorMode.SelectionChanged += async (_, _) =>
        {
            if (_settingMode || _preparingInstallation || _quitting || _colorMode.SelectedItem is not ComboBoxItem { Tag: MainColorMode mode }) return;
            _colorSave = SaveModeAsync(mode); await _colorSave;
        };
        _settingsContent.Children.Add(SettingCard("\uE790", UiText.Get("ColorMode"), _colorMode, UiText.Get("ThemeHint")));
        _settingsContent.Children.Add(Management);
        _settingsContent.Children.Add(SectionTitle("About"));
        var version = new TextBlock { Text = AppBuildInfo.Version, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
        _settingSubTexts.Add(version);
        _settingsContent.Children.Add(SettingCard("\uE946", "Battery Manager", version, UiText.Get("AboutHint")));
        _settingsContent.Children.Add(_settingsError);
        _settingsPage = new ScrollViewer { Content = _settingsContent, Visibility = Visibility.Collapsed, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_settingsPage, 1); _root.Children.Add(_settingsPage);
        _root.ActualThemeChanged += (_, _) => RefreshTheme();
        Content = _shell;
        SetTitleBar(_titleBar);
        AppWindow.Changed += (_, _) => UpdateTitleBarInsets();
        _shell.Loaded += (_, _) => UpdateTitleBarInsets();
        _shell.SizeChanged += (_, _) => UpdateTitleBarInsets();
        SetPreferences([], MainColorMode.System);
        if (scenarioMode) ShowScenario("overview", true);
        Apply(session.Snapshot); RefreshTheme();
    }

    public void ShowWindow()
    {
        AppWindow.Show(); NativeMethods.ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), 9);
        TrayPriority.SetWindowVisible(true);
        Activate(); NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    public void MarkLocalUpdateFixture()
    {
        Title += UiText.Pick(" — 本地更新验收", " — Local update acceptance");
        _titleText.Text = Title;
        Management.MarkLocalUpdateFixture();
    }

    public void HideToTray()
    {
        Management.HideConfirmation();
        TrayPriority.SetWindowVisible(false);
        _deviceUiRevision++;
        if (!_dialogSaving) _dialog?.Hide();
        // Undo belongs to the moment of the change; a window that was hidden does not offer it again.
        CloseMergeNotice();
        AppWindow.Hide();
    }
    /// <summary>Opens the settings page at the tray priority (the tray menu's "托盘设置").</summary>
    public void ShowTrayPriority()
    {
        ShowSettings(true);
        DispatcherQueue.TryEnqueue(() => { if (_settingsVisible && AppWindow.IsVisible && !_quitting) TrayPriority.StartBringIntoView(); });
    }
    public void ShowSettings(bool show)
    {
        if (_quitting || _preparingInstallation) return;
        if (!show) Management.HideConfirmation();
        if (show && !_settingsVisible)
        {
            _deviceUiRevision++;
            if (!_dialogSaving) _dialog?.Hide();
        }
        TrayPriority.SetPageVisible(show);
        _settingsVisible = show;
        _devicePage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _settingsPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _back.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _settings.Visibility = _refresh.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _title.Text = UiText.Get(show ? "Settings" : "Devices");
        if (show) _back.Focus(FocusState.Programmatic); else _settings.Focus(FocusState.Programmatic);
        if (show) ManagementOpened?.Invoke();
    }
    public void SetUpdateAvailable(string? version)
    {
        _availableUpdateVersion = version;
        if (version is null) { _updateNotice.IsOpen = false; return; }
        _updateNotice.Title = UiText.Pick($"有新版本 {version}", $"Version {version} is available");
        if (version != _dismissedUpdateVersion) _updateNotice.IsOpen = true;
    }
    public void SetPreferences(IReadOnlyList<DeviceCustomization> customizations, MainColorMode mode, string? warning = null)
    {
        _customizations = customizations.ToArray(); _savedMode = mode; _preferencesWarning = warning;
        _settingMode = true;
        try { _colorMode.SelectedIndex = (int)mode; }
        finally { _settingMode = false; }
        SetSettingsError(_installationPreparationError ?? warning);
    }
    private async Task SaveModeAsync(MainColorMode mode)
    {
        _colorMode.IsEnabled = false;
        try
        {
            var error = SaveColorModeRequested is null ? UiText.Get("Unavailable") : await SaveColorModeRequested(mode);
            if (error is null) _savedMode = mode;
            SetSettingsError(error ?? _preferencesWarning);
        }
        catch { SetSettingsError(UiText.Get("SaveFailed")); }
        finally
        {
            _settingMode = true; _colorMode.SelectedIndex = (int)_savedMode; _settingMode = false;
            _colorMode.IsEnabled = !_quitting && !_preparingInstallation;
        }
    }
    private void SetSettingsError(string? error)
    { _settingsError.Text = error ?? ""; _settingsError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible; }

    public void SetInstallationPreparing(bool preparing)
    {
        _preparingInstallation = preparing;
        if (_quitting) return;
        // Disabling the panel invalidates an unfinished drag without discarding its
        // accepted save queue. The page and navigation stay frozen until preflight ends.
        _settingsPage.IsEnabled = _deviceScroll.IsEnabled = !preparing;
        _back.IsEnabled = _settings.IsEnabled = !preparing;
        TrayPriority.IsEnabled = !preparing && !_scenarioChanging && !_mergeBusy;
        _colorMode.IsEnabled = !preparing && _colorSave is not { IsCompleted: false };
        _undo.IsEnabled = !preparing && !_mergeBusy;
        if (preparing)
        {
            _installationPreparationError = null;
            SetSettingsError(_preferencesWarning);
            _deviceUiRevision++;
            if (!_dialogSaving) _dialog?.Hide();
        }
    }

    public void ShowInstallationPreparationError(string error)
    {
        if (_quitting) return;
        _installationPreparationError = UiText.Pick("设置未保存，已停止安装。请检查设置后重试。", "Settings were not saved, so installation was stopped. Review your settings and try again.")
            + Environment.NewLine + error;
        SetSettingsError(_installationPreparationError);
        _settingsError.StartBringIntoView();
    }

    public void Apply(SessionSnapshot snapshot, IReadOnlyList<DeviceDisplay>? displays = null)
    {
        if (snapshot.State.Version < _lastVersion) return;
        _lastVersion = snapshot.State.Version;
        displays ??= snapshot.State.Devices.Select(device => DeviceDisplayMapper.Map(device, _customizations.FirstOrDefault(x => x.DeviceId == device.Id)))
            .OfType<DeviceDisplay>().Select(Merge.Enrich).ToArray();
        // Confirmed order: type group, then shown name. Cards keep their instance (and expansion) when they move.
        displays = DeviceDisplayOrder.Sort(displays);
        _devices.Clear(); foreach (var display in displays) _devices.Add(display.Id, display);
        var ids = displays.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _cardById.Keys.Where(id => !ids.Contains(id)).ToArray())
        { _cards.Children.Remove(_cardById[id]); _cardById.Remove(id); }
        foreach (var device in displays)
        {
            if (_cardById.TryGetValue(device.Id, out var card)) card.Update(device, Merge.IsMerged(device.Id));
            else
            {
                card = new DeviceCard(device, Merge.IsMerged(device.Id))
                {
                    RenameRequested = d => OpenDeviceDialog(d, "rename"), TypeRequested = d => OpenDeviceDialog(d, "type"),
                    MergeRequested = d => OpenDeviceDialog(d, "merge"), UnmergeRequested = d => _ = UnmergeAsync(d)
                };
                _cardById.Add(device.Id, card); _cards.Children.Add(card);
            }
        }
        for (var index = 0; index < displays.Count; index++)
        {
            var card = _cardById[displays[index].Id];
            var current = _cards.Children.IndexOf(card);
            if (current == index) continue;
            _cards.Children.RemoveAt(current);
            _cards.Children.Insert(index, card);
        }
        _empty.Visibility = displays.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshMergeChoices();
        _refresh.IsEnabled = !_quitting && !_scenarioChanging && !snapshot.IsSuspended && snapshot.Status != "Resuming" && !snapshot.IsRefreshing && snapshot.OutstandingOperations == 0;
        _spin.Set(!_quitting && snapshot.IsRefreshing);
    }
    public async Task<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_scenarioChanging || _quitting) return new("Busy", _session.Snapshot.Generation, "The window is unavailable for refresh.");
        try { return await _session.RefreshAsync(cancellationToken: cancellationToken); }
        catch (Exception error) { return new("Failed", _session.Snapshot.Generation, $"{error.GetType().Name}: {error.Message}"); }
    }

    private void OpenDeviceDialog(DeviceDisplay device, string kind)
    {
        if (_dialog is not null || _mergeBusy || !CanDeviceActions) return;
        _dialogDevice = device; _dialogKind = kind; _dialogError = _preferencesWarning;
        _nameInput = null; _typeInput = null; _mergeInput = null; _mergeNote = null; _mergeEmpty = null;
        _mergeCandidates = []; _mergeCandidatesSignature = "";
        var body = new StackPanel { Spacing = 12, MinWidth = 320 };
        if (kind == "rename")
        {
            _nameInput = new TextBox { Text = device.Name, MaxLength = 128, PlaceholderText = device.OriginalName };
            AutomationProperties.SetName(_nameInput, UiText.Get("RenameDevice")); body.Children.Add(_nameInput);
        }
        else if (kind == "merge")
        {
            body.Children.Add(new TextBlock { Text = UiText.Get("MergeHint"), TextWrapping = TextWrapping.Wrap });
            _mergeEmpty = new TextBlock { Text = UiText.Get("MergeNone"), TextWrapping = TextWrapping.Wrap, Foreground = UiText.MutedFor(_root) };
            body.Children.Add(_mergeEmpty);
            // Keep the list alive even when initially empty: another connection
            // can become visible while the user follows the empty-state hint.
            _mergeInput = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 370 };
            AutomationProperties.SetName(_mergeInput, UiText.Get("MergeTitle"));
            _mergeInput.SelectionChanged += (_, _) => UpdateMergeChoice();
            body.Children.Add(_mergeInput);
            _mergeNote = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = UiText.MutedFor(_root) };
            body.Children.Add(_mergeNote);
        }
        else
        {
            _typeInput = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 370 };
            var automatic = UiText.Get("Automatic") + UiText.Pick("（", " (") + UiText.Kind(device.KindDetected) + UiText.Pick("）", ")");
            _typeInput.Items.Add(TypeOption(automatic, device.KindDetected, null));
            foreach (var choice in Enum.GetValues<DisplayDeviceKind>().Where(x => x != DisplayDeviceKind.Unknown))
                _typeInput.Items.Add(TypeOption(UiText.Kind(choice), choice, choice));
            _typeInput.SelectedIndex = device.KindSetByUser ? (int)device.Kind : 0;
            body.Children.Add(_typeInput);
        }
        _dialogErrorText = new TextBlock { Text = _dialogError ?? "", TextWrapping = TextWrapping.Wrap, Visibility = _dialogError is null ? Visibility.Collapsed : Visibility.Visible };
        body.Children.Add(_dialogErrorText);
        _dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot, RequestedTheme = _root.ActualTheme,
            Title = UiText.Get(kind switch { "rename" => "RenameDevice", "merge" => "MergeTitle", _ => "ChangeType" }),
            Content = body, PrimaryButtonText = UiText.Get(kind == "merge" ? "Merge" : "Save"), CloseButtonText = UiText.Get("Cancel"),
            IsPrimaryButtonEnabled = kind != "merge",
            SecondaryButtonText = kind == "rename" && device.NameSetByUser ? UiText.Get("RestoreName") : "",
            DefaultButton = ContentDialogButton.Primary
        };
        _dialog.PrimaryButtonClick += async (_, args) =>
        { args.Cancel = true; var deferral = args.GetDeferral(); try { args.Cancel = !await SaveDialogAsync(false); } finally { deferral.Complete(); } };
        _dialog.SecondaryButtonClick += async (_, args) =>
        { args.Cancel = true; var deferral = args.GetDeferral(); try { args.Cancel = !await SaveDialogAsync(true); } finally { deferral.Complete(); } };
        _dialog.Closing += (_, args) => { if (_dialogSaving) args.Cancel = true; };
        RefreshMergeChoices();
        Action? cleanup = null;
        if ((_typeInput ?? _mergeInput) is { } list && _root.XamlRoot is { } xamlRoot)
        {
            // The dialog itself does not scroll: a list taller than the window was cut off. Fit the list
            // to the window instead and let it scroll; follow window resizes while the dialog is open.
            void Fit(XamlRoot sender, XamlRootChangedEventArgs args) => FitTypeList(list, sender);
            FitTypeList(list, xamlRoot);
            xamlRoot.Changed += Fit;
            _dialog.Opened += (_, _) => { if (list.SelectedItem is { } selected) list.ScrollIntoView(selected); };
            cleanup = () => xamlRoot.Changed -= Fit;
        }
        _ = ShowDeviceDialogAsync(_dialog, cleanup);
    }
    // 370 shows all nine choices; the rest of the window height goes to the title, buttons and margins.
    private static void FitTypeList(ListView list, XamlRoot root) => list.MaxHeight = Math.Clamp(root.Size.Height - 220, 120, 370);
    private static ListViewItem TypeOption(string label, DisplayDeviceKind icon, DisplayDeviceKind? choice)
    {
        // Icon and label centred on each other; the label's tight line bounds centre its characters, not its line box.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new FontIcon { Glyph = UiText.Icon(icon), FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight });
        return new ListViewItem { Content = row, Tag = choice };
    }
    private async Task ShowDeviceDialogAsync(ContentDialog dialog, Action? cleanup)
    {
        try { await dialog.ShowAsync(); }
        catch (Exception error) { SetSettingsError(UiText.Get("Unavailable") + " " + error.Message); }
        finally
        {
            cleanup?.Invoke();
            if (ReferenceEquals(_dialog, dialog))
            { _dialog = null; _dialogDevice = null; _dialogKind = null; _nameInput = null; _typeInput = null; _mergeInput = null; _mergeNote = null; _mergeEmpty = null; _mergeCandidates = []; _mergeCandidatesSignature = ""; _dialogErrorText = null; _dialogError = null; }
        }
    }
    private Task<bool> SaveDialogAsync(bool reset)
    {
        if (_dialogSaving || _mergeBusy || _dialog is null || _dialogDevice is null || !CanDeviceActions) return Task.FromResult(false);
        return _dialogSave = SaveDialogCoreAsync(reset);
    }
    private async Task<bool> SaveDialogCoreAsync(bool reset)
    {
        var revision = _deviceUiRevision;
        var dialog = _dialog!;
        var device = _dialogDevice!;
        _dialogSaving = true; dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
        dialog.PrimaryButtonText = UiText.Get("Saving");
        if (_nameInput is not null) _nameInput.IsEnabled = false;
        if (_typeInput is not null) _typeInput.IsEnabled = false;
        if (_mergeInput is not null) _mergeInput.IsEnabled = false;
        try
        {
            if (_dialogKind == "merge")
            {
                var key = (_mergeInput?.SelectedItem as ListViewItem)?.Tag as string;
                if (key is null || Merge.Plan(device, key) is not { } plan) { _dialogError = UiText.Get("Unavailable"); return false; }
                _mergeBusy = true;
                _dialogError = MergeRequested is null ? UiText.Get("Unavailable") : await MergeRequested(plan.Primary, plan.Other);
                if (_dialogError is not null)
                {
                    ShowMergeNotice("error", _dialogError, revision);
                    return false;
                }
                ShowMergeNotice("merged", revision: revision);
                // Show the result: the merged card opens on its connection rows.
                if (revision == _deviceUiRevision && CanDeviceActions && _cardById.TryGetValue(plan.Primary.DeviceId, out var merged)) merged.SetExpanded(true, animate: true);
                return true;
            }
            var previous = _customizations.FirstOrDefault(x => x.DeviceId == device.Id) ?? new(device.Id);
            var change = _dialogKind == "rename"
                ? previous with { Name = reset || string.IsNullOrWhiteSpace(_nameInput!.Text) ? null : _nameInput.Text.Trim() }
                : previous with { Kind = (_typeInput!.SelectedItem as ListViewItem)?.Tag as DisplayDeviceKind? };
            _dialogError = SaveCustomizationRequested is null ? UiText.Get("Unavailable") : await SaveCustomizationRequested(change);
            if (_dialogError is null) return true;
        }
        catch
        {
            _dialogError = UiText.Get("SaveFailed");
            if (_dialogKind == "merge") ShowMergeNotice("error", _dialogError, revision);
        }
        finally
        {
            _dialogSaving = false; _mergeBusy = false;
            if (_dialog is not null)
            {
                _dialog.IsPrimaryButtonEnabled = _dialog.IsSecondaryButtonEnabled = CanDeviceActions;
                _dialog.PrimaryButtonText = UiText.Get(_dialogKind == "merge" ? "Merge" : "Save");
                if (_dialogKind == "merge") RefreshMergeChoices();
                if (_nameInput is not null) _nameInput.IsEnabled = true;
                if (_typeInput is not null) _typeInput.IsEnabled = true;
                if (_mergeInput is not null) _mergeInput.IsEnabled = true;
                if (_dialogErrorText is not null) { _dialogErrorText.Text = _dialogError ?? ""; _dialogErrorText.Visibility = _dialogError is null ? Visibility.Collapsed : Visibility.Visible; }
                if (revision != _deviceUiRevision || !CanDeviceActions) _dialog.Hide();
            }
        }
        return false;
    }

    private ListViewItem MergeOption(DeviceMergeCandidate candidate)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = 44, Padding = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        // A connection that is not connected right now stays selectable but reads quieter.
        var quiet = candidate.State == ConnectionDisplayState.NotConnected ? .6 : 1;
        row.Children.Add(new FontIcon { Glyph = UiText.Icon(candidate.Kind), FontSize = 20, Opacity = quiet, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = candidate.Name, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = quiet });
        var transports = string.Join(UiText.Pick("、", ", "), candidate.Transports.Select(UiText.Transport));
        var detail = (transports.Length > 0 ? transports + " · " : "") + UiText.Presence(candidate.State);
        text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = UiText.MutedFor(_root), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); row.Children.Add(text);
        if (candidate.SameName)
        {
            var tag = new Border
            {
                Child = new TextBlock { Text = UiText.Get("SameName"), FontSize = 12, LineHeight = 16, Foreground = UiText.MutedFor(_root), VerticalAlignment = VerticalAlignment.Center },
                Height = 18, Padding = new Thickness(6, 0, 6, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                BorderBrush = UiText.LineFor(_root), VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tag, 2); row.Children.Add(tag);
        }
        var item = new ListViewItem { Content = row, Tag = candidate.Key };
        AutomationProperties.SetName(item, candidate.Name + " " + detail);
        return item;
    }
    private void UpdateMergeChoice()
    {
        if (_updatingMergeChoices || _dialog is null || _dialogDevice is null || _mergeInput is null) return;
        var key = (_mergeInput.SelectedItem as ListViewItem)?.Tag as string;
        if (_mergeNote is not null) _mergeNote.Text = string.Format(UiText.Get("MergeKeep"), Merge.KeepName(_dialogDevice, key));
        if (!_dialogSaving) _dialog.IsPrimaryButtonEnabled = CanDeviceActions && key is not null && Merge.Plan(_dialogDevice, key) is not null;
    }
    private void RefreshMergeChoices(bool force = false)
    {
        if (_dialogKind != "merge" || _dialogDevice is null || _mergeInput is null || _dialogSaving) return;
        if (_devices.TryGetValue(_dialogDevice.Id, out var current)) _dialogDevice = current;
        var candidates = Merge.For(_dialogDevice);
        var signature = System.Text.Json.JsonSerializer.Serialize(candidates);
        if (force || signature != _mergeCandidatesSignature)
        {
            var key = (_mergeInput.SelectedItem as ListViewItem)?.Tag as string;
            var focused = _mergeInput.Items.OfType<ListViewItem>().FirstOrDefault(item => item.FocusState != FocusState.Unfocused)?.Tag as string;
            _updatingMergeChoices = true;
            try
            {
                _mergeInput.Items.Clear();
                foreach (var candidate in candidates) _mergeInput.Items.Add(MergeOption(candidate));
                _mergeInput.SelectedItem = _mergeInput.Items.OfType<ListViewItem>().FirstOrDefault(item => (string)item.Tag == key);
                if (focused is not null && CanDeviceActions)
                    _mergeInput.Items.OfType<ListViewItem>().FirstOrDefault(item => (string)item.Tag == focused)?.Focus(FocusState.Programmatic);
                _mergeCandidates = candidates;
                _mergeCandidatesSignature = signature;
            }
            finally { _updatingMergeChoices = false; }
        }
        _mergeInput.Visibility = candidates.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_mergeEmpty is not null) _mergeEmpty.Visibility = candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_mergeNote is not null) _mergeNote.Visibility = candidates.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateMergeChoice();
    }
    private Task UnmergeAsync(DeviceDisplay device)
    {
        if (_mergeBusy || _dialogSaving || _dialog is not null || !CanDeviceActions || !Merge.IsMerged(device.Id)) return Task.CompletedTask;
        return _mergeOperation = UnmergeCoreAsync(device);
    }
    private async Task UnmergeCoreAsync(DeviceDisplay device)
    {
        var revision = _deviceUiRevision;
        _mergeBusy = true;
        try
        {
            var error = UnmergeRequested is null ? UiText.Get("Unavailable") : await UnmergeRequested(device.Id);
            ShowMergeNotice(error is null ? "unmerged" : "error", error, revision);
        }
        catch { ShowMergeNotice("error", UiText.Get("SaveFailed"), revision); }
        finally { _mergeBusy = false; }
    }
    private Task UndoMergeAsync()
    {
        if (_mergeBusy || _dialogSaving || _dialog is not null || !CanDeviceActions || _noticeKind is not ("merged" or "unmerged")) return Task.CompletedTask;
        return _mergeOperation = UndoMergeCoreAsync();
    }
    private async Task UndoMergeCoreAsync()
    {
        var revision = _deviceUiRevision;
        var retryKind = _noticeKind;
        _mergeBusy = true; _undo.IsEnabled = false;
        try
        {
            var error = UndoMergeRequested is null ? UiText.Get("Unavailable") : await UndoMergeRequested();
            if (error is null) CloseMergeNotice(); else ShowMergeNotice("error", error, revision, retryKind);
        }
        catch { ShowMergeNotice("error", UiText.Get("SaveFailed"), revision, retryKind); }
        finally { _mergeBusy = false; _undo.IsEnabled = !_quitting && !_preparingInstallation; }
    }
    // A failed undo keeps its retry action. The store still validates that the
    // saved settings match the operation before allowing any retry to restore it.
    private void ShowMergeNotice(string kind, string? error = null, long? revision = null, string? retryKind = null)
    {
        if (!CanDeviceActions || (revision is { } expected && expected != _deviceUiRevision)) return;
        _noticeKind = retryKind ?? kind;
        _mergeNotice.Severity = kind == "error" ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        _mergeNotice.Message = kind switch { "merged" => UiText.Get("Merged"), "unmerged" => UiText.Get("Unmerged"), _ => error ?? UiText.Get("SaveFailed") };
        _mergeNotice.ActionButton = kind == "error" && retryKind is null ? null : _undo;
        _mergeNotice.IsOpen = true;
    }
    private void CloseMergeNotice() { _noticeKind = null; _mergeNotice.IsOpen = false; }
    public async Task FlushDeviceChangesAsync()
    {
        await _dialogSave;
        await _mergeOperation;
        if (_colorSave is { } colorSave) await colorSave;
    }
    internal DeviceMergeState CaptureMergeState() => new(Merge.Groups, _dialogKind == "merge" ? _dialogDevice?.Id : null,
        _dialogKind == "merge" ? _mergeCandidates : [],
        (_mergeInput?.SelectedItem as ListViewItem)?.Tag as string, _mergeNote?.Text,
        _mergeBusy || (_dialogKind == "merge" && _dialogSaving), _mergeNotice.IsOpen ? _mergeNotice.Message : null, _noticeKind,
        CanDeviceActions && !_mergeBusy && !_dialogSaving && _dialog is null && _mergeNotice.IsOpen && _mergeNotice.ActionButton is not null && _undo.IsEnabled,
        _dialogKind == "merge" ? _dialogError : null);

    internal async Task ApplyUiTestActionAsync(string action, string? value, string? deviceId)
    {
        if (_quitting) throw new InvalidOperationException("The app is quitting.");
        switch (action)
        {
            case "settings": ShowSettings(true); return;
            case "back": ShowSettings(false); return;
            case "refresh-start": _ = RefreshAsync(); return;
            case "expand": case "collapse": case "expand-animated": case "collapse-animated":
                if (!_cardById.TryGetValue(deviceId ?? "", out var card)) throw new InvalidOperationException("Device card not found.");
                card.SetExpanded(action.StartsWith("expand", StringComparison.Ordinal), action.EndsWith("-animated", StringComparison.Ordinal)); return;
            case "rename-open": case "type-open": case "merge-open":
                if (_dialog is not null) throw new InvalidOperationException("A device dialog is already open.");
                if (!_devices.TryGetValue(deviceId ?? "", out var device)) throw new InvalidOperationException("Device not found.");
                OpenDeviceDialog(device, action switch { "rename-open" => "rename", "merge-open" => "merge", _ => "type" }); return;
            case "unmerge": case "unmerge-start":
                if (!_devices.TryGetValue(deviceId ?? "", out var mergedDevice) || !Merge.IsMerged(mergedDevice.Id)) throw new InvalidOperationException("Merged device not found.");
                if (action == "unmerge-start") { _ = UnmergeAsync(mergedDevice); return; }
                await UnmergeAsync(mergedDevice); return;
            case "merge-undo": case "merge-undo-start":
                if (_noticeKind is not ("merged" or "unmerged")) throw new InvalidOperationException("There is nothing to undo.");
                if (action == "merge-undo-start") { _ = UndoMergeAsync(); return; }
                await UndoMergeAsync(); return;
            case "merge-notice-close": CloseMergeNotice(); return;
            case "dialog-value":
                if (_dialogSaving) throw new InvalidOperationException("A save is in progress.");
                if (_nameInput is not null) _nameInput.Text = value ?? "";
                else if (_typeInput is not null)
                {
                    if (value is null or "Automatic") _typeInput.SelectedIndex = 0;
                    else if (Enum.TryParse<DisplayDeviceKind>(value, out var kind) && kind != DisplayDeviceKind.Unknown && Enum.IsDefined(kind)) _typeInput.SelectedIndex = (int)kind;
                    else throw new ArgumentException("Unknown device type.");
                }
                else if (_mergeInput is not null)
                {
                    var choice = _mergeInput.Items.OfType<ListViewItem>().FirstOrDefault(item => (string)item.Tag == value);
                    _mergeInput.SelectedItem = choice ?? throw new ArgumentException("Unknown merge choice.");
                }
                else throw new InvalidOperationException("Open a device dialog first.");
                return;
            case "type-scroll-last":
                if (_typeInput is null || _typeInput.Items.Count == 0) throw new InvalidOperationException("Open the type dialog first.");
                _typeInput.ScrollIntoView(_typeInput.Items[^1]); return;
            case "dialog-save": case "dialog-reset":
                if (_dialog is null) throw new InvalidOperationException("Open a device dialog first.");
                if (action == "dialog-reset" && _dialogKind != "rename") throw new InvalidOperationException("Only names have a reset action.");
                if (await SaveDialogAsync(action == "dialog-reset")) _dialog?.Hide(); return;
            case "dialog-save-start":
                if (_dialog is null) throw new InvalidOperationException("Open a device dialog first.");
                _ = SaveDialogForTestAsync(); return;
            case "dialog-cancel":
                if (_dialogSaving) throw new InvalidOperationException("A save is in progress."); _dialog?.Hide(); return;
            case "theme":
                if (_preparingInstallation || _dialog is not null || _colorSave is { IsCompleted: false }) throw new InvalidOperationException("Settings are busy.");
                if (!Enum.TryParse<MainColorMode>(value, out var mode) || !Enum.IsDefined(mode)) throw new ArgumentException("Unknown color mode.");
                _colorMode.SelectedIndex = (int)mode; if (_colorSave is not null) await _colorSave; return;
            default: throw new ArgumentException("Unknown main-window test action.");
        }
    }
    internal MainUiState CaptureUiState() => new(_settingsVisible ? "settings" : "devices", _root.ActualTheme.ToString(),
        _dialogKind, _dialogDevice?.Id, _nameInput?.Text ?? (_typeInput is null ? null : ((_typeInput.SelectedItem as ListViewItem)?.Tag?.ToString() ?? "Automatic"))
            ?? ((_mergeInput?.SelectedItem as ListViewItem)?.Tag as string),
        _dialogSaving, _dialogError, _typeInput?.MaxHeight, _typeInput?.ActualHeight,
        _root.XamlRoot?.Size.Height ?? 0, _spin.Capture(), LastTypeOptionVisible(),
        _preparingInstallation, _settingsError.Visibility == Visibility.Visible ? _settingsError.Text : null,
        TrayPriority.IsEnabled, _colorMode.IsEnabled, _back.IsEnabled);
    private async Task SaveDialogForTestAsync() { if (await SaveDialogAsync(false)) _dialog?.Hide(); }
    internal void ResizeForTest(int width, int height) => AppWindow.Resize(new(width, height));
    internal FrameworkElement? DialogForTest => _dialog;
    internal FrameworkElement CardsForTest => _cards;
    internal ScrollViewer DeviceScrollForTest => _deviceScroll;
    internal ScrollViewer PageScrollForTest => _settingsVisible ? _settingsPage : _deviceScroll;
    internal void SetScenarioBannerForTest(bool visible)
    {
        if (_scenarioBanner is null) throw new InvalidOperationException("Only a fictional scenario has a banner.");
        // The title bar and tray continue to identify the data as fictional while this layout-only strip is hidden.
        _scenarioBanner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
    internal DeviceCard? CardForTest(string id) => _cardById.GetValueOrDefault(id);
    private bool? LastTypeOptionVisible()
    {
        if (_typeInput is null) return null;
        if (_typeInput.Items.Count == 0 || _typeInput.ContainerFromItem(_typeInput.Items[^1]) is not FrameworkElement last || last.ActualHeight <= 0) return false;
        var scroll = FindScroll(_typeInput);
        if (scroll is null || scroll.ViewportHeight <= 0) return false;
        var bounds = last.TransformToVisual(scroll).TransformBounds(new Windows.Foundation.Rect(0, 0, last.ActualWidth, last.ActualHeight));
        return bounds.Top >= -0.01 && bounds.Bottom <= scroll.ViewportHeight + 0.01;
    }
    private static ScrollViewer? FindScroll(DependencyObject element)
    {
        if (element is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindScroll(VisualTreeHelper.GetChild(element, index)) is { } found) return found;
        return null;
    }
    internal AppTestView CaptureTestView()
    {
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("View capture requires the UI thread.");
        return new(_lastVersion, "", _refresh.IsEnabled, UiText.Get("Refresh"),
            _cards.Children.OfType<DeviceCard>().Select(card => card.CaptureTestState(_cardById.First(x => ReferenceEquals(x.Value, card)).Key)).ToArray(),
            _scenarioBanner?.Text, _settingsVisible ? "settings" : "devices", _root.ActualTheme.ToString());
    }
    public void ShowScenario(string name, bool changing)
    {
        if (_scenarioBanner is null) return;
        _scenarioChanging = changing;
        _scenarioBanner.Text = $"演示数据 · {name}{(changing ? " · 正在切换" : "")}";
        Title = $"Battery Manager {AppBuildInfo.Version} — 演示数据 ({name})";
        _titleText.Text = Title;
        TrayPriority.IsEnabled = !changing && !_quitting && !_preparingInstallation;
        Apply(_session.Snapshot);
    }
    public void RefreshTheme()
    {
        _shell.Background = _mica ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : UiText.BackgroundFor(_root);
        _titleText.Foreground = UiText.InkFor(_root);
        _title.Foreground = UiText.InkFor(_root);
        _empty.Foreground = UiText.MutedFor(_root); _settingsError.Foreground = UiText.LowFor(_root);
        if (_scenarioBanner is not null) _scenarioBanner.Foreground = UiText.LowFor(_root);
        foreach (var card in _cardById.Values) card.RefreshTheme();
        foreach (var card in _settingCards) { card.Background = UiText.CardFor(_root); card.BorderBrush = UiText.LineFor(_root); }
        foreach (var text in _settingTexts) text.Foreground = UiText.InkFor(_root);
        foreach (var text in _settingSubTexts) text.Foreground = UiText.MutedFor(_root);
        TrayPriority.RefreshTheme();
        Management.RefreshTheme();
        if (_dialog is not null) _dialog.RequestedTheme = _root.ActualTheme;
        RefreshMergeChoices(force: true);
        PaintCaptionButtons();
    }

    private bool TryUseMica()
    {
        try
        {
            if (!Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()) return false;
            SystemBackdrop = new MicaBackdrop();
            return true;
        }
        catch (Exception) { return false; }
    }

    private void UpdateTitleBarInsets()
    {
        // Caption button insets are physical pixels; keep title text outside them at any DPI.
        var scale = _shell.XamlRoot?.RasterizationScale ?? 1;
        _titleBar.Padding = new Thickness(AppWindow.TitleBar.LeftInset / scale, 0,
            AppWindow.TitleBar.RightInset / scale, 0);
        // A move to a monitor with another scale also lands here; the icons reload only when the DPI changed.
        _windowIcons.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    // The system draws the caption buttons over our content; give them the same subtle fills as the page.
    private void PaintCaptionButtons()
    {
        try
        {
            var bar = AppWindow.TitleBar;
            var dark = _root.ActualTheme == ElementTheme.Dark;
            var foreground = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 27, 27, 27);
            bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            bar.ButtonForegroundColor = foreground;
            bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(255, 120, 120, 120) : Color.FromArgb(255, 150, 150, 150);
            bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x0F, 255, 255, 255) : Color.FromArgb(0x09, 0, 0, 0);
            bar.ButtonHoverForegroundColor = foreground;
            bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x0A, 255, 255, 255) : Color.FromArgb(0x06, 0, 0, 0);
            bar.ButtonPressedForegroundColor = foreground;
        }
        catch (Exception) { }
    }
    public void PrepareToExit()
    {
        Management.Stop();
        TrayPriority.StopAcceptingChanges();
        _quitting = true; _refresh.IsEnabled = _settings.IsEnabled = TrayPriority.IsEnabled = _colorMode.IsEnabled = _undo.IsEnabled = false;
        _spin.Stop();
        foreach (var card in _cardById.Values) card.StopMotion();
        if (!_dialogSaving) _dialog?.Hide();
    }
    public void CloseForExit() { _allowClose = true; Close(); }
    private static Button IconButton(string glyph, string label)
    {
        var button = UiText.MakeSubtle(new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(4) });
        AutomationProperties.SetName(button, UiText.Get(label)); ToolTipService.SetToolTip(button, UiText.Get(label)); return button;
    }
    private TextBlock SectionTitle(string key, bool first = false)
    {
        var text = new TextBlock { Text = UiText.Get(key), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(1, first ? 0 : 24, 0, 4) };
        _settingTexts.Add(text); return text;
    }
    private Border SettingCard(string glyph, string title, FrameworkElement action, string? description = null)
    {
        action.VerticalAlignment = VerticalAlignment.Center;
        // Windows 11 settings card: 68 minimum height, 16 padding, 20 icon with 20 before the text.
        var grid = new Grid { MinHeight = 66, Padding = new Thickness(16) };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20, Margin = new Thickness(2, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center });
        var labels = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var name = new TextBlock { Text = title, FontSize = 14, LineHeight = 20 }; _settingTexts.Add(name); labels.Children.Add(name);
        if (description is not null)
        {
            var hint = new TextBlock { Text = description, FontSize = 12, LineHeight = 16, TextWrapping = TextWrapping.Wrap };
            _settingSubTexts.Add(hint); labels.Children.Add(hint);
        }
        Grid.SetColumn(labels, 1); grid.Children.Add(labels); Grid.SetColumn(action, 2); grid.Children.Add(action);
        var border = new Border { Child = grid, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) }; _settingCards.Add(border); return border;
    }
}


