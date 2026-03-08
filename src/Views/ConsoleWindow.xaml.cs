using System.Windows;
using Wpf.Ui.Controls;
using ExHyperV.ViewModels;
using ExHyperV.Tools;

namespace ExHyperV.Views
{
    public partial class ConsoleWindow : FluentWindow
    {
        public ConsoleWindow(string vmId, string vmName)
        {
            var vm = new ConsoleViewModel { VmId = vmId, VmName = vmName };
            this.DataContext = vm;
            InitializeComponent();
            this.Title = vmName;

            // 窗口关闭时，强行让 Host 释放资源
            this.Closed += (s, e) =>
            {
                if (ConsoleHost.FindName("RdpHost") is MsRdpExHost host)
                {
                    host.Disconnect();
                }
            };
        }
    }
}