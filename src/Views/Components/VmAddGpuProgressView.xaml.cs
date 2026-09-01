using System;
using System.Windows.Controls;

namespace ExHyperV.Views
{
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
