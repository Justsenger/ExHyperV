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
    }
}
