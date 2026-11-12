using System.Windows;
using System.Windows.Controls;
using ExHyperV.Services;

namespace ExHyperV.Views
{
    /// <summary>幽灵/失配 GPU 分区修复前的确认视图：逐条列出处理方式与目标路径。</summary>
    public partial class GpuRepairDialog : UserControl
    {
        public GpuRepairDialog(string vmName, IReadOnlyList<VmGpuRepairService.StaleGpuPartition> stalePartitions)
        {
            InitializeComponent();

            HeaderText.Text = string.Format(Properties.Resources.GpuPv_Repair_Header, vmName, stalePartitions.Count);

            foreach (var item in stalePartitions)
            {
                string action = item.IsStructurallyIncomplete
                    ? Properties.Resources.GpuPv_Repair_Action_Incomplete
                    : item.RebindPath != null
                        ? Properties.Resources.GpuPv_Repair_Action_Rebind
                        : Properties.Resources.GpuPv_Repair_Action_Remove;

                ItemsHost.Children.Add(new Border
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush = System.Windows.Media.Brushes.Gray,
                    Padding = new Thickness(2, 6, 2, 6),
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = string.Format(Properties.Resources.GpuPv_Repair_ItemHeader, item.VdevNumber, action),
                                FontWeight = FontWeights.SemiBold,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock
                            {
                                Text = Properties.Resources.GpuPv_Repair_CurrentHostResource + item.HostResource,
                                Opacity = 0.7,
                                FontSize = 12,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock
                            {
                                Text = item.RebindPath != null
                                    ? Properties.Resources.GpuPv_Repair_RebindTo + item.RebindPath
                                    : Properties.Resources.GpuPv_Repair_WillRemove,
                                Opacity = 0.7,
                                FontSize = 12,
                                TextWrapping = TextWrapping.Wrap,
                                Visibility = item.IsStructurallyIncomplete ? Visibility.Collapsed : Visibility.Visible,
                            },
                        },
                    },
                });
            }
        }
    }
}
