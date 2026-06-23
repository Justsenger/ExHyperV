using ExHyperV.ViewModels;

namespace ExHyperV.Views
{
    public partial class SettingsPage
    {
        public SettingsPage()
        {
            InitializeComponent();
            DataContext = new SettingsPageViewModel();
        }
    }
}