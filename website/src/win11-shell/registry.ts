import type { WinAppCapability, WinAppManifest } from './types';

/**
 * Web 桌面应用注册表（Manifest）。
 * 所有内置应用、iframe 独立 Web/Wasm 应用与系统命令均在此统一声明元数据、窗口规格与权限能力。
 */
export const APP_REGISTRY: WinAppManifest[] = [
  {
    id: 'this-pc',
    name: '此电脑',
    iconKey: 'this-pc',
    description: '管理驱动器、系统信息与设备连接。',
    category: 'system',
    runtime: 'builtin',
    onDesktop: true,
    pinnedToStart: true,
    pinnedToTaskbar: false,
    window: {
      defaultWidth: 860,
      defaultHeight: 540,
      minWidth: 420,
      minHeight: 300,
    },
    telemetry: {
      publisher: 'Microsoft Windows',
      estimatedMemMb: 48.5,
    },
  },
  {
    id: 'recycle-bin',
    name: '回收站',
    iconKey: 'recycle-bin',
    description: '查看与还原已删除的桌面项目。',
    category: 'system',
    runtime: 'builtin',
    onDesktop: true,
    pinnedToStart: false,
    pinnedToTaskbar: false,
    window: {
      defaultWidth: 780,
      defaultHeight: 480,
      minWidth: 400,
      minHeight: 280,
    },
    telemetry: {
      publisher: 'Microsoft Windows',
      estimatedMemMb: 36.2,
    },
  },
  {
    id: 'control-panel',
    name: '控制面板',
    iconKey: 'control-panel',
    description: '查看与更改传统的 Windows 系统控制项。',
    category: 'system',
    runtime: 'builtin',
    onDesktop: true,
    pinnedToStart: true,
    pinnedToTaskbar: false,
    window: {
      defaultWidth: 820,
      defaultHeight: 520,
      minWidth: 420,
      minHeight: 300,
    },
    telemetry: {
      publisher: 'Microsoft Windows',
      estimatedMemMb: 52.0,
    },
  },
  {
    id: 'explorer',
    name: '文件资源管理器',
    iconKey: 'explorer',
    description: '浏览本地文件与快速访问目录。',
    category: 'system',
    runtime: 'builtin',
    onDesktop: false,
    pinnedToStart: true,
    pinnedToTaskbar: true,
    window: {
      defaultWidth: 920,
      defaultHeight: 580,
      minWidth: 460,
      minHeight: 320,
    },
    telemetry: {
      publisher: 'Microsoft Windows',
      estimatedMemMb: 78.4,
    },
  },
  {
    id: 'settings',
    name: '设置',
    iconKey: 'settings',
    description: '调整桌面外观、主题与图标显示。',
    category: 'system',
    runtime: 'builtin',
    onDesktop: true,
    pinnedToStart: true,
    pinnedToTaskbar: true,
    window: {
      defaultWidth: 880,
      defaultHeight: 560,
      minWidth: 520,
      minHeight: 380,
    },
    capabilities: ['theme', 'volume', 'shell-control'],
    telemetry: {
      publisher: 'Microsoft Windows',
      estimatedMemMb: 64.0,
    },
  },
  {
    id: 'task-manager',
    name: '任务管理器',
    iconKey: 'task-manager',
    description: '查看当前桌面窗口进程与设备硬件探测状态。',
    category: 'system',
    runtime: 'builtin',
    onDesktop: false,
    pinnedToStart: true,
    pinnedToTaskbar: false,
    window: {
      defaultWidth: 1024,
      defaultHeight: 770,
      minWidth: 640,
      minHeight: 440,
      seamlessTitlebar: true,
      background: '#202020',
    },
    capabilities: ['theme', 'hardware-probe', 'shell-control', 'window-control'],
    telemetry: {
      publisher: 'Microsoft Windows',
      estimatedMemMb: 86.5,
    },
  },
  {
    id: 'terminal',
    name: '终端',
    iconKey: 'terminal',
    description: 'Windows 终端命令行外壳占位。',
    category: 'utility',
    runtime: 'builtin',
    onDesktop: false,
    pinnedToStart: true,
    pinnedToTaskbar: true,
    window: {
      defaultWidth: 820,
      defaultHeight: 500,
      minWidth: 420,
      minHeight: 280,
    },
    telemetry: {
      publisher: 'Microsoft Corporation',
      estimatedMemMb: 58.0,
    },
  },
  {
    id: 'exhyperv',
    name: 'ExHyperV',
    iconKey: 'exhyperv',
    description: '为 Windows 上的 Hyper-V 带来卓越的现代化桌面体验。',
    category: 'app',
    runtime: 'iframe',
    onDesktop: true,
    pinnedToStart: true,
    pinnedToTaskbar: false,
    window: {
      defaultWidth: 1140,
      defaultHeight: 900,
      minWidth: 640,
      minHeight: 480,
      seamlessTitlebar: true,
    },
    iframe: {
      entryUrl: '/apps/exhyperv/',
      passShellQuery: true,
    },
    capabilities: ['theme', 'focus', 'visibility', 'hardware-probe', 'app-messaging', 'window-control'],
    telemetry: {
      publisher: 'ExHyperV Project',
      estimatedMemMb: 188.5,
    },
  },
  {
    id: 'fullscreen',
    name: '全屏体验',
    iconKey: 'fullscreen',
    description: '切换浏览器全屏，获得沉浸式 Windows 11 桌面体验。',
    category: 'system',
    runtime: 'command',
    onDesktop: true,
    pinnedToStart: true,
    pinnedToTaskbar: false,
    capabilities: ['shell-control'],
    getDynamicLabel: ({ isFullscreen }) => (isFullscreen ? '退出全屏' : '全屏体验'),
  },
];

export function getManifestById(appId: string): WinAppManifest | undefined {
  return APP_REGISTRY.find((app) => app.id === appId);
}

export function hasAppCapability(
  manifest: WinAppManifest | undefined,
  capability: WinAppCapability,
): boolean {
  return Boolean(manifest?.capabilities?.includes(capability));
}

export function getAppDisplayName(
  manifest: WinAppManifest,
  ctx: { isFullscreen: boolean },
): string {
  return manifest.getDynamicLabel ? manifest.getDynamicLabel(ctx) : manifest.name;
}
