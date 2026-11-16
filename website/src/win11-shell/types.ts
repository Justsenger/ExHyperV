export type ThemeMode = 'dark' | 'light';

export type DesktopIconSize = 'large' | 'medium' | 'small';

export type DesktopSortMode = 'default' | 'name';

export type ActiveFlyout = 'none' | 'start' | 'quickSettings' | 'calendar' | 'trayOverflow';

export type SnapZone = 'none' | 'maximize' | 'left' | 'right';

export interface WinAppManifest {
  id: string;
  name: string;
  iconKey: string;
  description: string;
  onDesktop?: boolean;
  pinnedToStart?: boolean;
  pinnedToTaskbar?: boolean;
  defaultWidth?: number;
  defaultHeight?: number;
  /**
   * 外部或内置嵌入式 Web 应用挂载地址（同源路径或可嵌入 URL）。
   */
  entryUrl?: string;
  /**
   * 若应用自身直接延展 WinUI 3 Mica 背景，外壳标题栏取消底部分割线并无缝对齐其底色。
   */
  seamlessTitlebar?: boolean;
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
  zIndex: number;
  minimized: boolean;
  maximized: boolean;
  restoreBounds?: {
    x: number;
    y: number;
    width: number;
    height: number;
  };
  entryUrl?: string;
  seamlessTitlebar?: boolean;
}

export interface DesktopIconPosition {
  col: number;
  row: number;
}
