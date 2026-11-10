using System.Reflection;

namespace BatteryManager.App;

internal static class AppBuildInfo
{
    public static string InformationalVersion { get; } = typeof(AppBuildInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppBuildInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string Version { get; } = InformationalVersion.Split('+', 2)[0];
}
