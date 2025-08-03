using ExHyperV.ViewModels;
using System.Windows.Controls;

namespace ExHyperV.Views.Pages
{
    /// <summary>
    /// StatusPage.xaml 的后台代码。
    /// 它的唯一职责是初始化并将其DataContext设置为StatusPageViewModel。
    /// </summary>
    public partial class StatusPage : Page
    {
        public StatusPage()
        {
            InitializeComponent();
            // 将此页面的数据上下文设置为ViewModel实例，以启用MVVM绑定。
            this.DataContext = new StatusPageViewModel();
        }
    }
}