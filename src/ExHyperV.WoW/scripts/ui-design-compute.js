const HOST_THREADS = 32;

const FIELD_META = {
  Count: {
    zh: '核心数 (vCPU)',
    en: 'Processors (vCPU)',
    min: 1,
    max: HOST_THREADS,
    unit: '',
    runtime: false,
    descZh: '虚拟机的逻辑处理器数量，只能在虚拟机关机后修改。',
    descEn: 'Logical processors visible to the VM. Shut down the VM before changing this count.'
  },
  Reservation: {
    zh: '预留 (%)',
    en: 'Reserve (%)',
    min: 0,
    max: 100,
    unit: '%',
    runtime: true,
    descZh: '为虚拟机设定的最低处理器资源比例。',
    descEn: 'Minimum processor capacity reserved for the VM.'
  },
  Limit: {
    zh: '限制 (%)',
    en: 'Limit (%)',
    min: 0,
    max: 100,
    unit: '%',
    runtime: true,
    descZh: '为虚拟机设定的最高处理器资源比例。',
    descEn: 'Maximum processor capacity set for the VM.'
  },
  RelativeWeight: {
    zh: '权重',
    en: 'Relative weight',
    min: 1,
    max: 10000,
    unit: '',
    runtime: true,
    descZh: '多台虚拟机争抢处理器资源时的分配优先级。',
    descEn: 'Scheduling priority when VMs contend for CPU. Higher values receive more cycles (default 100).'
  }
};

const KEYS = ['Count', 'Reservation', 'Limit', 'RelativeWeight'];

function clampInt(raw, min, max, fallback) {
  const n = parseInt(String(raw ?? '').trim(), 10);
  if (Number.isNaN(n)) return fallback;
  return Math.max(min, Math.min(max, n));
}

function getVmStore(state, vm) {
  if (!state.computeByVm) state.computeByVm = {};
  if (!state.computeByVm[vm.id]) {
    const base = {
      Count: clampInt(vm.cores, 1, HOST_THREADS, 8),
      Reservation: 0,
      Limit: 100,
      RelativeWeight: 100
    };
    state.computeByVm[vm.id] = {
      saved: { ...base },
      draft: { ...base }
    };
  }
  return state.computeByVm[vm.id];
}

export function isComputeSectionDirty(state, vm) {
  if (!vm) return false;
  const store = getVmStore(state, vm);
  return KEYS.some((k) => store.draft[k] !== store.saved[k]);
}

export function getSavedComputeCores(state, vm) {
  return getVmStore(state, vm).saved.Count;
}

// Convert actual value -> normalized ratio 0..1
function valueToRatio(key, val) {
  if (key === 'RelativeWeight') {
    const clamped = Math.max(1, Math.min(10000, val));
    return Math.max(0, Math.min(1, Math.log10(clamped) / 4));
  }
  const m = FIELD_META[key];
  return Math.max(0, Math.min(1, (val - m.min) / (m.max - m.min)));
}

// Convert normalized ratio 0..1 -> integer field value
function ratioToValue(key, ratio) {
  const r = Math.max(0, Math.min(1, ratio));
  if (key !== 'RelativeWeight') {
    const m = FIELD_META[key];
    return Math.round(m.min + r * (m.max - m.min));
  }
  const v = Math.round(Math.pow(10, r * 4));
  if (v <= 20) return Math.max(1, v);
  if (v <= 100) return Math.round(v / 5) * 5;
  if (v <= 500) return Math.round(v / 10) * 10;
  if (v <= 2000) return Math.round(v / 50) * 50;
  return Math.min(10000, Math.round(v / 100) * 100);
}

function renderLabelHtml(key, isEn) {
  const m = FIELD_META[key];
  const title = isEn ? m.en : m.zh;
  const desc = (isEn ? m.descEn : m.descZh).replace(/"/g, '&quot;');
  return `
    <div class="setting-label-grid">
      <span class="setting-label-title">${title}</span>
      <button class="setting-info-btn" data-info-desc="${desc}" title="${desc}" type="button">
        <span class="fi" style="font-size:15px;">&#xE946;</span>
      </button>
    </div>
  `;
}

function formatCores(val) {
  return Number.isInteger(val) ? String(val) : val.toFixed(1).replace(/\.0$/, '');
}

function formatWeightMult(val) {
  const mult = val / 100;
  if (mult < 1) return mult.toFixed(2).replace(/\.?0+$/, '');
  return mult.toFixed(1).replace(/\.0$/, '');
}

function formatComputeHint(key, draft, isEn) {
  const count = draft.Count || 1;
  const val = draft[key];
  if (key === 'Count') {
    const pct = Math.round((val / HOST_THREADS) * 100);
    return isEn ? `${pct}% of ${HOST_THREADS} threads` : `占宿主机 ${pct}%`;
  }
  if (key === 'Reservation') {
    const eff = formatCores((count * val) / 100);
    return isEn ? `Floor ~${eff} cores` : `保底约 ${eff} 核心`;
  }
  if (key === 'Limit') {
    const eff = formatCores((count * val) / 100);
    return isEn ? `Cap ~${eff} cores` : `封顶约 ${eff} 核心`;
  }
  const mult = formatWeightMult(val);
  if (val === 100) return isEn ? 'Equal share when busy (1x)' : '繁忙时均分 CPU（标准）';
  if (val < 100) return isEn ? `Lower share when busy (${mult}x)` : `繁忙时少分 CPU（${mult}x）`;
  return isEn ? `Higher share when busy (${mult}x)` : `繁忙时多抢 CPU（${mult}x）`;
}

const WEIGHT_TIERS = [
  { value: 50, zh: '低', en: 'Low' },
  { value: 100, zh: '标准', en: 'Normal' },
  { value: 200, zh: '高', en: 'High' },
  { value: 500, zh: '极高', en: 'Max' }
];

// Custom WinUI3-style Energy Bar + Grippable Rectangular Thumb (Grid + Track Border + Fill Border + Thumb Border)
function renderCustomEnergyBarHtml(key, draft, disabled, variant, flags, thumbStyle, isEn) {
  const val = draft[key];
  const sizeCls = flags.jumboSize ? 'is-jumbo' : '';
  if (key === 'RelativeWeight' && flags.weightPresetMode) {
    const buttons = WEIGHT_TIERS.map((tier) => {
      const active = val === tier.value;
      return `<button class="weight-tier-btn ${active ? 'active' : ''}" data-weight-tier="${tier.value}" type="button">${isEn ? tier.en : tier.zh}</button>`;
    }).join('');
    return `<div class="weight-tier-bar ${sizeCls}" data-weight-tier-bar>${buttons}</div>`;
  }
  const ratio = valueToRatio(key, val).toFixed(4);
  const modeCls =
    variant === 'energy-fader'
      ? 'mode-fader'
      : variant === 'energy-cells'
        ? 'mode-cells'
        : 'mode-grip';
  const thumbCls = `thumb-${thumbStyle || 'slim-bar'}`;
  const tooltipHtml = flags.dragTooltip
    ? `<div class="winui-energy-tooltip" data-compute-tooltip="${key}">${formatComputeHint(key, draft, isEn)}</div>`
    : '';
  return `
    <div
      class="winui-energy-slider ${modeCls} ${sizeCls} ${thumbCls} ${disabled ? 'is-disabled' : ''}"
      data-custom-slider="${key}"
      style="--energy-ratio:${ratio};"
      role="slider"
      aria-valuemin="${FIELD_META[key].min}"
      aria-valuemax="${FIELD_META[key].max}"
      aria-valuenow="${val}"
      tabindex="${disabled ? '-1' : '0'}"
    >
      ${tooltipHtml}
      <div class="winui-energy-track">
        <div class="winui-energy-fill">
          <span class="energy-bubble"></span>
          <span class="energy-bubble"></span>
          <span class="energy-bubble"></span>
          <span class="energy-bubble"></span>
          <span class="energy-bubble"></span>
          <span class="energy-bubble"></span>
          <span class="energy-bubble"></span>
        </div>
        <div class="winui-energy-cells-overlay"></div>
      </div>
      <div class="winui-energy-thumb">
        <span class="thumb-grip"></span>
      </div>
    </div>
  `;
}

function renderActionCircles(dirty, isEn, actionMode = 'winui-rect') {
  if (actionMode === 'winui-rect') {
    return `
      <div class="compute-rect-actions">
        <span class="compute-inline-status" id="computeInlineNotice" aria-live="polite"></span>
        <button class="winui-btn-secondary" id="computeRevertBtn" ${dirty ? '' : 'disabled'} type="button">
          ${isEn ? 'Revert' : '撤销'}
        </button>
        <button class="winui-btn-accent" id="computeApplyBtn" ${dirty ? '' : 'disabled'} type="button">
          ${isEn ? 'Apply' : '应用'}
        </button>
      </div>
    `;
  }
  if (actionMode === 'dirty-bar') {
    return `
      <div class="compute-dirty-bar ${dirty ? 'is-dirty' : ''}" id="computeDirtyBar">
        <span class="compute-dirty-text">${isEn ? 'Unsaved changes' : '有未保存的更改'}</span>
        <div class="compute-dirty-btns">
          <button class="winui-btn-secondary" id="computeRevertBtn" ${dirty ? '' : 'disabled'} type="button">
            ${isEn ? 'Revert' : '撤销'}
          </button>
          <button class="winui-btn-accent" id="computeApplyBtn" ${dirty ? '' : 'disabled'} type="button">
            ${isEn ? 'Apply' : '应用'}
          </button>
        </div>
      </div>
    `;
  }
  return `
    <div class="settings-round-actions">
      <button class="settings-action-circle" id="computeRevertBtn" ${dirty ? '' : 'disabled'} title="${isEn ? 'Revert' : '撤销本页修改'}" type="button">
        <span class="fi" style="font-size:20px;">&#xE7A7;</span>
      </button>
      <button class="settings-action-circle primary" id="computeApplyBtn" ${dirty ? '' : 'disabled'} title="${isEn ? 'Apply' : '应用本页修改'}" type="button">
        <span class="fi" style="font-size:24px;">&#xE73E;</span>
      </button>
    </div>
  `;
}

function renderOriginalVariant(store, vmRunning, isEn) {
  const dirty = KEYS.some((k) => store.draft[k] !== store.saved[k]);
  const cards = KEYS.map((key) => {
    const m = FIELD_META[key];
    const val = store.draft[key];
    const disabled = !m.runtime && vmRunning;
    return `
      <div class="proc-field-card kind-editor ${disabled ? 'is-disabled' : ''}" data-compute-card="${key}">
        ${renderLabelHtml(key, isEn)}
        <div class="proc-stepper-row">
          <input type="text" class="proc-stepper-input" data-compute-input="${key}" value="${val}" ${disabled ? 'disabled' : ''} />
          <button class="proc-stepper-btn" data-compute-step="${key}" data-dir="-1" ${disabled || val <= m.min ? 'disabled' : ''} type="button">
            <span class="fi" style="font-size:16px;">&#xE738;</span>
          </button>
          <button class="proc-stepper-btn" data-compute-step="${key}" data-dir="1" ${disabled || val >= m.max ? 'disabled' : ''} type="button">
            <span class="fi" style="font-size:16px;">&#xE710;</span>
          </button>
        </div>
      </div>
    `;
  }).join('');

  return `
    <div class="proc-group-stack">
      <div class="settings-field-panel compute-grid-responsive">${cards}</div>
      ${renderActionCircles(dirty, isEn)}
    </div>
  `;
}

function renderEnergyGridVariant(store, flags, vmRunning, isEn, variant, thumbStyle, actionMode) {
  const dirty = KEYS.some((k) => store.draft[k] !== store.saved[k]);
  const topRight = Boolean(flags.valTopRight);

  const cards = KEYS.map((key) => {
    const m = FIELD_META[key];
    const val = store.draft[key];
    const isDirty = val !== store.saved[key];
    const disabled = !m.runtime && vmRunning;

    const valInputHtml = `
      <div class="energy-val-group">
        ${
          flags.showSteppers
            ? `<button class="energy-step-btn" data-compute-step="${key}" data-dir="-1" ${disabled || val <= m.min ? 'disabled' : ''} type="button">
                 <span class="fi" style="font-size:13px;">&#xE738;</span>
               </button>`
            : ''
        }
        <div class="energy-val-inline">
          <input
            type="text"
            class="energy-num-input ${topRight ? 'compact-num' : ''}"
            data-compute-input="${key}"
            value="${val}"
            style="width:${Math.max(1.5, String(val).length + 0.4)}ch;"
            ${disabled ? 'disabled' : ''}
          />
          ${m.unit ? `<span class="energy-num-unit">${m.unit}</span>` : ''}
        </div>
        ${
          flags.showSteppers
            ? `<button class="energy-step-btn" data-compute-step="${key}" data-dir="1" ${disabled || val >= m.max ? 'disabled' : ''} type="button">
                 <span class="fi" style="font-size:13px;">&#xE710;</span>
               </button>`
            : ''
        }
      </div>
    `;

    const equivHtml = flags.showEquivalent
      ? `<div class="energy-equiv-text" data-compute-equiv="${key}">${formatComputeHint(key, store.draft, isEn, false)}</div>`
      : '';

    if (topRight) {
      return `
        <div class="energy-card compact-head-mode ${isDirty ? 'is-dirty' : ''} ${disabled ? 'is-disabled' : ''}" data-compute-card="${key}">
          <div class="energy-card-head">
            ${renderLabelHtml(key, isEn)}
            ${valInputHtml}
          </div>
          ${renderCustomEnergyBarHtml(key, store.draft, disabled, variant, flags, thumbStyle, isEn)}
          ${equivHtml}
        </div>
      `;
    }

    return `
      <div class="energy-card standard-mode ${isDirty ? 'is-dirty' : ''} ${disabled ? 'is-disabled' : ''}" data-compute-card="${key}">
        <div class="energy-title-row">
          ${renderLabelHtml(key, isEn)}
        </div>
        <div class="energy-mid-val">${valInputHtml}</div>
        ${renderCustomEnergyBarHtml(key, store.draft, disabled, variant, flags, thumbStyle, isEn)}
        ${equivHtml}
      </div>
    `;
  }).join('');

  return `
    <div class="energy-stack">
      <div class="settings-field-panel compute-grid-responsive">${cards}</div>
      ${renderActionCircles(dirty, isEn, actionMode)}
    </div>
  `;
}

function patchLiveIndicators(host, store, isEn) {
  const dirty = KEYS.some((k) => store.draft[k] !== store.saved[k]);
  const revertBtn = host.querySelector('#computeRevertBtn');
  const applyBtn = host.querySelector('#computeApplyBtn');
  if (revertBtn) revertBtn.disabled = !dirty;
  if (applyBtn) applyBtn.disabled = !dirty;
  const dirtyBar = host.querySelector('#computeDirtyBar');
  if (dirtyBar) dirtyBar.classList.toggle('is-dirty', dirty);

  for (const key of KEYS) {
    const card = host.querySelector(`[data-compute-card="${key}"]`);
    if (card) card.classList.toggle('is-dirty', store.draft[key] !== store.saved[key]);
    const equivEl = host.querySelector(`[data-compute-equiv="${key}"]`);
    if (equivEl) equivEl.textContent = formatComputeHint(key, store.draft, isEn, false);
    const tipEl = host.querySelector(`[data-compute-tooltip="${key}"]`);
    if (tipEl) tipEl.textContent = formatComputeHint(key, store.draft, isEn, true);
  }

  const subnavBtn = document.querySelector('#vmSettingsSubnav [data-subnav-id="0"]');
  if (subnavBtn) {
    let dot = subnavBtn.querySelector('.subnav-dirty-dot');
    if (dirty && !dot) {
      dot = document.createElement('span');
      dot.className = 'subnav-dirty-dot';
      subnavBtn.appendChild(dot);
    } else if (!dirty && dot) {
      dot.remove();
    }
  }
}

export function renderComputeSection(host, { state, vm, processorFields, onApplied, showNotice, onLayoutNeeded }) {
  const store = getVmStore(state, vm);
  const variant = state.computeVariant || 'energy-fader';
  const flags = state.computeFlags || {
    jumboSize: false,
    valTopRight: true,
    showSteppers: false,
    showEquivalent: false,
    dragTooltip: true,
    weightPresetMode: false
  };
  const isEn = state.uiLang === 'en';
  const vmRunning = Boolean(vm.running);

  const thumbStyle = state.computeThumbStyle || 'slim-bar';
  const actionMode = state.computeActionMode || 'winui-rect';

  if (variant === 'original') {
    host.innerHTML = renderOriginalVariant(store, vmRunning, isEn);
  } else {
    host.innerHTML = renderEnergyGridVariant(store, flags, vmRunning, isEn, variant, thumbStyle, actionMode);
  }

  // Bind custom WinUI3-style Energy Bar + Rectangular Thumb pointer drag
  host.querySelectorAll('[data-custom-slider]').forEach((sliderEl) => {
    const key = sliderEl.getAttribute('data-custom-slider');
    const m = FIELD_META[key];
    const disabled = !m.runtime && vmRunning;
    if (disabled) return;

    const thumbEl = sliderEl.querySelector('.winui-energy-thumb');
    let dragging = false;
    let grabOffsetPx = 0;

    const updateFromClientX = (clientX) => {
      const rect = sliderEl.getBoundingClientRect();
      const thumbW = thumbEl ? thumbEl.offsetWidth : 24;
      const usableW = Math.max(1, rect.width - thumbW);
      const x = clientX - rect.left - grabOffsetPx;
      const rawRatio = Math.max(0, Math.min(1, x / usableW));
      const nextVal = ratioToValue(key, rawRatio);
      const snappedRatio = valueToRatio(key, nextVal);

      store.draft[key] = nextVal;
      sliderEl.style.setProperty('--energy-ratio', snappedRatio.toFixed(4));
      sliderEl.setAttribute('aria-valuenow', String(nextVal));

      const inp = host.querySelector(`[data-compute-input="${key}"]`);
      if (inp) {
        inp.value = String(nextVal);
        if (!inp.classList.contains('proc-stepper-input')) {
          inp.style.width = `${Math.max(1.5, String(nextVal).length + 0.4)}ch`;
        }
      }
      patchLiveIndicators(host, store, isEn);
    };

    sliderEl.addEventListener('pointerdown', (e) => {
      if (e.button !== 0) return;
      e.preventDefault();
      dragging = true;
      sliderEl.classList.add('is-dragging');
      const thumbRect = thumbEl?.getBoundingClientRect();
      if (thumbRect && e.clientX >= thumbRect.left && e.clientX <= thumbRect.right) {
        grabOffsetPx = e.clientX - thumbRect.left;
      } else {
        grabOffsetPx = (thumbEl ? thumbEl.offsetWidth : 24) / 2;
        updateFromClientX(e.clientX);
      }
      sliderEl.setPointerCapture(e.pointerId);
    });

    sliderEl.addEventListener('pointermove', (e) => {
      if (!dragging) return;
      updateFromClientX(e.clientX);
    });

    const endDrag = (e) => {
      if (!dragging) return;
      dragging = false;
      sliderEl.classList.remove('is-dragging');
      try {
        sliderEl.releasePointerCapture(e.pointerId);
      } catch {}
    };
    sliderEl.addEventListener('pointerup', endDrag);
    sliderEl.addEventListener('pointercancel', endDrag);
  });

  // Direct numeric typing
  host.querySelectorAll('[data-compute-input]').forEach((inp) => {
    inp.addEventListener('input', () => {
      const key = inp.getAttribute('data-compute-input');
      const m = FIELD_META[key];
      if (!inp.classList.contains('proc-stepper-input')) {
        inp.style.width = `${Math.max(1.5, (inp.value || '').length + 0.4)}ch`;
      }
      const parsed = parseInt(inp.value.trim(), 10);
      if (Number.isNaN(parsed)) return;
      const clamped = Math.max(m.min, Math.min(m.max, parsed));
      store.draft[key] = clamped;
      const sliderEl = host.querySelector(`[data-custom-slider="${key}"]`);
      if (sliderEl) {
        sliderEl.style.setProperty('--energy-ratio', valueToRatio(key, clamped).toFixed(4));
      }
      patchLiveIndicators(host, store, isEn);
    });
    inp.addEventListener('blur', () => {
      const key = inp.getAttribute('data-compute-input');
      const m = FIELD_META[key];
      store.draft[key] = clampInt(inp.value, m.min, m.max, store.draft[key]);
      renderComputeSection(host, { state, vm, processorFields, onApplied, showNotice, onLayoutNeeded });
    });
  });

  // Weight tier buttons (when weightPresetMode is enabled)
  host.querySelectorAll('[data-weight-tier]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const nextVal = Number(btn.getAttribute('data-weight-tier'));
      if (Number.isNaN(nextVal)) return;
      store.draft.RelativeWeight = nextVal;
      renderComputeSection(host, { state, vm, processorFields, onApplied, showNotice, onLayoutNeeded });
    });
  });

  // Stepper (- / +)
  host.querySelectorAll('[data-compute-step]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const key = btn.getAttribute('data-compute-step');
      const dir = Number(btn.getAttribute('data-dir'));
      const m = FIELD_META[key];
      if (!m.runtime && vmRunning) return;
      const step = key === 'RelativeWeight' ? 25 : key === 'Count' ? 1 : 5;
      const effectiveStep = variant === 'original' ? 1 : step;
      store.draft[key] = clampInt(store.draft[key] + dir * effectiveStep, m.min, m.max, store.draft[key]);
      renderComputeSection(host, { state, vm, processorFields, onApplied, showNotice, onLayoutNeeded });
    });
  });

  host.querySelector('#computeRevertBtn')?.addEventListener('click', () => {
    store.draft = { ...store.saved };
    renderComputeSection(host, { state, vm, processorFields, onApplied, showNotice, onLayoutNeeded });
  });

  host.querySelector('#computeApplyBtn')?.addEventListener('click', () => {
    if (store.draft.Count !== store.saved.Count && vmRunning) {
      showNotice(isEn ? 'Shut down VM before applying processor count changes.' : '请先关闭虚拟机再应用核心数更改。');
      return;
    }
    store.saved = { ...store.draft };
    vm.cores = store.saved.Count;
    if (Array.isArray(processorFields)) {
      for (const f of processorFields) {
        if (f.id in store.saved) f.value = String(store.saved[f.id]);
      }
    }
    renderComputeSection(host, { state, vm, processorFields, onApplied, showNotice, onLayoutNeeded });
    if (onApplied) onApplied(store.saved, vm);
    showNotice(isEn ? 'Applied' : '已应用');
  });

  patchLiveIndicators(host, store, isEn);
  if (onLayoutNeeded) requestAnimationFrame(onLayoutNeeded);
}
