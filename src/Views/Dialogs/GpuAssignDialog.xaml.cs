using System.Windows.Controls;
using ExHyperV.Models;

namespace ExHyperV.Views
{
    /// <summary>GPU-PV「分配分区」对话框：只列出关机状态的虚拟机，选中结果由调用方读取。</summary>
    public partial class GpuAssignDialog : UserControl
    {
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
        }

        public string? SelectedVmName => VmCombo.SelectedItem as string;
    }
}
