import type { DeviceProbeReport, StorageProbeInfo } from './device-probe';
import type { VirtualHardwareSnapshot } from './virtual-hardware';

export type ThemeMode = 'dark' | 'light';

export type DesktopIconSize = 'large' | 'medium' | 'small';

export type DesktopSortMode = 'default' | 'name';

export type ActiveFlyout = 'none' | 'start' | 'quickSettings' | 'calendar' | 'trayOverflow';

export type SnapZone = 'none' | 'maximize' | 'left' | 'right';

export type WinAppRuntime = 'builtin' | 'iframe' | 'command';

export type WinAppCategory = 'system' | 'app' | 'utility' | 'game';

export type WinAppCapability =
  | 'theme'
  | 'volume'
  | 'focus'
  | 'visibility'
  | 'hardware-probe'
  | 'window-control'
  | 'shell-control'
  | 'app-messaging';

export type WinAppLaunchArgs = Record<string, unknown>;

export interface WinAppWindowSpec {
  defaultWidth?: number;
  defaultHeight?: number;
  minWidth?: number;
  minHeight?: number;
  resizable?: boolean;
  maximizable?: boolean;
  /**
   * 若应用自身直接延展 WinUI 3 Mica 背景，外壳标题栏取消底部分割线并无缝对齐其底色。
   */
  seamlessTitlebar?: boolean;
  /**
   * 窗口主体背景色（例如在游戏或深色应用加载前避免白屏闪烁）。
   */
  background?: string;
}

export interface WinAppIframeSpec {
  /**
   * 同源路径（如 `/apps/exhyperv/`）或可嵌入 HTTPS URL。
   */
  entryUrl: string;
  /**
   * iframe `allow` 权限声明（如 `autoplay; fullscreen; gamepad`）。
   */
  allow?: string;
  /**
   * 可选 iframe `sandbox` 属性；托管外部站点时建议显式配置。
   */
  sandbox?: string;
  /**
   * 为同源路径自动附带 `?theme=...&volume=...&appId=...&pid=...` 初始查询参数（默认 true）。
   */
  passShellQuery?: boolean;
}

export interface WinAppTelemetrySpec {
  publisher?: string;
  version?: string;
  /**
   * 任务管理器估算内存占用基线（MB）。
   */
  estimatedMemMb?: number;
}

export interface WinAppManifest {
  id: string;
  name: string;
  iconKey: string;
  description: string;
  category: WinAppCategory;
  runtime: WinAppRuntime;
  onDesktop?: boolean;
  pinnedToStart?: boolean;
  pinnedToTaskbar?: boolean;
  /**
   * 默认 `true`（单实例聚焦）；设为 `false` 时允许同时开启多个窗口实例。
   */
  singleton?: boolean;
  window?: WinAppWindowSpec;
  iframe?: WinAppIframeSpec;
  telemetry?: WinAppTelemetrySpec;
  capabilities?: readonly WinAppCapability[];
  /**
   * 可选运行时动态标题（如“全屏体验 / 退出全屏”）。
   */
  getDynamicLabel?: (ctx: { isFullscreen: boolean }) => string;
}

export interface WindowInstance {
  pid: number;
  appId: string;
  title: string;
  iconKey: string;
  x: number;
  y: number;
  width: number;
  height: number;
  minWidth: number;
  minHeight: number;
  resizable: boolean;
  maximizable: boolean;
  zIndex: number;
  minimized: boolean;
  maximized: boolean;
  restoreBounds?: {
    x: number;
    y: number;
    width: number;
    height: number;
  };
  seamlessTitlebar?: boolean;
  background?: string;
  launchArgs?: WinAppLaunchArgs;
}

export interface DesktopIconPosition {
  col: number;
  row: number;
}

export interface WinAppShellApi {
  getTheme(): ThemeMode;
  setTheme(theme: ThemeMode): void;
  getVolume(): number;
  setVolume(volume: number): void;
  getBrightness(): number;
  setBrightness(brightness: number): void;
  getIconSize(): DesktopIconSize;
  setIconSize(size: DesktopIconSize): void;
  resetDesktopLayout(): void;
  getWindows(): readonly WindowInstance[];
  openApp(appId: string, args?: WinAppLaunchArgs): number | null;
  closeWindow(pid: number): void;
  focusWindow(pid: number): void;
  minimizeWindow(pid: number): void;
  toggleMaximizeWindow(pid: number): void;
  setWindowTitle(pid: number, title: string): void;
  isFullscreen(): boolean;
  toggleFullscreen(): void;
  getVirtualHardware(): VirtualHardwareSnapshot;
  refreshVirtualHardware(force?: boolean): Promise<VirtualHardwareSnapshot>;
  refreshVirtualStorage(): Promise<StorageProbeInfo>;
  getHardwareReport(): DeviceProbeReport | null;
  refreshHardwareReport(force?: boolean): Promise<DeviceProbeReport>;
}

export interface WinAppHostContext {
  pid: number;
  appId: string;
  manifest: WinAppManifest;
  container: HTMLElement;
  launchArgs?: WinAppLaunchArgs;
  shell: WinAppShellApi;
}

export interface WinAppLifecycle {
  onThemeChange?: (theme: ThemeMode) => void;
  onVolumeChange?: (volume: number) => void;
  onFocusChange?: (focused: boolean) => void;
  onVisibilityChange?: (minimized: boolean) => void;
  onWindowsChanged?: () => void;
  onShellPrefsChange?: () => void;
  onHardwareUpdate?: (snapshot: VirtualHardwareSnapshot) => void;
  onHardwareReport?: (report: DeviceProbeReport) => void;
  onMessage?: (channel: string, payload: unknown) => void;
  destroy?: () => void;
}

export interface WinAppModule {
  mount(ctx: WinAppHostContext): WinAppLifecycle | void;
}

export type WinCommandHandler = (shell: WinAppShellApi, args?: WinAppLaunchArgs) => void;

export interface WinAppHostHooks {
  onAppEvent?: (appId: string, pid: number, channel: string, payload: unknown) => void;
}
