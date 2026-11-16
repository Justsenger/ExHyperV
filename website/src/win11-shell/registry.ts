import type { WinAppManifest } from './types';

/**
 * 基础系统要素与图标注册表。
 * 保持极简：仅包含桌面、任务栏、开始菜单所需的基础系统项。
 * 后续接入 ExHyperV 或其他 Web 应用时，只需在此新增配置并填写 entryUrl 即可。
 */
export const APP_REGISTRY: WinAppManifest[] = [
  {
    id: 'this-pc',
    name: '此电脑',
    iconKey: 'this-pc',
    description: '管理驱动器、系统信息与设备连接。',
    onDesktop: true,
    pinnedToStart: true,
    pinnedToTaskbar: false,
    defaultWidth: 860,
    defaultHeight: 540,
  },
  {
    id: 'recycle-bin',
    name: '回收站',
    iconKey: 'recycle-bin',
    description: '查看与还原已删除的桌面项目。',
    onDesktop: true,
    pinnedToStart: false,
    pinnedToTaskbar: false,
    defaultWidth: 780,
    defaultHeight: 480,
  },
  {
    id: 'control-panel',
    name: '控制面板',
    iconKey: 'control-panel',
    description: '查看与更改传统的 Windows 系统控制项。',
    onDesktop: true,
    pinnedToStart: true,
    pinnedToTaskbar: false,
    defaultWidth: 820,
    defaultHeight: 520,
  },
  {
    id: 'explorer',
    name: '文件资源管理器',
    iconKey: 'explorer',
    description: '浏览本地文件与快速访问目录。',
    onDesktop: false,
    pinnedToStart: true,
    pinnedToTaskbar: true,
    defaultWidth: 920,
    defaultHeight: 580,
  },
  {
    id: 'settings',
    name: '设置',
    iconKey: 'settings',
    description: '调整桌面外观、主题与图标显示。',
    onDesktop: true,
    pinnedToStart: true,
    pinnedToTaskbar: true,
    defaultWidth: 880,
    defaultHeight: 560,
  },
  {
    id: 'task-manager',
    name: '任务管理器',
    iconKey: 'task-manager',
    description: '查看当前桌面窗口进程并管理运行状态。',
    onDesktop: false,
    pinnedToStart: true,
    pinnedToTaskbar: false,
    defaultWidth: 1024,
    defaultHeight: 770,
    seamlessTitlebar: true,
  },
  {
    id: 'terminal',
    name: '终端',
    iconKey: 'terminal',
    description: 'Windows 终端命令行外壳占位。',
    onDesktop: false,
    pinnedToStart: true,
    pinnedToTaskbar: true,
    defaultWidth: 820,
    defaultHeight: 500,
  },
  {
    id: 'exhyperv',
    name: 'ExHyperV',
    iconKey: 'exhyperv',
    description: '为 Windows 上的 Hyper-V 带来卓越的现代化桌面体验。',
    onDesktop: true,
    pinnedToStart: true,
    pinnedToTaskbar: false,
    defaultWidth: 1140,
    defaultHeight: 900,
    entryUrl: '/apps/exhyperv/',
    seamlessTitlebar: true,
  },
  {
    id: 'fullscreen',
    name: '全屏体验',
    iconKey: 'fullscreen',
    description: '切换浏览器全屏，获得沉浸式 Windows 11 桌面体验。',
    onDesktop: true,
    pinnedToStart: true,
    pinnedToTaskbar: false,
  },
];

export function getManifestById(appId: string): WinAppManifest | undefined {
  return APP_REGISTRY.find((app) => app.id === appId);
}
