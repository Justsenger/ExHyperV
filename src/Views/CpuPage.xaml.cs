using ExHyperV.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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