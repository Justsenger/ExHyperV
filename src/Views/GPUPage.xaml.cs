using System.Windows.Controls;
using ExHyperV.ViewModels;

namespace ExHyperV.Views.Pages
{
    public partial class GPUPage : Page
    {
        public GPUPage()
        {
            InitializeComponent();
            this.DataContext = new GPUPageViewModel();
        }
    }
}