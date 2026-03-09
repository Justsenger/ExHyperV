using System.Windows.Controls;
using ExHyperV.ViewModels;
namespace ExHyperV.Views.Components
{
    public partial class VmConsoleView : UserControl
    {
        public VmConsoleView()
        {
            InitializeComponent();
            RdpHost.OnRdpConnected += () =>
            {
                if (DataContext is ConsoleViewModel vm)
                    vm.IsLoading = false;
            };
        }

        // --- 新增这个方法 ---
        public void SendCtrlAltDel()
        {
            // 调用内部 RdpHost 控件的方法
            RdpHost.SendCtrlAltDel();
        }
    }
}
