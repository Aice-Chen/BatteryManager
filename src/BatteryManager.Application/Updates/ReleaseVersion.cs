using System.Text.RegularExpressions;

namespace BatteryManager.Application.Updates;

// Release discovery currently offers stable versions only. Preview builds may upgrade to
// the stable version with the same three numeric components.
internal static partial class ReleaseVersion
{
    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex StablePattern();

    public static Version ParseStable(string value)
    {
        if (!StablePattern().IsMatch(value) || !Version.TryParse(value, out var version))
            throw new InvalidDataException("Invalid stable release version.");
        return version;
    }

    public static bool IsNewer(string candidate, string current)
    {
        var latest = ParseStable(candidate);
        var currentWithoutMetadata = current.Split('+', 2)[0];
        var parts = currentWithoutMetadata.Split('-', 2);
        var installed = ParseStable(parts[0]);
        return latest > installed || (latest == installed && parts.Length == 2 && parts[1].Length > 0);
    }
}
