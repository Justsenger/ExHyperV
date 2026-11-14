import { initializeLocalization } from './localization.js';

const motions = new WeakMap();
let fullNameTarget;
let fullNamePinned = false;
let nameMeasureFrame;

function hideFullName() {
  document.querySelector('#full-name-popup')?.setAttribute('hidden', '');
  fullNameTarget?.removeAttribute('aria-describedby');
  fullNameTarget = undefined;
  fullNamePinned = false;
}

function nameTarget(element) {
  const info = element.closest?.('[data-name-for]');
  return info ? document.getElementById(info.dataset.nameFor) : element.closest?.('[data-full-name]');
}

function showFullName(target, pinned = false) {
  if (!target) return;
  const label = target.querySelector('.nav-label');
  if (!label || (label.textContent.trim() === target.dataset.fullName && !target.classList.contains('has-name-overflow'))) return;
  const popup = document.querySelector('#full-name-popup');
  if (!popup) return;
  fullNameTarget?.removeAttribute('aria-describedby');
  fullNameTarget = target;
  fullNamePinned = pinned;
  popup.textContent = target.dataset.fullName;
  popup.hidden = false;
  popup.style.visibility = 'hidden';
  const rect = target.getBoundingClientRect();
  const bounds = popup.getBoundingClientRect();
  const sidebar = document.querySelector('#slide-out').getBoundingClientRect();
  const beside = sidebar.right + 8 + bounds.width <= innerWidth - 12;
  const left = beside ? sidebar.right + 8 : Math.max(12, Math.min(rect.left, innerWidth - bounds.width - 12));
  const top = beside ? rect.top : rect.bottom + 6;
  popup.style.left = `${left}px`;
  popup.style.top = `${Math.max(12, Math.min(top, innerHeight - bounds.height - 12))}px`;
  popup.style.removeProperty('visibility');
  target.setAttribute('aria-describedby', popup.id);
}

function measureNames() {
  cancelAnimationFrame(nameMeasureFrame);
  nameMeasureFrame = requestAnimationFrame(() => {
    const sources = [...document.querySelectorAll('#slide-out [data-full-name]')];
    sources.forEach(source => source.classList.remove('has-name-overflow'));
    for (const source of sources) {
      const label = source.querySelector('.nav-label');
      const overflow = label.clientWidth > 0 && label.scrollWidth > label.clientWidth + 1;
      source.classList.toggle('has-name-overflow', overflow);
      const info = source.nextElementSibling;
      if (info?.matches('[data-name-for]')) info.hidden = !overflow;
    }
  });
}

function disclosure(row, open, animate = true) {
  const button = row.querySelector(':scope > .collapsible-header');
  const body = row.querySelector(':scope > .collapsible-body');
  if (!button || !body || (row.classList.contains('active') === open && !motions.has(body))) return;
  const from = body.hidden ? 0 : body.getBoundingClientRect().height;
  motions.get(body)?.cancel();
  motions.delete(body);
  row.classList.toggle('active', open);
  button.setAttribute('aria-expanded', String(open));
  body.hidden = false;
  body.style.height = 'auto';
  const to = open ? body.getBoundingClientRect().height : 0;
  if (!animate || matchMedia('(prefers-reduced-motion: reduce)').matches) {
    body.hidden = !open;
    body.style.removeProperty('height');
    body.style.removeProperty('overflow');
    return;
  }
  body.style.overflow = 'hidden';
  const animation = body.animate([
    { height: `${from}px`, paddingTop: from ? '8px' : '0px' },
    { height: `${to}px`, paddingTop: open ? '8px' : '0px' },
  ], { duration: 300, easing: 'cubic-bezier(.645,.045,.355,1)' });
  motions.set(body, animation);
  animation.onfinish = () => {
    if (motions.get(body) !== animation) return;
    body.hidden = !open;
    body.style.removeProperty('height');
    body.style.removeProperty('overflow');
    motions.delete(body);
  };
}

function openRow(row, animate = true) {
  for (const sibling of row.parentElement.children) {
    if (sibling !== row && sibling.classList.contains('active')) disclosure(sibling, false, animate);
  }
  disclosure(row, true, animate);
}

function reveal(link, animate = true) {
  const parents = [];
  let parent = link.parentElement.parentElement.closest('li');
  while (parent && parent.closest('#version-categories')) {
    if (parent.querySelector(':scope > .collapsible-header')) parents.unshift(parent);
    parent = parent.parentElement.closest('li');
  }
  parents.forEach(row => openRow(row, animate));
}

function closeSidebar() {
  document.querySelector('#slide-out')?.classList.remove('is-open');
  document.querySelector('#sidebar-toggle')?.setAttribute('aria-expanded', 'false');
  const backdrop = document.querySelector('#wiki-backdrop');
  if (backdrop) backdrop.hidden = true;
}

function initializeVersionOptions() {
  const params = new URLSearchParams(location.search);
  // Old links now select the build alone; product editions are not a filter.
  params.delete('edition');
  let selectedArchitecture;
  const architectureOptions = document.querySelector('[data-architecture-options]');
  if (architectureOptions) {
    const requested = params.get('arch');
    const choices = [...architectureOptions.querySelectorAll('[data-architecture-choice]')];
    const supported = [
      ...choices.map(choice => choice.dataset.architectureChoice),
      ...[...architectureOptions.querySelectorAll('[data-architecture-static]')].map(choice => choice.dataset.architectureStatic),
    ];
    const fallback = architectureOptions.dataset.defaultArchitecture;
    selectedArchitecture = supported.includes(requested) ? requested : supported.includes(fallback)
      ? fallback : supported[0];
    if (params.has('arch') && !supported.includes(requested)) params.set('arch', selectedArchitecture);
    for (const choice of choices) {
      const architecture = choice.dataset.architectureChoice;
      if (architecture === selectedArchitecture) choice.setAttribute('aria-current', 'true');
      else choice.removeAttribute('aria-current');
    }
  }
  if (!architectureOptions) params.delete('arch');
  const search = params.toString();
  if (search !== location.search.slice(1)) history.replaceState(history.state, '', location.pathname + (search ? `?${search}` : '') + location.hash);
  const content = document.querySelector('#wiki-content');
  if (content) {
    if (selectedArchitecture) content.dataset.architecture = selectedArchitecture;
    for (const preview of content.querySelectorAll('[data-architecture-preview], [data-architecture-tags]')) {
      const architecture = preview.dataset.architecturePreview || preview.dataset.architectureTags;
      preview.hidden = architecture !== (selectedArchitecture || 'default');
    }
  }
}

function initialize() {
  initializeLocalization();
  initializeVersionOptions();
  // Preserve URLs shared before the migration from hash routing.
  if (location.hash.startsWith('#/')) {
    const old = location.hash.slice(1);
    if (old === '/home' || old === '/') location.replace('/');
    else if (/^\/(os(?:\/[a-z0-9-]+)?|docs|api|changelog)$/.test(old)) location.replace(old + '/');
  }
  const sidebar = document.querySelector('#slide-out');
  const menu = document.querySelector('#sidebar-toggle');
  if (menu && !menu.dataset.bound) {
    menu.dataset.bound = 'true';
    menu.addEventListener('click', () => {
      const currentSidebar = document.querySelector('#slide-out');
      const open = currentSidebar.classList.toggle('is-open');
      menu.setAttribute('aria-expanded', String(open));
      document.querySelector('#wiki-backdrop').hidden = !open;
    });
  }
  const backdrop = document.querySelector('#wiki-backdrop');
  if (backdrop && !backdrop.dataset.bound) {
    backdrop.dataset.bound = 'true';
    backdrop.addEventListener('click', closeSidebar);
  }
  if (!sidebar) return;
  const list = sidebar.querySelector('#version-categories');
  if (!sidebar.dataset.bound) {
    sidebar.dataset.bound = 'true';
    list.addEventListener('click', event => {
      const header = event.target.closest('.collapsible-header');
      if (!header) return;
      const row = header.parentElement;
      if (row.classList.contains('active')) disclosure(row, false);
      else openRow(row);
    });
    sidebar.querySelector('#version-search').addEventListener('input', event => {
      const query = event.target.value.trim().toLowerCase();
      let firstMatch;
      for (const familyRow of list.children) {
        const familyMatches = familyRow.dataset.family.toLowerCase().includes(query);
        let familyMatched = false;
        for (const groupRow of familyRow.querySelector('.system-subcategories').children) {
          const groupMatches = familyMatches || groupRow.dataset.groupName.toLowerCase().includes(query);
          let matched = false;
          for (const link of groupRow.querySelectorAll('a[data-version]')) {
            const item = link.closest('li');
            const searchable = `${link.dataset.fullName} ${link.dataset.architecture || ''}`.toLowerCase();
            item.hidden = !groupMatches && !searchable.includes(query);
            matched ||= !item.hidden;
            if (!item.hidden && !firstMatch) firstMatch = link;
          }
          for (const collection of groupRow.querySelectorAll('[data-collection]')) {
            collection.hidden = ![...collection.querySelectorAll('a[data-version]')].some(link => !link.closest('li').hidden);
          }
          groupRow.hidden = !matched;
          familyMatched ||= matched;
        }
        familyRow.hidden = !familyMatched;
      }
      if (query && firstMatch) reveal(firstMatch);
      measureNames();
    });
    new ResizeObserver(measureNames).observe(sidebar.querySelector('.sidebar'));
  }
  const content = document.querySelector('#wiki-content');
  const selected = content.dataset.navigationVersion || content.dataset.version;
  for (const link of list.querySelectorAll('a[data-version]')) {
    const active = link.dataset.version === selected;
    link.classList.toggle('router-link-active', active);
    link.classList.toggle('router-link-exact-active', active);
    if (active) { link.setAttribute('aria-current', 'page'); reveal(link, false); }
    else link.removeAttribute('aria-current');
  }
  closeSidebar();
  if (sidebar.dataset.scrollPosition !== undefined) sidebar.scrollTop = Number(sidebar.dataset.scrollPosition);
  measureNames();
  document.fonts.ready.then(measureNames);
}

document.addEventListener('astro:page-load', initialize);
document.addEventListener('astro:before-preparation', hideFullName);
document.addEventListener('astro:before-swap', () => {
  const sidebar = document.querySelector('#slide-out');
  if (sidebar) sidebar.dataset.scrollPosition = String(sidebar.scrollTop);
});
document.addEventListener('keydown', event => { if (event.key === 'Escape') { hideFullName(); closeSidebar(); } });
document.addEventListener('pointerover', event => {
  if (event.pointerType === 'touch' || fullNamePinned) return;
  const target = nameTarget(event.target);
  if (target) showFullName(target);
});

document.addEventListener('pointerout', event => {
  if (fullNamePinned || !fullNameTarget) return;
  const next = nameTarget(event.relatedTarget || {});
  if (next !== fullNameTarget && !event.relatedTarget?.closest?.('#full-name-popup')) hideFullName();
});

document.addEventListener('focusin', event => { const target = nameTarget(event.target); if (target) showFullName(target); });
document.addEventListener('focusout', event => { if (!fullNamePinned && !nameTarget(event.relatedTarget || {})) hideFullName(); });

document.addEventListener('click', event => {
  const familyEntry = event.target.closest('[data-open-family]');
  if (familyEntry) {
    const sidebar = document.querySelector('#slide-out');
    const family = [...sidebar.querySelectorAll('#version-categories > li')]
      .find(row => row.dataset.family === familyEntry.dataset.openFamily);
    const search = sidebar.querySelector('#version-search');
    search.value = '';
    search.dispatchEvent(new Event('input', { bubbles: true }));
    hideFullName();
    openRow(family, false);
    if (matchMedia('(max-width: 999px)').matches) {
      sidebar.classList.add('is-open');
      document.querySelector('#sidebar-toggle').setAttribute('aria-expanded', 'true');
      document.querySelector('#wiki-backdrop').hidden = false;
    }
    const heading = family.querySelector(':scope > .collapsible-header');
    heading.focus({ preventScroll: true });
    heading.scrollIntoView({ block: 'nearest' });
    measureNames();
    return;
  }
  const info = event.target.closest('[data-name-for]');
  if (info) {
    const target = nameTarget(info);
    if (fullNamePinned && target === fullNameTarget) hideFullName();
    else showFullName(target, true);
  } else if (!event.target.closest('#full-name-popup')) hideFullName();
});

document.addEventListener('scroll', () => {
  const focused = nameTarget(document.activeElement);
  if (focused && focused === fullNameTarget && !fullNamePinned) {
    requestAnimationFrame(() => showFullName(focused));
  } else hideFullName();
}, true);
window.addEventListener('resize', () => { hideFullName(); measureNames(); });
