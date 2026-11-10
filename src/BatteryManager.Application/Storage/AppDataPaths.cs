using System.Text;
using System.Text.Json;

namespace BatteryManager.Application;

/// <summary>Resolves storage without creating directories or probing other installations.</summary>
internal sealed class AppDataPaths
{
    private const int MaximumMarkerBytes = 4096;

    private AppDataPaths(string appDirectory, string dataDirectory, bool installed)
    {
        AppDirectory = appDirectory;
        DataDirectory = dataDirectory;
        IsInstalled = installed;
    }

    public string AppDirectory { get; }
    public string DataDirectory { get; }
    public bool IsInstalled { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string ManagementPath => Path.Combine(DataDirectory, "management.json");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string UpdatesDirectory => Path.Combine(DataDirectory, "updates");

    public static AppDataPaths Resolve(string appDirectory, string localAppDataDirectory)
    {
        appDirectory = AbsoluteDirectory(appDirectory);
        string? marker;
        try
        {
            using var input = new FileStream(Path.Combine(appDirectory, "installed.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumMarkerBytes) throw new InvalidDataException("安装标记过大。");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            marker = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (FileNotFoundException) { marker = null; }
        catch (DirectoryNotFoundException) { marker = null; }
        catch (DecoderFallbackException error) { throw new InvalidDataException("安装标记不是有效的 UTF-8。", error); }
        return Parse(appDirectory, localAppDataDirectory, marker);
    }

    /// <summary>A missing marker keeps development builds local to their output directory. Invalid installed markers fail explicitly;
    /// silently switching directories would hide the user's existing settings.</summary>
    public static AppDataPaths Parse(string appDirectory, string localAppDataDirectory, string? markerJson)
    {
        appDirectory = AbsoluteDirectory(appDirectory);
        localAppDataDirectory = AbsoluteDirectory(localAppDataDirectory);
        if (markerJson is null) return new(appDirectory, appDirectory, false);
        if (Encoding.UTF8.GetByteCount(markerJson) > MaximumMarkerBytes) throw new InvalidDataException("安装标记过大。");
        try
        {
            using var json = JsonDocument.Parse(markerJson, new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("安装标记格式无效。");
            var fields = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (fields.Length != 3 || fields.Distinct(StringComparer.Ordinal).Count() != 3 ||
                !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.String || product.GetString() != "BatteryManager" ||
                !root.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.String || scope.GetString() != "currentUser")
                throw new InvalidDataException("安装标记的版本、产品或安装范围无效。");
        }
        catch (JsonException error) { throw new InvalidDataException("安装标记格式无效。", error); }
        return new(appDirectory, Path.Combine(localAppDataDirectory, "BatteryManager"), true);
    }

    internal static string AbsoluteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("目录必须为完整的绝对路径。", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
