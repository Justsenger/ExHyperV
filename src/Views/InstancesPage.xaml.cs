using System.Windows.Controls;
using ExHyperV.ViewModels;
using ExHyperV.Services;

namespace ExHyperV.Views.Pages
{
    public partial class InstancesPage : Page
    {
        public InstancesPage()
        {
            InitializeComponent();
            this.DataContext = new InstancesPageViewModel(new InstancesService());
        }
    }
}