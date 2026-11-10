using System.Diagnostics;
using System.Text.Json;
using BatteryManager.WindowsNative;

// Diagnostic only: fixed, evidenced read queries plus passive vendor collections.
// It never registers keyboard/mouse input hooks or sends settings/firmware commands.
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ReceiverProbe <session-directory-under-project-.artifacts>");
    return 2;
}
var root = FindRoot();
var directory = Path.GetFullPath(args[0]);
var allowed = Path.Combine(root, ".artifacts") + Path.DirectorySeparatorChar;
if (!directory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("The capture directory must be inside this project's .artifacts directory.");
Directory.CreateDirectory(directory);
var eventsFile = Path.Combine(directory, "events.jsonl");
if (File.Exists(eventsFile)) throw new IOException("Use a new capture directory to preserve previous evidence.");
var requestFile = Path.Combine(directory, "request.json");
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
using var capture = new CancellationTokenSource(TimeSpan.FromMinutes(30));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; capture.Cancel(); };
using var writer = new StreamWriter(new FileStream(eventsFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
var logGate = new object();
var timer = Stopwatch.StartNew();
var records = 0;
var inputCount = 0;
var failed = 0;
var io = new WindowsHidIo();
var inventory = await io.EnumerateAsync(capture.Token);
if (!inventory.Complete || inventory.Errors.Count != 0) throw new IOException("HID inventory was incomplete: " + string.Join("; ", inventory.Errors));
var candidates = inventory.Interfaces.Select(device => new Target(Select(device), device))
    .Where(target => target.Name.Length > 0).ToArray();
if (candidates.GroupBy(x => x.Name).Any(group => group.Count() > 1))
    throw new IOException("More than one receiver matches a capture target; refusing ambiguous attribution.");
if (!candidates.Any(x => x.Name == "lofree-query") || !candidates.Any(x => x.Name == "mchose-query"))
    throw new IOException("Both verified receiver query collections must be present.");
Log("start", "host", "unlabelled", 0, new { processId = Environment.ProcessId, targets = candidates.Select(x => new { x.Name, x.Device.UsagePage, x.Device.Usage, x.Device.InputReportLength, x.Device.OutputReportLength }), limitMinutes = 30, limitRecords = 20000 });
var workers = candidates.Select(Listen).ToArray();
var control = WatchControl();
await Task.WhenAll(workers.Append(control));
Log("complete", "host", "finished", 0, new { inputCount, failed, physicallyDrained = true });
return failed == 0 ? 0 : 1;

void Log(string kind, string target, string phase, long requestId, object? data = null)
{
    lock (logGate)
    {
        if (++records > 20000 && kind != "complete")
        {
            Interlocked.Exchange(ref failed, 1);
            capture.Cancel();
            return;
        }
        writer.WriteLine(JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, elapsedMs = timer.ElapsedMilliseconds, kind, target, phase, requestId, data }, json));
    }
}

Request? ReadRequest()
{
    try { return File.Exists(requestFile) ? JsonSerializer.Deserialize<Request>(File.ReadAllText(requestFile), json) : null; }
    catch (Exception error) when (error is IOException or JsonException) { return null; }
}

async Task WatchControl()
{
    try
    {
        var nextHeartbeat = TimeSpan.Zero;
        while (!capture.IsCancellationRequested)
        {
            if (ReadRequest()?.Action == "quit") { capture.Cancel(); break; }
            if (timer.Elapsed >= nextHeartbeat)
            {
                Log("heartbeat", "host", "monitoring", 0, new { inputCount, records });
                nextHeartbeat = timer.Elapsed + TimeSpan.FromSeconds(10);
            }
            await Task.Delay(200, capture.Token);
        }
    }
    catch (OperationCanceledException) when (capture.IsCancellationRequested) { }
}

async Task Listen(Target target)
{
    var phase = "unlabelled";
    long requestId = 0;
    long lastSeenRequestId = 0;
    try
    {
        await using var channel = await io.OpenAsync(target.Device, capture.Token);
        Log("ready", target.Name, phase, requestId);
        while (!capture.IsCancellationRequested)
        {
            var request = ReadRequest();
            if (request is { Id: > 0 } && request.Id > lastSeenRequestId)
            {
                lastSeenRequestId = request.Id;
                if (request.Target is "all" || target.Name.StartsWith(request.Target + "-", StringComparison.Ordinal))
                {
                    requestId = request.Id;
                    phase = request.Phase;
                    Log("mark", target.Name, phase, requestId, new { request.Action });
                    if (request.Action == "probe" && target.Name.EndsWith("-query", StringComparison.Ordinal))
                    {
                        foreach (var query in Queries(target.Name)) await Query(channel, query);
                        Log("probe-complete", target.Name, phase, requestId);
                    }
                }
            }
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(capture.Token);
            readDeadline.CancelAfter(TimeSpan.FromSeconds(1));
            try { Input(await channel.ReadInputReportAsync(readDeadline.Token), "passive", null); }
            catch (OperationCanceledException) when (!capture.IsCancellationRequested && readDeadline.IsCancellationRequested) { }
        }

        async Task Query(IHidChannel current, QueryFrame query)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(capture.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            var started = timer.ElapsedMilliseconds;
            try
            {
                if (target.Name.StartsWith("mchose", StringComparison.Ordinal))
                    await current.WriteInterruptReportAsync(query.Bytes, deadline.Token);
                else await current.WriteOutputReportAsync(query.Bytes, deadline.Token);
                Log("output", target.Name, phase, requestId, new { query.Name, hex = Convert.ToHexString(query.Bytes) });
                while (true)
                {
                    var report = await current.ReadInputReportAsync(deadline.Token);
                    var matched = target.Name.StartsWith("mchose", StringComparison.Ordinal)
                        ? report.Length == 65 && report[0] == 0 && report[1] == 0xAA && report[2] == query.Bytes[2]
                        : report.Length == 17 && report[0] == 8 && report[1] == query.Bytes[1];
                    Input(report, "reply-wait", query.Name);
                    if (matched)
                    {
                        Log("query-result", target.Name, phase, requestId, new { query.Name, outcome = "Reply", latencyMs = timer.ElapsedMilliseconds - started, hex = Convert.ToHexString(report) });
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (!capture.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                Log("query-result", target.Name, phase, requestId, new { query.Name, outcome = "Timeout", latencyMs = timer.ElapsedMilliseconds - started });
            }
        }

        void Input(byte[] report, string context, string? query)
        {
            Interlocked.Increment(ref inputCount);
            Log("input", target.Name, phase, requestId, new { context, query, hex = Convert.ToHexString(report) });
        }
    }
    catch (OperationCanceledException) when (capture.IsCancellationRequested) { }
    catch (Exception error)
    {
        Interlocked.Exchange(ref failed, 1);
        Log("error", target.Name, phase, requestId, new { error = error.ToString() });
        capture.Cancel();
    }
    finally { Log("closed", target.Name, phase, requestId); }
}

static QueryFrame[] Queries(string target)
{
    if (target == "lofree-query")
        return [Lofree(3, "online"), Lofree(4, "battery"), Lofree(0x12, "device-version"), Lofree(0x1D, "receiver-version"), new("model-identity", LofreeCompxProtocol.CreateIdentityQuery()), Lofree(3, "online-after")];
    return [new("online", MchoseG3Protocol.CreateOnlineQuery()), new("battery", MchoseG3Protocol.CreateBatteryQuery()), MchoseVersion(), MchoseConfig(), new("online-after", MchoseG3Protocol.CreateOnlineQuery())];
}

static QueryFrame Lofree(byte command, string name)
{
    var bytes = new byte[17];
    bytes[0] = 8;
    bytes[1] = command;
    bytes[5] = 0x80;
    bytes[16] = unchecked((byte)(0x55 - bytes.Take(16).Sum(value => value)));
    return new(name, bytes);
}

static QueryFrame MchoseVersion()
{
    var bytes = new byte[65];
    bytes[1] = 0x55;
    bytes[2] = 3;
    return new("version", bytes);
}

static QueryFrame MchoseConfig()
{
    // AA0E opcode echo was confirmed in the 2026-11-05 awake G3 A capture.
    // Keep unmatched frames and timeouts; matching an opcode does not prove freshness.
    var bytes = new byte[65];
    new byte[] { 0, 0x55, 0x0E, 0xA5, 0x0B, 0x30, 1, 1, 1, 0, 0 }.CopyTo(bytes, 0);
    return new("config-read", bytes);
}

static string Select(HidInterface device)
{
    if (LofreeCompxProtocol.Supports(device)) return "lofree-query";
    if (MchoseG3Protocol.Supports(device)) return "mchose-query";
    if (device.VendorId != 0xA8A5 || device.ProductId != 0x2255 || device.ProductName != "MCHOSE G3 A" ||
        device.Manufacturer != "YJX-CHIP" || device.Usage != 2 || device.InputReportLength != 64) return "";
    if (device.UsagePage == 0xFF05 && device.OutputReportLength == 0 && device.Path.StartsWith(@"\\?\hid#vid_a8a5&pid_2255&mi_01&col04#", StringComparison.OrdinalIgnoreCase)) return "mchose-ff05-passive";
    if (device.UsagePage == 0xFF06 && device.OutputReportLength == 64 && device.Path.StartsWith(@"\\?\hid#vid_a8a5&pid_2255&mi_01&col05#", StringComparison.OrdinalIgnoreCase)) return "mchose-ff06-passive";
    return "";
}

static string FindRoot()
{
    for (var current = new DirectoryInfo(Environment.CurrentDirectory); current is not null; current = current.Parent)
        if (File.Exists(Path.Combine(current.FullName, "BatteryManager.slnx"))) return current.FullName;
    throw new IOException("Run this diagnostic from the BatteryManager project.");
}

internal sealed record Request(long Id, string Action, string Target, string Phase);
internal sealed record Target(string Name, HidInterface Device);
internal sealed record QueryFrame(string Name, byte[] Bytes);
