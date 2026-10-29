using System.Windows;

namespace Boolka;

public partial class MainWindow
{
    internal static double CalculateInterfaceScale(double width, double height) =>
        Math.Clamp(Math.Min(width / 1280.0, height / 840.0), 0.85, 1.15);

    private void UpdateSidebarCaptionWidth()
    {
        // Линия в заголовке совпадает с разделителем панели после масштабирования интерфейса.
        SidebarCaptionBackground.Width = SidebarColumn.Width.Value * InterfaceScale.ScaleX;
        SidebarCaptionBackground.BorderThickness = new Thickness(0, 0, InterfaceScale.ScaleX, 0);
    }

    private void UpdateInterfaceScale(object sender, SizeChangedEventArgs e)
    {
        // Ограничение сохраняет читаемость текста; доступное место по-прежнему перераспределяется разметкой.
        var scale = CalculateInterfaceScale(ActualWidth, ActualHeight);
        InterfaceScale.ScaleX = InterfaceScale.ScaleY = scale;
        UpdateSidebarCaptionWidth();
    }
}
