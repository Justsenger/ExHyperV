using ExHyperV.ViewModels;
using System.Windows.Controls;

namespace ExHyperV.Views.Pages
{
    public partial class StoragePage : Page
    {
        public StoragePage()
        {
            InitializeComponent();
            DataContext = StoragePageViewModel.Instance;
        }
    }
}