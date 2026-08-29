using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace MiniFences.Controls;

public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(94d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemCount = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        var width = double.IsInfinity(availableSize.Width) ? ItemWidth : Math.Max(ItemWidth, availableSize.Width);
        var height = double.IsInfinity(availableSize.Height) ? ItemHeight : Math.Max(0, availableSize.Height);
        var perRow = Math.Max(1, (int)Math.Floor(width / ItemWidth));
        var rowCount = (int)Math.Ceiling(itemCount / (double)perRow);
        UpdateScrollInfo(new Size(width, rowCount * ItemHeight), new Size(width, height));

        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / ItemHeight));
        var visibleRows = Math.Max(1, (int)Math.Ceiling(height / ItemHeight) + 1);
        var firstIndex = Math.Min(itemCount, firstRow * perRow);
        var lastIndex = Math.Min(itemCount - 1, (firstRow + visibleRows) * perRow - 1);
        CleanupItems(firstIndex, lastIndex);
        if (lastIndex < firstIndex) return availableSize;

        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var newlyRealized)!;
                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var perRow = Math.Max(1, (int)Math.Floor(Math.Max(ItemWidth, finalSize.Width) / ItemWidth));
        foreach (UIElement child in InternalChildren)
        {
            var index = ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator.IndexFromContainer(child) ?? -1;
            if (index < 0) continue;
            var row = index / perRow;
            var column = index % perRow;
            child.Arrange(new Rect(column * ItemWidth, row * ItemHeight - _offset.Y, ItemWidth, ItemHeight));
        }
        return finalSize;
    }

    protected override void BringIndexIntoView(int index)
    {
        var perRow = Math.Max(1, (int)Math.Floor(Math.Max(ItemWidth, _viewport.Width) / ItemWidth));
        var top = index / perRow * ItemHeight;
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (top + ItemHeight > _offset.Y + _viewport.Height)
            SetVerticalOffset(top + ItemHeight - _viewport.Height);
    }

    private void CleanupItems(int firstIndex, int lastIndex)
    {
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var generatorPosition = new GeneratorPosition(childIndex, 0);
            var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(generatorPosition);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex) continue;
            ItemContainerGenerator.Remove(generatorPosition, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        var changed = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;
        _offset.Y = Math.Clamp(_offset.Y, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineUp() => SetVerticalOffset(VerticalOffset - ItemHeight);
    public void LineDown() => SetVerticalOffset(VerticalOffset + ItemHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - ItemHeight * 3);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + ItemHeight * 3);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() { }
    public void PageRight() { }
    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset)
    {
        var value = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (Math.Abs(value - _offset.Y) < 0.1) return;
        _offset.Y = value;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is UIElement element)
        {
            var index = ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator.IndexFromContainer(element) ?? -1;
            if (index >= 0) BringIndexIntoView(index);
        }
        return rectangle;
    }
}
