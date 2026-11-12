using System.Windows;
using System.Windows.Controls;
using ExHyperV.Models;

namespace ExHyperV.Views
{
    /// <summary>驱动复制/清理前选择目标系统分区。</summary>
    public partial class GpuDriverPartitionDialog : UserControl
    {
        public GpuDriverPartitionDialog(string vmName, IEnumerable<PartitionInfo> partitions, string action)
        {
            InitializeComponent();

            HeaderText.Text = string.Format(Properties.Resources.GpuPv_Partition_Header, vmName, action);
            PartitionList.ItemsSource = partitions.ToList();
            if (PartitionList.Items.Count > 0)
            {
                PartitionList.SelectedIndex = 0;
            }

            PartitionList.SelectionChanged += (_, _) => UpdateHint();
            UpdateHint();
        }

        public PartitionInfo? SelectedPartition => PartitionList.SelectedItem as PartitionInfo;

        private void UpdateHint()
        {
            LinuxHint.Visibility = SelectedPartition is { OsType: not OperatingSystemType.Windows }
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}
