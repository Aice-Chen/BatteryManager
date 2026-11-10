using System.Text.Json;

namespace BatteryManager.Application.Management;

/// <summary>Small, independent update preferences. Loading never writes or repairs a file.</summary>
internal sealed class ManagementStore(string path) : IManagementStore
{
    private const int MaximumBytes = 8192;
    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private ManagementSettings _current = new();

    public ManagementStoreResult Load()
    {
        _writes.Wait();
        try
        {
            var disk = ReadDisk();
            if (disk.Error is not null) return new(false, _current, disk.Error, false);
            _current = disk.Settings!;
            return new(true, _current);
        }
        finally { _writes.Release(); }
    }

    public Task<ManagementStoreResult> SetAutomaticChecksAsync(bool enabled, CancellationToken cancellationToken = default) =>
        ChangeAsync(settings => settings with { AutoCheckUpdates = enabled }, cancellationToken);
    public Task<ManagementStoreResult> RecordCheckAttemptAsync(DateTimeOffset at, CancellationToken cancellationToken = default) =>
        ChangeAsync(settings => settings with { LastCheckUtc = at.ToUniversalTime() }, cancellationToken);
    public Task<ManagementStoreResult> RecordSuccessfulCheckAsync(DateTimeOffset at, CancellationToken cancellationToken = default) =>
        ChangeAsync(settings => settings with { LastSuccessfulCheckUtc = at.ToUniversalTime() }, cancellationToken);

    private async Task<ManagementStoreResult> ChangeAsync(Func<ManagementSettings, ManagementSettings> change, CancellationToken token)
    {
        await _writes.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var disk = ReadDisk();
            if (disk.Error is not null) return new(false, _current, disk.Error, false);
            var candidate = change(disk.Settings!);
            if (candidate == disk.Settings)
            {
                _current = candidate;
                return new(true, _current);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, autoCheckUpdates = candidate.AutoCheckUpdates,
                lastCheckUtc = candidate.LastCheckUtc, lastSuccessfulCheckUtc = candidate.LastSuccessfulCheckUtc
            });
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = Path.Combine(Path.GetDirectoryName(_path)!, ".management-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (disk.Bytes is null) File.Move(temporary, _path, overwrite: false);
            else
            {
                // A different process/editor may have changed the file since this transaction began.
                var current = ReadDisk();
                if (current.Error is not null || current.Bytes is null || !current.Bytes.AsSpan().SequenceEqual(disk.Bytes))
                    return new(false, _current, "保存期间更新设置已改变，请重新读取后重试。", current.Error is null);
                File.Replace(temporary, _path, null);
            }
            _current = candidate;
            return new(true, _current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(false, _current, "更新设置保存失败：" + error.Message);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            _writes.Release();
        }
    }

    private DiskState ReadDisk()
    {
        try
        {
            using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumBytes) return new(null, null, "更新设置文件过大，原文件已保留。");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid();
            var names = root.EnumerateObject().Select(item => item.Name).ToArray();
            if (names.Length != 4 || names.Distinct(StringComparer.Ordinal).Count() != 4 ||
                !root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("autoCheckUpdates", out var automatic) || automatic.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !TryUtc(root, "lastCheckUtc", out var lastCheck) || !TryUtc(root, "lastSuccessfulCheckUtc", out var lastSuccess))
                return Invalid();
            return new(new(automatic.GetBoolean(), lastCheck, lastSuccess), bytes, null);
        }
        catch (FileNotFoundException) { return new(new(), null, null); }
        catch (DirectoryNotFoundException) { return new(new(), null, null); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new(null, null, "更新设置无法读取，原文件已保留：" + error.Message); }

        static DiskState Invalid() => new(null, null, "更新设置格式或版本不受支持，原文件已保留。");
    }

    private static bool TryUtc(JsonElement root, string name, out DateTimeOffset? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var field)) return false;
        if (field.ValueKind == JsonValueKind.Null) return true;
        if (field.ValueKind != JsonValueKind.String || !field.TryGetDateTimeOffset(out var at) || at.Offset != TimeSpan.Zero) return false;
        value = at;
        return true;
    }

    private sealed record DiskState(ManagementSettings? Settings, byte[]? Bytes, string? Error);
}
