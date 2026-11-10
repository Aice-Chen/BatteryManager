using System.Security.Cryptography;
using System.Text.Json;
using BatteryManager.Application.Startup;
using Microsoft.Win32;

namespace BatteryManager.App.Platform;

// Construction has no registry side effects. StartupApproved is always read-only.
internal sealed class WindowsStartupRegistry(IWindowsStartupRegistryBackend? backend = null) : IStartupRegistry
{
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovalPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string ValueName = "BatteryManager";
    private readonly IWindowsStartupRegistryBackend backend = backend ?? new CurrentUserStartupRegistryBackend();
    private readonly object gate = new();

    public StartupRegistrySnapshot Read()
    {
        lock (gate)
        {
            var run = backend.Read(RunPath, ValueName);
            var approval = backend.Read(ApprovalPath, ValueName);
            var isString = run.Exists && (run.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString) && run.Data is string;
            var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { run, approval })));
            return new(run.Exists, isString, isString ? (string)run.Data! : null, ReadApproval(approval), revision);
        }
    }

    public bool TrySetRunValue(StartupRegistrySnapshot expected, string? command)
    {
        lock (gate)
        {
            // This is an optimistic check, not a cross-process atomic registry
            // transaction. The service verifies the result before reporting it.
            if (Read() != expected) return false;
            backend.SetRunValue(command);
            return true;
        }
    }

    private static StartupApproval ReadApproval(WindowsStartupRegistryValue value)
    {
        if (!value.Exists) return StartupApproval.NotRecorded;
        // Interpret only the observed 12-byte enabled/disabled shapes. Other
        // Windows versions or formats remain unknown and are never overwritten.
        if (value.Kind != RegistryValueKind.Binary || value.Data is not byte[] { Length: 12 } bytes ||
            bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0) return StartupApproval.Unknown;
        return bytes[0] switch { 2 => StartupApproval.Allowed, 3 => StartupApproval.Blocked, _ => StartupApproval.Unknown };
    }
}

internal sealed record WindowsStartupRegistryValue(bool Exists, RegistryValueKind Kind, object? Data)
{
    public static WindowsStartupRegistryValue Missing { get; } = new(false, RegistryValueKind.None, null);
}

// Tests inject this boundary and never open HKCU. The only write operation has
// no caller-supplied key path, so it cannot modify StartupApproved.
internal interface IWindowsStartupRegistryBackend
{
    WindowsStartupRegistryValue Read(string path, string name);
    void SetRunValue(string? command);
}

internal sealed class CurrentUserStartupRegistryBackend : IWindowsStartupRegistryBackend
{
    public WindowsStartupRegistryValue Read(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return WindowsStartupRegistryValue.Missing;
        var kind = key.GetValueKind(name);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new(true, kind, value);
    }

    public void SetRunValue(string? command)
    {
        if (command is null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsStartupRegistry.RunPath, writable: true);
            key?.DeleteValue(WindowsStartupRegistry.ValueName, throwOnMissingValue: false);
        }
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(WindowsStartupRegistry.RunPath, writable: true);
            key.SetValue(WindowsStartupRegistry.ValueName, command, RegistryValueKind.String);
        }
    }
}
