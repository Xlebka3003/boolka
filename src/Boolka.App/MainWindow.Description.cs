using System.Globalization;
using System.Windows;

namespace Boolka;

public partial class MainWindow
{
    internal static string CompactDescription(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\n[ \t]*\n(?=[ \t]*[•●])", "\n", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    private void DecreaseDescription(object sender, RoutedEventArgs e) => ChangeDescriptionScale(-0.1);
    private void IncreaseDescription(object sender, RoutedEventArgs e) => ChangeDescriptionScale(0.1);
    private void ResetDescriptionScale(object sender, RoutedEventArgs e) => ChangeDescriptionScale(1 - preferences.DescriptionScale);

    private void ChangeDescriptionScale(double change)
    {
        preferences = preferences with { DescriptionScale = Math.Clamp(Math.Round(preferences.DescriptionScale + change, 2), 0.5, 1.5) };
        ApplyDescriptionScale();
        SavePreferences();
    }

    private void ApplyDescriptionScale()
    {
        foreach (var label in new[] { "Уменьшить страницу аддона", "Увеличить страницу аддона", "Сбросить масштаб страницы аддона" })
            UiResources.Register(label);
        // Имя сохранённого параметра оставлено для совместимости; теперь меняется вся страница.
        DetailPageScale.ScaleX = DetailPageScale.ScaleY = preferences.DescriptionScale;
        DescriptionScaleLabel.Content = (preferences.DescriptionScale * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        DescriptionSmaller.IsEnabled = preferences.DescriptionScale > 0.5;
        DescriptionLarger.IsEnabled = preferences.DescriptionScale < 1.5;
    }
}
