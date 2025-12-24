using ExHyperV.ViewModels.Dialogs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ExHyperV.Views.Dialogs
{
    public partial class CpuAffinityDialog
    {
        private bool _isDragging = false;
        private SelectableCoreViewModel _lastToggledCore = null;

        public CpuAffinityDialog()
        {
            InitializeComponent();
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e) { this.DialogResult = true; this.Close(); }
        private void CancelButton_Click(object sender, RoutedEventArgs e) { this.DialogResult = false; this.Close(); }

        private void ItemsControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            _lastToggledCore = null;
            (sender as IInputElement)?.CaptureMouse();
        }

        private void ItemsControl_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isDragging = true;

                var core = GetCoreFromPosition(e.GetPosition(CoresItemsControl));
                if (core != null && _lastToggledCore != core)
                {
                    core.IsSelected = !core.IsSelected;
                    _lastToggledCore = core;
                }
            }
        }

        private void ItemsControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging)
            {
                var core = GetCoreFromPosition(e.GetPosition(CoresItemsControl));
                if (core != null)
                {
                    core.IsSelected = !core.IsSelected;
                }
            }
            _isDragging = false;
            _lastToggledCore = null;
            (sender as IInputElement)?.ReleaseMouseCapture();
        }

        private SelectableCoreViewModel GetCoreFromPosition(Point position)
        {
            var hitTestResult = VisualTreeHelper.HitTest(CoresItemsControl, position);
            if (hitTestResult == null) return null;

            DependencyObject current = hitTestResult.VisualHit;
            while (current != null && !(current is ContentPresenter))
            {
                current = VisualTreeHelper.GetParent(current);
            }

            if (current is ContentPresenter contentPresenter)
            {
                return contentPresenter.DataContext as SelectableCoreViewModel;
            }
            return null;
        }
    }
}