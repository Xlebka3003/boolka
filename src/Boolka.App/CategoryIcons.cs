using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Boolka;

public static class CategoryIcons
{
    private static readonly HashSet<long> Available = [17, 18, 19, 20, 21, 22, 24, 25, 26, 27, 33, 35, 40, 45, 53, 55, 56, 57, 58, 88, 94, 95, 96, 97, 98, 109, 112, 114, 147, 149, 150, 151, 152, 155, 157, 159, 160, 162, 163, 164, 165, 166];
    private static readonly ConcurrentDictionary<long, BitmapSource> Images = new();

    public static void ApplyMode(string mode)
    {
        if (Application.Current is { } app) app.Resources["UseCategoryIcons"] = mode == "icons";
    }

    public static BitmapSource Get(long categoryId) => Images.GetOrAdd(Available.Contains(categoryId) ? categoryId : 27, id =>
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri($"pack://application:,,,/boolka;component/Assets/Categories/{id}.jpg");
        bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    });
}
