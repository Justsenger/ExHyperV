using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExHyperV.Models;

namespace ExHyperV.Tools
{
    /// <summary>
    /// ListBox 拖拽重排附加行为（替代原 Microsoft.Xaml.Behaviors 的 ListBoxDragDropBehavior，逻辑不变）。
    /// 用法：在 ListBox 上设置 behaviors:ListBoxDragDrop.MoveItemCommand / DropCompletedCommand。
    /// 注：项目 UseWindowsForms=true，故 ListBox / DragEventArgs 需全限定以消歧义。
    /// </summary>
    public static class ListBoxDragDrop
    {
        public static readonly DependencyProperty MoveItemCommandProperty =
            DependencyProperty.RegisterAttached(
                "MoveItemCommand", typeof(ICommand), typeof(ListBoxDragDrop),
                new PropertyMetadata(null, OnCommandChanged));

        public static ICommand? GetMoveItemCommand(DependencyObject o) => (ICommand?)o.GetValue(MoveItemCommandProperty);
        public static void SetMoveItemCommand(DependencyObject o, ICommand? v) => o.SetValue(MoveItemCommandProperty, v);

        public static readonly DependencyProperty DropCompletedCommandProperty =
            DependencyProperty.RegisterAttached(
                "DropCompletedCommand", typeof(ICommand), typeof(ListBoxDragDrop),
                new PropertyMetadata(null, OnCommandChanged));

        public static ICommand? GetDropCompletedCommand(DependencyObject o) => (ICommand?)o.GetValue(DropCompletedCommandProperty);
        public static void SetDropCompletedCommand(DependencyObject o, ICommand? v) => o.SetValue(DropCompletedCommandProperty, v);

        // 每个 ListBox 一个状态对象，挂在私有附加属性上，负责订阅事件与保存拖拽状态。
        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached("State", typeof(DragState), typeof(ListBoxDragDrop));

        private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not System.Windows.Controls.ListBox lb) return;
            if (lb.GetValue(StateProperty) is null)
                lb.SetValue(StateProperty, new DragState(lb));
        }

        private sealed class DragState
        {
            private readonly System.Windows.Controls.ListBox _lb;
            private Point _startPoint;
            private bool _isDragging;

            public DragState(System.Windows.Controls.ListBox lb)
            {
                _lb = lb;
                lb.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
                lb.PreviewMouseMove += OnPreviewMouseMove;
                lb.DragOver += OnDragOver;
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

                            var dropCmd = GetDropCompletedCommand(_lb);
                            if (dropCmd?.CanExecute(null) == true)
                                dropCmd.Execute(null);
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
                            double threshold = targetItem.ActualHeight / 3;

                            var moveCmd = GetMoveItemCommand(_lb);
                            if (moveCmd?.CanExecute(null) == true)
                            {
                                moveCmd.Execute(new DragMoveArgs
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

            private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
            {
                if (child == null) return null;
                DependencyObject parentObject = VisualTreeHelper.GetParent(child);
                if (parentObject == null) return null;
                if (parentObject is T parent) return parent;
                return FindVisualParent<T>(parentObject);
            }
        }
    }

    public class DragMoveArgs
    {
        public BootOrderItem? Source { get; set; }
        public BootOrderItem? Target { get; set; }
        public double RelativeY { get; set; }
        public double Threshold { get; set; }
    }
}
