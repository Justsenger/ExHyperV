using ExHyperV.ViewModels;

namespace ExHyperV.Views
{
    public partial class MainPage
    {
        public MainPage()
        {
            InitializeComponent();
            this.DataContext = new MainPageViewModel();
        }
    }
}