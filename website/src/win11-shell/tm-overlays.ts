export interface TmMenuItem {
  type?: 'divider';
  label?: string;
  shortcut?: string;
  checked?: boolean;
  checkedType?: 'check' | 'radio';
  disabled?: boolean;
  onSelect?: () => void;
  submenu?: TmMenuItem[];
}

export interface TmTooltipData {
  title: string;
  body?: string;
}

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

export function createTmMenuController(hostEl: HTMLElement) {
  let activeMenuEl: HTMLElement | null = null;
  let cleanupFns: Array<() => void> = [];

  function close() {
    cleanupFns.forEach((fn) => fn());
    cleanupFns = [];
    if (activeMenuEl) {
      activeMenuEl.remove();
      activeMenuEl = null;
    }
  }

  function clientToHost(clientX: number, clientY: number) {
    const rect = hostEl.getBoundingClientRect();
    const scaleX = rect.width > 0 ? rect.width / (hostEl.offsetWidth || rect.width) : 1;
    const scaleY = rect.height > 0 ? rect.height / (hostEl.offsetHeight || rect.height) : 1;
    return {
      x: (clientX - rect.left) / scaleX,
      y: (clientY - rect.top) / scaleY,
      w: hostEl.offsetWidth || rect.width,
      h: hostEl.offsetHeight || rect.height,
      scaleX,
      scaleY,
    };
  }

  function buildList(items: TmMenuItem[]): HTMLElement {
    const list = document.createElement('div');
    list.className = 'w11-tm-menu';

    items.forEach((item) => {
      if (item.type === 'divider') {
        const div = document.createElement('div');
        div.className = 'w11-tm-menu-sep';
        list.appendChild(div);
        return;
      }

      const row = document.createElement('div');
      row.className = `w11-tm-menu-item${item.submenu ? ' has-sub' : ''}${item.disabled ? ' is-disabled' : ''}`;
      const mark = item.checked
        ? item.checkedType === 'radio'
          ? '<span class="w11-tm-menu-dot">●</span>'
          : '<span class="w11-tm-menu-check">✓</span>'
        : '';

      row.innerHTML = `
        <span class="w11-tm-menu-mark">${mark}</span>
        <span class="w11-tm-menu-label">${escapeHtml(item.label ?? '')}</span>
        ${item.shortcut ? `<span class="w11-tm-menu-key">${escapeHtml(item.shortcut)}</span>` : ''}
        ${item.submenu ? '<span class="w11-tm-menu-arrow">›</span>' : ''}
      `;

      const closeSiblingSubs = () => {
        list.querySelectorAll(':scope > .w11-tm-menu-item.is-sub-open').forEach((el) => el.classList.remove('is-sub-open'));
      };

      row.addEventListener('pointerenter', () => {
        closeSiblingSubs();
        if (item.submenu && !item.disabled) {
          row.classList.add('is-sub-open');
          const sub = row.querySelector<HTMLElement>(':scope > .w11-tm-menu-sub');
          if (sub) positionSubmenu(row, sub);
        }
      });

      if (item.submenu && !item.disabled) {
        const subWrap = document.createElement('div');
        subWrap.className = 'w11-tm-menu-sub';
        subWrap.appendChild(buildList(item.submenu));
        row.appendChild(subWrap);
      }

      row.addEventListener('click', (e) => {
        e.stopPropagation();
        if (item.disabled || item.submenu) return;
        close();
        item.onSelect?.();
      });

      list.appendChild(row);
    });

    return list;
  }

  function positionSubmenu(row: HTMLElement, sub: HTMLElement) {
    sub.style.left = '';
    sub.style.right = '';
    sub.style.top = '';
    const hostRect = hostEl.getBoundingClientRect();
    const scaleX = hostRect.width > 0 ? hostRect.width / (hostEl.offsetWidth || hostRect.width) : 1;
    const scaleY = hostRect.height > 0 ? hostRect.height / (hostEl.offsetHeight || hostRect.height) : 1;
    const rect = sub.getBoundingClientRect();
    if (rect.right > hostRect.right - 8 * scaleX) {
      sub.style.left = 'auto';
      sub.style.right = 'calc(100% + 2px)';
    }
    if (rect.bottom > hostRect.bottom - 8 * scaleY) {
      const over = (rect.bottom - (hostRect.bottom - 8 * scaleY)) / scaleY;
      sub.style.top = `${-4 - over}px`;
    }
  }

  function openAt(clientX: number, clientY: number, items: TmMenuItem[]) {
    close();
    const wrap = document.createElement('div');
    wrap.className = 'w11-tm-menu-layer';
    const menu = buildList(items);
    wrap.appendChild(menu);
    hostEl.appendChild(wrap);
    activeMenuEl = wrap;

    const pt = clientToHost(clientX, clientY);
    const mw = menu.offsetWidth || 200;
    const mh = menu.offsetHeight || 180;
    const x = Math.max(6, Math.min(pt.w - mw - 6, pt.x));
    const y = Math.max(6, Math.min(pt.h - mh - 6, pt.y));
    wrap.style.left = `${x}px`;
    wrap.style.top = `${y}px`;

    const onDown = (e: PointerEvent) => {
      if (!wrap.contains(e.target as Node)) close();
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };
    window.addEventListener('pointerdown', onDown, true);
    window.addEventListener('keydown', onKey, true);
    cleanupFns.push(
      () => window.removeEventListener('pointerdown', onDown, true),
      () => window.removeEventListener('keydown', onKey, true),
    );
  }

  function openBelowElement(anchor: HTMLElement, items: TmMenuItem[]) {
    const r = anchor.getBoundingClientRect();
    openAt(r.left, r.bottom + 4, items);
  }

  return { openAt, openBelowElement, close, destroy: close };
}

export function createTmTooltipController(hostEl: HTMLElement) {
  const tipEl = document.createElement('div');
  tipEl.className = 'w11-tm-tooltip';
  tipEl.hidden = true;
  hostEl.appendChild(tipEl);

  let timer: number | null = null;
  let currentTarget: HTMLElement | null = null;

  function place(clientX: number, clientY: number) {
    const rect = hostEl.getBoundingClientRect();
    const scaleX = rect.width > 0 ? rect.width / (hostEl.offsetWidth || rect.width) : 1;
    const scaleY = rect.height > 0 ? rect.height / (hostEl.offsetHeight || rect.height) : 1;
    const hostW = hostEl.offsetWidth || rect.width;
    const hostH = hostEl.offsetHeight || rect.height;
    const x0 = (clientX - rect.left) / scaleX;
    const y0 = (clientY - rect.top) / scaleY;
    const tw = tipEl.offsetWidth || 160;
    const th = tipEl.offsetHeight || 36;
    const left = Math.max(6, Math.min(hostW - tw - 6, x0 + 12));
    const top = y0 + 20 + th > hostH - 6 ? Math.max(6, y0 - th - 10) : y0 + 18;
    tipEl.style.left = `${left}px`;
    tipEl.style.top = `${top}px`;
  }

  function show(clientX: number, clientY: number, data: TmTooltipData) {
    tipEl.innerHTML = data.body
      ? `<div class="w11-tm-tip-title">${escapeHtml(data.title)}</div><div class="w11-tm-tip-body">${escapeHtml(data.body)}</div>`
      : `<div class="w11-tm-tip-title">${escapeHtml(data.title)}</div>`;
    tipEl.hidden = false;
    place(clientX, clientY);
  }

  function hide() {
    if (timer) {
      window.clearTimeout(timer);
      timer = null;
    }
    currentTarget = null;
    tipEl.hidden = true;
  }

  const onOver = (e: PointerEvent) => {
    const el = (e.target as HTMLElement | null)?.closest<HTMLElement>('[data-tm-tip]');
    if (!el || el === currentTarget) return;
    hide();
    currentTarget = el;
    const title = el.dataset.tmTip ?? '';
    const body = el.dataset.tmTipBody ?? '';
    const cx = e.clientX;
    const cy = e.clientY;
    timer = window.setTimeout(() => {
      if (currentTarget === el && title) {
        show(cx, cy, { title, body: body || undefined });
      }
    }, 320);
  };

  const onOut = (e: PointerEvent) => {
    if (!currentTarget) return;
    const next = (e.relatedTarget as HTMLElement | null)?.closest?.('[data-tm-tip]');
    if (next !== currentTarget) hide();
  };

  hostEl.addEventListener('pointerover', onOver);
  hostEl.addEventListener('pointerout', onOut);

  return {
    show,
    hide,
    destroy() {
      hide();
      hostEl.removeEventListener('pointerover', onOver);
      hostEl.removeEventListener('pointerout', onOut);
      tipEl.remove();
    },
  };
}

export function showTmModal(
  hostEl: HTMLElement,
  opts: {
    title: string;
    bodyHtml: string;
    width?: number;
    footerButtons: Array<{ label: string; primary?: boolean; onClick: (modalEl: HTMLElement, close: () => void) => void }>;
    onMount?: (modalEl: HTMLElement, close: () => void) => void;
  },
) {
  const backdrop = document.createElement('div');
  backdrop.className = 'w11-tm-modal-backdrop';
  const dialog = document.createElement('div');
  dialog.className = 'w11-tm-modal';
  dialog.style.width = `${opts.width ?? 420}px`;

  dialog.innerHTML = `
    <div class="w11-tm-modal-head">
      <span>${escapeHtml(opts.title)}</span>
      <button type="button" class="w11-tm-modal-x" aria-label="关闭">✕</button>
    </div>
    <div class="w11-tm-modal-body">${opts.bodyHtml}</div>
    <div class="w11-tm-modal-foot"></div>
  `;

  const foot = dialog.querySelector<HTMLElement>('.w11-tm-modal-foot')!;
  const close = () => {
    window.removeEventListener('keydown', onKey);
    backdrop.remove();
  };

  const onKey = (e: KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      close();
    }
  };
  window.addEventListener('keydown', onKey);

  dialog.querySelector('.w11-tm-modal-x')?.addEventListener('click', close);
  backdrop.addEventListener('click', (e) => {
    if (e.target === backdrop) close();
  });

  opts.footerButtons.forEach((btnSpec) => {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = `w11-tm-btn${btnSpec.primary ? ' is-primary' : ''}`;
    btn.textContent = btnSpec.label;
    btn.addEventListener('click', () => btnSpec.onClick(dialog, close));
    foot.appendChild(btn);
  });

  backdrop.appendChild(dialog);
  hostEl.appendChild(backdrop);
  opts.onMount?.(dialog, close);
  return { close };
}
