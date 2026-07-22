using System.Windows;
using System.Windows.Media;
using ListaryOpen.Core.Settings;
using Microsoft.Win32;

namespace ListaryOpen.App;

internal static class ThemeManager
{
    private static bool _initialized;
    private static readonly IReadOnlyDictionary<AppTheme, IReadOnlyDictionary<string, string>> Palettes =
        new Dictionary<AppTheme, IReadOnlyDictionary<string, string>>
        {
            [AppTheme.Light] = CreatePalette(
                "#F7F8FA", "#FFFFFFFF", "#FFF1F3F5", "#FFD8DEE6",
                "#FF111827", "#FF4B5563", "#FF6B7280",
                "#FF2563EB", "#FFEFF6FF", "#FFE8F1FF",
                "#FF047857", "#FFECFDF5", "#FFB45309", "#FFFFFBEB", "#FFB91C1C", "#FFFEF2F2"),
            [AppTheme.Dark] = CreatePalette(
                "#FF111318", "#FF1B1F27", "#FF252B35", "#FF343C49",
                "#FFF3F4F6", "#FFD1D5DB", "#FF9CA3AF",
                "#FF60A5FA", "#FF172A46", "#FF24354F",
                "#FF6EE7B7", "#FF173C31", "#FFFBBF24", "#FF3D3218", "#FFFCA5A5", "#FF452727"),
            [AppTheme.Geek] = CreatePalette(
                "#FF07110C", "#FF0B1B12", "#FF10291A", "#FF1F5B38",
                "#FFD8FFE5", "#FFA7E8BC", "#FF70B989",
                "#FF39FF88", "#FF103B23", "#FF144D2A",
                "#FF39FF88", "#FF103B23", "#FFFFD166", "#FF3A3010", "#FFFF6B7A", "#FF40151B")
        };

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (Current == AppTheme.System && Application.Current is { } application)
            {
                application.Dispatcher.BeginInvoke(new Action(() => Apply(AppTheme.System)));
            }
        };
    }

    public static void Apply(AppTheme requestedTheme)
    {
        var effectiveTheme = requestedTheme == AppTheme.System
            ? GetSystemTheme()
            : requestedTheme;
        Current = requestedTheme;
        if (Application.Current is null || !Palettes.TryGetValue(effectiveTheme, out var palette))
        {
            return;
        }

        foreach (var (key, value) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            if (Application.Current.TryFindResource(key) is SolidColorBrush brush && !brush.IsFrozen)
            {
                brush.Color = color;
            }
            else
            {
                Application.Current.Resources[key] = new SolidColorBrush(color);
            }
        }
    }

    internal static AppTheme GetSystemTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int number && number == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch
        {
            return AppTheme.Light;
        }
    }

    private static IReadOnlyDictionary<string, string> CreatePalette(
        string background,
        string surface,
        string surfaceMuted,
        string border,
        string primary,
        string secondary,
        string muted,
        string accent,
        string accentSoft,
        string selection,
        string success,
        string successSoft,
        string warning,
        string warningSoft,
        string error,
        string errorSoft) => new Dictionary<string, string>
    {
        ["Brush.AppBackground"] = background,
        ["Brush.Surface"] = surface,
        ["Brush.SurfaceMuted"] = surfaceMuted,
        ["Brush.Border"] = border,
        ["Brush.TextPrimary"] = primary,
        ["Brush.TextSecondary"] = secondary,
        ["Brush.TextMuted"] = muted,
        ["Brush.Accent"] = accent,
        ["Brush.AccentSoft"] = accentSoft,
        ["Brush.Selection"] = selection,
        ["Brush.Success"] = success,
        ["Brush.SuccessSoft"] = successSoft,
        ["Brush.Warning"] = warning,
        ["Brush.WarningSoft"] = warningSoft,
        ["Brush.Error"] = error,
        ["Brush.ErrorSoft"] = errorSoft
    };
}
