using BatteryManager.Application.Startup;
using Xunit;

namespace BatteryManager.Application.Tests;

public sealed class StartupServiceTests
{
    private const string Current = @"D:\应用软件\Battery Manager\BatteryManager.App.exe";
    private const string Other = @"E:\development\BatteryManager.App.exe";

    [Theory]
    [InlineData(@"D:\应用软件\Battery Manager\BatteryManager.App.exe")]
    [InlineData(@"C:\BatteryManager\BatteryManager.App.exe")]
    [InlineData(@"\\server\apps\BatteryManager\BatteryManager.App.exe")]
    public void ExplicitEnableWritesOneQuotedAbsoluteCommandThatStartsInTheBackground(string path)
    {
        var registry = new FakeRegistry();
        var service = new StartupService(registry, path);
        var initial = service.Read();
        Assert.Equal(StartupStatus.Disabled, initial.Status);
        Assert.Empty(registry.Writes);
        var result = service.SetEnabled(true, initial);
        Assert.True(result.Success);
        Assert.True(result.State.IsEnabled);
        Assert.Equal(StartupStatus.Enabled, result.State.Status);
        Assert.Equal('"' + path + "\" --background", Assert.Single(registry.Writes));
        Assert.DoesNotContain("--startup", registry.Writes[0]!);
        Assert.True(service.SetEnabled(true, result.State).Success);
        Assert.Single(registry.Writes);
    }

    [Theory]
    [InlineData("BatteryManager.App.exe")]
    [InlineData(@"C:BatteryManager.App.exe")]
    [InlineData(@"C:\BatteryManager\Other.exe")]
    [InlineData(@"C:\one\..\BatteryManager.App.exe")]
    [InlineData(@"\\?\C:\BatteryManager.App.exe")]
    [InlineData(@"C:\folder\BatteryManager.App.exe:stream")]
    public void ConstructionRejectsAmbiguousOrNonApplicationPaths(string path) =>
        Assert.Throws<ArgumentException>(() => new StartupService(new FakeRegistry(), path));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldLocationAndLegacyArgumentsArePreservedUntilExplicitEnable(bool legacy)
    {
        var original = '"' + Other + '"' + (legacy ? " --startup" : "");
        var registry = new FakeRegistry(original);
        var service = new StartupService(registry, Current);
        var state = service.Read();
        Assert.Equal(StartupStatus.OtherLocation, state.Status);
        Assert.Equal(Other, state.RegisteredExecutable);
        Assert.Equal(legacy, state.HasLegacyArguments);
        Assert.Empty(registry.Writes);
        Assert.Equal(original, registry.State.Command);
        var saved = service.SetEnabled(true, state);
        Assert.True(saved.Success);
        Assert.True(saved.State.IsEnabled);
        Assert.Equal(service.Command, registry.State.Command);
        Assert.False(saved.State.HasLegacyArguments);
    }

    [Fact]
    public void LegacyArgumentsOnTheCurrentExecutableStillNeedExplicitRepair()
    {
        var registry = new FakeRegistry('"' + Current + "\" --startup");
        var service = new StartupService(registry, Current);
        var state = service.Read();
        Assert.False(state.IsEnabled);
        Assert.True(state.HasLegacyArguments);
        Assert.True(service.SetEnabled(true, state).State.IsEnabled);
    }

    [Fact]
    public void EntryWithoutArgumentsFromTheCurrentExecutableStaysEnabledUntilRewritten()
    {
        // 0.3.0 wrote the quoted path alone. It is this app's own entry, so it is shown as on and left as it is;
        // an explicit enable rewrites it to the background form.
        var original = '"' + Current + '"';
        var registry = new FakeRegistry(original);
        var service = new StartupService(registry, Current);
        var state = service.Read();
        Assert.Equal(StartupStatus.Enabled, state.Status);
        Assert.True(state.IsEnabled);
        Assert.False(state.HasLegacyArguments);
        Assert.Null(state.Message);
        Assert.Empty(registry.Writes);
        Assert.Equal(original, registry.State.Command);
        var saved = service.SetEnabled(true, state);
        Assert.True(saved.Success);
        Assert.Equal(service.Command, Assert.Single(registry.Writes));
        Assert.Equal(StartupStatus.Enabled, saved.State.Status);
    }

    [Fact]
    public void BackgroundCommandOnTheCurrentExecutableIsEnabled()
    {
        var registry = new FakeRegistry('"' + Current + "\" --background");
        var service = new StartupService(registry, Current);
        var state = service.Read();
        Assert.Equal(StartupStatus.Enabled, state.Status);
        Assert.False(state.HasLegacyArguments);
        Assert.True(service.SetEnabled(true, state).Success);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void UnquotedKnownPathIsReportedForRepairAndOnlyExplicitEnableAddsQuotes()
    {
        var registry = new FakeRegistry(Current);
        var service = new StartupService(registry, Current);
        var state = service.Read();
        Assert.Equal(StartupStatus.OtherLocation, state.Status);
        Assert.Empty(registry.Writes);
        Assert.True(service.SetEnabled(true, state).State.IsEnabled);
        Assert.Equal(service.Command, registry.State.Command);
    }

    [Theory]
    [InlineData("cmd.exe /c BatteryManager.App.exe")]
    [InlineData("\"C:\\other\\Other.exe\"")]
    [InlineData("\"C:\\app\\BatteryManager.App.exe\" --unknown")]
    [InlineData("\"C:\\app\\BatteryManager.App.exe\" --startup --unknown")]
    [InlineData("\"C:\\app\\BatteryManager.App.exe\"--startup")]
    [InlineData("\"C:\\app\\BatteryManager.App.exe\" --background --unknown")]
    [InlineData("\"C:\\app\\BatteryManager.App.exe\"--background")]
    [InlineData("\"C:\\app\\BatteryManager.App.exe\" --startup --background")]
    [InlineData("C:\\other.exe --argument\\BatteryManager.App.exe")]
    [InlineData("\"%LOCALAPPDATA%\\BatteryManager.App.exe\"")]
    public void UnknownCommandsCannotBeOverwrittenOrDeleted(string command)
    {
        var registry = new FakeRegistry(command);
        var service = new StartupService(registry, Current);
        var state = service.Read();
        Assert.Equal(StartupStatus.Unknown, state.Status);
        Assert.False(state.CanEnable);
        Assert.False(state.CanDisable);
        Assert.False(service.SetEnabled(true, state).Success);
        Assert.False(service.SetEnabled(false, state).Success);
        Assert.Equal(command, registry.State.Command);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void NonStringRunValueIsPreserved()
    {
        var registry = new FakeRegistry { State = new(true, false, null, StartupApproval.NotRecorded, "opaque") };
        var service = new StartupService(registry, Current);
        Assert.Equal(StartupStatus.Unknown, service.Read().Status);
        Assert.False(service.SetEnabled(false, service.Read()).Success);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void DisableRemovesRecognizedOldCommandAndKeepsSystemApproval()
    {
        var registry = new FakeRegistry('"' + Other + "\" --startup", StartupApproval.Blocked);
        var service = new StartupService(registry, Current);
        var result = service.SetEnabled(false, service.Read());
        Assert.True(result.Success);
        Assert.Equal(StartupStatus.Disabled, result.State.Status);
        Assert.Equal(StartupApproval.Blocked, registry.State.Approval);
        Assert.Null(Assert.Single(registry.Writes));
    }

    [Theory]
    [InlineData((int)StartupApproval.Blocked, (int)StartupStatus.BlockedByWindows)]
    [InlineData((int)StartupApproval.Unknown, (int)StartupStatus.Unknown)]
    public void SuccessfulRegistrationDoesNotClaimWindowsHasEnabledIt(int approvalValue, int expectedValue)
    {
        var approval = (StartupApproval)approvalValue;
        var expected = (StartupStatus)expectedValue;
        var registry = new FakeRegistry(approval: approval);
        var service = new StartupService(registry, Current);
        var result = service.SetEnabled(true, service.Read());
        Assert.True(result.Success);
        Assert.Equal(expected, result.State.Status);
        Assert.False(result.State.IsEnabled);
        Assert.NotNull(result.State.Message);
        Assert.Equal(approval, registry.State.Approval);
        Assert.Equal(service.Command, registry.State.Command);
    }

    [Fact]
    public void ExternalChangeSinceDisplayIsReportedWithoutWriting()
    {
        var registry = new FakeRegistry();
        var service = new StartupService(registry, Current);
        var displayed = service.Read();
        registry.Replace('"' + Other + '"');
        var result = service.SetEnabled(true, displayed);
        Assert.False(result.Success);
        Assert.True(result.Conflict);
        Assert.Equal(StartupStatus.OtherLocation, result.State.Status);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void ExternalChangeDuringWriteComparisonCannotBeDeleted()
    {
        var registry = new FakeRegistry('"' + Current + '"');
        registry.BeforeWrite = () => registry.Replace("unrelated-command.exe");
        var service = new StartupService(registry, Current);
        var result = service.SetEnabled(false, service.Read());
        Assert.False(result.Success);
        Assert.True(result.Conflict);
        Assert.Equal(StartupStatus.Unknown, result.State.Status);
        Assert.Empty(registry.Writes);
        Assert.Equal("unrelated-command.exe", registry.State.Command);
    }

    [Fact]
    public void ExternalChangeAfterWriteIsReadBackWithoutAutomaticRetry()
    {
        var registry = new FakeRegistry();
        registry.AfterWrite = () => registry.Replace('"' + Other + '"');
        var service = new StartupService(registry, Current);
        var result = service.SetEnabled(true, service.Read());
        Assert.False(result.Success);
        Assert.True(result.Conflict);
        Assert.Equal(Other, result.State.RegisteredExecutable);
        Assert.Single(registry.Writes);
    }

    [Fact]
    public void PermissionFailureReturnsTheActualUnchangedState()
    {
        var registry = new FakeRegistry('"' + Other + '"') { WriteFailure = new UnauthorizedAccessException("denied") };
        var service = new StartupService(registry, Current);
        var result = service.SetEnabled(true, service.Read());
        Assert.False(result.Success);
        Assert.Contains("denied", result.Error);
        Assert.Equal(StartupStatus.OtherLocation, result.State.Status);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void ReadFailureIsAnErrorAndDoesNotAssumeDisabled()
    {
        var registry = new FakeRegistry { ReadFailure = new IOException("unavailable") };
        var service = new StartupService(registry, Current);
        var state = service.Read();
        Assert.Equal(StartupStatus.Error, state.Status);
        Assert.False(state.CanEnable);
        Assert.False(service.SetEnabled(true, state).Success);
        Assert.Empty(registry.Writes);
    }

    private sealed class FakeRegistry : IStartupRegistry
    {
        private int revision;
        public StartupRegistrySnapshot State { get; set; }
        public List<string?> Writes { get; } = [];
        public Action? BeforeWrite { get; set; }
        public Action? AfterWrite { get; set; }
        public Exception? ReadFailure { get; set; }
        public Exception? WriteFailure { get; set; }
        public FakeRegistry(string? command = null, StartupApproval approval = StartupApproval.NotRecorded) =>
            State = new(command is not null, command is not null, command, approval, "initial");
        public StartupRegistrySnapshot Read() => ReadFailure is { } failure ? throw failure : State;
        public void Replace(string? command) =>
            State = new(command is not null, command is not null, command, State.Approval, (++revision).ToString());
        public bool TrySetRunValue(StartupRegistrySnapshot expected, string? command)
        {
            BeforeWrite?.Invoke();
            if (State != expected) return false;
            if (WriteFailure is { } failure) throw failure;
            Writes.Add(command);
            Replace(command);
            AfterWrite?.Invoke();
            return true;
        }
    }
}
