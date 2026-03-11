using System;
using System.Diagnostics;
using System.Windows.Controls;
using ExHyperV.ViewModels;

namespace ExHyperV.Views.Components
{
    public partial class VmConsoleView : UserControl
    {
        public VmConsoleView()
        {
            InitializeComponent();

            // 监听 DataContext 变化，这样当 ViewModel 加载时，View 自动绑定事件
            this.DataContextChanged += (s, e) =>
            {
                if (e.OldValue is ConsoleViewModel oldVm)
                    oldVm.SendCadRequested -= OnSendCadRequested;

                if (e.NewValue is ConsoleViewModel newVm)
                {
                    Debug.WriteLine("[View] 已成功订阅 ViewModel 的 CAD 事件");
                    newVm.SendCadRequested += OnSendCadRequested;
                }
            };

            RdpHost.OnRdpConnected += () =>
            {
                if (DataContext is ConsoleViewModel vm)
                    vm.IsLoading = false;
            };
        }

        private void OnSendCadRequested(object? sender, EventArgs e)
        {
            // 当 ViewModel 触发事件时，直接调用下面的公共方法
            this.SendCtrlAltDel();
        }

        // --- 核心：必须定义这个公共方法，外部(如Window)才能调用，且编译器才不会报错 ---
        public void SendCtrlAltDel()
        {
            Debug.WriteLine("[View] 正在转发 CAD 指令给底层 RdpHost");
            RdpHost?.SendCtrlAltDel();
        }
    }
}