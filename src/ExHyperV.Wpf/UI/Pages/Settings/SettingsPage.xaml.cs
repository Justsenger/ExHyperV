
namespace ExHyperV.UI.Pages.Settings
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