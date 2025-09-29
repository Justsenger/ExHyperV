// 文件路径: src/Views/Pages/CpuPage.xaml.cs

using ExHyperV.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ExHyperV.Views.Pages
{
    public partial class CpuPage : Page
    {
        public CpuPageViewModel ViewModel { get; }

        // ▼▼▼ 确保这是文件中唯一的 CpuPage() 构造函数 ▼▼▼
        public CpuPage()
        {
            ViewModel = new CpuPageViewModel();
            DataContext = ViewModel;
            InitializeComponent();

            this.Loaded += OnPageLoaded;
            this.Unloaded += OnPageUnloaded;
        }
        // ▲▲▲ 确保这是文件中唯一的 CpuPage() 构造函数 ▲▲▲

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            await ViewModel.InitializeAsync();
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.Cleanup();
            this.Loaded -= OnPageLoaded;
            this.Unloaded -= OnPageUnloaded;
        }
    }
}