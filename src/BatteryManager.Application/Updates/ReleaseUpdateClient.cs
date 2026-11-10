using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace BatteryManager.Application.Updates;

internal enum UpdatePackageKind { Installer, Portable }
internal sealed record UpdatePackage(string Version, UpdatePackageKind Kind, string FileName,
    long Size, string Sha256, Uri DownloadUri);
internal sealed record AvailableUpdate(string Version, Uri ReleaseUri, IReadOnlyList<UpdatePackage> Packages);

// No credentials are needed to read the project's published releases. Package URLs must
// belong to this repository and tag; redirects are limited to GitHub's HTTPS asset hosts.
internal sealed class ReleaseUpdateClient : IReleaseUpdateClient, IDisposable
{
    internal const string Repository = "Aice-Chen/BatteryManager";
    private const int MaximumMetadataBytes = 1024 * 1024;
    private const long MaximumPackageBytes = 1024L * 1024 * 1024;
    private readonly HttpClient http;

    public ReleaseUpdateClient(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BatteryManager-Update/1.0");
    }

    public async Task<AvailableUpdate?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        _ = ReleaseVersion.IsNewer("0.0.0", currentVersion);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await GetAsync(new($"https://api.github.com/repos/{Repository}/releases/latest"), deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await ReadLimitedAsync(response.Content, MaximumMetadataBytes, deadline.Token));
        var root = release.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = RequiredString(root, "tag_name");
        if (!tag.StartsWith('v')) throw new InvalidDataException("Release tag must start with v.");
        var version = tag[1..];
        if (!ReleaseVersion.IsNewer(version, currentVersion)) return null;
        var assets = root.GetProperty("assets").EnumerateArray().Select(asset => new RemoteAsset(
            RequiredString(asset, "name"), asset.GetProperty("size").GetInt64(),
            new Uri(RequiredString(asset, "browser_download_url"), UriKind.Absolute))).ToArray();
        if (assets.GroupBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("Duplicate release assets.");
        var manifestAsset = assets.SingleOrDefault(asset => asset.Name == "update-manifest.json")
            ?? throw new InvalidDataException("This release has no update manifest.");
        ValidateAssetUri(manifestAsset.Url, tag, manifestAsset.Name);
        if (manifestAsset.Size is <= 0 or > MaximumMetadataBytes) throw new InvalidDataException("Invalid manifest size.");
        using var manifestResponse = await GetAsync(manifestAsset.Url, deadline.Token);
        manifestResponse.EnsureSuccessStatusCode();
        var bytes = await ReadLimitedAsync(manifestResponse.Content, MaximumMetadataBytes, deadline.Token);
        if (bytes.LongLength != manifestAsset.Size) throw new InvalidDataException("Manifest size differs from the release.");
        using var manifest = JsonDocument.Parse(bytes);
        var data = manifest.RootElement;
        if (data.GetProperty("schemaVersion").GetInt32() != 1 || RequiredString(data, "version") != version)
            throw new InvalidDataException("Update manifest version does not match the release.");
        var packages = new List<UpdatePackage>();
        foreach (var entry in data.GetProperty("assets").EnumerateArray())
        {
            var kind = RequiredString(entry, "kind") switch
            {
                "installer" => UpdatePackageKind.Installer,
                "portable" => UpdatePackageKind.Portable,
                _ => throw new InvalidDataException("Unknown package kind.")
            };
            var name = RequiredString(entry, "fileName");
            ValidatePackageName(version, kind, name);
            var size = entry.GetProperty("size").GetInt64();
            var hash = RequiredString(entry, "sha256");
            if (size is <= 0 or > MaximumPackageBytes || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid update package size or checksum.");
            var asset = assets.SingleOrDefault(item => item.Name == name)
                ?? throw new InvalidDataException("Manifest package is missing from the release.");
            if (asset.Size != size) throw new InvalidDataException("Package sizes disagree.");
            ValidateAssetUri(asset.Url, tag, name);
            if (packages.Any(package => package.Kind == kind)) throw new InvalidDataException("Duplicate package kind.");
            packages.Add(new(version, kind, name, size, hash.ToLowerInvariant(), asset.Url));
        }
        if (packages.Count == 0) throw new InvalidDataException("The release has no update packages.");
        return new(version, new($"https://github.com/{Repository}/releases/tag/{tag}"), packages.AsReadOnly());
    }

    // This operation only downloads. Launching an installer is a separate, user-confirmed
    // application operation after settings and hardware I/O have drained.
    public async Task<string> DownloadAsync(UpdatePackage package, string cacheDirectory,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        _ = ReleaseVersion.ParseStable(package.Version);
        ValidatePackageName(package.Version, package.Kind, package.FileName);
        ValidateAssetUri(package.DownloadUri, "v" + package.Version, package.FileName);
        if (Path.GetFileName(package.FileName) != package.FileName || package.FileName.Contains('\\') ||
            package.Size is <= 0 or > MaximumPackageBytes || package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid download descriptor.");
        var directory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, package.FileName);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            using var response = await GetAsync(package.DownloadUri, deadline.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != package.Size)
                throw new InvalidDataException("Download length differs from the manifest.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    received += count;
                    if (received > package.Size) throw new InvalidDataException("Download exceeds the manifest size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    progress?.Report(received);
                }
                if (received != package.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(package.Sha256)))
                    throw new InvalidDataException("Update package checksum or size is incorrect.");
                await output.FlushAsync(deadline.Token);
                output.Flush(flushToDisk: true);
            }
            deadline.Token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            ValidateNetworkUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                uri.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream"));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            try
            {
                var next = response.Headers.Location ?? throw new InvalidDataException("Redirect has no location.");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
            }
            finally { response.Dispose(); }
        }
        throw new InvalidDataException("Too many download redirects.");
    }

    private static void ValidateNetworkUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            throw new InvalidDataException("Untrusted update URL.");
    }

    private static void ValidateAssetUri(Uri uri, string tag, string fileName)
    {
        ValidateNetworkUri(uri);
        var expected = $"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}";
        if (uri.Host != "github.com" || uri.AbsolutePath != expected || uri.Query.Length != 0)
            throw new InvalidDataException("Update asset is outside this repository release.");
    }

    private static void ValidatePackageName(string version, UpdatePackageKind kind, string name)
    {
        var expected = kind switch
        {
            UpdatePackageKind.Installer => $"BatteryManager-{version}-win-x64-setup.exe",
            UpdatePackageKind.Portable => $"BatteryManager-{version}-win-x64.zip",
            _ => throw new InvalidDataException("Unknown package kind.")
        };
        if (!string.Equals(name, expected, StringComparison.Ordinal))
            throw new InvalidDataException("Package name, version, architecture or kind does not match.");
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.GetProperty(name).GetString() is { Length: > 0 } text ? text : throw new InvalidDataException($"Missing {name}.");

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Update metadata is too large.");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Update metadata is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public void Dispose() => http.Dispose();
    private sealed record RemoteAsset(string Name, long Size, Uri Url);
}
