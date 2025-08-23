using System.Windows.Controls;
using ExHyperV.ViewModels;

namespace ExHyperV.Views.Pages
{
    public partial class StatusPage : Page
    {
        public StatusPage()
        {
            InitializeComponent();
            this.DataContext = new StatusPageViewModel();
        }
    }
}