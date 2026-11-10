using System.Windows.Controls;
using ExHyperV.Models;

namespace ExHyperV.Views
{
    /// <summary>GPU-PV「分配分区」对话框：只列出关机状态的虚拟机，并可选择分区份额。</summary>
    public partial class GpuAssignDialog : UserControl
    {
        private sealed record ShareOption(int Percent, string Label);

        private static readonly ShareOption[] ShareOptions =
        {
            new(100, "100%（整卡上限，不缩放）"),
            new(75, "75%（接近满额）"),
            new(50, "50%（多机共享常用）"),
            new(25, "25%（轻量图形）"),
        };

        public GpuAssignDialog(GpuInfo gpu, IEnumerable<string> vmNames)
        {
            InitializeComponent();

            GpuNameText.Text = gpu.Name;
            var names = vmNames.ToList();
            VmCombo.ItemsSource = names;
            if (names.Count > 0)
            {
                VmCombo.SelectedIndex = 0;
            }
            else
            {
                VmCombo.IsEnabled = false;
                EmptyHint.Visibility = System.Windows.Visibility.Visible;
            }

            ShareCombo.ItemsSource = ShareOptions;
            ShareCombo.SelectedIndex = 0;
            UpdateShareHint();
        }

        public string? SelectedVmName => VmCombo.SelectedItem as string;

        public int SelectedSharePercent => ShareCombo.SelectedItem is ShareOption option ? option.Percent : 100;

        private void ShareCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateShareHint();

        private void UpdateShareHint()
        {
            ShareHint.Text = SelectedSharePercent switch
            {
                100 => "使用模板原值：该分区可获得整卡的全部显存与编解码/计算上限，性能最好；多台虚拟机同时分配时更容易互相抢占。",
                75 => "显存与编解码/计算上限压到约 3/4：性能损失较小，适合一台主力虚拟机搭配一台轻量副机。",
                50 => "上限减半：显存带宽与编解码/计算能力最高只能用一半，高负载游戏或渲染的帧率、分辨率会明显下降，适合办公桌面与轻量图形。",
                25 => "上限约为 1/4：只适合桌面、视频播放等轻量场景，3D 负载会明显受限。",
                _ => string.Empty,
            };
        }
    }
}
