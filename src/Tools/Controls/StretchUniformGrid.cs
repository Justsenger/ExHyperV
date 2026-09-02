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
            if (double.IsInfinity(totalWidth)) totalWidth = 800;

            UIElement firstChild = InternalChildren[0];
            firstChild.Measure(new Size(totalWidth / Columns, double.PositiveInfinity));

            double rowHeight = firstChild.DesiredSize.Height;

            Size cellSize = new Size(totalWidth / Columns, rowHeight);
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(cellSize);
            }

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
