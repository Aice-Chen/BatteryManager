using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using BatteryManager.Core;
using BatteryManager.WindowsNative;

namespace BatteryManager.Cli;

public static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Task<int> Main(string[] args)
    {
        var clock = TimeProvider.System;
        return RunAsync(args, DeviceSources.CreateDefault(clock: clock), clock);
    }

    public static async Task<int> RunAsync(string[] args, IDeviceSource source, TimeProvider? clock = null)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("BatteryManager scan|watch [--json] [--include-hidden]\nwatch: type refresh or quit. Ctrl+C stops the session.");
            return 0;
        }
        if (args[0] is not ("scan" or "watch") || args.Skip(1).Any(x => x is not ("--json" or "--include-hidden")))
        {
            Console.Error.WriteLine("Unsupported command or scope. Use --help.");
            return 3;
        }
        bool hidden = args.Contains("--include-hidden");
        using var canceled = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = canceled.CancelAsync(); };
        await using var session = new BatterySession(source, clock);
        if (args[0] == "scan")
        {
            var result = await session.RefreshAsync(cancellationToken: canceled.Token);
            var stop = await session.StopAsync();
            var view = Project(session.Snapshot, hidden, source.Name);
            bool delivered = await WriteBoundedAsync(new { result, stop, snapshot = view }, TimeSpan.FromSeconds(1));
            if (!delivered) return 3;
            if (canceled.IsCancellationRequested) return 130;
            return result.Outcome switch { "Completed" when stop.Drained => 0, "Partial" or "TimedOut" or "Busy" => 2, _ => 3 };
        }

        session.StartPolling();
        var output = Channel.CreateBounded<object>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        var outputGate = new object();
        bool outputOpen = true;
        int outputFailed = 0;
        void Offer(object value)
        {
            lock (outputGate) if (outputOpen) output.Writer.TryWrite(value);
        }
        // One fixed writer, even when stdout blocks. No per-record tasks are accumulated.
        var writer = Task.Run(async () =>
        {
            try
            {
                using var stdout = OpenReliableOutput();
                await foreach (var item in output.Reader.ReadAllAsync())
                    stdout.WriteLine(JsonSerializer.Serialize(item, Json));
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                Interlocked.Exchange(ref outputFailed, 1);
                await canceled.CancelAsync();
            }
        });
        var snapshots = Task.Run(async () =>
        {
            try
            {
                await foreach (var view in session.Subscribe(canceled.Token)) Offer(Project(view, hidden, source.Name));
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        });
        _ = session.RefreshAsync(cancellationToken: canceled.Token);
        var input = Task.Run(async () =>
        {
            Task? pendingRefresh = null;
            while (!canceled.IsCancellationRequested)
            {
                var command = await Console.In.ReadLineAsync(canceled.Token);
                if (command is null) { await Task.Delay(Timeout.Infinite, canceled.Token); break; }
                if (command.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase)) { await canceled.CancelAsync(); break; }
                if (command.Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase))
                {
                    if (pendingRefresh is { IsCompleted: false }) Offer(new { request = new { outcome = "Busy" } });
                    else pendingRefresh = ObserveRefreshAsync();
                }
            }
            async Task ObserveRefreshAsync()
            {
                var result = await session.RefreshAsync(cancellationToken: canceled.Token);
                Offer(new { request = result });
            }
        });
        try { await Task.Delay(Timeout.Infinite, canceled.Token); } catch (OperationCanceledException) { }
        var stopped = await session.StopAsync();
        lock (outputGate)
        {
            outputOpen = false;
            output.Writer.TryWrite(new { stop = stopped });
            output.Writer.TryComplete();
        }
        try { await writer.WaitAsync(TimeSpan.FromMilliseconds(750)); } catch (TimeoutException) { return 3; }
        // ReadLine on a redirected stream may still be blocked; process termination owns cleanup.
        _ = input.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _ = snapshots.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return Volatile.Read(ref outputFailed) != 0 ? 3 : stopped.Drained ? 0 : 2;
    }

    private static object Project(SessionSnapshot snapshot, bool hidden, string scope)
    {
        // IDs and endpoint paths are local implementation details; exports use stable aliases.
        var all = snapshot.State.Devices.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        return new
        {
            version = snapshot.State.Version,
            decidedAt = snapshot.State.DecidedAt,
            snapshot.IsRefreshing,
            snapshot.Status,
            snapshot.OutstandingOperations,
            requestedScope = "all",
            actualScope = scope,
            devices = all.Select(device => new { device, alias = "device-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.Id)))[..12].ToLowerInvariant() }).Where(x => hidden || x.device.Visible).Select(x => new
            {
                id = x.alias, x.device.Name, x.device.Kind, x.device.Connection, x.device.Visible,
                x.device.BatteryPercent, x.device.Freshness, x.device.BatterySource, x.device.Reason,
                lastError = Redact(x.device.LastError, all),
                readings = x.device.Readings.Select(r => new
                {
                    r.Source, r.Percent, r.Freshness, r.Evidence, r.ObservedAt, r.ReceivedAt, r.ExpiresAt,
                    r.Attempt, r.AttemptedAt, error = Redact(r.Error, all), r.Property, r.Precision, r.Component, r.Charging
                })
            })
        };
    }

    private static string? Redact(string? message, IReadOnlyList<DeviceState> devices)
    {
        if (message is null) return null;
        foreach (var d in devices)
        {
            message = message.Replace(d.Id, "[device]", StringComparison.OrdinalIgnoreCase);
            foreach (var reading in d.Readings) message = message.Replace(reading.EndpointId, "[endpoint]", StringComparison.OrdinalIgnoreCase);
        }
        return message;
    }

    private static async Task<bool> WriteBoundedAsync(object value, TimeSpan timeout)
    {
        var write = Task.Run(() =>
        {
            using var stdout = OpenReliableOutput();
            stdout.WriteLine(JsonSerializer.Serialize(value, Json));
        });
        try { await write.WaitAsync(timeout); return true; }
        catch (Exception error) when (error is TimeoutException or IOException)
        {
            _ = write.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return false;
        }
    }

    private static StreamWriter OpenReliableOutput()
    {
        // Console's WindowsConsoleStream intentionally ignores broken-pipe errors.
        // A non-owning FileStream preserves failure so a lost report cannot exit 0.
        var handle = new SafeFileHandle(GetStdHandle(-11), ownsHandle: false);
        var stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
        return new StreamWriter(stream, new UTF8Encoding(false), 4096) { AutoFlush = true };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);
}
