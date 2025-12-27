using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ExHyperV.Models;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public partial class ChooseDiskWindow : FluentWindow
    {
        public ObservableCollection<DiskChoice> Items { get; } = new();
        public DiskChoice SelectedDisk { get; private set; }
        public bool AutoOffline { get; private set; }

        public ChooseDiskWindow(List<HostDiskInfo> hostDiskList)
        {
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            InitializeComponent();
            this.DataContext = this;

            var availableDisks = hostDiskList.Where(d => !d.IsSystem);
            foreach (var disk in availableDisks)
            {
                Items.Add(new DiskChoice
                {
                    Number = disk.Number,
                    FriendlyName = disk.FriendlyName,
                    SizeGB = disk.SizeGB,
                    IsOffline = disk.IsOffline,
                    OperationalStatus = disk.OperationalStatus
                });
            }
        }

        public class DiskChoice
        {
            public int Number { get; set; }
            public string FriendlyName { get; set; }
            public double SizeGB { get; set; }
            public bool IsOffline { get; set; }
            public string OperationalStatus { get; set; }
            public string SizeDisplay => $"{SizeGB} GB";
            public string StatusDisplay => IsOffline ? "已脱机 (就绪)" : $"联机 ({OperationalStatus})";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (DiskListView.SelectedItem is DiskChoice selectedDisk)
            {
                this.SelectedDisk = selectedDisk;
                this.AutoOffline = AutoOfflineCheckBox.IsChecked ?? false;
                this.DialogResult = true;
                this.Close();
            }
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ConfirmButton.IsEnabled = DiskListView.SelectedItem != null;
        }
    }
}