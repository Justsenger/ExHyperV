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