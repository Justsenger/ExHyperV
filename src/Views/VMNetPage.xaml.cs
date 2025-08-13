using ExHyperV.ViewModels;
using System.Windows.Controls;

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