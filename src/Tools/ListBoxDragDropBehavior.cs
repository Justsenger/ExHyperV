using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Xaml.Behaviors;
using ExHyperV.Models;

namespace ExHyperV.Behaviors
{
    public class ListBoxDragDropBehavior : Behavior<System.Windows.Controls.ListBox>
    {
        private Point _startPoint;
        private bool _isDragging = false;

        public static readonly DependencyProperty MoveItemCommandProperty =
            DependencyProperty.Register(nameof(MoveItemCommand), typeof(ICommand), typeof(ListBoxDragDropBehavior));

        public ICommand MoveItemCommand
        {
            get => (ICommand)GetValue(MoveItemCommandProperty);
            set => SetValue(MoveItemCommandProperty, value);
        }

        public static readonly DependencyProperty DropCompletedCommandProperty =
            DependencyProperty.Register(nameof(DropCompletedCommand), typeof(ICommand), typeof(ListBoxDragDropBehavior));

        public ICommand DropCompletedCommand
        {
            get => (ICommand)GetValue(DropCompletedCommandProperty);
            set => SetValue(DropCompletedCommandProperty, value);
        }

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            AssociatedObject.PreviewMouseMove += OnPreviewMouseMove;
            AssociatedObject.DragOver += OnDragOver;
            AssociatedObject.Drop += OnDrop;
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            AssociatedObject.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            AssociatedObject.PreviewMouseMove -= OnPreviewMouseMove;
            AssociatedObject.DragOver -= OnDragOver;
            AssociatedObject.Drop -= OnDrop;
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(null);
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging)
            {
                Point position = e.GetPosition(null);
                if (Math.Abs(position.X - _startPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(position.Y - _startPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    var item = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
                    if (item != null)
                    {
                        _isDragging = true;
                        item.Opacity = 0.6;
                        
                        System.Windows.DragDrop.DoDragDrop(item, item.DataContext, System.Windows.DragDropEffects.Move);
                        item.Opacity = 1.0;
                        _isDragging = false;
                    }
                }
            }
        }

        
        private void OnDragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(BootOrderItem)))
            {
                var sourceData = e.Data.GetData(typeof(BootOrderItem)) as BootOrderItem;
                var targetItem = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);

                if (targetItem != null && sourceData != null)
                {
                    var targetData = targetItem.DataContext as BootOrderItem;
                    if (sourceData != targetData)
                    {
                        Point relativePos = e.GetPosition(targetItem);
                        double threshold = targetItem.ActualHeight / 4;

                        if (MoveItemCommand?.CanExecute(null) == true)
                        {
                            MoveItemCommand.Execute(new DragMoveArgs
                            {
                                Source = sourceData,
                                Target = targetData,
                                RelativeY = relativePos.Y,
                                Threshold = threshold
                            });
                        }
                    }
                }
            }
            
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
        }

        
        private void OnDrop(object sender, System.Windows.DragEventArgs e)
        {
            _isDragging = false;
            if (DropCompletedCommand?.CanExecute(null) == true)
            {
                DropCompletedCommand.Execute(null);
            }
            e.Handled = true;
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
        }
    }

    public class DragMoveArgs
    {
        public BootOrderItem Source { get; set; }
        public BootOrderItem Target { get; set; }
        public double RelativeY { get; set; }
        public double Threshold { get; set; }
    }
}