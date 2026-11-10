using System.Text.Json;
using static BatteryManager.Application.AppPreferencesValidator;
using BatteryManager.Core;

namespace BatteryManager.Application;

internal sealed class TrayPreferencesStore(string path)
{
    private const int MaximumFileBytes = 256 * 1024;
    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private AppPreferences _current = AppPreferences.Default;

    public IReadOnlyList<TrayPreference> Current => _current.Priorities;
    public AppPreferences Preferences => _current;

    public TrayPreferencesResult Load()
    {
        _writes.Wait();
        try
        {
            var disk = ReadDisk();
            if (disk.Error is not null) return Result(false, disk.Error, canSave: false);
            _current = disk.Preferences!;
            return Result(true);
        }
        finally { _writes.Release(); }
    }

    public Task<TrayPreferencesResult> SaveAsync(
        IReadOnlyList<TrayPreference> priorities,
        bool replaceInvalid = false,
        CancellationToken cancellationToken = default,
        bool clearSuspendedPriorities = false)
    {
        ArgumentNullException.ThrowIfNull(priorities);
        // Copy before yielding; the UI may continue editing its draft.
        var candidate = Array.AsReadOnly(priorities.ToArray());
        var error = ValidatePriorities(candidate);
        return error is not null ? Task.FromResult(Result(false, error))
            : SaveChangeAsync(settings => settings with
            {
                Priorities = candidate,
                SuspendedPriorities = clearSuspendedPriorities ? Array.Empty<SuspendedLinkPriority>() : settings.SuspendedPriorities
            }, replaceInvalid, cancellationToken);
    }

    public Task<TrayPreferencesResult> LinkDevicesAsync(DeviceLinkMember primary, DeviceLinkMember other, CancellationToken cancellationToken = default,
        IReadOnlySet<string>? present = null, IReadOnlyList<DeviceLinkMember>? knownMembers = null)
    {
        if (primary is null || other is null) return Task.FromResult(Result(false, "请选择两台不同的设备记录。"));
        var requested = new[] { new DeviceLinkGroup(primary.DeviceId, [primary, other]) };
        if (DeviceLinks.Validate(requested) is { } error) return Task.FromResult(Result(false, error));
        var members = DeviceLinks.CopyValidated(requested)[0].Members;
        var protectedIds = present?.ToHashSet(StringComparer.Ordinal);
        if (knownMembers?.Any(member => member is null) == true) return Task.FromResult(Result(false, "关联成员的连接方式无效。"));
        var knownCopy = knownMembers?.Select(member => member with
        {
            Transports = member.Transports is null ? null! : member.Transports.Count == 0
                ? Array.Empty<DeviceTransport>() : Array.AsReadOnly(member.Transports.ToArray())
        }).ToArray();
        return SaveLinkChangeAsync(settings => AppPreferenceLinks.Link(settings, members[0], members[1], protectedIds, knownCopy), cancellationToken);
    }

    public Task<TrayPreferencesResult> RestoreDeviceLinksAsync(AppPreferences expected, AppPreferences previous, CancellationToken cancellationToken = default)
    {
        if ((Validate(expected) ?? Validate(previous)) is { } error) return Task.FromResult(Result(false, error));
        static AppPreferences Copy(AppPreferences settings) => settings with
        {
            Priorities = Array.AsReadOnly(settings.Priorities.ToArray()),
            DeviceLinks = DeviceLinks.CopyValidated(settings.DeviceLinks),
            SuspendedPriorities = Array.AsReadOnly(settings.SuspendedPriorities.ToArray())
        };
        var expectedCopy = Copy(expected);
        var previousCopy = Copy(previous);
        return SaveLinkChangeAsync(settings => AppPreferenceLinks.Restore(settings, expectedCopy, previousCopy), cancellationToken);
    }

    public Task<TrayPreferencesResult> UnlinkDevicesAsync(string primaryDeviceId, CancellationToken cancellationToken = default) =>
        SaveLinkChangeAsync(settings => AppPreferenceLinks.Unlink(settings, primaryDeviceId), cancellationToken);

    private async Task<TrayPreferencesResult> SaveLinkChangeAsync(Func<AppPreferences, DeviceLinkChangeResult> change, CancellationToken cancellationToken)
    {
        AppPreferences? previous = null;
        var saved = await SaveChangeAsync(settings =>
        {
            previous = settings;
            var result = change(settings);
            if (!result.Success) throw new PreferencesChangeException(result.Error!);
            return result.Preferences;
        }, false, cancellationToken).ConfigureAwait(false);
        return saved.Success ? saved with { PreviousPreferences = previous } : saved;
    }

    public Task<TrayPreferencesResult> SaveColorModeAsync(
        MainColorMode mode, CancellationToken cancellationToken = default) =>
        !Enum.IsDefined(mode) ? Task.FromResult(Result(false, "颜色模式无效。"))
            : SaveChangeAsync(settings => settings with { MainColorMode = mode }, false, cancellationToken);

    public Task<TrayPreferencesResult> SaveDeviceNameAsync(
        string deviceId, string? name, CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        return SaveDeviceChangeAsync(deviceId, previous => previous with { Name = normalized }, cancellationToken);
    }

    public Task<TrayPreferencesResult> SaveDeviceKindAsync(
        string deviceId, DisplayDeviceKind? kind, CancellationToken cancellationToken = default) =>
        SaveDeviceChangeAsync(deviceId, previous => previous with { Kind = kind }, cancellationToken);

    public Task<TrayPreferencesResult> SaveCustomizationAsync(
        DeviceCustomization customization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customization);
        var candidate = customization with { Name = string.IsNullOrWhiteSpace(customization.Name) ? null : customization.Name.Trim() };
        return SaveDeviceChangeAsync(candidate.DeviceId, _ => candidate, cancellationToken);
    }

    private Task<TrayPreferencesResult> SaveDeviceChangeAsync(
        string deviceId, Func<DeviceCustomization, DeviceCustomization> change, CancellationToken cancellationToken)
    {
        if (!ValidText(deviceId, 256)) return Task.FromResult(Result(false, "设备标识无效。"));
        return SaveChangeAsync(settings =>
        {
            var existing = settings.DeviceCustomizations.FirstOrDefault(item => item.DeviceId == deviceId)
                ?? new DeviceCustomization(deviceId, null, null);
            var candidate = change(existing);
            var devices = settings.DeviceCustomizations.Where(item => item.DeviceId != deviceId).ToList();
            if (candidate.Name is not null || candidate.Kind is not null) devices.Add(candidate);
            return settings with { DeviceCustomizations = Array.AsReadOnly(devices.OrderBy(item => item.DeviceId, StringComparer.Ordinal).ToArray()) };
        }, false, cancellationToken);
    }

    private async Task<TrayPreferencesResult> SaveChangeAsync(
        Func<AppPreferences, AppPreferences> change, bool replaceInvalid, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            // Read under the same lock as the commit. Changing one setting cannot
            // overwrite a different setting saved by an earlier operation.
            var disk = ReadDisk();
            if (disk.Error is not null && (!disk.Invalid || !replaceInvalid))
                return Result(false, disk.Error, canSave: false);
            var baseline = disk.Error is null ? disk.Preferences! : AppPreferences.Default;
            var candidate = change(baseline);
            var validationError = Validate(candidate);
            if (validationError is not null) return Result(false, validationError);

            if (disk.Error is null && (disk.Version == 3 || !disk.Exists) && Equivalent(candidate, baseline))
            {
                _current = candidate;
                return Result(true);
            }

            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteNumber("version", 3);
                writer.WriteStartArray("priorityDevices");
                foreach (var preference in candidate.Priorities)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", preference.DeviceId);
                    writer.WriteString("name", preference.Name);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteString("mainColorMode", candidate.MainColorMode.ToString());
                writer.WriteStartArray("deviceCustomizations");
                foreach (var customization in candidate.DeviceCustomizations)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", customization.DeviceId);
                    if (customization.Name is not null) writer.WriteString("name", customization.Name);
                    if (customization.Kind is { } kind) writer.WriteString("kind", kind.ToString());
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("deviceLinks");
                foreach (var group in candidate.DeviceLinks)
                {
                    writer.WriteStartObject();
                    writer.WriteString("primaryId", group.PrimaryDeviceId);
                    writer.WriteStartArray("members");
                    foreach (var member in group.Members)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", member.DeviceId);
                        writer.WriteString("name", member.Name);
                        writer.WriteString("kind", member.Kind.ToString());
                        writer.WriteStartArray("transports");
                        foreach (var transport in member.Transports) writer.WriteStringValue(transport.ToString());
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("suspendedPriorities");
                foreach (var saved in candidate.SuspendedPriorities)
                {
                    writer.WriteStartObject();
                    writer.WriteString("primaryId", saved.PrimaryDeviceId);
                    writer.WriteString("id", saved.Preference.DeviceId);
                    writer.WriteString("name", saved.Preference.Name);
                    writer.WriteNumber("index", saved.Index);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (output.Length > MaximumFileBytes) return Result(false, "设置内容过大，未保存。请减少设备设置。");
                output.Flush(flushToDisk: true);
            }

            // Honor cancellation before commit; afterwards memory must match disk.
            cancellationToken.ThrowIfCancellationRequested();
            var backup = disk.Invalid ? _path + $".invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.bak" : null;
            var migrationBackup = disk.Version is 1 or 2
                ? await PreservePreviousVersionAsync(disk.Content!, disk.Version, cancellationToken).ConfigureAwait(false) : null;
            cancellationToken.ThrowIfCancellationRequested();
            if (disk.Exists) await ReplaceAsync(temporary, disk, backup, cancellationToken).ConfigureAwait(false);
            else File.Move(temporary, _path);
            temporary = null;
            _current = candidate;
            return Result(true, backupPath: backup ?? migrationBackup);
        }
        catch (PreferencesChangeException exception) { return Result(false, exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(false, $"设置保存失败：{exception.Message} (0x{exception.HResult:X8})");
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            _writes.Release();
        }
    }

    private TrayPreferencesResult Result(bool success, string? error = null, bool canSave = true, string? backupPath = null) =>
        new(success, _current.Priorities, error, canSave, backupPath, _current);

    private async Task<string> PreservePreviousVersionAsync(byte[] original, int version, CancellationToken cancellationToken)
    {
        var backup = Path.ChangeExtension(_path, $".v{version}.bak");
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(backup)) return backup;

        // Publish a fully written copy without replacing an earlier backup.
        // Keep a completed backup even if the subsequent settings commit fails
        // or is canceled; it remains a valid recovery copy of the bytes read.
        var temporary = Path.Combine(Path.GetDirectoryName(_path)!, $".{Path.GetFileName(backup)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await output.WriteAsync(original, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, backup, overwrite: false); }
            catch (IOException) when (File.Exists(backup))
            {
                // Another save published its complete backup first. Preserve it.
            }
            return backup;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task ReplaceAsync(string temporary, DiskState original, string? backup, CancellationToken cancellationToken)
    {
        // ReplaceFile 1175 leaves both original names intact. A transient reader
        // can deny deletion even after our own read handle has been disposed.
        // Retry only errors with an intact destination, never partially moved
        // replacements (1176/1177), and never fall back to deleting the original.
        int[] delays = [25, 50, 100, 200];
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (original.Content is not null)
                {
                    using var current = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (current.Length != original.Content.Length) throw new IOException("保存期间设置文件已改变，请重新读取后再试。");
                    var bytes = new byte[original.Content.Length];
                    current.ReadExactly(bytes);
                    if (!bytes.AsSpan().SequenceEqual(original.Content)) throw new IOException("保存期间设置文件已改变，请重新读取后再试。");
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Replace(temporary, _path, backup);
                return;
            }
            catch (IOException error) when (original.Content is not null && attempt < delays.Length &&
                (error.HResult & 0xFFFF) is 32 or 33 or 1175)
            {
                await Task.Delay(delays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private DiskState ReadDisk()
    {
        byte[]? originalContent = null;
        DiskState Invalid(string error) => new(true, null, error, true, 0, originalContent);
        try
        {
            using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumFileBytes) return Invalid("设置文件过大，须先明确重设才能覆盖。");
            originalContent = new byte[checked((int)input.Length)];
            input.ReadExactly(originalContent);
            using var json = JsonDocument.Parse(originalContent, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid("设置格式无效，须先明确重设才能覆盖。");
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var value))
                return Invalid("设置缺少有效版本，须先明确重设才能覆盖。");
            if (value is not (1 or 2 or 3)) return Invalid($"设置版本 {value} 暂不支持，原文件已保留；须先明确重设才能覆盖。");
            if (!(value switch
                {
                    1 => HasExactly(root, "version", "priorityDevices"),
                    2 => HasExactly(root, "version", "priorityDevices", "mainColorMode", "deviceCustomizations"),
                    _ => HasExactly(root, "version", "priorityDevices", "mainColorMode", "deviceCustomizations", "deviceLinks", "suspendedPriorities")
                }) ||
                !root.TryGetProperty("priorityDevices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                return Invalid("设置格式无效，须先明确重设才能覆盖。");
            if (devices.GetArrayLength() > MaximumDevices) return Invalid($"托盘设置最多允许 {MaximumDevices} 个优先设备。");

            var priorities = new List<TrayPreference>();
            foreach (var item in devices.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !HasExactly(item, "id", "name") ||
                    item.GetProperty("id").ValueKind != JsonValueKind.String ||
                    item.GetProperty("name").ValueKind != JsonValueKind.String)
                    return Invalid("托盘设置的设备条目格式无效，须先明确重设才能覆盖。");
                priorities.Add(new(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!));
            }

            var color = MainColorMode.System;
            var customizations = new List<DeviceCustomization>();
            if (value >= 2)
            {
                if (!TryEnum(root.GetProperty("mainColorMode"), out color)) return Invalid("颜色模式设置无效，原文件已保留。");
                var customized = root.GetProperty("deviceCustomizations");
                if (customized.ValueKind != JsonValueKind.Array || customized.GetArrayLength() > MaximumDevices)
                    return Invalid("设备设置格式无效或数量过多，原文件已保留。");
                foreach (var item in customized.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !HasCustomizationFields(item) ||
                        item.GetProperty("id").ValueKind != JsonValueKind.String)
                        return Invalid("设备设置条目格式无效，原文件已保留。");
                    string? name = null;
                    DisplayDeviceKind? kind = null;
                    if (item.TryGetProperty("name", out var nameValue))
                    {
                        if (nameValue.ValueKind != JsonValueKind.String) return Invalid("设备名称设置无效，原文件已保留。");
                        name = nameValue.GetString();
                    }
                    if (item.TryGetProperty("kind", out var kindValue))
                    {
                        if (!TryEnum<DisplayDeviceKind>(kindValue, out var parsed)) return Invalid("设备类型设置无效，原文件已保留。");
                        kind = parsed;
                    }
                    customizations.Add(new(item.GetProperty("id").GetString()!, name, kind));
                }
            }
            var links = new List<DeviceLinkGroup>();
            var suspended = new List<SuspendedLinkPriority>();
            if (value == 3)
            {
                var groups = root.GetProperty("deviceLinks");
                var saved = root.GetProperty("suspendedPriorities");
                if (groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() > MaximumDevices ||
                    saved.ValueKind != JsonValueKind.Array || saved.GetArrayLength() > MaximumDevices)
                    return Invalid("设备关联设置格式无效或数量过多，原文件已保留。");
                foreach (var group in groups.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object || !HasExactly(group, "primaryId", "members") ||
                        group.GetProperty("primaryId").ValueKind != JsonValueKind.String ||
                        group.GetProperty("members").ValueKind != JsonValueKind.Array || group.GetProperty("members").GetArrayLength() > MaximumDevices)
                        return Invalid("设备关联条目格式无效，原文件已保留。");
                    var members = new List<DeviceLinkMember>();
                    foreach (var member in group.GetProperty("members").EnumerateArray())
                    {
                        if (member.ValueKind != JsonValueKind.Object || !HasLinkMemberFields(member) ||
                            member.GetProperty("id").ValueKind != JsonValueKind.String || member.GetProperty("name").ValueKind != JsonValueKind.String)
                            return Invalid("关联成员格式无效，原文件已保留。");
                        var kind = DeviceKind.Unknown;
                        if (member.TryGetProperty("kind", out var kindValue) && !TryEnum(kindValue, out kind))
                            return Invalid("关联成员的设备类型无效，原文件已保留。");
                        var transports = new List<DeviceTransport>();
                        if (member.TryGetProperty("transports", out var transportValues))
                        {
                            if (transportValues.ValueKind != JsonValueKind.Array || transportValues.GetArrayLength() > 3)
                                return Invalid("关联成员的连接方式无效，原文件已保留。");
                            foreach (var transportValue in transportValues.EnumerateArray())
                            {
                                if (!TryEnum<DeviceTransport>(transportValue, out var transport))
                                    return Invalid("关联成员的连接方式无效，原文件已保留。");
                                transports.Add(transport);
                            }
                        }
                        members.Add(new(member.GetProperty("id").GetString()!, member.GetProperty("name").GetString()!, kind)
                        {
                            Transports = transports.Count == 0 ? Array.Empty<DeviceTransport>() : Array.AsReadOnly(transports.Order().ToArray())
                        });
                    }
                    links.Add(new(group.GetProperty("primaryId").GetString()!, members.AsReadOnly()));
                }
                foreach (var item in saved.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !HasExactly(item, "primaryId", "id", "name", "index") ||
                        item.GetProperty("primaryId").ValueKind != JsonValueKind.String || item.GetProperty("id").ValueKind != JsonValueKind.String ||
                        item.GetProperty("name").ValueKind != JsonValueKind.String || item.GetProperty("index").ValueKind != JsonValueKind.Number ||
                        !item.GetProperty("index").TryGetInt32(out var index))
                        return Invalid("关联前的优先级记录格式无效，原文件已保留。");
                    suspended.Add(new(item.GetProperty("primaryId").GetString()!,
                        new(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!), index));
                }
            }
            var settings = new AppPreferences(priorities.AsReadOnly(), color,
                Array.AsReadOnly(customizations.OrderBy(item => item.DeviceId, StringComparer.Ordinal).ToArray()))
            {
                DeviceLinks = Array.AsReadOnly(links.OrderBy(group => group.PrimaryDeviceId, StringComparer.Ordinal).ToArray()),
                SuspendedPriorities = suspended.AsReadOnly()
            };
            var validationError = Validate(settings);
            return validationError is null ? new(true, settings, null, false, value, originalContent)
                : Invalid(validationError + " 原文件已保留，须先明确重设才能覆盖。");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(false, AppPreferences.Default, null, false, 0);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return Invalid("设置 JSON 损坏，原文件已保留；须先明确重设才能覆盖。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(true, null, $"设置读取失败：{exception.Message}", false, 0);
        }
    }

    private static bool HasExactly(JsonElement element, params string[] names)
    {
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }

    private static bool HasCustomizationFields(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (property.Name is not ("id" or "name" or "kind") || !names.Add(property.Name)) return false;
        return names.Contains("id") && (names.Contains("name") || names.Contains("kind"));
    }

    private static bool HasLinkMemberFields(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (property.Name is not ("id" or "name" or "kind" or "transports") || !names.Add(property.Name)) return false;
        return names.Contains("id") && names.Contains("name");
    }

    private static bool TryEnum<T>(JsonElement element, out T value) where T : struct, Enum
    {
        value = default;
        return element.ValueKind == JsonValueKind.String && Enum.TryParse(element.GetString(), ignoreCase: false, out value)
            && Enum.IsDefined(value) && element.GetString() == value.ToString();
    }

    private static bool Equivalent(AppPreferences left, AppPreferences right) =>
        left.MainColorMode == right.MainColorMode && left.Priorities.SequenceEqual(right.Priorities)
        && left.DeviceCustomizations.SequenceEqual(right.DeviceCustomizations)
        && left.DeviceLinks.Count == right.DeviceLinks.Count
        && left.DeviceLinks.Zip(right.DeviceLinks).All(pair => pair.First.PrimaryDeviceId == pair.Second.PrimaryDeviceId &&
            pair.First.Members.Count == pair.Second.Members.Count && pair.First.Members.Zip(pair.Second.Members).All(members =>
                members.First.DeviceId == members.Second.DeviceId && members.First.Name == members.Second.Name &&
                members.First.Kind == members.Second.Kind && members.First.Transports.SequenceEqual(members.Second.Transports)))
        && left.SuspendedPriorities.SequenceEqual(right.SuspendedPriorities);

    private sealed record DiskState(bool Exists, AppPreferences? Preferences, string? Error, bool Invalid, int Version, byte[]? Content = null);
    private sealed class PreferencesChangeException(string message) : Exception(message);
}
