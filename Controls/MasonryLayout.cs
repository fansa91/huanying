using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;

namespace Huanying.Controls;

/// <summary>
/// 瀑布流布局：把卡片依次放进当前最矮的一列，列宽随可用宽度自适应，
/// 卡片高度可以各不相同（海报比例不同），因此排出来是参差的「瀑布」。
///
/// 与 <see cref="ItemsRepeater"/> 配套使用（非虚拟化布局，配合分页加载控制数量）。
/// </summary>
public sealed class MasonryLayout : NonVirtualizingLayout
{
    /// <summary>单列最小宽度，窗口越宽列数越多</summary>
    public double MinColumnWidth { get; set; } = 172;

    public double ColumnSpacing { get; set; } = 14;

    public double RowSpacing { get; set; } = 14;

    /// <summary>由可用宽度算出列数与列宽（页面侧也用这个公式推列宽，保持与布局一致）</summary>
    public static (int Columns, double ColumnWidth) Compute(double availableWidth, double minColumnWidth, double spacing)
    {
        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            return (1, minColumnWidth);
        }

        var columns = Math.Max(1, (int)((availableWidth + spacing) / (minColumnWidth + spacing)));
        var width = (availableWidth - spacing * (columns - 1)) / columns;

        if (width <= 0)
        {
            columns = 1;
            width = availableWidth;
        }

        return (columns, width);
    }

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var children = context.Children;
        if (children.Count == 0) return new Size(0, 0);

        var width = availableSize.Width;
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0) width = MinColumnWidth;

        var (columns, columnWidth) = Compute(width, MinColumnWidth, ColumnSpacing);
        var heights = new double[columns];
        var itemSize = new Size(columnWidth, double.PositiveInfinity);

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(itemSize);

            var column = ShortestColumn(heights);
            heights[column] += child.DesiredSize.Height + RowSpacing;
        }

        var total = 0.0;
        for (var i = 0; i < heights.Length; i++) total = Math.Max(total, heights[i]);

        return new Size(width, Math.Max(0, total - RowSpacing));
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var children = context.Children;
        if (children.Count == 0) return finalSize;

        var (columns, columnWidth) = Compute(finalSize.Width, MinColumnWidth, ColumnSpacing);
        var heights = new double[columns];

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var column = ShortestColumn(heights);

            var x = column * (columnWidth + ColumnSpacing);
            var y = heights[column];

            child.Arrange(new Rect(x, y, columnWidth, child.DesiredSize.Height));
            heights[column] = y + child.DesiredSize.Height + RowSpacing;
        }

        return finalSize;
    }

    private static int ShortestColumn(double[] heights)
    {
        var index = 0;
        for (var i = 1; i < heights.Length; i++)
        {
            if (heights[i] < heights[index] - 0.01) index = i;
        }

        return index;
    }
}
