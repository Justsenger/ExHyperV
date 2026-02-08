using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ExHyperV.Views.Components
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
