using System.Windows;
using System.Windows.Media;

namespace Boolka;

public static class ThemeManager
{
    public static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(ThemePalette.Map(color)));

    public static void Apply(string theme)
    {
        ThemePalette.IsDracula = theme == "dracula";
        ThemePalette.IsLight = theme is "light" or "classic" or "warm";
        ThemePalette.IsClassic = true;
        if (Application.Current is not { } app) return;
        foreach (var color in ThemeColors.Values)
            app.Resources["Theme." + color.TrimStart('#').ToUpperInvariant()] = Brush(color);
        app.Resources["Muted"] = Brush("#9AA3B5");
        app.Resources["StatusAttention"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ThemePalette.IsLight ? "#A81D6B" : "#FF79C6"));
        app.Resources["StatusAttentionText"] = ThemePalette.IsLight ? Brushes.White : new SolidColorBrush(Color.FromRgb(40, 42, 54));
        foreach (var radius in new[] { 4, 5, 7, 8, 10, 12 })
            app.Resources["Theme.Radius" + radius] = new CornerRadius(ThemePalette.IsClassic ? 0 : radius);
        app.Resources["Theme.ButtonOutline"] = ThemePalette.IsClassic ? Brush("#353C4E") : Brushes.Transparent;
        foreach (Window window in app.Windows) WindowTheme.Refresh(window);
    }
}
