using BatteryManager.Core;
using Xunit;

namespace BatteryManager.Core.Tests;

public sealed class StateStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
    private const string Container = "a7a8fc07-01cd-458f-91d1-a34e94722f9a";

    [Fact]
    public void Cold_start_preserves_description_source_and_zero_percent()
    {
        var store = Start();
        Put(store, Active(0));
        var device = Assert.Single(store.Snapshot(T0).Devices);
        Assert.Equal("Keyboard", device.Name);
        Assert.Equal(DeviceKind.Keyboard, device.Kind);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.True(device.Visible);
        Assert.Equal(0, device.BatteryPercent);
        Assert.Equal("Gatt", device.BatterySource);
        Assert.Equal(BatteryFreshness.Current, device.Freshness);
        Assert.Equal(T0, Assert.Single(device.Readings).ObservedAt);
    }

    [Fact]
    public void Equivalent_guids_and_addresses_merge_but_preserve_endpoints()
    {
        var store = Start();
        Put(store, Active(72) with { EndpointId = "classic:one", ContainerId = Container, BluetoothAddress = "00:11:22:33:44:55" });
        Put(store, Active(73) with { EndpointId = "le:one", ContainerId = "{" + Container.ToUpperInvariant() + "}", BluetoothAddress = "001122334455" });
        var device = Assert.Single(store.Snapshot(T0).Devices);
        Assert.Equal(2, device.Readings.Count);
        Assert.Equal(2, device.Readings.Select(x => x.EndpointId).Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000-0000-0000-ffff-ffffffffffff")]
    public void Placeholder_and_name_do_not_merge_instances(string? container)
    {
        var store = Start();
        Put(store, Active(50) with { EndpointId = "one", ContainerId = container, BluetoothAddress = "001122334455" });
        Put(store, Active(50) with { EndpointId = "two", ContainerId = container, BluetoothAddress = "66778899aabb" });
        Assert.Equal(2, store.Snapshot(T0).Devices.Count);
    }

    [Fact]
    public void Contradictory_known_identities_remain_separate()
    {
        var other = "6db80921-3779-44d8-8e84-14a2a4c26bf1";
        var store = Start();
        Put(store, Active(50) with { EndpointId = "one", ContainerId = Container, BluetoothAddress = "001122334455" });
        Put(store, Active(60) with { EndpointId = "two", ContainerId = other, BluetoothAddress = "66778899aabb" });
        Put(store, Active(61) with { EndpointId = "two", ContainerId = Container, BluetoothAddress = "66778899aabb" });
        var devices = store.Snapshot(T0).Devices;
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, x => x.LastError?.Contains("Conflicting") == true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_container_cannot_merge_two_different_bluetooth_addresses(bool reverse)
    {
        var store = Start();
        var first = Active(70) with { EndpointId = "bt:first", BluetoothAddress = "001122334455" };
        var second = Active(80) with { EndpointId = "bt:second", BluetoothAddress = "66778899aabb" };
        Put(store, reverse ? second : first);
        Put(store, reverse ? first : second);
        var devices = store.Snapshot(T0).Devices;
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, x => x.BatteryPercent == 70);
        Assert.Contains(devices, x => x.BatteryPercent == 80);
        Assert.Contains(devices, x => x.LastError?.Contains("Conflicting") == true);

        // A later relationship-only node must not reuse the now-ambiguous container.
        Put(store, Connection(null, "unattributed:node"));
        Assert.Equal(3, store.Snapshot(T0).Devices.Count);
    }

    [Fact]
    public void Equivalent_container_nodes_without_addresses_can_merge()
    {
        var store = Start();
        Put(store, Active(70) with { EndpointId = "node:one", BluetoothAddress = null });
        Put(store, Active(70) with { EndpointId = "node:two", ContainerId = "{" + Container.ToUpperInvariant() + "}", BluetoothAddress = null });
        Assert.Single(store.Snapshot(T0).Devices);
    }

    [Fact]
    public void Contradictory_address_on_existing_endpoint_does_not_replace_its_value()
    {
        var store = Start();
        Put(store, Active(70));
        Put(store, Active(10) with { BluetoothAddress = "66778899aabb", ReceivedAt = T0.AddSeconds(1) }, now: T0.AddSeconds(1));
        var state = Assert.Single(store.Snapshot(T0.AddSeconds(1)).Devices);
        Assert.Equal(70, state.BatteryPercent);
        Assert.Contains("conflicting", state.LastError);
    }

    [Fact]
    public void Component_readings_do_not_invent_a_single_device_percentage()
    {
        var store = Start();
        Put(store, Active(30) with { Battery = new(30, BatteryEvidence.ActiveRead, T0, Component: "left") });
        Put(store, Active(90) with { Battery = new(90, BatteryEvidence.ActiveRead, T0, Component: "right") });
        var state = Assert.Single(store.Snapshot(T0).Devices);
        Assert.Null(state.BatteryPercent);
        Assert.Equal(2, state.Readings.Count);
        Assert.Contains(state.Readings, x => x.Component == "left" && x.Percent == 30);
        Assert.Contains(state.Readings, x => x.Component == "right" && x.Percent == 90);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cache_requires_same_scan_same_endpoint_connection_regardless_of_arrival_order(bool propertyFirst)
    {
        var store = Start();
        var property = Cached(65);
        var aep = Connection(true, "bt:one");
        if (propertyFirst)
        {
            Put(store, property);
            Assert.Null(Assert.Single(store.Snapshot(T0).Devices).BatteryPercent);
            Put(store, aep);
        }
        else { Put(store, aep); Put(store, property); }
        var device = Assert.Single(store.Snapshot(T0).Devices);
        Assert.Equal(65, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Recent, device.Freshness);
        var reading = Assert.Single(device.Readings);
        Assert.Null(reading.ObservedAt);
        Assert.Equal(T0.AddMinutes(15), reading.ExpiresAt);
    }

    [Fact]
    public void Other_endpoint_or_previous_scan_connection_cannot_qualify_cache()
    {
        var store = Start();
        Put(store, Connection(true, "bt:two"));
        Put(store, Cached(65));
        Assert.Null(Assert.Single(store.Snapshot(T0).Devices).BatteryPercent);
        store.EndScan(1);
        store.BeginScan(2, false);
        Put(store, Cached(66) with { EndpointId = "bt:two", ReceivedAt = T0.AddSeconds(1) }, 2, T0.AddSeconds(1));
        Assert.Null(Assert.Single(store.Snapshot(T0.AddSeconds(1)).Devices).BatteryPercent);
    }

    [Fact]
    public void Missing_battery_does_not_discard_independent_connected_evidence()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one") with { Status = ReadStatus.Missing });
        Put(store, Cached(50));
        Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(T0).Devices).Connection);
        Assert.Equal(50, Assert.Single(store.Snapshot(T0).Devices).BatteryPercent);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(60)]
    public void Each_successful_cache_read_renews_its_lifetime_even_when_the_value_is_unchanged(int percent)
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Cached(65));
        store.BeginScan(2, false);
        Put(store, Connection(true, "bt:one") with { ReceivedAt = T0.AddMinutes(14) }, 2, T0.AddMinutes(14));
        Put(store, Cached(percent) with { ReceivedAt = T0.AddMinutes(14) }, 2, T0.AddMinutes(14));
        Assert.Equal(percent, Assert.Single(store.Snapshot(T0.AddMinutes(15)).Devices).BatteryPercent);
        var reading = Assert.Single(Assert.Single(store.Snapshot(T0.AddMinutes(29).AddTicks(-1)).Devices).Readings);
        Assert.Equal(T0.AddMinutes(14), reading.ReceivedAt);
        Assert.Equal(T0.AddMinutes(29), reading.ExpiresAt);
        Assert.Null(reading.ObservedAt);
        var expired = Assert.Single(store.Snapshot(T0.AddMinutes(29)).Devices);
        Assert.Null(expired.BatteryPercent);
        Assert.Equal(BatteryFreshness.Expired, expired.Freshness);
    }

    [Fact]
    public void Expired_cache_recovers_after_a_new_qualified_read()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Cached(65));
        Assert.Null(Assert.Single(store.Snapshot(T0.AddMinutes(15)).Devices).BatteryPercent);
        store.BeginScan(2, false);
        var recoveredAt = T0.AddMinutes(16);
        Put(store, Connection(true, "bt:one") with { ReceivedAt = recoveredAt }, 2, recoveredAt);
        Put(store, Cached(55) with { ReceivedAt = recoveredAt }, 2, recoveredAt);
        var recovered = Assert.Single(store.Snapshot(recoveredAt).Devices);
        Assert.Equal(55, recovered.BatteryPercent);
        Assert.Equal(BatteryFreshness.Recent, recovered.Freshness);
        Assert.Equal(recoveredAt.AddMinutes(15), Assert.Single(recovered.Readings).ExpiresAt);
    }

    [Theory]
    [InlineData(ReadStatus.Missing)]
    [InlineData(ReadStatus.Error)]
    [InlineData(ReadStatus.Timeout)]
    public void Repeated_failed_cache_reads_and_connection_updates_do_not_renew_the_value(ReadStatus failure)
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Cached(65));
        foreach (var minute in new[] { 1, 14 })
        {
            var at = T0.AddMinutes(minute);
            var generation = minute + 1;
            store.BeginScan(generation, false);
            Put(store, Connection(true, "bt:one") with { ReceivedAt = at }, generation, at);
            Put(store, Cached(65) with { Battery = null, ReceivedAt = at, Status = failure }, generation, at);
            var device = Assert.Single(store.Snapshot(at).Devices);
            Assert.Equal(65, device.BatteryPercent);
            Assert.Equal(BatteryFreshness.Retained, device.Freshness);
            var reading = Assert.Single(device.Readings);
            Assert.Equal(T0, reading.ReceivedAt);
            Assert.Equal(T0.AddMinutes(15), reading.ExpiresAt);
            Assert.Equal(at, reading.AttemptedAt);
        }
        Assert.Null(Assert.Single(store.Snapshot(T0.AddMinutes(15)).Devices).BatteryPercent);
    }

    [Fact]
    public void Cache_lifetime_starts_at_software_read_time_not_delivery_or_connection_time()
    {
        var store = Start();
        var sample = new BatterySample(65, BatteryEvidence.SystemCache, ReadAt: T0);
        Put(store, Cached(65) with { Battery = sample, ReceivedAt = T0.AddSeconds(10) }, now: T0.AddSeconds(10));
        Put(store, Connection(true, "bt:one") with { ReceivedAt = T0.AddSeconds(20) }, now: T0.AddSeconds(20));
        var reading = Assert.Single(Assert.Single(store.Snapshot(T0.AddSeconds(20)).Devices).Readings);
        Assert.Equal(T0, reading.ReceivedAt);
        Assert.Equal(T0.AddMinutes(15), reading.ExpiresAt);
        Assert.Null(reading.ObservedAt);

        // Native aggregation and the final complete batch can replay the same successful field.
        store.BeginScan(2, false);
        var replayedAt = T0.AddMinutes(14);
        Put(store, Connection(true, "bt:one") with { ReceivedAt = replayedAt }, 2, replayedAt);
        store.Apply(2, new("Pnp", [Cached(65) with { Battery = sample, ReceivedAt = replayedAt }],
            Complete: true, Authoritative: true), replayedAt);
        Assert.Equal(T0.AddMinutes(15), Assert.Single(Assert.Single(store.Snapshot(replayedAt).Devices).Readings).ExpiresAt);
        Assert.Null(Assert.Single(store.Snapshot(T0.AddMinutes(15)).Devices).BatteryPercent);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    public void Cache_read_time_cannot_be_later_than_observation_or_decision_time(int readSeconds, int decisionSeconds)
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Cached(65) with { Battery = new(65, BatteryEvidence.SystemCache, ReadAt: T0.AddSeconds(readSeconds)) },
            now: T0.AddSeconds(decisionSeconds));
        var device = Assert.Single(store.Snapshot(T0.AddSeconds(decisionSeconds)).Devices);
        Assert.Null(device.BatteryPercent);
        Assert.Equal(ReadStatus.Error, Assert.Single(device.Readings).Attempt);
    }

    [Theory]
    [InlineData(false, "explicit")]
    [InlineData(true, "explicit")]
    [InlineData(false, "manual")]
    [InlineData(true, "manual")]
    [InlineData(false, "elapsed")]
    [InlineData(true, "elapsed")]
    public void Reconnection_keeps_pre_disconnect_battery_retained_until_read_again(bool active, string disconnect)
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, active ? Active(65) with { Connected = null } : Cached(65));
        store.BeginScan(2, true);
        if (disconnect == "explicit")
            Put(store, Connection(false, "bt:one") with { ReceivedAt = T0.AddSeconds(10) }, 2, T0.AddSeconds(10));
        else if (disconnect == "manual")
            store.Apply(2, new("Aep", [], Complete: true, Authoritative: true), T0.AddSeconds(10));
        var disconnectedAt = disconnect == "elapsed" ? T0.AddSeconds(105) : T0.AddSeconds(10);
        Assert.False(Assert.Single(store.Snapshot(disconnectedAt).Devices).Visible);

        store.BeginScan(3, false);
        var reconnectedAt = disconnectedAt.AddSeconds(5);
        Put(store, Connection(true, "bt:one") with { ReceivedAt = reconnectedAt }, 3, reconnectedAt);
        var beforeRead = Assert.Single(store.Snapshot(reconnectedAt).Devices);
        Assert.Equal(ConnectionState.Online, beforeRead.Connection);
        Assert.Equal(65, beforeRead.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, beforeRead.Freshness);
        Assert.Equal(T0.AddMinutes(15), Assert.Single(beforeRead.Readings).ExpiresAt);

        var readAt = reconnectedAt.AddSeconds(1);
        Put(store, active
            ? Active(60) with { ReceivedAt = readAt, Battery = new(60, BatteryEvidence.ActiveRead, readAt) }
            : Cached(60) with { ReceivedAt = readAt }, 3, readAt);
        var afterRead = Assert.Single(store.Snapshot(readAt).Devices);
        Assert.Equal(60, afterRead.BatteryPercent);
        Assert.Equal(active ? BatteryFreshness.Current : BatteryFreshness.Recent, afterRead.Freshness);
        Assert.Equal(readAt.AddMinutes(15), Assert.Single(afterRead.Readings).ExpiresAt);
    }

    [Fact]
    public void Cache_read_before_disconnect_cannot_be_qualified_by_later_same_scan_reconnection()
    {
        var store = Start();
        Put(store, Cached(65));
        Put(store, Connection(false, "bt:one") with { ReceivedAt = T0.AddSeconds(1) }, now: T0.AddSeconds(1));
        Put(store, Connection(true, "bt:one") with { ReceivedAt = T0.AddSeconds(2) }, now: T0.AddSeconds(2));
        Assert.Null(Assert.Single(store.Snapshot(T0.AddSeconds(2)).Devices).BatteryPercent);
        Put(store, Cached(60) with { ReceivedAt = T0.AddSeconds(3) }, now: T0.AddSeconds(3));
        Assert.Equal(60, Assert.Single(store.Snapshot(T0.AddSeconds(3)).Devices).BatteryPercent);
    }

    [Fact]
    public void Authoritative_absence_marks_only_that_sources_fields_missing_without_renewing_them()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Cached(65));
        Put(store, Active(70));
        store.BeginScan(2, false);
        store.Apply(2, new("Pnp", [], Complete: true, Authoritative: true), T0.AddSeconds(10));
        var device = Assert.Single(store.Snapshot(T0.AddSeconds(10)).Devices);
        var cache = Assert.Single(device.Readings, x => x.Source == "Pnp");
        Assert.Equal(ReadStatus.Missing, cache.Attempt);
        Assert.Equal(BatteryFreshness.Retained, cache.Freshness);
        Assert.Equal(T0, cache.ReceivedAt);
        Assert.Equal(T0.AddMinutes(15), cache.ExpiresAt);
        Assert.Equal(BatteryFreshness.Current, Assert.Single(device.Readings, x => x.Source == "Gatt").Freshness);
        Assert.Equal(70, device.BatteryPercent);
        store.BeginScan(3, false);
        store.Apply(3, new("Pnp", [], Complete: true, Authoritative: true), T0.AddMinutes(14));
        Assert.Equal(BatteryFreshness.Expired,
            Assert.Single(Assert.Single(store.Snapshot(T0.AddMinutes(15)).Devices).Readings, x => x.Source == "Pnp").Freshness);
    }

    [Fact]
    public void Non_authoritative_complete_absence_marks_old_battery_missing_but_keeps_connection_evidence()
    {
        var store = Start();
        Put(store, Cached(65) with { Connected = true });
        var untouchedId = Assert.Single(store.Snapshot(T0).Devices).Id;
        store.BeginScan(2, true);
        var at = T0.AddSeconds(10);
        store.Apply(2, new("Pnp", [Cached(90) with
        {
            EndpointId = "bt:other", ContainerId = null, BluetoothAddress = "66778899AABB",
            ReceivedAt = at, Connected = true
        }], Complete: true, Authoritative: false), at);
        var state = Assert.Single(store.Snapshot(at).Devices, x => x.Id == untouchedId);
        Assert.Equal(ConnectionState.Online, state.Connection);
        Assert.Equal(65, state.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, state.Freshness);
        var reading = Assert.Single(state.Readings);
        Assert.Equal(ReadStatus.Missing, reading.Attempt);
        Assert.Equal(T0, reading.ReceivedAt);
        Assert.Equal(T0.AddMinutes(15), reading.ExpiresAt);
        Assert.Equal(ConnectionState.Recent,
            Assert.Single(store.Snapshot(T0.AddSeconds(45)).Devices, x => x.Id == untouchedId).Connection);
    }

    [Fact]
    public void Success_on_another_field_or_endpoint_does_not_extend_an_old_field()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Cached(65));
        store.BeginScan(2, false);
        var at = T0.AddMinutes(14);
        Put(store, Connection(true, "bt:one") with { ReceivedAt = at }, 2, at);
        Put(store, Cached(90) with { ReceivedAt = at, Battery = new(90, BatteryEvidence.SystemCache, Component: "case") }, 2, at);
        Put(store, Connection(true, "bt:two") with { ReceivedAt = at }, 2, at);
        Put(store, Cached(80) with { EndpointId = "bt:two", ReceivedAt = at }, 2, at);
        var readings = Assert.Single(store.Snapshot(T0.AddMinutes(15)).Devices).Readings;
        Assert.Equal(BatteryFreshness.Expired, Assert.Single(readings, x => x.EndpointId == "bt:one" && x.Component == "device").Freshness);
        Assert.Equal(T0.AddMinutes(29), Assert.Single(readings, x => x.EndpointId == "bt:one" && x.Component == "case").ExpiresAt);
        Assert.Equal(T0.AddMinutes(29), Assert.Single(readings, x => x.EndpointId == "bt:two").ExpiresAt);
    }

    [Fact]
    public void Newer_successful_property_replaces_the_old_main_percentage_without_renewing_the_old_property()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Cached(65) with { Battery = new(65, BatteryEvidence.SystemCache, Property: "A-old-property") });
        store.BeginScan(2, false);
        var readAt = T0.AddSeconds(30);
        Put(store, Connection(true, "bt:one") with { ReceivedAt = readAt }, 2, readAt);
        Put(store, Cached(60) with
        {
            ReceivedAt = readAt,
            Battery = new(60, BatteryEvidence.SystemCache, Property: "Z-new-property")
        }, 2, readAt);
        var device = Assert.Single(store.Snapshot(readAt).Devices);
        Assert.Equal(60, device.BatteryPercent);
        Assert.Equal(T0.AddMinutes(15), Assert.Single(device.Readings, x => x.Property == "A-old-property").ExpiresAt);
        Assert.Equal(readAt.AddMinutes(15), Assert.Single(device.Readings, x => x.Property == "Z-new-property").ExpiresAt);
    }

    [Theory]
    [InlineData(ReadStatus.Error)]
    [InlineData(ReadStatus.Timeout)]
    [InlineData(ReadStatus.Canceled)]
    public void Source_failure_preserves_last_value_and_its_times(ReadStatus failure)
    {
        var store = Start();
        Put(store, Active(73));
        store.BeginScan(2, true);
        var failureAt = T0.AddSeconds(10);
        store.Apply(2, new("Gatt", [], Error: "Read failed", FailureStatus: failure), failureAt);
        var device = Assert.Single(store.Snapshot(failureAt).Devices);
        Assert.Equal(73, device.BatteryPercent);
        Assert.Equal(ConnectionState.Online, device.Connection);
        Assert.Equal(BatteryFreshness.Retained, device.Freshness);
        var reading = Assert.Single(device.Readings);
        Assert.Equal(failure, reading.Attempt);
        Assert.Equal(T0, reading.ObservedAt);
        Assert.Equal(T0, reading.ReceivedAt);
        Assert.Equal(T0.AddMinutes(15), reading.ExpiresAt);
        Assert.Equal(failureAt, reading.AttemptedAt);
        Assert.Equal("Read failed", device.LastError);
    }

    [Fact]
    public void Unqualified_new_cache_does_not_replace_usable_old_value()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Cached(70));
        store.BeginScan(2, true);
        Put(store, Cached(10) with { ReceivedAt = T0.AddSeconds(10) }, 2, T0.AddSeconds(10));
        var device = Assert.Single(store.Snapshot(T0.AddSeconds(10)).Devices);
        Assert.Equal(70, device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Retained, device.Freshness);
        Assert.Contains(device.Readings, x => x.Percent == 10 && x.Freshness == BatteryFreshness.Unknown);
    }

    [Fact]
    public void Receiver_path_does_not_prove_body_online_and_explicit_disconnect_suppresses_same_path()
    {
        var store = Start();
        Put(store, Connection(null, "receiver:one") with { PathPresent = true });
        Assert.Equal(ConnectionState.Pending, Assert.Single(store.Snapshot(T0).Devices).Connection);
        Put(store, Connection(false, "receiver:one") with { PathPresent = true });
        var disconnected = Assert.Single(store.Snapshot(T0).Devices);
        Assert.Equal(ConnectionState.Offline, disconnected.Connection);
        Assert.False(disconnected.Visible);
        Put(store, Connection(null, "receiver:two") with { PathPresent = true });
        Assert.Equal(ConnectionState.Pending, Assert.Single(store.Snapshot(T0).Devices).Connection);
    }

    [Fact]
    public void New_disconnect_cannot_override_other_endpoint_online_evidence()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Connection(true, "receiver:one"));
        Put(store, Connection(false, "bt:one") with { ReceivedAt = T0.AddSeconds(2) }, now: T0.AddSeconds(2));
        Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(T0.AddSeconds(2)).Devices).Connection);
    }

    [Fact]
    public void Same_endpoint_disconnect_overrides_older_other_source_connection_and_path()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Connection(false, "bt:one") with { Source = "Other", ReceivedAt = T0.AddSeconds(1) }, now: T0.AddSeconds(1));
        Put(store, Connection(null, "bt:one") with { Source = "Pnp", PathPresent = true, ReceivedAt = T0.AddSeconds(2) }, now: T0.AddSeconds(2));
        Assert.Equal(ConnectionState.Offline, Assert.Single(store.Snapshot(T0.AddSeconds(2)).Devices).Connection);
    }

    [Fact]
    public void Authoritative_manual_absence_cancels_only_covered_source_grace()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Connection(true, "bt:two") with { Source = "Other" });
        store.BeginScan(2, true);
        store.Apply(2, new("Aep", [], Complete: true, Authoritative: true), T0.AddSeconds(1));
        Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(T0.AddSeconds(1)).Devices).Connection);
        store.Apply(2, new("Other", [], Complete: true, Authoritative: true), T0.AddSeconds(2));
        Assert.Equal(ConnectionState.Offline, Assert.Single(store.Snapshot(T0.AddSeconds(2)).Devices).Connection);
    }

    [Fact]
    public void Authoritative_scan_uses_all_incremental_members_not_just_final_empty_marker()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        store.Apply(1, new("Aep", [], Complete: true, Authoritative: true), T0);
        Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(T0).Devices).Connection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incomplete_or_failed_empty_result_does_not_revoke_online(bool error)
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        store.BeginScan(2, true);
        store.Apply(2, new("Aep", [], Complete: error, Authoritative: error,
            Error: error ? "Permission denied" : null), T0.AddSeconds(1));
        Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(T0.AddSeconds(1)).Devices).Connection);
        Assert.Equal(ConnectionState.Recent, Assert.Single(store.Snapshot(T0.AddSeconds(45)).Devices).Connection);
    }

    [Fact]
    public void Automatic_absence_starts_bounded_grace_and_repeated_absence_does_not_extend_it()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        store.BeginScan(2, false);
        store.Apply(2, new("Aep", [], Complete: true, Authoritative: true), T0.AddSeconds(10));
        Assert.Equal(ConnectionState.Recent, Assert.Single(store.Snapshot(T0.AddSeconds(10)).Devices).Connection);
        store.BeginScan(3, false);
        store.Apply(3, new("Aep", [], Complete: true, Authoritative: true), T0.AddSeconds(30));
        Assert.Equal(ConnectionState.Recent, Assert.Single(store.Snapshot(T0.AddSeconds(69)).Devices).Connection);
        Assert.Equal(ConnectionState.Offline, Assert.Single(store.Snapshot(T0.AddSeconds(70)).Devices).Connection);
    }

    [Fact]
    public void Exact_connection_and_battery_boundaries_expire_without_reads()
    {
        var store = Start();
        Put(store, Active(75));
        Assert.Equal(T0.AddSeconds(45), store.NextExpiry(T0));
        Assert.Equal(ConnectionState.Online, Assert.Single(store.Snapshot(T0.AddSeconds(45).AddTicks(-1)).Devices).Connection);
        var recent = Assert.Single(store.Snapshot(T0.AddSeconds(45)).Devices);
        Assert.Equal(ConnectionState.Recent, recent.Connection);
        Assert.Equal(BatteryFreshness.Recent, recent.Freshness);
        Assert.Equal(ConnectionState.Offline, Assert.Single(store.Snapshot(T0.AddSeconds(105)).Devices).Connection);
        Assert.Equal(75, Assert.Single(store.Snapshot(T0.AddMinutes(15).AddTicks(-1)).Devices).BatteryPercent);
        Assert.Null(Assert.Single(store.Snapshot(T0.AddMinutes(15)).Devices).BatteryPercent);
        Assert.Equal(T0.AddSeconds(1005), store.NextExpiry(T0.AddMinutes(15)));
        Assert.Null(store.NextExpiry(T0.AddSeconds(1005)));
    }

    [Fact]
    public void Clock_rollback_does_not_resurrect_expired_values()
    {
        var store = Start();
        Put(store, Active(75));
        Assert.Null(Assert.Single(store.Snapshot(T0.AddMinutes(16)).Devices).BatteryPercent);
        Assert.Null(Assert.Single(store.Snapshot(T0.AddMinutes(1)).Devices).BatteryPercent);
    }

    [Fact]
    public void Current_beats_recent_and_recent_beats_retained_before_source_quality()
    {
        var store = Start();
        Put(store, Active(10));
        Put(store, Connection(true, "bt:one"));
        Put(store, Cached(90));
        Assert.Equal(10, Assert.Single(store.Snapshot(T0).Devices).BatteryPercent);
        store.Apply(1, new("Gatt", [], Error: "Read failed"), T0.AddSeconds(1));
        Assert.Equal(90, Assert.Single(store.Snapshot(T0.AddSeconds(1)).Devices).BatteryPercent);
    }

    [Fact]
    public void Reliable_source_time_is_preserved_and_older_source_time_cannot_replace_newer_fact()
    {
        var store = Start();
        Put(store, Active(73) with { Battery = new(73, BatteryEvidence.SourceTimestamp, T0.AddMinutes(-1)) });
        Put(store, Active(10) with { ReceivedAt = T0.AddSeconds(1), Battery = new(10, BatteryEvidence.SourceTimestamp, T0.AddMinutes(-2)) }, now: T0.AddSeconds(1));
        var device = Assert.Single(store.Snapshot(T0.AddSeconds(1)).Devices);
        Assert.Equal(73, device.BatteryPercent);
        var qualified = Assert.Single(device.Readings, x => x.Freshness == BatteryFreshness.Retained);
        Assert.Equal(T0.AddMinutes(-1), qualified.ObservedAt);
        Assert.Equal(T0.AddMinutes(14), qualified.ExpiresAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Invalid_percent_is_diagnostic_only(int percent)
    {
        var store = Start();
        Put(store, Active(percent));
        var device = Assert.Single(store.Snapshot(T0).Devices);
        Assert.Null(device.BatteryPercent);
        Assert.Null(Assert.Single(device.Readings).Percent);
        Assert.NotNull(device.LastError);
    }

    [Fact]
    public void Future_source_timestamp_does_not_become_current()
    {
        var store = Start();
        Put(store, Active(73) with { Battery = new(73, BatteryEvidence.ActiveRead, T0.AddSeconds(1)) });
        var device = Assert.Single(store.Snapshot(T0).Devices);
        Assert.Null(device.BatteryPercent);
        Assert.Equal(BatteryFreshness.Unknown, device.Freshness);
    }

    [Fact]
    public void Ended_and_older_generations_cannot_publish_or_revoke()
    {
        var store = Start();
        Put(store, Active(73));
        store.EndScan(1);
        Assert.False(store.Apply(1, new("Gatt", [], Complete: true, Authoritative: true), T0));
        store.BeginScan(2, true);
        Put(store, Active(75) with { ReceivedAt = T0.AddSeconds(1), Battery = new(75, BatteryEvidence.ActiveRead, T0.AddSeconds(1)) }, 2, T0.AddSeconds(1));
        Assert.False(store.Apply(1, new("Gatt", [Active(10)]), T0.AddSeconds(2)));
        Assert.Equal(75, Assert.Single(store.Snapshot(T0.AddSeconds(2)).Devices).BatteryPercent);
    }

    [Fact]
    public void Repeated_same_cache_only_changes_diagnostic_receive_time()
    {
        var store = Start();
        Put(store, Connection(true, "bt:one"));
        Put(store, Cached(70));
        var first = store.Snapshot(T0);
        store.BeginScan(2, false);
        Put(store, Connection(true, "bt:one") with { ReceivedAt = T0.AddSeconds(10) }, 2, T0.AddSeconds(10));
        Put(store, Cached(70) with { ReceivedAt = T0.AddSeconds(10) }, 2, T0.AddSeconds(10));
        var second = store.Snapshot(T0.AddSeconds(10));
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(T0, Assert.Single(Assert.Single(first.Devices).Readings).ReceivedAt);
        Assert.Equal(T0.AddSeconds(10), Assert.Single(Assert.Single(second.Devices).Readings).ReceivedAt);
    }

    [Fact]
    public async Task Concurrent_readers_see_whole_immutable_device_updates()
    {
        var store = Start();
        Put(store, Active(1) with { Name = "Device 1" });
        var writer = Task.Run(() =>
        {
            for (var i = 2; i <= 100; i++)
                Put(store, Active(i) with { Name = "Device " + i, ReceivedAt = T0.AddTicks(i), Battery = new(i, BatteryEvidence.ActiveRead, T0.AddTicks(i)) }, now: T0.AddTicks(i));
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var state = Assert.Single(store.Snapshot(T0).Devices);
                Assert.Equal("Device " + state.BatteryPercent, state.Name);
            }
        });
        await Task.WhenAll(writer, reader);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(105)]
    [InlineData(999)]
    public void SparseSnapshotsAndLatePathReturnKeepTheEvidenceGraceDeadline(int interimTick)
    {
        var f = new WindowFixture();
        f.Scan(0, WindowObservation(0, "window:a", true, 40));
        f.Store.Snapshot(WindowAt(interimTick));
        var returned = Assert.Single(f.Scan(1000, WindowObservation(1000, "window:a", null)).Devices);
        Assert.Equal(ConnectionState.Pending, returned.Connection);
        Assert.True(returned.Visible);
        Assert.Null(returned.BatteryPercent);
        Assert.Equal(T0.AddSeconds(900), Assert.Single(returned.Readings).ExpiresAt);
        Assert.True(Assert.Single(f.Store.Snapshot(WindowAt(1004.999)).Devices).Visible);
        Assert.Equal(TimeSpan.FromMilliseconds(1), f.Store.NextExpiryDelay(WindowAt(1004.999)));
        Assert.False(Assert.Single(f.Store.Snapshot(WindowAt(1005)).Devices).Visible);
        Assert.False(Assert.Single(f.Scan(1200, WindowObservation(1200, "window:a", null)).Devices).Visible);
    }

    [Theory]
    [InlineData(false, 90)]
    [InlineData(true, 30)]
    public void AuthoritativeAbsenceShortensTheWindowAndPathReturnDoesNotRestartIt(bool manual, int end)
    {
        var f = new WindowFixture();
        f.Scan(0, WindowObservation(0, "window:a", true, 40));
        f.Store.BeginScan(2, manual);
        f.Store.Apply(2, new("window", [], Complete: true, Authoritative: true), WindowAt(30));
        Assert.False(Assert.Single(f.Store.Snapshot(WindowAt(30)).Devices).IsExplicitlyOffline);
        f.Store.EndScan(2);
        var closed = Assert.Single(f.Store.Snapshot(WindowAt(30)).Devices);
        Assert.Equal(manual, closed.IsExplicitlyOffline);
        Assert.True(Assert.Single(f.Store.Snapshot(WindowAt(end)).Devices).IsExplicitlyOffline);
        // The next report arrives after the cache deadline without an intervening
        // cleanup snapshot, exercising a retained raw record instead of a new one.
        f.Store.BeginScan(3, false);
        f.Store.Apply(3, new("window", [WindowObservation(1000, "window:a", null)]), WindowAt(1000));
        f.Store.EndScan(3);
        var returned = Assert.Single(f.Store.Snapshot(WindowAt(1000)).Devices);
        Assert.Equal(ConnectionState.Pending, returned.Connection);
        Assert.False(returned.Visible);
        Assert.False(returned.IsExplicitlyOffline);
        Assert.Equal(T0.AddSeconds(900), Assert.Single(returned.Readings).ExpiresAt);
    }

    [Fact]
    public void LateConnectionReplyUsesItsSampleTimeAndExpiredFirstConfirmationCannotAdmit()
    {
        var f = new WindowFixture();
        var late = Assert.Single(f.Scan(1000, WindowObservation(950, "window:a", true)).Devices);
        Assert.Equal(ConnectionState.Recent, late.Connection);
        Assert.True(late.Visible);
        f.Scan(1040, WindowObservation(1040, "window:a", null));
        f.Scan(1930, WindowObservation(1930, "window:a", null));
        Assert.True(Assert.Single(f.Store.Snapshot(WindowAt(1954.999)).Devices).Visible);
        Assert.False(Assert.Single(f.Store.Snapshot(WindowAt(1955)).Devices).Visible);

        var expired = new WindowFixture();
        expired.Scan(1000, WindowObservation(0, "window:a", true));
        Assert.False(Assert.Single(expired.Scan(1010, WindowObservation(1010, "window:a", null)).Devices).Visible);
    }

    [Fact]
    public void LateNegativeReplyTruncatesTheWindowAtItsEvidenceTime()
    {
        var f = new WindowFixture();
        f.Scan(0, WindowObservation(0, "window:a", true, 40) with { ContainerId = Container },
            WindowObservation(0, "window:b", null) with { ContainerId = Container });
        var ended = Assert.Single(f.Scan(70,
            WindowObservation(50, "window:a", false) with { ContainerId = Container },
            WindowObservation(70, "window:b", null) with { ContainerId = Container }).Devices);
        Assert.Equal(ConnectionState.Pending, ended.Connection);
        Assert.True(ended.Visible);
        f.Scan(930, WindowObservation(930, "window:b", null) with { ContainerId = Container });
        Assert.True(Assert.Single(f.Store.Snapshot(WindowAt(949.999)).Devices).Visible);
        Assert.False(Assert.Single(f.Store.Snapshot(WindowAt(950)).Devices).Visible);
    }

    [Fact]
    public void ExplicitNegativeBlocksSameEndpointPathReturnAndIsReportedOnlyAfterScanClose()
    {
        var f = new WindowFixture();
        f.Scan(0, WindowObservation(0, "window:a", true, 40));
        f.Store.BeginScan(2, false);
        f.Store.Apply(2, new("window", [WindowObservation(30, "window:a", false)]), WindowAt(30));
        var collecting = f.Store.Snapshot(WindowAt(30));
        Assert.False(Assert.Single(collecting.Devices).IsExplicitlyOffline);
        f.Store.EndScan(2);
        var closed = f.Store.Snapshot(WindowAt(30));
        Assert.True(Assert.Single(closed.Devices).IsExplicitlyOffline);
        Assert.True(closed.Version > collecting.Version);
        f.Store.BeginScan(3, false);
        f.Store.Apply(3, new("window", [WindowObservation(600, "window:a", null)]), WindowAt(600));
        f.Store.EndScan(3);
        var returned = Assert.Single(f.Store.Snapshot(WindowAt(600)).Devices);
        Assert.Equal(ConnectionState.Offline, returned.Connection);
        Assert.False(returned.Visible);
        Assert.True(returned.IsExplicitlyOffline);
        Assert.Equal(T0.AddSeconds(900), Assert.Single(returned.Readings).ExpiresAt);
        Assert.Empty(f.Store.Snapshot(WindowAt(1200)).Devices);
    }

    [Fact]
    public void NaturalOfflineAndOneDisconnectedEndpointAreNotExplicitWholeDeviceRemoval()
    {
        var natural = new WindowFixture();
        natural.Scan(0, WindowObservation(0, "window:a", true, 40));
        var quiet = Assert.Single(natural.Store.Snapshot(WindowAt(200)).Devices);
        Assert.Equal(ConnectionState.Offline, quiet.Connection);
        Assert.False(quiet.IsExplicitlyOffline);

        var f = new WindowFixture();
        f.Scan(0, WindowObservation(0, "window:a", true, 40) with { ContainerId = Container },
            WindowObservation(0, "window:b", true) with { ContainerId = Container });
        Assert.False(Assert.Single(f.Scan(30, WindowObservation(30, "window:a", false)).Devices).IsExplicitlyOffline);
        Assert.True(Assert.Single(f.Scan(40, WindowObservation(40, "window:b", false)).Devices).IsExplicitlyOffline);
    }

    [Theory]
    [InlineData(false, 980)]
    [InlineData(true, 920)]
    public void RepeatedAuthoritativeMissingDoesNotExtendTheSameConfirmationWindow(bool manual, int deadline)
    {
        var f = new WindowFixture();
        DeviceObservation OtherPath(int seconds) => WindowObservation(seconds, "window:b", null) with { ContainerId = Container, Source = "other.path" };
        f.Scan(0, WindowObservation(0, "window:a", true, 40) with { ContainerId = Container }, OtherPath(0));
        f.Missing(20, manual);
        f.Scan(30, WindowObservation(30, "window:a", null) with { ContainerId = Container }, OtherPath(30));
        f.Missing(40, manual);
        f.Scan(deadline - 30, OtherPath(deadline - 30));
        Assert.True(Assert.Single(f.Store.Snapshot(WindowAt(deadline - 0.001)).Devices).Visible);
        Assert.False(Assert.Single(f.Store.Snapshot(WindowAt(deadline)).Devices).Visible);
    }

    private static ClockInstant WindowAt(double seconds) => new(T0.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));

    private static DeviceObservation WindowObservation(int seconds, string endpoint, bool? connected, int? battery = null) =>
        new(endpoint, null, null, endpoint, DeviceKind.Keyboard, "window", T0.AddSeconds(seconds),
            Connected: connected, PathPresent: true, Status: battery is null ? ReadStatus.Missing : ReadStatus.Success,
            Battery: battery is { } value ? new(value, BatteryEvidence.ActiveRead, T0.AddSeconds(seconds),
                ReadElapsed: TimeSpan.FromSeconds(seconds)) : null, ReceivedElapsed: TimeSpan.FromSeconds(seconds));

    private sealed class WindowFixture
    {
        public StateStore Store { get; } = new();
        private long _generation;
        public StateSnapshot Scan(int seconds, params DeviceObservation[] observations)
        {
            Store.BeginScan(++_generation, false);
            foreach (var source in observations.GroupBy(observation => observation.Source))
                Store.Apply(_generation, new(source.Key, source.ToArray()), WindowAt(seconds));
            Store.EndScan(_generation);
            return Store.Snapshot(WindowAt(seconds));
        }
        public void Missing(int seconds, bool manual)
        {
            Store.BeginScan(++_generation, manual);
            Store.Apply(_generation, new("window", [], Complete: true, Authoritative: true), WindowAt(seconds));
            Store.EndScan(_generation);
            Store.Snapshot(WindowAt(seconds));
        }
    }

    private static StateStore Start()
    {
        var store = new StateStore();
        store.BeginScan(1, true);
        return store;
    }

    private static void Put(StateStore store, DeviceObservation observation, long generation = 1, DateTimeOffset? now = null) =>
        store.Apply(generation, new(observation.Source, [observation]), now ?? T0);

    private static DeviceObservation Active(int percent) => new(
        "bt:one", Container, "001122334455", "Keyboard", DeviceKind.Keyboard, "Gatt", T0,
        Connected: true, Battery: new(percent, BatteryEvidence.ActiveRead, T0));

    private static DeviceObservation Cached(int percent) => new(
        "bt:one", Container, "001122334455", "Keyboard", DeviceKind.Keyboard, "Pnp", T0,
        Battery: new(percent, BatteryEvidence.SystemCache));

    private static DeviceObservation Connection(bool? connected, string endpoint) => new(
        endpoint, Container, null, "Keyboard", DeviceKind.Keyboard, "Aep", T0, Connected: connected);
}
