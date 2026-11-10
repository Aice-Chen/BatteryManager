using BatteryManager.Core;

namespace BatteryManager.App;

/// <summary>One choice in the "合并连接" dialog: a single record seen this session, or an existing merged group
/// (offered only when the card being merged is not merged itself, so two groups are never joined).</summary>
internal sealed record DeviceMergeCandidate(string Key, string TargetId, bool IsGroup, string Name, DisplayDeviceKind Kind,
    IReadOnlyList<DeviceTransport> Transports, ConnectionDisplayState State, bool SameName);
internal sealed record DeviceMergePlan(DeviceLinkMember Primary, DeviceLinkMember Other, string KeepName);
internal sealed record DeviceMergeState(IReadOnlyList<DeviceLinkGroup> Groups, string? DialogDeviceId,
    IReadOnlyList<DeviceMergeCandidate> Candidates, string? Selected, string? KeepName, bool IsSaving,
    string? Notice, string? NoticeKind, bool CanUndo, string? Error);

/// <summary>Session memory for merging. A record becomes a candidate once it has been shown in this run;
/// saved group members are known by their saved name. Names here label choices; they never decide identity.
/// Nothing in this class is persisted.</summary>
internal sealed class DeviceMergeCandidates
{
    private sealed record Seen(string Id, string Name, DeviceKind Kind, IReadOnlyList<DeviceTransport> Transports, ConnectionState Connection,
        bool DetectedNow, bool ObservedInSession);
    private readonly Dictionary<string, Seen> _seen = new(StringComparer.Ordinal);
    private IReadOnlyList<DeviceLinkGroup> _groups = [];
    private Dictionary<string, DeviceCustomization> _custom = new(StringComparer.Ordinal);

    public IReadOnlyList<DeviceLinkGroup> Groups => _groups;

    public void Update(IReadOnlyList<DeviceState> detectedDevices, IReadOnlyList<DeviceLinkGroup> groups,
        IReadOnlyList<DeviceCustomization>? customizations = null)
    {
        _groups = DeviceLinks.CopyValidated(groups);
        _custom = (customizations ?? []).ToDictionary(item => item.DeviceId, StringComparer.Ordinal);
        foreach (var id in _seen.Keys.Where(id => !_seen[id].ObservedInSession && GroupOf(id) is null).ToArray()) _seen.Remove(id);
        foreach (var id in _seen.Keys.ToArray()) _seen[id] = _seen[id] with { DetectedNow = false, Connection = ConnectionState.Offline };
        foreach (var device in detectedDevices)
        {
            // Discovery can briefly expose hidden Windows history before a scan closes. Only a visible
            // record becomes a candidate; a known one keeps following its state while it is hidden.
            if (!device.Visible && (!_seen.TryGetValue(device.Id, out var known) || !known.ObservedInSession)) continue;
            var transports = DeviceDisplayMapper.DetectedTransports(device)
                .Concat(_seen.GetValueOrDefault(device.Id)?.Transports ?? []).Distinct().Order().ToArray();
            _seen[device.Id] = new(device.Id, device.Name, device.Kind, transports, device.Connection, true, true);
        }
        foreach (var member in _groups.SelectMany(group => group.Members))
        {
            if (!_seen.TryGetValue(member.DeviceId, out var seen))
                _seen[member.DeviceId] = new(member.DeviceId, member.Name, member.Kind, member.Transports, ConnectionState.Offline, false, false);
            else
                _seen[member.DeviceId] = seen with
                {
                    Name = seen.ObservedInSession ? seen.Name : member.Name,
                    Kind = seen.ObservedInSession ? seen.Kind : member.Kind,
                    Transports = seen.Transports.Concat(member.Transports).Distinct().Order().ToArray()
                };
        }
    }

    public bool IsMerged(string deviceId) => _groups.Any(group => group.PrimaryDeviceId == deviceId);

    /// <summary>Saved methods keep the merged card stable across disconnects and
    /// restarts. Only current state evidence can mark a method as responding.</summary>
    public DeviceDisplay Enrich(DeviceDisplay display)
    {
        var group = _groups.FirstOrDefault(group => group.PrimaryDeviceId == display.Id);
        if (group is null) return display;
        var actual = display.Connections.ToDictionary(connection => connection.Transport);
        var methods = group.Members.SelectMany(member => KnownTransports(member.DeviceId))
            .Concat(actual.Keys).Distinct().Order().ToArray();
        return display with
        {
            Connections = Array.AsReadOnly(methods.Select(transport => actual.GetValueOrDefault(transport)
                ?? new ConnectionDisplay(transport, ConnectionDisplayState.NotConnected)).ToArray())
        };
    }

    public IReadOnlyList<DeviceLinkGroup> GroupsWithKnownTransports() => DeviceLinks.CopyValidated(_groups.Select(group => group with
    {
        Members = group.Members.Select(member => member with { Transports = KnownTransports(member.DeviceId) }).ToArray()
    }).ToArray());

    public IReadOnlyList<DeviceMergeCandidate> For(DeviceDisplay card)
    {
        var own = GroupOf(card.Id);
        if (own is not null && own.PrimaryDeviceId != card.Id) return [];
        var excluded = new HashSet<string>(own?.Members.Select(member => member.DeviceId) ?? [card.Id], StringComparer.Ordinal) { card.Id };
        var list = new List<DeviceMergeCandidate>();
        if (own is null)
            foreach (var group in _groups)
            {
                var states = group.Members.Select(member => State(member.DeviceId)).ToArray();
                var transports = group.Members.SelectMany(member => KnownTransports(member.DeviceId)).Distinct().Order().ToArray();
                var originals = group.Members.Select(member => member.Name);
                list.Add(new("group:" + group.PrimaryDeviceId, group.PrimaryDeviceId, true, Name(group.PrimaryDeviceId), Kind(group.PrimaryDeviceId),
                    transports, states.Min(), Same(Name(group.PrimaryDeviceId), card.Name) || originals.Any(name => Same(name, card.OriginalName))));
            }
        foreach (var seen in _seen.Values.Where(seen => seen.ObservedInSession && GroupOf(seen.Id) is null && !excluded.Contains(seen.Id)))
        {
            list.Add(new(seen.Id, seen.Id, false, Name(seen.Id), Kind(seen.Id), seen.Transports, State(seen.Id),
                Same(Name(seen.Id), card.Name) || Same(seen.Name, card.OriginalName)));
        }
        return list.OrderByDescending(item => item.SameName).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The card the merge starts from keeps its name and settings. Choosing an existing group
    /// joins that group instead, and the group keeps its own.</summary>
    public DeviceMergePlan? Plan(DeviceDisplay card, string key)
    {
        var choice = For(card).FirstOrDefault(item => item.Key == key);
        if (choice is null || Member(card.Id) is not { } self) return null;
        if (choice.IsGroup)
            return Member(choice.TargetId) is { } primary ? new(primary, self, Name(choice.TargetId)) : null;
        return Member(choice.TargetId) is { } other ? new(self, other, card.Name) : null;
    }

    public string KeepName(DeviceDisplay card, string? key) =>
        key is not null && For(card).FirstOrDefault(item => item.Key == key) is { IsGroup: true } group ? Name(group.TargetId) : card.Name;

    // Saved metadata is the detected name and type; user names and types stay in customizations.
    private DeviceLinkMember? Member(string id)
    {
        if (_seen.TryGetValue(id, out var seen)) return new(id, seen.Name, seen.Kind) { Transports = KnownTransports(id) };
        return _groups.SelectMany(group => group.Members).FirstOrDefault(member => member.DeviceId == id);
    }
    private IReadOnlyList<DeviceTransport> KnownTransports(string id) => _seen.GetValueOrDefault(id)?.Transports
        ?? _groups.SelectMany(group => group.Members).FirstOrDefault(member => member.DeviceId == id)?.Transports
        ?? Array.Empty<DeviceTransport>();
    private DeviceLinkGroup? GroupOf(string id) => _groups.FirstOrDefault(group => group.Members.Any(member => member.DeviceId == id));
    private string Name(string id) => _custom.GetValueOrDefault(id)?.Name is { Length: > 0 } custom ? custom
        : _seen.TryGetValue(id, out var seen) ? seen.Name : id;
    private DisplayDeviceKind Kind(string id)
    {
        if (_custom.GetValueOrDefault(id)?.Kind is { } chosen && chosen != DisplayDeviceKind.Unknown) return chosen;
        return (_seen.GetValueOrDefault(id)?.Kind ?? DeviceKind.Unknown) switch
        {
            DeviceKind.Headphones => DisplayDeviceKind.Headphones, DeviceKind.Keyboard => DisplayDeviceKind.Keyboard,
            DeviceKind.Mouse => DisplayDeviceKind.Mouse, DeviceKind.Other => DisplayDeviceKind.Other, _ => DisplayDeviceKind.Unknown
        };
    }
    private ConnectionDisplayState State(string id) => _seen.GetValueOrDefault(id) is { DetectedNow: true } seen
        ? seen.Connection switch
        {
            ConnectionState.Online or ConnectionState.Recent => ConnectionDisplayState.Connected,
            ConnectionState.Pending => ConnectionDisplayState.NoResponse,
            _ => ConnectionDisplayState.NotConnected
        }
        : ConnectionDisplayState.NotConnected;
    private static bool Same(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.CurrentCultureIgnoreCase);
}
