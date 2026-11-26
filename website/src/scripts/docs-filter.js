export function initializeDocsPages() {
  for (const root of document.querySelectorAll('[data-docs-filter-root]')) {
    if (root.dataset.filterBound === 'true') continue;
    root.dataset.filterBound = 'true';

    const items = [...root.querySelectorAll('[data-filter-item]')];
    const buttons = [...root.querySelectorAll('[data-filter-group][data-filter-value]')];
    const searchInput = root.querySelector('[data-filter-search]');
    const countLabel = root.querySelector('[data-filter-count]');
    const emptyBanner = root.querySelector('[data-filter-empty]');
    const activeFilters = new Map();

    for (const button of buttons) {
      if (button.getAttribute('aria-pressed') === 'true') {
        activeFilters.set(button.dataset.filterGroup, button.dataset.filterValue);
      }
    }

    function applyFilter() {
      const query = (searchInput?.value || '').trim().toLowerCase();
      let visibleCount = 0;

      for (const item of items) {
        let matched = true;
        for (const [group, selected] of activeFilters.entries()) {
          if (!selected || selected === 'all') continue;
          const attrName = `filter${group.charAt(0).toUpperCase()}${group.slice(1)}`;
          const rawTokens = (item.dataset[attrName] || '').trim().toLowerCase().split(/\s+/).filter(Boolean);
          if (!rawTokens.includes(selected.toLowerCase())) {
            matched = false;
            break;
          }
        }
        if (matched && query) {
          const text = (item.dataset.searchText || item.textContent || '').toLowerCase();
          if (!text.includes(query)) matched = false;
        }
        item.hidden = !matched;
        if (matched) visibleCount += 1;
      }

      if (countLabel) countLabel.textContent = `显示 ${visibleCount} / ${items.length} 条`;
      if (emptyBanner) emptyBanner.hidden = visibleCount > 0 || items.length === 0;
    }

    root.addEventListener('click', (event) => {
      const target = event.target instanceof Element ? event.target : null;
      const button = target?.closest('[data-filter-group][data-filter-value]');
      if (!button) return;
      const group = button.dataset.filterGroup;
      const value = button.dataset.filterValue;
      activeFilters.set(group, value);
      for (const sibling of buttons) {
        if (sibling.dataset.filterGroup === group) {
          sibling.setAttribute('aria-pressed', String(sibling === button));
        }
      }
      applyFilter();
    });

    searchInput?.addEventListener('input', applyFilter);
    applyFilter();
  }

  for (const button of document.querySelectorAll('[data-copy-text]')) {
    if (button.dataset.copyBound === 'true') continue;
    button.dataset.copyBound = 'true';
    button.addEventListener('click', async () => {
      const value = button.dataset.copyText || '';
      if (!value) return;
      const original = button.textContent;
      try {
        await navigator.clipboard?.writeText(value);
        button.textContent = '已复制';
        setTimeout(() => { button.textContent = original; }, 1600);
      } catch {
        button.textContent = '复制失败';
        setTimeout(() => { button.textContent = original; }, 1600);
      }
    });
  }
}

document.addEventListener('astro:page-load', initializeDocsPages);
