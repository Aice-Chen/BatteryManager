using System.Threading.Channels;
using BatteryManager.Core;
using BatteryManager.WindowsNative;

namespace BatteryManager.App;

internal sealed record AppScenarioState(string Name, bool Applying, DateTimeOffset SimulatedUtc,
    int OpenedHidChannels, int ClosedHidChannels, bool UsesRealHardware = false, bool SavesPreferences = false);

// All sample data originates at the hardware boundary. The shipped readers,
// session, state rules and controls remain responsible for the displayed result.
internal sealed class AppScenarios
{
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[]
        { "overview", "retained", "pending", "unknown", "error", "error-long", "expired", "offline", "recovered",
            "panel-one", "panel-two", "panel-three", "panel-many", "panel-battery-range", "receiver-pending", "connection-switch",
            "charging-10", "charging-60", "charging-100" });
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly ScenarioWindowsIo _windows;
    private readonly ScenarioHidIo _hid;
    private ScenarioInput _input = new(Absent: true);
    private string _name = "overview";
    private int _applying;
    public ScenarioClock Clock { get; } = new();
    public IWindowsHidIo HidIo => _hid;

    public AppScenarios()
    {
        _windows = new(() => Volatile.Read(ref _input));
        _hid = new(() => Volatile.Read(ref _input));
    }

    public IDeviceSource CreateSource(IWindowsHidIo? recordedHid = null) => new CompositeDeviceSource(
        new WindowsNativeSource(_windows, Clock), new AtkBatterySource(recordedHid ?? _hid, Clock),
        new MchoseBatterySource(recordedHid ?? _hid, Clock), new LofreeBatterySource(recordedHid ?? _hid, Clock));

    public static string? NameFromArguments(string[] arguments)
    {
        string? name = null;
        bool pipeSeen = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] == "--test-pipe")
            {
                if (pipeSeen || ++index >= arguments.Length || arguments[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Use --test-pipe once, followed by a suffix.");
                pipeSeen = true;
                continue;
            }
            if (arguments[index] != "--scenario")
                throw new ArgumentException("Unknown startup argument: " + arguments[index]);
            if (name is not null || ++index >= arguments.Length)
                throw new ArgumentException("Use --scenario once, followed by a scenario name.");
            name = arguments[index];
            Validate(name);
        }
        return name;
    }

    public static void Validate(string name)
    {
        if (!Names.Contains(name, StringComparer.Ordinal))
            throw new ArgumentException("Use a scenario: " + string.Join(", ", Names) + ".");
    }

    public AppScenarioState Capture() => new(_name, Volatile.Read(ref _applying) != 0, Clock.GetUtcNow(),
        _hid.Opened, _hid.Closed);

    internal void SetReadDelay(int milliseconds) =>
        Volatile.Write(ref _input, Volatile.Read(ref _input) with { ReadDelayMilliseconds = Math.Clamp(milliseconds, 0, 1000) });

    public async Task ApplyAsync(string name, BatterySession session, CancellationToken cancellationToken = default)
    {
        Validate(name);
        await _changes.WaitAsync(cancellationToken);
        Volatile.Write(ref _applying, 1);
        try
        {
            await WaitForPhysicalEndAsync(session, cancellationToken);
            // Remove the previous example through ordinary authoritative absence
            // and cache expiration, without accessing or resetting StateStore.
            Volatile.Write(ref _input, new(Absent: true));
            Clock.Advance(TimeSpan.FromMinutes(16));
            await ReadAsync(session, cancellationToken);
            int? panelCount = name switch
            {
                "panel-one" => 1,
                "panel-two" => 2,
                "panel-three" or "charging-10" or "charging-60" or "charging-100" => 3,
                "panel-many" or "panel-battery-range" => 5,
                _ => null
            };
            var batteryRange = name == "panel-battery-range";
            // The charging examples feed the existing receiver protocol and a noncharging Windows
            // reference at the same raw percentage; production readers and controls still derive the display.
            int? chargingPercent = name switch { "charging-10" => 10, "charging-60" => 60, "charging-100" => 100, _ => null };
            Volatile.Write(ref _input, new(Battery: name == "unknown" ? null : chargingPercent ?? (batteryRange ? 0 : 73),
                HidBattery: chargingPercent ?? (batteryRange ? 100 : 42), DeviceCount: name is "receiver-pending" or "connection-switch" ? 2 : panelCount ?? 3,
                ExtraKeyboardBattery: batteryRange ? 9 : 64, MchoseStatus: name == "receiver-pending" ? (byte)1 : null,
                LinkExample: name == "connection-switch"));
            Clock.Advance(TimeSpan.FromSeconds(1));
            await ReadAsync(session, cancellationToken);

            if (panelCount is null && name is not ("overview" or "unknown" or "receiver-pending" or "connection-switch"))
            {
                if (name == "recovered")
                {
                    Volatile.Write(ref _input, new(Battery: null, ReadError: true));
                    Clock.Advance(TimeSpan.FromSeconds(30));
                    await ReadAsync(session, cancellationToken);
                }
                var advance = name switch
                {
                    "pending" => TimeSpan.FromMinutes(2),
                    "expired" => TimeSpan.FromMinutes(16),
                    _ => TimeSpan.FromSeconds(30)
                };
                var next = name switch
                {
                    "pending" => new ScenarioInput(Connected: null, Battery: null),
                    "error" => new ScenarioInput(Battery: null, ReadError: true),
                    "error-long" => new ScenarioInput(Battery: null, ReadError: true, LongReadError: true),
                    "offline" => new ScenarioInput(Connected: false, HidOnline: false),
                    "recovered" => new ScenarioInput(Battery: 79, HidBattery: 47),
                    _ => new ScenarioInput(Battery: null)
                };
                Volatile.Write(ref _input, next);
                Clock.Advance(advance);
                await ReadAsync(session, cancellationToken);
            }
            _name = name;
        }
        finally { Volatile.Write(ref _applying, 0); _changes.Release(); }
    }

    // Opt-in scenario actions advance only fictional hardware and its clock. Each
    // step still runs the production readers, session, state store and controls.
    internal async Task StepReceiverAsync(string action, BatterySession session)
    {
        if (_name != "receiver-pending") throw new InvalidOperationException("Use the receiver-pending scenario first.");
        if (action is not ("wait" or "wake" or "wake-error" or "idle")) throw new ArgumentException("Use wait, wake, wake-error or idle.");
        await _changes.WaitAsync();
        Volatile.Write(ref _applying, 1);
        try
        {
            await WaitForPhysicalEndAsync(session, CancellationToken.None);
            var input = Volatile.Read(ref _input);
            Volatile.Write(ref _input, input with
            {
                MchoseStatus = action switch { "wake" or "wake-error" => 2, "idle" => 1, _ => input.MchoseStatus },
                MchoseBatteryError = action == "wake-error" || action == "wait" && input.MchoseBatteryError
            });
            Clock.Advance(TimeSpan.FromSeconds(30));
            await ReadAsync(session, CancellationToken.None);
        }
        finally { Volatile.Write(ref _applying, 0); _changes.Release(); }
    }

    internal async Task StepConnectionAsync(string action, BatterySession session)
    {
        if (_name != "connection-switch") throw new InvalidOperationException("Use the connection-switch scenario first.");
        if (action is not ("bluetooth" or "receiver" or "both" or "idle" or "wait" or "unknown" or "remove"))
            throw new ArgumentException("Use bluetooth, receiver, both, idle, wait, unknown or remove.");
        await _changes.WaitAsync();
        Volatile.Write(ref _applying, 1);
        try
        {
            await WaitForPhysicalEndAsync(session, CancellationToken.None);
            var input = Volatile.Read(ref _input);
            var next = action switch
            {
                "bluetooth" => input with { Absent = false, Connected = true, HidOnline = false, KeyboardBattery = 71 },
                "receiver" => input with { Absent = false, Connected = false, HidOnline = true, HidBattery = 43 },
                "both" => input with { Absent = false, Connected = true, HidOnline = true, KeyboardBattery = 72, HidBattery = 44 },
                "idle" => input with { Connected = null, HidOnline = false },
                "unknown" => input with { Connected = true, HidOnline = false, KeyboardBattery = null },
                "remove" => input with { Absent = true },
                _ => input
            };
            Volatile.Write(ref _input, next);
            Clock.Advance(TimeSpan.FromSeconds(30));
            await ReadAsync(session, CancellationToken.None);
        }
        finally { Volatile.Write(ref _applying, 0); _changes.Release(); }
    }

    private static async Task ReadAsync(BatterySession session, CancellationToken cancellationToken)
    {
        var result = await session.RefreshAsync(cancellationToken: cancellationToken);
        if (result.Outcome is not ("Completed" or "Partial" or "Failed"))
            throw new InvalidOperationException("Scenario refresh could not finish: " + result.Outcome);
        await WaitForPhysicalEndAsync(session, cancellationToken);
    }

    private static async Task WaitForPhysicalEndAsync(BatterySession session, CancellationToken cancellationToken)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (session.Snapshot is { OutstandingOperations: > 0 } or { IsRefreshing: true })
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("The previous scenario read has not physically finished.");
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed record ScenarioInput(bool Absent = false, bool? Connected = true,
        int? Battery = 73, bool ReadError = false, bool HidOnline = true, int HidBattery = 42, int DeviceCount = 3, int ReadDelayMilliseconds = 0,
        int ExtraKeyboardBattery = 64, bool LongReadError = false, byte? MchoseStatus = null,
        bool LinkExample = false, int? KeyboardBattery = 70, bool MchoseBatteryError = false);

    private sealed class ScenarioWindowsIo(Func<ScenarioInput> input) : IWindowsNativeIo
    {
        private const string Headphones = "020000000001";
        private const string Keyboard = "020000000002";
        private const string ExtraKeyboard = "020000000004";
        private const string ExtraHeadphones = "020000000005";
        private const string HistoricalKeyboard = "020000000008";
        public async Task<IReadOnlyList<RawWindowsDevice>> EnumerateAsync(WindowsQueryKind kind)
        {
            var data = input();
            if (data.ReadDelayMilliseconds > 0) await Task.Delay(data.ReadDelayMilliseconds);
            List<RawWindowsDevice> devices = [];
            if (!data.Absent && kind is WindowsQueryKind.BluetoothClassic or WindowsQueryKind.PnpDevice)
            {
                Add(Headphones, "演示耳机");
                if (data.DeviceCount >= 2) Add(Keyboard, data.LinkExample ? "演示键盘 · 蓝牙" : "演示键盘（未提供电量）");
                // The third device is supplied by the fictional HID receiver.
                if (data.DeviceCount >= 4) Add(ExtraKeyboard, "演示键盘 2");
                if (data.DeviceCount >= 5) Add(ExtraHeadphones, "演示耳机 2");
                if (data.LinkExample) devices.Add(kind == WindowsQueryKind.BluetoothClassic
                    ? Aep(HistoricalKeyboard, "演示历史键盘", false) : Pnp(HistoricalKeyboard, "演示历史键盘"));
            }
            return devices;

            void Add(string address, string name) => devices.Add(kind == WindowsQueryKind.BluetoothClassic
                ? Aep(address, name, data.LinkExample && address == Headphones ? true : data.Connected) : Pnp(address, name));
        }

        public Task<RawPnpProperty> ReadBatteryPropertyAsync(string instanceId)
        {
            var data = input();
            var main = instanceId.EndsWith(Headphones, StringComparison.Ordinal);
            if (main && data.LongReadError)
                return Task.FromException<RawPnpProperty>(new IOException(
                    "演示长错误：设备属性读取未完成。This is a fictional extended diagnostic message used to verify bounded wrapping, the copy action and the independent result column. " +
                    "读取通道与设备身份仍保留；这里的文字只来自演示硬件边界，用于检查较长错误不会遮挡右侧状态或复制按钮。"));
            int? battery = main ? data.Battery
                : data.LinkExample && instanceId.EndsWith(Keyboard, StringComparison.Ordinal) ? data.KeyboardBattery
                : instanceId.EndsWith(ExtraKeyboard, StringComparison.Ordinal) ? data.ExtraKeyboardBattery
                : instanceId.EndsWith(ExtraHeadphones, StringComparison.Ordinal) ? 91 : null;
            return Task.FromResult(main && data.ReadError ? new RawPnpProperty(0x17, 0, 0, [])
                : battery is { } percent ? new RawPnpProperty(0, 0, 3, [(byte)percent])
                : new RawPnpProperty(0, 0x25, 0, []));
        }

        private static RawWindowsDevice Aep(string address, string name, bool? connected) =>
            new("scenario-aep-" + address, name, new Dictionary<string, object?>
            {
                [WindowsPropertyNames.Address] = address,
                ["System.Devices.Aep.IsConnected"] = connected,
                ["System.Devices.Aep.IsPresent"] = true
            });
        private static RawWindowsDevice Pnp(string address, string name) =>
            new(@"BTHENUM\DEV_" + address, name, new Dictionary<string, object?>
            {
                [WindowsPropertyNames.InstanceId] = @"BTHENUM\DEV_" + address,
                ["System.Devices.Present"] = true
            });
    }

    private sealed class ScenarioHidIo(Func<ScenarioInput> input) : IWindowsHidIo
    {
        private int _opened;
        private int _closed;
        public int Opened => Volatile.Read(ref _opened);
        public int Closed => Volatile.Read(ref _closed);
        private static readonly HidInterface Receiver = new(
            @"\\?\hid#vid_373b&pid_11d9&mi_01&col05#scenario-only", 0x373B, 0x11D9,
            0xFF02, 2, 17, 17, 0, "演示鼠标（充电中）", "Scenario", "02000000-0000-0000-0000-000000000003");
        private static readonly HidInterface MchoseReceiver = new(
            @"\\?\hid#vid_a8a5&pid_2255&mi_02#scenario-only", 0xA8A5, 0x2255,
            0xFF01, 0x0010, 65, 65, 0, "MCHOSE G3 A", "YJX-CHIP", "02000000-0000-0000-0000-000000000006");
        private static readonly HidInterface LofreeReceiver = new(
            @"\\?\hid#vid_05ac&pid_024f&mi_01&col02#scenario-only", 0x05AC, 0x024F,
            0xFF02, 2, 17, 17, 0, "Scenario Lofree receiver", "CX", "02000000-0000-0000-0000-000000000007");

        public Task<HidInventory> EnumerateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = input();
            List<HidInterface> devices = [];
            if (!data.Absent)
            {
                if (data.DeviceCount >= 3) devices.Add(Receiver);
                if (data.MchoseStatus is not null) devices.Add(MchoseReceiver);
                if (data.LinkExample) devices.Add(LofreeReceiver);
            }
            return Task.FromResult(new HidInventory(devices, true, []));
        }

        public Task<IHidChannel> OpenAsync(HidInterface device, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (device != Receiver && device != MchoseReceiver && device != LofreeReceiver) throw new InvalidOperationException("Only fictional scenario receivers are available.");
            Interlocked.Increment(ref _opened);
            var data = input();
            Action closed = () => Interlocked.Increment(ref _closed);
            return Task.FromResult<IHidChannel>(device == MchoseReceiver ? new MchoseScenarioChannel(data, closed)
                : device == LofreeReceiver ? new LofreeScenarioChannel(data, closed) : new ScenarioChannel(data, closed));
        }
    }

    private sealed class LofreeScenarioChannel(ScenarioInput data, Action closed) : IHidChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private bool _disposed;
        public Task WriteOutputReportAsync(byte[] report, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (report.Length != 17 || report[0] != 8 || report[1] is not (1 or 3 or 4))
                throw new InvalidOperationException("Unexpected fictional keyboard query.");
            var expected = report[1] switch
            {
                1 => LofreeCompxProtocol.CreateIdentityQuery(report.AsSpan(6, 4)),
                3 => LofreeCompxProtocol.CreateOnlineQuery(),
                _ => LofreeCompxProtocol.CreateBatteryQuery()
            };
            if (!report.SequenceEqual(expected)) throw new InvalidOperationException("The fictional keyboard query is malformed.");
            var reply = new byte[17]; reply[0] = 8; reply[1] = report[1];
            reply[5] = report[1] switch { 1 => 0x87, 3 => 0x81, _ => 0x84 };
            if (report[1] == 1) { report.AsSpan(6, 4).CopyTo(reply.AsSpan(6, 4)); reply[10] = 1; reply[11] = 2; }
            else reply[6] = report[1] == 3 ? (byte)(data.HidOnline ? 1 : 0) : (byte)data.HidBattery;
            _replies.Writer.TryWrite(reply);
            return Task.CompletedTask;
        }
        public Task<byte[]> ReadInputReportAsync(CancellationToken cancellationToken) => _replies.Reader.ReadAsync(cancellationToken).AsTask();
        public ValueTask DisposeAsync()
        {
            if (!_disposed) { _disposed = true; _replies.Writer.TryComplete(); closed(); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MchoseScenarioChannel(ScenarioInput data, Action closed) : IHidChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private bool _disposed;
        public Task WriteOutputReportAsync(byte[] report, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fictional G3 A accepts only interrupt queries.");
        public Task WriteInterruptReportAsync(byte[] report, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!report.SequenceEqual(MchoseG3Protocol.CreateOnlineQuery()) && !report.SequenceEqual(MchoseG3Protocol.CreateBatteryQuery()))
                throw new InvalidOperationException("Unexpected command sent to the fictional G3 A.");
            if (report[2] != 0xED && data.MchoseBatteryError)
                throw new IOException("Fictional battery read failed after the receiver confirmed the mouse is active.");
            var reply = new byte[65];
            reply[1] = 0xAA;
            reply[2] = report[2];
            reply[9] = report[2] == 0xED ? data.MchoseStatus!.Value : (byte)8;
            _replies.Writer.TryWrite(reply);
            return Task.CompletedTask;
        }
        public Task<byte[]> ReadInputReportAsync(CancellationToken cancellationToken) => _replies.Reader.ReadAsync(cancellationToken).AsTask();
        public ValueTask DisposeAsync()
        {
            if (!_disposed) { _disposed = true; _replies.Writer.TryComplete(); closed(); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScenarioChannel(ScenarioInput data, Action closed) : IHidChannel
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private bool _disposed;
        public Task WriteOutputReportAsync(byte[] report, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (report.Length != 17 || report[0] != 0x08 || report[1] is not (3 or 4))
                throw new InvalidOperationException("Unexpected command sent to the fictional receiver.");
            var reply = new byte[17];
            reply[0] = 0x08;
            reply[1] = report[1];
            reply[6] = report[1] == 3 ? (byte)(data.HidOnline ? 1 : 0) : (byte)data.HidBattery;
            if (report[1] == 4) reply[7] = 1;
            _replies.Writer.TryWrite(reply);
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadInputReportAsync(CancellationToken cancellationToken) =>
            _replies.Reader.ReadAsync(cancellationToken).AsTask();

        public ValueTask DisposeAsync()
        {
            if (!_disposed) { _disposed = true; _replies.Writer.TryComplete(); closed(); }
            return ValueTask.CompletedTask;
        }
    }
}

// Frozen state time makes examples reproducible. Only ApplyAsync advances it;
// wall-clock timers still bound asynchronous requests, and Windows is untouched.
internal sealed class ScenarioClock : TimeProvider
{
    private long _ticks;
    private readonly DateTimeOffset _initial = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _initial.AddTicks(Interlocked.Read(ref _ticks));
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
