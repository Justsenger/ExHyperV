import { getAppIconSvg } from '../icons';
import type { DesktopIconSize, WinAppHostContext, WinAppLifecycle, WinAppModule } from '../types';

export const settingsApp: WinAppModule = {
  mount(ctx: WinAppHostContext): WinAppLifecycle {
    const { container, manifest, shell } = ctx;

    function render() {
      const theme = shell.getTheme();
      const iconSize = shell.getIconSize();
      container.innerHTML = `
        <div class="w11-stub w11-stub--panel">
          <div class="w11-stub-header">
            <div class="w11-stub-icon">${getAppIconSvg(manifest.iconKey, 36)}</div>
            <div>
              <h2>个性化与桌面设置</h2>
              <p>基础外壳外观选项，修改后实时同步到桌面与全部已打开应用。</p>
            </div>
          </div>
          <div class="w11-stub-group">
            <div class="w11-stub-row">
              <div>
                <strong>系统主题模式</strong>
                <span>切换 Windows 11 暗色 / 浅色 Bloom 外观</span>
              </div>
              <div class="w11-pill-group">
                <button type="button" class="w11-pill-btn ${theme === 'dark' ? 'is-active' : ''}" data-action="theme-dark">深色</button>
                <button type="button" class="w11-pill-btn ${theme === 'light' ? 'is-active' : ''}" data-action="theme-light">浅色</button>
              </div>
            </div>
            <div class="w11-stub-row">
              <div>
                <strong>桌面图标尺寸</strong>
                <span>对应桌面右键“查看”中的图标大小</span>
              </div>
              <div class="w11-pill-group">
                <button type="button" class="w11-pill-btn ${iconSize === 'large' ? 'is-active' : ''}" data-action="icon-large">大</button>
                <button type="button" class="w11-pill-btn ${iconSize === 'medium' ? 'is-active' : ''}" data-action="icon-medium">中</button>
                <button type="button" class="w11-pill-btn ${iconSize === 'small' ? 'is-active' : ''}" data-action="icon-small">小</button>
              </div>
            </div>
            <div class="w11-stub-row">
              <div>
                <strong>重置桌面布局</strong>
                <span>恢复图标排列、固定应用与默认深色模式</span>
              </div>
              <button type="button" class="w11-pill-btn" data-action="reset-layout">恢复默认</button>
            </div>
          </div>
        </div>
      `;
    }

    function handleClick(e: Event) {
      const target = e.target as HTMLElement | null;
      const btn = target?.closest<HTMLElement>('[data-action]');
      if (!btn) return;
      const action = btn.dataset.action;
      if (action === 'theme-dark') shell.setTheme('dark');
      else if (action === 'theme-light') shell.setTheme('light');
      else if (action === 'icon-large' || action === 'icon-medium' || action === 'icon-small') {
        shell.setIconSize(action.replace('icon-', '') as DesktopIconSize);
      } else if (action === 'reset-layout') {
        shell.resetDesktopLayout();
      }
    }

    container.addEventListener('click', handleClick);
    render();

    return {
      onThemeChange: () => render(),
      onShellPrefsChange: () => render(),
      destroy: () => {
        container.removeEventListener('click', handleClick);
      },
    };
  },
};
