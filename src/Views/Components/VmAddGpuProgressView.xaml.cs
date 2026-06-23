using System;
using System.Windows.Controls;

namespace ExHyperV.Views
{
    /// <summary>
    /// VmAddGpuProgressView.xaml 的交互逻辑
    /// </summary>
    public partial class VmAddGpuProgressView : UserControl
    {
        public VmAddGpuProgressView()
        {
            InitializeComponent();
        }
        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.ScrollToEnd();
            }
        }
    
    }
}
