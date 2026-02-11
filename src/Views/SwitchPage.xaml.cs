using System.Windows.Controls;
using ExHyperV.ViewModels;

namespace ExHyperV.Views.Pages
{
    public partial class SwitchPage : Page
    {
        public SwitchPage()
        {
            InitializeComponent();
            DataContext = new VMNetViewModel();
        }
    }
}