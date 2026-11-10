using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatteryManager.Core;
using Microsoft.UI.Dispatching;

namespace BatteryManager.App;

internal sealed record AppTestCard(string Id, string Name, string Connection, string Battery, string Explanation,
    string? Tone = null, bool ReadFailed = false, string? Transport = null, string? Source = null, bool Expanded = false, string[]? DetailText = null,
    bool Charging = false, double HeaderHeight = 0, string? BatteryColor = null, bool BarVisible = true, double BarValue = 0,
    double RevealProgress = 0, bool IsAnimating = false, double ActualCardHeight = 0, AppTestBatteryLayout? BatteryLayout = null,
    string? BatteryGlyph = null, int GlyphSteps = 0, AppTestGlyphLayout? GlyphLayout = null,
    string? BatteryGlyphColor = null, string? PercentColor = null, IReadOnlyList<AppTestSourceLayout>? SourceLayouts = null,
    string? GlyphPixelLayout = null, AppTestGlyphRootBounds? GlyphRootBounds = null,
    bool Merged = false, string[]? Connections = null, string[]? Tags = null, string? MarkLayout = null,
    bool ValueDashVisible = false, string? ValueDashLayout = null, AppTestGlyphRootBounds? ValueDashRootBounds = null, string? ValueDashColor = null);
// Actual transform to the exported XamlRoot content, in DIPs; not a screen-coordinate claim.
internal sealed record AppTestGlyphRootBounds(double Left, double Top, double Width, double Height, double RasterizationScale);
// Value (number, "—" or "暂无响应") and battery-glyph bounds within a row, in DIPs.
internal sealed record AppTestGlyphLayout(double ValueLeft, double ValueRight, double ValueTop, double ValueBottom,
    double GlyphLeft, double GlyphRight, double GlyphTop, double GlyphBottom, double RowWidth, double RowHeight,
    double ValueDesiredWidth = 0, double ValueActualWidth = 0, double GlyphActualWidth = 0, double RasterizationScale = 1,
    double ValueSlotLeft = 0, double ValueSlotRight = 0, double GlyphSlotLeft = 0, double GlyphSlotRight = 0,
    bool LayoutSlotsAvailable = false);
// Bounds share the DeviceCard coordinate system with its header GlyphLayout.
internal sealed record AppTestSourceLayout(string Method, bool HasError, string? ErrorText,
    double RowWidth, double RowHeight, double LeftColumnLeft, double LeftColumnRight, double StatusLeft, double StatusRight,
    double ErrorLeft, double ErrorRight, double ErrorTop, double ErrorBottom, double ErrorDesiredWidth, double ErrorActualWidth,
    double CopyLeft, double CopyRight, bool ErrorInsideLeftColumn,
    double LeftColumnTop = 0, double LeftColumnBottom = 0,
    bool ErrorParentMatches = false, bool CopyParentMatches = false, bool ErrorLineParentMatches = false,
    double ErrorLineLeft = 0, double ErrorLineRight = 0, double ErrorLineTop = 0, double ErrorLineBottom = 0,
    double ErrorSlotLeft = 0, double ErrorSlotRight = 0, double ErrorSlotTop = 0, double ErrorSlotBottom = 0,
    double ErrorDesiredHeight = 0, double ErrorActualHeight = 0, bool ErrorSlotAvailable = false,
    double RowTop = 0, double RowBottom = 0, double CopyTop = 0, double CopyBottom = 0);
internal sealed record AppTestBatteryLayout(
    double BarLeft, double BarRight, double BarWidth,
    double ValueAvailableWidth, double ValueDesiredWidth, double ValueLeft, double ValueRight, bool ValueFits,
    double ValueSlotLeft, double ValueSlotRight,
    double NumberLeft, double NumberRight, double NumberWidth, double NumberDesiredWidth,
    double ChargingLeft, double ChargingRight, double ChargingWidth, double ChargingDesiredWidth,
    double RowWidth, double RowHeight, double ValueTop, double ValueBottom,
    double NumberTop, double NumberBottom, double ChargingTop, double ChargingBottom);
internal sealed record AppTestView(long Version, string Summary, bool RefreshEnabled, string RefreshText, IReadOnlyList<AppTestCard> Cards,
    string? ModeBanner = null, string? Page = null, string? MainTheme = null);
internal sealed record AppTestTray(bool Available, string? DeviceId, int? Percent, string Tooltip, string? Tone = null, bool WindowsDark = false, uint Color = 0,
    int IconSize = 0, int GlyphWidth = 0, int GlyphHeight = 0, string? GlyphFace = null, bool BundledFontLoaded = false);
internal sealed record AppTestPreferences(IReadOnlyList<TrayPreference> Priorities, string? Warning, TrayPreferencesDraft? Dialog);
internal sealed record AppTestStatus(DateTimeOffset At, SessionSnapshot Session, AppTestView View, string? DiagnosticLog = null,
    AppTestTray? Tray = null, AppTestPreferences? Preferences = null, AppTestPower? Power = null, AppTestClockState? Clock = null,
    AppScenarioState? Scenario = null, AppManagedMemory? Memory = null, string? AppVersion = null,
    AppTestPanel? Panel = null, AppThemeState? Theme = null, MainUiState? UiState = null, AppPreferences? Settings = null,
    DeviceMergeState? DeviceMerge = null);
internal sealed record AppTestResponse(bool Ok, AppTestStatus? Status = null, RefreshResult? Result = null, string? Error = null);
internal sealed record AppManagedMemory(long ManagedBytes, long HeapSizeBytes, long TotalCommittedBytes,
    long FragmentedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections)
{
    public static AppManagedMemory Capture()
    {
        var info = GC.GetGCMemoryInfo();
        return new(GC.GetTotalMemory(false), info.HeapSizeBytes, info.TotalCommittedBytes, info.FragmentedBytes,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }
}

// An opt-in, current-user-only bridge to the real application. There is one client
// and one in-flight command; the ordinary app does not create this host.
internal sealed class AppTestHost
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _name;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<AppTestStatus> _capture;
    private readonly Func<CancellationToken, Task<RefreshResult>> _refresh;
    private readonly Func<Task> _quit;
    private readonly Func<string, string?, Task> _preferences;
    private readonly Action<string> _power;
    private readonly Action<string> _clock;
    private readonly Func<string, CancellationToken, Task> _scenario;
    private readonly Func<string, string?, string?, Task> _ui;
    private readonly Action<Exception, string> _report;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Task _run;
    private NamedPipeServerStream? _pipe;
    private Task? _stop;
    private int _uiPending;
    private bool _quitRequested;

    public AppTestHost(string name, DispatcherQueue dispatcher, Func<AppTestStatus> capture,
        Func<CancellationToken, Task<RefreshResult>> refresh, Func<Task> quit, Action<Exception, string> report,
        Func<string, string?, Task> preferences, Action<string> power, Action<string> clock,
        Func<string, CancellationToken, Task> scenario, Func<string, string?, string?, Task> ui)
    {
        _name = name;
        _dispatcher = dispatcher;
        _capture = capture;
        _refresh = refresh;
        _quit = quit;
        _preferences = preferences;
        _power = power;
        _clock = clock;
        _scenario = scenario;
        _ui = ui;
        _report = report;
        // Creating the first server here reports a conflicting/invalid pipe at startup.
        var pipe = CreatePipe();
        _pipe = pipe;
        _run = Task.Run(() => RunAsync(pipe));
    }

    public static string? PipeName(string[] arguments)
    {
        string? name = null;
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] != "--test-pipe") continue;
            if (name is not null || ++index >= arguments.Length)
                throw new ArgumentException("Use --test-pipe once, followed by a suffix.");
            var suffix = arguments[index];
            if (suffix.Length is < 1 or > 64 || suffix.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new ArgumentException("The test-pipe suffix must contain 1–64 ASCII letters, digits or hyphens.");
            name = "BatteryManager.Test." + suffix;
        }
        return name;
    }

    private NamedPipeServerStream CreatePipe() => new(_name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);

    private async Task RunAsync(NamedPipeServerStream first)
    {
        var pipe = first;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                lock (_gate) _pipe = pipe;
                try
                {
                    await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                    await ServeAsync(pipe, _lifetime.Token).ConfigureAwait(false);
                }
                // A timed-out or disconnected client cannot keep the only server slot.
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                finally
                {
                    lock (_gate) if (ReferenceEquals(_pipe, pipe)) _pipe = null;
                    pipe.Dispose();
                }
                if (_lifetime.IsCancellationRequested || _quitRequested) break;
                pipe = CreatePipe();
            }
        }
        catch (Exception error) { _report(error, "App test pipe failed"); }
        finally
        {
            lock (_gate) if (ReferenceEquals(_pipe, pipe)) _pipe = null;
            pipe.Dispose();
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            string command;
            string? action = null;
            string? deviceId = null;
            string? uiValue = null;
            try
            {
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await ReadLineAsync(pipe, readDeadline.Token).ConfigureAwait(false);
                if (line is null) return;
                using var request = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
                if (request.RootElement.ValueKind != JsonValueKind.Object ||
                    !request.RootElement.TryGetProperty("command", out var value) || value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Expected a JSON object with a string command.");
                command = value.GetString()!;
                if (command is "preferences" or "power" or "clock" or "scenario" or "ui")
                {
                    if (!request.RootElement.TryGetProperty("action", out var actionValue) || actionValue.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("This command requires a string action.");
                    action = actionValue.GetString();
                    if (request.RootElement.TryGetProperty("value", out var extra))
                    {
                        if (extra.ValueKind != JsonValueKind.String) throw new InvalidDataException("value must be a string.");
                        uiValue = extra.GetString();
                    }
                    if (request.RootElement.TryGetProperty("deviceId", out var idValue))
                    {
                        if (idValue.ValueKind != JsonValueKind.String) throw new InvalidDataException("deviceId must be a string.");
                        deviceId = idValue.GetString();
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await WriteAsync(pipe, new(false, Error: "Timed out waiting for a complete request line (5 seconds)."), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                await WriteAsync(pipe, new(false, Error: error.Message), cancellationToken).ConfigureAwait(false);
                return;
            }

            AppTestResponse response;
            try
            {
                response = command switch
                {
                    "status" => await OnUiAsync(_ => Task.FromResult(new AppTestResponse(true, _capture())), cancellationToken, TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                    "refresh" => await OnUiAsync(async token =>
                    {
                        var result = await _refresh(token);
                        token.ThrowIfCancellationRequested();
                        return new AppTestResponse(true, _capture(), result);
                    }, cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false),
                    "quit" => new(true),
                    "preferences" => await OnUiAsync(async _ =>
                    {
                        await _preferences(action!, deviceId);
                        return new AppTestResponse(true, _capture());
                    }, cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false),
                    "power" => await OnUiAsync(_ =>
                    {
                        _power(action!);
                        return Task.FromResult(new AppTestResponse(true, _capture()));
                    }, cancellationToken, TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                    "clock" => await OnUiAsync(_ =>
                    {
                        _clock(action!);
                        return Task.FromResult(new AppTestResponse(true, _capture()));
                    }, cancellationToken, TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                    "scenario" => await OnUiAsync(async token =>
                    {
                        await _scenario(action!, token);
                        return new AppTestResponse(true, _capture());
                    }, cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false),
                    "ui" => await OnUiAsync(async _ =>
                    {
                        await _ui(action!, uiValue, deviceId);
                        return new AppTestResponse(true, _capture());
                    }, cancellationToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false),
                    _ => new(false, Error: "Unknown command. Use status, refresh, preferences, power, clock, scenario or quit.")
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception error) { response = new(false, Error: Describe(error)); }
            await WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
            if (command == "quit" && response.Ok)
            {
                _quitRequested = true;
                // QuitAsync stops this host. It must run after the response and must
                // not be awaited by the server task that StopAsync will drain.
                if (!_dispatcher.TryEnqueue(async () =>
                {
                    try { await _quit(); }
                    catch (Exception error) { _report(error, "App test quit failed"); }
                })) _report(new InvalidOperationException("The UI dispatcher rejected the quit request."), "App test quit dispatch failed");
                return;
            }
        }
    }

    private async Task<T> OnUiAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken, TimeSpan timeout)
    {
        if (Interlocked.CompareExchange(ref _uiPending, 1, 0) != 0)
            throw new InvalidOperationException("A previous UI test command has not finished.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var commandToken = deadline.Token;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = commandToken.Register(() => completion.TrySetCanceled(commandToken));
        var queued = false;
        try
        {
            commandToken.ThrowIfCancellationRequested();
            queued = _dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    if (completion.Task.IsCompleted) return;
                    commandToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(await action(commandToken));
                }
                catch (OperationCanceledException) when (commandToken.IsCancellationRequested) { completion.TrySetCanceled(commandToken); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { Interlocked.Exchange(ref _uiPending, 0); }
            });
            if (!queued) throw new InvalidOperationException("The UI dispatcher rejected the test command.");
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && commandToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The UI test command did not finish within {timeout.TotalSeconds:0} seconds.");
        }
        finally
        {
            // A timed-out queued callback retains its slot until it actually runs.
            // Repeated requests cannot grow an unbounded dispatcher backlog.
            if (!queued) Interlocked.Exchange(ref _uiPending, 0);
        }
    }

    private static async Task<byte[]?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new byte[4096];
        var one = new byte[1];
        var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (count == 0) return null;
                throw new InvalidDataException("The client disconnected before the request line ended.");
            }
            if (one[0] == '\n') return line[..(count > 0 && line[count - 1] == '\r' ? count - 1 : count)];
            if (count == line.Length) throw new InvalidDataException("A request line may not exceed 4096 bytes.");
            line[count++] = one[0];
        }
    }

    private static async Task WriteAsync(Stream pipe, AppTestResponse response, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, Json) + "\n");
        await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
        await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
    }

    public Task StopAsync()
    {
        lock (_gate) return _stop ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        var cancellation = _lifetime.CancelAsync();
        lock (_gate) _pipe?.Dispose();
        await cancellation.ConfigureAwait(false);
        await _run.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message} (0x{error.HResult:X8})";
}
