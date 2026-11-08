
namespace ExHyperV.UI.Pages.Overview
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