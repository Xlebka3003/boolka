using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Boolka;

public partial class MainWindow
{
    private UIElement? galleryGestureOwner;
    private Point galleryGestureStart;
    private bool galleryGestureChanged;

    private void AddonListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (busy || e.Delta == 0) return;
        var scroll = FindListScroll(AddonList);
        if (scroll is null) return;
        // Перехватываем колесо до кнопок и превью, сохраняя виртуализацию обоих видов.
        var lines = SystemParameters.WheelScrollLines;
        if (lines < 0)
        {
            if (e.Delta < 0) scroll.PageDown(); else scroll.PageUp();
        }
        else
        {
            var steps = (int)Math.Ceiling(Math.Abs(e.Delta) / 120.0 * lines);
            for (var i = 0; i < steps; i++)
                if (e.Delta < 0) scroll.LineDown(); else scroll.LineUp();
        }
        e.Handled = true;
    }

    private static ScrollViewer? FindListScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindListScroll(child) is { } nested) return nested;
        }
        return null;
    }

    private void WindowRightMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right || busy || DetailPage.Visibility != Visibility.Visible
            || IsGestureControl(e.OriginalSource as DependencyObject)) return;
        ResetGalleryGesture();
        e.Handled = true;
        if (GalleryOverlay.Visibility == Visibility.Visible) CloseGallery(sender, e);
        else BackToList(sender, e);
    }

    private async void DetailMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DetailPage.Visibility != Visibility.Visible) return;
        // Встроенная галерея не перехватывает прокрутку страницы; колесо листает только увеличенный просмотр.
        if (GalleryOverlay.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            if (e.Delta != 0 && galleryImages.Count > 1)
            {
                await MoveGalleryAsync(e.Delta < 0 ? 1 : -1);
            }
            return;
        }

        var lines = SystemParameters.WheelScrollLines;
        var distance = lines < 0 ? DetailScroll.ViewportHeight : Math.Max(0, lines) * 16.0;
        DetailScroll.ScrollToVerticalOffset(DetailScroll.VerticalOffset - e.Delta / 120.0 * distance);
        e.Handled = true;
    }

    private bool IsGalleryImage(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source == GalleryImageButton || source == ExpandedImage)
            {
                return true;
            }
            source = source is ContentElement content ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void GalleryMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not UIElement owner
            || DetailPage.Visibility != Visibility.Visible) return;
        // Средняя кнопка не должна запускать обычное нажатие кнопки изображения.
        e.Handled = true;
        ResetGalleryGesture();
        if (galleryImages.Count < 2) return;
        galleryGestureStart = e.GetPosition(this);
        galleryGestureChanged = false;
        galleryGestureOwner = owner;
        if (!owner.CaptureMouse()) ResetGalleryGesture();
    }

    private async void GalleryMiddleMove(object sender, MouseEventArgs e)
    {
        if (galleryGestureOwner is null) return;
        if (e.MiddleButton != MouseButtonState.Pressed || Mouse.Captured != galleryGestureOwner)
        { ResetGalleryGesture(); return; }
        var local = e.GetPosition(galleryGestureOwner);
        if (!new Rect(galleryGestureOwner.RenderSize).Contains(local))
        { ResetGalleryGesture(); return; }
        e.Handled = true;
        if (galleryImages.Count < 2) return;
        var distance = e.GetPosition(this).Y - galleryGestureStart.Y;
        var direction = GalleryDragDirection(distance, galleryGestureChanged);
        if (direction == 0) return;
        // Один переход за нажатие, независимо от длины и направления дальнейшего движения.
        galleryGestureChanged = true;
        galleryIndex = (galleryIndex + (direction > 0 ? 1 : galleryImages.Count - 1)) % galleryImages.Count;
        await ShowGalleryImageAsync();
    }

    private void GalleryMiddleUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        ResetGalleryGesture();
    }

    private void GalleryLostCapture(object sender, MouseEventArgs e)
    {
        if (galleryGestureOwner is not null && Mouse.Captured != galleryGestureOwner) ResetGalleryGesture();
    }

    private void GalleryMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is UIElement owner && owner == galleryGestureOwner && !owner.IsMouseOver) ResetGalleryGesture();
    }

    private void ResetGalleryGesture()
    {
        ResetPagePan();
        var owner = galleryGestureOwner;
        galleryGestureOwner = null;
        galleryGestureChanged = false;
        if (owner is not null && Mouse.Captured == owner) owner.ReleaseMouseCapture();
    }

    internal static int GalleryDragDirection(double distance, bool alreadyChanged) => alreadyChanged || !double.IsFinite(distance) || Math.Abs(distance) < 40 ? 0 : distance > 0 ? 1 : -1;

    private bool IsGestureControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase && source != GalleryImageButton
                || source is TextBoxBase or PasswordBox or ComboBox or ScrollBar or Thumb or System.Windows.Controls.ContextMenu
                || source is FrameworkElement { ContextMenu: not null }) return true;
            source = source is ContentElement content ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
