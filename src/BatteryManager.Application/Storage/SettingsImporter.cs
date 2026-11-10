namespace BatteryManager.Application;

internal sealed record SettingsImportResult(bool Success, string? Error = null);

/// <summary>Explicit import from a user-selected directory. Originals are never modified.
/// Backups publish first, settings publish last; an interrupted import may leave matching backups,
/// which a subsequent attempt can reuse without overwriting either copy.</summary>
internal static class SettingsImporter
{
    private const int MaximumFileBytes = 256 * 1024;

    public static async Task<SettingsImportResult> ImportAsync(string sourceDirectory, AppDataPaths destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            sourceDirectory = AppDataPaths.AbsoluteDirectory(sourceDirectory);
            if (string.Equals(sourceDirectory, destination.DataDirectory, StringComparison.OrdinalIgnoreCase))
                return new(false, "源目录与目标目录相同。");
            if (Path.Exists(destination.SettingsPath)) return new(false, "目标目录已有设置，未覆盖现有配置。");

            var settings = await ReadSnapshotAsync(Path.Combine(sourceDirectory, "settings.json"), () =>
            {
                var loaded = new TrayPreferencesStore(Path.Combine(sourceDirectory, "settings.json")).Load();
                if (!loaded.Success || !loaded.CanSave) throw new InvalidDataException(loaded.Error ?? "源配置无法读取。");
            }, cancellationToken).ConfigureAwait(false);
            var backups = new List<(string Name, byte[] Bytes)>();
            foreach (var name in new[] { "settings.v1.bak", "settings.v2.bak" })
            {
                try { backups.Add((name, await ReadSnapshotAsync(Path.Combine(sourceDirectory, name), null, cancellationToken).ConfigureAwait(false))); }
                catch (FileNotFoundException) { }
            }
            // Check all conflicts before creating the destination or publishing any file.
            foreach (var backup in backups)
                await RequireMissingOrMatchingBackupAsync(Path.Combine(destination.DataDirectory, backup.Name), backup.Bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.Exists(destination.SettingsPath)) return new(false, "目标目录已有设置，未覆盖现有配置。");
            Directory.CreateDirectory(destination.DataDirectory);
            foreach (var backup in backups)
                await PublishAsync(Path.Combine(destination.DataDirectory, backup.Name), backup.Bytes, allowMatching: true, cancellationToken).ConfigureAwait(false);
            await PublishAsync(destination.SettingsPath, settings, allowMatching: false, cancellationToken).ConfigureAwait(false);
            return new(true);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(false, "导入配置失败：" + error.Message);
        }
    }

    private static async Task<byte[]> ReadSnapshotAsync(string path, Action? validate, CancellationToken token)
    {
        // FileShare.Read permits the production parser to read the same immutable bytes,
        // while denying concurrent writes/deletion until validation has completed.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length > MaximumFileBytes) throw new InvalidDataException("配置或备份文件过大。");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        validate?.Invoke();
        return bytes;
    }

    private static async Task RequireMissingOrMatchingBackupAsync(string path, byte[] bytes, CancellationToken token)
    {
        try
        {
            var existing = await ReadSnapshotAsync(path, null, token).ConfigureAwait(false);
            if (!existing.AsSpan().SequenceEqual(bytes)) throw new IOException("目标目录已有不同的配置备份，未覆盖该文件。");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static async Task PublishAsync(string path, byte[] bytes, bool allowMatching, CancellationToken token)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".settings-import-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (allowMatching && File.Exists(path))
            {
                var existing = await ReadSnapshotAsync(path, null, token).ConfigureAwait(false);
                if (!existing.AsSpan().SequenceEqual(bytes)) throw new IOException("目标目录已有不同的配置备份，未覆盖该文件。");
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
