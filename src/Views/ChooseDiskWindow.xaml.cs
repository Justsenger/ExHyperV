using ExHyperV.Models;
using ExHyperV.ViewModels;
using System.Windows;

namespace ExHyperV.Views
{
    public partial class ChooseDiskWindow
    {
        public AddDiskViewModel ViewModel => (AddDiskViewModel)DataContext;

        public ChooseDiskWindow(string vmName, int generation, bool isVmRunning, List<VmStorageControllerInfo> currentStorage)
        {
            InitializeComponent();
            DataContext = new AddDiskViewModel(vmName, generation, isVmRunning, currentStorage);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}