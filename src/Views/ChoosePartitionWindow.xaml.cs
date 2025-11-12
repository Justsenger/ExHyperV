// 文件: ExHyperV/Views/ChoosePartitionWindow.xaml.cs

using ExHyperV.Models; // 确保有这个 using
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls; // 确保有这个 using，并且基类正确

namespace ExHyperV.Views
{
    // 确保基类是 FluentWindow
    public partial class ChoosePartitionWindow : FluentWindow
    {
        // 公共属性，用于从外部获取用户最终选择的分区 (解决 CS1061)
        public PartitionInfo SelectedPartition { get; private set; }

        // 接收一个分区列表作为参数的构造函数 (解决 CS1729)
        public ChoosePartitionWindow(List<PartitionInfo> partitions)
        {
            InitializeComponent();

            // 将传入的分区列表设置为 ListView 的数据源
            PartitionListView.ItemsSource = partitions;
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 当用户在列表中选择一项时
            if (PartitionListView.SelectedItem is PartitionInfo selected)
            {
                // 将选择结果存起来
                SelectedPartition = selected;
                // 启用“确定”按钮
                ConfirmButton.IsEnabled = true;
            }
            else
            {
                // 如果没有选择任何项，则禁用“确定”按钮
                ConfirmButton.IsEnabled = false;
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            // 当用户点击“确定”时，设置 DialogResult 为 true 并关闭窗口
            this.DialogResult = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // 当用户点击“取消”时，设置 DialogResult 为 false 并关闭窗口
            this.DialogResult = false;
            this.Close();
        }
    }
}