using ExHyperV.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExHyperV.Views.Pages
{
    public partial class CpuPage : Page
    {
        public CpuPage()
        {
            InitializeComponent();
            DataContext = CpuPageViewModel.Instance;

            // 监听加载事件
            this.Loaded += CpuPage_Loaded;
        }

        private void CpuPage_Loaded(object sender, RoutedEventArgs e)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(this);
            while (parent != null)
            {
                if (parent is ScrollViewer parentScrollViewer)
                {
                    parentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    break;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
        }
    }
}