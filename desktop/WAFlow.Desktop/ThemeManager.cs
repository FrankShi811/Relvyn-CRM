using System.Windows;
using System.Windows.Media;
using System.Windows.Data;
using System.Globalization;
using Microsoft.Win32;

namespace WAFlow.Desktop;

internal static class ThemeManager
{
    private static readonly IReadOnlyDictionary<string, (string Light, string Dark)> Palette =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["Ink"] = ("#15251E", "#F4F8F6"),
            ["InkSecondary"] = ("#43564D", "#C4D1CB"),
            ["Muted"] = ("#586B64", "#98AAA2"),
            ["MutedSubtle"] = ("#96A8A1", "#6F7D94"),
            ["Primary"] = ("#087A59", "#19BD8C"),
            ["PrimaryDark"] = ("#066A4D", "#19BD8C"),
            ["PrimaryHover"] = ("#066A4D", "#38D5A3"),
            ["OnPrimary"] = ("#FFFFFF", "#07100D"),
            ["PrimarySoft"] = ("#D9F5EB", "#15352F"),
            ["PrimarySurface"] = ("#ECFAF5", "#102A27"),
            ["AiAccent"] = ("#6659B8", "#B9AEFF"),
            ["AiAccentDeep"] = ("#51459F", "#A79BFA"),
            ["OnAi"] = ("#FFFFFF", "#17112D"),
            ["AiProcessing"] = ("#31C8E5", "#69D7EF"),
            ["AiSoft"] = ("#E8E3FF", "#332D66"),
            ["AiSurface"] = ("#F4F1FF", "#211D45"),
            ["Surface"] = ("#FFFFFF", "#0E1714"),
            ["SurfaceElevated"] = ("#FFFFFF", "#121D19"),
            ["SurfaceMuted"] = ("#F4F7F5", "#111B17"),
            ["SurfaceAlt"] = ("#F4F7F5", "#111B17"),
            ["SurfaceInput"] = ("#FFFFFF", "#0A1410"),
            ["Canvas"] = ("#F4F7F5", "#07100D"),
            ["CanvasDeep"] = ("#E8EEEB", "#0B1612"),
            ["Line"] = ("#DDE5E1", "#26362F"),
            ["LineStrong"] = ("#B9C9C3", "#40544B"),
            ["Sidebar"] = ("#FFFFFF", "#07100D"),
            ["SidebarElevated"] = ("#F1F6F3", "#0E1915"),
            ["SidebarHover"] = ("#EAF4EF", "#10231C"),
            ["SidebarActive"] = ("#E1F1EA", "#073C2D"),
            ["SidebarText"] = ("#24362F", "#DCE8E3"),
            ["SidebarMuted"] = ("#586B64", "#91A39B"),
            ["LogoSurface"] = ("#EEF2F0", "#3A4440"),
            ["LogoBorder"] = ("#D6DEDA", "#59645F"),
            ["UnreadBadgeBackground"] = ("#C43131", "#C43131"),
            ["UnreadBadgeText"] = ("#FFFFFF", "#FFFFFF"),
            ["Success"] = ("#066A4D", "#43D6B2"),
            ["SuccessSoft"] = ("#E0F7EF", "#15352F"),
            ["Warning"] = ("#8A5A00", "#F0B94F"),
            ["WarningSoft"] = ("#FFF2D6", "#3D3018"),
            ["Danger"] = ("#A52D2D", "#F57D7D"),
            ["DangerSoft"] = ("#FDE7E7", "#402323"),
            ["OnDanger"] = ("#FFFFFF", "#2B0B0B"),
            ["Info"] = ("#4E8CF7", "#75A9FF"),
            ["InfoSoft"] = ("#E9F1FF", "#182D47"),
            ["GradeA"] = ("#16B889", "#3CD0A2"),
            ["GradeB"] = ("#4E8CF7", "#75A9FF"),
            ["GradeC"] = ("#E0A12B", "#F0B94F"),
            ["GradeD"] = ("#83958E", "#96A8A1"),
            ["ChatOutbound"] = ("#D1F5E8", "#0D3025"),
            ["ChatInbound"] = ("#FFFFFF", "#13211B"),
            ["Overlay"] = ("#B80A1813", "#E0030906"),
            ["GlassSurface"] = ("#EFFFFFFF", "#E6121D19"),
            ["GlassSurfaceStrong"] = ("#F8FFFFFF", "#F216241E"),
            ["GlassLine"] = ("#90D9E0DD", "#8A33483F")
        };

    private static readonly IReadOnlyDictionary<string, (string[] Light, string[] Dark)> GradientPalette =
        new Dictionary<string, (string[], string[])>(StringComparer.Ordinal)
        {
            ["AuroraAmbient"] = (
                ["#F8FFFFFF", "#EFF2EEFF", "#E8EAF9FF", "#E7E6FAF3"],
                ["#F2121D19", "#F015241E", "#ED172A22", "#EA11251C"]),
            ["AuroraBorder"] = (
                ["#55FFFFFF", "#807868FF", "#5031C8E5"],
                ["#73B9AEFF", "#6255C9A4", "#3F486258"])
        };

    public static string CurrentMode { get; private set; } = "System";
    public static bool IsDark { get; private set; }

    public static void Apply(string? mode)
    {
        CurrentMode = Normalize(mode);
        IsDark = CurrentMode == "Dark" || CurrentMode == "System" && SystemUsesDarkTheme();
        foreach (var (key, value) in Palette)
        {
            if (Application.Current.Resources[key] is not SolidColorBrush brush) continue;
            var color = (Color)ColorConverter.ConvertFromString(IsDark ? value.Dark : value.Light);
            if (brush.IsFrozen) Application.Current.Resources[key] = new SolidColorBrush(color);
            else brush.Color = color;
        }
        foreach (var (key, value) in GradientPalette)
        {
            if (Application.Current.Resources[key] is not LinearGradientBrush brush) continue;
            var colors = IsDark ? value.Dark : value.Light;
            if (brush.IsFrozen)
            {
                var clone = brush.Clone();
                ApplyGradientColors(clone, colors);
                Application.Current.Resources[key] = clone;
            }
            else
            {
                ApplyGradientColors(brush, colors);
            }
        }
    }

    public static string Next(string? mode) => Normalize(mode) switch
    {
        "System" => "Light",
        "Light" => "Dark",
        _ => "System"
    };

    public static string Label(string? mode) => Normalize(mode) switch
    {
        "Light" => "浅色",
        "Dark" => "深色",
        _ => "跟随系统"
    };

    public static string Glyph(string? mode) => Normalize(mode) switch
    {
        "Light" => "☀",
        "Dark" => "☾",
        _ => "◐"
    };

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "System"
    };

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return Convert.ToInt32(value) == 0;
        }
        catch { return false; }
    }

    private static void ApplyGradientColors(LinearGradientBrush brush, IReadOnlyList<string> colors)
    {
        for (var index = 0; index < Math.Min(brush.GradientStops.Count, colors.Count); index++)
            brush.GradientStops[index].Color = (Color)ColorConverter.ConvertFromString(colors[index]);
    }
}

public sealed class ComboSelectionTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is null || values[0] == DependencyProperty.UnsetValue) return "";
        var item = values[0];
        var path = values.Length > 1 ? values[1]?.ToString()?.Trim() : "";
        if (string.IsNullOrWhiteSpace(path)) return item.ToString() ?? "";
        object? current = item;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null) return "";
            current = current.GetType().GetProperty(part)?.GetValue(current);
        }
        return current?.ToString() ?? "";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
