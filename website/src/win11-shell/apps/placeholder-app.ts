import { getAppIconSvg } from '../icons';
import type { WinAppHostContext, WinAppLifecycle, WinAppModule } from '../types';

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

export const placeholderApp: WinAppModule = {
  mount(ctx: WinAppHostContext): WinAppLifecycle {
    const { container, manifest, pid } = ctx;
    const publisher = manifest.telemetry?.publisher ? ` · ${escapeHtml(manifest.telemetry.publisher)}` : '';

    container.innerHTML = `
      <div class="w11-stub">
        <div class="w11-stub-icon">${getAppIconSvg(manifest.iconKey, 56)}</div>
        <h2>${escapeHtml(manifest.name)}</h2>
        <p>${escapeHtml(manifest.description)}</p>
        <span class="w11-stub-badge">PID ${pid}${publisher} · 窗口壳已就绪</span>
      </div>
    `;

    return {};
  },
};
