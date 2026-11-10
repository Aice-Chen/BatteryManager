using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace BatteryManager.Core;

/// <summary>Owns evidence and produces an atomic, time-aware read model.</summary>
public sealed class StateStore
{
    private readonly object _gate = new();
    private readonly StateOptions _options;
    private readonly Dictionary<string, Entity> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguousContainers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _seen = new(StringComparer.Ordinal);
    private IReadOnlyList<DeviceLinkGroup> _links = Array.Empty<DeviceLinkGroup>();
    private readonly Dictionary<string, Entity> _linkedEntities = new(StringComparer.Ordinal);
    private long _generation = long.MinValue;
    private long _entitySequence;
    private long _version;
    private bool _open;
    private bool _manual;
    private TimeSpan _lastElapsed = TimeSpan.MinValue;
    private string _signature = "";

    public StateStore(StateOptions? options = null)
    {
        _options = options ?? StateOptions.Default;
        if (_options.ConnectionLifetime <= TimeSpan.Zero || _options.ConnectionGrace < TimeSpan.Zero ||
            _options.BatteryLifetime <= TimeSpan.Zero || _options.ActiveBatteryLifetime <= TimeSpan.Zero ||
            _options.PendingVisibilityLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public void BeginScan(long generation, bool manual)
    {
        lock (_gate)
        {
            if (generation <= _generation) return;
            _generation = generation;
            _manual = manual;
            _open = true;
            _seen.Clear();
        }
    }

    public void EndScan(long generation)
    {
        lock (_gate)
        {
            if (generation == _generation) _open = false;
        }
    }

    public void SetDeviceLinks(IReadOnlyList<DeviceLinkGroup> groups, ClockInstant instant)
    {
        var copied = DeviceLinks.CopyValidated(groups);
        lock (_gate)
        {
            var now = AdvanceTime(instant.Elapsed);
            _links = copied;
            var retained = copied.Select(group => group.PrimaryDeviceId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _linkedEntities.Keys.Where(id => !retained.Contains(id)).ToArray()) _linkedEntities.Remove(id);
            SyncLinkedEntities(now);
            BuildSnapshot(new(instant.Utc, now));
        }
    }

    public void SetDeviceLinks(IReadOnlyList<DeviceLinkGroup> groups, DateTimeOffset now) =>
        SetDeviceLinks(groups, ClockInstant.FromUtc(now));

    /// <summary>
    /// Legacy UTC-only timeline. Elapsed stamps are intentionally ignored; do not
    /// mix this overload with ClockInstant calls on the same store. Production
    /// sessions use ClockInstant to separate real UTC labels from elapsed lifetimes.
    /// </summary>
    public bool Apply(long generation, SourceBatch batch, DateTimeOffset now) =>
        Apply(generation, WithoutElapsedStamps(batch), ClockInstant.FromUtc(now));

    public bool Apply(long generation, SourceBatch batch, ClockInstant instant)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_gate)
        {
            if (!_open || generation != _generation || string.IsNullOrWhiteSpace(batch.Source)) return false;
            var now = AdvanceTime(instant.Elapsed);
            var version = _version;
            if (!_seen.TryGetValue(batch.Source, out var seen))
                _seen[batch.Source] = seen = new(StringComparer.Ordinal);

            foreach (var observation in batch.Observations)
            {
                if (observation.Source != batch.Source || string.IsNullOrWhiteSpace(observation.EndpointId)) continue;
                var endpointId = NormalizeEndpoint(observation.EndpointId);
                seen.Add(endpointId);
                ApplyObservation(observation, endpointId, instant, now);
            }

            if (batch.Error is not null || batch.FailureStatus is not null)
            {
                foreach (var endpoint in _endpoints.Values)
                {
                    if (!endpoint.Sources.TryGetValue(batch.Source, out var source)) continue;
                    source.Attempt = batch.FailureStatus ?? ReadStatus.Error;
                    source.Error = batch.Error;
                    source.AttemptedAt = instant.Utc;
                    source.AttemptedElapsed = now;
                    source.AttemptGeneration = _generation;
                    source.BatteryReturned = false;
                    foreach (var field in source.Fields.Values)
                    {
                        field.Attempt = source.Attempt;
                        field.Error = source.Error;
                        field.AttemptedAt = instant.Utc;
                        field.AttemptedElapsed = now;
                        field.AttemptGeneration = _generation;
                    }
                }
            }
            else if (batch.Complete)
            {
                foreach (var endpoint in _endpoints.Values)
                {
                    if (seen.Contains(endpoint.Id) || !endpoint.Sources.TryGetValue(batch.Source, out var source)) continue;
                    if (batch.Authoritative)
                    {
                        source.PathUntil = Earlier(source.PathUntil, now);
                        if (source.PositiveUntil is { } positiveUntil)
                        {
                            var ended = positiveUntil < now ? positiveUntil : now;
                            source.PositiveUntil = ended;
                            if (_manual) source.GraceUntil = now;
                            else if (!source.MembershipMissing) source.GraceUntil = ended + _options.ConnectionGrace;
                        }
                        source.MembershipMissing = true;
                        RememberEndedConnection(endpoint, now);
                    }
                    source.Attempt = ReadStatus.Missing;
                    source.Error = null;
                    source.AttemptedAt = instant.Utc;
                    source.AttemptedElapsed = now;
                    source.AttemptGeneration = _generation;
                    source.BatteryReturned = false;
                    foreach (var field in source.Fields.Values)
                    {
                        field.Attempt = ReadStatus.Missing;
                        field.Error = null;
                        field.AttemptedAt = instant.Utc;
                        field.AttemptedElapsed = now;
                        field.AttemptGeneration = _generation;
                    }
                }
            }

            // A property can arrive before its same-scan AEP connection evidence.
            foreach (var endpoint in _endpoints.Values) QualifyCaches(endpoint, now);
            BuildSnapshot(new(instant.Utc, now));
            return _version != version;
        }
    }

    public StateSnapshot Snapshot(DateTimeOffset now) => Snapshot(ClockInstant.FromUtc(now));

    public StateSnapshot Snapshot(ClockInstant now)
    {
        lock (_gate) return BuildSnapshot(new(now.Utc, AdvanceTime(now.Elapsed)));
    }

    public DateTimeOffset? NextExpiry(DateTimeOffset now) =>
        NextExpiryDelay(ClockInstant.FromUtc(now)) is { } delay ? now + delay : null;

    public TimeSpan? NextExpiryDelay(ClockInstant instant)
    {
        lock (_gate)
        {
            var now = AdvanceTime(instant.Elapsed);
            TimeSpan? next = null;
            void Consider(TimeSpan? value)
            {
                if (value > now && (next is null || value < next)) next = value;
            }
            foreach (var endpoint in _endpoints.Values)
            foreach (var source in endpoint.Sources.Values)
            {
                Consider(source.PositiveUntil);
                Consider(source.GraceUntil);
                Consider(source.PathUntil);
                foreach (var field in source.Fields.Values)
                {
                    if (field.Qualified is not { } fact) continue;
                    Consider(fact.ExpiresElapsed);
                    if (fact.Sample.Evidence == BatteryEvidence.ActiveRead)
                        Consider(fact.ObservedElapsed + _options.ActiveBatteryLifetime);
                }
            }
            foreach (var entity in _entities.Values)
                Consider(ConfirmedUntil(entity) + _options.PendingVisibilityLifetime);
            foreach (var entity in _linkedEntities.Values)
                Consider(ConfirmedUntil(entity) + _options.PendingVisibilityLifetime);
            return next - now;
        }
    }

    private void ApplyObservation(DeviceObservation observation, string endpointId, ClockInstant instant, TimeSpan now)
    {
        var receivedElapsed = observation.ReceivedElapsed ?? MapUtc(observation.ReceivedAt, instant);
        if (NormalizeAddress(observation.BluetoothAddress) is { } observedAddress &&
            _aliases.TryGetValue("endpoint:" + endpointId, out var knownEntityId) &&
            _entities[knownEntityId] is { } knownEntity && knownEntity.Addresses.Count > 0 &&
            !knownEntity.Addresses.Contains(observedAddress))
        {
            knownEntity.IdentityError = "An endpoint with a conflicting Bluetooth address was ignored.";
            return;
        }
        if (_endpoints.TryGetValue(endpointId, out var priorEndpoint) &&
            priorEndpoint.Sources.TryGetValue(observation.Source, out var priorSource) &&
            receivedElapsed < priorSource.LastReceivedElapsed) return;
        var entity = ResolveEntity(observation, endpointId);
        var endpoint = _endpoints[endpointId];
        if (!endpoint.Sources.TryGetValue(observation.Source, out var source))
            endpoint.Sources[observation.Source] = source = new();
        if (receivedElapsed < source.LastReceivedElapsed) return;

        var validTime = receivedElapsed <= now;
        if (validTime) source.LastReceivedElapsed = receivedElapsed;
        source.Attempt = observation.Status;
        source.Error = observation.Error;
        source.AttemptedAt = observation.ReceivedAt;
        source.AttemptedElapsed = receivedElapsed;
        source.AttemptGeneration = _generation;
        source.BatteryReturned = observation.Battery is not null;
        source.MembershipMissing = false;
        if (!validTime)
        {
            source.Attempt = ReadStatus.Error;
            source.Error = "Observation time is in the future.";
        }
        if (!string.IsNullOrWhiteSpace(observation.Name) &&
            (string.IsNullOrEmpty(entity.Name) || observation.Kind != DeviceKind.Unknown || entity.Kind == DeviceKind.Unknown))
            entity.Name = observation.Name;
        if (observation.Kind != DeviceKind.Unknown) entity.Kind = observation.Kind;

        if (validTime)
        {
            if (observation.Connected == true)
            {
                RememberEndedConnection(endpoint, receivedElapsed);
                source.PositiveAt = receivedElapsed;
                source.PositiveUntil = receivedElapsed + _options.ConnectionLifetime;
                source.GraceUntil = source.PositiveUntil + _options.ConnectionGrace;
                source.PositiveGeneration = _generation;
            }
            else if (observation.Connected == false)
            {
                source.NegativeAt = receivedElapsed;
                RetainBefore(endpoint, receivedElapsed);
            }
            if (observation.PathPresent) source.PathUntil = receivedElapsed + _options.ConnectionLifetime;
        }

        if (observation.Battery is not { } sample)
        {
            foreach (var field in source.Fields.Values)
            {
                field.Attempt = source.Attempt == ReadStatus.Success ? ReadStatus.Missing : source.Attempt;
                field.Error = source.Error;
                field.AttemptedAt = source.AttemptedAt;
                field.AttemptedElapsed = source.AttemptedElapsed;
                field.AttemptGeneration = _generation;
            }
            return;
        }
        var key = sample.Property + "\0" + sample.Component;
        if (!source.Fields.TryGetValue(key, out var slot)) source.Fields[key] = slot = new();
        var readAt = sample.ReadAt ?? observation.ReceivedAt;
        var readElapsed = sample.ReadElapsed ?? receivedElapsed + (readAt - observation.ReceivedAt);
        slot.Latest = new(sample, observation.ReceivedAt, receivedElapsed, readElapsed, _generation);
        slot.Attempt = source.Attempt;
        slot.Error = source.Error;
        slot.AttemptedAt = source.AttemptedAt;
        slot.AttemptedElapsed = source.AttemptedElapsed;
        slot.AttemptGeneration = _generation;
        if (sample.Percent is < 0 or > 100)
        {
            slot.Attempt = ReadStatus.Error;
            slot.Error = "Battery percentage is outside 0–100.";
            return;
        }
        if (!validTime || observation.Status != ReadStatus.Success) return;
        if (readElapsed > receivedElapsed || readElapsed > now)
        {
            slot.Attempt = ReadStatus.Error;
            slot.Error = "Battery read time is in the future.";
            return;
        }
        if (sample.Evidence == BatteryEvidence.SystemCache) return;
        var observedAt = sample.ObservedAt ?? (sample.Evidence == BatteryEvidence.ActiveRead ? readAt : null);
        if (observedAt is null) return;

        TimeSpan observedElapsed;
        if (sample.Evidence == BatteryEvidence.SourceTimestamp)
        {
            // A source's own timestamp is still evidence about measurement age.
            // Repeating it preserves its first elapsed-time translation, including
            // after expiry, so changing Windows time cannot revive that sample.
            if (observedAt > readAt) return;
            if (slot.Qualified is { Sample.Evidence: BatteryEvidence.SourceTimestamp } previous &&
                previous.ObservedAt is { } previousTime)
            {
                if (observedAt < previousTime) return;
                observedElapsed = observedAt == previousTime
                    ? previous.ObservedElapsed!.Value : readElapsed - (readAt - observedAt.Value);
            }
            else observedElapsed = readElapsed - (readAt - observedAt.Value);
        }
        else
        {
            // Active readers stamp the actual local I/O completion. Its UTC can
            // legitimately be ahead of the later publication UTC after a correction.
            observedElapsed = sample.ReadElapsed is not null ? readElapsed : readElapsed - (readAt - observedAt.Value);
        }
        if (observedElapsed > now) return;
        if (slot.Qualified?.ObservedElapsed is { } previousObserved && observedElapsed < previousObserved) return;
        slot.Qualified = new(sample, observedAt, readAt, observedElapsed, readElapsed,
            observedElapsed + _options.BatteryLifetime);
    }

    private void QualifyCaches(Endpoint endpoint, TimeSpan now)
    {
        var negative = endpoint.Sources.Values.Max(x => x.NegativeAt);
        var connectedThisScan = endpoint.Sources.Values.Any(x =>
            x.PositiveGeneration == _generation && x.PositiveUntil > now &&
            x.PositiveAt <= now && (negative is null || x.PositiveAt > negative));
        if (!connectedThisScan) return;
        foreach (var source in endpoint.Sources.Values)
        foreach (var field in source.Fields.Values)
        {
            if (field.Latest is not { } latest || latest.Generation != _generation ||
                latest.Sample.Evidence != BatteryEvidence.SystemCache || latest.Sample.Percent is < 0 or > 100 ||
                field.Attempt != ReadStatus.Success || latest.ReceivedElapsed > now) continue;
            var readElapsed = latest.ReadElapsed;
            if (readElapsed > latest.ReceivedElapsed || readElapsed > now ||
                (endpoint.RetainBefore is { } barrier && readElapsed <= barrier) ||
                (field.Qualified is { } previous && readElapsed < previous.ReadElapsed)) continue;
            field.Qualified = new(latest.Sample, null, SampleReadAt(latest), null, readElapsed,
                readElapsed + _options.BatteryLifetime);
        }
    }

    private StateSnapshot BuildSnapshot(ClockInstant instant)
    {
        var now = instant.Elapsed;
        // Capture revocations at their evidence time before closed-scan cleanup
        // removes the endpoints whose final window the linked view still needs.
        foreach (var entity in _entities.Values) UpdateConfirmationWindows(entity, now);
        SyncLinkedEntities(now);
        // Sources arrive independently within a scan. Keep their connection
        // barriers and unqualified caches until the whole round has closed.
        if (!_open) RemoveExpiredOfflineDevices(now);
        SyncLinkedEntities(now);
        var detected = _entities.Values.OrderBy(x => x.Sequence).Select(entity => BuildDeviceState(entity, instant, linked: false)).ToList();
        var linkedMembers = _links.SelectMany(group => group.Members).Select(member => member.DeviceId).ToHashSet(StringComparer.Ordinal);
        var states = detected.Where(device => !linkedMembers.Contains(device.Id)).ToList();
        states.AddRange(_linkedEntities.Values.OrderBy(entity => entity.Sequence).Select(entity => BuildDeviceState(entity, instant, linked: true)));
        var signature = Signature(states) + "|detected|" + Signature(detected) + "|links|" + LinkSignature();
        if (_signature != signature)
        {
            _signature = signature;
            _version++;
        }
        return new(_version, instant.Utc, new ReadOnlyCollection<DeviceState>(states))
        { DetectedDevices = new ReadOnlyCollection<DeviceState>(detected) };
    }

    private DeviceState BuildDeviceState(Entity entity, ClockInstant instant, bool linked)
    {
            var now = instant.Elapsed;
            UpdateConfirmationWindows(entity, now);
            var connection = ConnectionState.Offline;
            var preferredConnection = entity.Endpoints.Select(id => ResolveConnection(_endpoints[id], now))
                .OrderByDescending(ConnectionRank).FirstOrDefault();
            var readings = new List<BatteryReading>();
            var attempts = new List<DeviceSourceAttempt>();
            var candidates = new List<RankedReading>();
            string? latestError = entity.IdentityError;
            var latestErrorAt = TimeSpan.MinValue;
            foreach (var endpointId in entity.Endpoints.Order(StringComparer.Ordinal))
            {
                var endpoint = _endpoints[endpointId];
                var endpointConnection = ResolveConnection(endpoint, now);
                if (ConnectionRank(endpointConnection) > ConnectionRank(connection)) connection = endpointConnection;
                foreach (var pair in endpoint.Sources.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    var source = pair.Value;
                    var fieldFailure = source.Fields.Values.FirstOrDefault(field => field.AttemptGeneration == source.AttemptGeneration &&
                        field.Attempt is ReadStatus.Error or ReadStatus.Timeout);
                    var status = fieldFailure?.Attempt ?? source.Attempt;
                    if (status == ReadStatus.Success && !source.BatteryReturned) status = ReadStatus.Missing;
                    attempts.Add(new(endpointId, pair.Key, status, fieldFailure?.Error ?? source.Error,
                        source.AttemptGeneration == _generation,
                        source.Fields.Values.Any(field => (field.Latest?.Sample.Precision ?? field.Qualified?.Sample.Precision)?.StartsWith("coarse:", StringComparison.Ordinal) == true),
                        endpointConnection));
                    if (source.Error is not null && source.AttemptedElapsed >= latestErrorAt)
                    {
                        latestError = source.Error;
                        latestErrorAt = source.AttemptedElapsed;
                    }
                    foreach (var field in source.Fields.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value))
                    {
                        if (field.Error is not null && field.AttemptedElapsed >= latestErrorAt)
                        {
                            latestError = field.Error;
                            latestErrorAt = field.AttemptedElapsed;
                        }
                        if (field.Qualified is { } fact)
                        {
                            var freshness = Freshness(fact, field, endpoint, now);
                            // Linking is a presentation relationship, never permission to
                            // treat another transport's retained value as a new measurement.
                            if (linked && freshness is BatteryFreshness.Current or BatteryFreshness.Recent &&
                                (endpointConnection is not (ConnectionState.Online or ConnectionState.Recent) ||
                                 ConnectionRank(endpointConnection) < ConnectionRank(preferredConnection)))
                                freshness = BatteryFreshness.Retained;
                            var reading = ToReading(endpoint.Id, pair.Key, fact.Sample, fact.ObservedAt,
                                fact.ReceivedAt, ProjectUtc(fact.ExpiresElapsed, instant), freshness, field);
                            readings.Add(reading);
                            candidates.Add(new(reading, fact.ObservedElapsed, fact.ReadElapsed));
                        }
                        if (field.Latest is { } latest &&
                            (field.Qualified is null || !SameSample(field.Qualified, latest)))
                        {
                            readings.Add(ToReading(endpoint.Id, pair.Key, latest.Sample,
                                latest.Sample.Evidence == BatteryEvidence.SystemCache ? null : latest.Sample.ObservedAt,
                                SampleReadAt(latest), null, BatteryFreshness.Unknown, field));
                        }
                    }
                }
            }
            var selected = candidates.Where(x => x.Reading.Component == "device" && x.Reading.Percent is >= 0 and <= 100 &&
                    x.Reading.Freshness is BatteryFreshness.Current or BatteryFreshness.Recent or BatteryFreshness.Retained)
                .OrderByDescending(x => linked ? x.Reading.Freshness is BatteryFreshness.Current or BatteryFreshness.Recent ? 1 : 0 : 0)
                .ThenByDescending(x => linked ? x.ReadElapsed : TimeSpan.Zero)
                .ThenByDescending(x => FreshnessRank(x.Reading.Freshness))
                .ThenByDescending(x => EvidenceRank(x.Reading.Evidence))
                .ThenByDescending(x => x.Reading.Precision == "percent")
                .ThenByDescending(x => x.ObservedElapsed)
                .ThenByDescending(x => x.ReadElapsed)
                .ThenBy(x => x.Reading.Source, StringComparer.Ordinal)
                .ThenBy(x => x.Reading.EndpointId, StringComparer.Ordinal)
                .FirstOrDefault()?.Reading;
            var freshnessState = selected?.Freshness ??
                (readings.Any(x => x.Freshness == BatteryFreshness.Expired) ? BatteryFreshness.Expired : BatteryFreshness.Unknown);
            // Windows history and receiver paths do not confirm an active device.
            // A record first becomes visible after a resolved connection; admitted
            // records retain the normal window after their latest confirmation's
            // effective grace boundary. Rediscovering a path never moves it.
            var visible = connection != ConnectionState.Offline &&
                !(connection == ConnectionState.Pending && !entity.HasConfirmedConnection) &&
                !(connection == ConnectionState.Pending && (ConfirmedUntil(entity) is not { } confirmedUntil ||
                    confirmedUntil + _options.PendingVisibilityLifetime <= now)) &&
                !(connection == ConnectionState.Pending && _open && entity.CreatedGeneration == _generation);
            return new(entity.Id, string.IsNullOrWhiteSpace(entity.Name) ? "Unknown device" : entity.Name,
                entity.Kind, connection, visible, selected?.Percent,
                freshnessState, selected?.Source, Reason(connection, selected, readings),
                new ReadOnlyCollection<BatteryReading>(readings), latestError)
            {
                SourceAttempts = new ReadOnlyCollection<DeviceSourceAttempt>(attempts), SelectedReading = selected,
                IsExplicitlyOffline = !linked && !_open && entity.Endpoints.All(id => IsConfirmedOffline(_endpoints[id], now))
            };
    }

    private void SyncLinkedEntities(TimeSpan now)
    {
        foreach (var group in _links)
        {
            var members = group.Members.Select(member => _entities.GetValueOrDefault(member.DeviceId)).OfType<Entity>().ToArray();
            if (members.Length == 0)
            {
                // User links persist outside StateStore. Runtime evidence does not.
                _linkedEntities.Remove(group.PrimaryDeviceId);
                continue;
            }
            if (!_linkedEntities.TryGetValue(group.PrimaryDeviceId, out var combined))
            {
                combined = new Entity
                {
                    Id = group.PrimaryDeviceId, Sequence = members.Min(member => member.Sequence),
                    CreatedGeneration = members.Min(member => member.CreatedGeneration)
                };
                _linkedEntities.Add(group.PrimaryDeviceId, combined);
            }
            combined.Endpoints.Clear();
            foreach (var member in members) combined.Endpoints.UnionWith(member.Endpoints);
            var configuredIds = group.Members.Select(member => member.DeviceId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in combined.MemberConfirmedWindows.Keys.Where(id => !configuredIds.Contains(id)).ToArray())
                combined.MemberConfirmedWindows.Remove(id);
            foreach (var member in members)
            {
                if (!combined.MemberConfirmedWindows.TryGetValue(member.Id, out var remembered))
                    combined.MemberConfirmedWindows[member.Id] = remembered = new(StringComparer.Ordinal);
                foreach (var window in member.ConfirmedWindows) remembered[window.Key] = window.Value;
            }
            combined.ConfirmedWindows.Clear();
            // Keep history for configured members removed by runtime cleanup,
            // but remove a member's contribution when the user removes its link.
            // Current raw evidence wins if an automatic identity merge made two
            // saved member IDs refer to overlapping endpoints.
            foreach (var member in combined.MemberConfirmedWindows.OrderBy(pair => _entities.ContainsKey(pair.Key)))
            foreach (var window in member.Value)
                combined.ConfirmedWindows[window.Key] = window.Value;
            combined.HasConfirmedConnection = combined.ConfirmedWindows.Count > 0;
            var primary = group.Members.Single(member => member.DeviceId == group.PrimaryDeviceId);
            combined.Name = primary.Name;
            combined.Kind = _entities.GetValueOrDefault(group.PrimaryDeviceId)?.Kind
                ?? primary.Kind;
            combined.IdentityError = members.Select(member => member.IdentityError).FirstOrDefault(error => error is not null);
            UpdateConfirmationWindows(combined, now);
        }
    }

    private string LinkSignature()
    {
        var signature = new StringBuilder();
        void Add(string value) => signature.Append(value.Length).Append(':').Append(value);
        foreach (var group in _links.OrderBy(group => group.PrimaryDeviceId, StringComparer.Ordinal))
        {
            Add(group.PrimaryDeviceId);
            foreach (var member in group.Members.OrderBy(member => member.DeviceId, StringComparer.Ordinal))
            {
                Add(member.DeviceId); Add(member.Name); Add(member.Kind.ToString());
                foreach (var transport in member.Transports.Order()) Add(transport.ToString());
            }
        }
        return signature.ToString();
    }

    private void RemoveExpiredOfflineDevices(TimeSpan now)
    {
        var expired = _entities.Values.Where(entity =>
            entity.Endpoints.All(id => IsConfirmedOffline(_endpoints[id], now)) &&
            !entity.Endpoints.SelectMany(id => _endpoints[id].Sources.Values)
                .SelectMany(source => source.Fields.Values)
                .Any(field => field.Qualified is { } fact && fact.ExpiresElapsed > now)).ToArray();
        foreach (var entity in expired)
        {
            foreach (var endpointId in entity.Endpoints)
            {
                _endpoints.Remove(endpointId);
                foreach (var seen in _seen.Values) seen.Remove(endpointId);
            }
            foreach (var alias in _aliases.Where(pair => pair.Value == entity.Id).Select(pair => pair.Key).ToArray())
                _aliases.Remove(alias);
            _entities.Remove(entity.Id);
        }
        // Ambiguous containers remain unsafe for this session even after one of
        // their devices disappears. Forgetting that fact can merge unrelated devices.
    }

    private static bool IsConfirmedOffline(Endpoint endpoint, TimeSpan now)
    {
        if (ResolveConnection(endpoint, now) != ConnectionState.Offline) return false;
        var negative = endpoint.Sources.Values.Max(source => source.NegativeAt);
        if (negative is not null && endpoint.Sources.Values.All(source =>
            source.PositiveAt is null || source.PositiveAt <= negative)) return true;
        // A failed or silent source is not proof of removal. Each contributing
        // source must have explicitly revoked membership in a complete inventory.
        return endpoint.Sources.Count > 0 && endpoint.Sources.Values.All(source => source.MembershipMissing);
    }

    private static BatteryReading ToReading(string endpoint, string source, BatterySample sample,
        DateTimeOffset? observedAt, DateTimeOffset receivedAt, DateTimeOffset? expiresAt,
        BatteryFreshness freshness, Field field) =>
        new(endpoint, source, sample.Percent is >= 0 and <= 100 ? sample.Percent : null,
            freshness, sample.Evidence, observedAt, receivedAt, expiresAt, field.Attempt,
            field.Error, sample.Property, sample.Precision, sample.Component, sample.Charging, field.AttemptedAt);

    private BatteryFreshness Freshness(Fact fact, Field field, Endpoint endpoint, TimeSpan now)
    {
        if (now >= fact.ExpiresElapsed) return BatteryFreshness.Expired;
        var valueTime = fact.ObservedElapsed ?? fact.ReadElapsed;
        if ((endpoint.RetainBefore is { } barrier && valueTime <= barrier) ||
            field.Attempt != ReadStatus.Success || (field.Latest is { } latest && !SameSample(fact, latest)))
            return BatteryFreshness.Retained;
        return fact.Sample.Evidence == BatteryEvidence.ActiveRead && now < fact.ObservedElapsed + _options.ActiveBatteryLifetime
            ? BatteryFreshness.Current : BatteryFreshness.Recent;
    }

    private static bool SameSample(Fact fact, RawFact latest) =>
        fact.Sample == latest.Sample && fact.ReadElapsed == latest.ReadElapsed;

    private static DateTimeOffset SampleReadAt(RawFact fact) =>
        fact.Sample.ReadAt ?? fact.ReceivedAt;

    private static void RetainBefore(Endpoint endpoint, TimeSpan endedAt)
    {
        if (endpoint.RetainBefore is null || endpoint.RetainBefore < endedAt) endpoint.RetainBefore = endedAt;
    }

    private static void RememberEndedConnection(Endpoint endpoint, TimeSpan now)
    {
        if (ResolveConnection(endpoint, now) is ConnectionState.Online or ConnectionState.Recent) return;
        var negative = endpoint.Sources.Values.Max(x => x.NegativeAt);
        foreach (var source in endpoint.Sources.Values)
            if (source.PositiveAt is { } positive && (negative is null || positive > negative) &&
                source.GraceUntil is { } endedAt && endedAt <= now)
                RetainBefore(endpoint, endedAt);
    }

    private static ConnectionState ResolveConnection(Endpoint endpoint, TimeSpan now)
    {
        var negative = endpoint.Sources.Values.Max(x => x.NegativeAt);
        var positiveSources = endpoint.Sources.Values.Where(x =>
            x.PositiveAt <= now && x.PositiveAt is not null && (negative is null || x.PositiveAt > negative)).ToArray();
        if (positiveSources.Any(x => x.PositiveUntil > now)) return ConnectionState.Online;
        if (positiveSources.Any(x => x.GraceUntil > now)) return ConnectionState.Recent;
        if ((negative is null || positiveSources.Length > 0) && endpoint.Sources.Values.Any(x => x.PathUntil > now))
            return ConnectionState.Pending;
        return ConnectionState.Offline;
    }

    private Entity ResolveEntity(DeviceObservation observation, string endpointId)
    {
        var keys = new List<string>();
        var container = NormalizeContainer(observation.ContainerId);
        var address = NormalizeAddress(observation.BluetoothAddress);
        // Every published Bluetooth observation has an address; the Windows container
        // is optional. Prefer the address so startup arrival order cannot change its ID.
        if (address is not null) keys.Add("address:" + address);
        if (container is not null && !_ambiguousContainers.Contains(container)) keys.Add("container:" + container);
        keys.Add("endpoint:" + endpointId);
        var matches = keys.Where(_aliases.ContainsKey).Select(x => _entities[_aliases[x]])
            .Distinct().OrderBy(x => x.Sequence).ToArray();
        var conflict = (address is not null && matches.Any(x => x.Addresses.Count > 0 && !x.Addresses.Contains(address))) ||
            matches.Any(a => matches.Any(b => a != b &&
            a.Containers.Count > 0 && b.Containers.Count > 0 && !a.Containers.Overlaps(b.Containers) &&
            a.Addresses.Count > 0 && b.Addresses.Count > 0 && !a.Addresses.Overlaps(b.Addresses)));
        if (conflict)
        {
            if (container is not null)
            {
                _ambiguousContainers.Add(container);
                _aliases.Remove("container:" + container);
            }
            foreach (var match in matches) match.IdentityError = "Conflicting device identities were kept separate.";
            keys = address is null ? ["endpoint:" + endpointId] : ["address:" + address, "endpoint:" + endpointId];
            matches = keys.Where(_aliases.ContainsKey).Select(x => _entities[_aliases[x]])
                .Distinct().OrderBy(x => x.Sequence).ToArray();
        }
        Entity entity;
        if (matches.Length == 0)
        {
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keys[0])))[..20].ToLowerInvariant();
            var id = "device-" + digest;
            _entities[id] = entity = new() { Id = id, Sequence = ++_entitySequence, CreatedGeneration = _generation };
        }
        else
        {
            entity = matches[0];
            foreach (var duplicate in matches.Skip(1))
            {
                entity.CreatedGeneration = Math.Min(entity.CreatedGeneration, duplicate.CreatedGeneration);
                entity.HasConfirmedConnection |= duplicate.HasConfirmedConnection;
                foreach (var window in duplicate.ConfirmedWindows)
                    entity.ConfirmedWindows[window.Key] = window.Value;
                foreach (var oldEndpoint in duplicate.Endpoints) entity.Endpoints.Add(oldEndpoint);
                entity.Containers.UnionWith(duplicate.Containers);
                entity.Addresses.UnionWith(duplicate.Addresses);
                foreach (var alias in _aliases.Where(x => x.Value == duplicate.Id).Select(x => x.Key).ToArray())
                    _aliases[alias] = entity.Id;
                if (entity.Kind == DeviceKind.Unknown) entity.Kind = duplicate.Kind;
                if (string.IsNullOrWhiteSpace(entity.Name)) entity.Name = duplicate.Name;
                _entities.Remove(duplicate.Id);
            }
        }
        if (conflict)
        {
            entity.IdentityError = "Conflicting device identities were kept separate.";
            if (address is not null) entity.Addresses.Add(address);
        }
        else
        {
            entity.IdentityError = null;
            if (container is not null) entity.Containers.Add(container);
            if (address is not null) entity.Addresses.Add(address);
        }
        foreach (var key in keys) _aliases[key] = entity.Id;
        entity.Endpoints.Add(endpointId);
        if (!_endpoints.ContainsKey(endpointId)) _endpoints[endpointId] = new() { Id = endpointId };
        return entity;
    }

    private TimeSpan AdvanceTime(TimeSpan now)
    {
        if (now > _lastElapsed) _lastElapsed = now;
        // Windows are based on evidence deadlines, not the frequency of snapshots.
        // Capture old evidence before a source replaces it, including after long gaps.
        foreach (var entity in _entities.Values) UpdateConfirmationWindows(entity, _lastElapsed);
        foreach (var entity in _linkedEntities.Values) UpdateConfirmationWindows(entity, _lastElapsed);
        return _lastElapsed;
    }

    private void UpdateConfirmationWindows(Entity entity, TimeSpan now)
    {
        foreach (var id in entity.Endpoints)
        {
            var endpoint = _endpoints[id];
            var active = ResolveConnection(endpoint, now) is ConnectionState.Online or ConnectionState.Recent;
            if (active)
            {
                entity.HasConfirmedConnection = true;
            }
            else if (!entity.ConfirmedWindows.ContainsKey(id)) continue;

            var negative = endpoint.Sources.Values.Max(source => source.NegativeAt);
            var confirmations = endpoint.Sources.Values.Where(source => source.PositiveAt is not null && source.PositiveAt <= now).ToArray();
            var confirmedAt = confirmations.Select(source => source.PositiveAt).Max();
            var end = confirmations
                .Select(source => negative >= source.PositiveAt
                    ? Earlier(source.GraceUntil, negative!.Value) : source.GraceUntil).Max();
            // A negative reply can arrive later than its timestamp; the effective
            // end must be shortened to that timestamp, never the publication time.
            if (confirmedAt is not { } confirmation || end is not { } value) continue;
            if (!entity.ConfirmedWindows.TryGetValue(id, out var previous) || active && confirmation > previous.ConfirmedAt)
                entity.ConfirmedWindows[id] = new(confirmation, value);
            else if (value < previous.End)
                entity.ConfirmedWindows[id] = previous with { End = value };
        }
    }

    private static TimeSpan? ConfirmedUntil(Entity entity) =>
        entity.ConfirmedWindows.Count == 0 ? null : entity.ConfirmedWindows.Values.Max(window => window.End);

    private static TimeSpan MapUtc(DateTimeOffset utc, ClockInstant instant) => instant.Elapsed + (utc - instant.Utc);

    private static DateTimeOffset ProjectUtc(TimeSpan value, ClockInstant instant) => instant.Utc + (value - instant.Elapsed);

    private static SourceBatch WithoutElapsedStamps(SourceBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!batch.Observations.Any(observation => observation.ReceivedElapsed is not null || observation.Battery?.ReadElapsed is not null))
            return batch;
        return batch with
        {
            Observations = batch.Observations.Select(observation => observation with
            {
                ReceivedElapsed = null,
                Battery = observation.Battery is { } sample ? sample with { ReadElapsed = null } : null
            }).ToArray()
        };
    }

    private static TimeSpan? Earlier(TimeSpan? previous, TimeSpan current) =>
        previous is null || current < previous ? current : previous;

    private static string NormalizeEndpoint(string value) => value.Trim().ToLowerInvariant();

    private static string? NormalizeContainer(string? value)
    {
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty ||
            id == Guid.Parse("00000000-0000-0000-ffff-ffffffffffff") ||
            id == Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")) return null;
        return id.ToString("D");
    }

    private static string? NormalizeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var address = value.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return address.Length == 12 && address.All(Uri.IsHexDigit) && address != "000000000000" && address != "FFFFFFFFFFFF" ? address : null;
    }

    private static int ConnectionRank(ConnectionState state) => state switch
    { ConnectionState.Online => 3, ConnectionState.Recent => 2, ConnectionState.Pending => 1, _ => 0 };

    private static int FreshnessRank(BatteryFreshness freshness) => freshness switch
    { BatteryFreshness.Current => 3, BatteryFreshness.Recent => 2, BatteryFreshness.Retained => 1, _ => 0 };

    private static int EvidenceRank(BatteryEvidence evidence) => evidence switch
    { BatteryEvidence.ActiveRead => 3, BatteryEvidence.SourceTimestamp => 2, _ => 1 };

    private static string Reason(ConnectionState connection, BatteryReading? selected, List<BatteryReading> readings)
    {
        if (selected is not null) return selected.Freshness == BatteryFreshness.Retained
            ? "The last usable value is retained until another qualified read."
            : selected.Evidence == BatteryEvidence.SystemCache
            ? "Windows reported this value while the endpoint was connected; source measurement time remains unknown."
            : "Selected the freshest usable battery evidence.";
        if (readings.Any(x => x.Freshness == BatteryFreshness.Expired)) return "Battery evidence has expired.";
        if (readings.Count > 0) return "Battery source time or same-endpoint connection evidence is unavailable.";
        return connection == ConnectionState.Online ? "Device is connected; battery is unavailable." : "No usable battery evidence.";
    }

    private static string Signature(List<DeviceState> states)
    {
        var text = new StringBuilder();
        void Add(object? value) { var part = value?.ToString() ?? ""; text.Append(part.Length).Append(':').Append(part); }
        foreach (var state in states)
        {
            Add(state.Id); Add(state.Name); Add(state.Kind); Add(state.Connection); Add(state.Visible);
            Add(state.IsExplicitlyOffline);
            Add(state.BatteryPercent); Add(state.Freshness); Add(state.BatterySource); Add(state.Reason); Add(state.LastError);
            Add(state.SelectedReading?.EndpointId); Add(state.SelectedReading?.Property);
            Add(state.SelectedReading?.Component); Add(state.SelectedReading?.Charging);
            foreach (var value in state.Readings)
            {
                Add(value.EndpointId); Add(value.Source); Add(value.Percent); Add(value.Freshness); Add(value.Evidence);
                Add(value.Attempt); Add(value.Error); Add(value.Property); Add(value.Precision); Add(value.Component); Add(value.Charging);
            }
            foreach (var attempt in state.SourceAttempts)
            {
                Add(attempt.EndpointId); Add(attempt.Source); Add(attempt.Status); Add(attempt.Error);
                // The latest source result remains displayed until that source
                // reports again; a new scan alone must not flicker its warning.
                Add(attempt.Coarse); Add(attempt.Connection);
            }
        }
        return text.ToString();
    }

    private sealed class Entity
    {
        public required string Id { get; init; }
        public long Sequence { get; init; }
        public long CreatedGeneration { get; set; }
        // A receiver path alone cannot establish a visible physical device.
        // This admission lasts for the runtime record, never across cleanup or restart.
        public bool HasConfirmedConnection { get; set; }
        // Monotonic, per-endpoint confirmation-window ends. Existing entries may
        // be truncated by revocation; path-only observations never create/renew one.
        // Linked views retain a final end after a raw member is cleaned up.
        public Dictionary<string, ConnectionWindow> ConfirmedWindows { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, ConnectionWindow>> MemberConfirmedWindows { get; } = new(StringComparer.Ordinal);
        public string Name { get; set; } = "";
        public DeviceKind Kind { get; set; }
        public string? IdentityError { get; set; }
        public HashSet<string> Containers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Addresses { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Endpoints { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ConnectionWindow(TimeSpan ConfirmedAt, TimeSpan End);

    private sealed class Endpoint
    {
        public required string Id { get; init; }
        public TimeSpan? RetainBefore { get; set; }
        public Dictionary<string, Source> Sources { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Source
    {
        public TimeSpan LastReceivedElapsed { get; set; } = TimeSpan.MinValue;
        public TimeSpan? PositiveAt { get; set; }
        public TimeSpan? PositiveUntil { get; set; }
        public TimeSpan? GraceUntil { get; set; }
        public TimeSpan? NegativeAt { get; set; }
        public TimeSpan? PathUntil { get; set; }
        public long PositiveGeneration { get; set; } = long.MinValue;
        public bool MembershipMissing { get; set; }
        public ReadStatus Attempt { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset AttemptedAt { get; set; }
        public TimeSpan AttemptedElapsed { get; set; }
        public long AttemptGeneration { get; set; } = long.MinValue;
        public bool BatteryReturned { get; set; }
        public Dictionary<string, Field> Fields { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Field
    {
        public RawFact? Latest { get; set; }
        public Fact? Qualified { get; set; }
        public ReadStatus Attempt { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset AttemptedAt { get; set; }
        public TimeSpan AttemptedElapsed { get; set; }
        public long AttemptGeneration { get; set; } = long.MinValue;
    }

    private sealed record RawFact(BatterySample Sample, DateTimeOffset ReceivedAt, TimeSpan ReceivedElapsed, TimeSpan ReadElapsed, long Generation);
    private sealed record Fact(BatterySample Sample, DateTimeOffset? ObservedAt, DateTimeOffset ReceivedAt,
        TimeSpan? ObservedElapsed, TimeSpan ReadElapsed, TimeSpan ExpiresElapsed);
    private sealed record RankedReading(BatteryReading Reading, TimeSpan? ObservedElapsed, TimeSpan ReadElapsed);
}
