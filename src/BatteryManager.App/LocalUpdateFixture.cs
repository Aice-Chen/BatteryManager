using System.Net;
using System.Text.Json;

namespace BatteryManager.App;

// Opt-in acceptance transport. Production URL/version/manifest/hash checks still run in
// ReleaseUpdateClient. This handler has no inner handler and can never fall back to HTTP.
internal sealed class LocalUpdateFixture
{
    private const long MaximumBodyBytes = 1024L * 1024 * 1024;
    private readonly IReadOnlyDictionary<string, Response> responses;
    private readonly string directory;
    public string FilePath { get; }

    private LocalUpdateFixture(string filePath, IReadOnlyDictionary<string, Response> responses)
    { FilePath = filePath; directory = Path.GetDirectoryName(filePath)!; this.responses = responses; }

    public static (string? FixturePath, string[] RemainingArguments) ExtractArguments(string[] arguments)
    {
        string? fixture = null;
        var remaining = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] != "--update-fixture") { remaining.Add(arguments[index]); continue; }
            if (fixture is not null || ++index >= arguments.Length || !Path.IsPathFullyQualified(arguments[index]))
                throw new ArgumentException("Use --update-fixture once, followed by an absolute local JSON path.");
            fixture = arguments[index];
        }
        if (fixture is not null && (!remaining.Contains("--test-pipe", StringComparer.Ordinal) || remaining.Contains("--scenario", StringComparer.Ordinal)))
            throw new ArgumentException("An update fixture requires --test-pipe and cannot be combined with --scenario.");
        return (fixture, remaining.ToArray());
    }

    public static LocalUpdateFixture Load(string path)
    {
        var fullPath = LocalPath(path);
        var rootDirectory = Path.GetDirectoryName(fullPath)!;
        using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("The update fixture is empty or oversized.");
        using var json = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        Fields(root, "schemaVersion", "purpose", "responses");
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("purpose").GetString() != "local-update-acceptance")
            throw new InvalidDataException("The fixture must explicitly identify local update acceptance.");
        var routes = new Dictionary<string, Response>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("responses").EnumerateArray())
        {
            Fields(item, "url", "statusCode", "bodyFile", "location", "delayMilliseconds");
            var uri = new Uri(item.GetProperty("url").GetString()!, UriKind.Absolute);
            var status = item.GetProperty("statusCode").GetInt32();
            var delay = item.GetProperty("delayMilliseconds").GetInt32();
            if (status is < 200 or > 599 || delay is < 0 or > 10000) throw new InvalidDataException("Invalid fixture response status or delay.");
            var file = item.GetProperty("bodyFile").GetString();
            if (file is not null)
            {
                if (string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file)) throw new InvalidDataException("Fixture body paths must be relative.");
                file = LocalPath(Path.Combine(rootDirectory, file));
                var boundary = Path.EndsInDirectorySeparator(rootDirectory) ? rootDirectory : rootDirectory + Path.DirectorySeparatorChar;
                if (!file.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Fixture bodies must stay within the fixture directory.");
                if (new FileInfo(file).Length > MaximumBodyBytes) throw new InvalidDataException("Fixture body exceeds the package limit.");
            }
            var location = item.GetProperty("location").GetString() is { } destination ? new Uri(destination, UriKind.RelativeOrAbsolute) : null;
            if (!routes.TryAdd(uri.AbsoluteUri, new((HttpStatusCode)status, file, location, delay)))
                throw new InvalidDataException("Duplicate fixture response URL.");
        }
        if (routes.Count is < 1 or > 32) throw new InvalidDataException("A fixture needs between 1 and 32 exact response routes.");
        return new(fullPath, routes);
    }

    public HttpMessageHandler CreateHandler(Action<string, int>? recordResponse = null) => new Handler(responses, recordResponse);

    public string InstallerLogPath => LocalPath(Path.Combine(directory, "installer-handoff.log"));

    private static string LocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Fixture paths must be absolute.");
        var full = Path.GetFullPath(path);
        // Network shares and device paths are outside this local-file test boundary.
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':')
            throw new InvalidDataException("Fixture paths must be on a local drive.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Redirected fixture paths are not allowed.");
        return full;
    }

    private static void Fields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Fixture entries must be objects.");
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            names.Any(name => !expected.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("Unexpected or duplicate fixture fields.");
    }

    private sealed record Response(HttpStatusCode Status, string? File, Uri? Location, int DelayMilliseconds);

    private sealed class Handler(IReadOnlyDictionary<string, Response> responses, Action<string, int>? recordResponse) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.RequestUri is null || !responses.TryGetValue(request.RequestUri.AbsoluteUri, out var route))
                throw new HttpRequestException("No local fixture response matches this exact request. Network fallback is disabled.");
            recordResponse?.Invoke(request.RequestUri.AbsoluteUri, (int)route.Status);
            if (route.DelayMilliseconds > 0) await Task.Delay(route.DelayMilliseconds, cancellationToken);
            HttpContent body = new ByteArrayContent([]);
            if (route.File is { } file)
            {
                // Recheck the path at use, then hold a read lease until HTTP content is disposed.
                var input = new FileStream(LocalPath(file), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                if (input.Length > MaximumBodyBytes) { input.Dispose(); throw new InvalidDataException("Fixture body exceeds the package limit."); }
                body.Dispose(); body = new StreamContent(input);
                body.Headers.ContentLength = input.Length;
            }
            var response = new HttpResponseMessage(route.Status) { Content = body };
            response.Headers.Location = route.Location;
            return response;
        }
    }
}
