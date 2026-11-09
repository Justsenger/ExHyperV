// These are interface preferences, not changes to product or version names.
export const languageStorageKey = 'exhyperv.language';

export function normalizeLanguage(value = 'und') {
  const raw = String(value || 'und').trim().replaceAll('_', '-');
  const alias = { cn: 'zh-CN', sc: 'zh-CN', chs: 'zh-CN', cht: 'zh-TW', 'zh-hans': 'zh-CN', 'zh-hant': 'zh-TW', zh: 'zh-CN' };
  if (alias[raw.toLowerCase()]) return alias[raw.toLowerCase()];
  try { return Intl.getCanonicalLocales(raw)[0] || 'und'; } catch { return 'und'; }
}

export function interfaceLanguage(language) {
  const locale = normalizeLanguage(language);
  if (locale.startsWith('zh')) return /TW|HK|Hant/.test(locale) ? 'zh-TW' : 'zh-CN';
  return 'en';
}

export function inferSourceLanguage(source) {
  if (source.language) return normalizeLanguage(source.language);
  const filename = source.declared_file?.name || source.filename || '';
  let value = filename;
  if (!value) {
    try { value = decodeURIComponent(new URL(source.url).pathname.split('/').pop() || ''); } catch { /* Unknown is retained. */ }
  }
  if (/^zh[-_]hans/i.test(value)) return 'zh-CN';
  if (/^zh[-_]hant/i.test(value)) return 'zh-TW';
  const prefix = value.match(/^(zh[-_](?:cn|tw|hk)|[a-z]{2}(?:-[a-z]{2})?)[_ .]/i);
  if (prefix && !['mu', 'sw', 'hrm'].includes(prefix[1].toLowerCase())) return normalizeLanguage(prefix[1]);
  const words = { english: 'en', french: 'fr', german: 'de', spanish: 'es', espanol: 'es', russian: 'ru', japanese: 'ja', korean: 'ko' };
  for (const [word, locale] of Object.entries(words)) if (new RegExp(`(?:^|[ _.-])${word}(?:$|[ _.-])`, 'i').test(value)) return locale;
  const suffix = value.match(/[_ ](en|de|es|it|ru)\.(?:zip|7z|rar)$/i);
  return suffix ? normalizeLanguage(suffix[1]) : 'und';
}

export function languageRank(language, preferred = 'zh-CN') {
  const candidate = normalizeLanguage(language);
  const wanted = normalizeLanguage(preferred);
  if (candidate === 'und') return 5;
  if (candidate === wanted) return 0;
  if (candidate.startsWith('zh') && wanted.startsWith('zh')) {
    return new Intl.Locale(candidate).maximize().script === new Intl.Locale(wanted).maximize().script ? 1 : 2;
  }
  if (candidate !== 'und' && candidate.split('-')[0] === wanted.split('-')[0]) return 1;
  if (candidate.startsWith('zh')) return 2;
  if (candidate.startsWith('en')) return 3;
  return 4;
}
