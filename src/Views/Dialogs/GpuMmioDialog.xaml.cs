using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Views
{
    /// <summary>MMIO 修正前展示「当前值 → 目标值」，由用户确认后再写入。</summary>
    public partial class GpuMmioDialog : UserControl
    {
        public GpuMmioDialog(string vmName, VmMmioSettings current, VmMmioService.MmioPlan target, bool ceilingKnown)
        {
            InitializeComponent();

            HeaderText.Text = $"「{vmName}」— MMIO 体检结果";
            AddHeaderRow();
            AddRow("高位 MMIO 基址", current.HighBaseMb, target.BaseMb);
            AddRow("高位 MMIO 间隙", current.HighSizeMb, target.HighSizeMb);
            AddRow("低位 MMIO 间隙", current.LowSizeMb, target.LowSizeMb);
            AddRow("GuestControlledCacheTypes", null, null, targetValue: "启用");

            NoteText.Text = ceilingKnown
                ? "以上目标值按主机已探测到的 MMIO 上限计算：高位基址取上限的一半，高位间隙在剩余空间内取 256 GB 上限。应用后配置立即写入，可用虚拟机检查点还原。"
                : "当前没有主机 MMIO 上限缓存，本次只把高位间隙提升到默认 256 GB，高位基址与低位间隙保持现值。若应用后虚拟机无法启动，可在 VM 设置页执行完整探测，或用检查点还原。";
        }

        private void AddHeaderRow()
        {
            int row = ValueGrid.RowDefinitions.Count;
            ValueGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(row, 0, "项目", bold: true);
            AddCell(row, 1, "当前值", bold: true);
            AddCell(row, 2, "目标值", bold: true);
        }

        private void AddRow(string label, ulong? current, ulong? target, string? targetValue = null)
        {
            int row = ValueGrid.RowDefinitions.Count;
            ValueGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddCell(row, 0, label);
            AddCell(row, 1, current is ulong value ? FormatMb(value) : "未提供");

            string targetText = targetValue ?? (target is ulong targetMb ? FormatMb(targetMb) : "不变");
            bool changed = targetValue == null && current is ulong currentValue && target is ulong targetMb2 && currentValue != targetMb2;
            AddCell(row, 2, targetText, highlight: changed);
        }

        private void AddCell(int row, int column, string text, bool bold = false, bool highlight = false)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 10, 4),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            };
            if (highlight)
            {
                block.Foreground = System.Windows.Media.Brushes.SeaGreen;
            }
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            ValueGrid.Children.Add(block);
        }

        private static string FormatMb(ulong value)
        {
            double gb = value / 1024.0;
            return $"{value} MB（约 {gb:F1} GB）";
        }
    }
}
