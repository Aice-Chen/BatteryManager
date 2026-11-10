using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace BatteryManager.App;

// Called only by the application's scenario-only test command. These are the same
// coverage pixels used by the notification icon, without changing the live tray.
internal static class TrayGlyphDiagnostics
{
    internal static async Task ExportAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Use an absolute diagnostic directory.", nameof(directory));
        Directory.CreateDirectory(directory);
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        List<GlyphSample> samples = [];
        foreach (var size in new[] { 16, 20, 24, 28, 32 })
        foreach (var text in new[] { "8", "73", "100", "—" })
        {
            var coverage = TrayNumber.Draw(text, size, out var glyph);
            if (coverage.Length != size * size) throw new InvalidOperationException("The tray renderer returned an invalid image size.");
            var filename = $"{size}-{(text == "—" ? "unknown" : text)}.png";
            var sample = Measure(text, filename, size, glyph, coverage);
            var pixels = new byte[coverage.Length * 4];
            for (var index = 0; index < coverage.Length; index++)
            {
                // White with premultiplied alpha; the PNG keeps the actual antialias coverage.
                pixels[index * 4] = pixels[index * 4 + 1] = pixels[index * 4 + 2] = pixels[index * 4 + 3] = coverage[index];
            }
            var file = await folder.CreateFileAsync(filename, CreationCollisionOption.ReplaceExisting);
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)size, (uint)size, 96, 96, pixels);
                await encoder.FlushAsync();
            }
            samples.Add(sample);
        }
        var report = new
        {
            schemaVersion = 1,
            createdUtc = DateTimeOffset.UtcNow,
            renderer = "Production TrayNumber.Draw / " + TrayNumber.FontName,
            bundledFontLoaded = TrayNumber.BundledFontLoaded,
            measurement = "Requested icon pixel sizes; this does not switch or validate Windows display scaling or Shell presentation.",
            samples
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "metrics.json"), JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var fonts = new
        {
            schemaVersion = 1,
            createdUtc = DateTimeOffset.UtcNow,
            source = "EnumFontFamiliesExW / DEFAULT_CHARSET",
            selectedFace = TrayNumber.FontName,
            bundledFontLoaded = TrayNumber.BundledFontLoaded,
            families = EnumerateBahnschriftFamilies()
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "font-families.json"), JsonSerializer.Serialize(fonts,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static IReadOnlyList<FontFamily> EnumerateBahnschriftFamilies()
    {
        // The first pass finds family names; each matching name is then enumerated for all styles.
        // Only Bahnschrift names/styles leave this method. No font paths or unrelated inventory are retained.
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new HashSet<FontFamily>();
        nint screen = NativeMethods.GetDC(0);
        if (screen == 0) throw new InvalidOperationException("Could not acquire a screen context for font enumeration.");
        try
        {
            Enumerate("");
            foreach (var family in families.ToArray()) Enumerate(family);
        }
        finally { NativeMethods.ReleaseDC(0, screen); }
        return entries.OrderBy(entry => entry.Family, StringComparer.Ordinal)
            .ThenBy(entry => entry.Style, StringComparer.Ordinal).ToArray();

        void Enumerate(string family)
        {
            var filter = new LogFont { CharSet = 1, FaceName = family }; // DEFAULT_CHARSET; pitch/family and flags stay zero.
            Exception? callbackError = null;
            FontEnumProc callback = (font, _, _, _) =>
            {
                try
                {
                    var item = Marshal.PtrToStructure<EnumLogFontEx>(font);
                    var name = item.Font.FaceName ?? "";
                    if (name.StartsWith("Bahnschrift", StringComparison.OrdinalIgnoreCase))
                    {
                        families.Add(name);
                        entries.Add(new(name, item.Style ?? ""));
                    }
                    return 1;
                }
                catch (Exception error)
                {
                    // Managed exceptions must not unwind through the native callback.
                    callbackError = error;
                    return 0;
                }
            };
            EnumFontFamiliesEx(screen, ref filter, callback, 0, 0);
            GC.KeepAlive(callback);
            if (callbackError is not null)
                throw new InvalidOperationException("Could not read the enumerated font family.", callbackError);
        }
    }

    private static GlyphSample Measure(string text, string filename, int size, TrayGlyph glyph, byte[] coverage)
    {
        int left = size, top = size, right = -1, bottom = -1, nonZero = 0, partial = 0;
        byte minimum = byte.MaxValue, maximum = 0;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var alpha = coverage[y * size + x];
            minimum = Math.Min(minimum, alpha);
            maximum = Math.Max(maximum, alpha);
            if (alpha == 0) continue;
            nonZero++;
            if (alpha < byte.MaxValue) partial++;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        return new(text, filename, size, glyph.Width, glyph.Height,
            right < 0 ? null : new(left, top, right - left + 1, bottom - top + 1),
            minimum, maximum, nonZero, partial, Convert.ToHexStringLower(SHA256.HashData(coverage)));
    }

    private sealed record InkBounds(int Left, int Top, int Width, int Height);
    private sealed record GlyphSample(string Text, string File, int Size, int ReportedWidth, int ReportedHeight,
        InkBounds? Ink, byte AlphaMinimum, byte AlphaMaximum, int NonZeroPixels, int PartialAlphaPixels, string CoverageSha256);
    private sealed record FontFamily(string Family, string Style);

    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct LogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? FaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EnumLogFontEx
    {
        public LogFont Font;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? FullName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Style;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Script;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int FontEnumProc(nint font, nint metric, uint fontType, nint parameter);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumFontFamiliesExW")]
    private static extern int EnumFontFamiliesEx(nint dc, ref LogFont filter, FontEnumProc callback, nint parameter, uint flags);
}
