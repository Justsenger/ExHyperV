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
            MessageTextBlock.Text = string.Format(Properties.Resources.Confirm_ShutdownMessage, vmName);
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