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

            HeaderText.Text = $"「{vmName}」— 发现 {stalePartitions.Count} 个失配/幽灵 GPU 分区";

            foreach (var item in stalePartitions)
            {
                string action = item.IsStructurallyIncomplete
                    ? "删除（配置结构残缺）"
                    : item.RebindPath != null
                        ? "重指到主机上的新路径"
                        : "删除（该显卡已不在主机）";

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
                                Text = $"VDEV {item.VdevNumber} — {action}",
                                FontWeight = FontWeights.SemiBold,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock
                            {
                                Text = "当前 HostResource：" + item.HostResource,
                                Opacity = 0.7,
                                FontSize = 12,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock
                            {
                                Text = item.RebindPath != null ? "修复后指向：" + item.RebindPath : "修复后该设备将从虚拟机配置中移除。",
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
