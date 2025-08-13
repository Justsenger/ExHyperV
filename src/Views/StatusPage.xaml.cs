using ExHyperV.ViewModels;
using System.Windows.Controls;

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