using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryManager.Application.Updates;
using Xunit;

namespace BatteryManager.Application.Tests;

public sealed class ReleaseUpdateClientTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.9", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.2.0", "0.2.0-preview.1+source.123", true)]
    [InlineData("0.2.0", "0.2.0+source.123", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    public void StableVersionComparison(string next, string current, bool newer) =>
        Assert.Equal(newer, ReleaseVersion.IsNewer(next, current));

    [Theory]
    [InlineData("01.2.0")]
    [InlineData("0.2")]
    [InlineData("0.2.0-preview.1")]
    [InlineData("0.2.0\n")]
    public void InvalidStableVersionIsRejected(string version) =>
        Assert.Throws<InvalidDataException>(() => ReleaseVersion.ParseStable(version));

    [Fact]
    public async Task NoPublishedReleaseDoesNotInventAnUpdate()
    {
        using var client = new ReleaseUpdateClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        Assert.Null(await client.CheckAsync("0.1.0"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DraftOrPreviewIsNotOffered(bool draft, bool prerelease)
    {
        using var client = new ReleaseUpdateClient(new Handler(_ => Json(new { draft, prerelease })));
        Assert.Null(await client.CheckAsync("0.1.0"));
    }

    [Fact]
    public async Task DiscoveryAndVerifiedDownloadFollowPublishedManifest()
    {
        var payload = Encoding.UTF8.GetBytes("fixture update package; not executable");
        var fixture = Fixture(payload);
        var requests = new List<Uri>();
        using var client = new ReleaseUpdateClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.Host == "api.github.com" ? Json(fixture.Release)
                : request.RequestUri.AbsolutePath.EndsWith("update-manifest.json", StringComparison.Ordinal) ? Bytes(fixture.Manifest)
                : Bytes(payload);
        }));
        using var directory = new ScratchDirectory();
        var update = Assert.IsType<AvailableUpdate>(await client.CheckAsync("0.1.0"));
        var package = Assert.Single(update.Packages);
        Assert.Equal("0.2.0", update.Version);
        var path = await client.DownloadAsync(package, directory.Path);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(directory.Path));
        Assert.Equal(3, requests.Count);
    }

    [Theory]
    [InlineData("http://github.com/Aice-Chen/BatteryManager/releases/download/v0.2.0/BatteryManager-0.2.0-win-x64-setup.exe")]
    [InlineData("https://github.com/other/BatteryManager/releases/download/v0.2.0/BatteryManager-0.2.0-win-x64-setup.exe")]
    [InlineData("https://example.com/BatteryManager-0.2.0-win-x64-setup.exe")]
    public async Task DiscoveryRejectsPackageOutsideRepository(string url)
    {
        var fixture = Fixture([1, 2, 3], packageUrl: url);
        using var client = new ReleaseUpdateClient(new Handler(request =>
            request.RequestUri!.Host == "api.github.com" ? Json(fixture.Release) : Bytes(fixture.Manifest)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync("0.1.0"));
    }

    [Theory]
    [InlineData("0.3.0", 3L)]
    [InlineData("0.2.0", 4L)]
    public async Task VersionAndSizeMustAgreeAcrossManifestAndRelease(string version, long manifestSize)
    {
        var fixture = Fixture([1, 2, 3], manifestVersion: version, manifestSize: manifestSize);
        using var client = new ReleaseUpdateClient(new Handler(request =>
            request.RequestUri!.Host == "api.github.com" ? Json(fixture.Release) : Bytes(fixture.Manifest)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync("0.1.0"));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(2)]
    public async Task CorruptTruncatedOrOversizedDownloadKeepsExistingFile(int length)
    {
        using var directory = new ScratchDirectory();
        var package = Package([1, 2, 3]);
        var existing = Path.Combine(directory.Path, package.FileName);
        await File.WriteAllTextAsync(existing, "previous verified file");
        using var client = new ReleaseUpdateClient(new Handler(_ => Bytes(new byte[length])));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(package, directory.Path));
        Assert.Equal("previous verified file", await File.ReadAllTextAsync(existing));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task RejectedRedirectIsNotContacted()
    {
        var requests = 0;
        using var directory = new ScratchDirectory();
        using var client = new ReleaseUpdateClient(new Handler(_ =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new("https://example.com/payload.exe");
            return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(Package([1]), directory.Path));
        Assert.Equal(1, requests);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CancellationRemovesPartialDownload()
    {
        using var directory = new ScratchDirectory();
        using var cancellation = new CancellationTokenSource();
        using var client = new ReleaseUpdateClient(new Handler(_ => Bytes([1, 2, 3])));
        var existing = Path.Combine(directory.Path, Package([1, 2, 3]).FileName);
        await File.WriteAllTextAsync(existing, "existing before cancellation");
        var progress = new CallbackProgress(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DownloadAsync(Package([1, 2, 3]), directory.Path, progress, cancellation.Token));
        Assert.Equal("existing before cancellation", await File.ReadAllTextAsync(existing));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("BatteryManager-0.1.0-win-x64-setup.exe")]
    [InlineData("BatteryManager-0.2.0-win-arm64-setup.exe")]
    [InlineData("BatteryManager-0.2.0-win-x64.zip")]
    [InlineData("BatteryManager-a:stream.exe")]
    public async Task DirectDownloadRejectsWrongVersionArchitectureOrKindBeforeHttp(string name)
    {
        using var directory = new ScratchDirectory();
        var requests = 0;
        using var client = new ReleaseUpdateClient(new Handler(_ => { requests++; return Bytes([1]); }));
        var package = Package([1]) with { FileName = name };
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(package, directory.Path));
        Assert.Equal(0, requests);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(3, true)]
    public async Task StreamingFailurePreservesExistingFileAndRemovesPartial(int length, bool failAfterFirstRead)
    {
        using var directory = new ScratchDirectory();
        var package = Package([1, 2, 3]);
        var target = Path.Combine(directory.Path, package.FileName);
        await File.WriteAllTextAsync(target, "existing");
        using var client = new ReleaseUpdateClient(new Handler(_ => new(HttpStatusCode.OK)
        { Content = new StreamContent(new FixtureStream(Enumerable.Range(1, length).Select(value => (byte)value).ToArray(), failAfterFirstRead)) }));
        if (failAfterFirstRead)
            await Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(package, directory.Path));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(package, directory.Path));
        Assert.Equal("existing", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task GitHubAssetRedirectCanCompleteVerifiedDownload()
    {
        using var directory = new ScratchDirectory();
        var requests = 0;
        using var client = new ReleaseUpdateClient(new Handler(request =>
        {
            requests++;
            if (request.RequestUri!.Host == "release-assets.githubusercontent.com") return Bytes([1, 2, 3]);
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new("https://release-assets.githubusercontent.com/fixture?token=fixture");
            return response;
        }));
        var path = await client.DownloadAsync(Package([1, 2, 3]), directory.Path);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, requests);
    }

    private static UpdatePackage Package(byte[] payload) => new("0.2.0", UpdatePackageKind.Installer,
        "BatteryManager-0.2.0-win-x64-setup.exe", payload.Length,
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
        new("https://github.com/Aice-Chen/BatteryManager/releases/download/v0.2.0/BatteryManager-0.2.0-win-x64-setup.exe"));

    private static (object Release, byte[] Manifest) Fixture(byte[] payload, string? packageUrl = null,
        string manifestVersion = "0.2.0", long? manifestSize = null)
    {
        var package = Package(payload);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, version = manifestVersion,
            assets = new[] { new { kind = "installer", fileName = package.FileName, sha256 = package.Sha256, size = manifestSize ?? package.Size } } });
        object release = new { tag_name = "v0.2.0", draft = false, prerelease = false,
            assets = new[] {
                new { name = "update-manifest.json", size = (long)manifest.Length,
                    browser_download_url = "https://github.com/Aice-Chen/BatteryManager/releases/download/v0.2.0/update-manifest.json" },
                new { name = package.FileName, size = package.Size, browser_download_url = packageUrl ?? package.DownloadUri.AbsoluteUri }
            } };
        return (release, manifest);
    }

    private static HttpResponseMessage Json(object data) => Bytes(JsonSerializer.SerializeToUtf8Bytes(data));
    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
    }
    private sealed class CallbackProgress(Action<long> action) : IProgress<long> { public void Report(long value) => action(value); }
    // No length and no seeking, so StreamContent cannot reject these fixtures by headers alone.
    private sealed class FixtureStream(byte[] data, bool failAfterFirstRead) : Stream
    {
        private int offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
        public override int Read(Span<byte> buffer)
        {
            if (offset > 0 && failAfterFirstRead) throw new IOException("Fixture stream failure.");
            if (offset == data.Length) return 0;
            buffer[0] = data[offset++]; return 1;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "update-test-" + Guid.NewGuid().ToString("N"));
        public ScratchDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
