export const imageSourcePolicy = {
  blockedProviders: ['hellowindows'],
  blockedDomains: [
    { domain: 'hellowindows.cn', reason: '用户要求加入黑名单：使用迅雷网盘。', addedOn: '2026-10-24' },
    { domain: 'pan.xunlei.com', reason: '排除迅雷网盘入口，包括已记录的跳转目标。', addedOn: '2026-10-24' },
    { domain: 'pc528.net', reason: '用户要求移除、不再使用此来源。', addedOn: '2026-10-24' },
  ],
};

export function isBlockedSource(source) {
  if (imageSourcePolicy.blockedProviders.includes(source.origin_provider || source.provider)) return true;
  return [source.url, source.source_page_url, source.direct_download_url, source.download_url, source.verification?.final_url, source.payload_probe?.final_url].filter(Boolean).some(value => {
    try {
      const host = new URL(value).hostname.toLowerCase();
      return imageSourcePolicy.blockedDomains.some(({ domain }) => host === domain || host.endsWith(`.${domain}`));
    } catch { return true; }
  });
}
