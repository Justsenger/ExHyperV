import { collectDeviceHardwareReport, type DeviceProbeReport } from './device-probe';
import { getAppIconSvg, UI_ICONS } from './icons';
import { APP_REGISTRY, getManifestById } from './registry';
import { createShellStore } from './store';
import { mountTaskManager, type TaskManagerController } from './task-manager';
import type { DesktopIconSize, SnapZone, WindowInstance } from './types';
import { WALLPAPERS } from './wallpaper';

interface GridMetrics {
  colW: number;
  rowH: number;
  iconPx: number;
  pad: number;
}

const GRID_METRICS: Record<DesktopIconSize, GridMetrics> = {
  large: { colW: 126, rowH: 136, iconPx: 96, pad: 12 },
  medium: { colW: 90, rowH: 94, iconPx: 48, pad: 10 },
  small: { colW: 76, rowH: 78, iconPx: 32, pad: 10 },
};

interface ContextMenuItem {
  type?: 'divider';
  label?: string;
  icon?: string;
  checked?: boolean;
  checkedType?: 'dot' | 'check';
  disabled?: boolean;
  onSelect?: () => void;
  submenu?: ContextMenuItem[];
}

export function initWin11Desktop(root: HTMLElement) {
  const store = createShellStore();

  const wallpaperEl = root.querySelector<HTMLElement>('[data-wallpaper]')!;
  const brightnessOverlayEl = root.querySelector<HTMLElement>('[data-brightness-overlay]')!;
  const nightLightOverlayEl = root.querySelector<HTMLElement>('[data-nightlight-overlay]')!;
  const desktopAreaEl = root.querySelector<HTMLElement>('[data-desktop-area]')!;
  const iconsLayerEl = root.querySelector<HTMLElement>('[data-icons-layer]')!;
  const marqueeEl = root.querySelector<HTMLElement>('[data-marquee]')!;
  const windowsLayerEl = root.querySelector<HTMLElement>('[data-windows-layer]')!;
  const snapPreviewEl = root.querySelector<HTMLElement>('[data-snap-preview]')!;
  const taskbarEl = root.querySelector<HTMLElement>('[data-taskbar]')!;
  const taskbarAppsEl = root.querySelector<HTMLElement>('[data-taskbar-apps]')!;
  const startMenuEl = root.querySelector<HTMLElement>('[data-start-menu]')!;
  const startSearchEl = root.querySelector<HTMLInputElement>('[data-start-search]')!;
  const startGridEl = root.querySelector<HTMLElement>('[data-start-grid]')!;
  const quickSettingsEl = root.querySelector<HTMLElement>('[data-quick-settings]')!;
  const calendarFlyoutEl = root.querySelector<HTMLElement>('[data-calendar-flyout]')!;
  const trayOverflowEl = root.querySelector<HTMLElement>('[data-tray-overflow]')!;
  const contextMenuEl = root.querySelector<HTMLElement>('[data-context-menu]')!;
  const clockTimeEl = root.querySelector<HTMLElement>('[data-clock-time]')!;
  const clockDateEl = root.querySelector<HTMLElement>('[data-clock-date]')!;
  const imeBtnEl = root.querySelector<HTMLElement>('[data-ime-btn]')!;

  let lastRenderedTheme: string | null = null;
  let searchQuery = '';
  let showDesktopIcons = true;
  let latestProbeReport: DeviceProbeReport | null = null;
  let probeInFlight = false;

  function escapeHtml(text: string): string {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  async function runHardwareProbe(force = false) {
    if (probeInFlight && !force) return;
    probeInFlight = true;
    try {
      const report = await collectDeviceHardwareReport();
      latestProbeReport = report;
      const formatted = JSON.stringify(report, null, 2);
      windowsLayerEl.querySelectorAll<HTMLElement>('[data-tm-probe-output]').forEach((el) => {
        el.textContent = formatted;
      });
      taskManagers.forEach((ctrl) => ctrl.setReport(report));
    } finally {
      probeInFlight = false;
    }
  }

  const windowElements = new Map<number, HTMLElement>();
  const taskManagers = new Map<number, TaskManagerController>();

  function applyDesktopScale() {
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    if (vw < 1024 || vh < 580) {
      const rdpW = 1920;
      const rdpH = vw > vh ? Math.max(720, Math.round((vh * rdpW) / vw)) : 1080;
      const scale = vw > vh ? vw / rdpW : Math.min(vw / rdpW, vh / rdpH);
      root.classList.add('is-rdp-scaled');
      root.style.setProperty('--w11-rdp-w', `${rdpW}px`);
      root.style.setProperty('--w11-rdp-h', `${rdpH}px`);
      root.style.setProperty('--w11-scale', String(scale));
    } else {
      root.classList.remove('is-rdp-scaled');
      root.style.removeProperty('--w11-rdp-w');
      root.style.removeProperty('--w11-rdp-h');
      root.style.removeProperty('--w11-scale');
    }
  }

  function getShellMetrics() {
    const rect = root.getBoundingClientRect();
    const width = root.offsetWidth || window.innerWidth;
    const height = root.offsetHeight || window.innerHeight;
    const scaleX = width > 0 ? rect.width / width : 1;
    const scaleY = height > 0 ? rect.height / height : 1;
    return { rect, width, height, scaleX: scaleX || 1, scaleY: scaleY || 1 };
  }

  function getViewport() {
    return {
      width: root.offsetWidth || window.innerWidth,
      height: root.offsetHeight || window.innerHeight,
    };
  }

  function toShellCoords(clientX: number, clientY: number) {
    const { rect, width, height, scaleX, scaleY } = getShellMetrics();
    return {
      x: (clientX - rect.left) / scaleX,
      y: (clientY - rect.top) / scaleY,
      width,
      height,
      scaleX,
      scaleY,
      rect,
    };
  }

  function getMaxGridRows(size: DesktopIconSize): number {
    const m = GRID_METRICS[size];
    const availH = Math.max(m.rowH, getViewport().height - 48 - m.pad * 2);
    return Math.max(1, Math.floor(availH / m.rowH));
  }

  function getMaxGridCols(size: DesktopIconSize): number {
    const m = GRID_METRICS[size];
    return Math.max(1, Math.floor((getViewport().width - m.pad * 2) / m.colW));
  }

  function clampCell(col: number, row: number, size: DesktopIconSize) {
    const maxCols = getMaxGridCols(size);
    const maxRows = getMaxGridRows(size);
    return {
      col: Math.max(0, Math.min(maxCols - 1, col)),
      row: Math.max(0, Math.min(maxRows - 1, row)),
    };
  }

  function resolveVisibleIconCell(
    rawCol: number,
    rawRow: number,
    size: DesktopIconSize,
    occupied: Set<string>,
  ) {
    const maxCols = getMaxGridCols(size);
    const maxRows = getMaxGridRows(size);
    let col = Math.max(0, rawCol);
    let row = Math.max(0, rawRow);

    if (row >= maxRows) {
      col += Math.floor(row / maxRows);
      row = row % maxRows;
    }
    col = Math.min(maxCols - 1, col);
    row = Math.min(maxRows - 1, row);

    const key = (c: number, r: number) => `${c},${r}`;
    if (!occupied.has(key(col, row))) {
      occupied.add(key(col, row));
      return { col, row };
    }

    for (let dc = 0; dc < maxCols; dc++) {
      const c = (col + dc) % maxCols;
      const startR = dc === 0 ? row : 0;
      for (let r = startR; r < maxRows; r++) {
        if (!occupied.has(key(c, r))) {
          occupied.add(key(c, r));
          return { col: c, row: r };
        }
      }
      for (let r = 0; r < startR; r++) {
        if (!occupied.has(key(c, r))) {
          occupied.add(key(c, r));
          return { col: c, row: r };
        }
      }
    }

    return { col, row };
  }

  function renderWallpaper() {
    const { theme } = store.state;
    if (lastRenderedTheme === theme) return;
    lastRenderedTheme = theme;
    document.documentElement.setAttribute('data-theme', theme);
    wallpaperEl.style.backgroundImage = `url("${WALLPAPERS[theme]}")`;

    windowsLayerEl.querySelectorAll<HTMLIFrameElement>('iframe.w11-win-iframe').forEach((frame) => {
      try {
        if (frame.contentWindow) {
          frame.contentWindow.postMessage({ type: 'w11-theme', theme }, window.location.origin);
        }
      } catch {
        // Ignore cross-origin iframes
      }
    });
  }

  function renderOverlays() {
    const dimOpacity = ((100 - store.state.brightness) / 100) * 0.72;
    brightnessOverlayEl.style.opacity = dimOpacity.toFixed(3);
    nightLightOverlayEl.style.opacity = store.state.toggles.nightLight ? '0.24' : '0';
  }

  function toggleDesktopFullscreen() {
    if (document.fullscreenElement) {
      void document.exitFullscreen?.().catch(() => {});
    } else {
      void (document.documentElement.requestFullscreen?.() ?? root.requestFullscreen?.())?.catch(() => {});
    }
  }

  function launchShellItem(appId: string) {
    if (appId === 'fullscreen') {
      toggleDesktopFullscreen();
      return;
    }
    store.openApp(appId, getViewport());
  }

  function renderDesktopIcons() {
    const { iconSize, iconPositions, selectedIconIds, autoArrange, alignToGrid } = store.state;
    const m = GRID_METRICS[iconSize];
    iconsLayerEl.setAttribute('data-size', iconSize);

    const desktopApps = APP_REGISTRY.filter((app) => app.onDesktop);
    const maxCols = getMaxGridCols(iconSize);
    const maxRows = getMaxGridRows(iconSize);
    const occupied = new Set<string>();
    iconsLayerEl.innerHTML = '';

    const packedPositions = new Map<string, { col: number; row: number }>();
    if (autoArrange) {
      const ordered = [...desktopApps].sort((a, b) => {
        const pa = iconPositions[a.id] ?? { col: 0, row: 0 };
        const pb = iconPositions[b.id] ?? { col: 0, row: 0 };
        if (pa.col !== pb.col) return pa.col - pb.col;
        if (pa.row !== pb.row) return pa.row - pb.row;
        return desktopApps.indexOf(a) - desktopApps.indexOf(b);
      });
      ordered.forEach((app, idx) => {
        packedPositions.set(app.id, {
          col: Math.min(maxCols - 1, Math.floor(idx / maxRows)),
          row: idx % maxRows,
        });
      });
    }

    desktopApps.forEach((app) => {
      const rawPos = iconPositions[app.id] ?? { col: 0, row: 0 };
      let pos: { col: number; row: number };
      if (autoArrange) {
        pos = packedPositions.get(app.id) ?? { col: 0, row: 0 };
      } else if (!alignToGrid) {
        pos = {
          col: Math.max(0, Math.min(maxCols - 1, rawPos.col)),
          row: Math.max(0, Math.min(maxRows - 1, rawPos.row)),
        };
      } else {
        pos = resolveVisibleIconCell(Math.round(rawPos.col), Math.round(rawPos.row), iconSize, occupied);
      }
      const isSelected = selectedIconIds.includes(app.id);

      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = `w11-desktop-icon${isSelected ? ' is-selected' : ''}`;
      btn.dataset.appId = app.id;
      btn.style.left = `${m.pad + pos.col * m.colW}px`;
      btn.style.top = `${m.pad + pos.row * m.rowH}px`;
      btn.style.width = `${m.colW - 6}px`;
      btn.style.height = `${m.rowH - 4}px`;
      const label = app.id === 'fullscreen' && document.fullscreenElement ? '退出全屏' : app.name;
      btn.innerHTML = `
        <span class="w11-desktop-icon__glyph">${getAppIconSvg(app.iconKey, m.iconPx)}</span>
        <span class="w11-desktop-icon__label">${label}</span>
      `;
      iconsLayerEl.appendChild(btn);
    });
  }

  function buildWindowContent(win: WindowInstance): string {
    if (win.entryUrl) {
      let src = win.entryUrl;
      if (src.startsWith('/')) {
        const sep = src.includes('?') ? '&' : '?';
        src = `${src}${sep}theme=${encodeURIComponent(store.state.theme)}`;
      }
      return `<iframe class="w11-win-iframe" src="${src}" title="${win.title}"></iframe>`;
    }

    if (win.appId === 'settings') {
      const { theme, iconSize } = store.state;
      return `
        <div class="w11-stub w11-stub--panel">
          <div class="w11-stub-header">
            <div class="w11-stub-icon">${getAppIconSvg(win.iconKey, 36)}</div>
            <div>
              <h2>个性化与桌面设置</h2>
              <p>基础外壳外观选项，修改后实时同步到桌面。</p>
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

    if (win.appId === 'task-manager') {
      return '';
    }

    const manifest = getManifestById(win.appId);
    return `
      <div class="w11-stub">
        <div class="w11-stub-icon">${getAppIconSvg(win.iconKey, 56)}</div>
        <h2>${win.title}</h2>
        <p>${manifest?.description ?? '基础窗口容器占位'}</p>
        <span class="w11-stub-badge">PID ${win.pid} · 窗口壳已就绪</span>
      </div>
    `;
  }

  function renderWindows() {
    const activeSet = new Set(store.state.windows.map((w) => w.pid));
    for (const [pid, el] of windowElements.entries()) {
      if (!activeSet.has(pid)) {
        taskManagers.get(pid)?.destroy();
        taskManagers.delete(pid);
        el.remove();
        windowElements.delete(pid);
      }
    }

    store.state.windows.forEach((win) => {
      let winEl = windowElements.get(win.pid);
      if (!winEl) {
        winEl = document.createElement('section');
        winEl.className = 'w11-window';
        winEl.dataset.pid = String(win.pid);
        winEl.dataset.app = win.appId;
        winEl.innerHTML = `
          <header class="w11-win-titlebar" data-win-drag="${win.pid}">
            <div class="w11-win-title">
              <span class="w11-win-title-icon">${getAppIconSvg(win.iconKey, 16)}</span>
              <span class="w11-win-title-text">${win.title}</span>
            </div>
            <div class="w11-win-controls">
              <button type="button" class="w11-win-ctrl" data-win-min="${win.pid}" aria-label="最小化">${UI_ICONS.minimize}</button>
              <button type="button" class="w11-win-ctrl" data-win-max="${win.pid}" aria-label="最大化">${UI_ICONS.maximize}</button>
              <button type="button" class="w11-win-ctrl is-close" data-win-close="${win.pid}" aria-label="关闭">${UI_ICONS.close}</button>
            </div>
          </header>
          <div class="w11-win-body" data-win-body="${win.pid}"></div>
          <div class="w11-resize-handle is-n" data-resize="n"></div>
          <div class="w11-resize-handle is-s" data-resize="s"></div>
          <div class="w11-resize-handle is-e" data-resize="e"></div>
          <div class="w11-resize-handle is-w" data-resize="w"></div>
          <div class="w11-resize-handle is-nw" data-resize="nw"></div>
          <div class="w11-resize-handle is-ne" data-resize="ne"></div>
          <div class="w11-resize-handle is-sw" data-resize="sw"></div>
          <div class="w11-resize-handle is-se" data-resize="se"></div>
        `;
        windowsLayerEl.appendChild(winEl);
        windowElements.set(win.pid, winEl);
      }

      const isActive = store.state.activePid === win.pid;
      winEl.classList.toggle('is-active', isActive);
      winEl.classList.toggle('is-minimized', win.minimized);
      winEl.classList.toggle('is-maximized', win.maximized);
      winEl.classList.toggle('is-seamless-app', Boolean(win.seamlessTitlebar));

      winEl.style.left = `${win.x}px`;
      winEl.style.top = `${win.y}px`;
      winEl.style.width = `${win.width}px`;
      winEl.style.height = `${win.height}px`;
      winEl.style.zIndex = String(win.zIndex);

      const maxBtn = winEl.querySelector<HTMLElement>(`[data-win-max="${win.pid}"]`);
      if (maxBtn) {
        maxBtn.innerHTML = win.maximized ? UI_ICONS.restore : UI_ICONS.maximize;
        maxBtn.setAttribute('aria-label', win.maximized ? '向下还原' : '最大化');
      }

      const bodyEl = winEl.querySelector<HTMLElement>(`[data-win-body="${win.pid}"]`);
      if (bodyEl) {
        if (win.appId === 'task-manager') {
          if (!taskManagers.has(win.pid)) {
            if (!latestProbeReport && !probeInFlight) {
              void runHardwareProbe();
            }
            const ctrl = mountTaskManager({
              container: bodyEl,
              getWindows: () => store.state.windows,
              getInitialReport: () => latestProbeReport,
              onReportUpdated: (next) => {
                latestProbeReport = next;
              },
              onCloseWindow: (pid) => store.closeWindow(pid),
              onOpenNewTask: () => store.openApp('exhyperv', getViewport()),
            });
            taskManagers.set(win.pid, ctrl);
          }
        } else if (!win.entryUrl || bodyEl.childElementCount === 0) {
          bodyEl.innerHTML = buildWindowContent(win);
        }
      }
    });
  }

  function renderTaskbar() {
    const startBtn = taskbarEl.querySelector<HTMLElement>('[data-taskbar-start]');
    startBtn?.classList.toggle('is-active', store.state.activeFlyout === 'start');

    const trayQuickBtn = taskbarEl.querySelector<HTMLElement>('[data-taskbar-quick]');
    trayQuickBtn?.classList.toggle('is-active', store.state.activeFlyout === 'quickSettings');

    const clockBtn = taskbarEl.querySelector<HTMLElement>('[data-taskbar-clock]');
    clockBtn?.classList.toggle('is-active', store.state.activeFlyout === 'calendar');

    const overflowBtn = taskbarEl.querySelector<HTMLElement>('[data-taskbar-overflow]');
    overflowBtn?.classList.toggle('is-active', store.state.activeFlyout === 'trayOverflow');

    imeBtnEl.textContent = store.state.imeMode;

    const orderedIds = [...store.state.pinnedTaskbarIds];
    store.state.windows.forEach((win) => {
      if (!orderedIds.includes(win.appId)) {
        orderedIds.push(win.appId);
      }
    });

    taskbarAppsEl.innerHTML = '';
    orderedIds.forEach((appId) => {
      const manifest = getManifestById(appId);
      if (!manifest) return;
      const win = store.state.windows.find((w) => w.appId === appId);
      const isOpen = Boolean(win);
      const isFocused = Boolean(win && !win.minimized && store.state.activePid === win.pid);

      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = `w11-tb-item${isOpen ? ' is-open' : ''}${isFocused ? ' is-focused' : ''}`;
      btn.dataset.tbApp = appId;
      btn.title = manifest.name;
      btn.innerHTML = `
        <span class="w11-tb-glyph">${getAppIconSvg(manifest.iconKey, 24)}</span>
        <span class="w11-tb-indicator"></span>
      `;
      taskbarAppsEl.appendChild(btn);
    });
  }

  function renderStartMenu() {
    const open = store.state.activeFlyout === 'start';
    startMenuEl.classList.toggle('is-open', open);

    const q = searchQuery.trim().toLowerCase();
    const list = APP_REGISTRY.filter((app) => {
      if (q) {
        return app.name.toLowerCase().includes(q) || app.id.toLowerCase().includes(q);
      }
      return app.pinnedToStart !== false;
    });

    startGridEl.innerHTML = '';
    if (list.length === 0) {
      startGridEl.innerHTML = `<div class="w11-start-empty">未找到匹配的项目</div>`;
      return;
    }

    list.forEach((app) => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'w11-start-item';
      btn.dataset.launchApp = app.id;
      btn.innerHTML = `
        <span class="w11-start-item__icon">${getAppIconSvg(app.iconKey, 32)}</span>
        <span class="w11-start-item__name">${app.name}</span>
      `;
      startGridEl.appendChild(btn);
    });
  }

  function renderQuickSettings() {
    const open = store.state.activeFlyout === 'quickSettings';
    quickSettingsEl.classList.toggle('is-open', open);

    const toggleMap: Record<string, boolean> = {
      wifi: store.state.toggles.wifi,
      bluetooth: store.state.toggles.bluetooth,
      airplane: store.state.toggles.airplane,
      nightLight: store.state.toggles.nightLight,
      theme: store.state.theme === 'dark',
    };

    quickSettingsEl.querySelectorAll<HTMLElement>('[data-qs-toggle]').forEach((el) => {
      const key = el.dataset.qsToggle ?? '';
      el.classList.toggle('is-active', Boolean(toggleMap[key]));
    });

    const brightInput = quickSettingsEl.querySelector<HTMLInputElement>('[data-qs-brightness]');
    if (brightInput) {
      brightInput.value = String(store.state.brightness);
      const pct = ((store.state.brightness - 35) / 65) * 100;
      brightInput.style.setProperty('--track-pct', `${pct}%`);
    }
    const volInput = quickSettingsEl.querySelector<HTMLInputElement>('[data-qs-volume]');
    if (volInput) {
      volInput.value = String(store.state.volume);
      volInput.style.setProperty('--track-pct', `${store.state.volume}%`);
    }
  }

  function renderCalendarFlyout() {
    const open = store.state.activeFlyout === 'calendar';
    calendarFlyoutEl.classList.toggle('is-open', open);
    if (!open) return;

    const now = new Date();
    const year = now.getFullYear();
    const month = now.getMonth();
    const today = now.getDate();

    const firstWeekday = (new Date(year, month, 1).getDay() + 6) % 7;
    const daysInMonth = new Date(year, month + 1, 0).getDate();
    const daysInPrev = new Date(year, month, 0).getDate();

    const cells: string[] = [];
    for (let i = firstWeekday - 1; i >= 0; i -= 1) {
      cells.push(`<span class="w11-cal-day is-muted">${daysInPrev - i}</span>`);
    }
    for (let d = 1; d <= daysInMonth; d += 1) {
      cells.push(`<span class="w11-cal-day${d === today ? ' is-today' : ''}">${d}</span>`);
    }
    const remainder = (7 - (cells.length % 7)) % 7;
    for (let d = 1; d <= remainder; d += 1) {
      cells.push(`<span class="w11-cal-day is-muted">${d}</span>`);
    }

    const weekdayNames = ['星期日', '星期一', '星期二', '星期三', '星期四', '星期五', '星期六'];
    calendarFlyoutEl.innerHTML = `
      <div class="w11-cal-header">
        <div class="w11-cal-today">${year}年${month + 1}月${today}日 ${weekdayNames[now.getDay()]}</div>
        <div class="w11-cal-month">${year}年${month + 1}月</div>
      </div>
      <div class="w11-cal-weekdays">
        <span>一</span><span>二</span><span>三</span><span>四</span><span>五</span><span>六</span><span>日</span>
      </div>
      <div class="w11-cal-grid">${cells.join('')}</div>
    `;
  }

  function renderTrayOverflow() {
    trayOverflowEl.classList.toggle('is-open', store.state.activeFlyout === 'trayOverflow');
  }

  function renderAll() {
    renderWallpaper();
    renderOverlays();
    renderDesktopIcons();
    renderWindows();
    renderTaskbar();
    renderStartMenu();
    renderQuickSettings();
    renderCalendarFlyout();
    renderTrayOverflow();
  }

  function updateClock() {
    const now = new Date();
    const hh = String(now.getHours()).padStart(2, '0');
    const mm = String(now.getMinutes()).padStart(2, '0');
    clockTimeEl.textContent = `${hh}:${mm}`;
    clockDateEl.textContent = `${now.getFullYear()}/${now.getMonth() + 1}/${now.getDate()}`;
  }

  function closeContextMenu() {
    contextMenuEl.classList.remove('is-open');
    contextMenuEl.innerHTML = '';
  }

  function renderMenuItems(items: ContextMenuItem[]): HTMLElement {
    const list = document.createElement('div');
    list.className = 'w11-ctx-list';
    let lastMenuPointerType = 'mouse';

    items.forEach((item) => {
      if (item.type === 'divider') {
        const div = document.createElement('div');
        div.className = 'w11-ctx-divider';
        list.appendChild(div);
        return;
      }

      const row = document.createElement('div');
      row.className = `w11-ctx-item${item.submenu ? ' has-sub' : ''}${item.disabled ? ' is-disabled' : ''}`;
      const checkGlyph = item.checked
        ? item.checkedType === 'check'
          ? '<span class="w11-ctx-checkglyph">✓</span>'
          : '<span class="w11-ctx-dot">•</span>'
        : '';
      const leftGlyph = item.icon ?? checkGlyph;
      row.innerHTML = `
        <span class="w11-ctx-icon">${leftGlyph}</span>
        <span class="w11-ctx-label">${item.label ?? ''}</span>
        <span class="w11-ctx-arrow">${item.submenu ? '›' : ''}</span>
      `;

      const closeSiblingSubmenus = () => {
        list.querySelectorAll('.w11-ctx-item.is-sub-open').forEach((el) => el.classList.remove('is-sub-open'));
      };

      const positionSubmenu = (sub: HTMLElement) => {
        sub.style.left = '';
        sub.style.right = '';
        sub.style.top = '';
        const { rect: shellRect, scaleX, scaleY } = getShellMetrics();
        const rect = sub.getBoundingClientRect();
        if (rect.right > shellRect.right - 8 * scaleX) {
          sub.style.left = 'auto';
          sub.style.right = 'calc(100% + 2px)';
          const flippedRect = sub.getBoundingClientRect();
          if (flippedRect.left < shellRect.left + 8 * scaleX) {
            sub.style.right = 'auto';
            const rowRect = row.getBoundingClientRect();
            const clampedLeft =
              (Math.max(shellRect.left + 8 * scaleX, shellRect.right - flippedRect.width - 8 * scaleX) - rowRect.left) /
              scaleX;
            sub.style.left = `${clampedLeft}px`;
          }
        }
        const maxBottom = shellRect.bottom - 54 * scaleY;
        if (rect.bottom > maxBottom) {
          const shift = (rect.bottom - maxBottom) / scaleY;
          sub.style.top = `${-3 - shift}px`;
        }
      };

      row.addEventListener('pointerdown', (e) => {
        lastMenuPointerType = e.pointerType || 'mouse';
      });

      row.addEventListener('pointerenter', (e) => {
        if (e.pointerType !== 'mouse') return;
        closeSiblingSubmenus();
        if (item.submenu) {
          row.classList.add('is-sub-open');
          const sub = row.querySelector<HTMLElement>(':scope > .w11-ctx-submenu');
          if (sub) positionSubmenu(sub);
        }
      });

      if (item.submenu) {
        const sub = renderMenuItems(item.submenu);
        sub.classList.add('w11-ctx-submenu');
        row.appendChild(sub);
        row.addEventListener('click', (e) => {
          if ((e.target as HTMLElement).closest('.w11-ctx-submenu')) return;
          e.stopPropagation();
          const wasOpen = row.classList.contains('is-sub-open');
          closeSiblingSubmenus();
          if (lastMenuPointerType === 'mouse' || !wasOpen) {
            row.classList.add('is-sub-open');
            positionSubmenu(sub);
          }
        });
      } else if (!item.disabled) {
        row.addEventListener('click', (e) => {
          e.stopPropagation();
          closeContextMenu();
          item.onSelect?.();
        });
      }

      list.appendChild(row);
    });

    return list;
  }

  function openContextMenu(x: number, y: number, items: ContextMenuItem[]) {
    store.closeFlyouts();
    contextMenuEl.innerHTML = '';
    contextMenuEl.appendChild(renderMenuItems(items));
    contextMenuEl.classList.add('is-open');

    const localPos = toShellCoords(x, y);
    const menuW = contextMenuEl.offsetWidth || 174;
    const menuH = contextMenuEl.offsetHeight || 203;
    const { width: vw, height } = getViewport();
    const vh = height - 48;
    const clampedX = Math.max(8, Math.min(localPos.x, Math.max(8, vw - menuW - 8)));
    const clampedY = Math.max(8, Math.min(localPos.y, Math.max(8, vh - menuH - 8)));
    contextMenuEl.style.left = `${clampedX}px`;
    contextMenuEl.style.top = `${clampedY}px`;
  }

  let refreshStartTimer = 0;
  let refreshEndTimer = 0;

  function triggerDesktopRefresh() {
    window.clearTimeout(refreshStartTimer);
    window.clearTimeout(refreshEndTimer);
    iconsLayerEl.classList.remove('is-refreshing');

    refreshStartTimer = window.setTimeout(() => {
      iconsLayerEl.classList.add('is-refreshing');
      renderDesktopIcons();
      refreshEndTimer = window.setTimeout(() => {
        iconsLayerEl.classList.remove('is-refreshing');
      }, 150);
    }, 65);
  }

  // --- Desktop Icon Selection, Drag-to-Cell, Double-click & Marquee ---
  let lastTouchTap: { appId: string; time: number } | null = null;
  let lastTouchLongPressAt = 0;

  window.addEventListener(
    'contextmenu',
    (e) => {
      const target = e.target as HTMLElement | null;
      if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) return;
      e.preventDefault();
    },
    { capture: true },
  );

  desktopAreaEl.addEventListener(
    'touchstart',
    (e) => {
      e.preventDefault();
    },
    { passive: false },
  );

  function openIconContextMenu(appId: string, clientX: number, clientY: number) {
    if (!store.state.selectedIconIds.includes(appId)) {
      store.selectIcons([appId]);
    }
    const pinned = store.state.pinnedTaskbarIds.includes(appId);
    openContextMenu(clientX, clientY, [
      { label: appId === 'fullscreen' && document.fullscreenElement ? '退出全屏' : '打开', onSelect: () => launchShellItem(appId) },
      {
        label: pinned ? '从任务栏取消固定' : '固定到任务栏',
        onSelect: () => store.togglePinToTaskbar(appId),
      },
      { type: 'divider' },
      {
        label: '重置图标排列',
        icon: UI_ICONS.refresh,
        onSelect: () => store.sortDesktopIcons('default', getMaxGridRows(store.state.iconSize)),
      },
    ]);
  }

  function openDesktopContextMenu(clientX: number, clientY: number) {
    openContextMenu(clientX, clientY, [
      {
        label: '查看(V)',
        submenu: [
          {
            label: '大图标(R)',
            checked: store.state.iconSize === 'large',
            onSelect: () => store.setIconSize('large', getMaxGridRows('large')),
          },
          {
            label: '中等图标(M)',
            checked: store.state.iconSize === 'medium',
            onSelect: () => store.setIconSize('medium', getMaxGridRows('medium')),
          },
          {
            label: '小图标(N)',
            checked: store.state.iconSize === 'small',
            onSelect: () => store.setIconSize('small', getMaxGridRows('small')),
          },
          { type: 'divider' },
          {
            label: '自动排列图标(A)',
            checked: store.state.autoArrange,
            checkedType: 'check',
            onSelect: () => store.toggleAutoArrange(getMaxGridRows(store.state.iconSize)),
          },
          {
            label: '将图标与网格对齐(I)',
            checked: store.state.alignToGrid,
            checkedType: 'check',
            onSelect: () =>
              store.toggleAlignToGrid(
                getMaxGridCols(store.state.iconSize),
                getMaxGridRows(store.state.iconSize),
              ),
          },
          { type: 'divider' },
          {
            label: '显示桌面图标(D)',
            checked: showDesktopIcons,
            checkedType: 'check',
            onSelect: () => {
              showDesktopIcons = !showDesktopIcons;
              iconsLayerEl.style.visibility = showDesktopIcons ? '' : 'hidden';
              iconsLayerEl.style.pointerEvents = showDesktopIcons ? '' : 'none';
            },
          },
        ],
      },
      {
        label: '排序方式(O)',
        submenu: [
          {
            label: '名称(N)',
            onSelect: () => store.sortDesktopIcons('name', getMaxGridRows(store.state.iconSize)),
          },
          {
            label: '大小',
            onSelect: () => store.sortDesktopIcons('default', getMaxGridRows(store.state.iconSize)),
          },
          {
            label: '项目类型',
            onSelect: () => store.sortDesktopIcons('default', getMaxGridRows(store.state.iconSize)),
          },
          {
            label: '修改日期',
            onSelect: () => store.sortDesktopIcons('default', getMaxGridRows(store.state.iconSize)),
          },
        ],
      },
      { label: '刷新(E)', onSelect: triggerDesktopRefresh },
      { type: 'divider' },
      { label: '粘贴(P)', disabled: true },
      {
        label: '在终端中打开(T)',
        icon: getAppIconSvg('terminal', 14),
        onSelect: () => store.openApp('terminal', getViewport()),
      },
      { type: 'divider' },
      {
        label: '新建(W)',
        submenu: [
          { label: '文件夹(F)', icon: UI_ICONS.folder, disabled: true },
          { label: '快捷方式(S)', icon: UI_ICONS.shortcut, disabled: true },
        ],
      },
      { type: 'divider' },
      {
        label: '显示设置(D)',
        icon: UI_ICONS.displaySettings,
        onSelect: () => store.openApp('settings', getViewport()),
      },
      {
        label: '个性化(R)',
        icon: UI_ICONS.personalize,
        onSelect: () => store.openApp('settings', getViewport()),
      },
    ]);
  }

  desktopAreaEl.addEventListener('pointerdown', (e) => {
    if (e.button !== 0) return;
    const target = e.target as HTMLElement;
    const iconBtn = target.closest<HTMLElement>('.w11-desktop-icon');

    closeContextMenu();
    store.closeFlyouts();

    if (iconBtn) {
      const appId = iconBtn.dataset.appId!;
      if (e.ctrlKey || e.metaKey) {
        store.toggleIconSelection(appId);
      } else if (!store.state.selectedIconIds.includes(appId)) {
        store.selectIcons([appId]);
      }

      const startX = e.clientX;
      const startY = e.clientY;
      const origLeft = parseFloat(iconBtn.style.left);
      const origTop = parseFloat(iconBtn.style.top);
      let moved = false;
      let longPressed = false;
      const longPressTimer =
        e.pointerType === 'touch'
          ? window.setTimeout(() => {
              if (!moved) {
                longPressed = true;
                lastTouchLongPressAt = Date.now();
                openIconContextMenu(appId, startX, startY);
              }
            }, 550)
          : 0;

      const onMove = (me: PointerEvent) => {
        if (me.pointerId !== e.pointerId || longPressed) return;
        const { scaleX, scaleY } = getShellMetrics();
        const dx = (me.clientX - startX) / scaleX;
        const dy = (me.clientY - startY) / scaleY;
        if (!moved && Math.hypot(me.clientX - startX, me.clientY - startY) < 6) return;
        moved = true;
        if (longPressTimer) window.clearTimeout(longPressTimer);
        iconBtn.classList.add('is-dragging');
        iconBtn.style.left = `${origLeft + dx}px`;
        iconBtn.style.top = `${origTop + dy}px`;
      };

      const cleanup = () => {
        if (longPressTimer) window.clearTimeout(longPressTimer);
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onUp);
        window.removeEventListener('pointercancel', onCancel);
      };

      const onCancel = (ce: PointerEvent) => {
        if (ce.pointerId !== e.pointerId) return;
        cleanup();
        if (moved) {
          iconBtn.classList.remove('is-dragging');
          iconBtn.style.left = `${origLeft}px`;
          iconBtn.style.top = `${origTop}px`;
        }
      };

      const onUp = (ue: PointerEvent) => {
        if (ue.pointerId !== e.pointerId) return;
        cleanup();
        if (longPressed) return;
        if (!moved) {
          if (ue.pointerType !== 'mouse') {
            const now = Date.now();
            if (lastTouchTap && lastTouchTap.appId === appId && now - lastTouchTap.time < 450) {
              lastTouchTap = null;
              launchShellItem(appId);
            } else {
              lastTouchTap = { appId, time: now };
            }
          }
          return;
        }
        iconBtn.classList.remove('is-dragging');
        const m = GRID_METRICS[store.state.iconSize];
        const { scaleX, scaleY } = getShellMetrics();
        const targetLeft = origLeft + (ue.clientX - startX) / scaleX;
        const targetTop = origTop + (ue.clientY - startY) / scaleY;
        const rawCol = (targetLeft - m.pad) / m.colW;
        const rawRow = (targetTop - m.pad) / m.rowH;
        store.moveIconToCell(
          appId,
          rawCol,
          rawRow,
          getMaxGridCols(store.state.iconSize),
          getMaxGridRows(store.state.iconSize),
        );
      };

      window.addEventListener('pointermove', onMove);
      window.addEventListener('pointerup', onUp);
      window.addEventListener('pointercancel', onCancel);
      return;
    }

    // Marquee selection on empty desktop
    if (!e.ctrlKey && !e.metaKey) {
      store.selectIcons([]);
    }
    const startX = e.clientX;
    const startY = e.clientY;
    const startLocal = toShellCoords(startX, startY);
    let active = false;
    let longPressed = false;
    const longPressTimer =
      e.pointerType === 'touch'
        ? window.setTimeout(() => {
            if (!active) {
              longPressed = true;
              lastTouchLongPressAt = Date.now();
              openDesktopContextMenu(startX, startY);
            }
          }, 550)
        : 0;

    const onMove = (me: PointerEvent) => {
      if (me.pointerId !== e.pointerId || longPressed) return;
      const rawDx = me.clientX - startX;
      const rawDy = me.clientY - startY;
      if (!active && Math.hypot(rawDx, rawDy) < 6) return;
      active = true;
      if (longPressTimer) window.clearTimeout(longPressTimer);
      const curLocal = toShellCoords(me.clientX, me.clientY);
      const left = Math.min(startLocal.x, curLocal.x);
      const top = Math.min(startLocal.y, curLocal.y);
      const width = Math.abs(curLocal.x - startLocal.x);
      const height = Math.abs(curLocal.y - startLocal.y);
      marqueeEl.style.display = 'block';
      marqueeEl.style.left = `${left}px`;
      marqueeEl.style.top = `${top}px`;
      marqueeEl.style.width = `${width}px`;
      marqueeEl.style.height = `${height}px`;

      const vLeft = Math.min(startX, me.clientX);
      const vTop = Math.min(startY, me.clientY);
      const mRect = { left: vLeft, top: vTop, right: vLeft + Math.abs(rawDx), bottom: vTop + Math.abs(rawDy) };
      const hitIds: string[] = [];
      iconsLayerEl.querySelectorAll<HTMLElement>('.w11-desktop-icon').forEach((el) => {
        const r = el.getBoundingClientRect();
        const intersects = !(r.right < mRect.left || r.left > mRect.right || r.bottom < mRect.top || r.top > mRect.bottom);
        if (intersects && el.dataset.appId) hitIds.push(el.dataset.appId);
      });
      store.selectIcons(hitIds);
    };

    const onUp = (ue: PointerEvent) => {
      if (ue.pointerId !== e.pointerId) return;
      if (longPressTimer) window.clearTimeout(longPressTimer);
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onUp);
      marqueeEl.style.display = 'none';
    };

    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onUp);
  });

  desktopAreaEl.addEventListener('dblclick', (e) => {
    const iconBtn = (e.target as HTMLElement).closest<HTMLElement>('.w11-desktop-icon');
    if (!iconBtn?.dataset.appId) return;
    launchShellItem(iconBtn.dataset.appId);
  });

  // --- Context Menus (Desktop, Icon, Taskbar) ---
  desktopAreaEl.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    if (Date.now() - lastTouchLongPressAt < 800) return;
    const iconBtn = (e.target as HTMLElement).closest<HTMLElement>('.w11-desktop-icon');
    if (iconBtn?.dataset.appId) {
      openIconContextMenu(iconBtn.dataset.appId, e.clientX, e.clientY);
      return;
    }
    openDesktopContextMenu(e.clientX, e.clientY);
  });

  taskbarEl.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    openContextMenu(e.clientX, e.clientY, [
      {
        label: '任务管理器',
        onSelect: () => store.openApp('task-manager', getViewport()),
      },
      {
        label: '显示桌面',
        onSelect: () => store.toggleShowDesktop(),
      },
      { type: 'divider' },
      {
        label: '桌面与任务栏设置',
        onSelect: () => store.openApp('settings', getViewport()),
      },
    ]);
  });

  // --- Window Interactions (Focus, Controls, Drag/Snap, 8-way Resize) ---
  function updateSnapOverlay(zone: SnapZone) {
    if (zone === 'none') {
      snapPreviewEl.classList.remove('is-visible');
      return;
    }
    const { width: vw, height } = getViewport();
    const vh = height - 48;
    snapPreviewEl.classList.add('is-visible');
    if (zone === 'maximize') {
      snapPreviewEl.style.left = '8px';
      snapPreviewEl.style.top = '8px';
      snapPreviewEl.style.width = `${vw - 16}px`;
      snapPreviewEl.style.height = `${vh - 16}px`;
    } else if (zone === 'left') {
      snapPreviewEl.style.left = '8px';
      snapPreviewEl.style.top = '8px';
      snapPreviewEl.style.width = `${Math.round(vw / 2) - 12}px`;
      snapPreviewEl.style.height = `${vh - 16}px`;
    } else {
      snapPreviewEl.style.left = `${Math.round(vw / 2) + 4}px`;
      snapPreviewEl.style.top = '8px';
      snapPreviewEl.style.width = `${Math.round(vw / 2) - 12}px`;
      snapPreviewEl.style.height = `${vh - 16}px`;
    }
  }

  windowsLayerEl.addEventListener('pointerdown', (e) => {
    closeContextMenu();
    const target = e.target as HTMLElement;
    const winEl = target.closest<HTMLElement>('.w11-window');
    if (!winEl) return;
    const pid = Number(winEl.dataset.pid);
    store.focusWindow(pid);

    if (target.closest('.w11-win-controls')) return;

    const resizeHandle = target.closest<HTMLElement>('.w11-resize-handle');
    if (resizeHandle) {
      const dir = resizeHandle.dataset.resize ?? '';
      const win = store.state.windows.find((w) => w.pid === pid);
      if (!win || win.maximized) return;
      e.preventDefault();

      const startX = e.clientX;
      const startY = e.clientY;
      const orig = { x: win.x, y: win.y, w: win.width, h: win.height };
      const minW = 340;
      const minH = 240;
      windowsLayerEl.classList.add('is-interacting');

      const onMove = (me: PointerEvent) => {
        const { scaleX, scaleY } = getShellMetrics();
        const dx = (me.clientX - startX) / scaleX;
        const dy = (me.clientY - startY) / scaleY;
        let { x, y, w, h } = orig;

        if (dir.includes('e')) w = Math.max(minW, orig.w + dx);
        if (dir.includes('s')) h = Math.max(minH, orig.h + dy);
        if (dir.includes('w')) {
          const nw = Math.max(minW, orig.w - dx);
          x = orig.x + (orig.w - nw);
          w = nw;
        }
        if (dir.includes('n')) {
          const nh = Math.max(minH, orig.h - dy);
          y = Math.max(0, orig.y + (orig.h - nh));
          h = nh;
        }

        win.x = x;
        win.y = y;
        win.width = w;
        win.height = h;
        winEl.style.left = `${x}px`;
        winEl.style.top = `${y}px`;
        winEl.style.width = `${w}px`;
        winEl.style.height = `${h}px`;
      };

      const onUp = () => {
        windowsLayerEl.classList.remove('is-interacting');
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onUp);
        window.removeEventListener('pointercancel', onUp);
      };

      window.addEventListener('pointermove', onMove);
      window.addEventListener('pointerup', onUp);
      window.addEventListener('pointercancel', onUp);
      return;
    }

    const dragBar = target.closest<HTMLElement>('[data-win-drag]');
    if (!dragBar || e.button !== 0) return;
    if (e.pointerType !== 'mouse') e.preventDefault();

    const win = store.state.windows.find((w) => w.pid === pid);
    if (!win) return;

    const startX = e.clientX;
    const startY = e.clientY;
    let origX = win.x;
    let origY = win.y;
    let moved = false;
    let snapZone: SnapZone = 'none';

    const onMove = (me: PointerEvent) => {
      if (!moved && Math.hypot(me.clientX - startX, me.clientY - startY) < 4) return;
      if (!moved) {
        moved = true;
        windowsLayerEl.classList.add('is-interacting');
      }

      const localPtr = toShellCoords(me.clientX, me.clientY);
      const dx = (me.clientX - startX) / localPtr.scaleX;
      const dy = (me.clientY - startY) / localPtr.scaleY;
      const { width: vw, height } = getViewport();

      if (win.maximized) {
        const restoreW = win.restoreBounds?.width ?? 820;
        const restoreH = win.restoreBounds?.height ?? 520;
        const ratio = localPtr.x / Math.max(1, vw);
        win.maximized = false;
        win.width = restoreW;
        win.height = restoreH;
        origX = Math.round(localPtr.x - restoreW * ratio);
        origY = Math.max(0, localPtr.y - 18);
        winEl.classList.remove('is-maximized');
        winEl.style.width = `${restoreW}px`;
        winEl.style.height = `${restoreH}px`;
      }

      win.x = origX + dx;
      win.y = Math.max(0, Math.min(height - 84, origY + dy));
      winEl.style.left = `${win.x}px`;
      winEl.style.top = `${win.y}px`;

      if (localPtr.y <= 8) snapZone = 'maximize';
      else if (localPtr.x <= 10) snapZone = 'left';
      else if (localPtr.x >= vw - 10) snapZone = 'right';
      else snapZone = 'none';

      updateSnapOverlay(snapZone);
    };

    const onUp = () => {
      windowsLayerEl.classList.remove('is-interacting');
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onUp);
      updateSnapOverlay('none');
      if (!moved) return;

      const { width: vw, height } = getViewport();
      const vh = height - 48;
      if (snapZone === 'maximize') {
        store.toggleMaximizeWindow(pid, getViewport());
      } else if (snapZone === 'left') {
        win.restoreBounds = { x: origX, y: origY, width: win.width, height: win.height };
        win.x = 0;
        win.y = 0;
        win.width = Math.round(vw / 2);
        win.height = vh;
        store.notify();
      } else if (snapZone === 'right') {
        win.restoreBounds = { x: origX, y: origY, width: win.width, height: win.height };
        win.x = Math.round(vw / 2);
        win.y = 0;
        win.width = vw - Math.round(vw / 2);
        win.height = vh;
        store.notify();
      }
    };

    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onUp);
  });

  windowsLayerEl.addEventListener('dblclick', (e) => {
    const dragBar = (e.target as HTMLElement).closest<HTMLElement>('[data-win-drag]');
    if (!dragBar || (e.target as HTMLElement).closest('.w11-win-controls')) return;
    store.toggleMaximizeWindow(Number(dragBar.dataset.winDrag), getViewport());
  });

  windowsLayerEl.addEventListener('click', (e) => {
    const target = e.target as HTMLElement;
    const minBtn = target.closest<HTMLElement>('[data-win-min]');
    if (minBtn) {
      store.minimizeWindow(Number(minBtn.dataset.winMin));
      return;
    }
    const maxBtn = target.closest<HTMLElement>('[data-win-max]');
    if (maxBtn) {
      store.toggleMaximizeWindow(Number(maxBtn.dataset.winMax), getViewport());
      return;
    }
    const closeBtn = target.closest<HTMLElement>('[data-win-close]');
    if (closeBtn) {
      store.closeWindow(Number(closeBtn.dataset.winClose));
      return;
    }

    const endTaskBtn = target.closest<HTMLElement>('[data-end-task]');
    if (endTaskBtn) {
      store.closeWindow(Number(endTaskBtn.dataset.endTask));
      return;
    }

    const actionBtn = target.closest<HTMLElement>('[data-action]');
    if (actionBtn) {
      const action = actionBtn.dataset.action;
      if (action === 'theme-dark') store.setTheme('dark');
      else if (action === 'theme-light') store.setTheme('light');
      else if (action === 'icon-large') store.setIconSize('large', getMaxGridRows('large'));
      else if (action === 'icon-medium') store.setIconSize('medium', getMaxGridRows('medium'));
      else if (action === 'icon-small') store.setIconSize('small', getMaxGridRows('small'));
      else if (action === 'reset-layout') store.resetDesktopLayout();
      else if (action === 'reprobe-hw') {
        windowsLayerEl.querySelectorAll<HTMLElement>('[data-tm-probe-output]').forEach((el) => {
          el.textContent = '正在重新探测...';
        });
        void runHardwareProbe(true);
      } else if (action === 'copy-hw-json' && latestProbeReport) {
        void navigator.clipboard?.writeText(JSON.stringify(latestProbeReport, null, 2)).then(() => {
          actionBtn.textContent = '已复制';
          window.setTimeout(() => {
            actionBtn.textContent = '复制 JSON';
          }, 1200);
        });
      }
    }
  });

  // --- Taskbar & Flyout Events ---
  taskbarEl.addEventListener('click', (e) => {
    closeContextMenu();
    const target = e.target as HTMLElement;

    if (target.closest('[data-taskbar-start]')) {
      store.setActiveFlyout('start');
      return;
    }
    if (target.closest('[data-taskbar-search]')) {
      store.state.activeFlyout = 'start';
      store.notify();
      window.setTimeout(() => startSearchEl.focus(), 30);
      return;
    }
    if (target.closest('[data-taskbar-quick]')) {
      store.setActiveFlyout('quickSettings');
      return;
    }
    if (target.closest('[data-taskbar-clock]')) {
      store.setActiveFlyout('calendar');
      return;
    }
    if (target.closest('[data-taskbar-overflow]')) {
      store.setActiveFlyout('trayOverflow');
      return;
    }
    if (target.closest('[data-ime-btn]')) {
      store.toggleIme();
      return;
    }
    if (target.closest('[data-show-desktop]')) {
      store.toggleShowDesktop();
      return;
    }
    const tbApp = target.closest<HTMLElement>('[data-tb-app]');
    if (tbApp?.dataset.tbApp) {
      store.toggleTaskbarApp(tbApp.dataset.tbApp, getViewport());
    }
  });

  startSearchEl.addEventListener('input', () => {
    searchQuery = startSearchEl.value;
    renderStartMenu();
  });

  startMenuEl.addEventListener('click', (e) => {
    const target = e.target as HTMLElement;
    const appBtn = target.closest<HTMLElement>('[data-launch-app]');
    if (appBtn?.dataset.launchApp) {
      searchQuery = '';
      startSearchEl.value = '';
      store.closeFlyouts();
      launchShellItem(appBtn.dataset.launchApp);
      return;
    }
    if (target.closest('[data-start-reset]')) {
      store.resetDesktopLayout();
      store.closeFlyouts();
    }
  });

  quickSettingsEl.addEventListener('click', (e) => {
    const target = e.target as HTMLElement;
    const toggleBtn = target.closest<HTMLElement>('[data-qs-toggle]');
    if (toggleBtn) {
      const key = toggleBtn.dataset.qsToggle;
      if (key === 'theme') {
        store.setTheme(store.state.theme === 'dark' ? 'light' : 'dark');
      } else if (key === 'wifi' || key === 'bluetooth' || key === 'airplane' || key === 'nightLight') {
        store.toggleQuickSetting(key);
      }
      return;
    }
    if (target.closest('[data-qs-open-settings]')) {
      store.openApp('settings', getViewport());
    }
  });

  quickSettingsEl.addEventListener('input', (e) => {
    const target = e.target as HTMLInputElement;
    if (target.matches('[data-qs-brightness]')) {
      store.setBrightness(Number(target.value));
    } else if (target.matches('[data-qs-volume]')) {
      store.setVolume(Number(target.value));
    }
  });

  // Dismiss flyouts / context menus when clicking outside
  document.addEventListener('pointerdown', (e) => {
    const target = e.target as HTMLElement;
    if (!target.closest('[data-context-menu]')) {
      closeContextMenu();
    }
    const insideFlyout =
      target.closest('[data-start-menu]') ||
      target.closest('[data-quick-settings]') ||
      target.closest('[data-calendar-flyout]') ||
      target.closest('[data-tray-overflow]') ||
      target.closest('[data-taskbar]');
    if (!insideFlyout) {
      store.closeFlyouts();
    }
  });

  const ICON_SIZE_ORDER: DesktopIconSize[] = ['small', 'medium', 'large'];
  let lastWheelZoomAt = 0;

  window.addEventListener(
    'wheel',
    (e) => {
      if (!e.ctrlKey && !e.metaKey) return;
      e.preventDefault();
      if (e.deltaY === 0) return;

      const target = e.target as HTMLElement | null;
      if (target?.closest('.w11-window, [data-start-menu], [data-quick-settings], [data-calendar-flyout], [data-context-menu]')) {
        return;
      }

      const now = Date.now();
      if (now - lastWheelZoomAt < 120) return;
      lastWheelZoomAt = now;

      const idx = ICON_SIZE_ORDER.indexOf(store.state.iconSize);
      const nextIdx = e.deltaY < 0 ? Math.min(ICON_SIZE_ORDER.length - 1, idx + 1) : Math.max(0, idx - 1);
      if (nextIdx !== idx) {
        const nextSize = ICON_SIZE_ORDER[nextIdx];
        store.setIconSize(nextSize, getMaxGridRows(nextSize));
      }
    },
    { passive: false },
  );

  window.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && (e.key === '+' || e.key === '=' || e.key === '-' || e.key === '_' || e.key === '0')) {
      e.preventDefault();
      return;
    }
    if (e.key === 'Escape') {
      closeContextMenu();
      store.closeFlyouts();
    } else if (e.key === 'F5' && !e.ctrlKey) {
      e.preventDefault();
      triggerDesktopRefresh();
    } else if (e.key === 'Enter' && store.state.selectedIconIds.length === 1 && !document.activeElement?.matches('input')) {
      launchShellItem(store.state.selectedIconIds[0]);
    }
  });

  document.addEventListener('fullscreenchange', () => {
    renderDesktopIcons();
  });

  function handleViewportChange() {
    applyDesktopScale();
    store.constrainWindowsToViewport(getViewport());
    renderDesktopIcons();
  }

  window.addEventListener('resize', handleViewportChange);
  window.addEventListener('orientationchange', () => {
    handleViewportChange();
    window.setTimeout(handleViewportChange, 120);
  });
  window.visualViewport?.addEventListener('resize', handleViewportChange);

  if (typeof ResizeObserver !== 'undefined') {
    new ResizeObserver(() => renderDesktopIcons()).observe(root);
  }

  // Bring iframe window to front and dismiss shell popups when user interacts inside an embedded app
  window.addEventListener('blur', () => {
    window.setTimeout(() => {
      const active = document.activeElement;
      if (!(active instanceof HTMLIFrameElement)) return;
      const winEl = active.closest<HTMLElement>('.w11-window');
      if (!winEl) return;
      closeContextMenu();
      store.closeFlyouts();
      store.focusWindow(Number(winEl.dataset.pid));
    }, 0);
  });

  applyDesktopScale();
  store.subscribe(renderAll);
  updateClock();
  window.setInterval(updateClock, 1000);
  renderAll();
  store.openApp('exhyperv', getViewport());

  // Right-side Design Candidates Panel (GPU-PV layout options synced with ExHyperV iframe)
  const dcpPanel = root.querySelector<HTMLElement>('#desktopCandidatesPanel');
  const dcpHeader = root.querySelector<HTMLElement>('#desktopDcpHeader');
  const dcpCollapseBtn = root.querySelector<HTMLButtonElement>('#desktopDcpCollapseBtn');
  if (dcpPanel) {
    let pendingCandidateMsg: Record<string, unknown> | null = null;

    const sendGpuCandidateMsg = (payload: Record<string, unknown>) => {
      store.openApp('exhyperv', getViewport());
      const frame = windowsLayerEl.querySelector<HTMLIFrameElement>('iframe.w11-win-iframe');
      if (frame?.contentWindow) {
        frame.contentWindow.postMessage({ type: 'exhyperv-gpu-candidate', ...payload }, window.location.origin);
        pendingCandidateMsg = null;
      } else {
        pendingCandidateMsg = payload;
      }
    };

    const setActiveChip = (selector: string, attr: string, val: string) => {
      dcpPanel.querySelectorAll<HTMLElement>(selector).forEach((btn) => {
        btn.classList.toggle('active', btn.getAttribute(attr) === val);
      });
    };

    dcpPanel.querySelectorAll<HTMLElement>('[data-dcp-gpu-variant]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const variant = btn.getAttribute('data-dcp-gpu-variant') || 'expander';
        setActiveChip('[data-dcp-gpu-variant]', 'data-dcp-gpu-variant', variant);
        setActiveChip('[data-dcp-gpu-mode]', 'data-dcp-gpu-mode', 'list');
        sendGpuCandidateMsg({ variant, mode: 'list' });
      });
    });

    dcpPanel.querySelectorAll<HTMLElement>('[data-dcp-gpu-status]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const status = btn.getAttribute('data-dcp-gpu-status') || 'single';
        setActiveChip('[data-dcp-gpu-status]', 'data-dcp-gpu-status', status);
        setActiveChip('[data-dcp-gpu-mode]', 'data-dcp-gpu-mode', 'list');
        sendGpuCandidateMsg({ status, mode: 'list' });
      });
    });

    dcpPanel.querySelectorAll<HTMLElement>('[data-dcp-gpu-mode]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const mode = btn.getAttribute('data-dcp-gpu-mode') || 'list';
        setActiveChip('[data-dcp-gpu-mode]', 'data-dcp-gpu-mode', mode);
        sendGpuCandidateMsg({ mode });
      });
    });

    dcpCollapseBtn?.addEventListener('click', (e) => {
      e.stopPropagation();
      const collapsed = dcpPanel.classList.toggle('is-collapsed');
      dcpCollapseBtn.textContent = collapsed ? '+' : '−';
    });

    if (dcpHeader) {
      let dragging = false;
      let offX = 0;
      let offY = 0;
      let startX = 0;
      let startY = 0;
      dcpHeader.addEventListener('pointerdown', (e) => {
        if (e.button !== 0 || (e.target as HTMLElement)?.closest('#desktopDcpCollapseBtn')) return;
        const pt = toShellCoords(e.clientX, e.clientY);
        dragging = true;
        startX = pt.x - offX;
        startY = pt.y - offY;
        dcpHeader.setPointerCapture(e.pointerId);
      });
      dcpHeader.addEventListener('pointermove', (e) => {
        if (!dragging) return;
        const pt = toShellCoords(e.clientX, e.clientY);
        offX = pt.x - startX;
        offY = pt.y - startY;
        dcpPanel.style.transform = `translate3d(${offX}px, ${offY}px, 0)`;
      });
      dcpHeader.addEventListener('pointerup', (e) => {
        if (!dragging) return;
        dragging = false;
        dcpHeader.releasePointerCapture(e.pointerId);
      });
    }

    window.addEventListener('message', (event) => {
      if (event.origin !== window.location.origin) return;
      const data = event.data;
      if (!data || data.type !== 'exhyperv-gpu-sync') return;
      if (pendingCandidateMsg) {
        const payload = pendingCandidateMsg;
        pendingCandidateMsg = null;
        sendGpuCandidateMsg(payload);
        return;
      }
      if (data.variant) setActiveChip('[data-dcp-gpu-variant]', 'data-dcp-gpu-variant', data.variant);
      if (data.status) setActiveChip('[data-dcp-gpu-status]', 'data-dcp-gpu-status', data.status);
      if (data.mode) setActiveChip('[data-dcp-gpu-mode]', 'data-dcp-gpu-mode', data.mode);
    });
  }
}
