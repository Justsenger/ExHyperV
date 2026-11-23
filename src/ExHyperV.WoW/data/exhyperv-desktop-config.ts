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
      systemTrusted: true,
      telemetry: {
        publisher: 'ExHyperV Project',
        estimatedMemMb: 188.5,
      },
    },
  ],
};
