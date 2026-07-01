using System.Windows.Controls;
using ExHyperV.ViewModels;
using ExHyperV.Services;

namespace ExHyperV.Views
{
    public partial class VirtualMachinesPage : Page
    {
        public VirtualMachinesPage()
        {
            InitializeComponent();
            this.DataContext = new VirtualMachinesPageViewModel(new VmQueryService());
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
    }
}