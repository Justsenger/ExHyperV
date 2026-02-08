using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using ExHyperV.Models;

namespace ExHyperV.Views
{
    public partial class ChoosePartitionWindow : FluentWindow
    {
        public PartitionInfo SelectedPartition { get; private set; }
        public ChoosePartitionWindow(List<PartitionInfo> partitions)
        {
            InitializeComponent();
            PartitionListView.ItemsSource = partitions;
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PartitionListView.SelectedItem is PartitionInfo selected)
            {
                SelectedPartition = selected;
                ConfirmButton.IsEnabled = true;
            }
            else
            {
                ConfirmButton.IsEnabled = false;
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}