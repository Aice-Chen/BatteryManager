using BatteryManager.Core;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace BatteryManager.App;

internal sealed record TrayPreferenceRow(string DeviceId, string Name, string DisplayName, string Status,
    bool HasContent = false, bool ContentAttached = false, double ActualHeight = 0,
    double ActualWidth = 0, double Opacity = 0, string? Visibility = null, bool ContainerMatches = false);
internal sealed record TrayPreferenceOption(string DeviceId, string Name, string DisplayName);
internal sealed record TrayPreferencesDraft(
    IReadOnlyList<TrayPreferenceRow> Rows,
    IReadOnlyList<TrayPreferenceOption> Available,
    string? Error,
    bool CanSave,
    bool IsSaving,
    bool WillReset,
    bool IsOpen,
    double ActualWidth,
    double ActualHeight,
    string? Theme = null,
    string? ResetButtonText = null,
    IReadOnlyList<string>? DraftOrder = null,
    bool CanReorder = false,
    string? FocusedDeviceId = null,
    int NativeDragStarts = 0, int NativeDragCompletions = 0, string? LastNativeDropResult = null,
    string? CurrentDeviceId = null, bool ResetConfirmationOpen = false,
    bool IsDragging = false, bool DragCanCommit = false, bool WindowVisible = true);

/// <summary>A list row that can be picked up: the hand cursor tells the pointer the row can be dragged.</summary>
internal sealed class GrabRow : ListViewItem
{
    public GrabRow(bool draggable)
    {
        if (draggable) ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}

/// <summary>Tray device priority, edited in place on the settings page.
/// Two groups: devices with a set order (drag to reorder, "−" takes one out) and the other devices,
/// which the tray chooses automatically ("+" adds one at the end of the order). Every change is saved
/// at once; a failed save puts the last saved order back and says why.</summary>
internal sealed class TrayPriorityPanel : UserControl
{
    private const int MaximumPreferences = 64;
    private readonly List<TrayPreference> _draft = [];
    private List<TrayPreference> _saved = [];
    private Func<IReadOnlyList<TrayPreference>, bool, bool, Task<string?>>? _saveCallback;
    // Native ListView reordering, as on other Windows 11 reorderable lists. Alt+Up / Alt+Down and the
    // row's context menu move the focused row without a pointer.
    private readonly ObservableCollection<ListViewItem> _items = [];
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.None, CanReorderItems = true, CanDragItems = true, AllowDrop = true,
        // Item clicks do nothing, but enabling them gives every row the pressed state the moment it is grabbed.
        IsItemClickEnabled = true
    };
    private readonly ObservableCollection<ListViewItem> _otherItems = [];
    private readonly ListView _others = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
    private readonly Border _frame = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    private readonly FontIcon _headerIcon = new() { Glyph = "", FontSize = 20, Margin = new Thickness(2, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
    // Tight line bounds: the right-hand value sat about 2 px below the title block on its left.
    private readonly TextBlock _current = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260, TextLineBounds = TextLineBounds.Tight };
    private readonly TextBlock _emptyNote = new() { Text = UiText.Pick("暂无设备", "No devices yet"), FontSize = 13, Margin = new Thickness(58, 10, 16, 12) };
    private readonly TextBlock _othersEmpty = new() { Text = UiText.Pick("没有其他设备", "No other devices"), FontSize = 13, Margin = new Thickness(58, 10, 16, 12) };
    private readonly TextBlock _resetTitle = new() { FontSize = 14 };
    private readonly TextBlock _resetHint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button _reset = new() { CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
    private readonly Flyout _confirm = new();
    private readonly TextBlock _confirmText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
    private readonly Button _confirmReset = new() { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(58, 0, 16, 12) };
    private readonly List<TextBlock> _inkTexts = [];
    private readonly List<TextBlock> _mutedTexts = [];
    // Row texts are rebuilt with the rows; kept apart so old rows are not repainted forever.
    private readonly List<TextBlock> _rowInk = [];
    private readonly List<TextBlock> _rowMuted = [];
    private readonly List<Border> _lines = [];
    private readonly List<(TrayPreference Preference, TextBlock Name, TextBlock Status)> _renderedRows = [];
    private readonly List<Button> _rowButtons = [];
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _icons = new(StringComparer.Ordinal);
    private Dictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);
    private string? _loadWarning;
    private string? _saveError;
    private string? _currentId;
    private bool _saving;
    private bool _dirty;
    private bool _clearSuspendedRequested;
    private bool _resetting;
    private bool _hasRendered;
    private bool _dragging;
    private bool _dragAccepted;
    private bool _pageVisible;
    private bool _windowVisible = true;
    private bool _stopping;
    private bool _resetConfirmationOpen;
    private string? _draggedId;
    private int _dragRevision;
    private int _nativeDragStarts;
    private int _nativeDragCompletions;
    private string? _lastNativeDropResult;
    private Task _commit = Task.CompletedTask;

    public TrayPriorityPanel()
    {
        var stack = new StackPanel();
        // Header: the settings-card metrics (16 padding, 20 icon with 20 before the text).
        var header = new Grid { MinHeight = 66, Padding = new Thickness(16) };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(_headerIcon);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        labels.Children.Add(Ink(new TextBlock { Text = UiText.Get("TrayIcon"), FontSize = 14, LineHeight = 20 }));
        Grid.SetColumn(labels, 1); header.Children.Add(labels);
        Grid.SetColumn(_current, 2); header.Children.Add(Muted(_current));
        stack.Children.Add(header);

        stack.Children.Add(GroupHeader(UiText.Pick("优先顺序", "Order"), UiText.Pick("拖动调整", "Drag to reorder")));
        PrepareList(_list);
        _list.ItemsSource = _items;
        _list.DragItemsStarting += (_, args) =>
        {
            if (!CanEdit || _dragging) { args.Cancel = true; return; }
            _nativeDragStarts++;
            _dragging = true;
            _dragAccepted = true;
            _draggedId = _items.FirstOrDefault(item => args.Items.Contains(item)
                || item.Content is not null && args.Items.Contains(item.Content))?.Tag as string;
        };
        _list.DragItemsCompleted += (_, args) =>
        {
            _nativeDragCompletions++;
            _lastNativeDropResult = args.DropResult.ToString();
            CompleteDrag(args.DropResult == DataPackageOperation.Move, _draggedId ?? FocusedRowId());
        };
        _list.PreviewKeyDown += OnListKey;
        stack.Children.Add(_list);
        stack.Children.Add(Muted(_emptyNote));

        stack.Children.Add(GroupHeader(UiText.Pick("其他设备", "Other devices"), UiText.Pick("自动选择", "Chosen automatically")));
        PrepareList(_others);
        _others.ItemsSource = _otherItems;
        stack.Children.Add(_others);
        stack.Children.Add(Muted(_othersEmpty));

        var footer = new Grid { MinHeight = 52, Padding = new Thickness(58, 8, 16, 8), ColumnSpacing = 16 };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var resetLabels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        resetLabels.Children.Add(Ink(_resetTitle)); resetLabels.Children.Add(Muted(_resetHint));
        footer.Children.Add(resetLabels);
        Grid.SetColumn(_reset, 1); footer.Children.Add(_reset);
        stack.Children.Add(Line(footer));
        _reset.Click += (_, _) => RequestReset();
        var confirmBody = new StackPanel { Spacing = 12 };
        confirmBody.Children.Add(_confirmText);
        confirmBody.Children.Add(_confirmReset);
        _confirm.Content = confirmBody;
        _confirmText.Text = UiText.Pick("先备份，再把所有设置恢复为默认。", "The file is backed up, then all settings return to their defaults.");
        _confirmReset.Content = UiText.Pick("重置", "Reset");
        _confirmReset.Click += (_, _) => ConfirmReset();
        _confirm.Closed += (_, _) => _resetConfirmationOpen = false;
        stack.Children.Add(_message);

        _frame.Child = stack;
        Content = _frame;
        ActualThemeChanged += (_, _) => RefreshTheme();
        Loaded += (_, _) => RefreshInteractiveView();
        Unloaded += (_, _) => InvalidateInteractiveView();
        IsEnabledChanged += (_, _) => RefreshInteractiveView();
        RefreshTheme();
        RenderDraft();
        UpdateEnabled();
    }

    private bool CanInteract => !_stopping && _pageVisible && _windowVisible && IsLoaded && IsEnabled;
    private bool CanEdit => CanInteract && !_resetting && _saveCallback is not null && _loadWarning is null;

    public void SetPageVisible(bool visible)
    {
        _pageVisible = visible;
        RefreshInteractiveView();
    }

    public void SetWindowVisible(bool visible)
    {
        _windowVisible = visible;
        RefreshInteractiveView();
    }

    public void StopAcceptingChanges()
    {
        _stopping = true;
        InvalidateInteractiveView();
    }

    private void RefreshInteractiveView()
    {
        if (!CanInteract) { InvalidateInteractiveView(); return; }
        RenderDraft();
    }

    private void InvalidateInteractiveView()
    {
        ++_dragRevision;
        _dragging = false;
        _dragAccepted = false;
        _draggedId = null;
        _resetConfirmationOpen = false;
        _confirm.Hide();
        // Do not clear containers while WinUI is cancelling a drag. Rebuild when the page is shown.
        UpdateEnabled();
    }

    /// <summary>Supplies the saved order, any settings-file problem and the save path. Called once after loading.</summary>
    public void Initialize(IReadOnlyList<TrayPreference> saved, string? loadWarning,
        Func<IReadOnlyList<TrayPreference>, bool, bool, Task<string?>> saveCallback)
    {
        _draft.Clear();
        _draft.AddRange(saved.Take(MaximumPreferences).DistinctBy(p => p.DeviceId, StringComparer.Ordinal));
        _saved = [.. _draft];
        _loadWarning = loadWarning;
        if (_draft.Count != saved.Count)
            _loadWarning ??= UiText.Pick("已保存的设置无法完整读取。", "Saved preferences could not be fully read.");
        _saveCallback = saveCallback;
        _saveError = null;
        RenderDraft();
        UpdateEnabled();
    }

    /// <summary>The device the tray number shows now, for the header.</summary>
    public void SetCurrent(string? deviceId)
    {
        if (_currentId == deviceId) return;
        _currentId = deviceId;
        UpdateCurrent();
    }

    internal bool AddDevice(string id)
    {
        if (!CanEdit || _dragging || !_devices.TryGetValue(id, out var device) || !device.Visible || _draft.Any(p => p.DeviceId == id)) return false;
        if (_draft.Count >= MaximumPreferences)
        {
            _saveError = UiText.Pick("最多可设置 64 台优先设备。请先移除一台。", "You can order up to 64 devices. Take one out before adding another.");
            UpdateEnabled();
            return false;
        }
        _draft.Add(new TrayPreference(device.Id, device.Name));
        DraftChanged();
        return true;
    }

    internal bool MoveDevice(string id, int delta)
    {
        if (!CanEdit || _dragging || delta is not (-1 or 1)) return false;
        var index = _draft.FindIndex(p => p.DeviceId == id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _draft.Count) return false;
        (_draft[index], _draft[target]) = (_draft[target], _draft[index]);
        DraftChanged();
        return true;
    }

    /// <summary>Applies a validated order to the draft and collection, and saves it. Native drag completion
    /// rebuilds visual containers separately after the framework has finished releasing them.</summary>
    internal bool ReorderDevices(IReadOnlyList<string> order)
    {
        if (_dragging) return false;
        return ApplyOrder(order);
    }

    private bool ApplyOrder(IReadOnlyList<string> order)
    {
        var current = _draft.Select(p => p.DeviceId).ToArray();
        if (!CanEdit || order.Count != current.Length || !order.Order(StringComparer.Ordinal).SequenceEqual(current.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            RenderDraft();
            return false;
        }
        var changed = !order.SequenceEqual(current, StringComparer.Ordinal);
        var byId = _draft.ToDictionary(p => p.DeviceId, StringComparer.Ordinal);
        _draft.Clear();
        _draft.AddRange(order.Select(id => byId[id]));
        SynchronizeItems(order);
        if (!changed) return false;
        _saveError = null;
        UpdateEnabled();
        Commit();
        return true;
    }

    // Models the collection operations used by native ListView reordering, not a pointer gesture.
    internal bool ReorderRemoveInsertForTest(IReadOnlyList<string> order, bool commit = true)
    {
        var current = _draft.Select(p => p.DeviceId).ToArray();
        if (!CanEdit || _dragging || order.Count != current.Length || !order.Order(StringComparer.Ordinal).SequenceEqual(current.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            return false;
        if (!BeginDragForTest(order.FirstOrDefault())) return false;
        ReorderDuringDragForTest(order);
        return EndDragForTest(commit);
    }

    internal bool BeginDragForTest(string? deviceId)
    {
        if (!CanEdit || _dragging || !_items.Any(item => (string)item.Tag == deviceId)) return false;
        _dragging = true;
        _dragAccepted = true;
        _draggedId = deviceId;
        return true;
    }

    internal bool ReorderDuringDragForTest(IReadOnlyList<string> order)
    {
        var current = _items.Select(item => (string)item.Tag).ToArray();
        if (!_dragging || !_dragAccepted || order.Count != current.Length || !order.Order(StringComparer.Ordinal).SequenceEqual(current.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            return false;
        for (var index = 0; index < order.Count; index++)
        {
            var from = _items.IndexOf(_items.First(item => (string)item.Tag == order[index]));
            if (from == index) continue;
            var item = _items[from];
            _items.RemoveAt(from);
            _items.Insert(index, item);
        }
        return true;
    }

    internal bool EndDragForTest(bool commit)
    {
        if (!_dragging) return false;
        CompleteDrag(commit, _draggedId);
        return true;
    }

    private void CompleteDrag(bool commit, string? focusId)
    {
        // WinUI native reordering removes and reinserts the same item. A self-container can
        // lose Content and its container mapping during that removal (reproduced on this runtime).
        // Capture its new order now, but replace the containers only after DragItemsCompleted
        // returns: WinUI still performs its own drag cleanup after raising that event.
        if (!_dragging) return;
        var accepted = _dragAccepted;
        _dragAccepted = false;
        _dragging = true;
        if (commit && accepted && CanEdit) ApplyOrder(_items.Select(item => (string)item.Tag).ToArray());
        var revision = ++_dragRevision;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (revision != _dragRevision) return;
            _dragging = false;
            _draggedId = null;
            if (!CanInteract) return;
            RenderDraft();
            if (focusId is not null) FocusRow(focusId);
        }))
        {
            _dragging = false;
            _draggedId = null;
        }
    }

    private void SynchronizeItems(IReadOnlyList<string> order)
    {
        if (_items.Count != order.Count || order.Any(id => !_items.Any(item => (string)item.Tag == id)))
        {
            RenderDraft();
            return;
        }
        for (var index = 0; index < order.Count; index++)
        {
            var currentIndex = _items.IndexOf(_items.First(item => (string)item.Tag == order[index]));
            if (currentIndex != index) _items.Move(currentIndex, index);
        }
    }

    internal bool RemoveDevice(string id)
    {
        if (!CanEdit || _dragging) return false;
        var index = _draft.FindIndex(p => p.DeviceId == id);
        if (index < 0) return false;
        _draft.RemoveAt(index);
        DraftChanged();
        return true;
    }

    /// <summary>Clears the order (the tray then chooses automatically). With a damaged settings file this is
    /// the reset path: the file is backed up and replaced.</summary>
    internal bool ResetDraft()
    {
        if (!CanInteract || _dragging || _resetting || _saveCallback is null) return false;
        var replaceInvalid = _loadWarning is not null;
        _draft.Clear();
        _saveError = null;
        _clearSuspendedRequested = true;
        RenderDraft();
        Commit(replaceInvalid);
        return true;
    }

    internal bool RequestReset()
    {
        if (!CanInteract || _dragging || _resetting || _saveCallback is null) return false;
        if (_loadWarning is null) return ResetDraft();
        _resetConfirmationOpen = true;
        _confirm.ShowAt(_reset);
        return true;
    }

    internal bool ConfirmReset()
    {
        if (!_resetConfirmationOpen || !CanInteract || _resetting || _loadWarning is null) return false;
        _resetConfirmationOpen = false;
        _confirm.Hide();
        return ResetDraft();
    }

    internal bool CancelResetConfirmation()
    {
        if (!_resetConfirmationOpen) return false;
        _resetConfirmationOpen = false;
        _confirm.Hide();
        return true;
    }

    /// <summary>Waits for the save in progress, if any. True when the current order is saved.</summary>
    internal async Task<bool> SaveDraftAsync()
    {
        await FlushPendingChangesAsync();
        return _saveError is null && _loadWarning is null && _draft.Select(p => p.DeviceId).SequenceEqual(_saved.Select(p => p.DeviceId), StringComparer.Ordinal);
    }

    internal async Task FlushPendingChangesAsync()
    {
        Task pending;
        do { pending = _commit; await pending; } while (!ReferenceEquals(pending, _commit));
    }

    private void DraftChanged()
    {
        _saveError = null;
        RenderDraft();
        Commit();
    }

    // Saves are serialised: a change made while one is running is saved right after it.
    private void Commit(bool replaceInvalid = false)
    {
        if (_saving) { _dirty = true; return; }
        _commit = CommitAsync(replaceInvalid);
    }

    private async Task CommitAsync(bool replaceInvalid)
    {
        if (_saveCallback is null) return;
        _saving = true;
        _resetting = replaceInvalid;
        UpdateEnabled();
        try
        {
            do
            {
                _dirty = false;
                var order = _draft.ToArray();
                var clearSuspended = _clearSuspendedRequested;
                _clearSuspendedRequested = false;
                string? error;
                try { error = await _saveCallback(order, replaceInvalid, clearSuspended); }
                catch (Exception) { error = UiText.Pick("保存未完成，请稍后重试。", "Saving did not complete. Please try again."); }
                if (error is not null)
                {
                    // Show what is actually in effect: the last saved order.
                    _saveError = error;
                    _draft.Clear();
                    _draft.AddRange(_saved);
                    _dirty = false;
                    _clearSuspendedRequested = false;
                    // A native drag can still be in progress over the failed order. Reject its
                    // later commit, but retain the container barrier until its completion event.
                    _dragAccepted = false;
                    break;
                }
                _saved = [.. order];
                if (replaceInvalid) _loadWarning = null;
                replaceInvalid = false;
            } while (_dirty);
        }
        finally
        {
            _saving = false;
            _resetting = false;
            UpdateEnabled();
            RenderDraft();
        }
    }

    /// <summary>Known devices, with the names and types the user chose (for labels and icons).</summary>
    internal void UpdateDevices(IReadOnlyList<DeviceState> devices, IReadOnlyList<DeviceDisplay>? displays = null)
    {
        var namesChanged = false;
        // Keep the last supplied label if a device in the order disconnects.
        foreach (var device in devices)
        {
            if (!_displayNames.TryGetValue(device.Id, out var name) || name != device.Name) namesChanged = true;
            _displayNames[device.Id] = device.Name;
        }
        foreach (var display in displays ?? [])
        {
            var icon = UiText.Icon(display.Kind);
            if (!_icons.TryGetValue(display.Id, out var known) || known != icon) namesChanged = true;
            _icons[display.Id] = icon;
        }
        // A hidden Pending device is still known. Keep its actual state for saved
        // priorities; visibility only determines which new candidates are offered.
        var current = devices.DistinctBy(d => d.Id, StringComparer.Ordinal)
            .ToDictionary(d => d.Id, StringComparer.Ordinal);
        var changed = namesChanged || !_hasRendered || current.Count != _devices.Count || current.Any(pair =>
            !_devices.TryGetValue(pair.Key, out var previous) || previous.Name != pair.Value.Name
            || previous.Visible != pair.Value.Visible || previous.Connection != pair.Value.Connection || previous.BatteryPercent != pair.Value.BatteryPercent
            || HasUsableBattery(previous) != HasUsableBattery(pair.Value));
        _devices = current;
        if (changed) RenderDraft();
        else UpdateCurrent();
    }

    internal TrayPreferencesDraft CaptureDraft()
    {
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Preference capture requires the UI thread.");
        var rowsById = _renderedRows.ToDictionary(row => row.Preference.DeviceId, StringComparer.Ordinal);
        return new(
            _list.Items.OfType<ListViewItem>().Where(item => rowsById.ContainsKey((string)item.Tag)).Select(item =>
            {
                var row = rowsById[(string)item.Tag];
                var attached = false;
                if (item.Content is DependencyObject content)
                    for (var parent = VisualTreeHelper.GetParent(content); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                        if (ReferenceEquals(parent, _list)) { attached = true; break; }
                return new TrayPreferenceRow(row.Preference.DeviceId, CurrentName(row.Preference), row.Name.Text, row.Status.Text,
                    item.Content is not null, attached, item.ActualHeight, item.ActualWidth,
                    item.Opacity, item.Visibility.ToString(), ReferenceEquals(_list.ContainerFromItem(item), item));
            }).ToArray(),
            _otherItems.Select(item => (string)item.Tag).Select(id => new TrayPreferenceOption(id,
                _devices.GetValueOrDefault(id)?.Name ?? _displayNames.GetValueOrDefault(id) ?? id, Label(id))).ToArray(),
            _message.Visibility == Visibility.Visible ? _message.Text : null,
            CanEdit, _saving, _loadWarning is not null, CanInteract, ActualWidth, ActualHeight,
            ActualTheme.ToString(), _reset.Content as string, _draft.Select(p => p.DeviceId).ToArray(),
            _list.CanReorderItems && _list.CanDragItems && _list.AllowDrop, FocusedRowId(),
            _nativeDragStarts, _nativeDragCompletions, _lastNativeDropResult, _currentId, _resetConfirmationOpen,
            _dragging, _dragAccepted, _windowVisible);
    }

    private void RenderDraft()
    {
        // Live device updates may arrive while WinUI owns the dragged containers.
        // Keep them in place until the drag commits or cancels, then render the newest cache.
        if (_dragging || !CanInteract) return;
        var focusedId = FocusedRowId();
        _hasRendered = true;
        var labels = CreateDisplayNames();
        _items.Clear();
        _otherItems.Clear();
        _renderedRows.Clear();
        _rowButtons.Clear();
        _rowInk.Clear();
        _rowMuted.Clear();
        foreach (var preference in _draft)
            _items.Add(Row(preference.DeviceId, labels[preference.DeviceId], ordered: true, preference));
        var chosen = _draft.Select(p => p.DeviceId).ToHashSet(StringComparer.Ordinal);
        foreach (var device in _devices.Values.Where(d => d.Visible && !chosen.Contains(d.Id)).OrderBy(d => labels[d.Id], StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Id, StringComparer.Ordinal))
            _otherItems.Add(Row(device.Id, labels[device.Id], ordered: false, null));
        _list.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _emptyNote.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _others.Visibility = _otherItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _othersEmpty.Visibility = _otherItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCurrent();
        UpdateEnabled();
        RefreshTheme();
        if (focusedId is not null) FocusRow(focusedId);
    }

    private ListViewItem Row(string id, string label, bool ordered, TrayPreference? preference)
    {
        var name = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 14 };
        ToolTipService.SetToolTip(name, preference is null ? label : CurrentName(preference));
        var status = new TextBlock { Text = DescribeAvailability(id), FontSize = 12 };
        if (preference is not null) _renderedRows.Add((preference, name, status));
        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _rowInk.Add(name); _rowMuted.Add(status);
        details.Children.Add(name);
        details.Children.Add(status);
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Drag handle on ordered rows, as on reorderable Windows 11 lists. The whole row can be dragged.
        if (ordered) row.Children.Add(new FontIcon { Glyph = "", FontSize = 16, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        var icon = new FontIcon { Glyph = _icons.GetValueOrDefault(id) ?? UiText.Icon(_devices.GetValueOrDefault(id)?.Kind ?? DeviceKind.Unknown), FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        var offline = !_devices.TryGetValue(id, out var device) || device.Connection == ConnectionState.Offline;
        icon.Opacity = name.Opacity = offline ? .55 : 1;
        Grid.SetColumn(icon, 1); row.Children.Add(icon);
        Grid.SetColumn(details, 2); row.Children.Add(details);
        var action = UiText.MakeSubtle(new Button
        {
            Content = new FontIcon { Glyph = ordered ? "" : "", FontSize = 14 }, Width = 32, Height = 32,
            Padding = new Thickness(0), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center
        });
        var actionName = ordered ? UiText.Pick("移出优先顺序", "Take out of the order") : UiText.Pick("加入优先顺序", "Add to the order");
        AutomationProperties.SetName(action, actionName);
        ToolTipService.SetToolTip(action, actionName);
        action.Click += (_, _) => { if (ordered) RemoveDevice(id); else AddDevice(id); };
        _rowButtons.Add(action);
        Grid.SetColumn(action, 3); row.Children.Add(action);
        var item = new GrabRow(ordered)
        {
            Tag = id, Content = row, MinHeight = 52, Padding = new Thickness(16, 6, 12, 6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, ContextFlyout = ordered ? OrderedMenu(id) : OtherMenu(id)
        };
        AutomationProperties.SetName(item, label + UiText.Pick("，", ", ") + status.Text);
        return item;
    }

    private MenuFlyout OrderedMenu(string id)
    {
        var menu = new MenuFlyout();
        var up = new MenuFlyoutItem { Text = UiText.Get("Up"), Icon = new FontIcon { Glyph = "" } };
        var down = new MenuFlyoutItem { Text = UiText.Get("Down"), Icon = new FontIcon { Glyph = "" } };
        var remove = new MenuFlyoutItem { Text = UiText.Pick("移出优先顺序", "Take out of the order"), Icon = new FontIcon { Glyph = "" } };
        up.Click += (_, _) => { if (MoveDevice(id, -1)) FocusRow(id); };
        down.Click += (_, _) => { if (MoveDevice(id, 1)) FocusRow(id); };
        remove.Click += (_, _) => RemoveDevice(id);
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
        menu.Opening += (_, _) =>
        {
            var index = _draft.FindIndex(p => p.DeviceId == id);
            up.IsEnabled = CanEdit && index > 0;
            down.IsEnabled = CanEdit && index >= 0 && index < _draft.Count - 1;
            remove.IsEnabled = CanEdit && index >= 0;
        };
        return menu;
    }

    private MenuFlyout OtherMenu(string id)
    {
        var menu = new MenuFlyout();
        var add = new MenuFlyoutItem { Text = UiText.Pick("加入优先顺序", "Add to the order"), Icon = new FontIcon { Glyph = "" } };
        add.Click += (_, _) => AddDevice(id);
        menu.Items.Add(add);
        menu.Opening += (_, _) => add.IsEnabled = CanEdit;
        return menu;
    }

    private void OnListKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
        if (!InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down)) return;
        if (FocusedRowId() is not { } id) return;
        if (MoveDevice(id, e.Key == VirtualKey.Up ? -1 : 1)) FocusRow(id);
        e.Handled = true;
    }

    private string? FocusedRowId()
    {
        var element = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (element is not null and not ListViewItem) element = VisualTreeHelper.GetParent(element);
        return element is ListViewItem { Tag: string id } item && _items.Contains(item) ? id : null;
    }

    // Rows are rebuilt after a keyboard or menu move; put focus back on the moved row.
    private void FocusRow(string id)
    {
        var revision = _dragRevision;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (revision != _dragRevision || !CanInteract || _dragging) return;
            if (_items.FirstOrDefault(item => (string)item.Tag == id) is not { } row) return;
            row.StartBringIntoView();
            row.Focus(FocusState.Keyboard);
        });
    }

    private void UpdateCurrent()
    {
        var name = _currentId is null ? null : _devices.TryGetValue(_currentId, out var device) ? device.Name : _displayNames.GetValueOrDefault(_currentId);
        _current.Text = name is null ? "" : UiText.Pick("当前：", "Now: ") + name;
    }

    private void UpdateEnabled()
    {
        var canEdit = CanEdit;
        foreach (var button in _rowButtons) button.IsEnabled = canEdit && !_dragging;
        _list.CanReorderItems = _list.CanDragItems = _list.AllowDrop = canEdit;
        _reset.IsEnabled = CanInteract && !_dragging && !_resetting && _saveCallback is not null && (_draft.Count > 0 || _loadWarning is not null);
        if (_loadWarning is null)
        {
            _resetTitle.Text = UiText.Pick("全部自动", "All automatic");
            _resetHint.Text = "";
            _reset.Content = UiText.Pick("清除顺序", "Clear order");
        }
        else
        {
            _resetTitle.Text = UiText.Pick("设置无法读取", "Settings could not be read");
            _resetHint.Text = _loadWarning;
            _reset.Content = UiText.Pick("重置全部设置", "Reset all settings");
        }
        _resetHint.Visibility = _resetHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var error = _saveError;
        _message.Text = error ?? "";
        _message.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private string DescribeAvailability(string id)
    {
        if (!_devices.TryGetValue(id, out var device) || device.Connection == ConnectionState.Offline)
            return UiText.Pick("未连接", "Not connected");
        if (device.Connection is not (ConnectionState.Online or ConnectionState.Recent))
            return UiText.Connection(device.Connection);
        return HasUsableBattery(device)
            ? UiText.Pick("可用 · ", "Available · ") + device.BatteryPercent!.Value.ToString(CultureInfo.InvariantCulture) + "%"
            : UiText.Pick("暂无电量", "No battery reading");
    }

    private static bool HasUsableBattery(DeviceState device) => device.BatteryPercent is >= 0 and <= 100
        && device.Freshness is BatteryFreshness.Current or BatteryFreshness.Recent or BatteryFreshness.Retained;

    private string Label(string id) => CreateDisplayNames().GetValueOrDefault(id) ?? id;

    private Dictionary<string, string> CreateDisplayNames()
    {
        var names = _draft.Select(p => p with { Name = CurrentName(p) })
            .Concat(_devices.Values.Where(d => d.Visible).Select(d => new TrayPreference(d.Id, d.Name)))
            .DistinctBy(p => p.DeviceId, StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in names.GroupBy(p => DisplayName(p.Name), StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(p => p.DeviceId, StringComparer.Ordinal).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                var name = DisplayName(ordered[i].Name);
                result.Add(ordered[i].DeviceId, ordered.Length > 1 ? $"({i + 1}) {name}" : name);
            }
        }
        return result;
    }

    private string CurrentName(TrayPreference preference) =>
        _displayNames.GetValueOrDefault(preference.DeviceId) ?? preference.Name;

    private static string DisplayName(string original)
    {
        var name = string.Join(" ", original.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) return UiText.Pick("未命名设备", "Unnamed device");
        var elements = StringInfo.ParseCombiningCharacters(name);
        return elements.Length > 64 ? name[..elements[63]] + "…" : name;
    }

    private static void PrepareList(ListView list)
    {
        // The settings page scrolls; the lists show every row and do not scroll on their own.
        ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.Padding = new Thickness(0, 2, 0, 4);
    }

    private Border GroupHeader(string title, string hint)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(20, 12, 16, 4) };
        row.Children.Add(Ink(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom }));
        row.Children.Add(Muted(new TextBlock { Text = hint, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom }));
        return Line(row);
    }

    private Border Line(FrameworkElement child)
    {
        var border = new Border { Child = child, BorderThickness = new Thickness(0, 1, 0, 0) };
        _lines.Add(border);
        return border;
    }

    private TextBlock Ink(TextBlock text) { _inkTexts.Add(text); return text; }
    private TextBlock Muted(TextBlock text) { _mutedTexts.Add(text); return text; }

    public void RefreshTheme()
    {
        _frame.Background = UiText.CardFor(this);
        _frame.BorderBrush = UiText.LineFor(this);
        _headerIcon.Foreground = UiText.InkFor(this);
        foreach (var text in _inkTexts.Concat(_rowInk)) text.Foreground = UiText.InkFor(this);
        foreach (var text in _mutedTexts.Concat(_rowMuted)) text.Foreground = UiText.MutedFor(this);
        foreach (var line in _lines) line.BorderBrush = UiText.LineFor(this);
        _message.Foreground = UiText.LowFor(this);
    }
}
