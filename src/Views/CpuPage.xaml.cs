using System.Windows.Controls;
using ExHyperV.ViewModels; 

namespace ExHyperV.Views.Pages
{
    public partial class CpuPage : Page
    {
        public CpuPage()
        {
            InitializeComponent();
            this.DataContext = new CpuPageViewModel();
        }
    }
}