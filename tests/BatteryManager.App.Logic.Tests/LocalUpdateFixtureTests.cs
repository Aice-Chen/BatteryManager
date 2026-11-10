using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatteryManager.App;
using BatteryManager.Application.Updates;
using Xunit;

namespace BatteryManager.App.Logic.Tests;

public sealed class LocalUpdateFixtureTests
{
    private const string Latest = "https://api.github.com/repos/Aice-Chen/BatteryManager/releases/latest";
    private const string Base = "https://github.com/Aice-Chen/BatteryManager/releases/download/v0.2.0/";
    private const string PackageName = "BatteryManager-0.2.0-win-x64-setup.exe";

    [Fact]
    public void OptInRequiresPipeAndCannotChangeScenarioStorageRules()
    {
        const string path = @"C:\fixture\update.json";
        Assert.Throws<ArgumentException>(() => LocalUpdateFixture.ExtractArguments(["--update-fixture", path]));
        Assert.Throws<ArgumentException>(() => LocalUpdateFixture.ExtractArguments(["--test-pipe", "check", "--scenario", "overview", "--update-fixture", path]));
        Assert.Throws<ArgumentException>(() => LocalUpdateFixture.ExtractArguments(["--test-pipe", "check", "--update-fixture", "relative.json"]));
        Assert.Throws<ArgumentException>(() => LocalUpdateFixture.ExtractArguments(["--test-pipe", "check", "--update-fixture", path, "--update-fixture", path]));
        var parsed = LocalUpdateFixture.ExtractArguments(["--test-pipe", "check", "--update-fixture", path]);
        Assert.Equal(path, parsed.FixturePath);
        Assert.Equal(new[] { "--test-pipe", "check" }, parsed.RemainingArguments);
        Assert.Null(AppScenarios.NameFromArguments(parsed.RemainingArguments));
        var normal = LocalUpdateFixture.ExtractArguments(["--scenario", "overview"]);
        Assert.Null(normal.FixturePath);
        Assert.Equal("overview", AppScenarios.NameFromArguments(normal.RemainingArguments));
    }

    [Fact]
    public async Task ActualClientChecksVersionManifestAndWritesVerifiedDownload()
    {
        using var files = new FixtureFiles();
        var payload = Encoding.UTF8.GetBytes("Local acceptance unit-test bytes; never an executable.");
        files.WriteRelease(payload);
        var requests = new List<string>();
        using var client = new ReleaseUpdateClient(files.Load().CreateHandler((url, _) => requests.Add(url)));
        var update = Assert.IsType<AvailableUpdate>(await client.CheckAsync("0.2.0-preview.2"));
        var package = Assert.Single(update.Packages);
        var destination = await client.DownloadAsync(package, files.Cache);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(new[] { Latest, Base + "update-manifest.json", Base + PackageName }, requests);
        Assert.Empty(Directory.GetFiles(files.Cache, "*.partial"));
    }

    [Fact]
    public async Task ChecksumFailureKeepsPreviousCacheAndRemovesPartial()
    {
        using var files = new FixtureFiles();
        files.WriteRelease([1, 2, 3]);
        File.WriteAllBytes(Path.Combine(files.Path, PackageName), [4, 5, 6]);
        Directory.CreateDirectory(files.Cache);
        var destination = Path.Combine(files.Cache, PackageName);
        File.WriteAllBytes(destination, [7, 8]);
        using var client = new ReleaseUpdateClient(files.Load().CreateHandler());
        var package = Assert.Single((await client.CheckAsync("0.2.0-preview.2"))!.Packages);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(package, files.Cache));
        Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(files.Cache, "*.partial"));
    }

    [Fact]
    public async Task FixtureDoesNotForceAnUpdateAtTheSameStableVersion()
    {
        using var files = new FixtureFiles();
        files.WriteRelease([1, 2, 3]);
        var count = 0;
        using var client = new ReleaseUpdateClient(files.Load().CreateHandler((_, _) => count++));
        Assert.Null(await client.CheckAsync("0.2.0"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task FixtureDoesNotRelaxRepositoryOrHttpsValidation()
    {
        using var files = new FixtureFiles();
        files.WriteRelease([1, 2, 3], manifestUrl: "https://github.com/someone/other/releases/download/v0.2.0/update-manifest.json");
        var count = 0;
        using (var client = new ReleaseUpdateClient(files.Load().CreateHandler((_, _) => count++)))
            await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync("0.2.0-preview.2"));
        Assert.Equal(1, count);
        files.WriteRoutes([new Route(Latest, 302, null, "http://api.github.com/unsafe", 0)]);
        count = 0;
        using (var client = new ReleaseUpdateClient(files.Load().CreateHandler((_, _) => count++)))
            await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync("0.2.0-preview.2"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UnmappedRequestsFailInsteadOfContactingNetwork()
    {
        using var files = new FixtureFiles();
        files.WriteRoutes([new Route(Latest, 404, null, null, 0)]);
        using var client = new HttpClient(files.Load().CreateHandler());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://github.com/unmapped"));
        Assert.Contains("Network fallback is disabled", error.Message);
    }

    [Fact]
    public async Task DelayedResponsesHonorCancellation()
    {
        using var files = new FixtureFiles();
        files.WriteRoutes([new Route(Latest, 404, null, null, 10000)]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new ReleaseUpdateClient(files.Load().CreateHandler((_, _) => entered.TrySetResult()));
        using var cancellation = new CancellationTokenSource();
        var check = client.CheckAsync("0.2.0-preview.2", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ResponseOwnsBodyReadLeaseUntilDisposed()
    {
        using var files = new FixtureFiles();
        files.WriteRelease([1, 2, 3]);
        using var client = new HttpClient(files.Load().CreateHandler());
        var response = await client.GetAsync(Base + PackageName, HttpCompletionOption.ResponseHeadersRead);
        var file = Path.Combine(files.Path, PackageName);
        try { Assert.Throws<IOException>(() => { using var rejected = File.Open(file, FileMode.Open, FileAccess.Write, FileShare.Read); }); }
        finally { response.Dispose(); }
        using var writable = File.Open(file, FileMode.Open, FileAccess.Write, FileShare.Read);
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("C:\\outside.bin")]
    public void BodyCannotEscapeFixtureDirectory(string body)
    {
        using var files = new FixtureFiles();
        files.WriteRoutes([new Route(Latest, 200, body, null, 0)]);
        Assert.Throws<InvalidDataException>(() => files.Load());
    }

    [Fact]
    public void DuplicateRoutesAndUnmarkedMetadataAreRejected()
    {
        using var files = new FixtureFiles();
        files.WriteRoutes([new Route(Latest, 404, null, null, 0), new Route(Latest, 404, null, null, 0)]);
        Assert.Throws<InvalidDataException>(() => files.Load());
        File.WriteAllText(files.Config, "{\"schemaVersion\":1,\"purpose\":\"production\",\"responses\":[]}");
        Assert.Throws<InvalidDataException>(() => files.Load());
        File.WriteAllText(files.Config, "{\"schemaVersion\":1,\"schemaVersion\":1,\"purpose\":\"local-update-acceptance\",\"responses\":[]}");
        Assert.Throws<InvalidDataException>(() => files.Load());
    }

    private sealed record Route(string url, int statusCode, string? bodyFile, string? location, int delayMilliseconds);

    private sealed class FixtureFiles : IDisposable
    {
        private readonly string boundary;
        public string Path { get; }
        public string Config => System.IO.Path.Combine(Path, "fixture.json");
        public string Cache => System.IO.Path.Combine(Path, "cache");
        public FixtureFiles()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "version.props"))) root = root.Parent;
            if (root is null) throw new InvalidOperationException("The test workspace was not found.");
            boundary = System.IO.Path.Combine(root.FullName, ".artifacts", "tests", "local-update-fixture");
            Path = System.IO.Path.Combine(boundary, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public LocalUpdateFixture Load() => LocalUpdateFixture.Load(Config);
        public void WriteRoutes(Route[] routes) => File.WriteAllText(Config, JsonSerializer.Serialize(new { schemaVersion = 1, purpose = "local-update-acceptance", responses = routes }));
        public void WriteRelease(byte[] payload, string? manifestUrl = null)
        {
            File.WriteAllBytes(System.IO.Path.Combine(Path, PackageName), payload);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, version = "0.2.0", assets = new[] { new { kind = "installer", fileName = PackageName,
                    size = payload.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)) } }
            });
            File.WriteAllBytes(System.IO.Path.Combine(Path, "update-manifest.json"), manifest);
            var release = new
            {
                draft = false, prerelease = false, tag_name = "v0.2.0", assets = new[] {
                    new { name = "update-manifest.json", size = manifest.LongLength, browser_download_url = manifestUrl ?? Base + "update-manifest.json" },
                    new { name = PackageName, size = payload.LongLength, browser_download_url = Base + PackageName }
                }
            };
            File.WriteAllText(System.IO.Path.Combine(Path, "release.json"), JsonSerializer.Serialize(release));
            WriteRoutes([new(Latest, 200, "release.json", null, 0), new(Base + "update-manifest.json", 200, "update-manifest.json", null, 0),
                new(Base + PackageName, 200, PackageName, null, 0)]);
        }
        public void Dispose()
        {
            if (System.IO.Path.GetFullPath(Path).StartsWith(boundary + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(Path, recursive: true);
        }
    }
}
