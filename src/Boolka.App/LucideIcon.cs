using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Boolka;

public sealed class LucideIcon : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(LucideIcon),
        new FrameworkPropertyMetadata("square", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(LucideIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public LucideIcon()
    {
        Width = Height = 20;
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var size = Math.Min(ActualWidth, ActualHeight);
        var pen = new Pen(Foreground, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        drawing.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        drawing.PushTransform(new ScaleTransform(size / 24, size / 24));
        drawing.DrawGeometry(null, pen, LucideGeometry.Get(Icon));
        drawing.Pop();
        drawing.Pop();
    }
}
