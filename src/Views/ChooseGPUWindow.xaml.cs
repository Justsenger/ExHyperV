using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ExHyperV.Models;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public partial class ChooseGPUWindow : FluentWindow
    {
        public string Machinename { get; private set; }

        public ObservableCollection<GpuChoice> Items { get; } = new();
        public GpuChoice SelectedGpu { get; private set; }
        public ChooseGPUWindow(string vmname, List<GPUInfo> hostGpuList)
        {
            Machinename = vmname;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            InitializeComponent();
            this.DataContext = this;
            var availableGpus = hostGpuList.Where(gpu => !string.IsNullOrEmpty(gpu.Pname));
            foreach (var gpu in availableGpus)
            {
                Items.Add(new GpuChoice
                {
                    GPUname = gpu.Name,
                    Path = gpu.Pname,
                    Iconpath = Utils.GetGpuImagePath(gpu.Manu, gpu.Name),
                    Manu = gpu.Manu,
                    Id = gpu.InstanceId
                });
            }
        }
        public class GpuChoice
        {
            public string GPUname { get; set; }
            public string Path { get; set; }
            public string Iconpath { get; set; }
            public string Manu { get; set; }
            public string Id { get; set; }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (GpuListView.SelectedItem is GpuChoice selectedGpu)
            {
                this.SelectedGpu = selectedGpu;
                this.DialogResult = true;
                this.Close();
            }
        }
        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ConfirmButton.IsEnabled = (sender as Wpf.Ui.Controls.ListView)?.SelectedItem != null;
        }
    }
}