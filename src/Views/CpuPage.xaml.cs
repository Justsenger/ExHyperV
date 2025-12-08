using System.Windows;
using System.Windows.Controls;
using ExHyperV.ViewModels; 

namespace ExHyperV.Views.Pages
{
    public partial class CpuPage : Page
    {
        private readonly CpuPageViewModel _viewModel;

        public CpuPage()
        {
            InitializeComponent();
            _viewModel = (CpuPageViewModel)this.DataContext;
            this.Loaded += CpuPage_Loaded;
            this.Unloaded += CpuPage_Unloaded;
        }
        private void CpuPage_Loaded(object sender, RoutedEventArgs e)
        {
            _viewModel?.StartMonitoring();
        }
        private async void CpuPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                await _viewModel.StopMonitoringAsync();
            }
        }
    }
}