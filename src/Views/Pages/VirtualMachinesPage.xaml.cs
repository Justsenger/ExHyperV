using System.Windows.Controls;
using ExHyperV.ViewModels;
using ExHyperV.Services;
using ExHyperV.Models;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using WpfApi = System.Windows;
using Input = System.Windows.Input;

namespace ExHyperV.Views
{
    public partial class VirtualMachinesPage : Page
    {
        private VmNavigationItem? _dragSourceRow;
        private VmNavigationItem[] _dragRows = [];
        private WpfApi.Point _dragOrigin;
        private bool _deferredSelection;
        private DropOutline? _dropOutline;
        private DateTime _lastDragScroll;
        private sealed record NavigationDrag(VirtualMachinesPage Owner, VmGroupMove[] Items);

        public VirtualMachinesPage()
        {
            InitializeComponent();
            this.DataContext = new VirtualMachinesPageViewModel(new VmQueryService());
        }

        private void NavigationItem_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragSourceRow = null;
            _dragRows = [];
            _deferredSelection = false;
            if (sender is not System.Windows.Controls.ListViewItem { DataContext: VmNavigationItem item } || item.Vm?.IsEditing == true || item.NameEdit.IsEditing) return;
            _dragOrigin = e.GetPosition(NavigationList);
            _dragSourceRow = item;
            _dragRows = !item.IsGroup && NavigationList.SelectedItems.Contains(item)
                ? NavigationList.SelectedItems.OfType<VmNavigationItem>().Where(n => !n.IsGroup).ToArray() : [item];
            if (item.IsGroup || (_dragRows.Length > 1 && Input.Keyboard.Modifiers == Input.ModifierKeys.None))
            {
                // Defer group expansion until mouse-up so pressing a group can begin a drag.
                _deferredSelection = !item.IsGroup;
                e.Handled = true;
            }
        }

        private void NavigationList_PreviewMouseLeftButtonUp(object sender, Input.MouseButtonEventArgs e)
        {
            if (_dragSourceRow is { } row && DataContext is VirtualMachinesPageViewModel vm &&
                FindRow(e.OriginalSource as WpfApi.DependencyObject)?.DataContext is VmNavigationItem current && current.Key == row.Key)
            {
                if (row.IsGroup)
                {
                    e.Handled = true;
                    vm.ToggleGroupCommand.Execute(row);
                }
                else if (_deferredSelection) { NavigationList.SelectedItems.Clear(); NavigationList.SelectedItem = row; }
            }
            _dragSourceRow = null;
            _dragRows = [];
            _deferredSelection = false;
        }

        private void NavigationList_PreviewMouseMove(object sender, Input.MouseEventArgs e)
        {
            if (_dragSourceRow == null || e.LeftButton != Input.MouseButtonState.Pressed ||
                DataContext is not VirtualMachinesPageViewModel vm || !vm.CanEditGroups) return;
            var point = e.GetPosition(NavigationList);
            if (Math.Abs(point.X - _dragOrigin.X) < WpfApi.SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - _dragOrigin.Y) < WpfApi.SystemParameters.MinimumVerticalDragDistance) return;
            if (!_dragSourceRow.IsGroup)
            {
                // Ctrl/Shift may have changed selection after the preview mouse-down event.
                if (!NavigationList.SelectedItems.Contains(_dragSourceRow)) return;
                _dragRows = NavigationList.SelectedItems.OfType<VmNavigationItem>().Where(n => !n.IsGroup).ToArray();
            }
            var moves = _dragRows.Select(n => new VmGroupMove(n.Group?.Id ?? n.Vm!.Id, n.IsGroup, n.ParentId)).Distinct().ToArray();
            _dragSourceRow = null;
            _dragRows = [];
            _deferredSelection = false;
            vm.IsDraggingNavigation = true;
            try
            {
                WpfApi.DragDrop.DoDragDrop(NavigationList, new WpfApi.DataObject(typeof(NavigationDrag), new NavigationDrag(this, moves)), WpfApi.DragDropEffects.Move);
            }
            finally
            {
                ClearDropOutline();
                vm.IsDraggingNavigation = false;
                vm.RefreshGroupsCommand.Execute(null);
            }
            e.Handled = true;
        }

        private NavigationDrag? DragPayload(WpfApi.DragEventArgs e) =>
            e.Data.GetDataPresent(typeof(NavigationDrag)) && e.Data.GetData(typeof(NavigationDrag)) is NavigationDrag data && ReferenceEquals(data.Owner, this) ? data : null;

        private void NavigationList_DragOver(object sender, WpfApi.DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = WpfApi.DragDropEffects.None;
            var data = DragPayload(e);
            var row = FindRow(e.OriginalSource as WpfApi.DependencyObject);
            if (data != null && DataContext is VirtualMachinesPageViewModel vm && vm.CanDropNavigation(data.Items, row?.DataContext as VmNavigationItem))
            {
                e.Effects = WpfApi.DragDropEffects.Move;
                ShowDropOutline(row ?? (WpfApi.UIElement)NavigationList);
            }
            else ClearDropOutline();
            if (data != null && DateTime.UtcNow - _lastDragScroll > TimeSpan.FromMilliseconds(100))
            {
                double y = e.GetPosition(NavigationList).Y;
                var scroll = FindChild<ScrollViewer>(NavigationList);
                if (y < 24) scroll?.LineUp();
                else if (y > NavigationList.ActualHeight - 24) scroll?.LineDown();
                _lastDragScroll = DateTime.UtcNow;
            }
        }

        private async void NavigationList_Drop(object sender, WpfApi.DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = WpfApi.DragDropEffects.None;
            ClearDropOutline();
            var data = DragPayload(e);
            var target = FindRow(e.OriginalSource as WpfApi.DependencyObject)?.DataContext as VmNavigationItem;
            if (data == null || DataContext is not VirtualMachinesPageViewModel vm || !vm.CanDropNavigation(data.Items, target)) return;
            e.Effects = WpfApi.DragDropEffects.Move;
            await vm.DropNavigationAsync(data.Items, target);
        }

        private void NavigationList_DragLeave(object sender, WpfApi.DragEventArgs e) => ClearDropOutline();

        private static WpfApi.DependencyObject? ParentOf(WpfApi.DependencyObject node) => node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) : WpfApi.LogicalTreeHelper.GetParent(node);

        private static System.Windows.Controls.ListViewItem? FindRow(WpfApi.DependencyObject? node)
        {
            while (node != null)
            {
                if (node is System.Windows.Controls.ListViewItem row) return row;
                node = ParentOf(node);
            }
            return null;
        }

        private static T? FindChild<T>(WpfApi.DependencyObject node) where T : WpfApi.DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T found) return found;
                if (FindChild<T>(child) is { } nested) return nested;
            }
            return null;
        }

        private void ShowDropOutline(WpfApi.UIElement target)
        {
            if (_dropOutline?.AdornedElement == target) return;
            ClearDropOutline();
            if (AdornerLayer.GetAdornerLayer(target) is { } layer)
            {
                _dropOutline = new DropOutline(target, TryFindResource("AccentFillColorDefaultBrush") as Brush ?? WpfApi.SystemColors.HighlightBrush) { IsHitTestVisible = false };
                layer.Add(_dropOutline);
            }
        }

        private void ClearDropOutline()
        {
            if (_dropOutline == null) return;
            AdornerLayer.GetAdornerLayer(_dropOutline.AdornedElement)?.Remove(_dropOutline);
            _dropOutline = null;
        }

        private sealed class DropOutline(WpfApi.UIElement target, Brush brush) : Adorner(target)
        {
            protected override void OnRender(DrawingContext context)
            {
                var size = AdornedElement.RenderSize;
                context.DrawRoundedRectangle(null, new Pen(brush, 2), new WpfApi.Rect(1, 1, Math.Max(0, size.Width - 2), Math.Max(0, size.Height - 2)), 4, 4);
            }
        }

        // OS 类型下拉：ComboBox 无 Command，改选后复用 ChangeOsTypeCommand 把 [OSType:] 落地到 WMI Notes。
        // SelectedItem 走 OneWay，命令是 OsType 的唯一写者；命令内部的相等守卫挡掉加载/切换虚拟机时的空触发。
        private void OnOsTypeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox { SelectedItem: string osType }
                && DataContext is VirtualMachinesPageViewModel vm
                && vm.ChangeOsTypeCommand.CanExecute(osType))
            {
                vm.ChangeOsTypeCommand.Execute(osType);
            }
        }

        // ListView 多选（Ctrl/Shift）无法直接绑定 SelectedItems，经此把选中集推给 VM：>1 时右键菜单收敛为删除/彻底删除。
        private void VmList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.ListView lv && DataContext is VirtualMachinesPageViewModel vm)
            {
                foreach (var row in lv.SelectedItems.OfType<VmNavigationItem>().Where(n => n.IsGroup).ToArray())
                    lv.SelectedItems.Remove(row);
                vm.UpdateSelection(lv.SelectedItems.OfType<VmNavigationItem>().Where(n => n.Vm != null)
                    .Select(n => n.Vm!).DistinctBy(v => v.Id).ToArray());
            }
        }
    }
}
