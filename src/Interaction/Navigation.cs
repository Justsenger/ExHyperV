using System;
using System.Windows;
using ExHyperV.Views;

namespace ExHyperV.Interaction
{
    /// <summary>
    /// 应用级导航/窗口门面：页面导航 + 打开独立窗口。
    /// VM 调用本类，内部统一访问 MainWindow（VM 不再认识具体窗口类型）。
    /// </summary>
    public static class Navigation
    {
        /// <summary>导航主窗口的 NavigationView 到指定页类型。</summary>
        public static void NavigateTo(Type pageType)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                mw.RootNavigation.Navigate(pageType);
        }

        /// <summary>打开虚拟机沉浸式控制台窗口。</summary>
        public static void OpenConsoleWindow(string vmId, string vmName)
            => new ConsoleWindow(vmId, vmName).Show();
    }
}
