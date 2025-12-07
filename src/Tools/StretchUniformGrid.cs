namespace ExHyperV.Tools
{
    using System;
    using System.Windows;
    using System.Windows.Controls;

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
            {
                return new Size(0, 0);
            }

            if (double.IsInfinity(availableSize.Width) || double.IsInfinity(availableSize.Height))
            {
                return new Size(0, 0);
            }

            return availableSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (InternalChildren.Count == 0 || Columns <= 0 || Rows <= 0)
            {
                return finalSize;
            }

            double cellWidth = finalSize.Width / Columns;
            double cellHeight = finalSize.Height / Rows;

            if (double.IsNaN(cellWidth) || double.IsNaN(cellHeight) || double.IsInfinity(cellWidth) || double.IsInfinity(cellHeight))
            {
                return finalSize;
            }

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];

                int col = i % Columns;
                int row = i / Columns;

                double x = col * cellWidth;
                double y = row * cellHeight;

                var finalRect = new Rect(x, y, cellWidth, cellHeight);
                child.Arrange(finalRect);
            }

            return finalSize;
        }
    }
}