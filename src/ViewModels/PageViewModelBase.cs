using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Interaction;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    /// <summary>
    /// 页面级 ViewModel 的共享基类:统一经 <see cref="Notifications"/> 门面发通知,
    /// 免去各 VM 各自写一份转调样板。
    /// </summary>
    public abstract class PageViewModelBase : ObservableObject
    {
        protected void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
            => Notifications.ShowSnackbar(title, message, appearance, icon);

        protected void ShowRestartPrompt(string message)
            => Notifications.ShowRestartPrompt(message);
    }
}
