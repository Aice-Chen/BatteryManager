using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BatteryManager.Cli.Tests;

public sealed class CliProcessTests
{
    [Fact]
    public async Task EmptyRawScanExitsSuccessfullyWithParseableJson()
    {
        await using var probe = RunningProbe.Start("empty");
        await probe.WaitForExitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(0, probe.Process.ExitCode);
        using var document = JsonDocument.Parse(await probe.Output);
        var result = document.RootElement;
        Assert.Equal("Completed", result.GetProperty("result").GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("stop").GetProperty("drained").GetBoolean());
        Assert.Empty(result.GetProperty("snapshot").GetProperty("devices").EnumerateArray());
    }

    [Fact]
    public async Task RawBatteryAndConnectionPassThroughProductionReaderStateAndCli()
    {
        await using var probe = RunningProbe.Start("success");
        await probe.WaitForExitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(0, probe.Process.ExitCode);
        using var document = JsonDocument.Parse(await probe.Output);
        var snapshot = document.RootElement.GetProperty("snapshot");
        var device = Assert.Single(snapshot.GetProperty("devices").EnumerateArray());
        Assert.Equal("Process Test Keyboard", device.GetProperty("name").GetString());
        Assert.Equal("Keyboard", device.GetProperty("kind").GetString());
        Assert.Equal("Online", device.GetProperty("connection").GetString());
        Assert.Equal(73, device.GetProperty("batteryPercent").GetInt32());
        Assert.Equal("Recent", device.GetProperty("freshness").GetString());
        Assert.True(device.GetProperty("visible").GetBoolean());
        var reading = Assert.Single(device.GetProperty("readings").EnumerateArray());
        Assert.Equal(73, reading.GetProperty("percent").GetInt32());
        Assert.Equal("SystemCache", reading.GetProperty("evidence").GetString());
        Assert.Equal(JsonValueKind.Null, reading.GetProperty("observedAt").ValueKind);
        Assert.Equal("windows.aep.classic", reading.GetProperty("source").GetString());
        Assert.DoesNotContain("123456789ABC", await probe.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuitExitsActualProcessWhileNativeOperationIgnoresCancellation()
    {
        await using var probe = RunningProbe.Start("hung");
        await probe.WaitForMarkerAsync("PROBE:PHYSICAL_IO_STARTED", TimeSpan.FromSeconds(12));
        var stopWatch = Stopwatch.StartNew();
        await probe.QuitAsync();
        // Three-second product budget plus one second of process scheduling tolerance.
        await probe.WaitForExitAsync(TimeSpan.FromSeconds(4));
        Assert.True(stopWatch.Elapsed < TimeSpan.FromSeconds(4), $"Process took {stopWatch.Elapsed} to exit.");
        Assert.Equal(2, probe.Process.ExitCode);
        var stopRecords = (await probe.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            var stop = Assert.Single(stopRecords, record => record.RootElement.TryGetProperty("stop", out _)).RootElement.GetProperty("stop");
            Assert.False(stop.GetProperty("drained").GetBoolean());
            Assert.True(stop.GetProperty("outstandingOperations").GetInt32() > 0);
        }
        finally { foreach (var record in stopRecords) record.Dispose(); }
    }

    [Fact]
    public async Task QuitExitsWithDeliveryFailureWhenStdoutPipeIsFullAndUnread()
    {
        await using var probe = RunningProbe.Start("blocked-output", readOutput: false);
        await probe.WaitForMarkerAsync("PROBE:LARGE_RAW_DEVICE_READY", TimeSpan.FromSeconds(12));
        await probe.WaitForFullOutputPipeAsync(TimeSpan.FromSeconds(12));
        var stopWatch = Stopwatch.StartNew();
        await probe.QuitAsync();
        await probe.WaitForExitAsync(TimeSpan.FromSeconds(4));
        Assert.True(stopWatch.Elapsed < TimeSpan.FromSeconds(4), $"Process took {stopWatch.Elapsed} to exit.");
        Assert.Equal(3, probe.Process.ExitCode);
    }

    [Fact]
    public async Task PendingRefreshCommandDoesNotPreventFollowingQuitFromBeingRead()
    {
        await using var probe = RunningProbe.Start("hung");
        await probe.WaitForMarkerAsync("PROBE:PHYSICAL_IO_STARTED", TimeSpan.FromSeconds(12));
        var stopWatch = Stopwatch.StartNew();
        // Both commands enter the same stdin stream in order. The first must not monopolize its reader.
        await probe.Process.StandardInput.WriteAsync("refresh\nquit\n");
        await probe.Process.StandardInput.FlushAsync();
        await probe.WaitForExitAsync(TimeSpan.FromSeconds(4));
        Assert.True(stopWatch.Elapsed < TimeSpan.FromSeconds(4), $"Process took {stopWatch.Elapsed} to exit.");
        Assert.Equal(2, probe.Process.ExitCode);
        var records = (await probe.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var final = records[^1].RootElement;
            Assert.True(final.TryGetProperty("stop", out var stop), "The final readable record must describe shutdown.");
            Assert.False(stop.GetProperty("drained").GetBoolean());
            Assert.True(stop.GetProperty("outstandingOperations").GetInt32() > 0);
        }
        finally { foreach (var record in records) record.Dispose(); }
    }

    [Fact]
    public async Task BrokenStdoutPipeCannotExitSuccessfullyAfterQuit()
    {
        await using var probe = RunningProbe.Start("blocked-output", readOutput: false);
        await probe.WaitForMarkerAsync("PROBE:LARGE_RAW_DEVICE_READY", TimeSpan.FromSeconds(12));
        await probe.WaitForFullOutputPipeAsync(TimeSpan.FromSeconds(12));
        // The OS pipe breaks here; the CLI must not let a Console wrapper hide that delivery failure.
        probe.Process.StandardOutput.Dispose();
        await probe.QuitAsync();
        await probe.WaitForExitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(3, probe.Process.ExitCode);
    }

    private sealed class RunningProbe : IAsyncDisposable
    {
        private readonly Task _errorReader;
        private readonly List<string> _errors = [];
        private readonly Dictionary<string, TaskCompletionSource> _markers = new(StringComparer.Ordinal);
        private readonly object _errorGate = new();
        public Process Process { get; }
        public Task<string> Output { get; }

        private RunningProbe(Process process, bool readOutput)
        {
            Process = process;
            Output = readOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
            _errorReader = ReadErrorsAsync();
        }

        public static RunningProbe Start(string scenario, bool readOutput = true)
        {
            var probeDll = Path.Combine(AppContext.BaseDirectory, "probe", "BatteryManager.Cli.Probe.dll");
            Assert.True(File.Exists(probeDll), $"Missing built process probe: {probeDll}");
            var dotnet = ResolveDotnetHost();
            var info = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(probeDll)!,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            info.ArgumentList.Add(probeDll);
            info.ArgumentList.Add(scenario);
            info.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet);
            info.Environment["DOTNET_NOLOGO"] = "1";
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            Assert.True(process.Start());
            return new(process, readOutput);
        }

        public async Task WaitForExitAsync(TimeSpan timeout)
        {
            try { await Process.WaitForExitAsync().WaitAsync(timeout); }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Process {Process.Id} did not exit within {timeout}. stderr: {ErrorText()}");
            }
            await _errorReader.WaitAsync(TimeSpan.FromSeconds(2));
        }

        public async Task WaitForMarkerAsync(string marker, TimeSpan timeout)
        {
            Task pending;
            lock (_errorGate)
            {
                if (_errors.Contains(marker)) return;
                if (!_markers.TryGetValue(marker, out var completion))
                    _markers.Add(marker, completion = new(TaskCreationOptions.RunContinuationsAsynchronously));
                pending = completion.Task;
            }
            try { await pending.WaitAsync(timeout); }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Did not receive {marker}. Exited={Process.HasExited}; stderr: {ErrorText()}");
            }
        }

        public async Task QuitAsync()
        {
            await Process.StandardInput.WriteLineAsync("quit");
            await Process.StandardInput.FlushAsync();
        }

        public async Task WaitForFullOutputPipeAsync(TimeSpan timeout)
        {
            SafeHandle handle = Process.StandardOutput.BaseStream switch
            {
                FileStream stream => stream.SafeFileHandle,
                PipeStream pipe => pipe.SafePipeHandle,
                var stream => throw new InvalidOperationException($"Unexpected redirected output stream: {stream.GetType()}")
            };
            if (!GetNamedPipeInfo(handle, out _, out uint outbound, out uint inbound, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            uint capacity = Math.Max(outbound, inbound);
            Assert.True(capacity > 0, "The OS must expose the redirected pipe capacity.");
            var elapsed = Stopwatch.StartNew();
            uint available = 0;
            while (elapsed.Elapsed < timeout)
            {
                if (!PeekNamedPipe(handle, 0, 0, out _, out available, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (available >= capacity) return;
                if (Process.HasExited) break;
                await Task.Delay(10);
            }
            throw new TimeoutException($"stdout never filled: {available}/{capacity} bytes; exited={Process.HasExited}; stderr: {ErrorText()}");
        }

        private async Task ReadErrorsAsync()
        {
            while (await Process.StandardError.ReadLineAsync() is { } line)
            {
                lock (_errorGate)
                {
                    _errors.Add(line);
                    if (_markers.TryGetValue(line, out var marker)) marker.TrySetResult();
                }
            }
        }

        private string ErrorText() { lock (_errorGate) return string.Join(" | ", _errors); }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await _errorReader.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { Process.Dispose(); }
        }

        private static string ResolveDotnetHost()
        {
            var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                var local = Path.Combine(folder.FullName, ".tools", "dotnet", "dotnet.exe");
                if (File.Exists(local)) return local;
            }
            var runtime = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
            var inferred = Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, "dotnet.exe");
            if (File.Exists(inferred)) return inferred;
            throw new FileNotFoundException("Cannot locate the .NET host used by the test runtime.");
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeInfo(SafeHandle pipe, out uint flags, out uint outputBufferSize, out uint inputBufferSize, out uint maximumInstances);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekNamedPipe(SafeHandle pipe, nint buffer, uint bufferSize, out uint bytesRead, out uint bytesAvailable, out uint bytesLeftThisMessage);
    }
}
