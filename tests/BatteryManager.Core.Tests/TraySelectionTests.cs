using BatteryManager.Core;
using Xunit;

namespace BatteryManager.Core.Tests;

public sealed class TraySelectionTests
{
    [Fact]
    public void RestoredPriorityImmediatelyPreemptsFallbackAndListOrderWins()
    {
        var first = Device("first");
        var second = Device("second");
        var fallback = Device("fallback");
        var priorities = new[] { first.Id, second.Id };
        var offline = first with { Connection = ConnectionState.Offline };
        Assert.Same(second, TraySelection.Select(Snapshot(fallback, offline, second), priorities, fallback.Id));
        Assert.Same(first, TraySelection.Select(Snapshot(fallback, first, second), priorities, second.Id));
        Assert.Same(second, TraySelection.Select(Snapshot(first, second, fallback), [second.Id, first.Id]));
        Assert.Equal(new[] { "first", "second" }, priorities);
    }

    [Fact]
    public void FallbackIsStableAcrossEnumerationOrderAndNewArrivals()
    {
        var a = Device("a");
        var b = Device("b");
        var c = Device("c");
        Assert.Same(a, TraySelection.Select(Snapshot(c, b, a), []));
        Assert.Same(a, TraySelection.Select(Snapshot(a, c, b), []));
        Assert.Same(b, TraySelection.Select(Snapshot(a, c, b), ["missing"], b.Id));
        Assert.Same(c, TraySelection.Select(Snapshot(c, b with { Connection = ConnectionState.Pending }), [], b.Id));
    }

    [Theory]
    [InlineData(ConnectionState.Pending, BatteryFreshness.Current, 50)]
    [InlineData(ConnectionState.Offline, BatteryFreshness.Current, 50)]
    [InlineData(ConnectionState.Online, BatteryFreshness.Unknown, 50)]
    [InlineData(ConnectionState.Online, BatteryFreshness.Expired, 50)]
    [InlineData(ConnectionState.Online, BatteryFreshness.Current, null)]
    [InlineData(ConnectionState.Online, BatteryFreshness.Current, -1)]
    [InlineData(ConnectionState.Online, BatteryFreshness.Current, 101)]
    [InlineData(ConnectionState.Recent, BatteryFreshness.Expired, 50)]
    [InlineData(ConnectionState.Recent, BatteryFreshness.Unknown, null)]
    public void IneligiblePriorityAndPreviousFallbackAreSkipped(ConnectionState connection, BatteryFreshness freshness, int? percent)
    {
        var preferred = Device("preferred") with { Connection = connection, Freshness = freshness, BatteryPercent = percent };
        var fallback = Device("fallback");
        Assert.Same(fallback, TraySelection.Select(Snapshot(preferred, fallback), [preferred.Id], preferred.Id));
        Assert.Null(TraySelection.Select(Snapshot(preferred), [preferred.Id], preferred.Id));
    }

    [Theory]
    [InlineData(ConnectionState.Online, BatteryFreshness.Current, 0)]
    [InlineData(ConnectionState.Online, BatteryFreshness.Recent, 100)]
    [InlineData(ConnectionState.Online, BatteryFreshness.Retained, 5)]
    [InlineData(ConnectionState.Recent, BatteryFreshness.Current, 0)]
    [InlineData(ConnectionState.Recent, BatteryFreshness.Recent, 100)]
    [InlineData(ConnectionState.Recent, BatteryFreshness.Retained, 5)]
    public void OnlineAndRecentUsableReadingsIncludeValidZeroAndRetainedValue(ConnectionState connection, BatteryFreshness freshness, int percent)
    {
        var device = Device("preferred") with { Connection = connection, Freshness = freshness, BatteryPercent = percent };
        Assert.Same(device, TraySelection.Select(Snapshot(device), [device.Id]));
    }

    [Fact]
    public void SameNameOnNewIdentityDoesNotInheritPreference()
    {
        var original = Device("original") with { Name = "Lofree", Connection = ConnectionState.Offline };
        var moved = Device("moved") with { Name = "Lofree" };
        var other = Device("other");
        Assert.Same(other, TraySelection.Select(Snapshot(original, moved, other), [original.Id], other.Id));
        Assert.Null(TraySelection.Select(Snapshot(), [original.Id]));
    }

    private static DeviceState Device(string id) => new(id, id, DeviceKind.Mouse, ConnectionState.Online,
        true, 50, BatteryFreshness.Current, "test", "test", [], null);
    private static StateSnapshot Snapshot(params DeviceState[] devices) => new(1, DateTimeOffset.UtcNow, devices);
}
