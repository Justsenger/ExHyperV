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

        // ===== 程序性赋值抑制 =====
        // 加载/失败回弹时会以代码给绑定属性赋值，这会触发 OnXxxChanged / PropertyChanged 处理器；
        // 若不区分就会被当成"用户操作"再写回引擎，造成回环或误写。各页面 VM 过去各自发明了
        // _isInternalUpdating / _suppressXxxApply 一堆同义布尔——此处统一为一个按页计数的抑制域。
        //   用法：using (SuppressApply()) { ...程序性赋值... }；处理器里 if (IsApplySuppressed) return;
        // 计数而非布尔 → 支持嵌套；IDisposable → 即使中途抛异常也保证还原（不会把抑制态永久卡住）。
        private int _applySuppressDepth;

        /// <summary>是否正处于程序性赋值中（OnXxxChanged 处理器应据此早退，不要把赋值当用户操作）。</summary>
        protected bool IsApplySuppressed => _applySuppressDepth > 0;

        /// <summary>开启一段"程序性赋值"区间，dispose 时结束。可嵌套；跨 Dispatcher 回调时手动持有、回调里 Dispose。</summary>
        protected IDisposable SuppressApply()
        {
            _applySuppressDepth++;
            return new ApplySuppressionToken(this);
        }

        private sealed class ApplySuppressionToken : IDisposable
        {
            private PageViewModelBase? _owner;
            public ApplySuppressionToken(PageViewModelBase owner) => _owner = owner;
            public void Dispose()
            {
                // 幂等：跨异步/回调路径可能被多次 Dispose，只在首次生效，避免计数被压到负数。
                var o = _owner;
                _owner = null;
                if (o != null && o._applySuppressDepth > 0) o._applySuppressDepth--;
            }
        }
    }
}
