using System;
using System.Collections.Generic;
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

        // 每个 VM 至多一个控制台窗口，按 vmId(GUID) 记账（issue #245）。仅 UI 线程访问，无需加锁。
        private static readonly Dictionary<string, ConsoleWindow> _consoles = new();

        /// <summary>打开虚拟机沉浸式控制台窗口；若该 VM 已有窗口则前置，不新开。</summary>
        public static void OpenConsoleWindow(string vmId, string vmName)
        {
            if (_consoles.TryGetValue(vmId, out var existing))
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }

            var window = new ConsoleWindow(vmId, vmName);
            _consoles[vmId] = window;
            window.Closed += (_, _) => _consoles.Remove(vmId);
            window.Show();
        }
    }
}
