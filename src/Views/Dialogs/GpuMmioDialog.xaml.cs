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

            HeaderText.Text = string.Format(Properties.Resources.GpuPv_Mmio_Header, vmName);
            AddHeaderRow();
            AddRow(Properties.Resources.GpuPv_Mmio_Base, current.HighBaseMb, target.BaseMb);
            AddRow(Properties.Resources.GpuPv_Mmio_High, current.HighSizeMb, target.HighSizeMb);
            AddRow(Properties.Resources.GpuPv_Mmio_Low, current.LowSizeMb, target.LowSizeMb);
            AddRow("GuestControlledCacheTypes", null, null, targetValue: Properties.Resources.GpuPv_Mmio_Enabled);

            NoteText.Text = ceilingKnown
                ? Properties.Resources.GpuPv_Mmio_Note_Known
                : Properties.Resources.GpuPv_Mmio_Note_Unknown;
        }

        private void AddHeaderRow()
        {
            int row = ValueGrid.RowDefinitions.Count;
            ValueGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(row, 0, Properties.Resources.GpuPv_Mmio_Col_Item, bold: true);
            AddCell(row, 1, Properties.Resources.GpuPv_Mmio_Col_Current, bold: true);
            AddCell(row, 2, Properties.Resources.GpuPv_Mmio_Col_Target, bold: true);
        }

        private void AddRow(string label, ulong? current, ulong? target, string? targetValue = null)
        {
            int row = ValueGrid.RowDefinitions.Count;
            ValueGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddCell(row, 0, label);
            AddCell(row, 1, current is ulong value ? FormatMb(value) : Properties.Resources.GpuPv_Mmio_NotProvided);

            string targetText = targetValue ?? (target is ulong targetMb ? FormatMb(targetMb) : Properties.Resources.GpuPv_Mmio_Unchanged);
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
            return string.Format(Properties.Resources.GpuPv_Mmio_FormatMb, value, $"{gb:F1}");
        }
    }
}
