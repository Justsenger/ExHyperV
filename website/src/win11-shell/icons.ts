const PNG_ICON_MAP: Record<string, string> = {
  start: '/win11/icons/start.png',
  'this-pc': '/win11/icons/this-pc.png',
  'recycle-bin': '/win11/icons/recycle-bin.png',
  'control-panel': '/win11/icons/control-panel.png',
  explorer: '/win11/icons/explorer.png',
  settings: '/win11/icons/settings.png',
  'task-manager': '/win11/icons/task-manager.png',
  terminal: '/win11/icons/terminal.png',
  exhyperv: '/win11/icons/exhyperv.png',
};

let fsGradSeq = 0;

/**
 * 渲染真实的 Windows 11 应用/系统图标（优先使用原生提取的高清透明 PNG）。
 */
export function getAppIconSvg(key: string, size = 32): string {
  const src = PNG_ICON_MAP[key];
  if (src) {
    return `<img src="${src}" width="${size}" height="${size}" alt="" draggable="false" loading="eager" decoding="async" style="width:${size}px;height:${size}px;object-fit:contain;display:block;pointer-events:none;" />`;
  }

  if (key === 'fullscreen') {
    const gradId = `w11FsBg-${++fsGradSeq}`;
    return `<svg width="${size}" height="${size}" viewBox="0 0 48 48" fill="none" aria-hidden="true">
      <defs>
        <linearGradient id="${gradId}" x1="6" y1="6" x2="42" y2="42" gradientUnits="userSpaceOnUse">
          <stop offset="0%" stop-color="#38BDF8"/>
          <stop offset="100%" stop-color="#0066CC"/>
        </linearGradient>
      </defs>
      <rect x="5" y="5" width="38" height="38" rx="10" fill="url(#${gradId})"/>
      <rect x="5.5" y="5.5" width="37" height="37" rx="9.5" stroke="white" stroke-opacity="0.28"/>
      <path d="M18 14H14V18M30 14H34V18M18 34H14V30M30 34H34V30" stroke="white" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/>
      <rect x="19" y="19" width="10" height="10" rx="2" fill="white" fill-opacity="0.26" stroke="white" stroke-opacity="0.7" stroke-width="1.4"/>
    </svg>`;
  }

  return `<svg width="${size}" height="${size}" viewBox="0 0 48 48" fill="none" aria-hidden="true">
    <rect x="6" y="6" width="36" height="36" rx="8" fill="#0078D4"/>
    <rect x="14" y="14" width="20" height="20" rx="3" fill="#60CDFF" fill-opacity="0.88"/>
  </svg>`;
}

export const UI_ICONS = {
  minimize: `<svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true"><path d="M2 6H10" stroke="currentColor" stroke-width="1.15" stroke-linecap="round"/></svg>`,
  maximize: `<svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true"><rect x="2.2" y="2.2" width="7.6" height="7.6" rx="1.2" stroke="currentColor" stroke-width="1.15"/></svg>`,
  restore: `<svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true"><rect x="2" y="3.8" width="6.2" height="6.2" rx="1" stroke="currentColor" stroke-width="1.1"/><path d="M4.2 3.8V2.8C4.2 2.35 4.55 2 5 2H9.2C9.65 2 10 2.35 10 2.8V7C10 7.45 9.65 7.8 9.2 7.8H8.2" stroke="currentColor" stroke-width="1.1"/></svg>`,
  close: `<svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true"><path d="M2.5 2.5L9.5 9.5M9.5 2.5L2.5 9.5" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/></svg>`,
  chevronUp: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M4.8 9.8L8 6.6L11.2 9.8" stroke="currentColor" stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
  wifi: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M1.8 5.9C5.3 2.9 10.7 2.9 14.2 5.9M4.1 8.4C6.3 6.5 9.7 6.5 11.9 8.4M6.4 10.9C7.3 10.1 8.7 10.1 9.6 10.9" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/><circle cx="8" cy="13.1" r="1.05" fill="currentColor"/></svg>`,
  volume: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M2.2 6.3H4.6L8 3.5V12.5L4.6 9.7H2.2V6.3Z" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/><path d="M10.2 5.9C11 6.9 11 9.1 10.2 10.1" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/><path d="M11.9 4.4C13.2 6.1 13.2 9.9 11.9 11.6" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/><path d="M13.6 3C15.3 5.4 15.3 10.6 13.6 13" stroke="currentColor" stroke-opacity="0.4" stroke-width="1.2" stroke-linecap="round"/></svg>`,
  battery: `<svg width="18" height="16" viewBox="0 0 18 16" fill="none" aria-hidden="true"><rect x="1.5" y="4.2" width="13.2" height="7.6" rx="2" stroke="currentColor" stroke-width="1.15"/><rect x="3" y="5.6" width="8.6" height="4.8" rx="1" fill="#82E38F"/><path d="M16 6.6V9.4" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>`,
  power: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M8 2.2V7.5M4.6 4.2C3.3 5.2 2.5 6.7 2.5 8.5C2.5 11.5 5 14 8 14C11 14 13.5 11.5 13.5 8.5C13.5 6.7 12.7 5.2 11.4 4.2" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>`,
  user: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true"><circle cx="8" cy="5.5" r="2.7" stroke="currentColor" stroke-width="1.3"/><path d="M3.2 13.2C3.8 10.8 5.7 9.6 8 9.6C10.3 9.6 12.2 10.8 12.8 13.2" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg>`,
  bluetooth: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M5 4.8L11 10.8L8 13.6V2.4L11 5.2L5 11.2" stroke="currentColor" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
  airplane: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M8 2V14M8 5.5L13.5 8.5V10L8 8.3L2.5 10V8.5L8 5.5ZM6 12.8L8 11.8L10 12.8" stroke="currentColor" stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
  moon: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M12.6 10.2C11.9 10.5 11.1 10.7 10.3 10.7C7.3 10.7 4.9 8.3 4.9 5.3C4.9 4.5 5.1 3.7 5.4 3C3.5 3.7 2.2 5.6 2.2 7.8C2.2 10.7 4.5 13 7.4 13C9.7 13 11.6 11.8 12.6 10.2Z" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/></svg>`,
  brightness: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true"><circle cx="8" cy="8" r="2.8" stroke="currentColor" stroke-width="1.3"/><path d="M8 2V3.3M8 12.7V14M2 8H3.3M12.7 8H14M3.8 3.8L4.7 4.7M11.3 11.3L12.2 12.2M12.2 3.8L11.3 4.7M4.7 11.3L3.8 12.2" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg>`,
  refresh: `<svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M13.2 8A5.2 5.2 0 1 1 11.7 4.3M13.2 2.8V5.2H10.8" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
  view: `<svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden="true"><rect x="2.2" y="2.2" width="4.8" height="4.8" rx="1" stroke="currentColor" stroke-width="1.25"/><rect x="9" y="2.2" width="4.8" height="4.8" rx="1" stroke="currentColor" stroke-width="1.25"/><rect x="2.2" y="9" width="4.8" height="4.8" rx="1" stroke="currentColor" stroke-width="1.25"/><rect x="9" y="9" width="4.8" height="4.8" rx="1" stroke="currentColor" stroke-width="1.25"/></svg>`,
  sort: `<svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M4.8 3V13M4.8 13L2.8 11M4.8 13L6.8 11M11.2 13V3M11.2 3L9.2 5M11.2 3L13.2 5" stroke="currentColor" stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
  searchSmall: `<svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden="true"><circle cx="7" cy="7" r="4.5" stroke="currentColor" stroke-width="1.35"/><path d="M10.4 10.4L13.5 13.5" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>`,
  displaySettings: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
    <rect x="1.5" y="2.2" width="13" height="9.2" rx="1.2" fill="#0B5CAD" stroke="#60CDFF" stroke-opacity="0.45" stroke-width="0.6"/>
    <path d="M2.1 2.8H13.9V10.8H2.1V2.8Z" fill="url(#w11DispGrad)"/>
    <path d="M6.5 11.4H9.5V13.2H6.5V11.4Z" fill="#9AA4B2"/>
    <path d="M4.6 13.4H11.4" stroke="#B8C2CC" stroke-width="1.2" stroke-linecap="round"/>
    <defs><linearGradient id="w11DispGrad" x1="2" y1="2.8" x2="14" y2="11" gradientUnits="userSpaceOnUse"><stop offset="0%" stop-color="#4CC2FF"/><stop offset="100%" stop-color="#005FB8"/></linearGradient></defs>
  </svg>`,
  personalize: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
    <rect x="1.5" y="2.2" width="12.5" height="8.8" rx="1.2" fill="#1C2D4F" stroke="#7CC4FF" stroke-opacity="0.4" stroke-width="0.6"/>
    <path d="M2.2 7.5C4.2 5.2 7.4 4.8 11.2 6.8L13.3 8.2V10.3H2.2V7.5Z" fill="#0078D4"/>
    <path d="M3.4 9.5C5.5 7.4 8.5 7.5 11.4 9.6H3.4V9.5Z" fill="#60CDFF" fill-opacity="0.85"/>
    <path d="M6.3 11H9.2V12.8H6.3V11Z" fill="#9AA4B2"/>
    <path d="M4.5 13H11" stroke="#B8C2CC" stroke-width="1.2" stroke-linecap="round"/>
    <path d="M10.4 9.8L13.8 6.4C14.2 6 14.8 6 15.1 6.4C15.4 6.7 15.4 7.3 15 7.7L11.6 11.1L10.4 9.8Z" fill="#E8E8E8"/>
    <path d="M9.4 11.5C9.4 10.7 10 10.1 10.7 10.1L11.5 10.9C11.5 11.7 10.7 12.4 9.8 12.4C9.5 12.4 9.2 12.2 9.4 11.5Z" fill="#38BDF8"/>
  </svg>`,
  folder: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
    <path d="M1.5 3.5C1.5 2.95 1.95 2.5 2.5 2.5H6.1C6.45 2.5 6.78 2.68 6.96 2.98L7.6 4H13.5C14.05 4 14.5 4.45 14.5 5V12.5C14.5 13.05 14.05 13.5 13.5 13.5H2.5C1.95 13.5 1.5 13.05 1.5 12.5V3.5Z" fill="#E8A923"/>
    <path d="M1.5 5.2H14.5V12.5C14.5 13.05 14.05 13.5 13.5 13.5H2.5C1.95 13.5 1.5 13.05 1.5 12.5V5.2Z" fill="#FAD24B"/>
  </svg>`,
  shortcut: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
    <rect x="2" y="2" width="12" height="12" rx="2" fill="#F3F4F6" stroke="#9CA3AF" stroke-width="0.9"/>
    <rect x="3.5" y="3.5" width="9" height="9" rx="1.2" fill="#0078D4"/>
    <path d="M6 10L10 6M10 6H6.8M10 6V9.2" stroke="#FFFFFF" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/>
  </svg>`,
};
