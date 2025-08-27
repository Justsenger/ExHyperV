using System.Windows;
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
            DataContext = ViewModel;
            InitializeComponent();
            Unloaded += OnPageUnloaded;
        }
        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.Cleanup();
            Unloaded -= OnPageUnloaded;
        }
    }
}