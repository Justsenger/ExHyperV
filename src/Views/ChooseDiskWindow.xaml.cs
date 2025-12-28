using ExHyperV.Models;
using ExHyperV.ViewModels;
using System.Collections.Generic;
using System.Windows;

namespace ExHyperV.Views
{
    public partial class ChooseDiskWindow
    {
        /// <summary>
        /// 暴露 ViewModel，方便 StoragePageViewModel 在确认后读取用户的选择结果
        /// </summary>
        public AddDiskViewModel ViewModel => (AddDiskViewModel)DataContext;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="vmName">虚拟机名称</param>
        /// <param name="generation">代次 (1或2)</param>
        /// <param name="currentStorage">当前已挂载的存储布局，用于计算可用插槽</param>
        public ChooseDiskWindow(string vmName, int generation, bool isVmRunning, List<VmStorageControllerInfo> currentStorage)
        {
            InitializeComponent();
            // 确保将运行状态传入 ViewModel
            DataContext = new AddDiskViewModel(vmName, generation, isVmRunning, currentStorage);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // 点击取消时，设置结果为 false 并关闭
            DialogResult = false;
            Close();
        }
    }
}