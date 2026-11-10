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
        Command = '"' + this.executablePath + '"';
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
                "同名启动项包含无法确认的命令，已保留原项。请在 Windows 启动应用中检查。", snapshot);
        var current = string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase);
        if (snapshot.Approval == StartupApproval.Blocked)
            return new(StartupStatus.BlockedByWindows, false, true, true, path, legacy,
                "Windows 已禁用此启动项。请在 Windows 设置的“应用 → 启动”中启用 Battery Manager。", snapshot);
        if (snapshot.Approval == StartupApproval.Unknown)
            return new(StartupStatus.Unknown, false, true, true, path, legacy,
                "启动项已登记，但无法确认 Windows 的启用状态。请在“应用 → 启动”中检查。", snapshot);
        if (!current || legacy || !quoted)
            return new(StartupStatus.OtherLocation, false, true, true, path, legacy,
                legacy ? "启动项仍含旧版 --startup 参数。启用后将改为当前程序的启动命令。"
                    : !quoted ? "启动命令缺少完整引号。启用后将改为当前程序的启动命令。"
                    : "启动项指向其他位置的 Battery Manager。启用后将改为当前程序。", snapshot);
        return new(StartupStatus.Enabled, true, true, true, path, false, null, snapshot);
    }

    private static StartupChangeResult ChangedElsewhere(StartupState state) =>
        new(false, state, "启动项已被其他操作更改，已刷新当前状态；请确认后重试。", Conflict: true);

    private static StartupState Unavailable(Exception error) =>
        new(StartupStatus.Error, false, false, false, null, false, $"无法读取开机启动状态：{error.Message}", null);

    private static bool IsRegistryFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException;
}

internal static class StartupCommand
{
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
            if (tail.Length != 0 && tail != "--startup") return false;
            legacyArguments = tail.Length != 0;
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
