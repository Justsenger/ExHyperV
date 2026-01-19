using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExHyperV.Models; // 引用模型

namespace ExHyperV.Views.Components
{
    public partial class VmCpuAffinityView : UserControl
    {
        private bool _isDragging = false;
        private VmCoreModel _lastToggledCore = null;

        public VmCpuAffinityView()
        {
            InitializeComponent();
        }

        // 鼠标按下：开始捕获
        private void ItemsControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            _lastToggledCore = null;

            // 立即处理第一次点击，防止需要移动一点点才算选中
            var core = GetCoreFromPosition(e.GetPosition(CoresItemsControl));
            if (core != null)
            {
                core.IsSelected = !core.IsSelected;
                _lastToggledCore = core;
            }

            (sender as IInputElement)?.CaptureMouse();
        }

        // 鼠标移动：如果是按下状态，则连续选中
        private void ItemsControl_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isDragging = true;
                var core = GetCoreFromPosition(e.GetPosition(CoresItemsControl));

                // 只有移到了一个新的格子才切换状态，防止在一个格子里抖动导致闪烁
                if (core != null && _lastToggledCore != core)
                {
                    core.IsSelected = !core.IsSelected;
                    _lastToggledCore = core;
                }
            }
        }

        // 鼠标抬起：释放捕获
        private void ItemsControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            _lastToggledCore = null;
            (sender as IInputElement)?.ReleaseMouseCapture();
        }

        // 辅助方法：通过坐标找 DataContext (核心模型)
        private VmCoreModel GetCoreFromPosition(Point position)
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
                // 注意这里转成 VmCoreModel，而不是原来的 SelectableCoreViewModel
                return contentPresenter.DataContext as VmCoreModel;
            }
            return null;
        }
    }
}