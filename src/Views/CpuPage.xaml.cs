using ExHyperV.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ExHyperV.Views.Pages
{
    public partial class CpuPage : Page
    {
        public CpuPage()
        {
            InitializeComponent();
            DataContext = CpuPageViewModel.Instance;
            this.SizeChanged += CpuPage_SizeChanged;
        }

        private void CpuPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (CpuPageViewModel.Instance.IsLoading) return;
            this.InvalidateMeasure();
            this.UpdateLayout();
        }
    }
}