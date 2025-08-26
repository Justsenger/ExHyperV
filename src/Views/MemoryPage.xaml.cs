// In Views/Pages/MemoryPage.xaml.cs
using ExHyperV.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace ExHyperV.Views.Pages
{
    public partial class MemoryPage : INavigableView<MemoryPageViewModel>
    {
        public MemoryPageViewModel ViewModel { get; }

        public MemoryPage()
        {
            ViewModel = new MemoryPageViewModel();
            // --- 修改这里 ---
            DataContext = ViewModel;
            // ----------------
            InitializeComponent();
        }
    }
}