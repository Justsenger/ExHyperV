using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExHyperV.Models;
using ExHyperV.ViewModels;

namespace ExHyperV.Views.Components
{
    public partial class VmBootSettingsView : UserControl
    {
        private Point _startPoint;
        private bool _isDragging = false;

        public VmBootSettingsView() => InitializeComponent();

        private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(null);
        }

        private void Card_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging)
            {
                Point position = e.GetPosition(null);
                if (Math.Abs(position.X - _startPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(position.Y - _startPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is ListBoxItem listBoxItem)
                    {
                        _isDragging = true;
                        listBoxItem.Opacity = 0.6;
                        DragDrop.DoDragDrop(listBoxItem, listBoxItem.DataContext, System.Windows.DragDropEffects.Move);
                        listBoxItem.Opacity = 1.0;
                        _isDragging = false;
                    }
                }
            }
        }

        private void BootListBox_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(BootOrderItem)))
            {
                var droppedData = e.Data.GetData(typeof(BootOrderItem)) as BootOrderItem;
                var targetItem = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);

                if (targetItem != null && droppedData != null)
                {
                    var targetData = targetItem.DataContext as BootOrderItem;
                    var viewModel = this.DataContext as VirtualMachinesPageViewModel;

                    if (viewModel?.SelectedVm != null && droppedData != targetData)
                    {
                        var list = viewModel.SelectedVm.BootOrderItems;
                        int oldIndex = list.IndexOf(droppedData);
                        int newIndex = list.IndexOf(targetData);

                        if (oldIndex != -1 && newIndex != -1)
                        {
                            Point relativeMousePos = e.GetPosition(targetItem);
                            double threshold = targetItem.ActualHeight / 3;

                            if (newIndex > oldIndex) // 正在向下拖
                            {
                                // 鼠标进入目标卡片顶部 1/3 以上时，不触发交换
                                if (relativeMousePos.Y < threshold) return;
                            }
                            else // 正在向上拖
                            {
                                // 鼠标在目标卡片底部 1/3 以下时，不触发交换
                                if (relativeMousePos.Y > targetItem.ActualHeight - threshold) return;
                            }

                            list.Move(oldIndex, newIndex);
                        }
                    }
                }
            }
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
        }
        private async void BootListBox_Drop(object sender, System.Windows.DragEventArgs e)
        {
            _isDragging = false;
            if (e.Data.GetDataPresent(typeof(BootOrderItem)))
            {
                var droppedData = e.Data.GetData(typeof(BootOrderItem)) as BootOrderItem;
                var targetItem = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);

                if (targetItem != null && droppedData != null)
                {
                    var targetData = targetItem.DataContext as BootOrderItem;
                    var viewModel = this.DataContext as VirtualMachinesPageViewModel;

                    if (viewModel?.SelectedVm != null && droppedData != targetData)
                    {
                        var list = viewModel.SelectedVm.BootOrderItems;
                        int oldIndex = list.IndexOf(droppedData);
                        int newIndex = list.IndexOf(targetData);

                        if (oldIndex != -1 && newIndex != -1)
                        {
                            list.Move(oldIndex, newIndex);

                            await viewModel.SilentSaveBootOrderAsync();
                        }
                    }
                }
            }
            e.Handled = true;
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            if (child == null) return null;
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
        }
    }
}