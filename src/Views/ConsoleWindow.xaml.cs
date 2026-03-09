using System.Windows;
using Wpf.Ui.Controls;
using ExHyperV.ViewModels;

namespace ExHyperV.Views
{
    public partial class ConsoleWindow : FluentWindow
    {
        public ConsoleWindow(string vmId, string vmName)
        {
            // 1. 准备数据
            var vm = new ConsoleViewModel
            {
                VmId = vmId,
                VmName = vmName
            };

            // 2. 核心：必须在加载 UI 前绑定，否则 Host 控件拿不到初始 ID
            this.DataContext = vm;

            // 3. 初始化组件
            InitializeComponent();

            this.Title = vmName;
        }
    }
}