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
    /// <summary>
    /// 这个窗口就是用来让用户给虚拟机选一块物理GPU的对话框。
    /// 调用的时候用 .ShowDialog() 把它弹出来。
    /// </summary>
    public partial class ChooseGPUWindow : FluentWindow
    {
        /// <summary>
        /// 存一下虚拟机的名字，好在界面上显示“为 XXX 选择GPU”，让用户知道在给谁操作。
        /// </summary>
        public string Machinename { get; private set; }

        /// <summary>
        /// 绑定到界面上ListView的数据源。
        /// 用 ObservableCollection 是WPF的标配了，这样往里面加东西，界面上的列表会自动刷新。
        /// </summary>
        public ObservableCollection<GpuChoice> Items { get; } = new();

        /// <summary>
        /// 窗口关了以后，外面调用它的代码就从这个属性里拿用户选好的GPU信息。
        /// private set 确保只有这个窗口自己能修改这个值。
        /// </summary>
        public GpuChoice SelectedGpu { get; private set; }

        /// <summary>
        /// 构造函数，创建窗口实例时调用。
        /// </summary>
        /// <param name="vmname">虚拟机的名字。</param>
        /// <param name="hostGpuList">从主机上扫描到的所有GPU的列表。</param>
        public ChooseGPUWindow(string vmname, List<GPUInfo> hostGpuList)
        {
            Machinename = vmname;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            InitializeComponent();

            // DataContext设为this，这样XAML里就能直接绑定到这个文件里的“Machinename”和“Items”这些属性，省事。
            this.DataContext = this;

            // 不是所有GPU都能分给虚拟机，得是支持GPU-P（DDA，离散设备分配）的才行。
            // 这里的判断标准是看它有没有PnP设备路径（Pname），有路径的才是我们要的。
            var availableGpus = hostGpuList.Where(gpu => !string.IsNullOrEmpty(gpu.Pname));

            // 遍历筛选出来的GPU，把它们转成界面显示需要的数据格式（GpuChoice），然后加到列表里。
            foreach (var gpu in availableGpus)
            {
                Items.Add(new GpuChoice
                {
                    GPUname = gpu.Name,
                    Path = gpu.Pname,
                    Iconpath = Utils.GetGpuImagePath(gpu.Manu, gpu.Name),
                    Manu = gpu.Manu,
                    Id = gpu.InstanceId // 把这个唯一的ID存下来，后面要靠它干活。
                });
            }
        }

        /// <summary>
        /// 这是个专门给这个窗口的ListView用的数据类，算是“视图模型”（View Model）吧。
        /// 它把后端复杂的GPUInfo对象简化成界面需要展示的几个属性。
        /// </summary>
        public class GpuChoice
        {
            public string GPUname { get; set; }
            public string Path { get; set; }
            public string Iconpath { get; set; }
            public string Manu { get; set; }

            /// <summary>
            /// 关键！这个ID是Windows设备管理器里的“设备实例路径”，是硬件的唯一身份证。
            /// 后续调用 Add-VMAssignableDevice 之类的PowerShell命令全靠它来精确定位是哪块卡。
            /// </summary>
            public string Id { get; set; }
        }

        /// <summary>
        /// 用户点了“取消”按钮。
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // DialogResult设为false，外面调用ShowDialog()的地方就知道用户是取消了操作。
            this.DialogResult = false;
            this.Close();
        }

        /// <summary>
        /// 用户点了“确定”按钮。
        /// </summary>
        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            // 检查一下用户是不是真的选了东西。
            if (GpuListView.SelectedItem is GpuChoice selectedGpu)
            {
                // 把选中的项记下来，好让外面的代码能拿到。
                this.SelectedGpu = selectedGpu;
                // DialogResult设为true，表示用户成功完成了选择。
                this.DialogResult = true;
                this.Close();
            }
        }

        /// <summary>
        /// 当列表中的选中项变化时触发。
        /// </summary>
        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 列表里没选中任何东西时，“确定”按钮就是灰的，用不了。
            // 这样可以防止用户不选任何东西就点确定。
            ConfirmButton.IsEnabled = (sender as Wpf.Ui.Controls.ListView)?.SelectedItem != null;
        }
    }
}