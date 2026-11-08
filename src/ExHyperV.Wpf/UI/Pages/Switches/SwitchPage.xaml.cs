using System.Windows.Controls;

namespace ExHyperV.UI.Pages.Switches
{
    public partial class SwitchPage : Page
    {
        public SwitchPage()
        {
            InitializeComponent();
            DataContext = new SwitchPageViewModel();
        }
    }
}