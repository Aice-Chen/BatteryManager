using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatteryManager.WindowsNative;

namespace BatteryManager.App;

// Opt-in diagnostics on the application's existing channels. No extra HID opens,
// queries, buffering or polling. Read completion timestamps describe when software
// received a completed read, not when Windows queued the report or the device measured it.
internal sealed class AppDiagnosticLog : IWindowsHidIo, IDisposable
{
    private const long MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly IWindowsHidIo _inner;
    private readonly Stream _output;
    private readonly object _gate = new();
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private long _bytes;
    private long _channelId;
    private long _operationId;
    private long _sequence;
    private string? _lastView;
    private bool _disabled;
    private bool _disposed;
    public string FilePath { get; }

    public AppDiagnosticLog(IWindowsHidIo inner, string path)
        : this(inner, new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), path) { }

    internal AppDiagnosticLog(IWindowsHidIo inner, Stream output, string path = "test")
    {
        _inner = inner;
        _output = output;
        FilePath = path;
        Record("start", new { processId = Environment.ProcessId, maximumBytes = MaximumBytes });
    }

    public Task<HidInventory> EnumerateAsync(CancellationToken cancellationToken) => _inner.EnumerateAsync(cancellationToken);

    public void RecordView(object details, long stateVersion, long viewVersion)
    {
        lock (_gate)
        {
            if (_disabled || _disposed) return;
            try
            {
                // The state clock advances even when the displayed information is unchanged.
                // Keep changes and their versions without repeatedly copying every hidden reading.
                var signature = JsonSerializer.Serialize(details, Json);
                if (signature == _lastView) return;
                _lastView = signature;
                Record("view-applied", new { stateVersion, viewVersion, details });
            }
            catch (Exception) { _disabled = true; }
        }
    }

    public async Task<IHidChannel> OpenAsync(HidInterface device, CancellationToken cancellationToken)
    {
        var channel = await _inner.OpenAsync(device, cancellationToken).ConfigureAwait(false);
        // Capture only the supported vendor protocols, never ordinary input collections.
        if (!MchoseG3Protocol.Supports(device) && !LofreeCompxProtocol.Supports(device) && !AtkCompxProtocol.Supports(device)
            && !LogitechK380Protocol.Supports(device))
            return channel;
        var id = Interlocked.Increment(ref _channelId);
        Record("hid-open", new { channelId = id, device.ProductName, device.VendorId, device.ProductId,
            device.UsagePage, device.Usage, device.ContainerId });
        return new TracedChannel(this, channel, id);
    }

    public void Record(string kind, object data)
    {
        lock (_gate)
        {
            if (_disabled || _disposed) return;
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    sequence = ++_sequence, at = DateTimeOffset.UtcNow,
                    elapsedMs = _timer.ElapsedMilliseconds, kind, data
                }, Json);
                if (_bytes + bytes.Length + 1 > MaximumBytes)
                {
                    var limit = System.Text.Encoding.UTF8.GetBytes("{\"kind\":\"capture-limit\",\"data\":{\"complete\":false}}\n");
                    _output.Write(limit);
                    _output.Flush();
                    _disabled = true;
                    return;
                }
                _output.Write(bytes);
                _output.WriteByte((byte)'\n');
                _output.Flush();
                _bytes += bytes.Length + 1;
            }
            catch (Exception)
            {
                // A full/unavailable diagnostic disk must not alter device reads or shutdown.
                _disabled = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Record("closed", new { });
            _disposed = true;
            try { _output.Dispose(); } catch (Exception) { }
        }
    }

    private sealed class TracedChannel(AppDiagnosticLog owner, IHidChannel inner, long id) : IHidChannel
    {
        public Task WriteOutputReportAsync(byte[] report, CancellationToken token) => Write(report, token, false);
        public Task WriteInterruptReportAsync(byte[] report, CancellationToken token) => Write(report, token, true);

        private async Task Write(byte[] report, CancellationToken token, bool interrupt)
        {
            var operationId = Interlocked.Increment(ref owner._operationId);
            owner.Record("hid-write-start", new { channelId = id, operationId, interrupt, hex = Convert.ToHexString(report) });
            try
            {
                if (interrupt) await inner.WriteInterruptReportAsync(report, token).ConfigureAwait(false);
                else await inner.WriteOutputReportAsync(report, token).ConfigureAwait(false);
                owner.Record("hid-write-complete", new { channelId = id, operationId });
            }
            catch (Exception error)
            {
                owner.Record("hid-write-error", new { channelId = id, operationId, error = Describe(error) });
                throw;
            }
        }

        public async Task<byte[]> ReadInputReportAsync(CancellationToken token)
        {
            var operationId = Interlocked.Increment(ref owner._operationId);
            owner.Record("hid-read-start", new { channelId = id, operationId });
            try
            {
                var report = await inner.ReadInputReportAsync(token).ConfigureAwait(false);
                owner.Record("hid-read-complete", new { channelId = id, operationId, hex = Convert.ToHexString(report) });
                return report;
            }
            catch (Exception error)
            {
                owner.Record("hid-read-error", new { channelId = id, operationId, error = Describe(error) });
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Completion comes from the real channel; logging cannot release it early.
            await inner.DisposeAsync().ConfigureAwait(false);
            owner.Record("hid-closed", new { channelId = id });
        }

        private static string Describe(Exception error)
        {
            try { return error.ToString(); }
            catch (Exception) { return error.GetType().FullName ?? nameof(Exception); }
        }
    }
}
