import { interfaceLanguage, languageStorageKey, normalizeLanguage, normalizeLanguagePreference } from '../data/languages.js';
import { translate } from '../data/interface-text.js';
import { selectImageSources, sourceHref } from '../data/image-source-selection.js';

const cachedSourceOptions = new WeakMap();

function savedPreference() {
  try { return normalizeLanguagePreference(localStorage.getItem(languageStorageKey)); } catch { return 'auto'; }
}

function activePreference() {
  return savedPreference();
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

const optionNames = {
  'zh-CN': '简体中文',
  'zh-TW': '繁體中文',
  en: 'English',
};

function closeLanguageMenus() {
  for (const root of document.querySelectorAll('[data-site-language]')) {
    const trigger = root.querySelector('[data-lang-trigger]');
    const menu = root.querySelector('[data-lang-menu]');
    if (trigger) trigger.setAttribute('aria-expanded', 'false');
    if (menu) menu.hidden = true;
  }
}

function selectLanguagePreference(value, returnFocus = false) {
  const preference = normalizeLanguagePreference(value);
  try {
    if (preference === 'auto') localStorage.removeItem(languageStorageKey);
    else localStorage.setItem(languageStorageKey, preference);
  } catch { /* A private session can still change this page's language. */ }
  closeLanguageMenus();
  initializeLocalization(preference);
  if (returnFocus) {
    document.querySelector('[data-site-language] [data-lang-trigger]')?.focus({ preventScroll: true });
  }
}

export function initializeLocalization(preference = activePreference()) {
  const normalizedPreference = normalizeLanguagePreference(preference);
  const browserUiLang = interfaceLanguage(preferredLanguage('auto'));
  const language = preferredLanguage(normalizedPreference);
  const uiLang = interfaceLanguage(language);
  document.documentElement.lang = uiLang;
  document.documentElement.dataset.uiLang = uiLang;
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
  for (const root of document.querySelectorAll('[data-site-language]')) {
    if (root instanceof HTMLSelectElement) {
      root.value = normalizedPreference;
      continue;
    }
    const label = root.querySelector('[data-site-language-label]');
    if (label) {
      label.textContent = normalizedPreference === 'auto'
        ? translate('automatic', language)
        : (optionNames[normalizedPreference] || translate('automatic', language));
    }
    const detected = root.querySelector('[data-site-language-detected]');
    if (detected) detected.textContent = optionNames[browserUiLang] || '简体中文';
    for (const option of root.querySelectorAll('[data-lang-option]')) {
      option.setAttribute('aria-selected', String(option.dataset.langOption === normalizedPreference));
    }
  }
  const rawTitle = document.body.dataset.pageTitle;
  const titleKeys = {
    '首页': 'home',
    '系统图鉴': 'wiki',
    '文档': 'docs',
    'GPU-PV 显卡驱动要求': 'gpuDocs',
    'GPU-PV 驱动研究档案': 'gpuResearchDocs',
    '在线体验': 'uiDesign',
    '界面设计': 'uiDesign',
    'UI设计': 'uiDesign',
    '更新日志': 'changelog',
  };
  const title = titleKeys[rawTitle] ? translate(titleKeys[rawTitle], language) : rawTitle;
  const main = document.querySelector('#wiki-content');
  if (title) {
    document.title = document.body.dataset.module === 'home' ? 'ExHyperV' : `${title} | ExHyperV${main?.dataset.version ? ` ${translate('wiki', language)}` : ''}`;
    main?.setAttribute('aria-label', `${title} ${translate('content', language)}`);
  }
  updateSources(language);
  document.documentElement.classList.remove('i18n-pending');
}

document.addEventListener('change', event => {
  const target = event.target;
  if (!(target instanceof HTMLSelectElement) || !target.matches('[data-site-language]')) return;
  selectLanguagePreference(target.value);
});

document.addEventListener('click', event => {
  const target = event.target instanceof Element ? event.target : null;
  const option = target?.closest('[data-lang-option]');
  if (option) {
    selectLanguagePreference(option.dataset.langOption, event.detail === 0);
    return;
  }
  const trigger = target?.closest('[data-lang-trigger]');
  if (trigger) {
    const root = trigger.closest('[data-site-language]');
    const menu = root?.querySelector('[data-lang-menu]');
    if (!menu) return;
    const willOpen = menu.hidden;
    closeLanguageMenus();
    if (willOpen) {
      menu.hidden = false;
      trigger.setAttribute('aria-expanded', 'true');
      if (event.detail === 0) {
        const active = menu.querySelector('[aria-selected="true"]') || menu.querySelector('[data-lang-option]');
        active?.focus({ preventScroll: true });
      }
    }
    return;
  }
  if (!target?.closest('[data-site-language]')) closeLanguageMenus();
});

document.addEventListener('keydown', event => {
  const activeMenu = [...document.querySelectorAll('[data-lang-menu]')].find(menu => !menu.hidden);
  const trigger = document.activeElement instanceof Element ? document.activeElement.closest('[data-lang-trigger]') : null;
  if (!activeMenu && trigger && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
    event.preventDefault();
    const menu = trigger.closest('[data-site-language]')?.querySelector('[data-lang-menu]');
    if (!menu) return;
    menu.hidden = false;
    trigger.setAttribute('aria-expanded', 'true');
    const options = [...menu.querySelectorAll('[data-lang-option]')];
    const targetOption = event.key === 'ArrowUp' ? options.at(-1) : (menu.querySelector('[aria-selected="true"]') || options[0]);
    targetOption?.focus({ preventScroll: true });
    return;
  }
  if (!activeMenu) return;
  if (event.key === 'Escape') {
    const ownerTrigger = activeMenu.closest('[data-site-language]')?.querySelector('[data-lang-trigger]');
    closeLanguageMenus();
    ownerTrigger?.focus({ preventScroll: true });
    return;
  }
  if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
  const options = [...activeMenu.querySelectorAll('[data-lang-option]')];
  if (!options.length) return;
  event.preventDefault();
  const currentIndex = options.indexOf(document.activeElement);
  let nextIndex = 0;
  if (event.key === 'ArrowDown') nextIndex = currentIndex >= 0 ? (currentIndex + 1) % options.length : 0;
  else if (event.key === 'ArrowUp') nextIndex = currentIndex >= 0 ? (currentIndex - 1 + options.length) % options.length : options.length - 1;
  else if (event.key === 'End') nextIndex = options.length - 1;
  options[nextIndex]?.focus({ preventScroll: true });
});

document.addEventListener('astro:before-preparation', closeLanguageMenus);
window.addEventListener('languagechange', () => initializeLocalization());
window.addEventListener('storage', event => { if (event.key === languageStorageKey) initializeLocalization(); });
