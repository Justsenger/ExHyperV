using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExHyperV.ViewModels;

namespace ExHyperV.Views;

public partial class VmExportView : UserControl
{
    private VmExportDiskItemViewModel? _lastToggledDisk;

    public VmExportView() => InitializeComponent();

    private void ExportDisksList_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _lastToggledDisk = null;

        VmExportDiskItemViewModel? disk = GetExportDiskFromPosition(
            e.GetPosition(ExportDisksList));
        if (disk == null) return;

        disk.IsIncluded = !disk.IsIncluded;
        _lastToggledDisk = disk;
        (sender as IInputElement)?.CaptureMouse();
        e.Handled = true;
    }

    private void ExportDisksList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        VmExportDiskItemViewModel? disk = GetExportDiskFromPosition(
            e.GetPosition(ExportDisksList));
        if (disk == null || ReferenceEquals(disk, _lastToggledDisk)) return;

        disk.IsIncluded = !disk.IsIncluded;
        _lastToggledDisk = disk;
        e.Handled = true;
    }

    private void ExportDisksList_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        _lastToggledDisk = null;
        (sender as IInputElement)?.ReleaseMouseCapture();
        e.Handled = true;
    }

    private VmExportDiskItemViewModel? GetExportDiskFromPosition(Point position)
    {
        HitTestResult? hitTestResult = VisualTreeHelper.HitTest(ExportDisksList, position);
        if (hitTestResult == null) return null;

        DependencyObject? current = hitTestResult.VisualHit;
        while (current != null && !ReferenceEquals(current, ExportDisksList))
        {
            if (current is FrameworkElement element
                && element.DataContext is VmExportDiskItemViewModel disk)
            {
                var container = ItemsControl.ContainerFromElement(
                    ExportDisksList,
                    current) as System.Windows.Controls.ListViewItem;
                return container?.IsEnabled == true ? disk : null;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
