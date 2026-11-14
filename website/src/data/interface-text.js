import { interfaceLanguage } from './languages.js';

const text = {
  'zh-CN': {
    rotateModel: 'ExHyperV 三维图标，拖拽或使用方向键旋转，双击复位',
    productDescription: 'Windows 上的 Hyper-V 管理工具。', download: '下载', viewSource: '查看源码', releases: '版本与更新',
    home: '首页', wiki: '系统图鉴', docs: '文档', changelog: '更新日志', navigation: '主站导航', categories: '系统分类',
    search: '搜索...', searchVersions: '搜索版本', expandCategories: '展开分类', closeCategories: '关闭分类',
    architectures: '系统架构', architecture: '架构', imageSources: '镜像来源', searchSuffix: '检索',
    fullName: '查看完整名称：{name}', content: '内容', imagePlaceholder: '图片占位', imageScreenshot: '系统截图',
    supportedWddm: '系统支持的 WDDM 版本', supportedXddm: '系统支持 XDDM 显示驱动模型', language: '语言', automatic: '自动',
    buildNumberLabel: '构建版本号', graphicsVersionLabel: '图形驱动版本',
  },
  'zh-TW': {
    rotateModel: 'ExHyperV 三維圖示，拖曳或使用方向鍵旋轉，按兩下重設',
    productDescription: 'Windows 上的 Hyper-V 管理工具。', download: '下載', viewSource: '查看原始碼', releases: '版本與更新',
    home: '首頁', wiki: '系統圖鑑', docs: '文件', changelog: '更新日誌', navigation: '主站導覽', categories: '系統分類',
    search: '搜尋...', searchVersions: '搜尋版本', expandCategories: '展開分類', closeCategories: '關閉分類',
    architectures: '系統架構', architecture: '架構', imageSources: '映像來源', searchSuffix: '搜尋',
    fullName: '查看完整名稱：{name}', content: '內容', imagePlaceholder: '圖片預留位置', imageScreenshot: '系統截圖',
    supportedWddm: '系統支援的 WDDM 版本', supportedXddm: '系統支援 XDDM 顯示驅動模型', language: '語言', automatic: '自動',
    buildNumberLabel: '組建版本號', graphicsVersionLabel: '圖形驅動版本',
  },
  en: {
    rotateModel: 'ExHyperV 3D icon. Drag or use arrow keys to rotate; double-click to reset.',
    productDescription: 'A Hyper-V management tool for Windows.', download: 'Download', viewSource: 'View source', releases: 'Releases and updates',
    home: 'Home', wiki: 'System Gallery', docs: 'Docs', changelog: 'Changelog', navigation: 'Main navigation', categories: 'System categories',
    search: 'Search...', searchVersions: 'Search versions', expandCategories: 'Open categories', closeCategories: 'Close categories',
    architectures: 'System architectures', architecture: 'Architecture', imageSources: 'Image sources', searchSuffix: 'search',
    fullName: 'Show full name: {name}', content: 'content', imagePlaceholder: 'image placeholder', imageScreenshot: 'system screenshot',
    supportedWddm: 'WDDM version supported by the system', supportedXddm: 'XDDM display driver model supported by the system', language: 'Language', automatic: 'Auto',
    buildNumberLabel: 'Build', graphicsVersionLabel: 'WDDM Version',
  },
};

export function translate(key, language = 'zh-CN', values = {}) {
  let result = text[interfaceLanguage(language)][key] || key;
  for (const [name, value] of Object.entries(values)) result = result.replaceAll(`{${name}}`, String(value));
  return result;
}
