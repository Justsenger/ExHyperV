using ExHyperV.ViewModels;

namespace ExHyperV.Views.Pages
{
    public partial class Setting
    {
        public Setting()
        {
            InitializeComponent();
            DataContext = new SettingsViewModel();
        }
    }
}