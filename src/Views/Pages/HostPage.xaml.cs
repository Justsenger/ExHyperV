using System.Windows.Controls;
using ExHyperV.ViewModels;

namespace ExHyperV.Views
{
    public partial class HostPage : Page
    {
        public HostPage()
        {
            InitializeComponent();
            this.DataContext = new HostPageViewModel();
        }
    }
}