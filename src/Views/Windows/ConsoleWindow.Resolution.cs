using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace ExHyperV.Views
{
    public partial class ConsoleWindow
    {
        // 保留最新请求，不把登录界面回报的固定尺寸当作用户偏好。
        private int _postLoginWidth;
        private int _postLoginHeight;
        private bool _enhancedLoggedIn;
        private bool _enhancedDesktopConfirmed;
        private bool _resolutionPending;
        private bool _resolutionRequestAccepted;
        private bool _resolutionUsesViewport;
        private int _resolutionRevision;
        private int _resolutionRetryTicks;
        private DispatcherTimer? _resolutionRetryTimer;
        private const int ResolutionRetryLimit = 10;

        private void OnEnhancedLoginCompleted()
        {
            if (_closing || !_vm.IsEnhancedMode || RdpHost.ConnectionState != 1 || _enhancedLoggedIn) return;
            _enhancedLoggedIn = true;
            _enhancedDesktopConfirmed = false;
            int width = _postLoginWidth > 0 ? _postLoginWidth : _vm.InitialEnhancedWidth;
            int height = _postLoginHeight > 0 ? _postLoginHeight : _vm.InitialEnhancedHeight;
            bool filled = _vm.IsFullScreen || WindowState == WindowState.Maximized;
            if (filled && TryGetEnhancedViewport(out int viewportWidth, out int viewportHeight))
            {
                width = viewportWidth;
                height = viewportHeight;
            }
            Debug.WriteLine($"[Rdp] 登录完成，重新协商 {width}x{height}, filled={filled}");
            RequestEnhancedResolution(width, height, followWindow: !filled, force: true);
            RefreshEnhancedDesktopSize();
            LayoutRdpHost();
        }

        private void RequestEnhancedResolution(int width, int height, bool followWindow, bool force = false)
        {
            if (_closing || !_vm.IsEnhancedMode || width <= 0 || height <= 0) return;
            bool sameTarget = width == _postLoginWidth && height == _postLoginHeight;
            // 布局重复通知不得不断延长重试期限；显式选择仍可重新请求。
            if (!force && !followWindow && sameTarget && (_resolutionPending || _resolutionRequestAccepted)) return;
            _resolutionRetryTimer?.Stop();
            _resolutionRevision++;
            _postLoginWidth = width;
            _postLoginHeight = height;
            _windowFollowsResolution = followWindow;
            _resolutionUsesViewport = !followWindow;
            _resolutionRetryTicks = 0;
            _resolutionRequestAccepted = false;
            _resolutionPending = force || !_enhancedDesktopConfirmed || width != _vm.CurrentWidth || height != _vm.CurrentHeight;
            if (!_resolutionPending)
            {
                _windowFollowsResolution = false;
                return;
            }

            SendEnhancedResolution();
            // 登录前只尝试一次；登录后有限核对尺寸，仅接口失败时重发。
            if (_enhancedLoggedIn && _resolutionPending)
            {
                if (_resolutionRetryTimer == null)
                {
                    _resolutionRetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                    {
                        Interval = TimeSpan.FromSeconds(1)
                    };
                    _resolutionRetryTimer.Tick += OnResolutionRetry;
                }
                _resolutionRetryTimer.Start();
            }
        }

        private void SendEnhancedResolution()
        {
            if (RdpHost.ConnectionState != 1) return;
            Debug.WriteLine($"[Rdp] 请求分辨率 {_postLoginWidth}x{_postLoginHeight}, retry={_resolutionRetryTicks}");
            _resolutionRequestAccepted = RdpHost.Resize(_postLoginWidth, _postLoginHeight, GetDpiScale());
        }

        private void OnResolutionRetry(object? sender, EventArgs e)
        {
            if (_closing || !_vm.IsEnhancedMode || RdpHost.ConnectionState != 1)
            {
                ResetEnhancedResolution();
                return;
            }
            if (!_resolutionPending || ++_resolutionRetryTicks >= ResolutionRetryLimit)
            {
                _resolutionRetryTimer?.Stop();
                if (_resolutionPending)
                    Debug.WriteLine($"[Rdp] 分辨率未确认，停止重试 {_postLoginWidth}x{_postLoginHeight}");
                _resolutionPending = false;
                _windowFollowsResolution = false;
                return;
            }
            // 移动/缩放过程中不争夺窗口；最小化也不请求无效的画面尺寸。
            if (_userResizing || WindowState == WindowState.Minimized) return;
            if (_resolutionUsesViewport && (_vm.IsFullScreen || WindowState == WindowState.Maximized)
                && TryGetEnhancedViewport(out int width, out int height)
                && (width != _postLoginWidth || height != _postLoginHeight))
            {
                RequestEnhancedResolution(width, height, followWindow: false);
                return;
            }
            int revision = _resolutionRevision;
            RefreshEnhancedDesktopSize();
            if (revision != _resolutionRevision || !_resolutionPending) return;
            // 成功提交后等待事件/只读查询确认，绝不因没有尺寸变化事件而重复提交。
            if (!_resolutionRequestAccepted) SendEnhancedResolution();
        }

        private void RefreshEnhancedDesktopSize()
        {
            if (!_enhancedLoggedIn || !RdpHost.TryGetRemoteDesktopSize(out int width, out int height)) return;
            ApplyRemoteDesktopSize(width, height);
        }

        private void ApplyRemoteDesktopSize(int width, int height)
        {
            if (_closing || width <= 0 || height <= 0) return;
            bool changed = width != _vm.CurrentWidth || height != _vm.CurrentHeight;
            bool wasConfirmed = _enhancedDesktopConfirmed;
            _vm.CurrentWidth = width;
            _vm.CurrentHeight = height;
            bool targetConfirmed = ConfirmEnhancedResolution(width, height);
            if (_windowFollowsResolution && targetConfirmed)
            {
                if (_vm.IsEnhancedMode && !_vm.IsFullScreen && WindowState == WindowState.Normal)
                    FitToResolution(width, height);
                int revision = _resolutionRevision;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (revision == _resolutionRevision) _windowFollowsResolution = false;
                }), DispatcherPriority.Background);
            }
            // 只读核对得到相同尺寸时，不反复重排控件或切换缩放。
            if (changed || wasConfirmed != _enhancedDesktopConfirmed || targetConfirmed) LayoutRdpHost();
        }

        private bool ConfirmEnhancedResolution(int width, int height)
        {
            // 登录后的尺寸事件或远端显示器边界读回，才允许进行原生像素匹配。
            if (_enhancedLoggedIn && width > 0 && height > 0) _enhancedDesktopConfirmed = true;
            if (!_resolutionPending || width != _postLoginWidth || height != _postLoginHeight) return false;
            _resolutionPending = false;
            _resolutionRequestAccepted = false;
            _resolutionRetryTimer?.Stop();
            Debug.WriteLine($"[Rdp] 客户机确认分辨率 {width}x{height}");
            // 不清除目标：首次尺寸事件可能早于登录，登录后仍需重发最新目标。
            return true;
        }

        private void ResetEnhancedResolution()
        {
            _resolutionRetryTimer?.Stop();
            _resolutionRevision++;
            _enhancedLoggedIn = false;
            _enhancedDesktopConfirmed = false;
            _resolutionPending = false;
            _resolutionRequestAccepted = false;
            _windowFollowsResolution = false;
            _resolutionUsesViewport = false;
            _postLoginWidth = _postLoginHeight = 0;
        }
    }
}
