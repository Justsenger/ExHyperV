// /Views/ChooseGPUWindow.xaml.cs

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ExHyperV.Models;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public partial class ChooseGPUWindow : FluentWindow
    {
        public string Machinename { get; private set; }
        public ObservableCollection<GpuChoice> Items { get; } = new();

        // 这个属性用于在关闭窗口后，让调用者能获取到用户选择的GPU信息
        public GpuChoice SelectedGpu { get; private set; }

        public ChooseGPUWindow(string vmname, List<GPUInfo> hostGpuList)
        {
            Machinename = vmname;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            InitializeComponent();
            this.DataContext = this;

            // 从传入的GPU列表中，筛选出那些可以被分区的GPU
            var availableGpus = hostGpuList.Where(gpu => !string.IsNullOrEmpty(gpu.Pname));
            foreach (var gpu in availableGpus)
            {
                Items.Add(new GpuChoice
                {
                    GPUname = gpu.Name,
                    Path = gpu.Pname,
                    Iconpath = Utils.GetGpuImagePath(gpu.Manu, gpu.Name),
                    Manu = gpu.Manu,
                    Id = gpu.InstanceId // <<<--- 新增这一行赋值
                });
            }
        }

        // 这是一个窗口内部使用的数据类，用于在ListView中显示GPU选项
        public class GpuChoice
        {
            public string GPUname { get; set; }
            public string Path { get; set; }
            public string Iconpath { get; set; }
            public string Manu { get; set; }
            public string Id { get; set; } // <<<--- 重新添加这一行
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // 当用户点击“取消”时，设置窗口的对话框结果为 false
            // 这样调用者就知道用户没有做出选择
            this.DialogResult = false;
            this.Close();
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            // 确保用户有选中项
            if (GpuListView.SelectedItem is GpuChoice selectedGpu)
            {
                // 将选中的项保存在 SelectedGpu 属性中
                this.SelectedGpu = selectedGpu;
                // 设置窗口的对话框结果为 true，表示用户已确认选择
                this.DialogResult = true;
                this.Close();
            }
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 当用户在列表中选择或取消选择时，启用或禁用“确定”按钮
            ConfirmButton.IsEnabled = (sender as Wpf.Ui.Controls.ListView)?.SelectedItem != null;
        }
    }
}