using System.Windows;
using System.Windows.Controls;
using ExHyperV.Models;

namespace ExHyperV.Views
{
    /// <summary>GPU-PV「分配分区」对话框：只列出关机状态的虚拟机，并可选择分区份额。</summary>
    public partial class GpuAssignDialog : UserControl
    {
        private sealed record ShareOption(int Percent, string Label)
        {
            // ComboBox 项在 UI Automation 中的默认名称取 ToString()，
            // 覆盖后不再暴露编译器生成的 "ShareOption { Percent = ..., Label = ... }"。
            public override string ToString() => Label;
        }

        private static ShareOption[] CreateShareOptions() => new[]
        {
            new ShareOption(100, Properties.Resources.GpuPv_Share_100),
            new ShareOption(75, Properties.Resources.GpuPv_Share_75),
            new ShareOption(50, Properties.Resources.GpuPv_Share_50),
            new ShareOption(25, Properties.Resources.GpuPv_Share_25),
        };

        public GpuAssignDialog(GpuInfo gpu, IEnumerable<string> vmNames)
        {
            InitializeComponent();

            GpuNameText.Text = gpu.Name;
            var names = vmNames.ToList();
            VmCombo.ItemsSource = names;
            if (names.Count > 0)
            {
                // 不预选任何虚拟机：默认选中列表第一项容易把分区误分配给错误的 VM。
                VmCombo.SelectedIndex = -1;
            }
            else
            {
                VmCombo.IsEnabled = false;
                EmptyHint.Visibility = Visibility.Visible;
            }

            ShareCombo.ItemsSource = CreateShareOptions();
            ShareCombo.SelectedIndex = 0;
            UpdateShareHint();
            UpdateVmHint();
        }

        public string? SelectedVmName => VmCombo.SelectedItem as string;

        public int SelectedSharePercent => ShareCombo.SelectedItem is ShareOption option ? option.Percent : 100;

        private void VmCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateVmHint();

        private void ShareCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateShareHint();

        private void UpdateVmHint()
        {
            SelectVmHint.Visibility = VmCombo.Items.Count > 0 && VmCombo.SelectedIndex < 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateShareHint()
        {
            ShareHint.Text = SelectedSharePercent switch
            {
                100 => Properties.Resources.GpuPv_ShareHint_100,
                75 => Properties.Resources.GpuPv_ShareHint_75,
                50 => Properties.Resources.GpuPv_ShareHint_50,
                25 => Properties.Resources.GpuPv_ShareHint_25,
                _ => string.Empty,
            };
        }
    }
}
