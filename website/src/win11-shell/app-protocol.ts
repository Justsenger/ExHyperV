import type { ThemeMode, WinAppLaunchArgs } from './types';
import type { VirtualHardwareSnapshot } from './virtual-hardware';

export const WIN11_APP_PROTOCOL = 'win11-app/v1' as const;

export type Win11WindowControlAction = 'minimize' | 'maximize' | 'restore' | 'close';

export type Win11ShellToAppMessage =
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:init';
      pid: number;
      appId: string;
      theme: ThemeMode;
      volume: number;
      muted: boolean;
      focused: boolean;
      minimized: boolean;
      launchArgs?: WinAppLaunchArgs;
      hardware?: VirtualHardwareSnapshot;
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:theme';
      theme: ThemeMode;
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:volume';
      volume: number;
      muted: boolean;
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:focus';
      focused: boolean;
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:visibility';
      minimized: boolean;
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:hardware';
      hardware: VirtualHardwareSnapshot;
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:app-message';
      channel: string;
      payload?: unknown;
    };

export type Win11AppToShellMessage =
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:ready';
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:set-title';
      title: string;
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:focus-self';
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:window-control';
      action: Win11WindowControlAction;
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:open-app';
      appId: string;
      args?: WinAppLaunchArgs;
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:request-hardware';
      force?: boolean;
    }
  | {
      protocol: typeof WIN11_APP_PROTOCOL;
      type: 'w11:app-event';
      channel: string;
      payload?: unknown;
    };

const VALID_SHELL_MESSAGE_TYPES = new Set([
  'w11:init',
  'w11:theme',
  'w11:volume',
  'w11:focus',
  'w11:visibility',
  'w11:hardware',
  'w11:app-message',
]);

const VALID_APP_MESSAGE_TYPES = new Set([
  'w11:ready',
  'w11:set-title',
  'w11:focus-self',
  'w11:window-control',
  'w11:open-app',
  'w11:request-hardware',
  'w11:app-event',
]);

export function isWin11ShellToAppMessage(data: unknown): data is Win11ShellToAppMessage {
  if (!data || typeof data !== 'object') return false;
  const candidate = data as { protocol?: unknown; type?: unknown };
  return (
    candidate.protocol === WIN11_APP_PROTOCOL &&
    typeof candidate.type === 'string' &&
    VALID_SHELL_MESSAGE_TYPES.has(candidate.type)
  );
}

export function isWin11AppToShellMessage(data: unknown): data is Win11AppToShellMessage {
  if (!data || typeof data !== 'object') return false;
  const candidate = data as { protocol?: unknown; type?: unknown };
  return (
    candidate.protocol === WIN11_APP_PROTOCOL &&
    typeof candidate.type === 'string' &&
    VALID_APP_MESSAGE_TYPES.has(candidate.type)
  );
}

export interface Win11AppBootQuery {
  theme: ThemeMode | null;
  volume: number | null;
  appId: string | null;
  pid: number | null;
}

export function readWin11BootQuery(search: string): Win11AppBootQuery {
  const params = new URLSearchParams(search);
  const rawTheme = params.get('theme');
  const rawVolume = params.get('volume');
  const rawPid = params.get('pid');
  const parsedVol = rawVolume !== null ? Number(rawVolume) : Number.NaN;
  const parsedPid = rawPid !== null ? Number(rawPid) : Number.NaN;

  return {
    theme: rawTheme === 'light' || rawTheme === 'dark' ? rawTheme : null,
    volume: Number.isFinite(parsedVol) ? Math.max(0, Math.min(100, parsedVol)) : null,
    appId: params.get('appId'),
    pid: Number.isFinite(parsedPid) ? parsedPid : null,
  };
}

export interface Win11AppClientOptions {
  /**
   * 期望的桌面壳源（同源应用默认 `window.location.origin`，跨源嵌入可传具体 origin 或 `'*'`）。
   */
  shellOrigin?: string;
  /**
   * 创建后自动向宿主发送 `w11:ready`（默认 `true`）。
   */
  autoReady?: boolean;
  onInit?: (payload: Extract<Win11ShellToAppMessage, { type: 'w11:init' }>) => void;
  onThemeChange?: (theme: ThemeMode) => void;
  onVolumeChange?: (volume: number, muted: boolean) => void;
  onFocusChange?: (focused: boolean) => void;
  onVisibilityChange?: (minimized: boolean) => void;
  onHardwareUpdate?: (hardware: VirtualHardwareSnapshot) => void;
  onAppMessage?: (channel: string, payload: unknown) => void;
}

export interface Win11AppClient {
  readonly isEmbedded: boolean;
  readonly bootQuery: Win11AppBootQuery;
  ready(): void;
  setTitle(title: string): void;
  focusSelf(): void;
  windowControl(action: Win11WindowControlAction): void;
  openApp(appId: string, args?: WinAppLaunchArgs): void;
  requestHardware(force?: boolean): void;
  emitEvent(channel: string, payload?: unknown): void;
  destroy(): void;
}

/**
 * 供同源/嵌入式 Web 应用使用的桌面外壳 SDK 客户端。
 * 独立访问页面时安全退化为空操作。
 */
export function createWin11AppClient(options: Win11AppClientOptions = {}): Win11AppClient {
  const isEmbedded = typeof window !== 'undefined' && window.parent !== window;
  const targetOrigin =
    options.shellOrigin ?? (typeof window !== 'undefined' ? window.location.origin : '*');
  const bootQuery = readWin11BootQuery(typeof window !== 'undefined' ? window.location.search : '');

  if (bootQuery.theme) {
    options.onThemeChange?.(bootQuery.theme);
  }
  if (bootQuery.volume !== null) {
    options.onVolumeChange?.(bootQuery.volume, bootQuery.volume === 0);
  }

  function postToShell(message: Win11AppToShellMessage) {
    if (!isEmbedded) return;
    try {
      window.parent.postMessage(message, targetOrigin);
    } catch {
      // Ignore detached or cross-origin postMessage errors.
    }
  }

  function handleMessage(event: MessageEvent) {
    if (!isEmbedded || event.source !== window.parent) return;
    if (targetOrigin !== '*' && event.origin !== targetOrigin) return;

    const data = event.data;
    if (isWin11ShellToAppMessage(data)) {
      switch (data.type) {
        case 'w11:init':
          options.onInit?.(data);
          options.onThemeChange?.(data.theme);
          options.onVolumeChange?.(data.volume, data.muted);
          options.onFocusChange?.(data.focused);
          options.onVisibilityChange?.(data.minimized);
          if (data.hardware) {
            options.onHardwareUpdate?.(data.hardware);
          }
          break;
        case 'w11:theme':
          options.onThemeChange?.(data.theme);
          break;
        case 'w11:volume':
          options.onVolumeChange?.(data.volume, data.muted);
          break;
        case 'w11:focus':
          options.onFocusChange?.(data.focused);
          break;
        case 'w11:visibility':
          options.onVisibilityChange?.(data.minimized);
          break;
        case 'w11:hardware':
          options.onHardwareUpdate?.(data.hardware);
          break;
        case 'w11:app-message':
          options.onAppMessage?.(data.channel, data.payload);
          break;
      }
      return;
    }

    // Legacy theme message compatibility
    if (
      data &&
      typeof data === 'object' &&
      (data as { type?: unknown }).type === 'w11-theme' &&
      ((data as { theme?: unknown }).theme === 'dark' || (data as { theme?: unknown }).theme === 'light')
    ) {
      options.onThemeChange?.((data as { theme: ThemeMode }).theme);
    }
  }

  if (typeof window !== 'undefined' && isEmbedded) {
    window.addEventListener('message', handleMessage);
    if (options.autoReady !== false) {
      postToShell({ protocol: WIN11_APP_PROTOCOL, type: 'w11:ready' });
    }
  }

  return {
    isEmbedded,
    bootQuery,
    ready: () => postToShell({ protocol: WIN11_APP_PROTOCOL, type: 'w11:ready' }),
    setTitle: (title) => postToShell({ protocol: WIN11_APP_PROTOCOL, type: 'w11:set-title', title }),
    focusSelf: () => postToShell({ protocol: WIN11_APP_PROTOCOL, type: 'w11:focus-self' }),
    windowControl: (action) =>
      postToShell({ protocol: WIN11_APP_PROTOCOL, type: 'w11:window-control', action }),
    openApp: (appId, args) =>
      postToShell({ protocol: WIN11_APP_PROTOCOL, type: 'w11:open-app', appId, args }),
    requestHardware: (force) =>
      postToShell({ protocol: WIN11_APP_PROTOCOL, type: 'w11:request-hardware', force }),
    emitEvent: (channel, payload) =>
      postToShell({ protocol: WIN11_APP_PROTOCOL, type: 'w11:app-event', channel, payload }),
    destroy: () => {
      if (typeof window !== 'undefined') {
        window.removeEventListener('message', handleMessage);
      }
    },
  };
}
