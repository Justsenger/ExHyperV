using ExHyperV.Services;
using ExHyperV.ViewModels;

namespace ExHyperV.Views.Components
{
    public partial class VmConsoleView : UserControl
    {
        public VmConsoleView()
        {
            InitializeComponent();

            this.DataContextChanged += (s, e) =>
            {
                if (e.OldValue is ConsoleViewModel oldVm)
                    oldVm.SendCadRequested -= OnSendCadRequested;

                if (e.NewValue is ConsoleViewModel newVm)
                {
                    newVm.SendCadRequested += OnSendCadRequested;
                }
            };

            RdpHost.OnRdpConnected += () =>
            {
                if (DataContext is ConsoleViewModel vm)
                    vm.IsLoading = false;
            };

            // 基本会话的硬件级 Ctrl+Alt+Del：控件回调上提到此处，由 Views 层调用 Service。
            RdpHost.SendCtrlAltDelViaWmi = vmId => VmInputService.SendCtrlAltDelAsync(vmId);
        }

        private void OnSendCadRequested(object? sender, EventArgs e)
        {
            this.SendCtrlAltDel();
        }

        public void SendCtrlAltDel()
        {
            RdpHost?.SendCtrlAltDel();
        }
        public void SuspendRdpLayout(bool suspended)
        {
            RdpHost?.SuspendLayout(suspended);
        }
    }
}