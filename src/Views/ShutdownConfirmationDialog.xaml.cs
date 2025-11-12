using System.Windows;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public enum ShutdownChoice
    {
        Cancel,
        ShutdownAndContinue
    }

    public partial class ShutdownConfirmationDialog : FluentWindow
    {
        public ShutdownChoice UserChoice { get; private set; } = ShutdownChoice.Cancel;

        public ShutdownConfirmationDialog(string vmName)
        {
            InitializeComponent();
            MessageTextBlock.Text = $"虚拟机 “{vmName}” 当前正在运行。\n\n" +
                                    "为了继续分配GPU，需要将此虚拟机关闭。\n\n" +
                                    "选择“强制关机并继续”将执行强制关闭操作（等同于断开电源）。";
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            UserChoice = ShutdownChoice.ShutdownAndContinue;
            this.DialogResult = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            UserChoice = ShutdownChoice.Cancel;
            this.DialogResult = false;
            this.Close();
        }
    }
}