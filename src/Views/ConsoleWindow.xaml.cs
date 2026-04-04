using Wpf.Ui.Controls;
using ExHyperV.ViewModels;
using System.ComponentModel;
using System.Windows.Input;

namespace ExHyperV.Views
{
    public partial class ConsoleWindow : FluentWindow
    {
        private readonly ConsoleViewModel _viewModel;
        private bool _wasMaximized = false;
        private bool _isApplyingFullScreen = false;

        public ConsoleWindow(string vmId, string vmName)
        {
            _viewModel = new ConsoleViewModel(vmId, vmName);
            this.DataContext = _viewModel;
            InitializeComponent();
            this.Title = vmName;

            _viewModel.SendCadRequested += OnSendCadRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ConsoleViewModel.IsFullScreen) && !_isApplyingFullScreen)
                ApplyFullScreen(_viewModel.IsFullScreen);
        }
        private void ApplyFullScreen(bool fullScreen)
        {
            if (_isApplyingFullScreen) return;
            _isApplyingFullScreen = true;

            ConsoleHost.SuspendRdpLayout(true);   // 幕布盖上

            if (fullScreen)
            {
                _wasMaximized = this.WindowState == System.Windows.WindowState.Maximized;
                this.WindowState = System.Windows.WindowState.Normal;
                this.WindowStyle = System.Windows.WindowStyle.None;
                this.WindowState = System.Windows.WindowState.Maximized;
                this.Topmost = true;
            }
            else
            {
                this.Topmost = false;
                this.WindowState = System.Windows.WindowState.Normal;
                this.WindowStyle = System.Windows.WindowStyle.SingleBorderWindow;
                if (_wasMaximized)
                    this.WindowState = System.Windows.WindowState.Maximized;
            }

            Task.Delay(24).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                _isApplyingFullScreen = false;
                ConsoleHost.SuspendRdpLayout(false);
            }));
        }


        private void OnSendCadRequested(object? sender, EventArgs e)
        {
            ConsoleHost?.SendCtrlAltDel();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _viewModel.SendCadRequested -= OnSendCadRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
        }

        private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }
    }
}