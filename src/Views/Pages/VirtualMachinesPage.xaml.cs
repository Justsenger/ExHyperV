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
            this.DataContext = new VirtualMachinesPageViewModel(new VmQueryService(), new VmPowerService());
        }
    }
}