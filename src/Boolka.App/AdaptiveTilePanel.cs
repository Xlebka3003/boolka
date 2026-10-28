using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Boolka;

/// <summary>Адаптивные плитки: создаются только видимые строки и небольшой запас вокруг них.</summary>
public sealed class AdaptiveTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(nameof(TileHeight), typeof(double), typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(432d, FrameworkPropertyMetadataOptions.AffectsMeasure), value => value is double height && double.IsFinite(height) && height >= 260 && height <= 1000);
    public double TileHeight { get => (double)GetValue(TileHeightProperty); set => SetValue(TileHeightProperty, value); }
    public static readonly DependencyProperty PreferredColumnsProperty = DependencyProperty.Register(nameof(PreferredColumns), typeof(int), typeof(AdaptiveTilePanel),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure), value => value is int columns && columns is >= 1 and <= 8);
    public int PreferredColumns { get => (int)GetValue(PreferredColumnsProperty); set => SetValue(PreferredColumnsProperty, value); }
    public static int CalculateCapacity(double width) => Math.Clamp((int)(Math.Max(1, width) / 180), 1, 8);
    public static int CalculateColumns(double width, int preferred) => Math.Min(Math.Clamp(preferred, 1, 8), CalculateCapacity(width));

    private Size viewport;
    private Size extent;
    private double offset;
    private int columns = 1;
    public int Columns => columns;
    public int RealizedItemCount => InternalChildren.Count;
    public AdaptiveTilePanel() => ClipToBounds = true;

    public static int CalculateColumns(double width) => CalculateColumns(width, 4);

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return new();
        _ = InternalChildren; // Создаёт генератор также для изначально пустого списка.
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : Math.Max(1, ScrollOwner?.ActualWidth ?? 720);
        var height = double.IsFinite(availableSize.Height) ? Math.Max(1, availableSize.Height) : Math.Max(1, ScrollOwner?.ActualHeight ?? 600);
        var count = owner.Items.Count;
        var nextColumns = CalculateColumns(width, PreferredColumns);
        if (nextColumns != columns)
        {
            var atEnd = offset > 0 && Math.Abs(offset + viewport.Height - extent.Height) < 1;
            var anchor = (int)Math.Floor(offset / TileHeight) * columns;
            var fraction = offset % TileHeight;
            offset = atEnd ? Math.Max(0, Math.Ceiling(count / (double)nextColumns) * TileHeight - height) : anchor / nextColumns * TileHeight + fraction;
            columns = nextColumns;
        }
        UpdateScrollInfo(new(width, height), new(width, Math.Ceiling(count / (double)columns) * TileHeight));
        if (count == 0) { ClearRealized(); return new(width, height); }
        var firstRow = Math.Max(0, (int)Math.Floor(offset / TileHeight) - 1);
        var lastRow = (int)Math.Floor((offset + height) / TileHeight) + 1;
        var first = firstRow * columns;
        var last = Math.Min(count - 1, (lastRow + 1) * columns - 1);
        _ = InternalChildren; // Инициализирует генератор контейнеров при первом измерении.
        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (var index = first; index <= last; index++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var created);
                if (created)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(width / columns, TileHeight));
            }
        }
        for (var index = InternalChildren.Count - 1; index >= 0; index--)
        {
            var position = new GeneratorPosition(index, 0);
            var item = generator.IndexFromGeneratorPosition(position);
            if (item >= first && item <= last) continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(index, 1);
        }
        return new(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Max(1, viewport.Width / columns);
        for (var child = 0; child < InternalChildren.Count; child++)
        {
            var item = ItemContainerGenerator.IndexFromGeneratorPosition(new(child, 0));
            if (item < 0) continue;
            InternalChildren[child].Arrange(new Rect(item % columns * width, item / columns * TileHeight - offset, width, TileHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        // При замене каталога или фильтра старые контейнеры не должны хранить прежние записи.
        ClearRealized();
        if (args.Action == NotifyCollectionChangedAction.Reset) offset = 0;
        InvalidateMeasure();
    }
    private void ClearRealized()
    {
        _ = InternalChildren;
        ItemContainerGenerator.RemoveAll();
        if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);
    }
    private void UpdateScrollInfo(Size nextViewport, Size nextExtent)
    {
        var changed = viewport != nextViewport || extent != nextExtent;
        viewport = nextViewport; extent = nextExtent;
        var next = Math.Clamp(offset, 0, Math.Max(0, extent.Height - viewport.Height));
        changed |= next != offset; offset = next;
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    protected override void BringIndexIntoView(int index)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null || index < 0 || index >= owner.Items.Count) return;
        var top = index / columns * TileHeight;
        if (top < offset) SetVerticalOffset(top);
        else if (top + TileHeight > offset + viewport.Height) SetVerticalOffset(Math.Max(top, top + TileHeight - viewport.Height));
    }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is null || rectangle.IsEmpty) return Rect.Empty;
        DependencyObject? child = visual;
        while (child is not null && VisualTreeHelper.GetParent(child) != this) child = VisualTreeHelper.GetParent(child);
        if (child is not UIElement element) return Rect.Empty;
        var index = InternalChildren.IndexOf(element);
        if (index < 0) return Rect.Empty;
        var item = ItemContainerGenerator.IndexFromGeneratorPosition(new(index, 0));
        if (item < 0) return Rect.Empty;
        BringIndexIntoView(item);
        return new(item % columns * viewport.Width / columns, item / columns * TileHeight - offset, viewport.Width / columns, TileHeight);
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentHeight => extent.Height;
    public double ExtentWidth => extent.Width;
    public double HorizontalOffset => 0;
    public double VerticalOffset => offset;
    public double ViewportHeight => viewport.Height;
    public double ViewportWidth => viewport.Width;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetVerticalOffset(double value)
    {
        if (double.IsNaN(value)) value = 0;
        value = Math.Clamp(value, 0, Math.Max(0, extent.Height - viewport.Height));
        if (value == offset) return;
        offset = value; ScrollOwner?.InvalidateScrollInfo(); InvalidateMeasure();
    }
    public void SetHorizontalOffset(double value) { }
    public void LineUp() => SetVerticalOffset(offset - 40);
    public void LineDown() => SetVerticalOffset(offset + 40);
    public void PageUp() => SetVerticalOffset(offset - viewport.Height);
    public void PageDown() => SetVerticalOffset(offset + viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(offset - 120);
    public void MouseWheelDown() => SetVerticalOffset(offset + 120);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
}
