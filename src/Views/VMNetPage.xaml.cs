using System.Windows.Controls;
using ExHyperV.ViewModels;

namespace ExHyperV.Views.Pages
{
    public partial class VMNetPage : Page
    {
        public VMNetPage()
        {
            InitializeComponent();
            DataContext = new VMNetViewModel();
        }
    }
}