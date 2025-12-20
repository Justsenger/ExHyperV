using ExHyperV.ViewModels;
using System.Windows.Controls;

namespace ExHyperV.Views.Pages
{
    public partial class MemoryPage : Page
    {
        public MemoryPage()
        {
            InitializeComponent();
            DataContext = MemoryPageViewModel.Instance;

            Loaded += (s, e) =>
            {
                MemoryPageViewModel.Instance.StartTimer();
            };

            Unloaded += (s, e) =>
            {
                MemoryPageViewModel.Instance.StopTimer();
            };
        }
    }
}