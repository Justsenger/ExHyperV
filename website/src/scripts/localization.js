import { interfaceLanguage, languageStorageKey, normalizeLanguage } from '../data/languages.js';
import { translate } from '../data/interface-text.js';
import { selectImageSources, sourceHref } from '../data/image-source-selection.js';

const cachedSourceOptions = new WeakMap();

function savedPreference() {
  try { return localStorage.getItem(languageStorageKey) || 'auto'; } catch { return 'auto'; }
}

function activePreference() {
  // A hidden manual control must not leave the visitor stuck with an old override.
  return document.querySelector('[data-site-language]:not([hidden])') ? savedPreference() : 'auto';
}

function preferredLanguage(preference) {
  if (preference !== 'auto') {
    const language = normalizeLanguage(preference);
    return language === 'und' ? 'zh-CN' : language;
  }
  return (navigator.languages || [navigator.language]).map(normalizeLanguage).find(language => language !== 'und') || 'zh-CN';
}

function updateSources(language) {
  for (const row of document.querySelectorAll('[data-image-sources]')) {
    if (!cachedSourceOptions.has(row)) {
      const options = row.querySelector('[data-image-source-options]');
      try { cachedSourceOptions.set(row, JSON.parse(options?.textContent || '[]')); } catch { cachedSourceOptions.set(row, []); }
    }
    const sources = selectImageSources(cachedSourceOptions.get(row), { language });
    const list = row.querySelector('.sources-list');
    const template = row.querySelector('[data-image-source-template]');
    if (!list || !template) continue;
    const fragment = document.createDocumentFragment();
    for (const source of sources) {
      const item = template.content.cloneNode(true);
      const link = item.querySelector('a');
      link.href = sourceHref(source);
      link.dataset.sourceProvider = source.provider;
      link.dataset.sourceMatch = source.match;
      link.dataset.sourceStatus = source.verification?.status || 'not_checked';
      link.dataset.sourceLanguage = source.language;
      if (source.language !== 'und') link.hreflang = source.language;
      item.querySelector('span').textContent = source.displayLabel + (source.match === 'search_candidate' ? ` ${translate('searchSuffix', language)}` : '');
      fragment.append(item);
    }
    list.replaceChildren(fragment);
    row.hidden = sources.length === 0;
  }
}

export function initializeLocalization(preference = activePreference()) {
  const language = preferredLanguage(preference);
  document.documentElement.lang = interfaceLanguage(language);
  document.documentElement.dataset.preferredLanguage = language;
  for (const element of document.querySelectorAll('[data-i18n]')) element.textContent = translate(element.dataset.i18n, language);
  for (const [attribute, dataset] of [['aria-label', 'i18nAria'], ['placeholder', 'i18nPlaceholder'], ['title', 'i18nTitle']]) {
    for (const element of document.querySelectorAll(`[data-${dataset.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)}]`)) {
      element.setAttribute(attribute, translate(element.dataset[dataset], language));
    }
  }
  for (const element of document.querySelectorAll('[data-full-name-label]')) {
    element.setAttribute('aria-label', translate('fullName', language, { name: element.dataset.fullNameLabel }));
  }
  for (const image of document.querySelectorAll('[data-os-image-name]')) image.alt = `${image.dataset.osImageName} ${translate('imageScreenshot', language)}`;
  for (const select of document.querySelectorAll('[data-site-language]')) select.value = preference;
  const rawTitle = document.body.dataset.pageTitle;
  const titleKeys = { '首页': 'home', '系统图鉴': 'wiki', '文档': 'docs', '更新日志': 'changelog' };
  const title = titleKeys[rawTitle] ? translate(titleKeys[rawTitle], language) : rawTitle;
  const main = document.querySelector('#wiki-content');
  if (title) {
    document.title = document.body.dataset.module === 'home' ? 'ExHyperV' : `${title} | ExHyperV${main?.dataset.version ? ` ${translate('wiki', language)}` : ''}`;
    main?.setAttribute('aria-label', `${title} ${translate('content', language)}`);
  }
  updateSources(language);
}

document.addEventListener('change', event => {
  const target = event.target;
  if (!(target instanceof HTMLSelectElement) || !target.matches('[data-site-language]')) return;
  try {
    if (target.value === 'auto') localStorage.removeItem(languageStorageKey);
    else localStorage.setItem(languageStorageKey, target.value);
  } catch { /* A private session can still change this page's language. */ }
  initializeLocalization(target.value);
});
window.addEventListener('languagechange', () => initializeLocalization());
window.addEventListener('storage', event => { if (event.key === languageStorageKey) initializeLocalization(); });
