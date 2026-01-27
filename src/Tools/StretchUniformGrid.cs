using System;
using System.Windows;
using System.Windows.Controls;

namespace ExHyperV.Tools
{
    public class StretchUniformGrid : Panel
    {
        public static readonly DependencyProperty ColumnsProperty =
            DependencyProperty.Register(nameof(Columns), typeof(int), typeof(StretchUniformGrid),
                new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public int Columns
        {
            get => (int)GetValue(ColumnsProperty);
            set => SetValue(ColumnsProperty, value);
        }

        public static readonly DependencyProperty RowsProperty =
            DependencyProperty.Register(nameof(Rows), typeof(int), typeof(StretchUniformGrid),
                new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public int Rows
        {
            get => (int)GetValue(RowsProperty);
            set => SetValue(RowsProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (InternalChildren.Count == 0 || Columns <= 0 || Rows <= 0)
                return new Size(0, 0);

            double totalWidth = availableSize.Width;
            if (double.IsInfinity(totalWidth)) totalWidth = 800; // 兜底

            // 1. 先测量一个子项，看看它“自然状态”下希望有多高
            // 我们给它无限空间，看它的 DataTemplate 定义了多高
            UIElement firstChild = InternalChildren[0];
            firstChild.Measure(new Size(totalWidth / Columns, double.PositiveInfinity));

            // 拿到单行的高度。如果子项没写死高度，DesiredSize 会根据内部文字和图形撑起一个合适的高度
            double rowHeight = firstChild.DesiredSize.Height;

            // 2. 测量所有子项，并告诉它们最终的尺寸
            Size cellSize = new Size(totalWidth / Columns, rowHeight);
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(cellSize);
            }

            // 3. 总高度 = 单行高度 * 行数
            // 这样 4核(2行) 和 32核(4行) 的高度逻辑就统一了
            return new Size(totalWidth, rowHeight * Rows);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (InternalChildren.Count == 0 || Columns <= 0 || Rows <= 0)
                return finalSize;

            double cellWidth = finalSize.Width / Columns;
            double cellHeight = finalSize.Height / Rows;

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                int col = i % Columns;
                int row = i / Columns;
                child.Arrange(new Rect(col * cellWidth, row * cellHeight, cellWidth, cellHeight));
            }

            return finalSize;
        }
    }
}