using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Boolka;

public partial class MainWindow
{
    private DispatcherTimer? pagePanTimer;
    private Point pagePanOrigin;
    private double pagePanVelocity;
    private bool pagePanActive;
    private Cursor? pagePanPreviousCursor;

    internal static double PagePanSpeed(double distance) => Math.Abs(distance) <= 8
        ? 0 : Math.Clamp((distance - Math.Sign(distance) * 8) * 0.12, -40, 40);

    private void PageMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (DetailPage.Visibility != Visibility.Visible || e.ChangedButton != MouseButton.Middle || busy || IsGalleryImage(e.OriginalSource as DependencyObject)
            || IsGestureControl(e.OriginalSource as DependencyObject) || GalleryOverlay.Visibility == Visibility.Visible)
            return;

        ResetGalleryGesture();
        e.Handled = true;
        if (!DetailScroll.CaptureMouse()) return;
        pagePanActive = true;
        pagePanOrigin = e.GetPosition(this);
        pagePanVelocity = 0;
        pagePanPreviousCursor = DetailScroll.Cursor;
        DetailScroll.Cursor = Cursors.ScrollNS;
        pagePanTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input,
            (_, _) => AdvancePagePan(), Dispatcher);
        pagePanTimer.Start();
    }

    private void PageMiddleMove(object sender, MouseEventArgs e)
    {
        if (!pagePanActive) return;
        e.Handled = true;
        pagePanVelocity = PagePanSpeed(e.GetPosition(this).Y - pagePanOrigin.Y);
    }

    private void AdvancePagePan()
    {
        if (!pagePanActive || Mouse.Captured != DetailScroll || Mouse.MiddleButton != MouseButtonState.Pressed)
        {
            ResetPagePan();
            return;
        }

        // Смещение от точки нажатия задаёт скорость, поэтому жест позволяет дойти до самого низа.
        DetailScroll.ScrollToVerticalOffset(DetailScroll.VerticalOffset + pagePanVelocity);
    }

    private void PageMiddleUp(object sender, MouseButtonEventArgs e)
    {
        if (!pagePanActive || e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        ResetPagePan();
    }

    private void PagePanLostCapture(object sender, MouseEventArgs e)
    {
        if (pagePanActive && Mouse.Captured != DetailScroll) ResetPagePan();
    }

    private void ResetPagePan()
    {
        pagePanTimer?.Stop();
        if (!pagePanActive) return;
        pagePanActive = false;
        pagePanVelocity = 0;
        DetailScroll.Cursor = pagePanPreviousCursor;
        if (Mouse.Captured == DetailScroll) DetailScroll.ReleaseMouseCapture();
    }
}
