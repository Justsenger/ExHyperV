import { inferSourceLanguage, languageRank, interfaceLanguage } from './languages.js';
import { isBlockedSource } from './image-source-policy.js';

// Keep language alternatives in the data; show one precise link per publisher.
export function sourceProvider(source) {
  if (source.origin_provider === 'massgrave') return { id: 'massgrave', label: 'Massgrave' };
  if (source.origin_provider === 'bobpony') return { id: 'bobpony', label: 'Bob Pony' };
  const host = new URL(source.url).hostname.replace(/^www\./, '');
  if (host === 'microsoft.com' || host.endsWith('.microsoft.com') || host.endsWith('.windowsupdate.com') || host === 'my.visualstudio.com') return { id: 'microsoft', label: 'Microsoft' };
  if (host === 'archive.org' || host.endsWith('.archive.org')) return { id: 'archive', label: 'Internet Archive' };
  if (host === 'ubuntu.com' || host.endsWith('.ubuntu.com')) return { id: 'ubuntu', label: 'Ubuntu 官方' };
  if (host === 'github.com' && new URL(source.url).pathname.startsWith('/ms-iot/')) return { id: 'microsoft', label: 'Microsoft' };
  const providers = {
    'uupdump.net': ['uup', 'UUP dump'],
    'mirrors.aliyun.com': ['aliyun', '阿里云'],
    'mirrors.tuna.tsinghua.edu.cn': ['tuna', '清华大学'],
    'mirrors.huaweicloud.com': ['huawei', '华为云'],
    'winworldpc.com': ['winworld', 'WinWorld'],
    'sdfox7.com': ['sdfox7', 'SDfox7'],
    'legacyupdate.net': ['legacy-update', 'Legacy Update'],
    'files.rg-adguard.net': ['rg-adguard', 'RG-Adguard'],
    'msembedded.biz': ['msembedded', 'MS Embedded'],
    'betaarchive.com': ['betaarchive', 'BetaArchive'],
    'mirror.ps.kz': ['ps-internet', 'PS Internet'],
    'dl.bobpony.com': ['bobpony', 'Bob Pony'],
    'massgrave.dev': ['massgrave', 'Massgrave'],
    'imsdn.cn': ['imsdn', 'IMSDN'],
    'mirror.winx.run': ['winx', 'WinX-M'],
  };
  const [id, label] = providers[host] || [host, host];
  return { id, label };
}

export function sourceHref(source) {
  // Keep the original file URL in the data, but open a specific reviewable page when supplied.
  const candidate = source.source_page_url || source.url;
  try { return /^https?:$/.test(new URL(candidate).protocol) ? candidate : source.url; }
  catch { return source.url; }
}

export function isEligibleSource(source) {
  return source.display_eligible !== false && !isBlockedSource(source);
}

function matchRank(source) {
  return { exact_version_arch: 4, exact_version_arch_for_selected_files: 4, current_release_arch: 4,
    exact_version: 3, exact_service_pack_update: 3, version_matches_originality_unverified: 3,
    family_collection: 2, search_candidate: 1 }[source.match] || 0;
}

function priority(source) {
  const match = matchRank(source);
  const checked = ['page_accessible', 'headers_accessible', 'metadata_verified', 'search_metadata_verified'].includes(source.verification?.status)
    || /verified|matched/.test(source.verification?.status || '');
  const kind = { official_media: 5, mirror: 4, uup_build: 4, exact_release_page: 3, installer_components: 2, archive_collection: 1, search_page: 0 }[source.kind] || 0;
  return match * 100 + Number(checked) * 10 + kind;
}

function compareSources(a, b, language) {
  return matchRank(b) - matchRank(a)
    || languageRank(inferSourceLanguage(a), language) - languageRank(inferSourceLanguage(b), language)
    || priority(b) - priority(a);
}

function localizedProviderLabel(provider, source, language) {
  if (provider.id === 'microsoft') return 'Microsoft';
  if (interfaceLanguage(language) === 'zh-TW') return { ubuntu: 'Ubuntu 官方',
    aliyun: '阿里雲', tuna: '清華大學', huawei: '華為雲' }[provider.id] || provider.label;
  if (interfaceLanguage(language) !== 'en') return provider.label;
  return { ubuntu: 'Ubuntu official',
    aliyun: 'Alibaba Cloud', tuna: 'Tsinghua University', huawei: 'Huawei Cloud' }[provider.id] || provider.label;
}

export function selectImageSources(sources = [], { language = 'zh-CN' } = {}) {
  const selected = new Map();
  for (const base of sources) {
    if (!isEligibleSource(base)) continue;
    const alternatives = base.variants?.length ? base.variants.map(variant => ({ ...base, ...variant, variants: undefined })) : [base];
    for (const source of alternatives) {
    if (!isEligibleSource(source)) continue;
    const provider = sourceProvider(source);
    const previous = selected.get(provider.id);
    if (!previous || compareSources(source, previous, language) < 0) {
      selected.set(provider.id, { ...source, language: inferSourceLanguage(source), provider: provider.id, displayLabel: localizedProviderLabel(provider, source, language) });
    }
    }
  }
  return [...selected.values()].sort((a, b) => compareSources(a, b, language));
}
