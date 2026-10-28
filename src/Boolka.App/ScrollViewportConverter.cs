using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Boolka;

public sealed class ScrollViewportConverter : IMultiValueConverter
{
    public static double EffectiveViewport(double viewport, double minimum, double maximum, double length)
    {
        viewport = double.IsFinite(viewport) && viewport >= 0 ? viewport : 0;
        var range = maximum - minimum;
        if (!double.IsFinite(range) || range <= 0 || !double.IsFinite(length) || length <= 1) return viewport;
        // Размер задаётся через Track, поэтому расстояние перетаскивания остаётся согласованным с диапазоном.
        var thumb = Math.Min(48, length - 1);
        var required = range / (length / thumb - 1);
        return double.IsFinite(required) ? Math.Max(viewport, required) : viewport;
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 6 || values.Any(x => x == DependencyProperty.UnsetValue)) return 0d;
        return values[0] is double viewport && values[1] is double minimum && values[2] is double maximum
            && values[3] is Orientation orientation && values[4] is double height && values[5] is double width
            ? EffectiveViewport(viewport, minimum, maximum, orientation == Orientation.Vertical ? height : width) : 0d;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
