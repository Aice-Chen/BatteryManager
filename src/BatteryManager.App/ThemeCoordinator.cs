using BatteryManager.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace BatteryManager.App;

internal sealed record AppThemeState(bool WindowsDark, bool AppsDark, string MainMode, bool AnimationsEnabled, bool Simulated = false,
    bool? AnimationsOverride = null);

// Shell and application personalization are intentionally read independently.
// Registry access is read-only; no system setting is changed by this application.
internal sealed class ThemeCoordinator : IDisposable
{
    private readonly UISettings _settings = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _changed;
    private bool _disposed;
    private int _queued;
    private bool _simulated;
    public bool WindowsDark { get; private set; }
    public bool AppsDark { get; private set; }
    public bool AnimationsEnabled { get; private set; }
    public Color Accent { get; private set; }
    public MainColorMode MainMode { get; set; }
    public ElementTheme MainTheme => (MainMode == MainColorMode.System ? AppsDark : MainMode == MainColorMode.Dark)
        ? ElementTheme.Dark : ElementTheme.Light;
    public ElementTheme TrayTheme => WindowsDark ? ElementTheme.Dark : ElementTheme.Light;

    public ThemeCoordinator(DispatcherQueue dispatcher, Action changed)
    {
        _dispatcher = dispatcher;
        _changed = changed;
        Read();
        _settings.ColorValuesChanged += ColorsChanged;
        _settings.AnimationsEnabledChanged += AnimationsChanged;
    }

    private void ColorsChanged(UISettings sender, object args) => QueueRefresh();
    private void AnimationsChanged(UISettings sender, object args) => QueueRefresh();

    public void QueueRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _queued, 1) != 0) return;
        if (!_dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (_disposed) return;
            Read();
            _changed();
        })) Interlocked.Exchange(ref _queued, 0);
    }

    private void Read()
    {
        if (_simulated) return;
        var foreground = _settings.GetColorValue(UIColorType.Foreground);
        var fallbackDark = foreground.R + foreground.G + foreground.B > 384;
        WindowsDark = ReadMode("SystemUsesLightTheme", fallbackDark);
        AppsDark = ReadMode("AppsUseLightTheme", fallbackDark);
        Accent = _settings.GetColorValue(UIColorType.Accent);
        AnimationsEnabled = _settings.AnimationsEnabled;
    }

    private static bool ReadMode(string name, bool fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(name) is int light ? light == 0 : fallback;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { return fallback; }
    }

    public void SetTestModes(string value)
    {
        if (value == "reset") { _simulated = false; Read(); }
        else
        {
            var parts = value.Split(',');
            if (parts.Length != 2 || parts.Any(p => p is not ("light" or "dark"))) throw new ArgumentException("Use windowsMode,appsMode (light or dark), or reset.");
            _simulated = true; WindowsDark = parts[0] == "dark"; AppsDark = parts[1] == "dark";
        }
        _changed();
    }
    public AppThemeState Capture() => new(WindowsDark, AppsDark, MainMode.ToString(), AnimationsEnabled, _simulated, Motion.TestEnabled);
    public void Dispose()
    {
        _disposed = true;
        _settings.ColorValuesChanged -= ColorsChanged;
        _settings.AnimationsEnabledChanged -= AnimationsChanged;
    }
}
