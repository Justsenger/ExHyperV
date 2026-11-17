import { APP_REGISTRY, getManifestById } from './registry';
import type {
  ActiveFlyout,
  DesktopIconPosition,
  DesktopIconSize,
  DesktopSortMode,
  ThemeMode,
  WinAppLaunchArgs,
  WindowInstance,
} from './types';

export interface ShellState {
  theme: ThemeMode;
  iconSize: DesktopIconSize;
  sortMode: DesktopSortMode;
  autoArrange: boolean;
  alignToGrid: boolean;
  selectedIconIds: string[];
  iconPositions: Record<string, DesktopIconPosition>;
  pinnedTaskbarIds: string[];
  windows: WindowInstance[];
  activePid: number | null;
  activeFlyout: ActiveFlyout;
  imeMode: '中' | '英';
  brightness: number;
  volume: number;
  toggles: {
    wifi: boolean;
    bluetooth: boolean;
    airplane: boolean;
    nightLight: boolean;
  };
}

const STORAGE_KEY = 'win11-web-shell:state:v3';

interface PersistedPrefs {
  theme: ThemeMode;
  iconSize: DesktopIconSize;
  sortMode: DesktopSortMode;
  autoArrange: boolean;
  alignToGrid: boolean;
  iconPositions: Record<string, DesktopIconPosition>;
  pinnedTaskbarIds: string[];
}

function getDefaultIconPositions(): Record<string, DesktopIconPosition> {
  const desktopApps = APP_REGISTRY.filter((app) => app.onDesktop);
  const positions: Record<string, DesktopIconPosition> = {};
  desktopApps.forEach((app, index) => {
    positions[app.id] = { col: 0, row: index };
  });
  return positions;
}

function getDefaultPinnedTaskbarIds(): string[] {
  return APP_REGISTRY.filter((app) => app.pinnedToTaskbar).map((app) => app.id);
}

function loadPersistedPrefs(): Partial<PersistedPrefs> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return {};
    return JSON.parse(raw) as Partial<PersistedPrefs>;
  } catch {
    return {};
  }
}

export function createShellStore() {
  const saved = loadPersistedPrefs();
  let nextPid = 1001;
  let nextZ = 10;

  const state: ShellState = {
    theme: saved.theme === 'light' ? 'light' : 'dark',
    iconSize: saved.iconSize && ['large', 'medium', 'small'].includes(saved.iconSize) ? saved.iconSize : 'medium',
    sortMode: saved.sortMode === 'name' ? 'name' : 'default',
    autoArrange: typeof saved.autoArrange === 'boolean' ? saved.autoArrange : false,
    alignToGrid: typeof saved.alignToGrid === 'boolean' ? saved.alignToGrid : true,
    selectedIconIds: [],
    iconPositions: { ...getDefaultIconPositions(), ...(saved.iconPositions ?? {}) },
    pinnedTaskbarIds: Array.isArray(saved.pinnedTaskbarIds) ? saved.pinnedTaskbarIds : getDefaultPinnedTaskbarIds(),
    windows: [],
    activePid: null,
    activeFlyout: 'none',
    imeMode: '中',
    brightness: 100,
    volume: 65,
    toggles: {
      wifi: true,
      bluetooth: true,
      airplane: false,
      nightLight: false,
    },
  };

  const listeners = new Set<() => void>();

  function persist() {
    try {
      const payload: PersistedPrefs = {
        theme: state.theme,
        iconSize: state.iconSize,
        sortMode: state.sortMode,
        autoArrange: state.autoArrange,
        alignToGrid: state.alignToGrid,
        iconPositions: state.iconPositions,
        pinnedTaskbarIds: state.pinnedTaskbarIds,
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(payload));
    } catch {
      // Ignore storage quota or private mode restrictions.
    }
  }

  function notify() {
    listeners.forEach((fn) => fn());
  }

  function subscribe(fn: () => void): () => void {
    listeners.add(fn);
    return () => listeners.delete(fn);
  }

  function setTheme(theme: ThemeMode) {
    state.theme = theme;
    if (typeof document !== 'undefined') {
      document.documentElement.setAttribute('data-theme', theme);
    }
    persist();
    notify();
  }

  function getColumnMajorIconOrder(): string[] {
    const desktopApps = APP_REGISTRY.filter((a) => a.onDesktop);
    return [...desktopApps]
      .sort((a, b) => {
        const pa = state.iconPositions[a.id] ?? { col: 0, row: 0 };
        const pb = state.iconPositions[b.id] ?? { col: 0, row: 0 };
        if (pa.col !== pb.col) return pa.col - pb.col;
        if (pa.row !== pb.row) return pa.row - pb.row;
        return desktopApps.indexOf(a) - desktopApps.indexOf(b);
      })
      .map((a) => a.id);
  }

  function applyPackedOrder(orderedIds: string[], maxRows: number) {
    const rows = Math.max(1, maxRows);
    const nextPositions: Record<string, DesktopIconPosition> = {};
    orderedIds.forEach((id, idx) => {
      nextPositions[id] = {
        col: Math.floor(idx / rows),
        row: idx % rows,
      };
    });
    state.iconPositions = nextPositions;
  }

  function setIconSize(size: DesktopIconSize, maxRows?: number) {
    state.iconSize = size;
    if (state.autoArrange && typeof maxRows === 'number') {
      applyPackedOrder(getColumnMajorIconOrder(), maxRows);
    }
    persist();
    notify();
  }

  function toggleAutoArrange(maxRows: number) {
    state.autoArrange = !state.autoArrange;
    if (state.autoArrange) {
      applyPackedOrder(getColumnMajorIconOrder(), maxRows);
    }
    persist();
    notify();
  }

  function toggleAlignToGrid(maxCols: number, maxRows: number) {
    state.alignToGrid = !state.alignToGrid;
    if (state.alignToGrid && !state.autoArrange) {
      const occupied = new Set<string>();
      const ordered = getColumnMajorIconOrder();
      const cols = Math.max(1, maxCols);
      const rows = Math.max(1, maxRows);
      const nextPositions: Record<string, DesktopIconPosition> = {};
      ordered.forEach((id) => {
        const cur = state.iconPositions[id] ?? { col: 0, row: 0 };
        let col = Math.max(0, Math.min(cols - 1, Math.round(cur.col)));
        let row = Math.max(0, Math.min(rows - 1, Math.round(cur.row)));
        while (occupied.has(`${col},${row}`)) {
          row += 1;
          if (row >= rows) {
            row = 0;
            col = (col + 1) % cols;
          }
        }
        occupied.add(`${col},${row}`);
        nextPositions[id] = { col, row };
      });
      state.iconPositions = nextPositions;
    }
    persist();
    notify();
  }

  function sortDesktopIcons(mode: DesktopSortMode, maxRows: number) {
    state.sortMode = mode;
    const desktopApps = APP_REGISTRY.filter((a) => a.onDesktop);
    const ordered =
      mode === 'name'
        ? [...desktopApps].sort((a, b) => a.name.localeCompare(b.name, 'zh-CN'))
        : desktopApps;

    applyPackedOrder(
      ordered.map((a) => a.id),
      maxRows,
    );
    persist();
    notify();
  }

  function moveIconToCell(appId: string, rawCol: number, rawRow: number, maxCols: number, maxRows: number) {
    const cols = Math.max(1, maxCols);
    const rows = Math.max(1, maxRows);

    // Windows 规则：开启“自动排列图标”后，只能手动取消；拖到空白处会自动归位，仅在队内位置交换顺序
    if (state.autoArrange) {
      const ordered = getColumnMajorIconOrder();
      const targetCol = Math.round(rawCol);
      const targetRow = Math.round(rawRow);
      const targetSlot = targetCol * rows + targetRow;
      const currentIdx = ordered.indexOf(appId);
      if (
        currentIdx !== -1 &&
        targetCol >= 0 &&
        targetRow >= 0 &&
        targetRow < rows &&
        targetSlot >= 0 &&
        targetSlot < ordered.length &&
        targetSlot !== currentIdx
      ) {
        ordered.splice(currentIdx, 1);
        ordered.splice(targetSlot, 0, appId);
      }
      applyPackedOrder(ordered, rows);
      persist();
      notify();
      return;
    }

    if (!state.alignToGrid) {
      state.iconPositions[appId] = {
        col: Math.max(0, Math.min(cols - 1, rawCol)),
        row: Math.max(0, Math.min(rows - 1, rawRow)),
      };
      persist();
      notify();
      return;
    }

    const col = Math.max(0, Math.min(cols - 1, Math.round(rawCol)));
    const row = Math.max(0, Math.min(rows - 1, Math.round(rawRow)));
    const occupiedEntry = Object.entries(state.iconPositions).find(
      ([id, pos]) => id !== appId && Math.round(pos.col) === col && Math.round(pos.row) === row,
    );
    const prev = state.iconPositions[appId];
    if (occupiedEntry && prev) {
      state.iconPositions[occupiedEntry[0]] = {
        col: Math.max(0, Math.min(cols - 1, Math.round(prev.col))),
        row: Math.max(0, Math.min(rows - 1, Math.round(prev.row))),
      };
    }
    state.iconPositions[appId] = { col, row };
    persist();
    notify();
  }

  function selectIcons(ids: string[]) {
    state.selectedIconIds = ids;
    notify();
  }

  function toggleIconSelection(id: string) {
    if (state.selectedIconIds.includes(id)) {
      state.selectedIconIds = state.selectedIconIds.filter((item) => item !== id);
    } else {
      state.selectedIconIds = [...state.selectedIconIds, id];
    }
    notify();
  }

  function setActiveFlyout(flyout: ActiveFlyout) {
    state.activeFlyout = state.activeFlyout === flyout ? 'none' : flyout;
    notify();
  }

  function closeFlyouts() {
    if (state.activeFlyout !== 'none') {
      state.activeFlyout = 'none';
      notify();
    }
  }

  function openApp(
    appId: string,
    viewport: { width: number; height: number },
    launchArgs?: WinAppLaunchArgs,
  ): number | null {
    const manifest = getManifestById(appId);
    if (!manifest || manifest.runtime === 'command') return null;

    state.activeFlyout = 'none';

    if (manifest.singleton !== false) {
      const existing = state.windows.find((w) => w.appId === appId);
      if (existing) {
        existing.minimized = false;
        if (launchArgs !== undefined) {
          existing.launchArgs = launchArgs;
        }
        nextZ += 1;
        existing.zIndex = nextZ;
        state.activePid = existing.pid;
        notify();
        return existing.pid;
      }
    }

    const winSpec = manifest.window ?? {};
    const workW = Math.max(360, viewport.width);
    const workH = Math.max(320, viewport.height - 48);
    const minWidth = winSpec.minWidth ?? 340;
    const minHeight = winSpec.minHeight ?? 240;
    const effectiveMinW = Math.min(minWidth, Math.max(240, workW - 24));
    const effectiveMinH = Math.min(minHeight, Math.max(200, workH - 24));
    const width = Math.max(effectiveMinW, Math.min(winSpec.defaultWidth ?? 820, workW - 48));
    const height = Math.max(effectiveMinH, Math.min(winSpec.defaultHeight ?? 520, workH - 32));
    const cascadeOffset = (state.windows.length % 6) * 24;
    const rawX = Math.round((workW - width) / 2 + cascadeOffset);
    const rawY = Math.round((workH - height) / 2 + cascadeOffset);
    const x = Math.max(16, Math.min(rawX, Math.max(16, workW - width - 16)));
    const y = Math.max(16, Math.min(rawY, Math.max(16, workH - height - 16)));

    nextPid += 1;
    nextZ += 1;
    const win: WindowInstance = {
      pid: nextPid,
      appId: manifest.id,
      title: manifest.name,
      iconKey: manifest.iconKey,
      x,
      y,
      width,
      height,
      minWidth,
      minHeight,
      resizable: winSpec.resizable ?? true,
      maximizable: winSpec.maximizable ?? true,
      zIndex: nextZ,
      minimized: false,
      maximized: false,
      seamlessTitlebar: winSpec.seamlessTitlebar,
      background: winSpec.background,
      launchArgs,
    };
    state.windows.push(win);
    state.activePid = win.pid;
    notify();
    return win.pid;
  }

  function setWindowTitle(pid: number, title: string) {
    const win = state.windows.find((w) => w.pid === pid);
    if (!win) return;
    const manifest = getManifestById(win.appId);
    const nextTitle = title.trim() || manifest?.name || win.title;
    if (nextTitle === win.title) return;
    win.title = nextTitle;
    notify();
  }

  function focusWindow(pid: number) {
    const win = state.windows.find((w) => w.pid === pid);
    if (!win) return;
    if (!win.minimized && state.activePid === pid && state.activeFlyout === 'none') {
      return;
    }
    win.minimized = false;
    nextZ += 1;
    win.zIndex = nextZ;
    state.activePid = pid;
    state.activeFlyout = 'none';
    notify();
  }

  function minimizeWindow(pid: number) {
    const win = state.windows.find((w) => w.pid === pid);
    if (!win) return;
    win.minimized = true;
    if (state.activePid === pid) {
      const topOther = [...state.windows]
        .filter((w) => !w.minimized && w.pid !== pid)
        .sort((a, b) => b.zIndex - a.zIndex)[0];
      state.activePid = topOther ? topOther.pid : null;
    }
    notify();
  }

  function toggleMaximizeWindow(pid: number, viewport: { width: number; height: number }) {
    const win = state.windows.find((w) => w.pid === pid);
    if (!win || !win.maximizable) return;
    nextZ += 1;
    win.zIndex = nextZ;
    state.activePid = pid;

    if (win.maximized) {
      if (win.restoreBounds) {
        win.x = win.restoreBounds.x;
        win.y = win.restoreBounds.y;
        win.width = win.restoreBounds.width;
        win.height = win.restoreBounds.height;
      }
      win.maximized = false;
      win.restoreBounds = undefined;
    } else {
      win.restoreBounds = {
        x: win.x,
        y: win.y,
        width: win.width,
        height: win.height,
      };
      win.x = 0;
      win.y = 0;
      win.width = viewport.width;
      win.height = Math.max(240, viewport.height - 48);
      win.maximized = true;
    }
    notify();
  }

  function closeWindow(pid: number) {
    state.windows = state.windows.filter((w) => w.pid !== pid);
    if (state.activePid === pid) {
      const topOther = [...state.windows]
        .filter((w) => !w.minimized)
        .sort((a, b) => b.zIndex - a.zIndex)[0];
      state.activePid = topOther ? topOther.pid : null;
    }
    notify();
  }

  function toggleTaskbarApp(appId: string, viewport: { width: number; height: number }) {
    const win = state.windows.find((w) => w.appId === appId);
    if (!win) {
      openApp(appId, viewport);
      return;
    }
    if (!win.minimized && state.activePid === win.pid) {
      minimizeWindow(win.pid);
    } else {
      focusWindow(win.pid);
    }
  }

  function toggleShowDesktop() {
    const hasVisible = state.windows.some((w) => !w.minimized);
    if (hasVisible) {
      state.windows.forEach((w) => {
        w.minimized = true;
      });
      state.activePid = null;
    } else {
      state.windows.forEach((w) => {
        w.minimized = false;
      });
      const top = [...state.windows].sort((a, b) => b.zIndex - a.zIndex)[0];
      state.activePid = top ? top.pid : null;
    }
    state.activeFlyout = 'none';
    notify();
  }

  function togglePinToTaskbar(appId: string) {
    if (state.pinnedTaskbarIds.includes(appId)) {
      state.pinnedTaskbarIds = state.pinnedTaskbarIds.filter((id) => id !== appId);
    } else {
      state.pinnedTaskbarIds = [...state.pinnedTaskbarIds, appId];
    }
    persist();
    notify();
  }

  function toggleIme() {
    state.imeMode = state.imeMode === '中' ? '英' : '中';
    notify();
  }

  function setBrightness(value: number) {
    state.brightness = Math.max(30, Math.min(100, value));
    notify();
  }

  function setVolume(value: number) {
    state.volume = Math.max(0, Math.min(100, value));
    notify();
  }

  function toggleQuickSetting(key: keyof ShellState['toggles']) {
    state.toggles[key] = !state.toggles[key];
    notify();
  }

  function constrainWindowsToViewport(viewport: { width: number; height: number }) {
    if (state.windows.length === 0) return;
    const workW = Math.max(360, viewport.width);
    const workH = Math.max(280, viewport.height - 48);
    let changed = false;

    state.windows.forEach((win) => {
      if (win.maximized) {
        if (win.x !== 0 || win.y !== 0 || win.width !== workW || win.height !== workH) {
          win.x = 0;
          win.y = 0;
          win.width = workW;
          win.height = workH;
          changed = true;
        }
      } else {
        const minW = Math.min(win.minWidth, Math.max(240, workW - 24));
        const minH = Math.min(win.minHeight, Math.max(200, workH - 24));
        const nextW = Math.max(minW, Math.min(win.width, Math.max(minW, workW - 24)));
        const nextH = Math.max(minH, Math.min(win.height, Math.max(minH, workH - 24)));
        const nextX = Math.max(0, Math.min(win.x, Math.max(0, workW - nextW)));
        const nextY = Math.max(0, Math.min(win.y, Math.max(0, workH - nextH)));
        if (nextW !== win.width || nextH !== win.height || nextX !== win.x || nextY !== win.y) {
          win.width = nextW;
          win.height = nextH;
          win.x = nextX;
          win.y = nextY;
          changed = true;
        }
      }
    });

    if (changed) notify();
  }

  function resetDesktopLayout() {
    state.iconPositions = getDefaultIconPositions();
    state.pinnedTaskbarIds = getDefaultPinnedTaskbarIds();
    state.iconSize = 'medium';
    state.sortMode = 'default';
    state.autoArrange = false;
    state.alignToGrid = true;
    state.theme = 'dark';
    if (typeof document !== 'undefined') {
      document.documentElement.setAttribute('data-theme', 'dark');
    }
    persist();
    notify();
  }

  return {
    state,
    subscribe,
    notify,
    setTheme,
    setIconSize,
    toggleAutoArrange,
    toggleAlignToGrid,
    sortDesktopIcons,
    moveIconToCell,
    constrainWindowsToViewport,
    selectIcons,
    toggleIconSelection,
    setActiveFlyout,
    closeFlyouts,
    openApp,
    setWindowTitle,
    focusWindow,
    minimizeWindow,
    toggleMaximizeWindow,
    closeWindow,
    toggleTaskbarApp,
    toggleShowDesktop,
    togglePinToTaskbar,
    toggleIme,
    setBrightness,
    setVolume,
    toggleQuickSetting,
    resetDesktopLayout,
  };
}

export type ShellStore = ReturnType<typeof createShellStore>;
