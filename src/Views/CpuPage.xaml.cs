using ExHyperV.ViewModels;
using System.Windows.Controls;

namespace ExHyperV.Views.Pages
{
    public partial class CpuPage : Page
    {
        public CpuPage()
        {
            InitializeComponent();
            DataContext = CpuPageViewModel.Instance;
        }
    }
}