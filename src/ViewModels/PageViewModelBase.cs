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

        // 统一三态提示（标题固定状态词、图标固定、颜色固定；正文写具体内容）：
        //   成功 → 绿 · 对勾(CheckmarkCircle24) · 标题"操作成功"
        //   失败 → 红 · 错误(ErrorCircle24)     · 标题"操作失败"
        //   提示 → 黄 · 圆圈 i(Info24)           · 标题"提示"
        // 短语原本当标题的(如"删除失败")→ 改为正文前缀："删除失败：<详情>"。
        protected void ShowSuccess(string message)
            => ShowSnackbar(Properties.Resources.Status_Title_Success, message, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);

        protected void ShowError(string message)
            => ShowSnackbar(Properties.Resources.Error_Common_OpFail, message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);

        protected void ShowTip(string message)
            => ShowSnackbar(Properties.Resources.Status_Title_Info, message, ControlAppearance.Caution, SymbolRegular.Info24);

        protected void ShowRestartPrompt(string message)
            => Notifications.ShowRestartPrompt(message);
    }
}
