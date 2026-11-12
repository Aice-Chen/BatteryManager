namespace BatteryManager.Application.Startup;

internal enum StartupStatus { Disabled, Enabled, OtherLocation, BlockedByWindows, Unknown, Error }
internal enum StartupApproval { NotRecorded, Allowed, Blocked, Unknown }

// Revision covers the original Run value and the system's approval value. Neither
// a display refresh nor a failed write authorizes replacing a newer external value.
internal sealed record StartupRegistrySnapshot(bool ValuePresent, bool ValueIsString, string? Command,
    StartupApproval Approval, string Revision);

internal interface IStartupRegistry
{
    StartupRegistrySnapshot Read();
    // Compare immediately before changing only the Run value. Windows registry
    // has no cross-process compare-and-swap; the service also verifies afterward.
    bool TrySetRunValue(StartupRegistrySnapshot expected, string? command);
}

internal sealed record StartupState(StartupStatus Status, bool IsEnabled, bool CanEnable, bool CanDisable,
    string? RegisteredExecutable, bool HasLegacyArguments, string? Message,
    StartupRegistrySnapshot? Snapshot);

internal sealed record StartupChangeResult(bool Success, StartupState State, string? Error = null,
    bool Conflict = false);

internal sealed class StartupService
{
    private readonly IStartupRegistry registry;
    private readonly string executablePath;
    private readonly object gate = new();
    public string Command { get; }

    public StartupService(IStartupRegistry registry, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
        this.executablePath = StartupCommand.NormalizeExecutable(executablePath)
            ?? throw new ArgumentException("A fully qualified BatteryManager.App.exe path is required.", nameof(executablePath));
        // The sign-in entry starts the app in the tray; a manual start opens the main window.
        Command = '"' + this.executablePath + "\" " + StartupCommand.BackgroundArgument;
    }

    public StartupState Read()
    {
        lock (gate)
        {
            try { return Interpret(registry.Read()); }
            catch (Exception error) when (IsRegistryFailure(error)) { return Unavailable(error); }
        }
    }

    // The UI passes the last state it displayed. A changed registration must be
    // shown to the user before a second deliberate action can replace it.
    public StartupChangeResult SetEnabled(bool enabled, StartupState expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (gate)
        {
            try
            {
                var before = registry.Read();
                var state = Interpret(before);
                if (expected.Snapshot is null || before != expected.Snapshot)
                    return ChangedElsewhere(state);
                if (enabled ? !state.CanEnable : !state.CanDisable)
                {
                    if (!enabled && !before.ValuePresent) return new(true, state);
                    return new(false, state, state.Message ?? "无法修改该启动项。");
                }
                var desired = enabled ? Command : null;
                if ((enabled && before.ValueIsString && before.Command == desired) || (!enabled && !before.ValuePresent))
                    return new(true, state);
                if (!registry.TrySetRunValue(before, desired))
                    return ChangedElsewhere(Interpret(registry.Read()));
                var after = registry.Read();
                state = Interpret(after);
                if (enabled ? !after.ValueIsString || after.Command != desired : after.ValuePresent)
                    return ChangedElsewhere(state);
                // Registering a command cannot override a Windows startup block.
                // Success means that the requested registry edit was read back;
                // State.IsEnabled remains false when Windows has disabled it.
                return new(true, state);
            }
            catch (Exception error) when (IsRegistryFailure(error))
            {
                var state = Read();
                return new(false, state, $"开机启动设置失败：{error.Message}");
            }
        }
    }

    private StartupState Interpret(StartupRegistrySnapshot snapshot)
    {
        if (!snapshot.ValuePresent)
            return new(StartupStatus.Disabled, false, true, false, null, false, null, snapshot);
        if (!snapshot.ValueIsString || !StartupCommand.TryParse(snapshot.Command, out var path, out var legacy, out var quoted))
            return new(StartupStatus.Unknown, false, false, false, null, false,
                "已有无法识别的同名启动项，未改动", snapshot);
        var current = string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase);
        if (snapshot.Approval == StartupApproval.Blocked)
            return new(StartupStatus.BlockedByWindows, false, true, true, path, legacy,
                "已在 Windows 启动应用中关闭", snapshot);
        if (snapshot.Approval == StartupApproval.Unknown)
            return new(StartupStatus.Unknown, false, true, true, path, legacy,
                "无法确认 Windows 是否允许启动", snapshot);
        if (!current || legacy || !quoted)
            return new(StartupStatus.OtherLocation, false, true, true, path, legacy,
                // An old --startup entry, a missing quote or another copy: enabling writes this app's command.
                "启动项需要更新，打开开关即可", snapshot);
        return new(StartupStatus.Enabled, true, true, true, path, false, null, snapshot);
    }

    private static StartupChangeResult ChangedElsewhere(StartupState state) =>
        new(false, state, "启动项刚被其他程序改动，请重试", Conflict: true);

    private static StartupState Unavailable(Exception error) =>
        new(StartupStatus.Error, false, false, false, null, false, $"无法读取开机启动状态：{error.Message}", null);

    private static bool IsRegistryFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException;
}

internal static class StartupCommand
{
    /// <summary>Passed by the sign-in entry and by the restart after an automatic update: the app stays in the tray.</summary>
    public const string BackgroundArgument = "--background";

    public static bool TryParse(string? command, out string? executable, out bool legacyArguments, out bool quoted)
    {
        executable = null; legacyArguments = false; quoted = false;
        if (string.IsNullOrWhiteSpace(command) || command.Any(char.IsControl)) return false;
        var text = command.Trim();
        string path;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return false;
            path = text[1..end];
            if (end + 1 < text.Length && !char.IsWhiteSpace(text[end + 1])) return false;
            var tail = text[(end + 1)..].Trim();
            // No argument is the entry written by 0.3.0, which opened the main window at sign-in. It still counts as
            // this app's own entry; the next explicit enable or installer upgrade writes the background form.
            if (tail.Length != 0 && tail != "--startup" && tail != BackgroundArgument) return false;
            legacyArguments = tail == "--startup";
            quoted = true;
        }
        else
        {
            const string suffix = " --startup";
            legacyArguments = text.EndsWith(suffix, StringComparison.Ordinal);
            path = legacyArguments ? text[..^suffix.Length].TrimEnd() : text;
            // Do not reinterpret another executable plus arguments as one long
            // unquoted path ending in our filename.
            if (path.Contains(".exe ", StringComparison.OrdinalIgnoreCase)) return false;
        }
        executable = NormalizeExecutable(path);
        return executable is not null;
    }

    public static string? NormalizeExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path != path.Trim() || path.Any(char.IsControl) ||
            path.IndexOfAny(['"', '<', '>', '|', '?', '*']) >= 0) return null;
        // Deliberately accept only ordinary absolute drive or UNC paths; no
        // environment expansion, shell wrapper, device namespace or relative path.
        var value = path.Replace('/', '\\');
        var drive = value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\';
        var unc = value.StartsWith(@"\\", StringComparison.Ordinal) && !value.StartsWith(@"\\.\", StringComparison.Ordinal);
        if ((!drive && !unc) || value.IndexOf(':', drive ? 2 : 0) >= 0) return null;
        var segments = value[(drive ? 3 : 2)..].Split('\\');
        if (segments.Length < (drive ? 1 : 3) || segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.EndsWith(' ') || segment.EndsWith('.'))) return null;
        return string.Equals(segments[^1], "BatteryManager.App.exe", StringComparison.OrdinalIgnoreCase) ? value : null;
    }
}
