export const EXHYPERV_HOST_CONFIG = {
  machineName: 'EXHYPERV-VM',
  bootAppId: 'exhyperv',
  icons: {
    exhyperv: '/ui-design/app-icon.webp',
  },
  desktopShortcuts: [
    {
      file: 'ExHyperV.lnk',
      type: '快捷方式',
      size: 2048,
      icon: '/ui-design/app-icon.webp',
    },
  ],
  startupItems: [
    {
      id: 'st-exhyperv',
      name: 'ExHyperV Helper',
      publisher: 'ExHyperV Project',
      status: '已启用',
      impact: '低',
      filePath: 'C:\\Program Files\\ExHyperV\\ExHyperV.Helper.exe',
    },
  ],
  apps: [
    {
      id: 'exhyperv',
      name: 'ExHyperV',
      iconKey: 'exhyperv',
      description: '为 Windows 上的 Hyper-V 带来现代化桌面体验。',
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
      capabilities: ['theme', 'focus', 'visibility', 'app-messaging', 'window-control'],
      systemTrusted: true,
      telemetry: {
        publisher: 'ExHyperV Project',
        estimatedMemMb: 188.5,
      },
    },
    {
      id: 'exhyperv-console',
      name: '虚拟机控制台',
      iconKey: 'exhyperv',
      description: 'Hyper-V 虚拟机控制台独立连接窗口。',
      category: 'app',
      runtime: 'iframe',
      singleton: false,
      onDesktop: false,
      pinnedToStart: false,
      pinnedToTaskbar: false,
      window: {
        defaultWidth: 1024,
        defaultHeight: 680,
        minWidth: 680,
        minHeight: 440,
        seamlessTitlebar: true,
      },
      iframe: {
        entryUrl: '/apps/exhyperv-console/',
        passShellQuery: true,
      },
      capabilities: ['theme', 'focus', 'visibility', 'app-messaging', 'window-control'],
      systemTrusted: true,
      telemetry: {
        publisher: 'ExHyperV Project',
        estimatedMemMb: 96.0,
      },
    },
  ],
};
