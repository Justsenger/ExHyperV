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

        private void OnPreferencesLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is HostPageViewModel viewModel)
                viewModel.Preferences.Reload();
        }
    }
}
