using BatteryManager.App.Platform;
using BatteryManager.Application.Startup;
using Microsoft.Win32;
using Xunit;

namespace BatteryManager.App.Logic.Tests;

public sealed class WindowsStartupRegistryTests
{
    [Fact]
    public void ConstructorDoesNotReadOrWriteRegistry()
    {
        var backend = new FakeBackend();
        _ = new WindowsStartupRegistry(backend);
        Assert.Empty(backend.Reads);
        Assert.Empty(backend.Writes);
    }

    [Theory]
    [InlineData(2, (int)StartupApproval.Allowed)]
    [InlineData(3, (int)StartupApproval.Blocked)]
    [InlineData(0, (int)StartupApproval.Allowed)]
    [InlineData(1, (int)StartupApproval.Blocked)]
    [InlineData(6, (int)StartupApproval.Unknown)]
    [InlineData(7, (int)StartupApproval.Unknown)]
    public void OnlyKnownApprovalShapesAreInterpreted(byte status, int expected)
    {
        var bytes = new byte[12];
        bytes[0] = status;
        var backend = new FakeBackend { Approval = new(true, RegistryValueKind.Binary, bytes) };
        var registry = new WindowsStartupRegistry(backend);
        Assert.Equal((StartupApproval)expected, registry.Read().Approval);
        Assert.Empty(backend.Writes);
        Assert.All(backend.Reads, read => Assert.Equal(WindowsStartupRegistry.ValueName, read.Name));
    }

    [Fact]
    public void ObservedWindowsToggleRefreshesServiceWithoutChangingApproval()
    {
        const string command = "\"D:\\Program\\BatteryManager\\BatteryManager.App.exe\"";
        var disabled = new byte[] { 1, 0, 0, 0, 192, 228, 48, 129, 43, 113, 221, 1 };
        var backend = new FakeBackend
        {
            Run = new(true, RegistryValueKind.String, command),
            Approval = new(true, RegistryValueKind.Binary, disabled)
        };
        var service = new StartupService(new WindowsStartupRegistry(backend), @"D:\Program\BatteryManager\BatteryManager.App.exe");
        var before = service.Read();
        Assert.Equal(StartupStatus.BlockedByWindows, before.Status);
        Assert.False(service.SetEnabled(true, before).State.IsEnabled);
        Assert.Equal(disabled, Assert.IsType<byte[]>(backend.Approval.Data));
        var enabled = new byte[12];
        backend.Approval = new(true, RegistryValueKind.Binary, enabled);
        Assert.Equal(StartupStatus.Enabled, service.Read().Status);
        Assert.Equal(enabled, Assert.IsType<byte[]>(backend.Approval.Data));
    }

    [Fact]
    public void MalformedAndNonBinaryApprovalRemainUnknown()
    {
        foreach (var value in new WindowsStartupRegistryValue[]
        {
            new(true, RegistryValueKind.Binary, new byte[] { 3 }),
            new(true, RegistryValueKind.Binary, new byte[] { 3, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            new(true, RegistryValueKind.String, "disabled")
        })
        {
            var backend = new FakeBackend { Approval = value };
            Assert.Equal(StartupApproval.Unknown, new WindowsStartupRegistry(backend).Read().Approval);
            Assert.Empty(backend.Writes);
        }
    }

    [Fact]
    public void ExplicitWriteChangesOnlyRunAndPreservesApprovalBytes()
    {
        var approval = new byte[] { 3, 0, 0, 0, 9, 8, 7, 6, 5, 4, 3, 2 };
        var backend = new FakeBackend { Approval = new(true, RegistryValueKind.Binary, approval) };
        var registry = new WindowsStartupRegistry(backend);
        const string command = "\"D:\\apps\\BatteryManager.App.exe\"";
        Assert.True(registry.TrySetRunValue(registry.Read(), command));
        Assert.Equal(command, Assert.Single(backend.Writes));
        Assert.Equal(approval, Assert.IsType<byte[]>(backend.Approval.Data));
        Assert.Equal(StartupApproval.Blocked, registry.Read().Approval);
        Assert.True(registry.TrySetRunValue(registry.Read(), null));
        Assert.False(registry.Read().ValuePresent);
        Assert.Equal(approval, Assert.IsType<byte[]>(backend.Approval.Data));
    }

    [Fact]
    public void ApprovalChangesInvalidateDisplayedSnapshotEvenIfRunCommandIsIdentical()
    {
        var backend = new FakeBackend();
        var registry = new WindowsStartupRegistry(backend);
        var expected = registry.Read();
        backend.Approval = new(true, RegistryValueKind.Binary, new byte[12]);
        Assert.False(registry.TrySetRunValue(expected, "replacement"));
        Assert.Empty(backend.Writes);
    }

    [Fact]
    public void RawRunTypeAndDataChangesInvalidateTheSnapshot()
    {
        var backend = new FakeBackend { Run = new(true, RegistryValueKind.Binary, new byte[] { 1, 2 }) };
        var registry = new WindowsStartupRegistry(backend);
        var expected = registry.Read();
        Assert.True(expected.ValuePresent);
        Assert.False(expected.ValueIsString);
        backend.Run = new(true, RegistryValueKind.Binary, new byte[] { 1, 3 });
        Assert.False(registry.TrySetRunValue(expected, null));
        Assert.Empty(backend.Writes);
    }

    [Fact]
    public void EnvironmentStringIsReturnedWithoutExpandingAnUnknownPath()
    {
        const string command = "\"%LOCALAPPDATA%\\BatteryManager\\BatteryManager.App.exe\"";
        var backend = new FakeBackend { Run = new(true, RegistryValueKind.ExpandString, command) };
        var registry = new WindowsStartupRegistry(backend);
        Assert.Equal(command, registry.Read().Command);
        var service = new StartupService(registry, @"D:\app\BatteryManager.App.exe");
        Assert.Equal(StartupStatus.Unknown, service.Read().Status);
        Assert.Empty(backend.Writes);
    }

    // No test constructs CurrentUserStartupRegistryBackend or opens Registry.CurrentUser.
    private sealed class FakeBackend : IWindowsStartupRegistryBackend
    {
        public WindowsStartupRegistryValue Run { get; set; } = WindowsStartupRegistryValue.Missing;
        public WindowsStartupRegistryValue Approval { get; set; } = WindowsStartupRegistryValue.Missing;
        public List<(string Path, string Name)> Reads { get; } = [];
        public List<string?> Writes { get; } = [];
        public WindowsStartupRegistryValue Read(string path, string name)
        {
            Reads.Add((path, name));
            return path switch
            {
                WindowsStartupRegistry.RunPath => Run,
                WindowsStartupRegistry.ApprovalPath => Approval,
                _ => throw new InvalidOperationException("Unexpected key path.")
            };
        }
        public void SetRunValue(string? command)
        {
            Writes.Add(command);
            Run = command is null ? WindowsStartupRegistryValue.Missing : new(true, RegistryValueKind.String, command);
        }
    }
}
