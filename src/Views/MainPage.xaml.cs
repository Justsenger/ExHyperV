// ExHyperV.Views.Pages/MainPage.xaml.cs

using ExHyperV.ViewModels;

namespace ExHyperV.Views.Pages
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