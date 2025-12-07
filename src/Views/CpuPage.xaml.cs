using System;
using System.Windows;
using System.Windows.Controls;
using ExHyperV.ViewModels; // 确保 ViewModel 命名空间正确

namespace ExHyperV.Views.Pages
{
    public partial class CpuPage : Page
    {
        // 将 ViewModel 存为字段，方便重复访问
        private readonly CpuPageViewModel _viewModel;

        public CpuPage()
        {
            InitializeComponent();

            // 从 DataContext 获取 ViewModel 实例
            // 确保你的 XAML 中有 <Page.DataContext><vm:CpuPageViewModel/></Page.DataContext>
            _viewModel = (CpuPageViewModel)this.DataContext;

            // 订阅“进入”和“离开”事件
            this.Loaded += CpuPage_Loaded;
            this.Unloaded += CpuPage_Unloaded;
        }

        // 当页面显示时（进入）
        private void CpuPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 调用 ViewModel 的启动方法
            _viewModel?.StartMonitoring();
        }

        // 当页面消失时（离开）
        private void CpuPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // 调用 ViewModel 的停止方法
            _viewModel?.StopMonitoring();
        }
    }
}