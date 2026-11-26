/* Unified Device Workbench & GPU-PV Layout Candidates (Restrained WinUI 3)
 * Only GPU-PV (No DDA, No Performance Charts).
 */

export function escapeDeviceHtml(text = '') {
  return String(text)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function renderPivotTrackHtml(tabsInnerHtml, trailingActionsHtml = '') {
  const actions = String(trailingActionsHtml || '').trim();
  if (!actions) {
    return `<div class="st-pivot-track" role="tablist">${tabsInnerHtml}</div>`;
  }
  return `
    <div class="st-pivot-track has-actions">
      <div class="st-pivot-tabs" role="tablist">${tabsInnerHtml}</div>
      <div class="st-pivot-actions">${actions}</div>
    </div>
  `;
}

/** Sorts picker candidates with usable items first, preserving original order within each tier. */
function sortPickerByAvailability(items = [], getTier) {
  return [...items]
    .map((item, idx) => ({ item, idx, tier: getTier(item, idx) }))
    .sort((a, b) => a.tier - b.tier || a.idx - b.idx)
    .map((entry) => entry.item);
}

function resolveManu(g) {
  if (g.manu) return g.manu;
  const n = (g.name || '').toUpperCase();
  if (n.includes('AMD') || n.includes('RADEON')) return 'AMD';
  if (n.includes('INTEL')) return 'Intel';
  if (n.includes('QUALCOMM') || n.includes('ADRENO')) return 'Qualcomm Incorporated';
  return 'NVIDIA';
}

function resolveVendor(g) {
  return g.vendor || resolveManu(g);
}

function resolveGpuVectorPath(g) {
  const manu = (resolveManu(g) || '').toUpperCase();
  const name = (g.name || '').toLowerCase();
  if (manu.includes('NVIDIA') || name.includes('nvidia') || name.includes('geforce') || name.includes('rtx')) {
    return '/ui-design/gpu/nvidia.svg';
  }
  if (manu.includes('AMD') || manu.includes('ADVANCED') || name.includes('radeon') || name.includes('amd')) {
    return '/ui-design/gpu/amd.svg';
  }
  if (manu.includes('QUALCOMM') || name.includes('qualcomm') || name.includes('adreno')) {
    return '/ui-design/gpu/qualcomm.svg';
  }
  if (manu.includes('LISUAN') || name.includes('lisuan')) {
    return '/ui-design/gpu/lisuan.svg';
  }
  if (manu.includes('INTEL') || name.includes('intel')) {
    if (name.includes('iris')) return '/ui-design/gpu/intel-irisxe.svg';
    if (name.includes('arc')) return '/ui-design/gpu/intel-arc.svg';
    if (name.includes('data')) return '/ui-design/gpu/intel-datacenter.svg';
    return '/ui-design/gpu/intel.svg';
  }
  return null;
}

function renderGpuIconBadgeHtml(g, iconStyle = 'vector', size = 38) {
  if (iconStyle === 'vector') {
    const vectorPath = resolveGpuVectorPath(g);
    if (vectorPath) {
      return `<div class="dev-gpu-vector-box" style="width:${size}px;height:${size}px;"><img src="${vectorPath}" alt="" draggable="false" /></div>`;
    }
  }
  const fontSize = Math.max(14, Math.round(size * 0.45));
  return `<div class="tc-head-icon-box" style="width:${size}px;height:${size}px;font-size:${fontSize}px;"><span class="fi">&#xF211;</span></div>`;
}

function renderUpdateIconSvg(iconKey = 'upgrade-base', size = 16) {
  return `<svg width="${size}" height="${size}" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true"><path d="M8 2.3 12.6 7H9.8v3.2H6.2V7H3.4Z"/><rect x="3.4" y="11.9" width="9.2" height="1.55" rx="0.75"/></svg>`;
}

function resolveGpuDriverDisplay(g, hostGpus, gpuDriverMismatch) {
  const matchedHost = hostGpus.find(
    (h) => h.instanceId && g.instanceId && h.instanceId.toLowerCase() === g.instanceId.toLowerCase()
  );
  const rawGuest = typeof g.driverVersion === 'string' ? g.driverVersion.trim() : '';
  const hasRecordedGuest = Boolean(rawGuest && rawGuest !== '---' && rawGuest !== '未部署');
  const guestDriver = hasRecordedGuest ? rawGuest : '未部署';
  const matchedHostVer = typeof matchedHost?.driverVersion === 'string' ? matchedHost.driverVersion.trim() : '';
  const hostDriver = !hasRecordedGuest
    ? matchedHostVer || (gpuDriverMismatch ? '31.0.180.0' : '')
    : gpuDriverMismatch && (!matchedHostVer || matchedHostVer === rawGuest)
      ? '31.0.180.0'
      : matchedHostVer || rawGuest;
  const hasDriverUpdate = Boolean(
    gpuDriverMismatch || (hostDriver && (!hasRecordedGuest || hostDriver !== rawGuest))
  );
  const driverText = hasDriverUpdate && hostDriver ? `${guestDriver}（宿主 ${hostDriver}）` : guestDriver;
  return {
    guestDriver,
    hostDriver,
    hasDriverUpdate,
    driverText,
    copyValue: hasRecordedGuest ? rawGuest : ''
  };
}

function markVmGpuDriversDeployed(assigns, hostGpus) {
  (assigns || []).forEach((item) => {
    const matchedHost = hostGpus.find(
      (h) => h.instanceId && item.instanceId && h.instanceId.toLowerCase() === item.instanceId.toLowerCase()
    );
    item.driverVersion = matchedHost?.driverVersion || item.driverVersion || '31.0.170.0';
  });
}

function renderAddGpuButtonHtml(btnStyle = 'accent-icon-text', opts = {}) {
  const { compact = false, fab = false } = opts;
  const compactCls = compact ? 'is-compact' : '';
  const fabPillCls = fab ? 'is-fab-pill' : '';
  const roundSizeCls = fab ? 'is-fab' : compact ? 'is-sm' : '';
  const plusSize = fab ? 18 : compact ? 13 : 15;
  const textPlusSize = compact ? 11.5 : 13;

  if (btnStyle === 'accent-text') {
    return `<button class="winui-btn accent ${compactCls} ${fabPillCls}" id="gpuOpenSelectBtn" type="button">添加显卡</button>`;
  }
  if (btnStyle === 'accent-circle') {
    return `<button class="round-icon-btn accent ${roundSizeCls}" id="gpuOpenSelectBtn" type="button" title="添加显卡" aria-label="添加显卡"><span class="fi" style="font-size:${plusSize}px;">&#xE710;</span></button>`;
  }
  if (btnStyle === 'round-icon') {
    return `<button class="round-icon-btn ${roundSizeCls}" id="gpuOpenSelectBtn" type="button" title="添加显卡" aria-label="添加显卡"><span class="fi" style="font-size:${plusSize}px;">&#xE710;</span></button>`;
  }
  if (btnStyle === 'secondary-icon-text') {
    return `<button class="winui-btn dev-add-btn ${compactCls} ${fabPillCls}" id="gpuOpenSelectBtn" type="button"><span class="fi" style="font-size:${textPlusSize}px;">&#xE710;</span><span>添加显卡</span></button>`;
  }
  return `<button class="winui-btn accent dev-add-btn ${compactCls} ${fabPillCls}" id="gpuOpenSelectBtn" type="button"><span class="fi" style="font-size:${textPlusSize}px;">&#xE710;</span><span>添加显卡</span></button>`;
}

function renderAddGpuRowCardHtml(btnStyle = 'secondary-icon-text', isEmpty = false) {
  const isAccent = btnStyle === 'accent-icon-text' || btnStyle === 'accent-text' || btnStyle === 'accent-circle';
  const isCircle = btnStyle === 'accent-circle' || btnStyle === 'round-icon';
  const badgeClass = `dev-add-card-badge ${isCircle ? 'is-circle' : ''} ${isAccent ? 'is-accent' : ''}`.trim();

  return `
    <button class="dev-add-card-row ${isEmpty ? 'is-empty' : ''}" id="gpuOpenSelectBtn" type="button">
      <div class="dev-add-card-left">
        <div class="${badgeClass}"><span class="fi">&#xE710;</span></div>
        <div>
          <div class="dev-add-card-title">添加显卡</div>
        </div>
      </div>
    </button>
  `;
}

function getDriverStepLabel(deployMode) {
  return deployMode === 'update-drivers' ? '升级驱动' : '安装驱动';
}

function getDeployStepsForMode(deployMode) {
  if (deployMode === 'update-drivers') {
    return ['关闭虚拟机', getDriverStepLabel(deployMode)];
  }
  if (deployMode === 'assign-only') {
    return ['参数调整', '添加显卡'];
  }
  return ['关闭虚拟机', '参数调整', '添加显卡', getDriverStepLabel(deployMode)];
}

function getDeployActiveStepIdx(deployMode, decision, simIdx) {
  const steps = getDeployStepsForMode(deployMode);
  if (decision === 'Completed' || decision === 'CompletedSkipped') {
    return steps.length;
  }
  if (Number.isInteger(simIdx) && simIdx >= 0) return Math.min(simIdx, steps.length - 1);
  if (deployMode === 'update-drivers') {
    return decision === 'PowerOff' ? 0 : 1;
  }
  if (deployMode === 'assign-only') {
    return 1;
  }
  if (decision === 'PowerOff') return 0;
  return 3;
}

export function renderGpuWorkbench(host, ctx) {
  const {
    state,
    vm,
    HOST_GPUS,
    INITIAL_GPU_PARTITIONS,
    INITIAL_GPU_ASSIGNMENTS,
    renderTitleFirstKvItemHtml,
    renderSettingLabelHtml,
    renderDashboardSummary,
    showNotice,
    onLayoutNeeded,
    notifyParentGpuState
  } = ctx;

  const rerender = () => {
    renderGpuWorkbench(host, ctx);
    ctx.bindHostControls?.(host);
    notifyParentGpuState?.();
    requestAnimationFrame(() => onLayoutNeeded?.());
  };

  if (!state.gpuAssignmentsByVm[vm.id]) {
    state.gpuAssignmentsByVm[vm.id] = [];
  }
  const effectiveDriverMismatch = ctx.isLive ? false : state.gpuDriverMismatch;
  const assigns = state.gpuAssignmentsByVm[vm.id];
  const variant = state.gpuVariant || 'tiles'; // 'expander' | 'tiles' | 'compact' | 'layered'
  const addBtnStyle = state.gpuAddBtnStyle || 'secondary-icon-text';
  const addPlacement = state.gpuAddPlacement || 'list-bottom-row'; // 'list-bottom-row' | 'below-right' | 'floating-fab' | 'tabs-right'
  const iconStyle = state.gpuIconStyle || 'vector';

  // =========================================================================
  // View 2: 选择宿主显卡 (Restrained WinUI 3)
  // =========================================================================
  const clearDeployTimer = () => {
    if (state.gpuDeployTimer) {
      clearTimeout(state.gpuDeployTimer);
      state.gpuDeployTimer = null;
    }
  };

  const resolveAfterScanDecision = () => {
    if (INITIAL_GPU_PARTITIONS.length === 0) {
      return 'FallbackOptions';
    }
    if (INITIAL_GPU_PARTITIONS.length === 1) {
      const only = INITIAL_GPU_PARTITIONS[0];
      if (!only.locked && only.osType === 'Windows') {
        state.gpuSelectedPartitionId = only.id;
        return 'AutoWindows';
      }
    }
    return 'SelectPartition';
  };

  const advanceDeploySimulation = () => {
    clearDeployTimer();
    const mode = state.gpuDeployMode || 'add-with-drivers';
    const driverStepLabel = getDriverStepLabel(mode);
    const steps = getDeployStepsForMode(mode);
    const curIdx = Number.isInteger(state.gpuDeploySimIdx) ? state.gpuDeploySimIdx : 0;
    const curLabel = steps[curIdx];

    if (curLabel === '关闭虚拟机' && vm.running) {
      state.gpuDeployDecision = 'PowerOff';
      rerender();
      return;
    }
    if (curLabel === driverStepLabel && !state.gpuDeployPartitionResolved) {
      const nextDec = resolveAfterScanDecision();
      if (nextDec === 'SelectPartition' || nextDec === 'FallbackOptions') {
        state.gpuDeployDecision = nextDec;
        rerender();
        return;
      }
      state.gpuDeployPartitionResolved = true;
    }

    state.gpuDeployTimer = setTimeout(() => {
      if (state.gpuMode !== 'deploy') return;
      const nextIdx = curIdx + 1;
      if (nextIdx >= steps.length) {
        if (mode === 'assign-only') {
          state.gpuDriverMismatch = true;
        } else {
          markVmGpuDriversDeployed(state.gpuAssignmentsByVm[vm.id], HOST_GPUS);
          state.gpuDriverMismatch = false;
        }
        const finalDecision = state.gpuDeployPendingFinish || 'Completed';
        state.gpuDeployPendingFinish = null;
        state.gpuDeployDecision = finalDecision;
        if (finalDecision === 'Completed') {
          clearDeployTimer();
          state.gpuDeployProgressPct = null;
          state.gpuMode = 'list';
          renderDashboardSummary();
          rerender();
          showNotice?.(
            mode === 'update-drivers'
              ? '已更新 GPU 驱动'
              : mode === 'assign-only'
                ? '已完成 GPU 分配'
                : '已完成 GPU 配置与驱动安装'
          );
          return;
        }
        rerender();
        return;
      }
      state.gpuDeploySimIdx = nextIdx;
      state.gpuDeployDecision = 'Running';
      rerender();
      advanceDeploySimulation();
    }, 460);
  };

  if (state.gpuMode === 'select') {
    ctx.setPageTabsTrailingAction?.('', null);
    const assignedInstanceIds = new Set(assigns.map((a) => a.instanceId));
    const allowMultiGpu = true;
    const validIds = new Set(HOST_GPUS.filter((g) => g.partitionable).map((g) => g.id));
    if (!Array.isArray(state.selectedHostGpuIds)) {
      const firstUnadded = HOST_GPUS.find((g) => g.partitionable && !assignedInstanceIds.has(g.instanceId));
      const fallback = firstUnadded || HOST_GPUS.find((g) => g.partitionable);
      state.selectedHostGpuIds = fallback ? [fallback.id] : [];
    } else {
      state.selectedHostGpuIds = state.selectedHostGpuIds.filter((id) => validIds.has(id));
    }
    if (!allowMultiGpu && state.selectedHostGpuIds.length > 1) {
      state.selectedHostGpuIds = [state.selectedHostGpuIds[0]];
    }
    const selectedSet = new Set(state.selectedHostGpuIds);
    state.selectedHostGpuId = state.selectedHostGpuIds[0] || '';

    const sortedHostGpus = sortPickerByAvailability(HOST_GPUS, (g) => {
      if (!g.partitionable) return 2;
      return assignedInstanceIds.has(g.instanceId) ? 1 : 0;
    });

    const hostRowsHtml = sortedHostGpus.map((g) => {
      const sel = selectedSet.has(g.id);
      const disabled = !g.partitionable;
      const alreadyAdded = !disabled && assignedInstanceIds.has(g.instanceId);
      const tagName = disabled ? 'div' : 'button';
      const rightNote = disabled
        ? `<span class="dev-picker-tag">不支持分区</span>`
        : alreadyAdded
          ? `<span class="dev-picker-tag">已添加</span>`
          : '';
      const boardVendor = resolveVendor(g);
      const selectorHtml = allowMultiGpu
        ? `<span class="dev-checkbox">${sel ? '<span class="fi">&#xE73E;</span>' : ''}</span>`
        : `<span class="dev-radio"></span>`;

      return `
        <${tagName}
          class="dev-picker-row ${sel ? 'is-selected' : ''} ${disabled ? 'is-disabled' : ''}"
          ${disabled ? '' : `data-host-gpu="${escapeDeviceHtml(g.id)}" type="button"`}
        >
          ${selectorHtml}
          ${renderGpuIconBadgeHtml(g, iconStyle, 38)}
          <div style="min-width:0;">
            <div style="font-size:14px;font-weight:400;color:var(--TextFillColorPrimaryBrush);">${escapeDeviceHtml(g.name)}</div>
            <div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">
              ${escapeDeviceHtml(boardVendor)}
            </div>
          </div>
          ${rightNote}
        </${tagName}>
      `;
    }).join('');

    ctx.setPageTabsTrailingAction?.(
      `<button class="winui-btn" id="gpuBackListBtn" type="button">返回</button><button class="winui-btn accent" id="gpuStartDeployBtn" type="button" ${state.selectedHostGpuIds.length ? '' : 'disabled'}>开始配置</button>`,
      null
    );

    host.innerHTML = `
      <div class="dev-stack">
        <div class="winui-card" style="margin-bottom:0;">
          <div class="winui-card-row">
            ${renderSettingLabelHtml('安装驱动', '为 Windows 虚拟机离线注入驱动')}
            <div class="winui-switch ${state.installGpuDrivers ? 'on' : ''}" id="gpuInstallDrvSwitch" data-custom-switch="true" role="switch" aria-checked="${state.installGpuDrivers}">
              <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
            </div>
          </div>
        </div>

        <div class="dev-picker-list">${hostRowsHtml}</div>
      </div>
    `;

    document.getElementById('gpuBackListBtn')?.addEventListener('click', () => {
      state.gpuMode = 'list';
      rerender();
    });
    host.querySelectorAll('[data-host-gpu]').forEach((el) => {
      el.addEventListener('click', () => {
        const gpuId = el.getAttribute('data-host-gpu');
        if (!gpuId) return;
        if (allowMultiGpu) {
          if (selectedSet.has(gpuId)) {
            state.selectedHostGpuIds = state.selectedHostGpuIds.filter((id) => id !== gpuId);
          } else {
            state.selectedHostGpuIds = [...state.selectedHostGpuIds, gpuId];
          }
        } else {
          state.selectedHostGpuIds = [gpuId];
        }
        state.selectedHostGpuId = state.selectedHostGpuIds[0] || '';
        rerender();
      });
    });
    host.querySelector('#gpuInstallDrvSwitch')?.addEventListener('click', () => {
      state.installGpuDrivers = !state.installGpuDrivers;
      rerender();
    });
    document.getElementById('gpuStartDeployBtn')?.addEventListener('click', () => {
      const pickedList = HOST_GPUS.filter((g) => g.partitionable && selectedSet.has(g.id));
      if (!pickedList.length) return;
      const existing = [...(state.gpuAssignmentsByVm[vm.id] || [])];
      pickedList.forEach((picked, offset) => {
        const newAssign = {
          id: `gpu-${Date.now()}-${offset}`,
          name: picked.name,
          manu: picked.manu,
          vendor: picked.vendor || picked.manu,
          memoryText: picked.memoryText || '未知',
          driverVersion: existing.find((x) => x.instanceId === picked.instanceId)?.driverVersion ?? null,
          instanceId: picked.instanceId,
          partitionPath: picked.partitionPath,
          expanded: false
        };
        const hitIdx = existing.findIndex((x) => x.instanceId === picked.instanceId);
        if (hitIdx >= 0) {
          existing[hitIdx] = { ...newAssign, id: existing[hitIdx].id };
        } else {
          existing.push(newAssign);
        }
      });
      state.gpuAssignmentsByVm[vm.id] = existing;
      state.gpuScenario = 'normal';
      state.gpuDeployMode = state.installGpuDrivers ? 'add-with-drivers' : 'assign-only';
      state.gpuDeployPartitionResolved = false;
      state.gpuDeployPendingFinish = null;
      state.gpuDeployProgressPct = null;
      state.gpuDeploySimIdx = 0;
      state.gpuDeployDecision = 'Running';
      state.gpuMode = 'deploy';
      state.gpuLogExpanded = false;
      renderDashboardSummary();
      rerender();
      advanceDeploySimulation();
    });

    requestAnimationFrame(() => onLayoutNeeded?.());
    return;
  }

  // =========================================================================
  // View 3: 显卡配置与驱动部署工作台 (Restrained WinUI 3)
  // =========================================================================
  if (state.gpuMode === 'deploy') {
    ctx.setPageTabsTrailingAction?.('', null);
    const deployMode = state.gpuDeployMode || 'add-with-drivers';
    const driverStepLabel = getDriverStepLabel(deployMode);
    const decision = state.gpuDeployDecision || 'Running';
    const steps = getDeployStepsForMode(deployMode);
    const activeIdx = getDeployActiveStepIdx(deployMode, decision, state.gpuDeploySimIdx);
    const isDone = decision === 'Completed' || decision === 'CompletedSkipped';

    let inlineActionsStep = '';
    let inlineActionsHtml = '';
    let inlineDecisionTargetStep = '';
    let decisionBoxHtml = '';

    if (decision === 'PowerOff') {
      inlineActionsStep = '关闭虚拟机';
      inlineActionsHtml = `
        <div class="dev-action-row" style="margin-top:0;">
          <button class="winui-btn" id="gpuDecisionCancelBtn" type="button">返回</button>
          <button class="winui-btn accent" id="gpuDecisionPowerOffBtn" type="button">关机并继续</button>
        </div>
      `;
    } else if (decision === 'FallbackOptions') {
      inlineActionsStep = driverStepLabel;
      inlineActionsHtml = `
        <div class="dev-action-row" style="margin-top:0;">
          <button class="winui-btn" id="gpuDecisionCancelBtn" type="button">返回</button>
          <button class="winui-btn" id="gpuDecisionSkipDriverBtn" type="button">跳过驱动</button>
        </div>
      `;
    } else if (decision === 'SelectPartition') {
      inlineDecisionTargetStep = driverStepLabel;
      const selectedPart = INITIAL_GPU_PARTITIONS.find((p) => p.id === state.gpuSelectedPartitionId) || INITIAL_GPU_PARTITIONS[0];
      const partRows = INITIAL_GPU_PARTITIONS.map((p) => {
        const sel = (selectedPart?.id || '') === p.id;
        const isLinux = p.osType === 'Linux';
        const helpText = p.locked
          ? '已开启 BitLocker 保护，请先解除 BitLocker 后再注入驱动。'
          : isLinux
            ? '检测到 Linux 分区，当前暂不支持为其部署驱动。'
            : '';
        const infoBtnHtml = helpText
          ? `<button class="setting-info-btn" style="width:18px;height:18px;padding:0;" data-info-desc="${escapeDeviceHtml(helpText)}" title="${escapeDeviceHtml(helpText)}" type="button"><span class="fi" style="font-size:14px;">&#xE946;</span></button>`
          : '';
        const statusTag = p.locked
          ? '<span class="dev-picker-tag">需先解除 BitLocker</span>'
          : isLinux
            ? '<span class="dev-picker-tag">暂不支持</span>'
            : '';
        return `
          <div class="dev-picker-row ${sel ? 'is-selected' : ''}" data-gpu-part="${p.id}">
            <span class="dev-radio"></span>
            <span class="fi" style="font-size:18px;color:var(--TextFillColorPrimaryBrush);">${p.locked ? '&#xE72E;' : '&#xEDA2;'}</span>
            <div style="min-width:0;">
              <div style="display:flex;align-items:center;gap:6px;font-size:13.5px;font-weight:600;color:var(--TextFillColorPrimaryBrush);">${escapeDeviceHtml(p.title)}${infoBtnHtml}</div>
              <div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);margin-top:2px;">${escapeDeviceHtml(p.fs ? `${p.diskLabel}，${p.fs}` : p.diskLabel)}</div>
            </div>
            <div style="display:flex;align-items:center;gap:8px;">
              ${statusTag}
              <span class="dev-status-muted">${escapeDeviceHtml(p.sizeText)}</span>
            </div>
          </div>
        `;
      }).join('');

      const isUnsupported = !selectedPart || selectedPart.locked || selectedPart.osType !== 'Windows';
      const noticeText = !isUnsupported
        ? ''
        : selectedPart?.locked
          ? '解除 BitLocker 后才能继续安装驱动。'
          : selectedPart?.osType === 'Linux'
            ? '暂不支持为 Linux 安装 GPU 驱动。'
            : '所选分区不支持注入驱动，请选择未加密的 Windows 系统分区或跳过驱动。';
      const noticeHtml = noticeText
        ? `<div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);margin-top:2px;">${escapeDeviceHtml(noticeText)}</div>`
        : '';

      decisionBoxHtml = `
        <div class="dev-inline-decision">
          <div class="dev-picker-list">${partRows}</div>
          ${noticeHtml}
          <div class="dev-action-row" style="justify-content:flex-end;">
            <button class="winui-btn" id="gpuBackListFromDeployBtn" type="button">返回</button>
            <button class="winui-btn" id="gpuDecisionSkipDriverBtn" type="button">跳过驱动</button>
            <button class="winui-btn accent" id="gpuDecisionPartNextBtn" type="button" ${isUnsupported ? 'disabled' : ''}>继续</button>
          </div>
        </div>
      `;
    }

    const taskListHtml = steps
      .map((label, idx) => {
        const isSkipped = label === driverStepLabel && decision === 'CompletedSkipped';
        const displayLabel = label;
        const done = !isSkipped && (isDone || idx < activeIdx);
        const active = !isDone && idx === activeIdx;
        const iconHtml = isSkipped
          ? '<span class="fi" style="font-size:15px;color:var(--TextFillColorTertiaryBrush);">&#xE738;</span>'
          : done
            ? '<span class="fi" style="font-size:18px;color:#3CCB5F;">&#xEC61;</span>'
            : active
              ? '<span class="dev-spinner" aria-hidden="true"></span>'
              : '<span class="fi" style="font-size:17px;color:var(--TextFillColorTertiaryBrush);">&#xEA3A;</span>';
        const rowRightHtml = isSkipped
          ? '<span class="dev-status-muted">已跳过</span>'
          : active && label === inlineActionsStep
            ? inlineActionsHtml
            : '';
        const inlineHtml = label === inlineDecisionTargetStep ? decisionBoxHtml : '';
        return `
          <div class="dev-task-row ${active ? 'is-active' : done ? 'is-done' : 'is-pending'}">
            <div class="dev-task-icon">${iconHtml}</div>
            <div style="font-size:14.5px;font-weight:600;color:var(--TextFillColorPrimaryBrush);min-width:0;">${escapeDeviceHtml(displayLabel)}</div>
            ${rowRightHtml}
          </div>
          ${inlineHtml}
        `;
      })
      .join('');

    const hasInlineBackBtn = Boolean(
      inlineActionsStep || decision === 'SelectPartition'
    );
    const footerHtml = isDone
      ? `
        <div class="dev-action-row" style="justify-content:flex-end;">
          <button class="winui-btn accent" id="gpuDecisionDoneBtn" type="button">返回</button>
        </div>
      `
      : !hasInlineBackBtn
        ? `
          <div class="dev-action-row" style="justify-content:flex-end;">
            <button class="winui-btn" id="gpuBackListFromDeployBtn" type="button">返回</button>
          </div>
        `
        : '';

    host.innerHTML = `
      <div class="dev-stack">
        <div class="dev-task-list">${taskListHtml}</div>
        ${footerHtml}
      </div>
    `;

    const backToList = () => {
      clearDeployTimer();
      state.gpuDeployPendingFinish = null;
      state.gpuDeployProgressPct = null;
      state.gpuMode = 'list';
      renderDashboardSummary();
      rerender();
    };
    host.querySelector('#gpuBackListFromDeployBtn')?.addEventListener('click', backToList);
    host.querySelector('#gpuDecisionCancelBtn')?.addEventListener('click', backToList);
    host.querySelector('#gpuDecisionDoneBtn')?.addEventListener('click', backToList);
    host.querySelector('#gpuDecisionPowerOffBtn')?.addEventListener('click', () => {
      vm.running = false;
      ctx.renderVmList?.();
      renderDashboardSummary();
      state.gpuDeployDecision = 'Running';
      state.gpuDeploySimIdx = (state.gpuDeploySimIdx ?? 1) + 1;
      rerender();
      advanceDeploySimulation();
    });
    host.querySelectorAll('[data-gpu-part]').forEach((row) => {
      row.addEventListener('click', () => {
        state.gpuSelectedPartitionId = row.getAttribute('data-gpu-part');
        rerender();
      });
    });
    host.querySelector('#gpuDecisionPartNextBtn')?.addEventListener('click', () => {
      state.gpuDeployPartitionResolved = true;
      state.gpuDeployDecision = 'Running';
      state.gpuDeploySimIdx = steps.indexOf(driverStepLabel);
      rerender();
      advanceDeploySimulation();
    });
    host.querySelector('#gpuDecisionSkipDriverBtn')?.addEventListener('click', () => {
      clearDeployTimer();
      state.gpuDriverMismatch = true;
      state.gpuDeployDecision = 'CompletedSkipped';
      rerender();
    });
    host.querySelector('#gpuDecisionBackPartBtn')?.addEventListener('click', () => {
      state.gpuDeployDecision = 'SelectPartition';
      rerender();
    });
    host.querySelector('#gpuDecisionWinInjectBtn')?.addEventListener('click', () => {
      markVmGpuDriversDeployed(state.gpuAssignmentsByVm[vm.id], HOST_GPUS);
      state.gpuDriverMismatch = false;
      state.gpuDeployDecision = 'Completed';
      showNotice('已完成 Windows 驱动注入');
      backToList();
    });

    requestAnimationFrame(() => onLayoutNeeded?.());
    return;
  }

  // =========================================================================
  // View 1: 主视图 — 极净无技术术语、聚焦显卡自身（第一行直接对齐左侧导航）
  // =========================================================================
  const openSelect = () => {
    state.gpuMode = 'select';
    rerender();
  };

  if (addPlacement === 'tabs-right') {
    ctx.setPageTabsTrailingAction?.(renderAddGpuButtonHtml(addBtnStyle, { compact: true }), openSelect);
  } else {
    ctx.setPageTabsTrailingAction?.('', null);
  }

  let emptyCardHtml = '';
  if (addPlacement === 'list-bottom-row') {
    emptyCardHtml = renderAddGpuRowCardHtml(addBtnStyle, true);
  } else {
    const emptySub =
      addPlacement === 'floating-fab'
        ? '点击右下角悬浮按钮添加宿主机显卡'
        : addPlacement === 'tabs-right'
          ? '点击右上角按钮添加宿主机显卡'
          : '添加宿主机显卡以启用硬件图形加速';
    const emptyRightHtml = addPlacement === 'below-right' ? renderAddGpuButtonHtml(addBtnStyle) : '';
    emptyCardHtml = `
      <div class="dev-empty-row">
        <div style="display:flex;align-items:center;gap:12px;">
          <div class="tc-head-icon-box"><span class="fi">&#xF211;</span></div>
          <div>
            <div style="font-size:14px;font-weight:600;color:var(--TextFillColorPrimaryBrush);">未添加显卡</div>
            <div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);">${emptySub}</div>
          </div>
        </div>
        ${emptyRightHtml}
      </div>
    `;
  }

  let cardsHtml = '';
  const cardGap = variant === 'compact' ? 8 : variant === 'layered' ? 12 : 10;

  if (assigns.length === 0) {
    cardsHtml = emptyCardHtml;
  } else if (variant === 'expander') {
    cardsHtml = assigns
      .map((g, idx) => {
        const expanded = g.expanded === true;
        const drv = resolveGpuDriverDisplay(g, HOST_GPUS, effectiveDriverMismatch);
        const rows = [
          { k: '芯片厂商', v: resolveManu(g), copy: resolveManu(g) },
          { k: '板卡厂商', v: resolveVendor(g), copy: resolveVendor(g) },
          { k: '宿主专用显存', v: g.memoryText || '未取得', copy: g.memoryText || '' },
          { k: '驱动版本', v: drv.driverText, copy: drv.copyValue },
          { k: '设备实例', v: g.instanceId || '---', copy: g.instanceId || '' }
        ]
          .map(
            (r) => `
                <div class="dev-a-kv-row">
                  <span class="dev-a-kv-lbl">${r.k}</span>
                  <span class="dev-a-kv-val" title="${escapeDeviceHtml(r.v)}">${escapeDeviceHtml(r.v)}</span>
                  ${
                    r.copy
                      ? `<button class="dt-kv-head-copy" data-copy="${escapeDeviceHtml(r.copy)}" title="复制：${escapeDeviceHtml(r.copy)}" type="button"><span class="fi" style="font-size:11.5px;">&#xE8C8;</span></button>`
                      : '<span></span>'
                  }
                </div>
              `
          )
          .join('');

        return `
                <div class="winui-expander ${expanded ? 'expanded' : ''}">
                  <div class="winui-expander-header" data-gpu-toggle="${idx}">
                    <div style="display:grid;grid-template-columns:auto minmax(0,1fr) auto;column-gap:14px;align-items:center;">
                      ${renderGpuIconBadgeHtml(g, iconStyle, 36)}
                      <span style="font-size:15px;font-weight:500;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeDeviceHtml(g.name)}</span>
                      <div style="display:flex;gap:8px;" data-stop-prop>
                        <button class="winui-btn" data-gpu-update="${idx}" type="button">升级驱动</button>
                        <button class="winui-btn" data-gpu-remove="${idx}" type="button">移除</button>
                      </div>
                    </div>
                    <span class="fi winui-expander-chevron">${expanded ? '&#xE70E;' : '&#xE70D;'}</span>
                  </div>
                  ${expanded ? `<div class="winui-expander-body"><div class="dev-a-kv-list">${rows}</div></div>` : ''}
                </div>
              `;
      })
      .join('');
  } else if (variant === 'tiles') {
    cardsHtml = assigns
      .map((g, idx) => {
        const expanded = g.expanded === true;
        const { hostDriver, hasDriverUpdate, driverText, copyValue } = resolveGpuDriverDisplay(
          g,
          HOST_GPUS,
          effectiveDriverMismatch
        );
        const kvItems = [
          renderTitleFirstKvItemHtml({ title: '芯片厂商', stateText: resolveManu(g), copyVal: resolveManu(g) }),
          renderTitleFirstKvItemHtml({ title: '板卡厂商', stateText: resolveVendor(g), copyVal: resolveVendor(g) }),
          renderTitleFirstKvItemHtml({ title: '宿主专用显存', stateText: g.memoryText || '未取得', copyVal: g.memoryText || '' }),
          renderTitleFirstKvItemHtml({ title: '驱动版本', stateText: driverText, copyVal: copyValue }),
          renderTitleFirstKvItemHtml({ title: '设备实例路径', stateText: g.instanceId || '---', copyVal: g.instanceId || '', wide: true })
        ].join('');

        return `
                <div class="dev-b-card ${expanded ? 'is-expanded' : ''}">
                  <div class="dev-b-head" data-gpu-toggle="${idx}">
                    ${renderGpuIconBadgeHtml(g, iconStyle, 38)}
                    <div class="dev-b-identity">
                      <div class="dev-b-title">${escapeDeviceHtml(g.name)}</div>
                    </div>
                    <div class="dev-b-actions" data-stop-prop>
                      ${
                        hasDriverUpdate
                          ? `<button class="round-icon-btn ${state.gpuUpdateColor === 'accent' ? 'accent-glyph' : state.gpuUpdateColor === 'red' ? 'red-glyph' : ''}" title="升级驱动（宿主新版本 ${escapeDeviceHtml(hostDriver)}）" data-gpu-update="${idx}" type="button">${renderUpdateIconSvg(state.gpuUpdateIcon || 'upgrade-base', 16)}<span class="round-icon-dot" aria-hidden="true"></span></button>`
                          : ''
                      }
                      <button class="round-icon-btn" title="移除显卡" data-gpu-remove="${idx}" type="button"><span class="fi fi-fluent" style="font-size:16px;">&#xF34D;</span></button>
                      <span class="dev-b-chevron"><span class="fi">${expanded ? '&#xE70E;' : '&#xE70D;'}</span></span>
                    </div>
                  </div>
                  ${expanded ? `<div class="dev-b-body"><div class="dt-kv-grid is-title-first is-2col">${kvItems}</div></div>` : ''}
                </div>
              `;
      })
      .join('');
  } else if (variant === 'compact') {
    cardsHtml = assigns
      .map((g, idx) => {
        const drv = resolveGpuDriverDisplay(g, HOST_GPUS, effectiveDriverMismatch);
        const expanded = g.expanded === true;
        const body = expanded
          ? `
                  <div class="dev-c-kv-4col">
                    <div class="dev-c-kv-cell"><span class="dev-c-kv-lbl">芯片厂商</span><span class="dev-c-kv-val">${escapeDeviceHtml(resolveManu(g))}</span></div>
                    <div class="dev-c-kv-cell"><span class="dev-c-kv-lbl">板卡厂商</span><span class="dev-c-kv-val">${escapeDeviceHtml(resolveVendor(g))}</span></div>
                    <div class="dev-c-kv-cell"><span class="dev-c-kv-lbl">宿主专用显存</span><span class="dev-c-kv-val">${escapeDeviceHtml(g.memoryText || '未取得')}</span></div>
                    <div class="dev-c-kv-cell"><span class="dev-c-kv-lbl">驱动版本</span><span class="dev-c-kv-val">${escapeDeviceHtml(drv.driverText)}</span></div>
                  </div>
                  <div class="dev-c-path-row">
                    <span class="dev-c-kv-lbl">实例路径</span>
                    <span class="dev-c-kv-val tc-mid-fit" style="font-family:Consolas,monospace;font-size:12px;" title="${escapeDeviceHtml(g.instanceId)}">${escapeDeviceHtml(g.instanceId)}</span>
                    <button class="dt-kv-head-copy" data-copy="${escapeDeviceHtml(g.instanceId)}" title="复制实例路径" type="button">
                      <span class="fi" style="font-size:11.5px;">&#xE8C8;</span>
                    </button>
                  </div>
                `
          : '';

        return `
                <div class="dev-c-card ${expanded ? 'is-expanded' : ''}">
                  <div class="dev-c-head" data-gpu-toggle="${idx}">
                    ${renderGpuIconBadgeHtml(g, iconStyle, 28)}
                    <div class="dev-c-title-inline">
                      <span class="dev-c-name">${escapeDeviceHtml(g.name)}</span>
                      <span class="dev-c-spec">${escapeDeviceHtml(drv.guestDriver)}</span>
                    </div>
                    <div style="display:inline-flex;align-items:center;gap:4px;" data-stop-prop>
                      <button class="round-icon-btn" title="升级驱动" data-gpu-update="${idx}" type="button">
                        <span class="fi" style="font-size:14px;">&#xE777;</span>
                      </button>
                      <button class="round-icon-btn" title="移除显卡" data-gpu-remove="${idx}" type="button">
                        <span class="fi" style="font-size:14px;">&#xE74D;</span>
                      </button>
                      <span class="dev-b-chevron" style="margin-left:2px;"><span class="fi">${expanded ? '&#xE70E;' : '&#xE70D;'}</span></span>
                    </div>
                  </div>
                  ${body}
                </div>
              `;
      })
      .join('');
  } else {
    cardsHtml = assigns
      .map((g, idx) => {
        const drv = resolveGpuDriverDisplay(g, HOST_GPUS, effectiveDriverMismatch);
        return `
                <div class="dev-d-card">
                  <div class="dev-d-head">
                    ${renderGpuIconBadgeHtml(g, iconStyle, 34)}
                    <span style="font-size:15px;font-weight:400;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeDeviceHtml(g.name)}</span>
                    <div class="dev-toolbar-actions">
                      <button class="winui-btn" data-gpu-update="${idx}" type="button">升级驱动</button>
                      <button class="winui-btn" data-gpu-remove="${idx}" type="button">移除</button>
                    </div>
                  </div>
                  <div class="dev-d-body">
                    <div class="dev-d-grid">
                      <div class="dev-d-field">
                        <span class="dev-d-lbl">芯片厂商</span>
                        <span class="dev-d-val">${escapeDeviceHtml(resolveManu(g))}</span>
                      </div>
                      <div class="dev-d-field">
                        <span class="dev-d-lbl">板卡厂商</span>
                        <span class="dev-d-val">${escapeDeviceHtml(resolveVendor(g))}</span>
                      </div>
                      <div class="dev-d-field">
                        <span class="dev-d-lbl">宿主专用显存</span>
                        <span class="dev-d-val">${escapeDeviceHtml(g.memoryText || '未取得')}</span>
                      </div>
                      <div class="dev-d-field">
                        <span class="dev-d-lbl">驱动版本</span>
                        <span class="dev-d-val">${escapeDeviceHtml(drv.driverText)}</span>
                      </div>
                      <div class="dev-d-field is-wide">
                        <span class="dev-d-lbl">
                          <span>设备实例路径</span>
                          <button class="dt-kv-head-copy" data-copy="${escapeDeviceHtml(g.instanceId)}" title="复制实例路径" type="button">
                            <span class="fi" style="font-size:11.5px;">&#xE8C8;</span>
                          </button>
                        </span>
                        <span class="dev-d-val tc-mid-fit" title="${escapeDeviceHtml(g.instanceId)}">${escapeDeviceHtml(g.instanceId)}</span>
                      </div>
                    </div>
                  </div>
                </div>
              `;
      })
      .join('');
  }

  let trailingPlacementHtml = '';
  if (addPlacement === 'list-bottom-row' && assigns.length > 0) {
    trailingPlacementHtml = renderAddGpuRowCardHtml(addBtnStyle, false);
  } else if (addPlacement === 'below-right' && assigns.length > 0) {
    trailingPlacementHtml = `<div class="dev-bottom-actions">${renderAddGpuButtonHtml(addBtnStyle)}</div>`;
  }

  const fabDockHtml =
    addPlacement === 'floating-fab'
      ? `<div class="dev-fab-dock">${renderAddGpuButtonHtml(addBtnStyle, { fab: true })}</div>`
      : '';

  host.innerHTML = `
    <div class="dev-stack ${addPlacement === 'floating-fab' ? 'has-floating-fab' : ''}">
      <div style="display:flex;flex-direction:column;gap:${cardGap}px;">
        ${cardsHtml}
        ${trailingPlacementHtml}
      </div>
      ${fabDockHtml}
    </div>
  `;

  // Event Bindings
  host.querySelectorAll('[data-stop-prop]').forEach((el) => {
    el.addEventListener('click', (e) => e.stopPropagation());
  });
  host.querySelectorAll('[data-gpu-toggle]').forEach((hdr) => {
    hdr.addEventListener('click', () => {
      const idx = Number(hdr.getAttribute('data-gpu-toggle'));
      if (assigns[idx]) {
        assigns[idx].expanded = !(assigns[idx].expanded === true);
        rerender();
      }
    });
  });

  host.querySelector('#gpuOpenSelectBtn')?.addEventListener('click', openSelect);

  host.querySelectorAll('[data-gpu-update]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.gpuDeployMode = 'update-drivers';
      state.gpuDeployPartitionResolved = false;
      state.gpuDeployPendingFinish = null;
      state.gpuDeployProgressPct = null;
      state.gpuDeploySimIdx = 0;
      state.gpuDeployDecision = 'Running';
      state.gpuMode = 'deploy';
      rerender();
      advanceDeploySimulation();
    });
  });

  host.querySelectorAll('[data-gpu-remove]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const idx = Number(btn.getAttribute('data-gpu-remove'));
      const removed = assigns[idx];
      const removedName = removed?.name || removed?.title || 'Unknown Device';
      assigns.splice(idx, 1);
      renderDashboardSummary();
      rerender();
      showNotice(`已移除显卡：${removedName}`);
    });
  });

  requestAnimationFrame(() => onLayoutNeeded?.());
}

/* ============================================================================
 * Storage Workbench (Aligned 1:1 with GPU-PV Restrained WinUI 3 Vocabulary)
 * ============================================================================ */

function getStorageControllerTypes(generation, running, driveType) {
  if (generation !== 1) return ['SCSI'];
  if (driveType === 'DvdDrive') return ['IDE'];
  return running ? ['SCSI'] : ['IDE', 'SCSI'];
}

function getStorageControllerNumbers(controllerType) {
  const count = controllerType === 'IDE' ? 2 : 4;
  return Array.from({ length: count }, (_, i) => i);
}

function getAvailableStorageLocations(controllerType, controllerNumber, items) {
  const limit = controllerType === 'IDE' ? 2 : 64;
  const used = new Set(
    (items || [])
      .filter((x) => x.controllerType === controllerType && Number(x.controllerNumber) === Number(controllerNumber))
      .map((x) => Number(x.controllerLocation))
  );
  const avail = [];
  for (let i = 0; i < limit; i++) {
    if (!used.has(i)) avail.push(i);
  }
  return avail;
}

function planBestStorageSlot(generation, running, driveType, items) {
  const types = getStorageControllerTypes(generation, running, driveType);
  for (const type of types) {
    for (const num of getStorageControllerNumbers(type)) {
      const locs = getAvailableStorageLocations(type, num, items);
      if (locs.length > 0) {
        return { controllerType: type, controllerNumber: num, location: locs[0] };
      }
    }
  }
  return null;
}

function ensureSelectedStorageSlot(form, generation, running, driveType, items) {
  const types = getStorageControllerTypes(generation, running, driveType);
  if (!types.includes(form.controllerType)) form.controllerType = types[0];
  const numbers = getStorageControllerNumbers(form.controllerType);
  if (!numbers.includes(Number(form.controllerNumber))) form.controllerNumber = numbers[0];
  let locs = getAvailableStorageLocations(form.controllerType, form.controllerNumber, items);
  if (locs.length === 0) {
    const bestNum = numbers.find((n) => getAvailableStorageLocations(form.controllerType, n, items).length > 0);
    if (bestNum !== undefined) {
      form.controllerNumber = bestNum;
      locs = getAvailableStorageLocations(form.controllerType, form.controllerNumber, items);
    }
  }
  if (!locs.includes(Number(form.location))) {
    form.location = locs.length > 0 ? locs[0] : 0;
  }
  return { types, numbers, locs };
}

function formatStorageFileName(pathStr, fallback) {
  if (!pathStr) return fallback;
  const parts = String(pathStr).replace(/\//g, '\\').split('\\').filter(Boolean);
  return parts[parts.length - 1] || fallback;
}

function normalizeVirtualDiskType(item) {
  const raw = String(item?.vhdType || item?.vhdFormat || '').trim();
  if (raw === 'Differencing' || raw.includes('差异') || raw.includes('差分') || Boolean(item?.parentPath)) {
    return { vhdType: 'Differencing', label: '差异' };
  }
  if (raw === 'Fixed' || raw.includes('固定')) {
    return { vhdType: 'Fixed', label: '固定大小' };
  }
  return { vhdType: 'Dynamic', label: '动态扩展' };
}

function normalizePhysicalDrivePath(item) {
  const raw = String(item?.path || '').trim();
  const num = Number(item?.diskNumber);
  if (Number.isFinite(num) && (!raw || /^PhysicalDisk\s+\d+$/i.test(raw))) {
    return `\\\\.\\PhysicalDrive${num}`;
  }
  return raw;
}

function inferVirtualDiskLockFromPath(pathStr) {
  const raw = String(pathStr || '').trim();
  if (!raw) return { kind: 'none', detail: '' };
  if (/(?:missing|notfound|丢失)/i.test(raw)) {
    return { kind: 'missing', detail: '' };
  }
  if (/(?:host[-_]?mounted|mounted)/i.test(raw)) {
    return { kind: 'host-mounted', detail: '宿主机磁盘管理器' };
  }
  if (/(?:in[-_]?use|locked[-_]?vm|vm[-_]?locked)/i.test(raw)) {
    return { kind: 'locked-vm', detail: 'Win11-Prod' };
  }
  if (/(?:locked|busy)/i.test(raw)) {
    return { kind: 'locked-process', detail: 'wslservice.exe' };
  }
  if (/(?:parent[-_]?dep|has[-_]?child|shared[-_]?parent)/i.test(raw)) {
    return { kind: 'parent-in-use', detail: 'win11_dev_diff.vhdx' };
  }
  return { kind: 'none', detail: '' };
}

function resolveVirtualDiskLockInfo(item, items = []) {
  const explicit = String(item?.lockState || '').trim();
  let kind = explicit;
  let detail = String(item?.lockDetail || '').trim();
  if (!kind) {
    const inferred = inferVirtualDiskLockFromPath(item?.path);
    kind = inferred.kind;
    if (!detail) detail = inferred.detail;
  }
  if (kind === 'none' && Array.isArray(items) && item?.path) {
    const selfLower = String(item.path).trim().toLowerCase();
    const dependentChild = items.find(
      (other) =>
        other &&
        other.id !== item.id &&
        String(other.parentPath || '').trim().toLowerCase() === selfLower
    );
    if (dependentChild) {
      kind = 'parent-in-use';
      detail = dependentChild.displayName || formatStorageFileName(dependentChild.path, '子差异盘');
    }
  }
  return { kind: kind || 'none', detail };
}

function resolveStorageTypeMeta(item, items = []) {
  const isDvd = item.driveType === 'DvdDrive';
  const isPhysical = item.diskType === 'Physical';
  const isStale = isPhysical && Boolean(item.isPassthroughStale);
  const resolvedPath = isPhysical && !isDvd ? normalizePhysicalDrivePath(item) : String(item.path || '').trim();
  const hasPath = Boolean(resolvedPath);
  const isEmptyOptical = isDvd && !isPhysical && !hasPath;
  const glyph = isDvd ? '&#xE958;' : isPhysical ? '&#xEC59;' : '&#xEDA2;';
  const slotDisplay = `${item.controllerType} ${item.controllerNumber}:${item.controllerLocation}`;
  const slotDetailDisplay = `${item.controllerType} 控制器 ${item.controllerNumber}（位置 ${item.controllerLocation}）`;

  const subParts = [];
  let sourceLabel = '虚拟磁盘';
  let diskTypeLabel = '';
  let isDifferencing = false;
  let sizeDisplay = item.sizeText || '';
  let serialText = '';
  let parentPath = '';
  let pathText = resolvedPath;
  let fileStatusText = '';

  if (isPhysical && isDvd) {
    sourceLabel = '物理光驱';
    sizeDisplay = item.sizeText && item.sizeText !== '物理光驱' ? item.sizeText : '';
    serialText = String(item.serialNumber || '').trim();
    pathText = resolvedPath || '\\\\.\\CDROM0';
    if (sizeDisplay) subParts.push(sizeDisplay);
  } else if (isPhysical) {
    sourceLabel = isStale ? '物理磁盘（脱机）' : '物理磁盘';
    sizeDisplay = item.sizeText || '容量未知';
    serialText = String(item.serialNumber || '').trim();
    subParts.push(sizeDisplay);
    if (isStale) subParts.push('已脱机');
  } else if (isDvd) {
    sourceLabel = '虚拟光驱';
    sizeDisplay = isEmptyOptical ? '' : (item.sizeText || '');
    if (isEmptyOptical) {
      subParts.push('未挂载');
    } else if (sizeDisplay) {
      subParts.push(sizeDisplay);
    }
  } else {
    const vhdMeta = normalizeVirtualDiskType(item);
    diskTypeLabel = vhdMeta.label;
    isDifferencing = vhdMeta.vhdType === 'Differencing';
    parentPath = String(item.parentPath || '').trim();
    const capText = String(item.capacityText || '').trim();
    if (vhdMeta.vhdType === 'Fixed') {
      sizeDisplay = capText || item.sizeText || '容量未知';
    } else if (item.sizeText && capText && item.sizeText !== capText) {
      sizeDisplay = `${item.sizeText} / ${capText}`;
    } else {
      sizeDisplay = item.sizeText || capText || '容量未知';
    }
    subParts.push(sizeDisplay);
    const lockInfo = resolveVirtualDiskLockInfo(item, items);
    if (lockInfo.kind === 'missing') fileStatusText = '文件缺失';
    else if (lockInfo.kind === 'host-mounted') fileStatusText = '宿主已挂载';
    else if (lockInfo.kind === 'locked-vm' || lockInfo.kind === 'locked-process') fileStatusText = '已被占用';
    else if (lockInfo.kind === 'parent-in-use') fileStatusText = '子盘依赖中';
    if (fileStatusText) subParts.push(fileStatusText);
  }

  return {
    isDvd,
    isPhysical,
    isStale,
    isDifferencing,
    hasPath,
    isEmptyOptical,
    hasCaution: isStale || Boolean(fileStatusText),
    glyph,
    slotDisplay,
    slotDetailDisplay,
    sourceLabel,
    diskTypeLabel,
    sizeDisplay,
    serialText,
    parentPath,
    pathText,
    fileStatusText,
    subParts,
    subtitle: subParts.join(' ')
  };
}

function renderStorageIconBadgeHtml(meta, size = 38) {
  const fontSize = Math.max(15, Math.round(size * 0.45));
  const dimCls = meta.isEmptyOptical ? 'is-dimmed' : '';
  return `<div class="tc-head-icon-box ${dimCls}" style="width:${size}px;height:${size}px;font-size:${fontSize}px;"><span class="fi">${meta.glyph}</span></div>`;
}

function renderStorageDetailGridHtml(meta, renderTitleFirstKvItemHtml) {
  const safeKv = (opts) =>
    renderTitleFirstKvItemHtml({
      ...opts,
      stateText: escapeDeviceHtml(opts.stateText ?? ''),
      copyVal: opts.copyVal ? escapeDeviceHtml(opts.copyVal) : '',
      revealVal: opts.revealVal ? escapeDeviceHtml(opts.revealVal) : ''
    });

  const shortDefs = [{ title: '介质', stateText: meta.sourceLabel, caution: meta.isStale }];
  const pathDefs = [];

  if (meta.isEmptyOptical) {
    shortDefs.push({ title: '插槽', stateText: meta.slotDetailDisplay });
  } else if (meta.isDvd && !meta.isPhysical) {
    shortDefs.push({ title: '插槽', stateText: meta.slotDetailDisplay });
    if (meta.pathText) {
      pathDefs.push({ title: '路径', stateText: meta.pathText, copyVal: meta.pathText, revealVal: meta.pathText, wide: true });
    }
  } else if (meta.isDvd && meta.isPhysical) {
    if (meta.serialText) {
      shortDefs.push({ title: '序列号', stateText: meta.serialText, copyVal: meta.serialText });
    }
    shortDefs.push({ title: '插槽', stateText: meta.slotDetailDisplay });
    if (meta.pathText) {
      pathDefs.push({ title: '路径', stateText: meta.pathText, copyVal: meta.pathText, wide: true });
    }
  } else if (meta.isPhysical) {
    shortDefs.push(
      { title: '序列号', stateText: meta.serialText || '未取得', copyVal: meta.serialText || '' },
      { title: '插槽', stateText: meta.slotDetailDisplay }
    );
    pathDefs.push({
      title: '路径',
      stateText: meta.pathText || '未知',
      copyVal: meta.isStale ? '' : meta.pathText,
      wide: true
    });
  } else {
    shortDefs.push(
      { title: '类型', stateText: meta.diskTypeLabel },
      { title: '插槽', stateText: meta.slotDetailDisplay }
    );
    if (meta.isDifferencing && meta.parentPath) {
      pathDefs.push({ title: '父盘路径', stateText: meta.parentPath, copyVal: meta.parentPath, revealVal: meta.parentPath, wide: true });
    }
    pathDefs.push({
      title: '路径',
      stateText: meta.pathText || '未知',
      copyVal: meta.pathText,
      revealVal: meta.pathText,
      wide: true
    });
  }

  const colCls = shortDefs.length >= 3 ? 'is-3col' : 'is-2col';
  const kvItems = [...shortDefs, ...pathDefs].map(safeKv);
  return `<div class="dt-kv-grid is-title-first ${colCls}">${kvItems.join('')}</div>`;
}

function renderStorageAddRowCardHtml(isEmpty = false) {
  return `
    <button class="dev-add-card-row ${isEmpty ? 'is-empty' : ''}" id="stOpenAddBtn" type="button">
      <div class="dev-add-card-left">
        <div class="dev-add-card-badge"><span class="fi">&#xE710;</span></div>
        <div>
          <div class="dev-add-card-title">添加存储</div>
        </div>
      </div>
    </button>
  `;
}

function ensureStorageState(state, vm, initialItems) {
  if (!state.storageItemsByVm) state.storageItemsByVm = {};
  if (!state.storageItemsByVm[vm.id]) {
    if (vm.id === 'vm-test' && Array.isArray(state.storageItems)) {
      state.storageItemsByVm[vm.id] = state.storageItems;
    } else if (Array.isArray(vm.storageItems) && vm.storageItems.length > 0) {
      state.storageItemsByVm[vm.id] = JSON.parse(JSON.stringify(vm.storageItems));
    } else if (Array.isArray(vm.disks) && vm.disks.length > 0) {
      state.storageItemsByVm[vm.id] = vm.disks.map((d, i) => {
        const vhdMeta = normalizeVirtualDiskType(d);
        return {
          id: d.key || `st-${vm.id}-${i}`,
          driveType: d.driveType || 'HardDisk',
          diskType: d.diskType || 'Virtual',
          vhdType: vhdMeta.vhdType,
          vhdFormat: vhdMeta.label,
          displayName: d.title || formatStorageFileName(d.path, '虚拟磁盘.vhdx'),
          sizeText: d.fileSizeText || d.capacityText || '容量未知',
          capacityText: d.capacityText || '',
          parentPath: d.parentPath || '',
          controllerType: d.controllerType || 'SCSI',
          controllerNumber: d.controllerNumber ?? 0,
          controllerLocation: d.controllerLocation ?? i,
          path: d.path || '',
          hasIsoMedia: Boolean(d.hasIsoMedia),
          isPassthroughStale: false,
          shrinkFloorGb: d.shrinkFloorGb,
          partitionMode: d.partitionMode,
          lockState: d.lockState,
          lockDetail: d.lockDetail,
          expanded: false
        };
      });
    } else {
      state.storageItemsByVm[vm.id] = JSON.parse(JSON.stringify(initialItems || []));
    }
  }
  if (!state.storageMode) state.storageMode = 'list';
  if (!state.storageAddIntent) state.storageAddIntent = 'existing';
  if (!state.storageAddVariant) state.storageAddVariant = 'pivot-2x3';
  if (!state.storageForm) {
    state.storageForm = {
      driveType: 'HardDisk',
      sourceMode: 'existing',
      filePath: 'D:\\Hyper-V\\Virtual Hard Disks\\extra_disk.vhdx',
      existingFilePath: 'D:\\Hyper-V\\Virtual Hard Disks\\extra_disk.vhdx',
      newDiskPath: 'D:\\Hyper-V\\Virtual Hard Disks\\new_disk.vhdx',
      isoPath: '',
      vhdType: 'Dynamic',
      sizeGb: '128',
      sectorFormat: 'Default',
      blockSize: 'Default',
      parentPath: '',
      showAdvancedDiskOptions: false,
      isoSourceDir: 'C:\\Drivers\\Qualcomm_GPU',
      isoVolumeLabel: 'Qualcomm_GPU',
      isoOutputPath: 'D:\\Hyper-V\\ISO\\Qualcomm_GPU.iso',
      isoOutputCustom: false,
      physicalKind: 'disk',
      selectedPhysicalDiskNumber: 2,
      selectedPhysicalOpticalId: 'opt-0',
      autoLocation: true,
      controllerType: 'SCSI',
      controllerNumber: 0,
      location: 3
    };
  }
  if (state.storageForm.existingFilePath === undefined) {
    state.storageForm.existingFilePath = state.storageForm.filePath || 'D:\\Hyper-V\\Virtual Hard Disks\\extra_disk.vhdx';
  }
  if (state.storageForm.newDiskPath === undefined) {
    state.storageForm.newDiskPath = 'D:\\Hyper-V\\Virtual Hard Disks\\new_disk.vhdx';
  }
  if (!state.storageForm.sourceMode) {
    state.storageForm.sourceMode = deriveMatrixFromState(state, state.storageForm).sourceMode;
  }
  return state.storageItemsByVm[vm.id];
}

const ST_CAPACITY_STOPS = [16, 32, 40, 50, 64, 80, 100, 128, 160, 200, 256, 320, 512, 768, 1024, 1536, 2048, 4096];

const ST_SECTOR_STOPS = [
  { value: 'Default', label: '默认', tip: '默认' },
  { value: '512n', label: '512n', tip: '512n' },
  { value: '512e', label: '512e', tip: '512e' },
  { value: '4kn', label: '4Kn', tip: '4Kn' }
];

const ST_BLOCK_STOPS = [
  { value: 'Default', num: '默认', unit: '', tip: '默认' },
  { value: '1048576', num: '1', unit: 'MB', tip: '1 MB' },
  { value: '2097152', num: '2', unit: 'MB', tip: '2 MB' },
  { value: '4194304', num: '4', unit: 'MB', tip: '4 MB' },
  { value: '8388608', num: '8', unit: 'MB', tip: '8 MB' },
  { value: '16777216', num: '16', unit: 'MB', tip: '16 MB' },
  { value: '33554432', num: '32', unit: 'MB', tip: '32 MB' },
  { value: '67108864', num: '64', unit: 'MB', tip: '64 MB' },
  { value: '134217728', num: '128', unit: 'MB', tip: '128 MB' },
  { value: '268435456', num: '256', unit: 'MB', tip: '256 MB' }
];

function getStopIndex(stops, value) {
  const idx = stops.findIndex((s) => s.value === String(value || 'Default'));
  return idx >= 0 ? idx : 0;
}

function getSteppedStopPos(stops, rawVal) {
  const val = Number(rawVal);
  if (!Number.isFinite(val) || val <= stops[0]) return 0;
  const lastIdx = stops.length - 1;
  if (val >= stops[lastIdx]) return lastIdx;
  for (let i = 0; i < lastIdx; i += 1) {
    const a = stops[i];
    const b = stops[i + 1];
    if (val <= b) return i + (val - a) / Math.max(1, b - a);
  }
  return lastIdx;
}

function getCapacityStopPos(gb) {
  return getSteppedStopPos(ST_CAPACITY_STOPS, gb);
}

function formatCapacityEquiv(gb) {
  const val = Number(gb);
  if (!Number.isFinite(val) || val < 1024) return '';
  const tb = val / 1024;
  return `（${Number.isInteger(tb) ? tb : tb.toFixed(1).replace(/\.0$/, '')} TB）`;
}

function parseStorageSizeGb(sizeStr) {
  const raw = String(sizeStr || '').replace(/,/g, '').trim();
  if (!raw) return null;
  const match = raw.match(/(\d+(?:\.\d+)?)\s*(TB|GB|MB)/i);
  if (!match) {
    const numOnly = Number.parseFloat(raw);
    return Number.isFinite(numOnly) && numOnly > 0 ? numOnly : null;
  }
  const num = Number.parseFloat(match[1]);
  const unit = match[2].toUpperCase();
  if (!Number.isFinite(num) || num <= 0) return null;
  if (unit === 'TB') return num * 1024;
  if (unit === 'MB') return num / 1024;
  return num;
}

function syncVmDisksFromStorageState(state, vm) {
  if (!vm) return;
  const items = state.storageItemsByVm?.[vm.id];
  if (!Array.isArray(items)) return;
  const prevDisks = Array.isArray(vm.disks) ? vm.disks : [];
  vm.disks = items.map((item, idx) => {
    const prev =
      prevDisks.find((d) => d.key === item.id || (item.path && d.path === item.path)) ||
      prevDisks[idx] ||
      {};
    const isDvd = item.driveType === 'DvdDrive';
    const isPhysical = item.diskType === 'Physical';
    const vhdMeta = normalizeVirtualDiskType(item);
    const slotLoc = `${item.controllerType || 'SCSI'} ${item.controllerNumber ?? 0}:${item.controllerLocation ?? idx}`;
    const extLabel = /\.vhd$/i.test(item.path || '') ? 'VHD' : 'VHDX';
    const capGb = parseStorageSizeGb(item.capacityText || item.sizeText) || 0;
    const fileGb = parseStorageSizeGb(item.sizeText) || 0;
    const ratio = capGb > 0 && fileGb > 0 ? Math.min(1, Math.max(0.02, fileGb / capGb)) : prev.ratio || 0.25;
    return {
      ...prev,
      key: item.id,
      title: item.displayName || (isDvd ? '光驱' : '虚拟磁盘.vhdx'),
      subtitle: isDvd ? (item.hasIsoMedia ? 'ISO 映像' : '空光驱') : isPhysical ? '物理直通' : `${vhdMeta.label}磁盘`,
      driveType: item.driveType,
      diskType: item.diskType,
      vhdType: vhdMeta.vhdType,
      controllerType: item.controllerType || 'SCSI',
      controllerNumber: item.controllerNumber ?? 0,
      controllerLocation: item.controllerLocation ?? idx,
      controllerLoc: slotLoc,
      vhdFormat: isDvd || isPhysical ? prev.vhdFormat || '' : `${vhdMeta.vhdType === 'Fixed' ? '固定' : vhdMeta.vhdType === 'Differencing' ? '差异' : '动态'} ${extLabel}`,
      glyph: isDvd ? '\uE958' : isPhysical ? '\uEC59' : '\uEDA2',
      fileSizeText: item.sizeText || prev.fileSizeText || '',
      capacityText: item.capacityText || item.sizeText || prev.capacityText || '',
      ratio,
      parentPath: item.parentPath || '',
      hasIsoMedia: Boolean(item.hasIsoMedia),
      path: item.path || ''
    };
  });
}

function renderEnergySliderTrackHtml({ id, min = 0, max, value, ratio, tooltipText, tooltipId = '', disabled = false }) {
  return `
    <div
      class="winui-energy-slider mode-fader thumb-slim-bar ${disabled ? 'is-disabled' : ''}"
      id="${id}"
      style="--energy-ratio:${Math.max(0, Math.min(1, ratio)).toFixed(4)};"
      role="slider"
      aria-valuemin="${min}"
      aria-valuemax="${max}"
      aria-valuenow="${value}"
      aria-disabled="${disabled}"
      tabindex="${disabled ? -1 : 0}"
    >
      <div class="winui-energy-tooltip" ${tooltipId ? `id="${tooltipId}"` : ''}>${escapeDeviceHtml(tooltipText)}</div>
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

function bindSteppedEnergySlider(slider, { count, onStep }) {
  if (!slider || count < 2) return;
  const thumbEl = slider.querySelector('.winui-energy-thumb');
  const maxIdx = count - 1;
  let dragging = false;
  let grabOffsetPx = 0;

  const commitIdx = (idx) => {
    const clamped = Math.max(0, Math.min(maxIdx, idx));
    slider.style.setProperty('--energy-ratio', (clamped / maxIdx).toFixed(4));
    slider.setAttribute('aria-valuenow', String(clamped));
    onStep?.(clamped);
  };

  const updateFromClientX = (clientX) => {
    const rect = slider.getBoundingClientRect();
    const thumbW = thumbEl ? thumbEl.offsetWidth : 14;
    const usableW = Math.max(1, rect.width - thumbW);
    const x = clientX - rect.left - grabOffsetPx;
    const r = Math.max(0, Math.min(1, x / usableW));
    commitIdx(Math.round(r * maxIdx));
  };

  slider.addEventListener('pointerdown', (e) => {
    if (e.button !== 0 || slider.classList.contains('is-disabled')) return;
    e.preventDefault();
    dragging = true;
    slider.classList.add('is-dragging');
    const thumbRect = thumbEl?.getBoundingClientRect();
    if (thumbRect && e.clientX >= thumbRect.left && e.clientX <= thumbRect.right) {
      grabOffsetPx = e.clientX - thumbRect.left;
    } else {
      grabOffsetPx = (thumbEl ? thumbEl.offsetWidth : 14) / 2;
      updateFromClientX(e.clientX);
    }
    slider.setPointerCapture(e.pointerId);
  });

  slider.addEventListener('pointermove', (e) => {
    if (!dragging) return;
    updateFromClientX(e.clientX);
  });

  const endDrag = (e) => {
    if (!dragging) return;
    dragging = false;
    slider.classList.remove('is-dragging');
    try {
      slider.releasePointerCapture(e.pointerId);
    } catch {}
  };

  slider.addEventListener('pointerup', endDrag);
  slider.addEventListener('pointercancel', endDrag);

  slider.addEventListener('keydown', (e) => {
    if (slider.classList.contains('is-disabled')) return;
    const cur = Math.round(Number(slider.getAttribute('aria-valuenow')) || 0);
    if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') {
      e.preventDefault();
      commitIdx(cur - 1);
    } else if (e.key === 'ArrowRight' || e.key === 'ArrowUp') {
      e.preventDefault();
      commitIdx(cur + 1);
    }
  });
}

const ST_PARENT_DISK_SCENARIOS = [
  { key: 'idmismatch', label: '父盘 ID 不一致', path: 'D:\\Hyper-V\\Base\\Win11_24H2_Base_Copy.vhdx' },
  { key: 'valid', label: '有效 VHDX', path: 'D:\\Hyper-V\\Virtual Hard Disks\\base_win11.vhdx' },
  { key: 'chain2', label: '二级差异链', path: 'D:\\Hyper-V\\Virtual Hard Disks\\child_stage.vhdx' },
  { key: 'chain4', label: '四级差异链', path: 'D:\\Hyper-V\\Virtual Hard Disks\\stage4.vhdx' },
  { key: 'mismatch', label: '格式冲突', path: 'D:\\Hyper-V\\Base\\Legacy_System.vhd' },
  { key: 'locked', label: '占用中', path: 'D:\\Hyper-V\\VMs\\win11_running\\os.vhdx' },
  { key: 'broken', label: '上游断链', path: 'D:\\Hyper-V\\VMs\\broken_diff.vhdx' }
];

const ST_PARENT_BADGE_GLYPHS = {
  success: '<span class="fi" aria-hidden="true">&#xE73E;</span>',
  warning: '<span class="fi" aria-hidden="true">&#xF13C;</span>',
  error: '<span class="fi" aria-hidden="true">&#xE711;</span>',
  info: '<span class="fi" aria-hidden="true">&#xE946;</span>'
};

function inspectParentDiskState(f, opts = {}) {
  const mode = opts.mode === 'edit' ? 'edit' : 'create';
  const raw = String(f?.parentPath || '').trim();
  if (!raw) {
    return { severity: 'info', title: '未选择父盘', chain: null, fixExt: '' };
  }

  const lower = raw.toLowerCase();
  const targetPath =
    mode === 'edit' ? String(opts.targetPath || f?.newDiskPath || '').trim() : getStorageAddNewDiskPath(f).trim();
  const targetExt = targetPath.toLowerCase().endsWith('.vhd') ? '.vhd' : targetPath.toLowerCase().endsWith('.vhdx') ? '.vhdx' : '';
  const hasDiskExt = lower.endsWith('.vhdx') || lower.endsWith('.vhd');

  if (!hasDiskExt) {
    const hasOtherExt = /\.[a-z0-9]{1,8}$/i.test(raw);
    return hasOtherExt
      ? { severity: 'error', title: '仅支持 .vhd / .vhdx 文件', chain: null, fixExt: '' }
      : { severity: 'info', title: '输入 .vhd 或 .vhdx 文件路径', chain: null, fixExt: '' };
  }

  if (mode === 'edit' && targetPath && lower === targetPath.toLowerCase()) {
    return { severity: 'error', title: '父盘路径不能指向当前磁盘自身', chain: null, fixExt: '' };
  }

  const targetName = formatStorageFileName(targetPath, 'new_disk.vhdx');
  const targetNode = { name: targetName, path: targetPath || targetName };
  const fileName = formatStorageFileName(raw, 'parent.vhdx');
  const parentNode = { name: fileName, path: raw };

  if (lower.includes('broken') || lower.includes('missing')) {
    return {
      severity: 'error',
      title: '上游父盘缺失',
      chain: [
        targetNode,
        parentNode,
        { name: 'missing_base.vhdx', path: 'D:\\Hyper-V\\Base\\missing_base.vhdx', missing: true }
      ],
      fixExt: ''
    };
  }

  const parentIsVhd = lower.endsWith('.vhd');
  const parentExt = parentIsVhd ? '.vhd' : '.vhdx';
  const parentFormat = parentIsVhd ? 'VHD' : 'VHDX';

  if (lower.includes('running') || lower.includes('locked')) {
    return {
      severity: 'warning',
      title: '父盘文件正被占用',
      chain: [targetNode, parentNode],
      fixExt: mode === 'create' && targetExt && targetExt !== parentExt ? parentExt : ''
    };
  }

  const isChain4 = lower.includes('stage4') || (lower.includes('4') && lower.includes('stage'));
  const isChain3 = !isChain4 && lower.includes('win11_24h2_base');
  const isChain2 = !isChain4 && !isChain3 && (lower.includes('child') || lower.includes('diff'));
  const chainNodes = isChain4
    ? [
        targetNode,
        parentNode,
        { name: 'stage3.vhdx', path: 'D:\\Hyper-V\\Virtual Hard Disks\\stage3.vhdx' },
        { name: 'stage2.vhdx', path: 'D:\\Hyper-V\\Virtual Hard Disks\\stage2.vhdx' },
        { name: 'base_win11.vhdx', path: 'D:\\Hyper-V\\Base\\base_win11.vhdx' }
      ]
    : isChain3
      ? [
          targetNode,
          parentNode,
          { name: 'Win11_24H2_Sysprep.vhdx', path: 'D:\\Hyper-V\\Base\\Win11_24H2_Sysprep.vhdx' },
          { name: 'Win11_24H2_Golden.vhdx', path: 'D:\\Hyper-V\\Base\\Win11_24H2_Golden.vhdx' }
        ]
      : isChain2
        ? [
            targetNode,
            parentNode,
            { name: 'base_win11.vhdx', path: 'D:\\Hyper-V\\Base\\base_win11.vhdx' }
          ]
        : [targetNode, parentNode];

  if (targetExt && targetExt !== parentExt) {
    if (mode === 'edit') {
      return {
        severity: 'error',
        title: `父盘格式 ${parentFormat} 与当前磁盘不一致`,
        chain: chainNodes,
        fixExt: ''
      };
    }
    return {
      severity: 'warning',
      title: `保存后缀与父盘 ${parentFormat} 不一致`,
      chain: chainNodes,
      fixExt: parentExt
    };
  }

  const linkedLower = String(opts.linkedParentPath || '').trim().toLowerCase();
  const isIdMismatch = mode === 'edit' && /(?:copy|clone|rebuilt|id[-_]?mismatch)/i.test(raw) && lower !== linkedLower;
  if (isIdMismatch) {
    return {
      severity: 'warning',
      title: f?.ignoreParentId ? '已允许忽略 ID 强制连接' : '父盘 ID 不一致',
      chain: chainNodes,
      fixExt: '',
      idMismatch: true,
      forceAllowed: Boolean(f?.ignoreParentId)
    };
  }

  return {
    severity: 'success',
    title: '已连接到父盘',
    chain: chainNodes,
    fixExt: '',
    idMismatch: false,
    forceAllowed: false
  };
}

function renderInlineChainHtml(chain) {
  if (!Array.isArray(chain) || chain.length < 2) return { html: '', title: '' };
  const fullChainTitle = chain.map((n) => (n.missing ? `${n.path} (缺失)` : n.path)).join(' → ');
  const visible =
    chain.length > 3
      ? [chain[0], chain[1], { name: '…' }, chain[chain.length - 1]]
      : chain;
  const parts = visible.map((node) => {
    if (node.missing) {
      return `<span class="st-pinspect-missing">${escapeDeviceHtml(`${node.name} (缺失)`)}</span>`;
    }
    return escapeDeviceHtml(node.name);
  });
  return {
    html: `<span class="st-pinspect-chain">${parts.join(' &#x2192; ')}</span>`,
    title: fullChainTitle
  };
}

function renderStorageInlineBannerHtml({ severity = 'info', text, title = '', actionHtml = '' }) {
  const sevCls = severity === 'error' ? '' : `is-${severity}`;
  const badgeGlyph = ST_PARENT_BADGE_GLYPHS[severity] || ST_PARENT_BADGE_GLYPHS.info;
  return `
    <div class="winui-floating-banner is-inline ${sevCls}" role="status">
      <span class="winui-floating-banner-badge">${badgeGlyph}</span>
      <div class="st-pinspect-line" title="${escapeDeviceHtml(title || text)}">
        <span class="st-pinspect-title">${escapeDeviceHtml(text)}</span>
      </div>
      ${actionHtml}
    </div>
  `;
}

function inspectExistingPathState(f, effectiveDriveType) {
  const isDisk = effectiveDriveType === 'HardDisk';
  const raw = String((isDisk ? getStorageAddExistingDiskPath(f) : f.isoPath) || '').trim();
  if (!raw) return null;
  if (isDisk ? /\.vhdx?$/i.test(raw) : /\.iso$/i.test(raw)) return null;
  const hasDotExt = /\.[a-z0-9]{1,8}$/i.test(raw);
  return hasDotExt
    ? { severity: 'error', text: isDisk ? '仅支持 .vhd / .vhdx 文件' : '仅支持 .iso 映像文件' }
    : { severity: 'info', text: isDisk ? '输入 .vhd 或 .vhdx 文件路径' : '输入 .iso 文件路径' };
}

function inspectNewDiskPathState(f) {
  const raw = getStorageAddNewDiskPath(f).trim();
  if (!raw || /\.vhdx?$/i.test(raw)) return null;
  const hasDotExt = /\.[a-z0-9]{1,8}$/i.test(raw);
  return hasDotExt
    ? { severity: 'error', text: '虚拟磁盘仅支持 .vhd / .vhdx 后缀' }
    : { severity: 'info', text: '文件名请以 .vhd 或 .vhdx 结尾' };
}

function inspectNewDiskCapState(f) {
  if ((f.vhdType || 'Dynamic') === 'Differencing') return null;
  const path = getStorageAddNewDiskPath(f).trim();
  const isVhd = /\.vhd$/i.test(path);
  const sizeVal = Number.parseInt(String(f.sizeGb ?? ''), 10);
  if (isVhd && Number.isFinite(sizeVal) && sizeVal > 2040) {
    return { severity: 'warning', text: '.vhd 格式上限为 2040 GB', fixVhdx: true };
  }
  return null;
}

function inspectNewIsoOutputState(f) {
  const raw = String(f.isoOutputPath || '').trim();
  if (!raw || /\.iso$/i.test(raw)) return null;
  const hasDotExt = /\.[a-z0-9]{1,8}$/i.test(raw);
  return hasDotExt
    ? { severity: 'error', text: 'ISO 文件路径仅支持 .iso 后缀' }
    : { severity: 'info', text: '文件名请以 .iso 结尾' };
}

function renderParentInspectHtml(f, opts = {}) {
  const insp = inspectParentDiskState(f, opts);
  const sevCls = insp.severity === 'error' ? '' : `is-${insp.severity}`;
  const badgeGlyph = ST_PARENT_BADGE_GLYPHS[insp.severity] || ST_PARENT_BADGE_GLYPHS.info;
  const inlineChain = renderInlineChainHtml(insp.chain);
  const forceBtnHtml = insp.idMismatch
    ? insp.forceAllowed
      ? '<button class="winui-btn st-pinspect-fix-btn" type="button" data-st-parent-force-connect="false">取消强制</button>'
      : '<button class="winui-btn st-pinspect-fix-btn" type="button" data-st-parent-force-connect="true" title="若确认父盘内容完全一致，可跳过 ID 校验直接连接">强制连接</button>'
    : '';
  const fixBtnHtml = insp.fixExt
    ? `<button class="winui-btn st-pinspect-fix-btn" type="button" data-st-parent-fix-ext="${insp.fixExt}">改为 ${insp.fixExt}</button>`
    : forceBtnHtml;

  return `
    <div class="winui-floating-banner is-inline ${sevCls}" role="status">
      <span class="winui-floating-banner-badge">${badgeGlyph}</span>
      <div class="st-pinspect-line" title="${escapeDeviceHtml(inlineChain.title || insp.title)}">
        <span class="st-pinspect-title">${escapeDeviceHtml(insp.title)}</span>
        ${inlineChain.html}
      </div>
      ${fixBtnHtml}
    </div>
  `;
}

function deriveMatrixFromState(state, f) {
  const intent = state.storageAddIntent || 'existing';
  if (intent === 'new-disk') return { driveType: 'HardDisk', sourceMode: 'create' };
  if (intent === 'new-iso') return { driveType: 'DvdDrive', sourceMode: 'create' };
  if (intent === 'physical') {
    return { driveType: f.physicalKind === 'optical' ? 'DvdDrive' : 'HardDisk', sourceMode: 'physical' };
  }
  return { driveType: f.driveType === 'DvdDrive' ? 'DvdDrive' : 'HardDisk', sourceMode: 'existing' };
}

function applyMatrixSelection(state, f, driveType, sourceMode) {
  const nextDrive = driveType === 'DvdDrive' ? 'DvdDrive' : 'HardDisk';
  const nextSource = sourceMode === 'create' || sourceMode === 'physical' ? sourceMode : 'existing';
  f.driveType = nextDrive;
  f.sourceMode = nextSource;
  if (nextSource === 'existing') {
    state.storageAddIntent = 'existing';
  } else if (nextSource === 'create') {
    state.storageAddIntent = nextDrive === 'DvdDrive' ? 'new-iso' : 'new-disk';
  } else {
    state.storageAddIntent = 'physical';
    f.physicalKind = nextDrive === 'DvdDrive' ? 'optical' : 'disk';
  }
}

function getStorageAddExistingDiskPath(f) {
  return String(f.existingFilePath ?? f.filePath ?? '');
}

function getStorageAddNewDiskPath(f) {
  return String(f.newDiskPath ?? 'D:\\Hyper-V\\Virtual Hard Disks\\new_disk.vhdx');
}

function syncIsoOutputFromVolume(f) {
  if (f.isoOutputCustom) return false;
  const vol = String(f.isoVolumeLabel || 'NewISO').trim() || 'NewISO';
  const safeVol = vol.replace(/[\\/:*?"<>|]/g, '_');
  f.isoOutputPath = `D:\\Hyper-V\\ISO\\${safeVol}.iso`;
  return true;
}

function resolvePhysicalSelection(HOST_PHYSICAL_DISKS, items, f) {
  const attachedDiskNums = new Set(
    items.filter((x) => x.diskType === 'Physical' && Number.isFinite(Number(x.diskNumber))).map((x) => Number(x.diskNumber))
  );
  const selectableDisks = HOST_PHYSICAL_DISKS.filter((d) => d.canPassthrough && !attachedDiskNums.has(d.number));
  const selectableSet = new Set(selectableDisks.map((d) => d.number));
  if (!Array.isArray(f.selectedPhysicalDiskNumbers)) {
    const legacyNum = Number(f.selectedPhysicalDiskNumber);
    if (Number.isFinite(legacyNum) && selectableSet.has(legacyNum)) {
      f.selectedPhysicalDiskNumbers = [legacyNum];
    } else {
      f.selectedPhysicalDiskNumbers = selectableDisks[0] ? [selectableDisks[0].number] : [];
    }
  } else {
    f.selectedPhysicalDiskNumbers = f.selectedPhysicalDiskNumbers.map(Number).filter((n) => selectableSet.has(n));
  }
  const activeDiskNum = f.selectedPhysicalDiskNumbers[0] ?? null;
  f.selectedPhysicalDiskNumber = activeDiskNum;
  return {
    attachedDiskNums,
    selectableDisks,
    selectedNumbers: f.selectedPhysicalDiskNumbers,
    selectedSet: new Set(f.selectedPhysicalDiskNumbers),
    activeDiskNum
  };
}

function resolvePhysicalOpticalSelection(HOST_OPTICAL_DRIVES, items, f) {
  const attachedOpticalIds = new Set(
    items.filter((x) => x.diskType === 'Physical' && x.driveType === 'DvdDrive').map((x) => String(x.pnpDeviceId || x.displayName || '').trim())
  );
  const isOpticalAttached = (o) =>
    attachedOpticalIds.has(String(o.pnpDeviceId || '').trim()) || attachedOpticalIds.has(String(o.model || '').trim());
  const selectableOpticals = HOST_OPTICAL_DRIVES.filter((o) => o.canPassthrough !== false && !isOpticalAttached(o));
  const selectableSet = new Set(selectableOpticals.map((o) => o.id));
  if (!Array.isArray(f.selectedPhysicalOpticalIds)) {
    const legacyId = String(f.selectedPhysicalOpticalId || '');
    if (legacyId && selectableSet.has(legacyId)) {
      f.selectedPhysicalOpticalIds = [legacyId];
    } else {
      f.selectedPhysicalOpticalIds = selectableOpticals[0] ? [selectableOpticals[0].id] : [];
    }
  } else {
    f.selectedPhysicalOpticalIds = f.selectedPhysicalOpticalIds.filter((id) => selectableSet.has(id));
  }
  f.selectedPhysicalOpticalId = f.selectedPhysicalOpticalIds[0] ?? null;
  return {
    isOpticalAttached,
    selectableOpticals,
    selectedIds: f.selectedPhysicalOpticalIds,
    selectedSet: new Set(f.selectedPhysicalOpticalIds)
  };
}

function isAddStorageConfirmDisabled({ intent, effectiveDriveType, generation, running, f, activeSlot }) {
  if (generation === 1 && running && effectiveDriveType === 'DvdDrive') return true;
  if (!activeSlot) return true;
  if (intent === 'existing') {
    const isDisk = effectiveDriveType === 'HardDisk';
    const raw = String((isDisk ? getStorageAddExistingDiskPath(f) : f.isoPath) || '').trim();
    if (isDisk) return !/\.vhdx?$/i.test(raw);
    return Boolean(raw) && !/\.iso$/i.test(raw);
  }
  if (intent === 'new-disk') {
    if (!/\.vhdx?$/i.test(getStorageAddNewDiskPath(f).trim())) return true;
    if (f.vhdType === 'Differencing') return inspectParentDiskState(f).severity !== 'success';
    const sizeVal = Number.parseInt(String(f.sizeGb || ''), 10);
    if (!(Number.isFinite(sizeVal) && sizeVal >= 1)) return true;
    return Boolean(inspectNewDiskCapState(f));
  }
  if (intent === 'new-iso') {
    const srcOk = Boolean(String(f.isoSourceDir || '').trim());
    const outOk = /\.iso$/i.test(String(f.isoOutputPath || '').trim());
    return !srcOk || !outOk;
  }
  if (intent === 'physical') {
    if (f.physicalKind === 'optical') {
      return generation !== 1 || !Array.isArray(f.selectedPhysicalOpticalIds) || f.selectedPhysicalOpticalIds.length === 0;
    }
    return !Array.isArray(f.selectedPhysicalDiskNumbers) || f.selectedPhysicalDiskNumbers.length === 0;
  }
  return false;
}

function renderMatrixDriveRowHtml({ matrixDrive, matrixSource, generation }) {
  const desc =
    matrixSource === 'existing'
      ? matrixDrive === 'HardDisk'
        ? '连接已存在的 .vhd / .vhdx 虚拟硬盘'
        : '挂载 .iso 映像文件，或留空添加空白光驱'
      : matrixSource === 'create'
        ? matrixDrive === 'HardDisk'
          ? '创建空白动态扩展、固定大小或差异虚拟硬盘'
          : '将宿主机文件夹内容打包制作成 .iso 映像'
        : matrixDrive === 'HardDisk'
          ? '直通宿主机物理硬盘（联机磁盘将自动脱机）'
          : generation === 1
            ? '直接直通宿主机物理光驱硬件（IDE）'
            : '仅第 1 代 IDE 支持物理光驱，第 2 代请用 ISO 映像';

  const dvdMuted = matrixSource === 'physical' && generation !== 1 ? 'is-muted' : '';
  return `
    <div class="winui-card-row">
      <div>
        <div class="setting-row-title">设备类型</div>
        <div class="winui-row-desc">${escapeDeviceHtml(desc)}</div>
      </div>
      <div class="isa-pill-track inline-width" role="tablist">
        <button class="isa-pill-btn ${matrixDrive === 'HardDisk' ? 'active' : ''}" data-st-matrix-drive="HardDisk" type="button">硬盘</button>
        <button class="isa-pill-btn ${matrixDrive === 'DvdDrive' ? 'active' : ''} ${dvdMuted}" data-st-matrix-drive="DvdDrive" type="button">光驱</button>
      </div>
    </div>
  `;
}

function renderMatrixSourceRowHtml({ matrixDrive, matrixSource, generation }) {
  const isDvd = matrixDrive === 'DvdDrive';
  const labels = isDvd
    ? { existing: '连接文件', create: '新建 ISO', physical: '物理光驱' }
    : { existing: '连接文件', create: '新建硬盘', physical: '物理磁盘' };
  const physMuted = isDvd && generation !== 1 ? 'is-muted' : '';

  return `
    <div class="winui-card-row">
      <div class="setting-row-title">添加方式</div>
      <div class="isa-pill-track cols-3 st-source-track" role="tablist">
        <button class="isa-pill-btn ${matrixSource === 'existing' ? 'active' : ''}" data-st-matrix-source="existing" type="button">${labels.existing}</button>
        <button class="isa-pill-btn ${matrixSource === 'create' ? 'active' : ''}" data-st-matrix-source="create" type="button">${labels.create}</button>
        <button class="isa-pill-btn ${matrixSource === 'physical' ? 'active' : ''} ${physMuted}" data-st-matrix-source="physical" type="button">${labels.physical}</button>
      </div>
    </div>
  `;
}

function renderStorageAddBodyRowsHtml({ f, intent, effectiveDriveType, generation, items, HOST_PHYSICAL_DISKS, HOST_OPTICAL_DRIVES, headerRowsHtml, renderSettingLabelHtml }) {
  if (intent === 'existing') {
    const isDisk = effectiveDriveType === 'HardDisk';
    const activePath = isDisk ? getStorageAddExistingDiskPath(f) : String(f.isoPath || '');
    const pathState = inspectExistingPathState(f, effectiveDriveType);
    return `
      <div class="winui-card" style="margin-bottom:0;">
        ${headerRowsHtml}
        <div class="st-field-stack">
          <div class="st-field-head">
            <span class="st-field-title">${isDisk ? '虚拟磁盘文件路径' : 'ISO 映像文件'}</span>
          </div>
          <div class="st-input-group">
            <input type="text" class="winui-textbox is-mono" id="stExistingPathInput" value="${escapeDeviceHtml(activePath)}" placeholder="${isDisk ? 'D:\\Hyper-V\\Virtual Hard Disks\\disk.vhdx' : 'D:\\ISO\\system.iso（可留空）'}" />
            <button class="winui-btn" id="stExistingBrowseBtn" type="button">浏览</button>
          </div>
          <div id="stExistingInspectHost">${pathState ? renderStorageInlineBannerHtml(pathState) : ''}</div>
        </div>
      </div>
    `;
  }

  if (intent === 'new-disk') {
    const vhdType = f.vhdType || 'Dynamic';
    const isDiff = vhdType === 'Differencing';
    const curSizeRaw = String(f.sizeGb ?? '128').trim();
    const curSizeNum = Math.max(1, Number.parseInt(curSizeRaw, 10) || 128);
    const capPos = getCapacityStopPos(curSizeNum);
    const capRatio = capPos / Math.max(1, ST_CAPACITY_STOPS.length - 1);
    const capEquiv = formatCapacityEquiv(curSizeNum);
    const sectorIdx = getStopIndex(ST_SECTOR_STOPS, f.sectorFormat);
    const sectorStop = ST_SECTOR_STOPS[sectorIdx];
    const blockIdx = getStopIndex(ST_BLOCK_STOPS, f.blockSize);
    const blockStop = ST_BLOCK_STOPS[blockIdx];
    const advOpen = Boolean(f.showAdvancedDiskOptions);
    const savePathState = inspectNewDiskPathState(f);
    const capNoticeState = inspectNewDiskCapState(f);
    const capNoticeHtml = capNoticeState
      ? renderStorageInlineBannerHtml({
          severity: capNoticeState.severity,
          text: capNoticeState.text,
          actionHtml: capNoticeState.fixVhdx
            ? '<button class="winui-btn st-pinspect-fix-btn" type="button" id="stCapFixVhdxBtn">改存 .vhdx</button>'
            : ''
        })
      : '';
    const diskTypeDesc =
      '动态扩展：按写入块动态分配宿主机空间。\n' +
      '固定大小：创建时按逻辑容量一次性预分配全部空间。\n' +
      '差异磁盘：链接只读父盘，仅记录增量写入块。';

    return `
      <div class="winui-card" style="margin-bottom:0;">
        ${headerRowsHtml}
        <div class="st-field-stack">
          <div class="st-field-head">
            <span class="st-field-title">虚拟磁盘文件路径</span>
            <span class="st-field-hint">按文件扩展名区分 .vhd / .vhdx</span>
          </div>
          <div class="st-input-group">
            <input type="text" class="winui-textbox is-mono" id="stNewDiskPathInput" value="${escapeDeviceHtml(getStorageAddNewDiskPath(f))}" placeholder="D:\\Hyper-V\\Virtual Hard Disks\\new_disk.vhdx" />
            <button class="winui-btn" id="stNewDiskBrowseBtn" type="button">保存到</button>
          </div>
          <div id="stNewDiskPathInspectHost">${savePathState ? renderStorageInlineBannerHtml(savePathState) : ''}</div>
        </div>
        <div class="winui-card-row">
          ${renderSettingLabelHtml('磁盘类型', diskTypeDesc)}
          <div class="isa-pill-track cols-3 inline-width" role="tablist">
            <button class="isa-pill-btn ${vhdType === 'Dynamic' ? 'active' : ''}" data-st-vhdtype="Dynamic" type="button">动态扩展</button>
            <button class="isa-pill-btn ${vhdType === 'Fixed' ? 'active' : ''}" data-st-vhdtype="Fixed" type="button">固定大小</button>
            <button class="isa-pill-btn ${vhdType === 'Differencing' ? 'active' : ''}" data-st-vhdtype="Differencing" type="button">差异磁盘</button>
          </div>
        </div>
        ${
          isDiff
            ? `
              <div class="st-field-stack">
                <div class="st-field-head">
                  <span class="st-field-title">父盘路径</span>
                  <span class="st-field-hint">容量与扇区格式自动继承父盘</span>
                </div>
                <div class="st-input-group">
                  <input type="text" class="winui-textbox is-mono" id="stParentDiskInput" value="${escapeDeviceHtml(f.parentPath || '')}" placeholder="选择只读父盘文件 (.vhd / .vhdx)" />
                  <button class="winui-btn" id="stParentBrowseBtn" type="button">浏览</button>
                </div>
                <div id="stParentInspectHost">${renderParentInspectHtml(f)}</div>
              </div>
            `
            : `
              <div class="st-slider-block">
                <div class="energy-card-head">
                  ${renderSettingLabelHtml('虚拟容量', '暴露给来宾系统的逻辑容量（GiB），.vhd 上限 2040 GB，.vhdx 上限 64 TB。')}
                  <div class="energy-val-inline">
                    <input
                      type="text"
                      class="energy-num-input compact-num"
                      id="stNewDiskSizeInput"
                      value="${escapeDeviceHtml(curSizeRaw)}"
                      style="width:${Math.max(2, curSizeRaw.length + 0.5)}ch;"
                    />
                    <span class="energy-num-unit">GB</span>
                  </div>
                </div>
                ${renderEnergySliderTrackHtml({
                  id: 'stCapacitySlider',
                  max: ST_CAPACITY_STOPS.length - 1,
                  value: Math.round(capPos),
                  ratio: capRatio,
                  tooltipText: `${curSizeNum} GB${capEquiv}`,
                  tooltipId: 'stCapacityTooltip'
                })}
                <div id="stNewDiskCapInspectHost">${capNoticeHtml}</div>
              </div>
              <div class="winui-card-row" id="stAdvancedToggleRow" style="cursor:pointer;">
                <div class="setting-row-title">高级格式参数</div>
                <span class="fi" style="font-size:12px;color:var(--TextFillColorSecondaryBrush);">${advOpen ? '&#xE70E;' : '&#xE70D;'}</span>
              </div>
              ${
                advOpen
                  ? `
                    <div class="st-adv-slider-grid">
                      <div class="st-adv-slider-col">
                        <div class="energy-card-head">
                          ${renderSettingLabelHtml(
                            '物理扇区格式',
                            '逻辑扇区为来宾寻址单位，物理扇区为底层写入对齐单位：\n默认：不指定扇区参数，采用创建器默认配置。\n512n：逻辑 512 B / 物理 512 B\n512e：逻辑 512 B / 物理 4096 B\n4Kn：逻辑 4096 B / 物理 4096 B'
                          )}
                          <div class="energy-val-inline">
                            <span class="energy-num-input compact-num is-readonly" id="stSectorValText">${escapeDeviceHtml(sectorStop.label)}</span>
                          </div>
                        </div>
                        ${renderEnergySliderTrackHtml({
                          id: 'stSectorSlider',
                          max: ST_SECTOR_STOPS.length - 1,
                          value: sectorIdx,
                          ratio: sectorIdx / Math.max(1, ST_SECTOR_STOPS.length - 1),
                          tooltipText: sectorStop.tip,
                          tooltipId: 'stSectorTooltip'
                        })}
                      </div>
                      <div class="st-adv-slider-col">
                        <div class="energy-card-head">
                          ${renderSettingLabelHtml(
                            '虚拟块大小',
                            '.vhdx 块分配表（BAT）单个有效载荷块大小：\n默认：不指定块大小参数，采用创建器默认配置。\n块越小，稀疏分配粒度越细。块越大，元数据开销越低。'
                          )}
                          <div class="energy-val-inline">
                            <span class="energy-num-input compact-num is-readonly" id="stBlockNumText">${escapeDeviceHtml(blockStop.num)}</span>
                            <span class="energy-num-unit" id="stBlockUnitText" style="${blockStop.unit ? '' : 'display:none;'}">${escapeDeviceHtml(blockStop.unit)}</span>
                          </div>
                        </div>
                        ${renderEnergySliderTrackHtml({
                          id: 'stBlockSlider',
                          max: ST_BLOCK_STOPS.length - 1,
                          value: blockIdx,
                          ratio: blockIdx / Math.max(1, ST_BLOCK_STOPS.length - 1),
                          tooltipText: blockStop.tip,
                          tooltipId: 'stBlockTooltip'
                        })}
                      </div>
                    </div>
                  `
                  : ''
              }
            `
        }
      </div>
    `;
  }

  if (intent === 'new-iso') {
    const isoFsDesc = '默认生成 ISO 9660 + UDF 格式，总容量或单文件达到 4 GiB 时自动切换纯 UDF。';
    const isoOutState = inspectNewIsoOutputState(f);
    return `
      <div class="winui-card" style="margin-bottom:0;">
        ${headerRowsHtml}
        <div class="st-field-stack">
          <div class="st-field-head">
            <span class="st-field-title-wrap">
              <span class="st-field-title">来源文件夹</span>
              <button class="setting-info-btn" style="width:18px;height:18px;padding:0;" data-info-desc="${escapeDeviceHtml(isoFsDesc)}" title="${escapeDeviceHtml(isoFsDesc)}" type="button"><span class="fi" style="font-size:14px;">&#xE946;</span></button>
            </span>
          </div>
          <div class="st-input-group">
            <input type="text" class="winui-textbox is-mono" id="stIsoSourceInput" value="${escapeDeviceHtml(f.isoSourceDir || '')}" placeholder="C:\\Drivers\\Qualcomm_GPU" />
            <button class="winui-btn" id="stIsoFolderBrowseBtn" type="button">选择</button>
          </div>
        </div>
        <div class="winui-card-row">
          <div class="setting-row-title">光盘卷标</div>
          <input type="text" class="winui-textbox" id="stIsoVolumeInput" style="width:240px;" value="${escapeDeviceHtml(f.isoVolumeLabel || 'NewISO')}" />
        </div>
        <div class="st-field-stack">
          <div class="st-field-head">
            <span class="st-field-title">ISO 文件路径</span>
          </div>
          <div class="st-input-group">
            <input type="text" class="winui-textbox is-mono" id="stIsoOutputInput" value="${escapeDeviceHtml(f.isoOutputPath || '')}" placeholder="D:\\Hyper-V\\ISO\\NewISO.iso" />
            <button class="winui-btn" id="stIsoOutputBrowseBtn" type="button">保存到</button>
          </div>
          <div id="stNewIsoInspectHost">${isoOutState ? renderStorageInlineBannerHtml(isoOutState) : ''}</div>
        </div>
      </div>
    `;
  }

  // intent === 'physical'
  const { attachedDiskNums, selectedSet: selectedDiskSet } = resolvePhysicalSelection(HOST_PHYSICAL_DISKS, items, f);
  const showOptical = f.physicalKind === 'optical';
  const { isOpticalAttached, selectedSet: selectedOpticalSet } = resolvePhysicalOpticalSelection(HOST_OPTICAL_DRIVES, items, f);

  const sortedPhysicalDisks = sortPickerByAvailability(HOST_PHYSICAL_DISKS, (d) => {
    if (d.canPassthrough && !attachedDiskNums.has(d.number)) return 0;
    return attachedDiskNums.has(d.number) ? 1 : 2;
  });

  const diskPickerHtml =
    sortedPhysicalDisks.length === 0
      ? `
        <div class="winui-floating-banner is-inline is-info" role="status">
          <span class="winui-floating-banner-badge">${ST_PARENT_BADGE_GLYPHS.info}</span>
          <div class="st-pinspect-line">
            <span class="st-pinspect-title">未检测到宿主机物理磁盘</span>
          </div>
        </div>
      `
      : sortedPhysicalDisks.map((d) => {
          const alreadyAdded = attachedDiskNums.has(d.number);
          const disabled = !d.canPassthrough || alreadyAdded;
          const sel = !disabled && selectedDiskSet.has(d.number);
          const isOnline = String(d.status || '').toLowerCase() === 'online';
          const statusText = isOnline ? '联机' : String(d.status || '').toLowerCase() === 'offline' ? '脱机' : d.status;
          const reasonText = d.unavailabilityReason || '当前不可作为直通磁盘';
          const tagHtml = alreadyAdded
            ? '<span class="dev-picker-tag">已添加</span>'
            : !d.canPassthrough
              ? `<span class="dev-picker-tag-wrap"><span class="dev-picker-tag">不可直通</span><button class="setting-info-btn" style="width:18px;height:18px;padding:0;" data-info-desc="${escapeDeviceHtml(reasonText)}" title="${escapeDeviceHtml(reasonText)}" type="button"><span class="fi" style="font-size:14px;">&#xE946;</span></button></span>`
              : '<span class="dev-picker-tag">可直通</span>';
          const tagName = disabled ? 'div' : 'button';
          return `
            <${tagName}
              class="dev-picker-row is-no-icon ${sel ? 'is-selected' : ''} ${disabled ? 'is-disabled' : ''}"
              ${disabled ? '' : `data-phys-disk="${d.number}" type="button"`}
            >
              <span class="dev-checkbox">${sel ? '<span class="fi">&#xE73E;</span>' : ''}</span>
              <div style="min-width:0;">
                <div style="font-size:14px;font-weight:400;color:var(--TextFillColorPrimaryBrush);">${escapeDeviceHtml(d.friendlyName)}</div>
                <div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">
                  <span class="dev-b-sub-part">${escapeDeviceHtml(`磁盘 ${d.number}`)}</span><span class="dev-b-sub-part">${escapeDeviceHtml(d.busType)}</span><span class="dev-b-sub-part">${escapeDeviceHtml(statusText)}</span>
                </div>
              </div>
              <div class="dev-picker-meta-right">
                ${tagHtml}
                <span class="dev-status-muted">${escapeDeviceHtml(d.sizeText)}</span>
              </div>
            </${tagName}>
          `;
        }).join('');

  const opticalPickerHtml =
    generation !== 1
      ? `
        <div class="winui-floating-banner is-inline is-warning" role="status">
          <span class="winui-floating-banner-badge">${ST_PARENT_BADGE_GLYPHS.warning}</span>
          <div class="st-pinspect-line">
            <span class="st-pinspect-title">第 2 代虚拟机不支持物理光驱直通（仅第 1 代 IDE 支持）</span>
          </div>
          <button class="winui-btn st-pinspect-fix-btn" id="stSwitchToVirtualDvdBtn" type="button">改用连接文件</button>
        </div>
      `
      : HOST_OPTICAL_DRIVES.length === 0
        ? `
          <div class="winui-floating-banner is-inline is-info" role="status">
            <span class="winui-floating-banner-badge">${ST_PARENT_BADGE_GLYPHS.info}</span>
            <div class="st-pinspect-line">
              <span class="st-pinspect-title">未检测到宿主机物理光驱</span>
            </div>
          </div>
        `
        : sortPickerByAvailability(HOST_OPTICAL_DRIVES, (o) => {
            const added = isOpticalAttached(o);
            const canPass = o.canPassthrough !== false;
            if (!added && canPass) return 0;
            return added ? 1 : 2;
          }).map((o, idx) => {
            const alreadyAdded = isOpticalAttached(o);
            const canPass = o.canPassthrough !== false;
            const disabled = !canPass || alreadyAdded;
            const sel = !disabled && selectedOpticalSet.has(o.id);
            const reasonText = o.unavailabilityReason || '当前不可作为直通光驱';
            const tagHtml = alreadyAdded
              ? '<span class="dev-picker-tag">已添加</span>'
              : !canPass
                ? `<span class="dev-picker-tag-wrap"><span class="dev-picker-tag">不可直通</span><button class="setting-info-btn" style="width:18px;height:18px;padding:0;" data-info-desc="${escapeDeviceHtml(reasonText)}" title="${escapeDeviceHtml(reasonText)}" type="button"><span class="fi" style="font-size:14px;">&#xE946;</span></button></span>`
                : '<span class="dev-picker-tag">可直通</span>';
            const tagName = disabled ? 'div' : 'button';
            return `
              <${tagName}
                class="dev-picker-row is-no-icon ${sel ? 'is-selected' : ''} ${disabled ? 'is-disabled' : ''}"
                ${disabled ? '' : `data-phys-optical="${escapeDeviceHtml(o.id)}" type="button"`}
              >
                <span class="dev-checkbox">${sel ? '<span class="fi">&#xE73E;</span>' : ''}</span>
                <div style="min-width:0;">
                  <div style="font-size:14px;font-weight:400;color:var(--TextFillColorPrimaryBrush);">${escapeDeviceHtml(o.model)}</div>
                  <div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">
                    <span class="dev-b-sub-part">${escapeDeviceHtml(`光驱 ${o.number ?? idx}`)}</span><span class="dev-b-sub-part">${escapeDeviceHtml(o.busType || 'SATA')}</span><span class="dev-b-sub-part">${escapeDeviceHtml(o.mediaStatus || '无光盘')}</span>
                  </div>
                </div>
                <div class="dev-picker-meta-right">
                  ${tagHtml}
                  <span class="dev-status-muted">${escapeDeviceHtml(o.drive)}</span>
                </div>
              </${tagName}>
            `;
          }).join('');

  const selectorCard = headerRowsHtml ? `<div class="winui-card" style="margin-bottom:0;">${headerRowsHtml}</div>` : '';
  const physListArea =
    showOptical && generation !== 1
      ? opticalPickerHtml
      : `
        <div class="dev-picker-list">
          ${showOptical ? opticalPickerHtml : diskPickerHtml}
        </div>
      `;

  return `${selectorCard}${physListArea}`;
}

function renderStorageSlotGridHtml({
  slotMeta,
  controllerType,
  controllerNumber,
  location,
  typeAttr = 'data-st-ctrl-type',
  numberId = 'stCtrlNumberSelect',
  locationId = 'stCtrlLocationSelect',
  disabled = false
}) {
  const supportsScsi = slotMeta.types.includes('SCSI');
  const supportsIde = slotMeta.types.includes('IDE');
  const selDisabledAttr = disabled ? 'disabled' : '';

  return `
    <div class="dev-slot-grid">
      <div class="dev-field-col">
        <label class="dev-field-lbl">控制器</label>
        <div class="isa-pill-track">
          <button class="isa-pill-btn ${controllerType === 'SCSI' ? 'active' : ''}" ${typeAttr}="SCSI" ${disabled || !supportsScsi ? 'disabled' : ''} type="button">SCSI</button>
          <button class="isa-pill-btn ${controllerType === 'IDE' ? 'active' : ''}" ${typeAttr}="IDE" ${disabled || !supportsIde ? 'disabled' : ''} type="button">IDE</button>
        </div>
      </div>
      <div class="dev-field-col">
        <label class="dev-field-lbl">编号</label>
        <select class="winui-select" id="${numberId}" ${selDisabledAttr}>
          ${slotMeta.numbers.map((n) => `<option value="${n}" ${Number(controllerNumber) === n ? 'selected' : ''}>控制器 ${n}</option>`).join('')}
        </select>
      </div>
      <div class="dev-field-col">
        <label class="dev-field-lbl">位置</label>
        <select class="winui-select" id="${locationId}" ${selDisabledAttr}>
          ${
            slotMeta.locs.length > 0
              ? slotMeta.locs.map((loc) => `<option value="${loc}" ${Number(location) === loc ? 'selected' : ''}>位置 ${loc}</option>`).join('')
              : '<option value="" disabled selected>无空闲位置</option>'
          }
        </select>
      </div>
    </div>
  `;
}

function renderManualSlotGridHtml({ f, slotMeta }) {
  return renderStorageSlotGridHtml({
    slotMeta,
    controllerType: f.controllerType,
    controllerNumber: f.controllerNumber,
    location: f.location
  });
}

function renderSlotCardHtml({ f, slotMeta, activeSlot, generation, running, batchCount = 1 }) {
  const supportsScsi = slotMeta.types.includes('SCSI');
  const supportsIde = slotMeta.types.includes('IDE');
  const hasInterfaceChoice = supportsScsi && supportsIde;

  const autoDesc = activeSlot
    ? batchCount > 1
      ? `将顺延挂载 ${batchCount} 项，起始于 ${activeSlot.controllerType} 控制器 ${activeSlot.controllerNumber}（位置 ${activeSlot.location}）`
      : `将挂载到 ${activeSlot.controllerType} 控制器 ${activeSlot.controllerNumber}（位置 ${activeSlot.location}）`
    : '当前控制器没有空闲插槽，请关闭后手动调整';

  const manualDesc =
    batchCount > 1
      ? `优先从所选位置起依次分配 ${batchCount} 项插槽`
      : hasInterfaceChoice
        ? '手动选择控制器、编号和位置'
        : !supportsScsi && supportsIde
          ? '第 1 代光驱仅支持 IDE'
          : generation !== 1
            ? '第 2 代仅支持 SCSI'
            : running
              ? '运行中仅支持 SCSI 热插拔'
              : '手动选择控制器、编号和位置';

  const desc = f.autoLocation ? autoDesc : manualDesc;

  return `
    <div class="winui-card" style="margin-bottom:0;">
      <div class="winui-card-row">
        <div>
          <div class="setting-row-title">自动选择插槽</div>
          <div class="winui-row-desc">${escapeDeviceHtml(desc)}</div>
        </div>
        <div class="winui-switch ${f.autoLocation ? 'on' : ''}" id="stAutoSlotSwitch" data-custom-switch="true" role="switch" aria-checked="${f.autoLocation}">
          <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
        </div>
      </div>
      ${!f.autoLocation ? renderManualSlotGridHtml({ f, slotMeta }) : ''}
    </div>
  `;
}

function renderStorageAddLayoutHtml({ state, variant, f, intent, effectiveDriveType, slotMeta, activeSlot, generation, running, items, HOST_PHYSICAL_DISKS, HOST_OPTICAL_DRIVES, renderSettingLabelHtml }) {
  const { driveType: matrixDrive, sourceMode: matrixSource } = deriveMatrixFromState(state, f);
  const hideSlotBecauseGen2Optical = intent === 'physical' && f.physicalKind === 'optical' && generation !== 1;
  const gen1RunningDvdBlocked = generation === 1 && running && effectiveDriveType === 'DvdDrive';
  const hideSlotCard = hideSlotBecauseGen2Optical || gen1RunningDvdBlocked;
  const addActionBtnsHtml = '';

  let topNavHtml = '';
  let headerRowsHtml = '';

  if (variant === 'pivot-3x2') {
    const sourceTabs = [
      { key: 'existing', icon: '&#xEDA2;', label: '挂载现有' },
      { key: 'create', icon: '&#xE710;', label: '新建 / 打包' },
      { key: 'physical', icon: '&#xEC59;', label: '物理直通' }
    ];
    topNavHtml = renderPivotTrackHtml(
      sourceTabs
        .map(
          (t) =>
            `<button class="st-pivot-btn ${matrixSource === t.key ? 'active' : ''}" data-st-matrix-source="${t.key}" type="button" role="tab" aria-selected="${matrixSource === t.key}"><span class="fi" style="font-size:13.5px;">${t.icon}</span><span>${t.label}</span></button>`
        )
        .join(''),
      addActionBtnsHtml
    );
    headerRowsHtml = renderMatrixDriveRowHtml({ matrixDrive, matrixSource, generation });
  } else if (variant === 'matrix-card') {
    headerRowsHtml =
      renderMatrixDriveRowHtml({ matrixDrive, matrixSource, generation }) +
      renderMatrixSourceRowHtml({ matrixDrive, matrixSource, generation });
  } else {
    // Default: 'pivot-2x3' — 顶部「硬盘 / 光驱」2 Pivot + 卡内首行 3 来源
    topNavHtml = renderPivotTrackHtml(
      `
        <button class="st-pivot-btn ${matrixDrive === 'HardDisk' ? 'active' : ''}" data-st-matrix-drive="HardDisk" type="button" role="tab" aria-selected="${matrixDrive === 'HardDisk'}">
          <span class="fi" style="font-size:14px;">&#xEDA2;</span>
          <span>硬盘</span>
        </button>
        <button class="st-pivot-btn ${matrixDrive === 'DvdDrive' ? 'active' : ''}" data-st-matrix-drive="DvdDrive" type="button" role="tab" aria-selected="${matrixDrive === 'DvdDrive'}">
          <span class="fi" style="font-size:14px;">&#xE958;</span>
          <span>光驱</span>
        </button>
      `,
      addActionBtnsHtml
    );
    headerRowsHtml = renderMatrixSourceRowHtml({ matrixDrive, matrixSource, generation });
  }

  const bodyRowsHtml = renderStorageAddBodyRowsHtml({
    f,
    intent,
    effectiveDriveType,
    generation,
    items,
    HOST_PHYSICAL_DISKS,
    HOST_OPTICAL_DRIVES,
    headerRowsHtml,
    renderSettingLabelHtml
  });
  const gen1RunningDvdNoticeHtml = gen1RunningDvdBlocked
    ? renderStorageInlineBannerHtml({
        severity: 'warning',
        text: '第 1 代虚拟机运行中不支持热添加光驱，请先关机'
      })
    : '';
  const batchCount =
    intent === 'physical'
      ? f.physicalKind === 'optical'
        ? (f.selectedPhysicalOpticalIds?.length || 0)
        : (f.selectedPhysicalDiskNumbers?.length || 0)
      : 1;
  const bottomActionsHtml = topNavHtml
    ? ''
    : `
      <div class="dev-action-row" style="justify-content:flex-end;">
        ${addActionBtnsHtml}
      </div>
    `;

  return `
    <div class="dev-stack">
      ${topNavHtml}
      ${bodyRowsHtml}
      ${gen1RunningDvdNoticeHtml}
      ${hideSlotCard ? '' : renderSlotCardHtml({ f, slotMeta, activeSlot, generation, running, batchCount })}
      ${bottomActionsHtml}
    </div>
  `;
}

function ensureStorageSettingsDraft(state, vm, item) {
  const prev = state.storageSettingsDraft;
  if (prev && prev.vmId === vm.id && prev.itemId === item.id) {
    return prev;
  }
  const vhdMeta = normalizeVirtualDiskType(item);
  const capGb = Math.max(1, Math.round(parseStorageSizeGb(item.capacityText || item.sizeText) || 128));
  const rawItemPath = String(item.path || 'D:\\Hyper-V\\Virtual Hard Disks\\disk.vhdx');
  const defaultExportPath = /\.(vhdx?|vhd)$/i.test(rawItemPath)
    ? rawItemPath.replace(/(?:_diff)?(\.(?:vhdx?|vhd))$/i, '_full$1')
    : `${rawItemPath}_full.vhdx`;
  const draft = {
    vmId: vm.id,
    itemId: item.id,
    parentPath: item.parentPath || '',
    ignoreParentId: false,
    exportOpen: false,
    exportPath: defaultExportPath,
    mergeDepth: 1,
    mergeConfirm: false,
    resizeGb: String(capGb),
    targetVhdType: vhdMeta.vhdType === 'Fixed' ? 'Fixed' : 'Dynamic',
    mediaPath: item.path || '',
    controllerType: item.controllerType || 'SCSI',
    controllerNumber: Number(item.controllerNumber ?? 0),
    controllerLocation: Number(item.controllerLocation ?? 0),
    activeTab: 'disk'
  };
  state.storageSettingsDraft = draft;
  return draft;
}

function inspectVirtualDiskAccessState(item, { running = false, vm = null, items = [] } = {}) {
  const vmStateRaw = String(vm?.state || '').toLowerCase();
  const isSavedOrPaused =
    vmStateRaw === 'saved' ||
    vmStateRaw === 'paused' ||
    vm?.saved === true ||
    vm?.paused === true;
  const isActiveVm = Boolean(running) || isSavedOrPaused;
  const isVhd = /\.vhd$/i.test(item?.path || '');
  const vhdMeta = normalizeVirtualDiskType(item);
  const isDifferencing = vhdMeta.vhdType === 'Differencing';
  const isFixed = vhdMeta.vhdType === 'Fixed';
  const canOnlineExpand = Boolean(running) && !isSavedOrPaused && !isDifferencing && item?.controllerType === 'SCSI' && !isVhd;
  const lockInfo = resolveVirtualDiskLockInfo(item, items);

  const allowEditSlot = !isActiveVm;
  const allowEditMedia = item?.driveType === 'DvdDrive' ? !isSavedOrPaused : !isActiveVm;

  if (lockInfo.kind === 'missing') {
    return {
      kind: 'missing',
      detail: lockInfo.detail,
      isSavedOrPaused,
      canOpenFile: false,
      canExpand: false,
      canShrink: false,
      canChangeType: false,
      canCompact: false,
      canUpgradeVhdx: false,
      canExport: false,
      canMerge: false,
      canEditParentPath: false,
      canDeleteFile: false,
      canEditSlot: allowEditSlot,
      canEditMedia: allowEditMedia,
      bannerSeverity: 'error',
      bannerText: '未找到虚拟磁盘文件',
      bannerAction: { id: 'stSetLocateMediaBtn', label: '定位路径' }
    };
  }

  if (isSavedOrPaused) {
    return {
      kind: 'vm-saved',
      detail: '',
      isSavedOrPaused: true,
      canOpenFile: false,
      canExpand: false,
      canShrink: false,
      canChangeType: false,
      canCompact: false,
      canUpgradeVhdx: false,
      canExport: false,
      canMerge: false,
      canEditParentPath: false,
      canDeleteFile: false,
      canEditSlot: false,
      canEditMedia: false,
      bannerSeverity: 'warning',
      bannerText: '请彻底关机后再编辑虚拟磁盘',
      bannerAction: null
    };
  }

  if (running) {
    return {
      kind: 'running',
      detail: '',
      isSavedOrPaused: false,
      canOpenFile: false,
      canExpand: canOnlineExpand,
      canShrink: false,
      canChangeType: false,
      canCompact: false,
      canUpgradeVhdx: false,
      canExport: false,
      canMerge: false,
      canEditParentPath: false,
      canDeleteFile: false,
      canEditSlot: false,
      canEditMedia: false,
      bannerSeverity: 'warning',
      bannerText: canOnlineExpand ? '运行中仅支持联机扩展 SCSI VHDX 容量，其余变更需先关机' : '关闭虚拟机后可编辑虚拟磁盘配置',
      bannerAction: null
    };
  }

  if (lockInfo.kind === 'host-mounted') {
    return {
      kind: 'host-mounted',
      detail: lockInfo.detail,
      isSavedOrPaused: false,
      canOpenFile: false,
      canExpand: false,
      canShrink: false,
      canChangeType: false,
      canCompact: !isFixed,
      canUpgradeVhdx: false,
      canExport: false,
      canMerge: false,
      canEditParentPath: false,
      canDeleteFile: false,
      canEditSlot: true,
      canEditMedia: true,
      bannerSeverity: 'warning',
      bannerText: isDifferencing
        ? '已挂载到宿主机，卸载后可导出、合并或删除'
        : '已挂载到宿主机，卸载后可调整配置或删除',
      bannerAction: { id: 'stSetDismountHostBtn', label: '卸载挂载' }
    };
  }

  if (lockInfo.kind === 'locked-vm' || lockInfo.kind === 'locked-process') {
    const ownerText = lockInfo.kind === 'locked-vm'
      ? `「${lockInfo.detail || '其他虚拟机'}」`
      : `宿主进程（${lockInfo.detail || '外部进程'}）`;
    return {
      kind: lockInfo.kind,
      detail: lockInfo.detail,
      isSavedOrPaused: false,
      canOpenFile: false,
      canExpand: false,
      canShrink: false,
      canChangeType: false,
      canCompact: false,
      canUpgradeVhdx: false,
      canExport: false,
      canMerge: false,
      canEditParentPath: false,
      canDeleteFile: false,
      canEditSlot: true,
      canEditMedia: true,
      bannerSeverity: 'warning',
      bannerText: `正被${ownerText}占用，暂不可修改`,
      bannerAction: { id: 'stSetRecheckDiskAccessBtn', label: '重新检测' }
    };
  }

  if (lockInfo.kind === 'parent-in-use') {
    return {
      kind: 'parent-in-use',
      detail: lockInfo.detail,
      isSavedOrPaused: false,
      canOpenFile: true,
      canExpand: false,
      canShrink: false,
      canChangeType: false,
      canCompact: false,
      canUpgradeVhdx: false,
      canExport: isDifferencing,
      canMerge: false,
      canEditParentPath: false,
      canDeleteFile: false,
      canEditSlot: true,
      canEditMedia: true,
      bannerSeverity: 'warning',
      bannerText: `正作为「${lockInfo.detail || '其他差异盘'}」的父盘，部分操作已锁定`,
      bannerAction: null
    };
  }

  return {
    kind: 'none',
    detail: '',
    isSavedOrPaused: false,
    canOpenFile: true,
    canExpand: true,
    canShrink: !isVhd,
    canChangeType: true,
    canCompact: !isFixed,
    canUpgradeVhdx: isVhd && !isDifferencing,
    canExport: isDifferencing,
    canMerge: isDifferencing,
    canEditParentPath: isDifferencing,
    canDeleteFile: true,
    canEditSlot: true,
    canEditMedia: true,
    bannerSeverity: null,
    bannerText: '',
    bannerAction: null
  };
}

function inspectDiskPartitionLayout(item, access, curGb) {
  if (!access?.canOpenFile) {
    return { readable: false, mode: 'unavailable', floorGb: curGb };
  }
  const p = String(item?.path || '').trim();
  const explicitMode = String(item?.partitionMode || '').trim();
  let mode = explicitMode || 'ntfs';
  if (!explicitMode) {
    if (/(?:linux|ubuntu|debian|ext4|wsl|alpine|arch)/i.test(p)) mode = 'linux';
    else if (/(?:bitlocker|encrypted|fve)/i.test(p)) mode = 'bitlocker';
    else if (/(?:empty|raw|blank|uninit)/i.test(p)) mode = 'empty';
    else if (/(?:corrupt|damage)/i.test(p)) mode = 'corrupt';
    else mode = 'ntfs';
  }

  let floorGb;
  if (Number.isFinite(Number(item?.shrinkFloorGb))) {
    floorGb = Math.max(4, Math.min(curGb, Math.round(Number(item.shrinkFloorGb))));
  } else if (mode === 'empty') {
    floorGb = Math.min(curGb, 4);
  } else if (/(?:full[-_]?part|no[-_]?free|nospace)/i.test(p)) {
    floorGb = curGb;
  } else {
    floorGb = Math.min(curGb, Math.max(16, Math.round(curGb * 0.5)));
  }

  return { readable: mode !== 'corrupt', mode, floorGb };
}

function inspectStorageResizeState({ item, targetGbText, running, vm = null, items = [], access = null }) {
  const curGb = Math.max(1, Math.round(parseStorageSizeGb(item.capacityText || item.sizeText) || 128));
  const raw = String(targetGbText ?? '').trim();
  const isInt = /^\d+$/.test(raw);
  const parsed = isInt ? Number.parseInt(raw, 10) : NaN;
  const isVhd = /\.vhd$/i.test(item.path || '');
  const effAccess = access || inspectVirtualDiskAccessState(item, { running, vm, items });
  const canOnlineExpand = item.controllerType === 'SCSI' && !isVhd;

  if (!effAccess.canExpand && !effAccess.canShrink) {
    let lockedText = '关机后可调整容量';
    let lockedSeverity = 'warning';
    if (effAccess.kind === 'missing') {
      lockedText = '未找到磁盘文件，无法调整容量';
      lockedSeverity = 'error';
    } else if (effAccess.kind === 'host-mounted') {
      lockedText = '卸载宿主机挂载后可调整容量';
    } else if (effAccess.kind === 'locked-vm' || effAccess.kind === 'locked-process') {
      lockedText = '文件正被占用，无法读取分区或调整容量';
    } else if (effAccess.kind === 'parent-in-use') {
      lockedText = '存在子差异盘依赖，禁止修改容量';
    } else if (effAccess.kind === 'vm-saved') {
      lockedText = '彻底关机后可调整容量';
    } else if (running && !canOnlineExpand) {
      lockedText = isVhd ? '.vhd 磁盘需关闭虚拟机后调整容量' : 'IDE 控制器需关闭虚拟机后调整容量';
    }
    return {
      severity: lockedSeverity,
      text: lockedText,
      canApply: false,
      sliderDisabled: true,
      curGb,
      parsed
    };
  }
  if (!raw || !isInt || !Number.isFinite(parsed) || parsed < 1) {
    return {
      severity: 'error',
      text: '请输入大于 0 的整数容量（GB）',
      canApply: false,
      sliderDisabled: false,
      curGb,
      parsed: curGb
    };
  }
  if (parsed === curGb) {
    return {
      severity: null,
      text: '',
      canApply: false,
      sliderDisabled: false,
      curGb,
      parsed
    };
  }
  if (parsed > curGb) {
    if (isVhd && parsed > 2040) {
      return {
        severity: 'warning',
        text: '.vhd 格式上限为 2040 GB',
        canApply: false,
        sliderDisabled: false,
        curGb,
        parsed
      };
    }
    return {
      severity: 'info',
      text: `${running ? '联机扩展' : '扩展容量'} ${curGb} GB → ${parsed} GB（扩展后需在系统内扩容）`,
      canApply: true,
      sliderDisabled: false,
      curGb,
      parsed
    };
  }
  if (isVhd) {
    return {
      severity: 'error',
      text: '.vhd 格式不支持收缩容量',
      canApply: false,
      sliderDisabled: false,
      curGb,
      parsed
    };
  }
  if (running) {
    return {
      severity: 'warning',
      text: '收缩容量需先关闭虚拟机',
      canApply: false,
      sliderDisabled: false,
      curGb,
      parsed
    };
  }
  if (parsed < 4) {
    return {
      severity: 'error',
      text: '虚拟磁盘容量不能小于 4 GB',
      canApply: false,
      sliderDisabled: false,
      curGb,
      parsed
    };
  }

  const layout = inspectDiskPartitionLayout(item, effAccess, curGb);
  if (!layout.readable || layout.mode === 'corrupt') {
    return {
      severity: 'error',
      text: '未识别分区表，已禁止收缩',
      canApply: false,
      sliderDisabled: false,
      curGb,
      parsed
    };
  }
  if (layout.floorGb >= curGb) {
    return {
      severity: 'warning',
      text: `末尾无未分配空间（分区至 ${curGb} GB），需先在系统内缩容`,
      canApply: false,
      sliderDisabled: false,
      curGb,
      parsed
    };
  }
  if (parsed < layout.floorGb) {
    return {
      severity: 'error',
      text: `低于分区末尾（${layout.floorGb} GB），需先在系统内缩容`,
      canApply: false,
      sliderDisabled: false,
      curGb,
      parsed
    };
  }
  if (layout.mode === 'linux' || layout.mode === 'bitlocker') {
    const fsLabel = layout.mode === 'bitlocker' ? 'BitLocker 卷' : 'Linux 卷';
    return {
      severity: 'warning',
      text: `收缩容量 ${curGb} GB → ${parsed} GB（${layout.floorGb} GB 后无分区，未校验${fsLabel}）`,
      canApply: true,
      sliderDisabled: false,
      curGb,
      parsed
    };
  }
  return {
    severity: null,
    text: '',
    canApply: true,
    sliderDisabled: false,
    curGb,
    parsed
  };
}

function inspectSettingsMediaPathState(item, pathStr) {
  const isDvd = item.driveType === 'DvdDrive';
  const raw = String(pathStr || '').trim();
  if (!isDvd && !raw) {
    return { severity: 'error', text: '虚拟硬盘路径不能为空', valid: false };
  }
  if (!raw) {
    return { severity: null, text: '', valid: true };
  }
  const extOk = isDvd ? /\.iso$/i.test(raw) : /\.vhdx?$/i.test(raw);
  if (!extOk) {
    return {
      severity: 'error',
      text: isDvd ? '仅支持 .iso 映像文件' : '仅支持 .vhd / .vhdx 文件',
      valid: false
    };
  }
  const lockInfo = inferVirtualDiskLockFromPath(raw);
  if (lockInfo.kind === 'missing') {
    return { severity: 'error', text: isDvd ? '未找到该 ISO 映像文件' : '未找到该虚拟磁盘文件', valid: false };
  }
  if (!isDvd) {
    if (lockInfo.kind === 'host-mounted') {
      return { severity: 'warning', text: '目标磁盘已在宿主机挂载，应用后需卸载才能修改容量或删除', valid: true };
    }
    if (lockInfo.kind === 'locked-vm' || lockInfo.kind === 'locked-process') {
      return { severity: 'warning', text: '目标磁盘正被占用锁定，应用后暂不可修改容量或压缩', valid: true };
    }
    if (lockInfo.kind === 'parent-in-use') {
      return { severity: 'warning', text: '目标磁盘正被作为父盘依赖，应用后将锁定容量与类型修改', valid: true };
    }
  }
  return { severity: null, text: '', valid: true };
}

function computeStorageSettingsApplyState({ item, draft, items, vm = null, generation, running }) {
  const meta = resolveStorageTypeMeta(item);
  const access = !meta.isPhysical && !meta.isDvd
    ? inspectVirtualDiskAccessState(item, { running, vm, items })
    : {
        kind: running ? 'running' : 'none',
        isSavedOrPaused: false,
        canEditSlot: !running,
        canEditMedia: item.driveType === 'DvdDrive' ? true : !running,
        canDeleteFile: !running,
        bannerSeverity: running ? 'warning' : null,
        bannerText: running ? (meta.isDvd ? '运行中仅支持更换或弹出 ISO 映像' : '关闭虚拟机后可调整控制器插槽') : '',
        bannerAction: null
      };

  const slotDisabled = !access.canEditSlot;
  const otherItems = items.filter((x) => x.id !== item.id);
  const slotForm = {
    controllerType: draft.controllerType,
    controllerNumber: draft.controllerNumber,
    location: draft.controllerLocation
  };
  const slotMeta = ensureSelectedStorageSlot(slotForm, generation, false, item.driveType, otherItems);
  draft.controllerType = slotForm.controllerType;
  draft.controllerNumber = slotForm.controllerNumber;
  draft.controllerLocation = slotForm.location;
  const slotDirty =
    !slotDisabled &&
    slotMeta.locs.length > 0 &&
    (slotForm.controllerType !== item.controllerType ||
      Number(slotForm.controllerNumber) !== Number(item.controllerNumber) ||
      Number(slotForm.location) !== Number(item.controllerLocation));

  const mediaLocked = !access.canEditMedia;
  const mediaState = !meta.isPhysical ? inspectSettingsMediaPathState(item, draft.mediaPath) : { valid: true, text: '' };
  const mediaDirty =
    !meta.isPhysical &&
    !mediaLocked &&
    String(draft.mediaPath || '').trim() !== String(item.path || '').trim();
  const mediaError = mediaDirty && !mediaState.valid;

  const isHardDisk = !meta.isPhysical && !meta.isDvd;
  const vhdMeta = normalizeVirtualDiskType(item);
  const isDifferencing = isHardDisk && vhdMeta.vhdType === 'Differencing';

  let parentDirty = false;
  let parentError = false;
  let parentInsp = null;
  let savedParentInsp = null;
  if (isDifferencing) {
    parentInsp = inspectParentDiskState(
      { parentPath: draft.parentPath, ignoreParentId: draft.ignoreParentId },
      { mode: 'edit', targetPath: item.path, linkedParentPath: item.linkedParentPath }
    );
    savedParentInsp = inspectParentDiskState(
      { parentPath: item.parentPath },
      { mode: 'edit', targetPath: item.path, linkedParentPath: item.linkedParentPath }
    );
    const nextParent = String(draft.parentPath || '').trim();
    const savedParent = String(item.parentPath || '').trim();
    parentDirty = Boolean(access.canEditParentPath) && Boolean(nextParent) && nextParent !== savedParent;
    parentError =
      Boolean(access.canEditParentPath) &&
      nextParent !== savedParent &&
      (!nextParent || parentInsp.severity === 'error' || (Boolean(parentInsp.idMismatch) && !draft.ignoreParentId));
  }

  let resizeDirty = false;
  let resizeError = false;
  let resizeInsp = null;
  let typeDirty = false;
  if (isHardDisk && !isDifferencing) {
    resizeInsp = inspectStorageResizeState({ item, targetGbText: draft.resizeGb, running, vm, items, access });
    resizeDirty =
      !resizeInsp.sliderDisabled &&
      String(draft.resizeGb ?? '').trim() !== String(resizeInsp.curGb);
    resizeError = resizeDirty && !resizeInsp.canApply;
    typeDirty = Boolean(access.canChangeType) && draft.targetVhdType !== vhdMeta.vhdType;
  }

  const hasDirty = Boolean(slotDirty || mediaDirty || parentDirty || resizeDirty || typeDirty);
  const hasError = Boolean(mediaError || parentError || resizeError);
  return {
    meta,
    access,
    slotMeta,
    slotDisabled,
    slotDirty,
    mediaState,
    mediaDirty,
    mediaError,
    vhdMeta,
    isDifferencing,
    parentInsp,
    savedParentInsp,
    parentDirty,
    parentError,
    resizeInsp,
    resizeDirty,
    resizeError,
    typeDirty,
    hasDirty,
    hasError,
    canApply: hasDirty && !hasError
  };
}

function renderStorageSettingsActionsHtml(canApply) {
  return `
    <button class="winui-btn" id="stSettingsBackBtn" type="button">返回</button>
    <button class="winui-btn accent" id="stSettingsApplyBtn" type="button" ${canApply ? '' : 'disabled'}>应用</button>
  `;
}

function renderStorageSettingsSubpageHtml({
  state,
  vm,
  item,
  items,
  generation,
  running,
  renderSettingLabelHtml
}) {
  const draft = ensureStorageSettingsDraft(state, vm, item);
  const st = computeStorageSettingsApplyState({ item, draft, items, vm, generation, running });
  const {
    meta,
    access,
    slotMeta,
    slotDisabled,
    mediaState,
    vhdMeta,
    isDifferencing,
    parentInsp,
    parentError,
    savedParentInsp,
    resizeInsp
  } = st;
  const isVhd = /\.vhd$/i.test(item.path || '');
  const actionBtnsHtml = renderStorageSettingsActionsHtml(st.canApply);
  const slotGridHtml = renderStorageSlotGridHtml({
    slotMeta,
    controllerType: draft.controllerType,
    controllerNumber: draft.controllerNumber,
    location: draft.controllerLocation,
    typeAttr: 'data-st-set-ctrl-type',
    numberId: 'stSetCtrlNumberSelect',
    locationId: 'stSetCtrlLocationSelect',
    disabled: slotDisabled
  });

  if (meta.isPhysical) {
    const topNoticeHtml = item.isPassthroughStale
      ? renderStorageInlineBannerHtml({
          severity: 'warning',
          text: '物理直通引用已失效',
          actionHtml: '<button class="winui-btn st-pinspect-fix-btn" id="stSetRecheckPhysBtn" type="button">重新检测</button>'
        })
      : running
        ? renderStorageInlineBannerHtml({ severity: 'warning', text: '关闭虚拟机后可调整控制器插槽' })
        : '';

    return `
      <div class="dev-stack">
        ${topNoticeHtml}
        <div class="winui-card" style="margin-bottom:0;">
          ${slotGridHtml}
        </div>
        <div class="dev-action-row" style="justify-content:flex-end;">
          ${actionBtnsHtml}
        </div>
      </div>
    `;
  }

  if (meta.isDvd) {
    const deleteFileDisabled = running;
    const topNoticeHtml = running
      ? renderStorageInlineBannerHtml({ severity: 'warning', text: '运行中仅支持更换或弹出 ISO 映像' })
      : '';

    return `
      <div class="dev-stack">
        ${topNoticeHtml}
        <div class="winui-card" style="margin-bottom:0;">
          <div class="st-field-stack">
            <div class="st-field-head">
              <span class="st-field-title">ISO 映像路径</span>
            </div>
            <div class="st-input-group">
              <input type="text" class="winui-textbox is-mono" id="stSetMediaInput" value="${escapeDeviceHtml(draft.mediaPath)}" placeholder="D:\\ISO\\system.iso" />
              <button class="winui-btn" id="stSetMediaBrowseBtn" type="button">浏览</button>
            </div>
            <div id="stSetMediaInspectHost">${mediaState.text ? renderStorageInlineBannerHtml(mediaState) : ''}</div>
          </div>
          ${slotGridHtml}
          ${
            item.hasIsoMedia
              ? `
                <div class="winui-card-row">
                  <div class="setting-row-title">彻底删除 ISO 文件</div>
                  <button class="winui-btn danger" id="stSetDeleteToggleBtn" type="button" ${deleteFileDisabled ? 'disabled' : ''}>删除</button>
                </div>
              `
              : ''
          }
        </div>
        <div class="dev-action-row" style="justify-content:flex-end;">
          ${actionBtnsHtml}
        </div>
      </div>
    `;
  }

  const activeTab = draft.activeTab === 'mount' ? 'mount' : 'disk';
  const isFixed = vhdMeta.vhdType === 'Fixed';
  const parentChainBroken = savedParentInsp?.severity === 'error';
  const savedParentLocked = savedParentInsp?.severity === 'warning' && savedParentInsp?.title === '父盘文件正被占用';
  const hasSavedParent = Boolean(String(item.parentPath || '').trim());
  const exportDisabled = !isDifferencing || !access.canExport || !hasSavedParent || parentChainBroken || savedParentLocked;
  const mergeDisabled = !isDifferencing || !access.canMerge || !hasSavedParent || parentChainBroken || savedParentLocked;
  const parentEditDisabled = !access.canEditParentPath;
  const mediaEditDisabled = !access.canEditMedia;
  const typeChangeDisabled = !access.canChangeType;
  const upgradeVhdxDisabled = !access.canUpgradeVhdx;

  const resizeNum = Math.max(1, Number.parseInt(String(draft.resizeGb || resizeInsp?.curGb || 128), 10) || 128);
  const capPos = getCapacityStopPos(resizeNum);
  const capRatio = capPos / Math.max(1, ST_CAPACITY_STOPS.length - 1);
  const capEquiv = formatCapacityEquiv(resizeNum);
  const compactDisabled = !access.canCompact || isFixed;
  const deleteFileDisabled = !access.canDeleteFile;

  const compactDescText = isFixed
    ? '固定大小磁盘不支持压缩体积。'
    : access.kind === 'host-mounted'
      ? '已在宿主机挂载，压缩将直接执行文件系统感知回收。'
      : !access.canCompact
        ? access.kind === 'running'
          ? '关闭虚拟机后可执行压缩回收空间。'
          : '磁盘文件当前无法独占访问，暂不支持压缩回收。'
        : '回收未分配空闲块以缩小宿主机磁盘文件体积。';

  const exportDescText = parentChainBroken
    ? '差异链断裂，请先修复有效父盘路径后再导出。'
    : savedParentLocked
      ? '父盘文件正被占用，解除占用后可执行导出。'
      : !access.canExport
        ? '请先关机并确保当前磁盘文件未处于占用或挂载状态。'
        : '将整条父盘链与当前增量合成为新的独立虚拟磁盘文件，不改动现有差异链与父盘。';

  const mergeDescText = parentChainBroken
    ? '差异链断裂，请先修复有效父盘路径后再合并。'
    : savedParentLocked
      ? '父盘文件正被占用，解除占用后可执行合并。'
      : !access.canMerge
        ? '请先关机并确保当前磁盘与父盘均未处于占用或挂载状态。'
        : '将增量写入所选父盘并清理已并入差异盘，同时自动切换当前磁盘挂载路径。';

  const showBannerAction =
    access.bannerAction && !(access.bannerAction.id === 'stSetLocateMediaBtn' && activeTab === 'mount');
  const topNoticeHtml = access.bannerText
    ? renderStorageInlineBannerHtml({
        severity: access.bannerSeverity || 'warning',
        text: access.bannerText,
        actionHtml: showBannerAction
          ? `<button class="winui-btn st-pinspect-fix-btn" id="${access.bannerAction.id}" type="button">${access.bannerAction.label}</button>`
          : ''
      })
    : '';

  const settingsTabsHtml = renderPivotTrackHtml(
    `
      <button
        class="st-pivot-btn ${activeTab === 'disk' ? 'active' : ''}"
        data-st-settings-tab="disk"
        type="button"
        role="tab"
        aria-selected="${activeTab === 'disk'}"
      >
        <span class="fi" style="font-size:14px;">&#xE90F;</span>
        <span>编辑</span>
      </button>
      <button
        class="st-pivot-btn ${activeTab === 'mount' ? 'active' : ''}"
        data-st-settings-tab="mount"
        type="button"
        role="tab"
        aria-selected="${activeTab === 'mount'}"
      >
        <span class="fi" style="font-size:14px;">&#xE71B;</span>
        <span>路径</span>
      </button>
    `,
    actionBtnsHtml
  );

  const deleteDiskSectionHtml = `
    <div class="winui-card-row">
      <div class="setting-row-title">彻底删除磁盘文件</div>
      <button class="winui-btn danger" id="stSetDeleteToggleBtn" type="button" ${deleteFileDisabled ? 'disabled' : ''}>删除</button>
    </div>
  `;

  const activeChainNodes =
    isDifferencing && !parentError && Array.isArray(parentInsp?.chain) && parentInsp.chain.length >= 2
      ? parentInsp.chain
      : isDifferencing && Array.isArray(savedParentInsp?.chain) && savedParentInsp.chain.length >= 2
        ? savedParentInsp.chain
        : [];
  const mergeAncestors = activeChainNodes.slice(1).map((node, idx) => ({
    depth: idx + 1,
    name: node.name || `父盘 ${idx + 1}`,
    path: node.path || node.name || ''
  }));
  if (isDifferencing && mergeAncestors.length === 0) {
    const fallbackParent = String(draft.parentPath || item.parentPath || '').trim();
    mergeAncestors.push({
      depth: 1,
      name: formatStorageFileName(fallbackParent, 'parent.vhdx'),
      path: fallbackParent
    });
  }
  const selectedMergeDepth = Math.min(
    Math.max(1, Number(draft.mergeDepth) || 1),
    Math.max(1, mergeAncestors.length)
  );
  draft.mergeDepth = selectedMergeDepth;
  const selectedMergeAncestor = mergeAncestors[selectedMergeDepth - 1] || mergeAncestors[0];
  const exportPathTrimmed = String(draft.exportPath || '').trim();
  const exportPathValid = /\.(vhdx?|vhd)$/i.test(exportPathTrimmed);

  const diskToolsCardHtml = isDifferencing
    ? `
      <div class="winui-card" style="margin-bottom:0;">
        <div class="st-field-stack">
          <div class="st-field-head">
            ${renderSettingLabelHtml('父盘路径', '重新定位差异磁盘父盘。容量与扇区格式由父盘继承，如需调整容量请先导出完整磁盘或合并到父盘。')}
          </div>
          <div class="st-input-group">
            <input type="text" class="winui-textbox is-mono" id="stSetParentInput" value="${escapeDeviceHtml(draft.parentPath)}" placeholder="输入父盘路径 (.vhd / .vhdx)" ${parentEditDisabled ? 'disabled' : ''} />
            <button class="winui-btn" id="stSetParentBrowseBtn" type="button" ${parentEditDisabled ? 'disabled' : ''}>浏览</button>
          </div>
          <div id="stSetParentInspectHost">${renderParentInspectHtml({ parentPath: draft.parentPath, newDiskPath: item.path, ignoreParentId: draft.ignoreParentId }, { mode: 'edit', targetPath: item.path, linkedParentPath: item.linkedParentPath })}</div>
        </div>
        <div class="winui-card-row">
          ${renderSettingLabelHtml('导出完整磁盘', exportDescText)}
          <button class="winui-btn" id="stSetExportToggleBtn" type="button" ${exportDisabled ? 'disabled' : ''}>导出</button>
        </div>
        ${
          draft.exportOpen && !exportDisabled
            ? `
              <div class="st-field-stack" style="background:var(--SubtleFillColorTertiaryBrush);">
                <div class="st-field-head">
                  <span class="st-field-title">导出目标路径</span>
                </div>
                <div class="st-input-group">
                  <input type="text" class="winui-textbox is-mono" id="stSetExportPathInput" value="${escapeDeviceHtml(draft.exportPath)}" placeholder="D:\\Hyper-V\\Virtual Hard Disks\\exported_full.vhdx" />
                  <button class="winui-btn" id="stSetExportBrowseBtn" type="button">浏览</button>
                </div>
                <div style="display:flex;align-items:center;justify-content:flex-end;gap:8px;margin-top:4px;">
                  <button class="winui-btn" id="stSetExportCancelBtn" type="button">取消</button>
                  <button class="winui-btn accent" id="stSetExportConfirmBtn" type="button" ${exportPathValid ? '' : 'disabled'}>开始导出</button>
                </div>
              </div>
            `
            : ''
        }
        <div class="winui-card-row">
          ${renderSettingLabelHtml('合并到父盘', mergeDescText)}
          <button class="winui-btn" id="stSetMergeBtn" type="button" ${mergeDisabled ? 'disabled' : ''}>合并</button>
        </div>
        ${
          draft.mergeConfirm && !mergeDisabled
            ? `
              <div class="st-field-stack" style="background:var(--SubtleFillColorTertiaryBrush);">
                <div class="st-mchain-list" role="radiogroup" aria-label="选择合并目标父盘">
                  <div class="st-mchain-node">
                    <div class="st-mchain-rail">
                      <span class="st-mchain-dot is-source"></span>
                      <span class="st-mchain-line is-active"></span>
                    </div>
                    <div class="st-mchain-card is-source" title="${escapeDeviceHtml(item.path || '')}">
                      <div class="st-mchain-main">
                        <span class="st-mchain-badge">当前</span>
                        <span class="st-mchain-name">${escapeDeviceHtml(item.displayName)}</span>
                      </div>
                    </div>
                  </div>
                  ${mergeAncestors
                    .map((anc, idx) => {
                      const d = anc.depth;
                      const isTarget = d === selectedMergeDepth;
                      const isPassThrough = d < selectedMergeDepth;
                      const isLast = idx === mergeAncestors.length - 1;
                      const stateCls = isTarget ? 'is-target' : isPassThrough ? 'is-passthrough' : 'is-untouched';
                      const dotCls = isTarget ? 'is-target' : isPassThrough ? 'is-passthrough' : '';
                      const lineCls = d < selectedMergeDepth ? 'is-active' : 'is-dim';
                      const statusText = isTarget ? '写入目标' : isPassThrough ? '并入' : '';
                      const badgeText = isLast && mergeAncestors.length > 1 ? '根父盘' : `深度 ${d}`;
                      return `
                        <div class="st-mchain-node">
                          <div class="st-mchain-rail">
                            <span class="st-mchain-dot ${dotCls}"></span>
                            ${isLast ? '' : `<span class="st-mchain-line ${lineCls}"></span>`}
                          </div>
                          <button
                            type="button"
                            class="st-mchain-card ${stateCls}"
                            data-st-merge-depth="${d}"
                            title="${escapeDeviceHtml(anc.path)}"
                            role="radio"
                            aria-checked="${isTarget}"
                          >
                            <div class="st-mchain-main">
                              <span class="st-mchain-badge">${badgeText}</span>
                              <span class="st-mchain-name">${escapeDeviceHtml(anc.name)}</span>
                            </div>
                            ${statusText ? `<span class="st-mchain-status">${statusText}</span>` : ''}
                          </button>
                        </div>
                      `;
                    })
                    .join('')}
                </div>
                ${renderStorageInlineBannerHtml({
                  severity: 'warning',
                  text: `将改写「${selectedMergeAncestor?.name || ''}」并删除原差异盘，完成后自动切换磁盘路径`,
                  actionHtml: `
                    <div style="display:flex;align-items:center;gap:8px;flex-shrink:0;">
                      <button class="winui-btn st-pinspect-fix-btn" id="stSetMergeCancelBtn" type="button">取消</button>
                      <button class="winui-btn danger st-pinspect-fix-btn" id="stSetMergeConfirmBtn" type="button">确认合并</button>
                    </div>
                  `
                })}
              </div>
            `
            : ''
        }
        <div class="winui-card-row">
          ${renderSettingLabelHtml('释放空间', compactDescText)}
          <button class="winui-btn" id="stSetCompactBtn" type="button" ${compactDisabled ? 'disabled' : ''}>压缩</button>
        </div>
        ${deleteDiskSectionHtml}
      </div>
    `
    : `
      <div class="winui-card" style="margin-bottom:0;">
        <div class="st-slider-block">
          <div class="energy-card-head">
            ${renderSettingLabelHtml('修改容量', '自动检测分区末尾与安全收缩下限，.vhd 不支持收缩，SCSI .vhdx 支持联机扩展。')}
            <div class="energy-val-inline">
              <input
                type="text"
                class="energy-num-input compact-num"
                id="stSetResizeInput"
                value="${escapeDeviceHtml(draft.resizeGb)}"
                style="width:${Math.max(2, String(draft.resizeGb || '0').length + 0.5)}ch;"
                ${resizeInsp.sliderDisabled ? 'disabled' : ''}
              />
              <span class="energy-num-unit">GB</span>
            </div>
          </div>
          ${renderEnergySliderTrackHtml({
            id: 'stSetResizeSlider',
            max: ST_CAPACITY_STOPS.length - 1,
            value: Math.round(capPos),
            ratio: capRatio,
            tooltipText: `${resizeNum} GB${capEquiv}`,
            tooltipId: 'stSetResizeTooltip',
            disabled: resizeInsp.sliderDisabled
          })}
          <div id="stSetResizeInspectHost">${resizeInsp.text ? renderStorageInlineBannerHtml({ severity: resizeInsp.severity || 'info', text: resizeInsp.text }) : ''}</div>
        </div>
        <div class="winui-card-row">
          ${renderSettingLabelHtml('修改类型', '动态扩展按需分配空间，固定大小一次性预分配全部空间。')}
          <div class="isa-pill-track inline-width" role="tablist">
            <button class="isa-pill-btn ${draft.targetVhdType === 'Dynamic' ? 'active' : ''}" data-st-set-vhdtype="Dynamic" type="button" ${typeChangeDisabled ? 'disabled' : ''}>动态扩展</button>
            <button class="isa-pill-btn ${draft.targetVhdType === 'Fixed' ? 'active' : ''}" data-st-set-vhdtype="Fixed" type="button" ${typeChangeDisabled ? 'disabled' : ''}>固定大小</button>
          </div>
        </div>
        <div class="winui-card-row">
          ${renderSettingLabelHtml('释放空间', compactDescText)}
          <button class="winui-btn" id="stSetCompactBtn" type="button" ${compactDisabled ? 'disabled' : ''}>压缩</button>
        </div>
        ${
          isVhd
            ? `
              <div class="winui-card-row">
                ${renderSettingLabelHtml('升级为 VHDX 格式', '转换为 .vhdx 容器以突破 2040 GB 容量限制并启用日志保护。')}
                <button class="winui-btn" id="stSetUpgradeVhdxBtn" type="button" ${upgradeVhdxDisabled ? 'disabled' : ''}>转换格式</button>
              </div>
            `
            : ''
        }
        ${deleteDiskSectionHtml}
      </div>
    `;

  const mountSettingsCardHtml = `
    <div class="winui-card" style="margin-bottom:0;">
      <div class="st-field-stack">
        <div class="st-field-head">
          <span class="st-field-title">磁盘文件路径</span>
        </div>
        <div class="st-input-group">
          <input type="text" class="winui-textbox is-mono" id="stSetMediaInput" value="${escapeDeviceHtml(draft.mediaPath)}" placeholder="输入 .vhd / .vhdx 路径" ${mediaEditDisabled ? 'disabled' : ''} />
          <button class="winui-btn" id="stSetMediaBrowseBtn" type="button" ${mediaEditDisabled ? 'disabled' : ''}>浏览</button>
        </div>
        <div id="stSetMediaInspectHost">${mediaState.text ? renderStorageInlineBannerHtml(mediaState) : ''}</div>
      </div>
      ${slotGridHtml}
    </div>
  `;

  return `
    <div class="dev-stack">
      ${settingsTabsHtml}
      ${topNoticeHtml}
      ${activeTab === 'disk' ? diskToolsCardHtml : mountSettingsCardHtml}
    </div>
  `;
}

function bindStorageSettingsEvents({
  host,
  state,
  vm,
  item,
  targetIdx,
  items,
  HOST_PHYSICAL_DISKS,
  generation,
  running,
  rerender,
  renderDashboardSummary,
  showNotice,
  runProgressFlow
}) {
  const draft = ensureStorageSettingsDraft(state, vm, item);
  const initSt = computeStorageSettingsApplyState({ item, draft, items, vm, generation, running });
  const initAccess = initSt.access;
  const parentChainBroken = initSt.savedParentInsp?.severity === 'error';
  const savedParentLocked =
    initSt.savedParentInsp?.severity === 'warning' && initSt.savedParentInsp?.title === '父盘文件正被占用';
  const hasSavedParent = Boolean(String(item.parentPath || '').trim());
  const exportDisabled = !initSt.isDifferencing || !initAccess.canExport || !hasSavedParent || parentChainBroken || savedParentLocked;
  const mergeDisabled = !initSt.isDifferencing || !initAccess.canMerge || !hasSavedParent || parentChainBroken || savedParentLocked;
  const activeChainNodes =
    initSt.isDifferencing && !initSt.parentError && Array.isArray(initSt.parentInsp?.chain) && initSt.parentInsp.chain.length >= 2
      ? initSt.parentInsp.chain
      : initSt.isDifferencing && Array.isArray(initSt.savedParentInsp?.chain) && initSt.savedParentInsp.chain.length >= 2
        ? initSt.savedParentInsp.chain
        : [];
  const mergeAncestors = activeChainNodes.slice(1).map((node, idx) => ({
    depth: idx + 1,
    name: node.name || `父盘 ${idx + 1}`,
    path: node.path || node.name || ''
  }));
  if (initSt.isDifferencing && mergeAncestors.length === 0) {
    const fallbackParent = String(draft.parentPath || item.parentPath || '').trim();
    mergeAncestors.push({
      depth: 1,
      name: formatStorageFileName(fallbackParent, 'parent.vhdx'),
      path: fallbackParent
    });
  }
  const selectedMergeDepth = Math.min(
    Math.max(1, Number(draft.mergeDepth) || 1),
    Math.max(1, mergeAncestors.length)
  );
  const selectedMergeAncestor = mergeAncestors[selectedMergeDepth - 1] || mergeAncestors[0];

  const goBackToList = () => {
    state.storageMode = 'list';
    state.storageSettingsTargetId = null;
    state.storageSettingsDraft = null;
    rerender();
  };

  const syncApplyButtonState = () => {
    const applyBtn = host.querySelector('#stSettingsApplyBtn');
    if (!applyBtn) return;
    const nextSt = computeStorageSettingsApplyState({ item, draft, items, vm, generation, running });
    applyBtn.disabled = !nextSt.canApply;
  };

  host.querySelectorAll('[data-st-settings-tab]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const nextTab = btn.getAttribute('data-st-settings-tab');
      if (!nextTab || nextTab === draft.activeTab) return;
      draft.activeTab = nextTab === 'mount' ? 'mount' : 'disk';
      rerender();
    });
  });

  host.querySelector('#stSettingsBackBtn')?.addEventListener('click', goBackToList);

  host.querySelector('#stSettingsApplyBtn')?.addEventListener('click', () => {
    const st = computeStorageSettingsApplyState({ item, draft, items, vm, generation, running });
    if (!st.canApply) return;

    const doResize = Boolean(st.resizeDirty && st.resizeInsp?.canApply);
    const nextGb = st.resizeInsp?.parsed;
    const prevCapGb = st.resizeInsp?.curGb;
    const doConvert = Boolean(st.typeDirty);
    const toFixed = draft.targetVhdType === 'Fixed';
    const doParent = Boolean(st.parentDirty);
    const nextParent = String(draft.parentPath || '').trim();
    const doMedia = Boolean(st.mediaDirty);
    const nextMedia = String(draft.mediaPath || '').trim();
    const doSlot = Boolean(st.slotDirty);

    const commitLightweight = () => {
      if (doMedia) {
        item.path = nextMedia;
        item.lockState = undefined;
        item.lockDetail = undefined;
        item.partitionMode = undefined;
        item.shrinkFloorGb = undefined;
        if (item.driveType === 'DvdDrive') {
          item.diskType = nextMedia ? 'Virtual' : 'Empty';
          item.hasIsoMedia = Boolean(nextMedia);
          item.displayName = nextMedia ? formatStorageFileName(nextMedia, '光驱') : '光驱';
          item.sizeText = nextMedia ? item.sizeText || '4.7 GB' : '';
        } else {
          item.displayName = formatStorageFileName(nextMedia, item.displayName);
          if (!doConvert && !doParent) {
            item.vhdType = 'Dynamic';
            item.vhdFormat = '动态扩展';
            item.parentPath = '';
          }
        }
      }
      if (doParent) {
        item.parentPath = nextParent;
        item.linkedParentPath = nextParent;
        item.vhdType = 'Differencing';
        item.vhdFormat = '差异';
      }
      if (doSlot) {
        item.controllerType = draft.controllerType;
        item.controllerNumber = Number(draft.controllerNumber);
        item.controllerLocation = Number(draft.controllerLocation);
      }
    };

    const usedForceParent = Boolean(doParent && draft.ignoreParentId);

    if (doResize || doConvert) {
      const steps = ['校验虚拟磁盘元数据'];
      if (doResize) {
        steps.push(nextGb > prevCapGb ? `扩展逻辑容量至 ${nextGb} GB` : `收缩逻辑容量至 ${nextGb} GB`);
      }
      if (doConvert) {
        steps.push(toFixed ? '预分配固定大小数据块' : '释放空白块并转为动态扩展');
      }
      if (doSlot || doMedia) {
        steps.push('更新路径与插槽配置');
      }
      runProgressFlow({
        steps,
        progressStepIndex: 1,
        returnMode: 'settings',
        onComplete: () => {
          commitLightweight();
          if (doResize) {
            item.capacityText = `${nextGb} GB`;
            if (nextGb < prevCapGb && Number.isFinite(Number(item.shrinkFloorGb))) {
              item.shrinkFloorGb = Math.min(Number(item.shrinkFloorGb), nextGb);
            }
            if (normalizeVirtualDiskType(item).vhdType === 'Fixed') {
              item.sizeText = `${nextGb} GB`;
            }
          }
          if (doConvert) {
            item.vhdType = toFixed ? 'Fixed' : 'Dynamic';
            item.vhdFormat = toFixed ? '固定大小' : '动态扩展';
            if (toFixed) {
              item.sizeText = item.capacityText || item.sizeText || '128 GB';
            } else {
              const capGb = Math.max(8, Math.round((parseStorageSizeGb(item.capacityText) || 128) * 0.3));
              item.sizeText = `${capGb}.0 GB`;
            }
          }
          state.storageSettingsDraft = null;
          ensureStorageSettingsDraft(state, vm, item);
          syncVmDisksFromStorageState(state, vm);
          renderDashboardSummary?.();
          showNotice?.(`已应用「${item.displayName}」设置`);
        }
      });
      return;
    }

    commitLightweight();
    state.storageSettingsDraft = null;
    ensureStorageSettingsDraft(state, vm, item);
    syncVmDisksFromStorageState(state, vm);
    renderDashboardSummary?.();
    rerender();
    showNotice?.(usedForceParent ? `已忽略父盘 ID 并连接到「${formatStorageFileName(nextParent)}」` : `已应用「${item.displayName}」设置`);
  });

  host.querySelector('#stSetRecheckPhysBtn')?.addEventListener('click', () => {
    const diskNum = Number(item.diskNumber);
    const matched = HOST_PHYSICAL_DISKS.find((d) => Number(d.number) === diskNum);
    if (matched && String(matched.status || '').toLowerCase() === 'online') {
      matched.status = 'Offline';
    }
    item.isPassthroughStale = false;
    syncVmDisksFromStorageState(state, vm);
    renderDashboardSummary?.();
    rerender();
    showNotice?.('已重新校验物理直通状态');
  });

  host.querySelector('#stSetLocateMediaBtn')?.addEventListener('click', () => {
    draft.activeTab = 'mount';
    rerender();
    requestAnimationFrame(() => host.querySelector('#stSetMediaInput')?.focus?.());
  });

  host.querySelector('#stSetDismountHostBtn')?.addEventListener('click', () => {
    item.lockState = 'none';
    item.lockDetail = '';
    syncVmDisksFromStorageState(state, vm);
    renderDashboardSummary?.();
    rerender();
    showNotice?.('已卸载宿主机挂载，已完成分区边界检测');
  });

  host.querySelector('#stSetRecheckDiskAccessBtn')?.addEventListener('click', () => {
    item.lockState = 'none';
    item.lockDetail = '';
    syncVmDisksFromStorageState(state, vm);
    renderDashboardSummary?.();
    rerender();
    showNotice?.('磁盘占用已解除，已完成分区边界检测');
  });

  const bindEditParentInspectControls = () => {
    const inspectEl = host.querySelector('#stSetParentInspectHost');
    inspectEl?.querySelector('[data-st-parent-force-connect]')?.addEventListener('click', (e) => {
      if (!initAccess.canEditParentPath) return;
      draft.ignoreParentId = e.currentTarget.getAttribute('data-st-parent-force-connect') === 'true';
      syncParentSection();
    });
  };

  const syncParentSection = () => {
    const inspectEl = host.querySelector('#stSetParentInspectHost');
    if (inspectEl) {
      inspectEl.innerHTML = renderParentInspectHtml(
        { parentPath: draft.parentPath, newDiskPath: item.path, ignoreParentId: draft.ignoreParentId },
        { mode: 'edit', targetPath: item.path, linkedParentPath: item.linkedParentPath }
      );
    }
    bindEditParentInspectControls();
    syncApplyButtonState();
  };

  bindEditParentInspectControls();

  host.querySelector('#stSetParentInput')?.addEventListener('input', (e) => {
    if (!initAccess.canEditParentPath) return;
    draft.parentPath = e.target.value;
    draft.ignoreParentId = false;
    syncParentSection();
  });

  host.querySelector('#stSetParentBrowseBtn')?.addEventListener('click', () => {
    if (!initAccess.canEditParentPath) return;
    const candidates = [
      'D:\\Hyper-V\\Base\\Win11_24H2_Base.vhdx',
      ...ST_PARENT_DISK_SCENARIOS.map((s) => s.path)
    ];
    const curIdx = candidates.findIndex((p) => p.toLowerCase() === String(draft.parentPath || '').trim().toLowerCase());
    draft.parentPath = candidates[(curIdx + 1) % candidates.length];
    draft.ignoreParentId = false;
    rerender();
  });

  host.querySelector('#stSetExportToggleBtn')?.addEventListener('click', () => {
    if (exportDisabled) return;
    draft.exportOpen = !draft.exportOpen;
    if (draft.exportOpen) {
      draft.mergeConfirm = false;
    }
    rerender();
  });

  host.querySelector('#stSetExportPathInput')?.addEventListener('input', (e) => {
    draft.exportPath = e.target.value;
    const confirmBtn = host.querySelector('#stSetExportConfirmBtn');
    if (confirmBtn) {
      confirmBtn.disabled = !/\.(vhdx?|vhd)$/i.test(String(draft.exportPath || '').trim());
    }
  });

  host.querySelector('#stSetExportBrowseBtn')?.addEventListener('click', () => {
    const candidates = [
      'D:\\Hyper-V\\Virtual Hard Disks\\win11_dev_full.vhdx',
      'D:\\Hyper-V\\Exports\\Win11_24H2_Standalone.vhdx',
      'E:\\Archive\\win11_exported.vhdx'
    ];
    const curIdx = candidates.findIndex((p) => p.toLowerCase() === String(draft.exportPath || '').trim().toLowerCase());
    draft.exportPath = candidates[(curIdx + 1) % candidates.length];
    rerender();
  });

  host.querySelector('#stSetExportCancelBtn')?.addEventListener('click', () => {
    draft.exportOpen = false;
    rerender();
  });

  host.querySelector('#stSetExportConfirmBtn')?.addEventListener('click', () => {
    if (exportDisabled) return;
    const outPath = String(draft.exportPath || '').trim();
    if (!/\.(vhdx?|vhd)$/i.test(outPath)) return;
    const outName = formatStorageFileName(outPath, 'exported_full.vhdx');
    runProgressFlow({
      steps: [
        '校验差异链完整性',
        '读取父盘链与当前增量数据块',
        `导出完整磁盘（${outName}）`
      ],
      progressStepIndex: 1,
      returnMode: 'settings',
      onComplete: () => {
        draft.exportOpen = false;
        showNotice?.(`已导出完整磁盘：${outPath}`);
      }
    });
  });

  host.querySelectorAll('[data-st-merge-depth]').forEach((btn) => {
    btn.addEventListener('click', () => {
      if (mergeDisabled) return;
      draft.mergeDepth = Math.max(1, Number(btn.getAttribute('data-st-merge-depth')) || 1);
      rerender();
    });
  });

  host.querySelector('#stSetMergeBtn')?.addEventListener('click', () => {
    if (mergeDisabled) return;
    draft.mergeConfirm = !draft.mergeConfirm;
    if (draft.mergeConfirm) {
      draft.exportOpen = false;
    }
    rerender();
  });

  host.querySelector('#stSetMergeCancelBtn')?.addEventListener('click', () => {
    draft.mergeConfirm = false;
    rerender();
  });

  host.querySelector('#stSetMergeConfirmBtn')?.addEventListener('click', () => {
    if (mergeDisabled) return;
    const targetAnc = selectedMergeAncestor;
    const targetName = targetAnc?.name || 'parent.vhdx';
    const targetPath = targetAnc?.path || item.parentPath || '';
    const mergedToRoot = selectedMergeDepth >= mergeAncestors.length;
    const nextParentAnc = !mergedToRoot ? mergeAncestors[selectedMergeDepth] : null;
    runProgressFlow({
      steps: [
        '校验差异链完整性',
        `向深度 ${selectedMergeDepth} 父盘写入增量数据（${targetName}）`,
        '清理已并入的差异盘文件',
        `切换磁盘挂载路径至 ${targetName}`
      ],
      progressStepIndex: 1,
      returnMode: 'settings',
      onComplete: () => {
        const diffGb = parseStorageSizeGb(item.sizeText) || 4;
        const mergedGb = Math.min(parseStorageSizeGb(item.capacityText) || 128, Math.max(24, Math.round((diffGb + 22.4) * 10) / 10));
        if (targetPath) {
          item.path = targetPath;
          item.displayName = formatStorageFileName(targetPath, targetName);
        }
        item.lockState = undefined;
        item.lockDetail = undefined;
        if (mergedToRoot || !nextParentAnc?.path) {
          item.vhdType = 'Dynamic';
          item.vhdFormat = '动态扩展';
          item.parentPath = '';
          item.linkedParentPath = '';
        } else {
          item.vhdType = 'Differencing';
          item.vhdFormat = '差异';
          item.parentPath = nextParentAnc.path;
          item.linkedParentPath = nextParentAnc.path;
        }
        item.sizeText = `${mergedGb} GB`;
        state.storageSettingsDraft = null;
        ensureStorageSettingsDraft(state, vm, item);
        syncVmDisksFromStorageState(state, vm);
        renderDashboardSummary?.();
        showNotice?.(`已合并至「${targetName}」，已清理原差异盘并切换挂载路径`);
      }
    });
  });

  const resizeInput = host.querySelector('#stSetResizeInput');
  const resizeSlider = host.querySelector('#stSetResizeSlider');
  const resizeTip = host.querySelector('#stSetResizeTooltip');
  const resizeInspectEl = host.querySelector('#stSetResizeInspectHost');

  const syncResizeReadout = (gbVal, syncInputText = true) => {
    const validNum = Number.isFinite(gbVal) && gbVal >= 1;
    if (syncInputText && resizeInput && validNum) {
      const text = String(gbVal);
      resizeInput.value = text;
      resizeInput.style.width = `${Math.max(2, text.length + 0.5)}ch`;
    }
    const insp = inspectStorageResizeState({ item, targetGbText: draft.resizeGb, running, vm, items, access: initAccess });
    const safeNum = validNum ? gbVal : insp.curGb;
    const equiv = validNum ? formatCapacityEquiv(safeNum) : '';
    if (resizeTip) resizeTip.textContent = validNum ? `${safeNum} GB${equiv}` : '请输入有效容量';
    if (resizeSlider && validNum) {
      const pos = getCapacityStopPos(safeNum);
      resizeSlider.style.setProperty('--energy-ratio', (pos / Math.max(1, ST_CAPACITY_STOPS.length - 1)).toFixed(4));
      resizeSlider.setAttribute('aria-valuenow', String(Math.round(pos)));
    }
    if (resizeInspectEl) {
      resizeInspectEl.innerHTML = insp.text
        ? renderStorageInlineBannerHtml({ severity: insp.severity || 'info', text: insp.text })
        : '';
    }
    syncApplyButtonState();
  };

  bindSteppedEnergySlider(resizeSlider, {
    count: ST_CAPACITY_STOPS.length,
    onStep: (idx) => {
      if (initSt.resizeInsp?.sliderDisabled) return;
      const nextGb = ST_CAPACITY_STOPS[idx];
      draft.resizeGb = String(nextGb);
      syncResizeReadout(nextGb, true);
    }
  });

  resizeInput?.addEventListener('input', (e) => {
    if (initSt.resizeInsp?.sliderDisabled) return;
    draft.resizeGb = e.target.value;
    resizeInput.style.width = `${Math.max(2, String(e.target.value || '0').length + 0.5)}ch`;
    const parsed = /^\d+$/.test(String(e.target.value || '').trim()) ? Number.parseInt(String(e.target.value || '').trim(), 10) : NaN;
    syncResizeReadout(parsed, false);
  });

  host.querySelector('#stSetCompactBtn')?.addEventListener('click', () => {
    if (!initAccess.canCompact || normalizeVirtualDiskType(item).vhdType === 'Fixed') return;
    const isAlreadyAttached = initAccess.kind === 'host-mounted';
    runProgressFlow({
      steps: isAlreadyAttached
        ? ['检测宿主机已挂载卷', '原地执行文件系统感知压缩']
        : ['检查磁盘', '回收容量'],
      progressStepIndex: 1,
      returnMode: 'settings',
      onComplete: () => {
        const curFileGb = parseStorageSizeGb(item.sizeText);
        if (curFileGb && curFileGb >= 2) {
          const nextFileGb = Math.max(1, Math.round(curFileGb * 0.86 * 10) / 10);
          item.sizeText = `${nextFileGb} GB`;
        }
        syncVmDisksFromStorageState(state, vm);
        renderDashboardSummary?.();
        showNotice?.(isAlreadyAttached ? `「${item.displayName}」原地压缩完成（已保留宿主机挂载）` : `「${item.displayName}」压缩完成`);
      }
    });
  });

  host.querySelectorAll('[data-st-set-vhdtype]').forEach((btn) => {
    btn.addEventListener('click', () => {
      if (btn.disabled || !initAccess.canChangeType) return;
      draft.targetVhdType = btn.getAttribute('data-st-set-vhdtype') || 'Dynamic';
      rerender();
    });
  });

  host.querySelector('#stSetUpgradeVhdxBtn')?.addEventListener('click', () => {
    if (!initAccess.canUpgradeVhdx || !/\.vhd$/i.test(item.path || '') || normalizeVirtualDiskType(item).vhdType === 'Differencing') return;
    runProgressFlow({
      steps: ['创建目标 VHDX 容器', '复制扇区并切换到新容器'],
      progressStepIndex: 1,
      returnMode: 'settings',
      onComplete: () => {
        item.path = String(item.path || '').replace(/\.vhd$/i, '.vhdx');
        item.displayName = formatStorageFileName(item.path, String(item.displayName || '').replace(/\.vhd$/i, '.vhdx'));
        draft.mediaPath = item.path;
        syncVmDisksFromStorageState(state, vm);
        renderDashboardSummary?.();
        showNotice?.(`已升级为 VHDX 格式：${item.displayName}`);
      }
    });
  });

  const syncMediaSection = () => {
    const mState = inspectSettingsMediaPathState(item, draft.mediaPath);
    const mInspect = host.querySelector('#stSetMediaInspectHost');
    if (mInspect) {
      mInspect.innerHTML = mState.text ? renderStorageInlineBannerHtml(mState) : '';
    }
    syncApplyButtonState();
  };

  host.querySelector('#stSetMediaInput')?.addEventListener('input', (e) => {
    if (!initAccess.canEditMedia) return;
    draft.mediaPath = e.target.value;
    syncMediaSection();
  });

  host.querySelector('#stSetMediaBrowseBtn')?.addEventListener('click', () => {
    if (!initAccess.canEditMedia) return;
    const isDvd = item.driveType === 'DvdDrive';
    const candidates = isDvd
      ? ['D:\\ISO\\Win11_24H2_ARM64.iso', 'D:\\Hyper-V\\ISO\\Qualcomm_GPU.iso', '']
      : [
          'D:\\Hyper-V\\Virtual Hard Disks\\system_updated.vhdx',
          'D:\\Hyper-V\\Virtual Hard Disks\\full_partition_nospace.vhdx',
          'D:\\Hyper-V\\Virtual Hard Disks\\wsl_ubuntu_ext4.vhdx',
          'D:\\Hyper-V\\Virtual Hard Disks\\host_mounted_data.vhdx',
          'D:\\Hyper-V\\Virtual Hard Disks\\shared_locked_vm.vhdx',
          'D:\\Hyper-V\\Virtual Hard Disks\\missing_disk.vhdx'
        ];
    const curIdx = candidates.findIndex((c) => c.toLowerCase() === String(draft.mediaPath || '').trim().toLowerCase());
    draft.mediaPath = candidates[(curIdx + 1) % candidates.length];
    rerender();
  });

  host.querySelectorAll('[data-st-set-ctrl-type]').forEach((btn) => {
    btn.addEventListener('click', () => {
      if (btn.disabled || initSt.slotDisabled) return;
      draft.controllerType = btn.getAttribute('data-st-set-ctrl-type') || 'SCSI';
      rerender();
    });
  });

  host.querySelector('#stSetCtrlNumberSelect')?.addEventListener('change', (e) => {
    if (initSt.slotDisabled) return;
    draft.controllerNumber = Number(e.target.value);
    rerender();
  });

  host.querySelector('#stSetCtrlLocationSelect')?.addEventListener('change', (e) => {
    if (initSt.slotDisabled) return;
    draft.controllerLocation = Number(e.target.value);
    rerender();
  });

  host.querySelector('#stSetDeleteToggleBtn')?.addEventListener('click', () => {
    if (!initAccess.canDeleteFile) return;
    const modal = document.getElementById('stDeleteConfirmModal');
    const titleEl = document.getElementById('stDeleteConfirmTitle');
    const descEl = document.getElementById('stDeleteConfirmDesc');
    const pathEl = document.getElementById('stDeleteConfirmPath');
    const cancelBtn = document.getElementById('stDeleteConfirmCancelBtn');
    const okBtn = document.getElementById('stDeleteConfirmOkBtn');
    const isDvdItem = item.driveType === 'DvdDrive';
    const pathText = initSt.meta?.pathText || item.path || '';
    const executeDelete = () => {
      const removed = items.splice(targetIdx, 1)[0];
      syncVmDisksFromStorageState(state, vm);
      renderDashboardSummary?.();
      goBackToList();
      const removedPath = removed?.path || removed?.displayName || '';
      showNotice?.(
        removed?.driveType === 'DvdDrive'
          ? `已彻底删除 ISO 文件：${removedPath}`
          : `已彻底删除磁盘文件：${removedPath}`
      );
    };
    if (!modal || !cancelBtn || !okBtn) {
      executeDelete();
      return;
    }
    if (titleEl) {
      titleEl.textContent = isDvdItem
        ? '确认彻底删除 ISO 文件'
        : initSt.isDifferencing
          ? '确认彻底删除差异磁盘文件'
          : '确认彻底删除磁盘文件';
    }
    if (descEl) {
      descEl.textContent = isDvdItem
        ? '确定要从宿主机永久删除该 ISO 文件吗？此操作不可撤销。'
        : initSt.isDifferencing
          ? '确定要从宿主机永久删除该差异磁盘文件吗？此操作不可撤销。'
          : '确定要从宿主机永久删除该虚拟磁盘文件吗？此操作不可撤销。';
    }
    if (pathEl) {
      pathEl.textContent = pathText || '未知路径';
      pathEl.hidden = !pathText;
    }
    const closeModal = () => {
      modal.hidden = true;
      cancelBtn.onclick = null;
      okBtn.onclick = null;
      modal.onclick = null;
      document.removeEventListener('keydown', onKeyDown);
    };
    const onKeyDown = (ev) => {
      if (ev.key === 'Escape') closeModal();
    };
    cancelBtn.onclick = closeModal;
    modal.onclick = (ev) => {
      if (ev.target === modal) closeModal();
    };
    okBtn.onclick = () => {
      closeModal();
      executeDelete();
    };
    document.addEventListener('keydown', onKeyDown);
    modal.hidden = false;
  });
}

export function renderStorageWorkbench(host, ctx) {
  const {
    state,
    vm,
    HOST_PHYSICAL_DISKS = [],
    HOST_OPTICAL_DRIVES = [],
    INITIAL_STORAGE_ITEMS = [],
    renderTitleFirstKvItemHtml,
    renderSettingLabelHtml,
    renderDashboardSummary,
    showNotice,
    onLayoutNeeded,
    bindHostControls
  } = ctx;

  const rerender = () => {
    syncVmDisksFromStorageState(state, vm);
    renderStorageWorkbench(host, ctx);
    bindHostControls?.(host);
    requestAnimationFrame(() => onLayoutNeeded?.());
  };

  const items = ensureStorageState(state, vm, INITIAL_STORAGE_ITEMS);
  if (vm.id === 'vm-test') state.storageItems = items;
  syncVmDisksFromStorageState(state, vm);
  ctx.setPageTabsTrailingAction?.('', null);
  const generation = Number(vm.generation) === 1 ? 1 : 2;
  const running = Boolean(vm.running);

  const clearTimer = () => {
    if (state.storageTaskTimer) {
      clearTimeout(state.storageTaskTimer);
      state.storageTaskTimer = null;
    }
  };

  const runProgressFlow = ({ steps, progressStepIndex = -1, returnMode = 'list', onComplete }) => {
    clearTimer();
    state.storageMode = 'progress';
    state.storageProgress = {
      steps,
      activeIdx: 0,
      pct: progressStepIndex === 0 ? 0 : null,
      progressStepIndex,
      returnMode,
      done: false
    };
    rerender();

    const advance = () => {
      const p = state.storageProgress;
      if (!p || state.storageMode !== 'progress') return;
      if (p.activeIdx === p.progressStepIndex) {
        const nextPct = Math.min(100, (p.pct ?? 0) + 12);
        p.pct = nextPct;
        const pctEl = host.querySelector('.dev-task-pct');
        const fillEl = host.querySelector('.dev-task-progress-fill');
        if (pctEl && fillEl && nextPct < 100) {
          pctEl.textContent = `${nextPct}%`;
          fillEl.style.width = `${nextPct}%`;
          state.storageTaskTimer = setTimeout(advance, 90);
          return;
        }
        if (nextPct >= 100) {
          p.pct = null;
          p.activeIdx += 1;
        } else {
          state.storageTaskTimer = setTimeout(advance, 90);
          return;
        }
      } else {
        p.activeIdx += 1;
      }

      if (p.activeIdx >= p.steps.length) {
        const hasProblem = Boolean(p.error || p.failed);
        p.done = true;
        clearTimer();
        onComplete?.();
        if (!hasProblem) {
          const nextMode = p.returnMode === 'settings' && state.storageSettingsTargetId ? 'settings' : 'list';
          state.storageMode = nextMode;
          state.storageProgress = null;
          renderDashboardSummary?.();
        }
        rerender();
        return;
      }
      if (p.activeIdx === p.progressStepIndex) p.pct = 0;
      rerender();
      state.storageTaskTimer = setTimeout(advance, p.activeIdx === p.progressStepIndex ? 90 : 380);
    };

    state.storageTaskTimer = setTimeout(advance, pInitialDelay(progressStepIndex));
  };

  function pInitialDelay(progressIdx) {
    return progressIdx === 0 ? 90 : 380;
  }

  // =========================================================================
  // View 3: 异步任务进度视图（打包 ISO / 磁盘维护与转换 — 复用 .dev-task-list）
  // =========================================================================
  if (state.storageMode === 'progress' && state.storageProgress) {
    const p = state.storageProgress;
    const rowsHtml = p.steps
      .map((label, idx) => {
        const done = p.done || idx < p.activeIdx;
        const active = !p.done && idx === p.activeIdx;
        const iconHtml = done
          ? '<span class="fi" style="font-size:18px;color:#3CCB5F;">&#xEC61;</span>'
          : active
            ? '<span class="dev-spinner" aria-hidden="true"></span>'
            : '<span class="fi" style="font-size:17px;color:var(--TextFillColorTertiaryBrush);">&#xEA3A;</span>';
        const showPct = active && Number.isFinite(p.pct);
        const clamped = showPct ? Math.min(100, Math.max(0, Math.round(p.pct))) : 0;
        return `
          <div class="dev-task-row ${active ? 'is-active' : done ? 'is-done' : 'is-pending'}">
            <div class="dev-task-icon">${iconHtml}</div>
            <div style="font-size:14.5px;font-weight:600;color:var(--TextFillColorPrimaryBrush);min-width:0;">${escapeDeviceHtml(label)}</div>
            ${showPct ? `<span class="dev-task-pct">${clamped}%</span>` : ''}
            ${showPct ? `<div class="dev-task-progress"><div class="dev-task-progress-fill" style="width:${clamped}%;"></div></div>` : ''}
          </div>
        `;
      })
      .join('');

    const footerHtml = p.done
      ? `
        <div class="dev-action-row" style="justify-content:flex-end;">
          <button class="winui-btn accent" id="stProgressDoneBtn" type="button">返回</button>
        </div>
      `
      : '';

    host.innerHTML = `
      <div class="dev-stack">
        <div class="dev-task-list">${rowsHtml}</div>
        ${footerHtml}
      </div>
    `;

    host.querySelector('#stProgressDoneBtn')?.addEventListener('click', () => {
      const nextMode = p.returnMode === 'settings' && state.storageSettingsTargetId ? 'settings' : 'list';
      state.storageMode = nextMode;
      state.storageProgress = null;
      renderDashboardSummary?.();
      rerender();
    });
    requestAnimationFrame(() => onLayoutNeeded?.());
    return;
  }

  // =========================================================================
  // View 4: 存储设备设置子页（齿轮按钮进入：容量编辑 / 差异链修复 / 类型转换 / 插槽 / 删除）
  // =========================================================================
  if (state.storageMode === 'settings') {
    const targetIdx = items.findIndex((x) => x.id === state.storageSettingsTargetId);
    const targetItem = targetIdx >= 0 ? items[targetIdx] : null;
    if (!targetItem) {
      state.storageMode = 'list';
      state.storageSettingsTargetId = null;
      state.storageSettingsDraft = null;
      rerender();
      return;
    }

    host.innerHTML = renderStorageSettingsSubpageHtml({
      state,
      vm,
      item: targetItem,
      items,
      generation,
      running,
      renderSettingLabelHtml
    });

    bindStorageSettingsEvents({
      host,
      state,
      vm,
      item: targetItem,
      targetIdx,
      items,
      HOST_PHYSICAL_DISKS,
      generation,
      running,
      rerender,
      renderDashboardSummary,
      showNotice,
      runProgressFlow
    });

    bindHostControls?.(host);
    requestAnimationFrame(() => onLayoutNeeded?.());
    return;
  }

  // =========================================================================
  // View 2: 添加存储（5 套候选布局统一切换与交互绑定）
  // =========================================================================
  if (state.storageMode === 'add') {
    const f = state.storageForm;
    const variant = state.storageAddVariant || 'pivot-2x3';
    const intent = state.storageAddIntent || 'existing';
    const effectiveDriveType =
      intent === 'new-iso'
        ? 'DvdDrive'
        : intent === 'new-disk'
          ? 'HardDisk'
          : intent === 'physical'
            ? (generation === 1 && f.physicalKind === 'optical' ? 'DvdDrive' : 'HardDisk')
            : f.driveType || 'HardDisk';

    const slotMeta = ensureSelectedStorageSlot(f, generation, running, effectiveDriveType, items);
    const bestSlot = planBestStorageSlot(generation, running, effectiveDriveType, items);
    const activeSlot = f.autoLocation
      ? bestSlot
      : slotMeta.locs.includes(Number(f.location))
        ? { controllerType: f.controllerType, controllerNumber: Number(f.controllerNumber), location: Number(f.location) }
        : null;

    const confirmDisabled = isAddStorageConfirmDisabled({ intent, effectiveDriveType, generation, running, f, activeSlot });
    ctx.setPageTabsTrailingAction?.(
      `<button class="winui-btn" id="stAddCancelBtn" type="button">返回</button><button class="winui-btn accent" id="stAddConfirmBtn" type="button" ${confirmDisabled ? 'disabled' : ''}>添加</button>`,
      null
    );

    host.innerHTML = renderStorageAddLayoutHtml({
      state,
      variant,
      f,
      intent,
      effectiveDriveType,
      slotMeta,
      activeSlot,
      generation,
      running,
      items,
      HOST_PHYSICAL_DISKS,
      HOST_OPTICAL_DRIVES,
      renderSettingLabelHtml
    });

    const syncConfirmButtonState = () => {
      const btn = document.getElementById('stAddConfirmBtn');
      if (!btn) return;
      btn.disabled = isAddStorageConfirmDisabled({ intent, effectiveDriveType, generation, running, f, activeSlot });
    };

    const syncExistingInspectHost = () => {
      const box = host.querySelector('#stExistingInspectHost');
      if (!box) return;
      const st = inspectExistingPathState(f, effectiveDriveType);
      box.innerHTML = st ? renderStorageInlineBannerHtml(st) : '';
    };

    const syncNewDiskInspectHosts = () => {
      const pBox = host.querySelector('#stNewDiskPathInspectHost');
      if (pBox) {
        const st = inspectNewDiskPathState(f);
        pBox.innerHTML = st ? renderStorageInlineBannerHtml(st) : '';
      }
      const cBox = host.querySelector('#stNewDiskCapInspectHost');
      if (cBox) {
        const cst = inspectNewDiskCapState(f);
        cBox.innerHTML = cst
          ? renderStorageInlineBannerHtml({
              severity: cst.severity,
              text: cst.text,
              actionHtml: cst.fixVhdx
                ? '<button class="winui-btn st-pinspect-fix-btn" type="button" id="stCapFixVhdxBtn">改存 .vhdx</button>'
                : ''
            })
          : '';
        cBox.querySelector('#stCapFixVhdxBtn')?.addEventListener('click', () => {
          const curSave = getStorageAddNewDiskPath(f);
          f.newDiskPath = /\.vhd$/i.test(curSave) ? curSave.replace(/\.vhd$/i, '.vhdx') : `${curSave}.vhdx`;
          const saveInp = host.querySelector('#stNewDiskPathInput');
          if (saveInp) saveInp.value = f.newDiskPath;
          syncNewDiskInspectHosts();
          syncConfirmButtonState();
          showNotice?.('已将保存路径改为 .vhdx');
        });
      }
    };

    const syncNewIsoInspectHost = () => {
      const box = host.querySelector('#stNewIsoInspectHost');
      if (!box) return;
      const st = inspectNewIsoOutputState(f);
      box.innerHTML = st ? renderStorageInlineBannerHtml(st) : '';
    };

    const goBackToList = () => {
      state.storageMode = 'list';
      rerender();
    };

    host.querySelectorAll('[data-st-matrix-drive]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const nextDrive = btn.getAttribute('data-st-matrix-drive') || 'HardDisk';
        const curMatrix = deriveMatrixFromState(state, f);
        applyMatrixSelection(state, f, nextDrive, curMatrix.sourceMode);
        rerender();
      });
    });

    host.querySelectorAll('[data-st-matrix-source]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const nextSource = btn.getAttribute('data-st-matrix-source') || 'existing';
        const curMatrix = deriveMatrixFromState(state, f);
        applyMatrixSelection(state, f, curMatrix.driveType, nextSource);
        rerender();
      });
    });

    host.querySelector('#stSwitchToVirtualDvdBtn')?.addEventListener('click', () => {
      applyMatrixSelection(state, f, 'DvdDrive', 'existing');
      rerender();
    });

    host.querySelector('#stExistingPathInput')?.addEventListener('input', (e) => {
      if (effectiveDriveType === 'HardDisk') f.existingFilePath = e.target.value;
      else f.isoPath = e.target.value;
      syncExistingInspectHost();
      syncConfirmButtonState();
    });
    host.querySelector('#stExistingBrowseBtn')?.addEventListener('click', () => {
      if (effectiveDriveType === 'HardDisk') {
        const candidates = [
          'D:\\Hyper-V\\Virtual Hard Disks\\extra_disk.vhdx',
          'D:\\Hyper-V\\Virtual Hard Disks\\ubuntu_data.vhdx'
        ];
        const cur = getStorageAddExistingDiskPath(f);
        f.existingFilePath = candidates.find((c) => c !== cur) || candidates[0];
      } else {
        const isoCandidates = [
          'D:\\ISO\\Win11_24H2_ARM64.iso',
          'D:\\ISO\\ubuntu-24.04.1-live-server-arm64.iso'
        ];
        f.isoPath = isoCandidates.find((c) => c !== f.isoPath) || isoCandidates[0];
      }
      rerender();
    });

    const bindParentInspectControls = () => {
      const inspectHost = host.querySelector('#stParentInspectHost');
      if (!inspectHost) return;
      inspectHost.querySelector('[data-st-parent-fix-ext]')?.addEventListener('click', (e) => {
        const nextExt = e.currentTarget.getAttribute('data-st-parent-fix-ext') || '.vhdx';
        const curSave = getStorageAddNewDiskPath(f);
        f.newDiskPath = /\.(vhdx?|vhd)$/i.test(curSave) ? curSave.replace(/\.(vhdx?|vhd)$/i, nextExt) : `${curSave}${nextExt}`;
        const saveInp = host.querySelector('#stNewDiskPathInput');
        if (saveInp) saveInp.value = f.newDiskPath;
        inspectHost.innerHTML = renderParentInspectHtml(f);
        bindParentInspectControls();
        syncNewDiskInspectHosts();
        syncConfirmButtonState();
        showNotice?.(`已将保存路径后缀对齐为 ${nextExt}`);
      });
    };

    const syncParentInspectHost = () => {
      const inspectHost = host.querySelector('#stParentInspectHost');
      if (!inspectHost) return;
      inspectHost.innerHTML = renderParentInspectHtml(f);
      bindParentInspectControls();
    };

    host.querySelector('#stNewDiskPathInput')?.addEventListener('input', (e) => {
      f.newDiskPath = e.target.value;
      syncNewDiskInspectHosts();
      syncParentInspectHost();
      syncConfirmButtonState();
    });
    host.querySelector('#stNewDiskBrowseBtn')?.addEventListener('click', () => {
      f.newDiskPath = `D:\\Hyper-V\\Virtual Hard Disks\\disk_${items.length + 1}.vhdx`;
      rerender();
    });
    host.querySelectorAll('[data-st-vhdtype]').forEach((btn) => {
      btn.addEventListener('click', () => {
        f.vhdType = btn.getAttribute('data-st-vhdtype') || 'Dynamic';
        rerender();
      });
    });
    host.querySelector('#stParentDiskInput')?.addEventListener('input', (e) => {
      f.parentPath = e.target.value;
      syncParentInspectHost();
      syncConfirmButtonState();
    });
    host.querySelector('#stParentBrowseBtn')?.addEventListener('click', () => {
      const paths = ST_PARENT_DISK_SCENARIOS.map((s) => s.path);
      const curIdx = paths.findIndex((p) => p.toLowerCase() === String(f.parentPath || '').trim().toLowerCase());
      f.parentPath = paths[(curIdx + 1) % paths.length];
      rerender();
    });
    bindParentInspectControls();
    const capInput = host.querySelector('#stNewDiskSizeInput');
    const capSlider = host.querySelector('#stCapacitySlider');
    const capTip = host.querySelector('#stCapacityTooltip');
    const syncCapReadout = (gbNum, syncInputText = true) => {
      const valid = Number.isFinite(gbNum) && gbNum >= 1;
      if (syncInputText && capInput && valid) {
        const text = String(gbNum);
        capInput.value = text;
        capInput.style.width = `${Math.max(2, text.length + 0.5)}ch`;
      }
      const safeNum = valid ? gbNum : 128;
      const equiv = valid ? formatCapacityEquiv(safeNum) : '';
      if (capTip) capTip.textContent = valid ? `${safeNum} GB${equiv}` : '请输入有效容量';
      if (capSlider && valid) {
        const pos = getCapacityStopPos(safeNum);
        capSlider.style.setProperty('--energy-ratio', (pos / Math.max(1, ST_CAPACITY_STOPS.length - 1)).toFixed(4));
        capSlider.setAttribute('aria-valuenow', String(Math.round(pos)));
      }
    };

    bindSteppedEnergySlider(capSlider, {
      count: ST_CAPACITY_STOPS.length,
      onStep: (idx) => {
        const nextGb = ST_CAPACITY_STOPS[idx];
        f.sizeGb = String(nextGb);
        syncCapReadout(nextGb, true);
        syncNewDiskInspectHosts();
        syncConfirmButtonState();
      }
    });

    capInput?.addEventListener('input', (e) => {
      const raw = e.target.value;
      f.sizeGb = raw;
      capInput.style.width = `${Math.max(2, String(raw || '0').length + 0.5)}ch`;
      const parsed = Number.parseInt(String(raw || ''), 10);
      syncCapReadout(parsed, false);
      syncNewDiskInspectHosts();
      syncConfirmButtonState();
    });
    syncNewDiskInspectHosts();

    host.querySelector('#stAdvancedToggleRow')?.addEventListener('click', () => {
      f.showAdvancedDiskOptions = !f.showAdvancedDiskOptions;
      rerender();
    });

    bindSteppedEnergySlider(host.querySelector('#stSectorSlider'), {
      count: ST_SECTOR_STOPS.length,
      onStep: (idx) => {
        const stop = ST_SECTOR_STOPS[idx];
        f.sectorFormat = stop.value;
        const valEl = host.querySelector('#stSectorValText');
        const tipEl = host.querySelector('#stSectorTooltip');
        if (valEl) valEl.textContent = stop.label;
        if (tipEl) tipEl.textContent = stop.tip;
      }
    });

    bindSteppedEnergySlider(host.querySelector('#stBlockSlider'), {
      count: ST_BLOCK_STOPS.length,
      onStep: (idx) => {
        const stop = ST_BLOCK_STOPS[idx];
        f.blockSize = stop.value;
        const numEl = host.querySelector('#stBlockNumText');
        const unitEl = host.querySelector('#stBlockUnitText');
        const tipEl = host.querySelector('#stBlockTooltip');
        if (numEl) numEl.textContent = stop.num;
        if (unitEl) {
          unitEl.textContent = stop.unit;
          unitEl.style.display = stop.unit ? '' : 'none';
        }
        if (tipEl) tipEl.textContent = stop.tip;
      }
    });

    host.querySelector('#stIsoSourceInput')?.addEventListener('input', (e) => {
      f.isoSourceDir = e.target.value;
      const tail = formatStorageFileName(e.target.value, '');
      if (tail) {
        f.isoVolumeLabel = tail;
        const volEl = host.querySelector('#stIsoVolumeInput');
        if (volEl) volEl.value = tail;
        if (syncIsoOutputFromVolume(f)) {
          const outEl = host.querySelector('#stIsoOutputInput');
          if (outEl) outEl.value = f.isoOutputPath;
        }
      }
      syncNewIsoInspectHost();
      syncConfirmButtonState();
    });
    host.querySelector('#stIsoVolumeInput')?.addEventListener('input', (e) => {
      f.isoVolumeLabel = e.target.value;
      if (syncIsoOutputFromVolume(f)) {
        const outEl = host.querySelector('#stIsoOutputInput');
        if (outEl) outEl.value = f.isoOutputPath;
      }
      syncNewIsoInspectHost();
      syncConfirmButtonState();
    });
    host.querySelector('#stIsoOutputInput')?.addEventListener('input', (e) => {
      f.isoOutputCustom = true;
      f.isoOutputPath = e.target.value;
      syncNewIsoInspectHost();
      syncConfirmButtonState();
    });
    host.querySelector('#stIsoFolderBrowseBtn')?.addEventListener('click', () => {
      const folderCandidates = ['C:\\Drivers\\Qualcomm_GPU', 'C:\\Tools\\VirtIO_Drivers'];
      f.isoSourceDir = folderCandidates.find((c) => c !== f.isoSourceDir) || folderCandidates[0];
      f.isoVolumeLabel = formatStorageFileName(f.isoSourceDir, 'NewISO');
      f.isoOutputCustom = false;
      syncIsoOutputFromVolume(f);
      rerender();
    });
    host.querySelector('#stIsoOutputBrowseBtn')?.addEventListener('click', () => {
      f.isoOutputCustom = false;
      syncIsoOutputFromVolume(f);
      rerender();
    });

    host.querySelectorAll('[data-phys-kind]').forEach((btn) => {
      btn.addEventListener('click', () => {
        f.physicalKind = btn.getAttribute('data-phys-kind') || 'disk';
        rerender();
      });
    });
    host.querySelectorAll('[data-phys-disk]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const diskNum = Number(btn.getAttribute('data-phys-disk'));
        const cur = Array.isArray(f.selectedPhysicalDiskNumbers) ? f.selectedPhysicalDiskNumbers : [];
        f.selectedPhysicalDiskNumbers = cur.includes(diskNum) ? cur.filter((x) => x !== diskNum) : [...cur, diskNum];
        rerender();
      });
    });
    host.querySelectorAll('[data-phys-optical]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const optId = btn.getAttribute('data-phys-optical');
        if (!optId) return;
        const cur = Array.isArray(f.selectedPhysicalOpticalIds) ? f.selectedPhysicalOpticalIds : [];
        f.selectedPhysicalOpticalIds = cur.includes(optId) ? cur.filter((x) => x !== optId) : [...cur, optId];
        rerender();
      });
    });

    host.querySelector('#stSlimSlotTrigger')?.addEventListener('click', () => {
      f.showSlimSlotDrawer = !f.showSlimSlotDrawer;
      rerender();
    });
    host.querySelector('#stAutoSlotSwitch')?.addEventListener('click', () => {
      f.autoLocation = !f.autoLocation;
      rerender();
    });
    host.querySelectorAll('[data-st-ctrl-type]').forEach((btn) => {
      btn.addEventListener('click', () => {
        if (btn.disabled) return;
        f.controllerType = btn.getAttribute('data-st-ctrl-type') || 'SCSI';
        rerender();
      });
    });
    host.querySelector('#stCtrlNumberSelect')?.addEventListener('change', (e) => {
      f.controllerNumber = Number(e.target.value);
      rerender();
    });
    host.querySelector('#stCtrlLocationSelect')?.addEventListener('change', (e) => {
      f.location = Number(e.target.value);
      rerender();
    });

    host.querySelector('#stAddBackBtn')?.addEventListener('click', goBackToList);
    document.getElementById('stAddCancelBtn')?.addEventListener('click', goBackToList);

    document.getElementById('stAddConfirmBtn')?.addEventListener('click', () => {
      if (!activeSlot || isAddStorageConfirmDisabled({ intent, effectiveDriveType, generation, running, f, activeSlot })) return;
      const appendAttachment = (attachment) => {
        items.push({
          ...attachment,
          controllerType: activeSlot.controllerType,
          controllerNumber: activeSlot.controllerNumber,
          controllerLocation: activeSlot.location,
          expanded: false
        });
      };

      if (intent === 'existing') {
        const isDisk = effectiveDriveType === 'HardDisk';
        const chosenPath = String((isDisk ? getStorageAddExistingDiskPath(f) : f.isoPath) || '').trim();
        if (isDisk && !chosenPath) {
          showNotice?.('请填写虚拟磁盘文件路径');
          return;
        }
        appendAttachment({
          id: `st-${Date.now()}`,
          driveType: effectiveDriveType,
          diskType: isDisk || chosenPath ? 'Virtual' : 'Empty',
          vhdType: isDisk ? 'Dynamic' : '',
          vhdFormat: isDisk ? '动态扩展' : '',
          displayName: chosenPath ? formatStorageFileName(chosenPath, isDisk ? '虚拟硬盘.vhdx' : '光驱') : '光驱',
          sizeText: isDisk ? '32.0 GB' : chosenPath ? '4.7 GB' : '',
          capacityText: isDisk ? '128 GB' : '',
          path: chosenPath,
          hasIsoMedia: !isDisk && Boolean(chosenPath),
          isPassthroughStale: false
        });
        state.storageMode = 'list';
        renderDashboardSummary?.();
        rerender();
        showNotice?.(isDisk ? '已挂载虚拟硬盘' : chosenPath ? '已挂载 ISO 光盘映像' : '已添加空光驱');
        return;
      }

      if (intent === 'new-disk') {
        const savePath = getStorageAddNewDiskPath(f).trim() || `D:\\Hyper-V\\Virtual Hard Disks\\disk_${items.length + 1}.vhdx`;
        const vhdType = f.vhdType || 'Dynamic';
        const vhdTypeText = vhdType === 'Fixed' ? '固定大小' : vhdType === 'Differencing' ? '差异' : '动态扩展';
        const sizeNum = Math.max(1, parseInt(String(f.sizeGb || '128'), 10) || 128);
        const capText = vhdType === 'Differencing' ? '128 GB' : `${sizeNum}.0 GB`;
        appendAttachment({
          id: `st-${Date.now()}`,
          driveType: 'HardDisk',
          diskType: 'Virtual',
          vhdType,
          vhdFormat: vhdTypeText,
          parentPath: vhdType === 'Differencing' ? String(f.parentPath || '').trim() : '',
          displayName: formatStorageFileName(savePath, 'new_disk.vhdx'),
          sizeText: vhdType === 'Fixed' ? capText : '4.0 MB',
          capacityText: capText,
          path: savePath,
          hasIsoMedia: false,
          isPassthroughStale: false
        });
        state.storageMode = 'list';
        renderDashboardSummary?.();
        rerender();
        showNotice?.(`已创建并挂载 ${vhdTypeText}`);
        return;
      }

      if (intent === 'new-iso') {
        const outPath = String(f.isoOutputPath || '').trim() || `D:\\Hyper-V\\ISO\\${f.isoVolumeLabel || 'NewISO'}.iso`;
        runProgressFlow({
          steps: ['统计来源目录大小', '生成 ISO / UDF 映像', '挂载光驱到虚拟机'],
          progressStepIndex: 1,
          onComplete: () => {
            appendAttachment({
              id: `st-${Date.now()}`,
              driveType: 'DvdDrive',
              diskType: 'Virtual',
              displayName: formatStorageFileName(outPath, 'NewISO.iso'),
              sizeText: '760.0 MB',
              path: outPath,
              hasIsoMedia: true,
              isPassthroughStale: false
            });
            showNotice?.('ISO 打包完成并已挂载');
          }
        });
        return;
      }

      if (intent === 'physical') {
        const allocateNextSlot = (idx) => {
          if (idx === 0) return activeSlot;
          if (!f.autoLocation) {
            const sameCtrlLocs = getAvailableStorageLocations(f.controllerType, f.controllerNumber, items);
            if (sameCtrlLocs.length > 0) {
              return { controllerType: f.controllerType, controllerNumber: Number(f.controllerNumber), location: sameCtrlLocs[0] };
            }
          }
          return planBestStorageSlot(generation, running, effectiveDriveType, items);
        };

        if (f.physicalKind === 'optical') {
          if (generation !== 1) return;
          const { isOpticalAttached, selectedSet } = resolvePhysicalOpticalSelection(HOST_OPTICAL_DRIVES, items, f);
          const pickedOpticals = HOST_OPTICAL_DRIVES.filter(
            (o) => selectedSet.has(o.id) && o.canPassthrough !== false && !isOpticalAttached(o)
          );
          if (!pickedOpticals.length) return;
          let addedCount = 0;
          pickedOpticals.forEach((pickedOpt, idx) => {
            const nextSlot = allocateNextSlot(idx);
            if (!nextSlot) return;
            items.push({
              id: `st-${Date.now()}-${idx}`,
              driveType: 'DvdDrive',
              diskType: 'Physical',
              displayName: pickedOpt.model,
              pnpDeviceId: pickedOpt.pnpDeviceId,
              sizeText: '',
              path: `\\\\.\\CDROM${pickedOpt.number ?? idx}`,
              hasIsoMedia: false,
              isPassthroughStale: false,
              controllerType: nextSlot.controllerType,
              controllerNumber: nextSlot.controllerNumber,
              controllerLocation: nextSlot.location,
              expanded: false
            });
            addedCount += 1;
          });
          if (!addedCount) {
            showNotice?.('当前没有足够的空闲光驱插槽');
            return;
          }
          f.selectedPhysicalOpticalIds = undefined;
          state.storageMode = 'list';
          renderDashboardSummary?.();
          rerender();
          showNotice?.(addedCount > 1 ? `已添加 ${addedCount} 个宿主物理光驱直通` : '已添加宿主物理光驱直通');
          return;
        }

        const { attachedDiskNums, selectedSet } = resolvePhysicalSelection(HOST_PHYSICAL_DISKS, items, f);
        const pickedDisks = HOST_PHYSICAL_DISKS.filter(
          (d) => selectedSet.has(d.number) && d.canPassthrough && !attachedDiskNums.has(d.number)
        );
        if (!pickedDisks.length) return;
        let addedCount = 0;
        const offlinedNums = [];
        pickedDisks.forEach((pickedDisk, idx) => {
          const nextSlot = allocateNextSlot(idx);
          if (!nextSlot) return;
          const wasOnline = String(pickedDisk.status || '').toLowerCase() === 'online';
          if (wasOnline) {
            pickedDisk.status = 'Offline';
            offlinedNums.push(pickedDisk.number);
          }
          items.push({
            id: `st-${Date.now()}-${idx}`,
            driveType: 'HardDisk',
            diskType: 'Physical',
            displayName: pickedDisk.friendlyName,
            sizeText: pickedDisk.sizeText,
            diskNumber: pickedDisk.number,
            serialNumber: pickedDisk.serialNumber,
            path: `\\\\.\\PhysicalDrive${pickedDisk.number}`,
            hasIsoMedia: false,
            isPassthroughStale: false,
            controllerType: nextSlot.controllerType,
            controllerNumber: nextSlot.controllerNumber,
            controllerLocation: nextSlot.location,
            expanded: false
          });
          addedCount += 1;
        });
        if (!addedCount) {
          showNotice?.('当前没有足够的空闲磁盘插槽');
          return;
        }
        f.selectedPhysicalDiskNumbers = undefined;
        state.storageMode = 'list';
        renderDashboardSummary?.();
        rerender();
        const noticeMsg =
          addedCount > 1
            ? offlinedNums.length > 0
              ? `已添加 ${addedCount} 块物理硬盘（磁盘 ${offlinedNums.join('、')} 已自动脱机）`
              : `已添加 ${addedCount} 块宿主物理硬盘直通`
            : offlinedNums.length === 1
              ? `已将磁盘 ${offlinedNums[0]} 自动脱机并添加直通`
              : '已添加宿主物理存储直通';
        showNotice?.(noticeMsg);
      }
    });
    bindHostControls?.(host);
    requestAnimationFrame(() => onLayoutNeeded?.());
    return;
  }

  // =========================================================================
  // View 1: 存储列表主视图（完全对齐显卡 .dev-b-card + 展开 .dt-kv-grid + .dev-add-card-row）
  // =========================================================================
  const cardsHtml =
    items.length === 0
      ? renderStorageAddRowCardHtml(true)
      : items
          .map((item, idx) => {
            const meta = resolveStorageTypeMeta(item, items);
            const expanded = item.expanded === true;
            const removeDisabled = item.controllerType === 'IDE' && running;

            const canEjectIsoFirst = meta.isDvd && !meta.isPhysical && item.hasIsoMedia;
            const actionsHtml = `
              <div class="dev-b-actions" data-stop-prop>
                <button class="round-icon-btn" title="设备设置与磁盘工具" data-st-settings="${idx}" type="button"><span class="fi fi-fluent" style="font-size:16px;">&#xF6AA;</span></button>
                ${
                  canEjectIsoFirst
                    ? `<button class="round-icon-btn" title="弹出 ISO（保留光驱，弹出后可断开）" data-st-eject="${idx}" type="button"><span class="fi fi-fluent" style="font-size:16px;">&#xE0BE;</span></button>`
                    : `<button class="round-icon-btn" title="${removeDisabled ? '关闭虚拟机后可移除 IDE 设备' : meta.isDvd && !meta.isPhysical ? '移除光驱设备' : meta.isPhysical ? '断开物理直通（不影响磁盘数据）' : '移除磁盘（保留文件）'}" data-st-remove="${idx}" ${removeDisabled ? 'disabled' : ''} type="button"><span class="fi fi-fluent" style="font-size:16px;">&#xF60A;</span></button>`
                }
                <span class="dev-b-chevron"><span class="fi">${expanded ? '&#xE70E;' : '&#xE70D;'}</span></span>
              </div>
            `;

            const detailGridHtml = expanded ? renderStorageDetailGridHtml(meta, renderTitleFirstKvItemHtml) : '';

            const staleBannerHtml = item.isPassthroughStale
              ? `<div style="margin-top:10px;">${renderStorageInlineBannerHtml({
                  severity: 'warning',
                  text: '物理磁盘直通引用已失效：请核对当前设备后重新挂载'
                })}</div>`
              : '';

            return `
              <div class="dev-b-card ${expanded ? 'is-expanded' : ''}">
                <div class="dev-b-head" data-st-toggle="${idx}">
                  ${renderStorageIconBadgeHtml(meta, 38)}
                  <div class="dev-b-identity">
                    <div class="dev-b-title">${escapeDeviceHtml(item.displayName)}</div>
                    <div class="dev-b-sub ${meta.hasCaution ? 'is-caution' : ''}">${meta.subParts.map((part) => `<span class="dev-b-sub-part">${escapeDeviceHtml(part)}</span>`).join('')}</div>
                  </div>
                  ${actionsHtml}
                </div>
                ${
                  expanded
                    ? `<div class="dev-b-body">${detailGridHtml}${staleBannerHtml}</div>`
                    : ''
                }
              </div>
            `;
          })
          .join('');

  host.innerHTML = `
    <div class="dev-stack">
      <div style="display:flex;flex-direction:column;gap:10px;">
        ${cardsHtml}
        ${items.length > 0 ? renderStorageAddRowCardHtml(false) : ''}
      </div>
    </div>
  `;

  host.querySelectorAll('[data-stop-prop]').forEach((el) => {
    el.addEventListener('click', (e) => e.stopPropagation());
  });
  host.querySelectorAll('[data-st-toggle]').forEach((hdr) => {
    hdr.addEventListener('click', () => {
      const idx = Number(hdr.getAttribute('data-st-toggle'));
      if (items[idx]) {
        items[idx].expanded = !(items[idx].expanded === true);
        rerender();
      }
    });
  });
  host.querySelectorAll('[data-st-settings]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const idx = Number(btn.getAttribute('data-st-settings'));
      const target = items[idx];
      if (!target) return;
      state.storageSettingsTargetId = target.id;
      state.storageSettingsDraft = null;
      ensureStorageSettingsDraft(state, vm, target);
      state.storageMode = 'settings';
      rerender();
    });
  });
  host.querySelectorAll('[data-st-eject]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const idx = Number(btn.getAttribute('data-st-eject'));
      const target = items[idx];
      if (!target) return;
      target.path = '';
      target.diskType = 'Empty';
      target.hasIsoMedia = false;
      target.displayName = '光驱';
      target.sizeText = '';
      renderDashboardSummary?.();
      rerender();
      showNotice('已弹出介质，光驱保留');
    });
  });
  host.querySelectorAll('[data-st-remove]').forEach((btn) => {
    btn.addEventListener('click', () => {
      if (btn.disabled) return;
      const idx = Number(btn.getAttribute('data-st-remove'));
      items.splice(idx, 1);
      renderDashboardSummary?.();
      rerender();
      showNotice('已移除磁盘（文件已保留）');
    });
  });
  host.querySelector('#stOpenAddBtn')?.addEventListener('click', () => {
    state.storageMode = 'add';
    rerender();
  });

  requestAnimationFrame(() => onLayoutNeeded?.());
}

/* ============================================================================
 * Network Adapter Workbench (Unified Guided-Add + Read-Only List + 1-Screen Chip Policy)
 * ============================================================================ */

const AVAILABLE_VSWITCHES = [
  {
    id: 'sw-default',
    name: 'Default Switch',
    mode: 'NAT',
    subnet: '172.24.112.0/20',
    desc: '自动共享宿主机外网连接，开箱即用（95% 场景推荐）'
  },
  {
    id: 'sw-ext',
    name: 'External vSwitch',
    mode: '外部桥接',
    subnet: 'Intel I226-V 2.5GbE',
    desc: '直接桥接物理网卡，与局域网设备处于同一物理网段'
  },
  {
    id: 'sw-iso',
    name: 'Lab Isolated',
    mode: '内部隔离',
    subnet: '10.10.0.0/24',
    desc: '仅宿主机与同交换机虚拟机内部互通，与外网物理隔离'
  }
];

function formatNicMac(raw = '') {
  const clean = String(raw).replace(/[^0-9a-fA-F]/g, '').toUpperCase();
  if (clean.length === 12) {
    return clean.match(/.{1,2}/g).join(':');
  }
  return raw || '00:15:5D:01:2A:0B';
}

function buildDefaultNicTemplates(scenario = 'single') {
  if (scenario === 'empty') return [];
  const primary = {
    id: 'nic-1',
    name: '网络适配器',
    isConnected: true,
    switchName: 'Default Switch',
    macAddress: '00:15:5D:01:2A:0B',
    isStaticMac: false,
    ipv4: ['172.24.118.94'],
    ipv6: ['fe80::215:5dff:fe01:2a0b'],
    vlanMode: 'Off', // 'Off' | 'Access' | 'Trunk' | 'Private'
    accessVlanId: 20,
    nativeVlanId: 1,
    trunkAllowedVlans: '10, 20, 30-40',
    pvlanMode: 'Isolated', // 'Isolated' | 'Community' | 'Promiscuous'
    pvlanPrimaryId: 100,
    pvlanSecondaryId: 101,
    bandwidthLimitMbps: 0,
    stormLimit: 0,
    macSpoofing: false,
    dhcpGuard: false,
    routerGuard: false,
    teamingAllowed: false,
    monitorMode: 'None', // 'None' | 'Source' | 'Destination'
    vmqEnabled: true,
    sriovEnabled: false,
    ipsecOffloadEnabled: true,
    expanded: true,
    activeSubTab: 'connection', // 'connection' | 'vlan' | 'bandwidth' | 'security'
    advDrawerOpen: false
  };
  if (scenario !== 'multi') return [primary];
  return [
    { ...primary, expanded: true },
    {
      ...primary,
      id: 'nic-2',
      name: '网络适配器 2',
      isConnected: true,
      switchName: 'External vSwitch',
      macAddress: '00:15:5D:01:2A:0C',
      isStaticMac: true,
      ipv4: ['192.168.1.128'],
      ipv6: ['240e:3b0:1280:1::128', 'fe80::215:5dff:fe01:2a0c'],
      vlanMode: 'Access',
      accessVlanId: 20,
      bandwidthLimitMbps: 1000,
      sriovEnabled: true,
      macSpoofing: true,
      expanded: false
    },
    {
      ...primary,
      id: 'nic-3',
      name: '网络适配器 3',
      isConnected: false,
      switchName: 'Lab Isolated',
      macAddress: '00:15:5D:01:2A:0D',
      isStaticMac: false,
      ipv4: [],
      ipv6: [],
      vlanMode: 'Off',
      expanded: false
    }
  ];
}

export function resetNetworkAdaptersForScenario(state, vm, scenario = 'single') {
  if (!state.networkAdaptersByVm) state.networkAdaptersByVm = {};
  const list = buildDefaultNicTemplates(scenario);
  state.networkAdaptersByVm[vm.id] = list;
  state.nicEditTargetId = list[0]?.id || null;
  state.nicPolicyDraft = null;
  state.nicPolicyDraftVmId = null;
  if (scenario === 'empty' && state.nicMode === 'policy') {
    state.nicMode = 'list';
  }
  syncVmAdaptersFromNetworkState(state, vm);
}

function syncVmAdaptersFromNetworkState(state, vm) {
  const list = state.networkAdaptersByVm?.[vm.id] || [];
  const prevList = Array.isArray(vm.adapters) ? vm.adapters : [];
  vm.adapters = list.map((nic, i) => {
    const prev = prevList.find((a) => a.key === (nic.id || nic.key)) || prevList[i] || {};
    const connected = Boolean(nic.isConnected);
    const macVal = String(nic.macAddress ?? nic.mac ?? prev.mac ?? '').replace(/[^0-9A-Fa-f]/g, '');
    return {
      ...prev,
      key: nic.id || nic.key || `net-${i}`,
      title: nic.name || nic.title || prev.title || '网络适配器',
      isConnected: connected,
      subtitle: connected ? (nic.switchName || nic.subtitle || '虚拟交换机') : '未连接',
      mac: macVal || prev.mac || ''
    };
  });
}

let activeSwitchesCatalog = AVAILABLE_VSWITCHES;

function getActiveHostSwitches(state) {
  if (activeSwitchesCatalog !== AVAILABLE_VSWITCHES) {
    return activeSwitchesCatalog;
  }
  const mode = state.nicHostSwitches || 'one';
  if (mode === 'none') return [];
  if (mode === 'one') return [AVAILABLE_VSWITCHES[0]];
  return AVAILABLE_VSWITCHES;
}

function getVmGeneration(_state, vm) {
  return Number(vm?.generation) === 1 ? 1 : 2;
}

function ensureNetworkState(state, vm) {
  if (!state.networkAdaptersByVm) state.networkAdaptersByVm = {};
  if (!state.networkAdaptersByVm[vm.id]) {
    state.networkAdaptersByVm[vm.id] = buildDefaultNicTemplates(state.nicScenario || 'single');
  }
  if (!state.nicVariant) state.nicVariant = 'unified';
  if (!state.nicScenario) state.nicScenario = 'single';
  if (!state.nicMode) state.nicMode = 'list';
  state.nicPolicyTab = normalizeNicPolicyTab(state.nicPolicyTab);
  if (!state.nicHostSwitches) state.nicHostSwitches = 'one';
  if (!state.nicAddForm) {
    state.nicAddForm = {
      isLegacy: false,
      targetSwitch: 'Default Switch', // or '__disconnected__'
      isStaticMac: false,
      staticMac: '00:15:5D:01:2A:0E'
    };
  }
  const activeSwitches = getActiveHostSwitches(state);
  if (activeSwitches.length === 0) {
    state.nicAddForm.targetSwitch = '__disconnected__';
  } else if (
    state.nicAddForm.targetSwitch !== '__disconnected__' &&
    !activeSwitches.some((s) => s.name === state.nicAddForm.targetSwitch)
  ) {
    state.nicAddForm.targetSwitch = activeSwitches[0].name;
  }
  if (getVmGeneration(state, vm) === 2 || vm?.running) {
    state.nicAddForm.isLegacy = false;
  }
  return state.networkAdaptersByVm[vm.id];
}

function getNicFeatureBadges(nic) {
  const badges = [];
  if (nic.isLegacy) {
    badges.push('旧版');
  }
  if (nic.vlanMode === 'Access' && Number(nic.accessVlanId) > 0) {
    badges.push(`VLAN ${nic.accessVlanId}`);
  } else if (nic.vlanMode === 'Trunk') {
    badges.push(`Trunk (${nic.nativeVlanId || 1})`);
  } else if (nic.vlanMode === 'Private') {
    badges.push(`PVLAN ${nic.pvlanPrimaryId || 100}`);
  }
  if (Number(nic.bandwidthLimitMbps) > 0) {
    const lim = Number(nic.bandwidthLimitMbps);
    badges.push(lim >= 1000 && lim % 1000 === 0 ? `限速 ${lim / 1000} Gbps` : `限速 ${lim} Mbps`);
  }
  if (nic.sriovEnabled) {
    badges.push('SR-IOV');
  }
  if (nic.macSpoofing) {
    badges.push('允许 MAC 欺骗');
  }
  if (nic.monitorMode && nic.monitorMode !== 'None') {
    badges.push(nic.monitorMode === 'Source' ? '镜像: 源' : '镜像: 目标');
  }
  return badges;
}

function getNicSummarySubtitleParts(nic) {
  if (!nic.isConnected) {
    return ['未连接'];
  }
  const swObj = activeSwitchesCatalog.find((s) => s.name === nic.switchName);
  const swText = `${nic.switchName || '未连接'}${swObj ? ` (${swObj.mode})` : ''}`;
  return [swText];
}

function renderNicAddRowCardHtml(isEmpty = false) {
  return `
    <button class="dev-add-card-row ${isEmpty ? 'is-empty' : ''}" id="nicAddAdapterBtn" type="button">
      <div class="dev-add-card-left">
        <div class="dev-add-card-badge"><span class="fi">&#xE710;</span></div>
        <div>
          <div class="dev-add-card-title">添加网卡</div>
        </div>
      </div>
    </button>
  `;
}

function renderNicIpKvGridHtml(nic, running, renderTitleFirstKvItemHtml) {
  const ipv4List = running && nic.isConnected ? nic.ipv4 || [] : [];
  const ipv6List = running && nic.isConnected ? nic.ipv6 || [] : [];
  const ipv4Text = ipv4List.length > 0 ? ipv4List.join('，') : '未获取';
  const ipv6Text = ipv6List.length > 0 ? ipv6List[0] : '未获取';

  return `
    <div class="dt-kv-grid is-title-first is-2col" style="margin-top:2px;">
      ${renderTitleFirstKvItemHtml({
        title: 'IPv4 地址',
        stateText: escapeDeviceHtml(ipv4Text),
        copyVal: ipv4List[0] ? escapeDeviceHtml(ipv4List[0]) : ''
      })}
      ${renderTitleFirstKvItemHtml({
        title: 'IPv6 地址',
        stateText: escapeDeviceHtml(ipv6Text),
        copyVal: ipv6List[0] ? escapeDeviceHtml(ipv6List[0]) : ''
      })}
    </div>
  `;
}

function renderNicReadOnlyKvBodyHtml(nic, running, renderTitleFirstKvItemHtml) {
  const typeText = nic.isLegacy ? '旧版网卡' : '标准网卡';
  const macFormatted = formatNicMac(nic.macAddress);

  const ipv4List = running && nic.isConnected ? nic.ipv4 || [] : [];
  const ipv6List = running && nic.isConnected ? nic.ipv6 || [] : [];
  const ipv4Text = ipv4List.length > 0 ? ipv4List.join('，') : '未获取';
  const ipv6Text = ipv6List.length > 0 ? ipv6List[0] : '未获取';

  const policyParts = [];
  if (nic.vlanMode === 'Access') policyParts.push(`VLAN ${nic.accessVlanId || 20}`);
  else if (nic.vlanMode === 'Trunk') policyParts.push(`Trunk (${nic.nativeVlanId || 1})`);
  else if (nic.vlanMode === 'Private') policyParts.push(`PVLAN ${nic.pvlanPrimaryId || 100}`);

  if (Number(nic.bandwidthLimitMbps) > 0) {
    const lim = Number(nic.bandwidthLimitMbps);
    policyParts.push(lim >= 1000 && lim % 1000 === 0 ? `限速 ${lim / 1000}G` : `限速 ${lim}M`);
  }
  if (nic.sriovEnabled) policyParts.push('SR-IOV');
  if (nic.macSpoofing) policyParts.push('允许 MAC 欺骗');
  if (nic.dhcpGuard) policyParts.push('DHCP 防护');
  if (nic.routerGuard) policyParts.push('路由器防护');
  if (nic.teamingAllowed) policyParts.push('网卡组队');
  if (nic.monitorMode === 'Source') policyParts.push('镜像源');
  else if (nic.monitorMode === 'Destination') policyParts.push('镜像目标');

  const policyTileHtml =
    policyParts.length > 0
      ? renderTitleFirstKvItemHtml({
          title: '端口策略',
          stateText: escapeDeviceHtml(policyParts.join('，')),
          wide: true
        })
      : '';

  return `
    <div class="dt-kv-grid is-title-first is-2col">
      ${renderTitleFirstKvItemHtml({
        title: '网卡类型',
        stateText: escapeDeviceHtml(typeText)
      })}
      ${renderTitleFirstKvItemHtml({
        title: 'MAC 地址',
        stateText: escapeDeviceHtml(macFormatted),
        copyVal: escapeDeviceHtml(macFormatted)
      })}
      ${renderTitleFirstKvItemHtml({
        title: 'IPv4 地址',
        stateText: escapeDeviceHtml(ipv4Text),
        copyVal: ipv4List[0] ? escapeDeviceHtml(ipv4List[0]) : ''
      })}
      ${renderTitleFirstKvItemHtml({
        title: 'IPv6 地址',
        stateText: escapeDeviceHtml(ipv6Text),
        copyVal: ipv6List[0] ? escapeDeviceHtml(ipv6List[0]) : ''
      })}
      ${policyTileHtml}
    </div>
  `;
}

/* ============================================================================
 * Flat-Expanded Port Policy Editor (Aligned with WinUI SettingsFieldPanel)
 * ============================================================================ */
function renderNicSwitchCardHtml({ title, desc, idx, key, checked, disabled = false, extraCls = '', renderSettingLabelHtml }) {
  const isOn = !disabled && Boolean(checked);
  return `
    <div class="isa-card uniform-1row ${extraCls} ${disabled ? 'is-disabled' : ''}">
      <div class="isa-card-row">
        ${renderSettingLabelHtml(title, desc)}
        <div
          class="winui-switch ${isOn ? 'on' : ''} ${disabled ? 'disabled is-locked' : ''}"
          data-custom-switch="true"
          data-nic-toggle="${idx}:${key}"
          role="switch"
          aria-checked="${isOn}"
          aria-disabled="${disabled}"
        >
          <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
        </div>
      </div>
    </div>
  `;
}

function renderNicVlanSliderCardHtml({ title, desc, idx, field, value, defaultVal = 1, span2 = false, renderSettingLabelHtml }) {
  const min = 1;
  const max = 4094;
  const numVal = Math.max(min, Math.min(max, parseInt(value ?? defaultVal, 10) || defaultVal));
  const ratio = ((numVal - min) / (max - min)).toFixed(4);
  const inputWidth = `${Math.max(1.8, String(numVal).length + 0.5)}ch`;

  return `
    <div class="energy-card compact-head-mode ${span2 ? 'span-2' : ''}" data-nic-slider-card="${idx}:${field}">
      <div class="energy-card-head">
        ${renderSettingLabelHtml(title, desc)}
        <div class="energy-val-group">
          <div class="energy-val-inline">
            <input
              type="text"
              class="energy-num-input compact-num"
              data-nic-slider-input="${idx}:${field}"
              value="${numVal}"
              style="width:${inputWidth};"
            />
          </div>
        </div>
      </div>
      <div
        class="winui-energy-slider mode-fader thumb-slim-bar"
        data-nic-slider="${idx}:${field}"
        data-min="${min}"
        data-max="${max}"
        style="--energy-ratio:${ratio};"
        role="slider"
        aria-valuemin="${min}"
        aria-valuemax="${max}"
        aria-valuenow="${numVal}"
        tabindex="0"
      >
        <div class="winui-energy-tooltip" data-nic-slider-tooltip="${idx}:${field}">VLAN ${numVal}</div>
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
    </div>
  `;
}

function renderNicVlanGridHtml(nic, idx, renderSettingLabelHtml) {
  const vlanMode = nic.vlanMode || 'Off';
  let vlanSubHtml = '';

  if (vlanMode === 'Access') {
    vlanSubHtml = renderNicVlanSliderCardHtml({
      title: '接入 VLAN ID',
      desc: '该端口仅收发此单一 VLAN 标签流量，有效范围 1 – 4094',
      idx,
      field: 'accessVlanId',
      value: nic.accessVlanId,
      defaultVal: 20,
      span2: true,
      renderSettingLabelHtml
    });
  } else if (vlanMode === 'Trunk') {
    vlanSubHtml = `
      ${renderNicVlanSliderCardHtml({
        title: '本征 VLAN',
        desc: '未标记帧所属的默认 VLAN，有效范围 1 – 4094',
        idx,
        field: 'nativeVlanId',
        value: nic.nativeVlanId,
        defaultVal: 1,
        span2: true,
        renderSettingLabelHtml
      })}
      <div class="isa-card uniform-1row span-2">
        <div class="isa-card-row">
          ${renderSettingLabelHtml('允许的 VLAN 列表', '支持逗号与区间，例如 10, 20, 30-40')}
          <input type="text" class="winui-textbox" data-nic-field="${idx}:trunkAllowedVlans" style="width:240px;" value="${escapeDeviceHtml(nic.trunkAllowedVlans || '10, 20, 30-40')}" placeholder="10, 20-30" />
        </div>
      </div>
    `;
  } else if (vlanMode === 'Private') {
    const pvRole = nic.pvlanMode || 'Isolated';
    const hideSecondary = pvRole === 'Promiscuous';
    vlanSubHtml = `
      <div class="isa-card uniform-1row span-2">
        <div class="isa-card-row">
          ${renderSettingLabelHtml('专用 VLAN 端口角色', '隔离端口仅可与混杂端口通信，团体端口可在同团体内互访')}
          <div class="isa-pill-track cols-3" style="width:240px;flex-shrink:0;">
            <button type="button" class="isa-pill-btn ${pvRole === 'Isolated' ? 'active' : ''}" data-nic-pvlan-role="${idx}:Isolated">隔离</button>
            <button type="button" class="isa-pill-btn ${pvRole === 'Community' ? 'active' : ''}" data-nic-pvlan-role="${idx}:Community">团体</button>
            <button type="button" class="isa-pill-btn ${pvRole === 'Promiscuous' ? 'active' : ''}" data-nic-pvlan-role="${idx}:Promiscuous">混杂</button>
          </div>
        </div>
      </div>
      ${renderNicVlanSliderCardHtml({
        title: '主 VLAN ID',
        desc: '指定主 VLAN 编号，有效范围 1 – 4094',
        idx,
        field: 'pvlanPrimaryId',
        value: nic.pvlanPrimaryId,
        defaultVal: 100,
        span2: hideSecondary,
        renderSettingLabelHtml
      })}
      ${
        hideSecondary
          ? ''
          : renderNicVlanSliderCardHtml({
              title: '辅助 VLAN ID',
              desc: '指定次级隔离或团体 VLAN 编号，有效范围 1 – 4094',
              idx,
              field: 'pvlanSecondaryId',
              value: nic.pvlanSecondaryId,
              defaultVal: 101,
              span2: false,
              renderSettingLabelHtml
            })
      }
    `;
  }

  return `
    <div class="isa-grid-2x2">
      <div class="isa-card uniform-1row span-2">
        <div class="isa-card-row">
          ${renderSettingLabelHtml('VLAN 模式', '划分虚拟局域网隔离广播域，支持 Access、Trunk 与 PVLAN 模式')}
          <div class="isa-pill-track cols-4" style="width:320px;flex-shrink:0;">
            <button type="button" class="isa-pill-btn ${vlanMode === 'Off' ? 'active' : ''}" data-nic-vlan-mode="${idx}:Off">关闭</button>
            <button type="button" class="isa-pill-btn ${vlanMode === 'Access' ? 'active' : ''}" data-nic-vlan-mode="${idx}:Access">Access</button>
            <button type="button" class="isa-pill-btn ${vlanMode === 'Trunk' ? 'active' : ''}" data-nic-vlan-mode="${idx}:Trunk">Trunk</button>
            <button type="button" class="isa-pill-btn ${vlanMode === 'Private' ? 'active' : ''}" data-nic-vlan-mode="${idx}:Private">PVLAN</button>
          </div>
        </div>
      </div>
      ${vlanSubHtml}
    </div>
  `;
}

const NIC_STEPPED_SLIDER_SPECS = {
  bandwidthLimitMbps: {
    stops: [0, 1, 5, 10, 20, 50, 100, 200, 300, 500, 1000, 2500, 5000, 10000],
    unit: 'Mbps',
    zeroLabel: '不限速',
    title: '出站带宽上限',
    desc: '限制此网卡向外部发送数据的最大速率，0 表示不限速'
  },
  stormLimit: {
    stops: [0, 50, 100, 200, 500, 1000, 2000, 5000, 10000],
    unit: '包/秒',
    zeroLabel: '不限制',
    title: '风暴限制',
    desc: '限制每秒广播、组播与未知单播风暴流量上限，0 表示不限制'
  }
};

function formatNicSteppedSliderDisplay(field, rawVal) {
  const spec = NIC_STEPPED_SLIDER_SPECS[field] || NIC_STEPPED_SLIDER_SPECS.bandwidthLimitMbps;
  const val = Math.max(0, parseInt(rawVal ?? 0, 10) || 0);
  const tooltipText = val === 0 ? spec.zeroLabel : `${val} ${spec.unit}`;
  return { val, tooltipText, spec };
}

function renderNicSteppedSliderCardHtml(nic, idx, field, renderSettingLabelHtml) {
  const { val, tooltipText, spec } = formatNicSteppedSliderDisplay(field, nic[field]);
  const stopPos = getSteppedStopPos(spec.stops, val);
  const ratio = stopPos / Math.max(1, spec.stops.length - 1);
  const inputWidth = `${Math.max(1.8, String(val).length + 0.5)}ch`;

  return `
    <div class="energy-card compact-head-mode span-2" data-nic-stepped-card="${idx}:${field}">
      <div class="energy-card-head">
        ${renderSettingLabelHtml(spec.title, spec.desc)}
        <div class="energy-val-group">
          <div class="energy-val-inline">
            <input
              type="text"
              class="energy-num-input compact-num"
              data-nic-stepped-input="${idx}:${field}"
              value="${val}"
              style="width:${inputWidth};"
            />
            <span class="energy-num-unit">${spec.unit}</span>
          </div>
        </div>
      </div>
      ${renderEnergySliderTrackHtml({
        id: `nicSteppedSlider-${field}-${idx}`,
        max: spec.stops.length - 1,
        value: Math.round(stopPos),
        ratio,
        tooltipText,
        tooltipId: `nicSteppedTooltip-${field}-${idx}`
      })}
    </div>
  `;
}

function renderNicBandwidthCardHtml(nic, idx, renderSettingLabelHtml) {
  return renderNicSteppedSliderCardHtml(nic, idx, 'bandwidthLimitMbps', renderSettingLabelHtml);
}

function renderNicBandwidthGridHtml(nic, idx, renderSettingLabelHtml) {
  return `<div class="isa-grid-2x2">${renderNicBandwidthCardHtml(nic, idx, renderSettingLabelHtml)}</div>`;
}

function renderNicSecurityGridHtml(nic, idx, renderSettingLabelHtml) {
  const mon = nic.monitorMode || 'None';
  const toggles = [
    { key: 'macSpoofing', title: '允许 MAC 欺骗', desc: '允许虚拟机修改发出帧的源 MAC 地址，软路由、嵌套虚拟化或负载均衡场景需开启' },
    { key: 'teamingAllowed', title: '允许网卡组队', desc: '仅开启组队权限，需进入虚拟机系统内将此网卡与同一台虚拟机的其他网卡绑定聚合，不可在此选择成员' },
    { key: 'dhcpGuard', title: 'DHCP 防护', desc: '拦截来自该虚拟机的非法 DHCP 响应报文，防止局域网地址欺骗' },
    { key: 'routerGuard', title: '路由器防护', desc: '拦截来自该虚拟机的非法 IPv6 路由器通告与重定向报文' }
  ];

  return `
    <div class="isa-grid-2x2">
      ${toggles
        .map((t) =>
          renderNicSwitchCardHtml({
            title: t.title,
            desc: t.desc,
            idx,
            key: t.key,
            checked: nic[t.key],
            renderSettingLabelHtml
          })
        )
        .join('')}
      <div class="isa-card uniform-1row span-2">
        <div class="isa-card-row">
          ${renderSettingLabelHtml('端口镜像', '将此网卡设为端口流量抓包监控的源端口或接收镜像的目标端口')}
          <div class="isa-pill-track cols-3" style="width:240px;flex-shrink:0;">
            <button type="button" class="isa-pill-btn ${mon === 'None' ? 'active' : ''}" data-nic-mirror-mode="${idx}:None">禁用</button>
            <button type="button" class="isa-pill-btn ${mon === 'Source' ? 'active' : ''}" data-nic-mirror-mode="${idx}:Source">源端口</button>
            <button type="button" class="isa-pill-btn ${mon === 'Destination' ? 'active' : ''}" data-nic-mirror-mode="${idx}:Destination">目标端口</button>
          </div>
        </div>
      </div>
      ${renderNicSteppedSliderCardHtml(nic, idx, 'stormLimit', renderSettingLabelHtml)}
      ${renderNicSwitchCardHtml({
        title: 'SR-IOV 硬件直通',
        desc: nic.isLegacy
          ? '旧版模拟网卡不支持 SR-IOV 硬件直通'
          : '为虚拟机直通物理网卡硬件通道，需物理网卡与虚拟交换机同时支持，启用后将绕过软交换机策略、镜像与限速',
        idx,
        key: 'sriovEnabled',
        checked: nic.sriovEnabled,
        disabled: Boolean(nic.isLegacy),
        extraCls: 'span-2',
        renderSettingLabelHtml
      })}
    </div>
  `;
}

function validateTrunkVlanList(raw) {
  const text = String(raw || '').trim();
  if (!text) return 'Trunk 模式下须填写允许的 VLAN，示例 10, 20, 30-40';
  const parts = text.split(',').map((s) => s.trim()).filter(Boolean);
  if (parts.length === 0) return 'Trunk 允许的 VLAN 列表不能为空';
  for (const part of parts) {
    const bounds = part.split('-').map((s) => s.trim());
    if (bounds.length > 2) return `VLAN 格式无效：${part}，示例 10, 30-40`;
    const first = Number(bounds[0]);
    if (!Number.isInteger(first) || first < 1 || first > 4094) {
      return `VLAN 编号「${bounds[0]}」须在 1 – 4094 之间`;
    }
    if (bounds.length === 2) {
      const last = Number(bounds[1]);
      if (!Number.isInteger(last) || last < first || last > 4094) {
        return `VLAN 范围「${part}」无效，须在 1 – 4094 内且终止不小于起始`;
      }
    }
  }
  return null;
}

function buildDeviceInlineBannerHtml({ severity = 'info', title, desc = '', actionHtml = '' }) {
  const sevCls = severity === 'error' ? '' : `is-${severity}`;
  const badgeGlyph = ST_PARENT_BADGE_GLYPHS[severity] || ST_PARENT_BADGE_GLYPHS.info;
  return `
    <div class="winui-floating-banner is-inline ${sevCls}" role="status">
      <span class="winui-floating-banner-badge">${badgeGlyph}</span>
      <div class="nic-banner-line">
        <span class="nic-banner-title">${escapeDeviceHtml(title)}</span>
        ${desc ? `<span class="nic-banner-detail">${escapeDeviceHtml(desc)}</span>` : ''}
      </div>
      ${actionHtml}
    </div>
  `;
}

function buildNicInfobarHtml({ severity = 'info', title, desc = '' }) {
  return buildDeviceInlineBannerHtml({ severity, title, desc });
}

function buildNicVlanContextInfobarHtml(nic) {
  const mode = nic.vlanMode || 'Off';
  if (mode === 'Off') return '';

  if (mode === 'Trunk') {
    const syntaxErr = validateTrunkVlanList(nic.trunkAllowedVlans);
    if (syntaxErr) {
      return buildNicInfobarHtml({
        severity: 'error',
        title: syntaxErr
      });
    }
  }

  const pvRole = nic.pvlanMode || 'Isolated';
  if (!nic.isConnected) {
    return buildNicInfobarHtml({
      severity: 'info',
      title: '未连接虚拟交换机，策略将在接入后生效'
    });
  }

  const swObj = activeSwitchesCatalog.find((s) => s.name === nic.switchName);
  const swMode = swObj?.mode || 'NAT';

  if (swMode === 'NAT') {
    return buildNicInfobarHtml({
      severity: 'warning',
      title: 'NAT 交换机开启 VLAN 后无法访问外网'
    });
  }

  if (swMode === '外部桥接') {
    return buildNicInfobarHtml({
      severity: 'info',
      title: '需物理网卡与上游交换机放行对应 VLAN'
    });
  }

  if (mode === 'Trunk') {
    return buildNicInfobarHtml({
      severity: 'info',
      title: '须在虚拟机系统内配置对应 VLAN 子接口'
    });
  }

  if (mode === 'Private' && pvRole !== 'Promiscuous') {
    return buildNicInfobarHtml({
      severity: 'info',
      title: '隔离与团体端口需同主 VLAN 混杂端口才可通网关'
    });
  }

  return '';
}

function normalizeNicPolicyTab(tab) {
  if (tab === 'vlan') return 'vlan';
  if (tab === 'security' || tab === 'offload' || tab === 'more' || tab === 'advanced') return 'security';
  return 'connection';
}

function toNicTabIconEntity(raw, fallbackHex) {
  const hex = String(raw || '').replace(/[^0-9A-Fa-f]/g, '').toUpperCase();
  return `&#x${/^[0-9A-F]{4,5}$/.test(hex) ? hex : fallbackHex};`;
}

function renderNicOneScreenConvergedEditorHtml(
  nic,
  idx,
  running,
  renderSettingLabelHtml,
  activePolicyTab = 'connection',
  trailingActionsHtml = '',
  policyIconOverrides = null
) {
  const macLocked = running;
  const tab = normalizeNicPolicyTab(activePolicyTab);
  const switchOptionsHtml = [
    `<option value="__disconnected__" ${!nic.isConnected ? 'selected' : ''}>未连接</option>`,
    ...activeSwitchesCatalog.map(
      (sw) =>
        `<option value="${escapeDeviceHtml(sw.name)}" ${nic.isConnected && nic.switchName === sw.name ? 'selected' : ''}>${escapeDeviceHtml(sw.name)}</option>`
    )
  ].join('');

  const advIcon = toNicTabIconEntity(policyIconOverrides?.security, 'E71C');
  const advLabel = String(policyIconOverrides?.securityLabel || '').trim() || '策略';

  const policyTabs = [
    { key: 'connection', icon: '&#xE71B;', label: '连接' },
    { key: 'vlan', icon: '&#xE8EC;', label: 'VLAN' },
    { key: 'security', icon: advIcon, label: advLabel }
  ];

  const tabsHtml = renderPivotTrackHtml(
    policyTabs
      .map(
        (t) => `
          <button class="st-pivot-btn ${tab === t.key ? 'active' : ''}" data-nic-policy-tab="${t.key}" type="button" role="tab" aria-selected="${tab === t.key}">
            <span class="fi" style="font-size:14px;">${t.icon}</span>
            <span>${t.label}</span>
          </button>
        `
      )
      .join(''),
    trailingActionsHtml
  );

  let bodyHtml = '';
  if (tab === 'connection') {
    bodyHtml = `
      <div class="nic-policy-group">
        <div class="isa-grid-2x2">
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('虚拟交换机', '切换网卡接入的虚拟交换机，或设为“未连接”拔出虚拟网线')}
              <div style="display:flex;align-items:center;gap:8px;flex-shrink:0;">
                <select class="winui-select" data-nic-switch-select="${idx}" style="width:240px;">
                  ${switchOptionsHtml}
                </select>
              </div>
            </div>
          </div>

          <div class="isa-card uniform-1row span-2 ${macLocked ? 'is-disabled' : ''}">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('静态 MAC 地址', '关闭时由地址池自动分配，开启后可手动指定 12 位十六进制 MAC 地址，仅关机状态下可修改')}
              <div style="display:flex;align-items:center;gap:10px;flex-shrink:0;">
                ${
                  nic.isStaticMac
                    ? `<input type="text" class="winui-textbox" data-nic-mac-input="${idx}" style="width:156px;height:30px;font-family:Consolas,monospace;font-size:12.5px;" value="${escapeDeviceHtml(formatNicMac(nic.macAddress))}" ${macLocked ? 'disabled' : ''} />`
                    : `<span class="nic-mac-preview">${escapeDeviceHtml(formatNicMac(nic.macAddress))}</span>`
                }
                <button class="dt-kv-head-copy" type="button" data-copy="${escapeDeviceHtml(formatNicMac(nic.macAddress))}" title="复制 MAC 地址"><span class="fi" style="font-size:11.5px;">&#xE8C8;</span></button>
                <div
                  class="winui-switch ${nic.isStaticMac ? 'on' : ''} ${macLocked ? 'disabled is-locked' : ''}"
                  data-nic-static-mac-toggle="${idx}"
                  data-custom-switch="true"
                  role="switch"
                  aria-checked="${Boolean(nic.isStaticMac)}"
                  aria-disabled="${macLocked}"
                >
                  <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                </div>
              </div>
            </div>
          </div>

          ${renderNicBandwidthCardHtml(nic, idx, renderSettingLabelHtml)}
        </div>
      </div>
    `;
  } else if (tab === 'vlan') {
    const vlanWarnHtml = buildNicVlanContextInfobarHtml(nic);
    bodyHtml = `
      <div class="nic-policy-group">
        ${renderNicVlanGridHtml(nic, idx, renderSettingLabelHtml)}
        <div data-nic-vlan-warn="${idx}" ${vlanWarnHtml ? '' : 'hidden'}>${vlanWarnHtml}</div>
      </div>
    `;
  } else {
    bodyHtml = `
      <div class="nic-policy-group">
        ${renderNicSecurityGridHtml(nic, idx, renderSettingLabelHtml)}
      </div>
    `;
  }

  return `
    <div class="nic-section-stack" style="gap:16px;">
      ${tabsHtml}
      ${bodyHtml}
    </div>
  `;
}

/* ============================================================================
 * Guided Add Network Adapter View (Compact Single WinUI Card + ComboBox Switch)
 * ============================================================================ */
function renderNicGuidedAddViewHtml(state, vm, running, renderSettingLabelHtml) {
  const form = state.nicAddForm;
  const macLocked = running;
  const vmGen = getVmGeneration(state, vm);
  const canUseLegacy = vmGen === 1 && !running;
  const legacyTooltip =
    '默认关闭，绝大多数系统请使用标准高速网卡。仅在第 1 代虚机安装 XP 等老旧系统，或需要传统 BIOS PXE 引导时开启。只能在关机状态添加，且不支持硬件加速。';
  const macTooltip =
    '关闭时由地址池动态分配，开启后可手动指定 12 位十六进制静态 MAC 地址，仅关机状态下可设置';

  const activeSwitches = getActiveHostSwitches(state);
  const switchTooltip = '选择要连接的宿主机虚拟交换机，或设为“未连接”';

  const switchOptionsHtml =
    activeSwitches.length === 0
      ? `<option value="__disconnected__" selected>未连接</option>`
      : [
          ...activeSwitches.map(
            (sw) =>
              `<option value="${escapeDeviceHtml(sw.name)}" ${form.targetSwitch === sw.name ? 'selected' : ''}>${escapeDeviceHtml(sw.name)}</option>`
          ),
          `<option value="__disconnected__" ${form.targetSwitch === '__disconnected__' ? 'selected' : ''}>未连接</option>`
        ].join('');

  return `
    <div class="dev-stack">
      <div class="winui-card" style="margin-bottom:0;">
        <div class="winui-card-row">
          ${renderSettingLabelHtml('虚拟交换机', switchTooltip)}
          <select
            class="winui-select"
            id="nicAddSwitchSelect"
            style="width:240px;"
            ${activeSwitches.length === 0 ? 'disabled' : ''}
          >
            ${switchOptionsHtml}
          </select>
        </div>

        <div class="winui-card-row">
          <div style="${macLocked ? 'opacity:0.48;' : ''}">
            ${renderSettingLabelHtml('静态 MAC 地址', macTooltip)}
          </div>
          <div style="display:flex;align-items:center;gap:10px;flex-shrink:0;">
            ${
              form.isStaticMac && !macLocked
                ? `<input type="text" class="winui-textbox" id="nicAddMacInput" style="width:158px;height:30px;font-family:Consolas,monospace;font-size:12.5px;" value="${escapeDeviceHtml(form.staticMac || '00:15:5D:01:2A:0E')}" />`
                : ''
            }
            <div
              class="winui-switch ${form.isStaticMac && !macLocked ? 'on' : ''} ${macLocked ? 'disabled is-locked' : ''}"
              id="nicAddStaticMacSwitch"
              data-custom-switch="true"
              role="switch"
              aria-checked="${form.isStaticMac && !macLocked}"
              aria-disabled="${macLocked}"
            >
              <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
            </div>
          </div>
        </div>

        <div class="winui-card-row">
          <div style="${!canUseLegacy ? 'opacity:0.48;' : ''}">
            ${renderSettingLabelHtml('旧版网卡', legacyTooltip)}
          </div>
          <div
            class="winui-switch ${form.isLegacy && canUseLegacy ? 'on' : ''} ${!canUseLegacy ? 'disabled is-locked' : ''}"
            id="nicAddLegacySwitch"
            data-custom-switch="true"
            role="switch"
            aria-checked="${form.isLegacy && canUseLegacy}"
            aria-disabled="${!canUseLegacy}"
          >
            <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
          </div>
        </div>
      </div>
    </div>
  `;
}

function renderNicConnectionSectionHtml(nic, idx, running, renderSettingLabelHtml, renderTitleFirstKvItemHtml) {
  const macLocked = running;
  const macDesc = '由地址池自动分配，或手动指定 12 位十六进制静态 MAC 地址，仅关机状态下可修改';

  const switchOptionsHtml = [
    `<option value="__disconnected__" ${!nic.isConnected ? 'selected' : ''}>未连接</option>`,
    ...activeSwitchesCatalog.map(
      (sw) =>
        `<option value="${escapeDeviceHtml(sw.name)}" ${nic.isConnected && nic.switchName === sw.name ? 'selected' : ''}>${escapeDeviceHtml(sw.name)}</option>`
    )
  ].join('');

  return `
    <div class="nic-section-stack">
      <div class="isa-grid-2x2">
        <div class="isa-card uniform-1row span-2">
          <div class="isa-card-row">
            ${renderSettingLabelHtml('虚拟交换机连接', '选择要接入的虚拟交换机，或设为“未连接”模拟拔掉网线')}
            <div style="display:flex;align-items:center;gap:10px;flex-shrink:0;">
              <select class="winui-select" data-nic-switch-select="${idx}" style="width:260px;">
                ${switchOptionsHtml}
              </select>
            </div>
          </div>
        </div>

        <div class="isa-card uniform-1row ${nic.isStaticMac ? '' : 'span-2'} ${macLocked ? 'is-disabled' : ''}">
          <div class="isa-card-row">
            ${renderSettingLabelHtml('MAC 地址分配', macDesc)}
            <div style="display:flex;align-items:center;gap:8px;flex-shrink:0;">
              ${
                !nic.isStaticMac
                  ? `<span class="nic-mac-preview">${escapeDeviceHtml(formatNicMac(nic.macAddress))}</span>
                     <button class="dt-kv-head-copy" type="button" data-copy="${escapeDeviceHtml(formatNicMac(nic.macAddress))}" title="复制 MAC 地址"><span class="fi" style="font-size:11.5px;">&#xE8C8;</span></button>`
                  : ''
              }
              <div class="isa-pill-track inline-width" style="width:172px;">
                <button type="button" class="isa-pill-btn ${!nic.isStaticMac ? 'active' : ''}" data-nic-mac-mode="${idx}:dynamic" ${macLocked ? 'disabled' : ''}>动态</button>
                <button type="button" class="isa-pill-btn ${nic.isStaticMac ? 'active' : ''}" data-nic-mac-mode="${idx}:static" ${macLocked ? 'disabled' : ''}>静态</button>
              </div>
            </div>
          </div>
        </div>

        ${
          nic.isStaticMac
            ? `
              <div class="isa-card uniform-1row ${macLocked ? 'is-disabled' : ''}">
                <div class="isa-card-row">
                  ${renderSettingLabelHtml('静态 MAC', '格式 00:15:5D:XX:XX:XX，仅关机状态下可编辑')}
                  <div style="display:flex;align-items:center;gap:6px;flex-shrink:0;">
                    <input type="text" class="winui-textbox" data-nic-mac-input="${idx}" style="width:158px;font-family:Consolas,monospace;" value="${escapeDeviceHtml(formatNicMac(nic.macAddress))}" ${macLocked ? 'disabled' : ''} />
                    <button class="dt-kv-head-copy" type="button" data-copy="${escapeDeviceHtml(formatNicMac(nic.macAddress))}" title="复制 MAC 地址"><span class="fi" style="font-size:11.5px;">&#xE8C8;</span></button>
                  </div>
                </div>
              </div>
            `
            : ''
        }
      </div>

      ${renderNicIpKvGridHtml(nic, running, renderTitleFirstKvItemHtml)}
    </div>
  `;
}

function renderNicVlanSectionHtml(nic, idx, renderSettingLabelHtml) {
  return renderNicVlanGridHtml(nic, idx, renderSettingLabelHtml);
}

function renderNicBandwidthSectionHtml(nic, idx, renderSettingLabelHtml) {
  return renderNicBandwidthGridHtml(nic, idx, renderSettingLabelHtml);
}

function renderNicSecurityOffloadSectionHtml(nic, idx, renderSettingLabelHtml) {
  return renderNicSecurityGridHtml(nic, idx, renderSettingLabelHtml);
}

function renderNicCardHeaderHtml(nic, idx, opts = {}) {
  const { showPolicyBtn = false } = opts;
  const expanded = nic.expanded === true;
  const subParts = getNicSummarySubtitleParts(nic);
  const badges = getNicFeatureBadges(nic);

  const badgesHtml = badges
    .map((b) => `<span class="nic-head-badge">${escapeDeviceHtml(b)}</span>`)
    .join('');

  return `
    <div class="dev-b-head" data-nic-head-click="${idx}">
      <div class="tc-head-icon-box ${nic.isConnected ? '' : 'is-dimmed'}" style="width:38px;height:38px;font-size:17px;">
        <span class="fi">&#xE774;</span>
      </div>
      <div class="dev-b-identity">
        <div style="display:flex;align-items:center;gap:8px;min-width:0;flex-wrap:wrap;">
          <span class="dev-b-title">${escapeDeviceHtml(nic.name)}</span>
          ${badgesHtml}
        </div>
        <div class="dev-b-sub">${subParts.map((p) => `<span class="dev-b-sub-part">${escapeDeviceHtml(p)}</span>`).join('')}</div>
      </div>
      <div class="dev-b-actions" data-stop-prop>
        ${
          showPolicyBtn
            ? `<button class="round-icon-btn" title="配置端口策略" data-nic-open-policy="${idx}" type="button"><span class="fi fi-fluent" style="font-size:16px;">&#xF6AA;</span></button>`
            : ''
        }
        <button class="round-icon-btn" title="移除网卡" data-nic-remove="${idx}" type="button">
          <span class="fi fi-fluent" style="font-size:16px;">&#xF60A;</span>
        </button>
        <span class="dev-b-chevron"><span class="fi">${expanded ? '&#xE70E;' : '&#xE70D;'}</span></span>
      </div>
    </div>
  `;
}

function isNicPolicyDirty(saved, draft) {
  if (!saved || !draft) return false;
  if (Boolean(saved.isConnected) !== Boolean(draft.isConnected)) return true;
  if (draft.isConnected && String(saved.switchName || '') !== String(draft.switchName || '')) return true;
  if (Boolean(saved.isStaticMac) !== Boolean(draft.isStaticMac)) return true;
  if (draft.isStaticMac) {
    const cleanSaved = String(saved.macAddress || '').replace(/[^0-9A-Fa-f]/g, '').toUpperCase();
    const cleanDraft = String(draft.macAddress || '').replace(/[^0-9A-Fa-f]/g, '').toUpperCase();
    if (cleanSaved !== cleanDraft) return true;
  }
  if (Number(saved.bandwidthLimitMbps || 0) !== Number(draft.bandwidthLimitMbps || 0)) return true;
  const savedVlan = saved.vlanMode || 'Off';
  const draftVlan = draft.vlanMode || 'Off';
  if (savedVlan !== draftVlan) return true;
  if (draftVlan === 'Access' && Number(saved.accessVlanId ?? 20) !== Number(draft.accessVlanId ?? 20)) return true;
  if (draftVlan === 'Trunk') {
    if (Number(saved.nativeVlanId ?? 1) !== Number(draft.nativeVlanId ?? 1)) return true;
    if (String(saved.trunkAllowedVlans || '').trim() !== String(draft.trunkAllowedVlans || '').trim()) return true;
  }
  if (draftVlan === 'Private') {
    const savedRole = saved.pvlanMode || 'Isolated';
    const draftRole = draft.pvlanMode || 'Isolated';
    if (savedRole !== draftRole) return true;
    if (Number(saved.pvlanPrimaryId ?? 100) !== Number(draft.pvlanPrimaryId ?? 100)) return true;
    if (draftRole !== 'Promiscuous' && Number(saved.pvlanSecondaryId ?? 101) !== Number(draft.pvlanSecondaryId ?? 101)) return true;
  }
  if (Boolean(saved.macSpoofing) !== Boolean(draft.macSpoofing)) return true;
  if (Boolean(saved.teamingAllowed) !== Boolean(draft.teamingAllowed)) return true;
  if (Boolean(saved.dhcpGuard) !== Boolean(draft.dhcpGuard)) return true;
  if (Boolean(saved.routerGuard) !== Boolean(draft.routerGuard)) return true;
  if ((saved.monitorMode || 'None') !== (draft.monitorMode || 'None')) return true;
  if (Number(saved.stormLimit || 0) !== Number(draft.stormLimit || 0)) return true;
  if (Boolean(saved.sriovEnabled) !== Boolean(draft.sriovEnabled)) return true;
  return false;
}

function validateNicPolicyConfig(target, state, vm, showNotice, rerender) {
  if (!target) return true;
  if (!vm?.running && target.isStaticMac) {
    const cleanMac = String(target.macAddress || '').replace(/[^0-9A-Fa-f]/g, '');
    if (cleanMac.length !== 12) {
      if (state.nicMode === 'policy' && normalizeNicPolicyTab(state.nicPolicyTab) !== 'connection') {
        state.nicPolicyTab = 'connection';
        rerender();
      }
      showNotice('静态 MAC 地址须为 12 位十六进制字符（如 00:15:5D:01:2A:0B）');
      return false;
    }
    target.macAddress = formatNicMac(cleanMac);
  }
  if (target.vlanMode === 'Trunk') {
    const trunkErr = validateTrunkVlanList(target.trunkAllowedVlans);
    if (trunkErr) {
      if (state.nicMode === 'policy' && normalizeNicPolicyTab(state.nicPolicyTab) !== 'vlan') {
        state.nicPolicyTab = 'vlan';
        rerender();
      }
      showNotice(trunkErr);
      return false;
    }
  }
  return true;
}

export function renderNetworkWorkbench(host, ctx) {
  const {
    state,
    vm,
    renderTitleFirstKvItemHtml,
    renderSettingLabelHtml,
    renderDashboardSummary,
    showNotice,
    onLayoutNeeded,
    bindHostControls,
    notifyParentNicState
  } = ctx;

  activeSwitchesCatalog = ctx.isLive ? (Array.isArray(ctx.AVAILABLE_VSWITCHES) ? ctx.AVAILABLE_VSWITCHES : []) : AVAILABLE_VSWITCHES;

  const rerender = () => {
    syncVmAdaptersFromNetworkState(state, vm);
    renderNetworkWorkbench(host, ctx);
    bindHostControls?.(host);
    notifyParentNicState?.();
    requestAnimationFrame(() => onLayoutNeeded?.());
  };

  const adapters = ensureNetworkState(state, vm);
  syncVmAdaptersFromNetworkState(state, vm);
  ctx.setPageTabsTrailingAction?.('', null);
  const running = Boolean(vm.running);
  const variant = state.nicVariant || 'unified'; // 'unified' | 'inline-converged' | 'pill-tabs' | 'legacy'

  // =========================================================================
  // Old Legacy View Reference (5 Vertical Cards + 6 Apply Buttons)
  // =========================================================================
  if (variant === 'legacy') {
    const adapter = adapters[0] || {
      name: '网络适配器',
      isConnected: true,
      switchName: 'Default Switch',
      macAddress: '00155D012A0B'
    };
    const vlanMode = state.netVlanMode || 'Access';
    let vlanExtraRows = '';
    if (vlanMode === 'Access') {
      vlanExtraRows = `
        <div class="winui-card-row">
          ${renderSettingLabelHtml('VLAN ID', '0 表示不标记')}
          <input type="number" class="winui-textbox" style="width:180px;" value="0" />
        </div>
      `;
    } else if (vlanMode === 'Trunk') {
      vlanExtraRows = `
        <div class="winui-card-row">
          ${renderSettingLabelHtml('Native VLAN ID', null)}
          <input type="number" class="winui-textbox" style="width:180px;" value="1" />
        </div>
        <div class="winui-card-row">
          ${renderSettingLabelHtml('允许的 VLAN', '如 1-100,200')}
          <input type="text" class="winui-textbox" style="width:220px;" value="1-100" />
        </div>
      `;
    } else {
      vlanExtraRows = `
        <div class="winui-card-row">
          ${renderSettingLabelHtml('PVLAN 角色', null)}
          <select class="winui-select" style="width:180px;"><option>隔离</option><option>团体</option><option>混杂</option></select>
        </div>
        <div class="winui-card-row">
          ${renderSettingLabelHtml('主 VLAN ID', null)}
          <input type="number" class="winui-textbox" style="width:180px;" value="10" />
        </div>
        <div class="winui-card-row">
          ${renderSettingLabelHtml('辅助 VLAN ID', '混杂模式可填辅助列表，如 20-30')}
          <input type="text" class="winui-textbox" style="width:220px;" value="20" />
        </div>
      `;
    }

    host.innerHTML = `
      <div style="padding:8px 24px 24px 24px;display:flex;flex-direction:column;gap:16px;">
        <div style="display:flex;gap:8px;">
          <button class="winui-btn" id="legacyAddNicBtn" type="button">添加网卡</button>
          <button class="winui-btn" type="button">刷新</button>
        </div>

        <div class="winui-expander expanded">
          <div class="winui-expander-header">
            <span style="font-size:15px;">${escapeDeviceHtml(adapter.name)}</span>
            <span class="fi winui-expander-chevron">&#xE70E;</span>
          </div>
          <div class="winui-expander-body">
            <div class="settings-section-title">基本连接</div>
            <div class="winui-card">
              <div class="winui-card-row">
                ${renderSettingLabelHtml('连接', null)}
                <div class="winui-switch on" data-on-text="已连接" data-off-text="已断开">
                  <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                  <span class="switch-state-label">已连接</span>
                </div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('虚拟交换机', null)}
                <div style="display:flex;gap:8px;">
                  <select class="winui-select" style="width:200px;">
                    <option selected>Default Switch</option>
                    <option>External vSwitch</option>
                  </select>
                  <button class="winui-btn" type="button">应用</button>
                </div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('MAC 地址', '手动指定静态 MAC 或留空恢复自动分配（仅关机状态下可修改）')}
                <div style="display:flex;gap:8px;">
                  <input type="text" class="winui-textbox" style="width:180px;" value="${escapeDeviceHtml(adapter.macAddress)}" ${running ? 'disabled' : ''} />
                  <button class="winui-btn" ${running ? 'disabled' : ''} type="button">应用</button>
                  <button class="winui-btn" type="button">复制</button>
                </div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('IP 地址', null)}
                <div>${running ? `<button class="winui-btn" type="button">${vm.ipWhenRunning}  复制</button>` : `<span style="opacity:0.6;">暂无 IP 地址</span>`}</div>
              </div>
            </div>

            <div class="winui-card">
              <div class="winui-card-row">
                ${renderSettingLabelHtml('VLAN 隔离', null)}
                <select class="winui-select" id="netVlanModeSelect" style="width:180px;">
                  <option value="Access" ${vlanMode === 'Access' ? 'selected' : ''}>Access</option>
                  <option value="Trunk" ${vlanMode === 'Trunk' ? 'selected' : ''}>Trunk</option>
                  <option value="PrivateVlan" ${vlanMode === 'PrivateVlan' ? 'selected' : ''}>Private VLAN</option>
                </select>
              </div>
              ${vlanExtraRows}
              <div class="winui-card-row">
                <span></span>
                <button class="winui-btn" type="button">应用</button>
              </div>
            </div>

            <div class="winui-card">
              <div class="winui-card-row">
                ${renderSettingLabelHtml('最低带宽', 'Mbit/s，0 表示不保留')}
                <input type="text" class="winui-textbox" style="width:160px;" value="0" />
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('最高带宽', 'Mbit/s，0 表示不限速')}
                <input type="text" class="winui-textbox" style="width:160px;" value="0" />
              </div>
              <div class="winui-card-row">
                <span></span>
                <button class="winui-btn" type="button">应用</button>
              </div>
            </div>

            <div class="winui-card">
              <div class="winui-card-row">
                ${renderSettingLabelHtml('MAC 地址欺骗', null)}
                <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('DHCP 防护', null)}
                <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('路由器通告防护', null)}
                <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('允许网卡组队', null)}
                <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('端口镜像', null)}
                <select class="winui-select" style="width:140px;"><option>禁用</option><option>源端口</option><option>目标端口</option></select>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('风暴限制', '0 表示不限')}
                <input type="number" class="winui-textbox" style="width:140px;" value="0" />
              </div>
              <div class="winui-card-row">
                <span></span>
                <button class="winui-btn" type="button">应用</button>
              </div>
            </div>

            <div class="winui-card">
              <div class="winui-card-row">
                ${renderSettingLabelHtml('虚拟机队列 VMQ', null)}
                <div class="winui-switch on"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('SR-IOV', '需要宿主硬件与交换机支持')}
                <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('IPsec 任务卸载', null)}
                <div class="winui-switch on"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                <span></span>
                <button class="winui-btn" type="button">应用</button>
              </div>
            </div>

            <button class="winui-btn" type="button">移除网卡</button>
          </div>
        </div>
      </div>
    `;

    host.querySelector('#netVlanModeSelect')?.addEventListener('change', (e) => {
      state.netVlanMode = e.target.value;
      rerender();
    });
    return;
  }

  // =========================================================================
  // Guided Add Network Adapter Sub-View (Like GPU Add & Storage Add)
  // =========================================================================
  if (state.nicMode === 'add') {
    ctx.setPageTabsTrailingAction?.(
      `<button class="winui-btn" id="nicAddCancelBtn" type="button">返回</button><button class="winui-btn accent" id="nicAddConfirmBtn" type="button">添加网卡</button>`,
      null
    );
    host.innerHTML = renderNicGuidedAddViewHtml(state, vm, running, renderSettingLabelHtml);
    bindNicEvents(host, adapters, state, vm, rerender, renderDashboardSummary, showNotice);
    return;
  }

  // =========================================================================
  // 1-Screen Converged Port Policy Sub-View (Variant A: 'unified' -> 'policy')
  // =========================================================================
  if (variant === 'unified' && (state.nicMode === 'policy' || state.nicMode === 'edit')) {
    let targetIdx = adapters.findIndex((a) => a.id === state.nicEditTargetId);
    if (targetIdx < 0) targetIdx = 0;
    const savedNic = adapters[targetIdx];
    if (!savedNic) {
      state.nicPolicyDraft = null;
      state.nicPolicyDraftVmId = null;
      state.nicMode = 'list';
      rerender();
      return;
    }

    if (!state.nicPolicyDraft || state.nicPolicyDraftVmId !== vm.id || state.nicPolicyDraft.id !== savedNic.id) {
      state.nicPolicyDraft = structuredClone(savedNic);
      state.nicPolicyDraftVmId = vm.id;
    }
    const draftNic = state.nicPolicyDraft;
    const policyDirty = isNicPolicyDirty(savedNic, draftNic);

    const policyIconOverrides = {
      security: state.nicAdvIcon,
      securityLabel: state.nicAdvLabel
    };

    host.innerHTML = `
      <div class="dev-stack">
        ${renderNicOneScreenConvergedEditorHtml(
          draftNic,
          targetIdx,
          running,
          renderSettingLabelHtml,
          state.nicPolicyTab || 'connection',
          `
            <button class="winui-btn" id="nicSubviewBackBtn" type="button">返回</button>
            <button class="winui-btn accent" id="nicSubviewApplyBtn" type="button" ${policyDirty ? '' : 'disabled'}>应用</button>
          `,
          policyIconOverrides
        )}
      </div>
    `;

    const syncPolicyApplyBtn = () => {
      const applyBtn = host.querySelector('#nicSubviewApplyBtn');
      if (applyBtn) applyBtn.disabled = !isNicPolicyDirty(savedNic, draftNic);
    };

    const workingAdapters = adapters.map((a, i) => (i === targetIdx ? draftNic : a));
    bindNicEvents(host, workingAdapters, state, vm, rerender, renderDashboardSummary, showNotice, syncPolicyApplyBtn);

    host.querySelector('#nicSubviewApplyBtn')?.addEventListener('click', () => {
      if (!validateNicPolicyConfig(draftNic, state, vm, showNotice, rerender)) return;
      Object.assign(savedNic, structuredClone(draftNic));
      state.nicPolicyDraft = structuredClone(savedNic);
      state.nicPolicyDraftVmId = vm.id;
      renderDashboardSummary?.();
      showNotice(`已应用「${savedNic.name || '网络适配器'}」端口策略`);
      rerender();
    });

    host.querySelector('#nicSubviewBackBtn')?.addEventListener('click', () => {
      state.nicPolicyDraft = null;
      state.nicPolicyDraftVmId = null;
      state.nicMode = 'list';
      renderDashboardSummary?.();
      rerender();
    });
    return;
  }

  state.nicPolicyDraft = null;
  state.nicPolicyDraftVmId = null;

  // =========================================================================
  // List View for Variant A (Unified Read-Only List), Variant B (Inline 1-Screen), Variant C (4 Pill Tabs)
  // =========================================================================
  let cardsHtml = '';
  if (adapters.length === 0) {
    cardsHtml = renderNicAddRowCardHtml(true);
  } else {
    cardsHtml = adapters
      .map((nic, idx) => {
        const expanded = nic.expanded === true;

        if (variant === 'unified') {
          // 方案 A（主推）：心智对齐显卡与存储 —— 纯净只读 KV 砖块列表 + 点齿轮进一屏端口策略
          const bodyHtml = expanded
            ? `
              <div class="dev-b-body">
                ${renderNicReadOnlyKvBodyHtml(nic, running, renderTitleFirstKvItemHtml)}
              </div>
            `
            : '';

          return `
            <div class="dev-b-card ${expanded ? 'is-expanded' : ''}">
              ${renderNicCardHeaderHtml(nic, idx, { showPolicyBtn: true })}
              ${bodyHtml}
            </div>
          `;
        }

        let bodyHtml = '';
        if (expanded) {
          if (variant === 'inline-converged') {
            // 方案 B：列表卡片内直接展开一屏芯片化策略（免跳子页）
            bodyHtml = `
              <div class="dev-b-body nic-card-body">
                ${renderNicIpKvGridHtml(nic, running, renderTitleFirstKvItemHtml)}
                ${renderNicOneScreenConvergedEditorHtml(
                  nic,
                  idx,
                  running,
                  renderSettingLabelHtml,
                  state.nicPolicyTab || 'connection',
                  '',
                  { security: state.nicAdvIcon, securityLabel: state.nicAdvLabel }
                )}
                <div class="compute-rect-actions" style="margin-top:2px;">
                  <button class="winui-btn-secondary" data-nic-revert="${idx}" type="button">恢复默认</button>
                  <button class="winui-btn-accent" data-nic-apply="${idx}" type="button">应用修改</button>
                </div>
              </div>
            `;
          } else {
            // 方案 C：卡片内 4 段胶囊分栏 + 单一应用栏
            const activeTab = nic.activeSubTab || 'connection';
            const subTabs = [
              { id: 'connection', label: '连接与地址' },
              { id: 'vlan', label: 'VLAN 隔离' },
              { id: 'bandwidth', label: '带宽与限流' },
              { id: 'security', label: '端口策略' }
            ];
            let tabContent = '';
            if (activeTab === 'connection') {
              tabContent = renderNicConnectionSectionHtml(nic, idx, running, renderSettingLabelHtml, renderTitleFirstKvItemHtml);
            } else if (activeTab === 'vlan') {
              tabContent = renderNicVlanSectionHtml(nic, idx, renderSettingLabelHtml);
            } else if (activeTab === 'bandwidth') {
              tabContent = renderNicBandwidthSectionHtml(nic, idx, renderSettingLabelHtml);
            } else {
              tabContent = renderNicSecurityOffloadSectionHtml(nic, idx, renderSettingLabelHtml);
            }

            bodyHtml = `
              <div class="dev-b-body nic-card-body">
                <div class="isa-pill-track cols-4" role="tablist">
                  ${subTabs
                    .map(
                      (t) =>
                        `<button type="button" class="isa-pill-btn ${activeTab === t.id ? 'active' : ''}" data-nic-subtab="${idx}:${t.id}">${t.label}</button>`
                    )
                    .join('')}
                </div>
                ${tabContent}
                <div class="compute-rect-actions" style="margin-top:2px;">
                  <button class="winui-btn-secondary" data-nic-revert="${idx}" type="button">撤销</button>
                  <button class="winui-btn-accent" data-nic-apply="${idx}" type="button">应用</button>
                </div>
              </div>
            `;
          }
        }

        return `
          <div class="dev-b-card ${expanded ? 'is-expanded' : ''}">
            ${renderNicCardHeaderHtml(nic, idx, { showPolicyBtn: false })}
            ${bodyHtml}
          </div>
        `;
      })
      .join('');
  }

  host.innerHTML = `
    <div class="dev-stack">
      <div style="display:flex;flex-direction:column;gap:10px;">
        ${cardsHtml}
        ${adapters.length > 0 ? renderNicAddRowCardHtml(false) : ''}
      </div>
    </div>
  `;

  bindNicEvents(host, adapters, state, vm, rerender, renderDashboardSummary, showNotice);
  requestAnimationFrame(() => onLayoutNeeded?.());
}

function bindNicEvents(host, adapters, state, vm, rerender, renderDashboardSummary, showNotice, onDraftMutate = null) {
  host.querySelectorAll('[data-stop-prop]').forEach((el) => {
    el.addEventListener('click', (e) => e.stopPropagation());
  });

  host.querySelectorAll('[data-nic-head-click]').forEach((hdr) => {
    hdr.addEventListener('click', () => {
      const idx = Number(hdr.getAttribute('data-nic-head-click'));
      const target = adapters[idx];
      if (!target) return;
      target.expanded = !target.expanded;
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-open-policy]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const idx = Number(btn.getAttribute('data-nic-open-policy'));
      const target = adapters[idx];
      if (!target) return;
      state.nicEditTargetId = target.id;
      state.nicPolicyDraft = structuredClone(target);
      state.nicPolicyDraftVmId = vm.id;
      state.nicPolicyTab = 'connection';
      state.nicMode = 'policy';
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-policy-tab]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const nextTab = btn.getAttribute('data-nic-policy-tab');
      if (!nextTab) return;
      state.nicPolicyTab = normalizeNicPolicyTab(nextTab);
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-switch-select]').forEach((sel) => {
    sel.addEventListener('change', (e) => {
      const idx = Number(sel.getAttribute('data-nic-switch-select'));
      const target = adapters[idx];
      if (!target) return;
      const val = e.target.value;
      if (val === '__disconnected__') {
        target.isConnected = false;
        if (state.nicMode !== 'policy') {
          showNotice(`已断开「${target.name}」的虚拟网线连接`);
        }
      } else {
        target.isConnected = true;
        target.switchName = val;
        if (!target.ipv4 || target.ipv4.length === 0) {
          target.ipv4 = val.includes('External') ? ['192.168.1.128'] : ['172.24.118.94'];
          target.ipv6 = ['fe80::215:5dff:fe01:2a0b'];
        }
        if (state.nicMode !== 'policy') {
          showNotice(`「${target.name}」已连接到 ${val}`);
        }
      }
      renderDashboardSummary?.();
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-subtab]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const [idxStr, tabId] = (btn.getAttribute('data-nic-subtab') || '').split(':');
      const target = adapters[Number(idxStr)];
      if (!target || !tabId) return;
      target.activeSubTab = tabId;
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-mac-mode]').forEach((btn) => {
    btn.addEventListener('click', () => {
      if (btn.disabled) return;
      const [idxStr, mode] = (btn.getAttribute('data-nic-mac-mode') || '').split(':');
      const target = adapters[Number(idxStr)];
      if (!target) return;
      target.isStaticMac = mode === 'static';
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-static-mac-toggle]').forEach((sw) => {
    sw.addEventListener('click', () => {
      if (vm?.running || sw.classList.contains('disabled')) return;
      const idx = Number(sw.getAttribute('data-nic-static-mac-toggle'));
      const target = adapters[idx];
      if (!target) return;
      target.isStaticMac = !target.isStaticMac;
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-mac-input]').forEach((inp) => {
    inp.addEventListener('input', (e) => {
      const idx = Number(inp.getAttribute('data-nic-mac-input'));
      const target = adapters[idx];
      if (target) {
        target.macAddress = e.target.value;
        onDraftMutate?.();
      }
    });
    inp.addEventListener('change', (e) => {
      const idx = Number(inp.getAttribute('data-nic-mac-input'));
      const target = adapters[idx];
      if (!target) return;
      const cleanMac = String(e.target.value || '').replace(/[^0-9A-Fa-f]/g, '');
      if (cleanMac.length === 12) {
        const formatted = formatNicMac(cleanMac);
        target.macAddress = formatted;
        e.target.value = formatted;
        e.target.closest('.isa-card-row')?.querySelector('[data-copy]')?.setAttribute('data-copy', formatted);
        onDraftMutate?.();
      }
    });
  });

  host.querySelectorAll('[data-nic-vlan-mode]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const [idxStr, mode] = (btn.getAttribute('data-nic-vlan-mode') || '').split(':');
      const target = adapters[Number(idxStr)];
      if (!target || !mode) return;
      target.vlanMode = mode;
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-pvlan-role]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const [idxStr, role] = (btn.getAttribute('data-nic-pvlan-role') || '').split(':');
      const target = adapters[Number(idxStr)];
      if (!target || !role) return;
      target.pvlanMode = role;
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-field]').forEach((inp) => {
    inp.addEventListener('input', (e) => {
      const [idxStr, field] = (inp.getAttribute('data-nic-field') || '').split(':');
      const idx = Number(idxStr);
      const target = adapters[idx];
      if (!target || !field) return;
      target[field] = inp.type === 'number' ? Number(e.target.value) || 0 : e.target.value;

      if (field === 'trunkAllowedVlans') {
        const warnHost = host.querySelector(`[data-nic-vlan-warn="${idx}"]`);
        if (warnHost) {
          const html = buildNicVlanContextInfobarHtml(target);
          warnHost.innerHTML = html;
          warnHost.hidden = !html;
        }
      }
      onDraftMutate?.();
    });
  });

  const updateNicSliderVisual = (keyStr, rawVal, min, max, syncInput = true) => {
    const [idxStr, field] = (keyStr || '').split(':');
    const target = adapters[Number(idxStr)];
    if (!target || !field) return;
    const clamped = Math.max(min, Math.min(max, parseInt(rawVal || '0', 10) || min));
    target[field] = clamped;
    const ratio = (clamped - min) / Math.max(1, max - min);
    const sliderEl = host.querySelector(`[data-nic-slider="${keyStr}"]`);
    if (sliderEl) {
      sliderEl.style.setProperty('--energy-ratio', ratio.toFixed(4));
      sliderEl.setAttribute('aria-valuenow', String(clamped));
    }
    const tipEl = host.querySelector(`[data-nic-slider-tooltip="${keyStr}"]`);
    if (tipEl) tipEl.textContent = `VLAN ${clamped}`;
    if (syncInput) {
      const inpEl = host.querySelector(`[data-nic-slider-input="${keyStr}"]`);
      if (inpEl) {
        inpEl.value = String(clamped);
        inpEl.style.width = `${Math.max(1.8, String(clamped).length + 0.5)}ch`;
      }
    }
    onDraftMutate?.();
  };

  host.querySelectorAll('[data-nic-slider]').forEach((sliderEl) => {
    const keyStr = sliderEl.getAttribute('data-nic-slider');
    const min = Number(sliderEl.getAttribute('data-min')) || 1;
    const max = Number(sliderEl.getAttribute('data-max')) || 4094;
    const thumbEl = sliderEl.querySelector('.winui-energy-thumb');
    let dragging = false;
    let grabOffsetPx = 0;

    const updateFromClientX = (clientX) => {
      const rect = sliderEl.getBoundingClientRect();
      const thumbW = thumbEl ? thumbEl.offsetWidth : 24;
      const usableW = Math.max(1, rect.width - thumbW);
      const x = clientX - rect.left - grabOffsetPx;
      const r = Math.max(0, Math.min(1, x / usableW));
      const next = Math.round(min + r * (max - min));
      updateNicSliderVisual(keyStr, next, min, max, true);
    };

    sliderEl.addEventListener('pointerdown', (e) => {
      if (e.button !== 0 || sliderEl.classList.contains('is-disabled')) return;
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

    sliderEl.addEventListener('keydown', (e) => {
      const cur = Number(sliderEl.getAttribute('aria-valuenow')) || min;
      let delta = 0;
      if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') delta = -1;
      else if (e.key === 'ArrowRight' || e.key === 'ArrowUp') delta = 1;
      else if (e.key === 'PageDown') delta = -10;
      else if (e.key === 'PageUp') delta = 10;
      else if (e.key === 'Home') {
        e.preventDefault();
        updateNicSliderVisual(keyStr, min, min, max, true);
        return;
      } else if (e.key === 'End') {
        e.preventDefault();
        updateNicSliderVisual(keyStr, max, min, max, true);
        return;
      }
      if (delta !== 0) {
        e.preventDefault();
        updateNicSliderVisual(keyStr, cur + delta, min, max, true);
      }
    });
  });

  host.querySelectorAll('[data-nic-slider-input]').forEach((inp) => {
    const keyStr = inp.getAttribute('data-nic-slider-input');
    const sliderEl = host.querySelector(`[data-nic-slider="${keyStr}"]`);
    const min = Number(sliderEl?.getAttribute('data-min')) || 1;
    const max = Number(sliderEl?.getAttribute('data-max')) || 4094;
    inp.addEventListener('input', () => {
      inp.style.width = `${Math.max(1.8, (inp.value || '0').length + 0.5)}ch`;
      const parsed = parseInt(inp.value.trim(), 10);
      if (!Number.isNaN(parsed)) {
        updateNicSliderVisual(keyStr, parsed, min, max, false);
      }
    });
    inp.addEventListener('change', () => {
      const parsed = parseInt(inp.value.trim(), 10);
      updateNicSliderVisual(keyStr, Number.isNaN(parsed) ? min : parsed, min, max, true);
    });
  });

  host.querySelectorAll('[data-nic-stepped-card]').forEach((cardEl) => {
    const [idxStr, field] = (cardEl.getAttribute('data-nic-stepped-card') || '').split(':');
    const idx = Number(idxStr);
    const target = adapters[idx];
    const spec = NIC_STEPPED_SLIDER_SPECS[field];
    if (!target || !spec) return;
    const sliderEl = cardEl.querySelector(`#nicSteppedSlider-${field}-${idx}`);
    const tipEl = cardEl.querySelector(`#nicSteppedTooltip-${field}-${idx}`);
    const inpEl = cardEl.querySelector(`[data-nic-stepped-input="${idx}:${field}"]`);

    const syncSteppedVisual = (rawVal, syncInput = true) => {
      const { val, tooltipText } = formatNicSteppedSliderDisplay(field, rawVal);
      target[field] = val;
      if (sliderEl) {
        const stopPos = getSteppedStopPos(spec.stops, val);
        sliderEl.style.setProperty('--energy-ratio', (stopPos / Math.max(1, spec.stops.length - 1)).toFixed(4));
        sliderEl.setAttribute('aria-valuenow', String(Math.round(stopPos)));
      }
      if (tipEl) tipEl.textContent = tooltipText;
      if (syncInput && inpEl) {
        inpEl.value = String(val);
        inpEl.style.width = `${Math.max(1.8, String(val).length + 0.5)}ch`;
      }
      onDraftMutate?.();
    };

    bindSteppedEnergySlider(sliderEl, {
      count: spec.stops.length,
      onStep: (stepIdx) => {
        syncSteppedVisual(spec.stops[stepIdx], true);
      }
    });

    inpEl?.addEventListener('input', () => {
      inpEl.style.width = `${Math.max(1.8, (inpEl.value || '0').length + 0.5)}ch`;
      const raw = inpEl.value.trim();
      if (/^\d+$/.test(raw)) {
        syncSteppedVisual(parseInt(raw, 10), false);
      }
    });

    inpEl?.addEventListener('change', () => {
      const raw = inpEl.value.trim();
      const parsed = /^\d+$/.test(raw) ? parseInt(raw, 10) : 0;
      syncSteppedVisual(parsed, true);
    });
  });

  host.querySelectorAll('[data-nic-toggle]').forEach((sw) => {
    sw.addEventListener('click', () => {
      const [idxStr, key] = (sw.getAttribute('data-nic-toggle') || '').split(':');
      const target = adapters[Number(idxStr)];
      if (!target || !key) return;
      if (target.isLegacy && key === 'sriovEnabled') {
        return;
      }
      target[key] = !target[key];
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-mirror-mode]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const [idxStr, mode] = (btn.getAttribute('data-nic-mirror-mode') || '').split(':');
      const target = adapters[Number(idxStr)];
      if (!target || !mode) return;
      target.monitorMode = mode;
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-apply]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const idx = Number(btn.getAttribute('data-nic-apply'));
      const target = adapters[idx];
      if (!validateNicPolicyConfig(target, state, vm, showNotice, rerender)) return;
      showNotice(`已应用「${target?.name || '网络适配器'}」端口策略`);
      renderDashboardSummary?.();
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-revert]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const idx = Number(btn.getAttribute('data-nic-revert'));
      const target = adapters[idx];
      if (!target) return;
      target.vlanMode = 'Off';
      target.bandwidthLimitMbps = 0;
      target.stormLimit = 0;
      target.macSpoofing = false;
      target.dhcpGuard = false;
      target.routerGuard = false;
      target.teamingAllowed = false;
      target.monitorMode = 'None';
      target.sriovEnabled = false;
      showNotice('已恢复当前网卡默认端口策略');
      rerender();
    });
  });

  host.querySelectorAll('[data-nic-remove]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const idx = Number(btn.getAttribute('data-nic-remove'));
      const removed = adapters.splice(idx, 1)[0];
      if (adapters.length === 0) state.nicScenario = 'empty';
      else if (adapters.length === 1) state.nicScenario = 'single';
      renderDashboardSummary?.();
      rerender();
      showNotice(`已移除「${removed?.name || '网络适配器'}」`);
    });
  });

  // Open Guided Add Sub-view when clicking [+ 添加网卡]
  host.querySelector('#nicAddAdapterBtn')?.addEventListener('click', () => {
    const nextNum = adapters.length + 1;
    const hexTail = (10 + nextNum).toString(16).toUpperCase().padStart(2, '0');
    const activeSwitches = getActiveHostSwitches(state);
    state.nicAddForm = {
      isLegacy: false,
      targetSwitch: activeSwitches.length > 0 ? activeSwitches[0].name : '__disconnected__',
      isStaticMac: false,
      staticMac: `00:15:5D:01:2A:${hexTail}`
    };
    state.nicMode = 'add';
    rerender();
  });

  // Guided Add Sub-view Controls
  host.querySelector('#nicAddLegacySwitch')?.addEventListener('click', () => {
    const vmGen = getVmGeneration(state, vm);
    if (vmGen === 2 || vm?.running) return;
    state.nicAddForm.isLegacy = !state.nicAddForm.isLegacy;
    rerender();
  });

  host.querySelector('#nicAddSwitchSelect')?.addEventListener('change', (e) => {
    state.nicAddForm.targetSwitch = e.target.value || '__disconnected__';
    rerender();
  });

  host.querySelector('#nicAddStaticMacSwitch')?.addEventListener('click', () => {
    if (vm?.running) return;
    state.nicAddForm.isStaticMac = !state.nicAddForm.isStaticMac;
    rerender();
  });

  host.querySelector('#nicAddMacInput')?.addEventListener('input', (e) => {
    state.nicAddForm.staticMac = e.target.value;
  });

  document.getElementById('nicAddCancelBtn')?.addEventListener('click', () => {
    state.nicMode = 'list';
    rerender();
  });

  document.getElementById('nicAddConfirmBtn')?.addEventListener('click', () => {
    const form = state.nicAddForm || {};
    const nextNum = adapters.length + 1;
    const hexTail = (10 + nextNum).toString(16).toUpperCase().padStart(2, '0');
    const activeSwitches = getActiveHostSwitches(state);
    const isConnected = activeSwitches.length > 0 && form.targetSwitch !== '__disconnected__';
    const switchName = isConnected ? form.targetSwitch || activeSwitches[0].name : '';
    const useStaticMac = !vm.running && Boolean(form.isStaticMac);
    const macAddr = useStaticMac ? formatNicMac(form.staticMac) : `00:15:5D:01:2A:${hexTail}`;
    const isLegacy = getVmGeneration(state, vm) === 1 && !vm.running && Boolean(form.isLegacy);
    const baseName = '网络适配器';

    adapters.forEach((a) => {
      a.expanded = false;
    });

    const created = {
      id: `nic-${Date.now()}`,
      name: nextNum === 1 ? baseName : `${baseName} ${nextNum}`,
      isLegacy,
      isConnected,
      switchName: switchName || 'Default Switch',
      macAddress: macAddr,
      isStaticMac: useStaticMac,
      ipv4: isConnected ? [switchName.includes('External') ? `192.168.1.${127 + nextNum}` : `172.24.118.${93 + nextNum}`] : [],
      ipv6: isConnected ? [`fe80::215:5dff:fe01:2a${hexTail.toLowerCase()}`] : [],
      vlanMode: 'Off',
      accessVlanId: 20,
      nativeVlanId: 1,
      trunkAllowedVlans: '10, 20, 30-40',
      pvlanMode: 'Isolated',
      pvlanPrimaryId: 100,
      pvlanSecondaryId: 101,
      bandwidthLimitMbps: 0,
      stormLimit: 0,
      macSpoofing: false,
      dhcpGuard: false,
      routerGuard: false,
      teamingAllowed: false,
      monitorMode: 'None',
      vmqEnabled: !isLegacy,
      sriovEnabled: false,
      ipsecOffloadEnabled: !isLegacy,
      expanded: true,
      activeSubTab: 'connection',
      advDrawerOpen: false
    };

    adapters.push(created);
    state.nicScenario = adapters.length > 1 ? 'multi' : 'single';
    state.nicEditTargetId = created.id;
    state.nicMode = 'list';
    renderDashboardSummary?.();
    rerender();
    showNotice(
      isConnected
        ? `已添加「${created.name}」并连接到 ${created.switchName}`
        : `已添加「${created.name}」（未连接虚拟网线）`
    );
  });
}

/* ============================================================================
 * Serial Port Workbench (Aligned 1:1 with GPU / Storage / Network Cards)
 * ============================================================================ */

function normalizeSerialPipePath(raw) {
  const trimmed = String(raw || '').trim();
  if (!trimmed) return '';
  if (trimmed.startsWith('\\\\')) return trimmed;
  return `\\\\.\\pipe\\${trimmed.replace(/^\\+/, '')}`;
}

function getSerialPipePathError(pipePath) {
  const normalized = normalizeSerialPipePath(pipePath);
  if (!normalized) return null;
  if (!normalized.startsWith('\\\\')) {
    return '命名管道路径必须以 \\\\.\\pipe\\ 或 \\\\服务器\\pipe\\ 开头。';
  }
  const pipeMarker = normalized.toLowerCase().indexOf('\\pipe\\', 2);
  if (pipeMarker <= 2 || pipeMarker + 6 >= normalized.length) {
    return '命名管道路径格式无效，示例：\\\\.\\pipe\\my-vm-com1。';
  }
  const server = normalized.slice(2, pipeMarker);
  if (!server.trim() || server.includes('\\') || server.includes('/') || /\s/.test(server)) {
    return '命名管道服务器名格式无效，本地请使用 \\\\.\\pipe\\名称。';
  }
  const pipeName = normalized.slice(pipeMarker + 6);
  if (
    !pipeName.trim() ||
    pipeName === '.' ||
    pipeName === '..' ||
    pipeName.startsWith('\\') ||
    pipeName.endsWith('\\') ||
    pipeName.includes('\\\\') ||
    /[\x00-\x1f:/\*?"<>|]/.test(pipeName)
  ) {
    return '命名管道名称包含无效字符或格式有误，示例：\\\\.\\pipe\\my-vm-com1。';
  }
  return null;
}

function buildDefaultSerialPipePath(vm, port) {
  const seg =
    String(vm?.name || 'vm')
      .trim()
      .replace(/[^A-Za-z0-9._-]+/g, '-')
      .replace(/^-+|-+$/g, '') || 'vm';
  return `\\\\.\\pipe\\${seg}-com${port.portNumber}`;
}

function ensureSerialPortsForVm(state, vm, initialPorts) {
  if (!state.serialPortsByVm) state.serialPortsByVm = {};
  if (!state.serialPortsByVm[vm.id]) {
    state.serialPortsByVm[vm.id] = (initialPorts || []).map((p) => structuredClone(p));
  }
  return state.serialPortsByVm[vm.id];
}

export function renderSerialWorkbench(host, ctx) {
  const {
    state,
    vm,
    INITIAL_SERIAL_PORTS = [],
    renderTitleFirstKvItemHtml,
    renderSettingLabelHtml,
    showNotice,
    onLayoutNeeded,
    bindHostControls,
    setPageTabsTrailingAction
  } = ctx;

  setPageTabsTrailingAction?.('', null);

  const rerender = () => {
    renderSerialWorkbench(host, ctx);
    bindHostControls?.(host);
    requestAnimationFrame(() => onLayoutNeeded?.());
  };

  const canModify = !vm?.running;
  const ports = ensureSerialPortsForVm(state, vm, INITIAL_SERIAL_PORTS);
  if (state.serialEditVmId !== vm.id) {
    state.serialMode = 'list';
    state.serialDraft = null;
  }

  const lockBannerHtml = !canModify
    ? buildDeviceInlineBannerHtml({
        severity: 'warning',
        title: '虚拟机运行中，串口配置已锁定',
        desc: '关闭虚拟机后才可开关连接或修改管道与内核调试设置'
      })
    : '';

  // =========================================================================
  // View 2: 单独的串口设置子页（精简两项：命名管道路径 + 内核调试模式）
  // =========================================================================
  if (state.serialMode === 'config') {
    const targetIdx = ports.findIndex((p) => p.portNumber === state.serialEditPortNumber);
    const portIdx = targetIdx >= 0 ? targetIdx : 0;
    const port = ports[portIdx];
    if (!port) {
      state.serialMode = 'list';
      rerender();
      return;
    }

    if (!state.serialDraft || state.serialDraft.portNumber !== port.portNumber) {
      state.serialDraft = {
        portNumber: port.portNumber,
        pipePath: String(port.pipePath || '').trim(),
        debuggerMode: Boolean(port.debuggerMode)
      };
    }

    const draft = state.serialDraft;
    const draftTrimmed = String(draft.pipePath || '').trim();
    const hasDraftPipe = Boolean(draftTrimmed);
    const normalizedDraft = normalizeSerialPipePath(draftTrimmed);
    const debugEditable = canModify && (hasDraftPipe || Boolean(normalizedDraft) || Boolean(draft.debuggerMode));

    host.innerHTML = `
      <div class="dev-stack">
        <div class="isa-grid-2x2">
          <div class="isa-card uniform-1row span-2 ${canModify ? '' : 'is-disabled'}">
            <div class="isa-card-row">
              ${renderSettingLabelHtml(
                '命名管道路径',
                '输入短名称会自动补全为 \\\\.\\pipe\\名称，供 PuTTY / WinDbg 连接，留空后应用可断开连接。'
              )}
              <input
                type="text"
                class="winui-textbox"
                id="serialSubPipeInput"
                style="width:260px;font-family:Consolas,monospace;font-size:12.5px;"
                value="${escapeDeviceHtml(draft.pipePath)}"
                placeholder="管道名或 \\\\.\\pipe\\名称"
                ${canModify ? '' : 'disabled'}
              />
            </div>
          </div>

          <div class="isa-card uniform-1row span-2 ${canModify ? '' : 'is-disabled'}">
            <div class="isa-card-row">
              ${renderSettingLabelHtml(
                '内核调试模式',
                '为 Windows 内核调试器（WinDbg）优化传输，普通串口控制台无需开启，需配置命名管道后启用。'
              )}
              <div
                class="winui-switch ${draft.debuggerMode ? 'on' : ''} ${debugEditable ? '' : 'disabled'}"
                id="serialSubDebugSwitch"
                data-custom-switch="true"
                role="switch"
                aria-checked="${Boolean(draft.debuggerMode)}"
                title="${
                  !canModify
                    ? '关闭虚拟机后可修改'
                    : debugEditable
                      ? '切换内核调试模式'
                      : '需先配置命名管道后才可开启内核调试模式'
                }"
              >
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
              </div>
            </div>
          </div>
        </div>

        ${lockBannerHtml}

        <div class="dev-action-row" style="justify-content:flex-end;">
          <button class="winui-btn" id="serialSubviewBackBtn" type="button">返回</button>
          <button class="winui-btn accent" id="serialSubApplyBtn" type="button" ${canModify ? '' : 'disabled'}>应用</button>
        </div>
      </div>
    `;

    const syncSubDebugAvailability = () => {
      const dbgSw = host.querySelector('#serialSubDebugSwitch');
      if (!dbgSw) return;
      const norm = normalizeSerialPipePath(draft.pipePath);
      const canDbg = canModify && (Boolean(norm) || Boolean(draft.debuggerMode));
      dbgSw.classList.toggle('disabled', !canDbg);
      dbgSw.setAttribute(
        'title',
        !canModify ? '关闭虚拟机后可修改' : canDbg ? '切换内核调试模式' : '需先配置命名管道后才可开启内核调试模式'
      );
    };

    host.querySelector('#serialSubviewBackBtn')?.addEventListener('click', () => {
      state.serialMode = 'list';
      state.serialDraft = null;
      rerender();
    });

    host.querySelector('#serialSubPipeInput')?.addEventListener('input', (e) => {
      draft.pipePath = e.target.value;
      syncSubDebugAvailability();
    });

    host.querySelector('#serialSubDebugSwitch')?.addEventListener('click', () => {
      if (!canModify) return;
      const canDbg = Boolean(normalizeSerialPipePath(draft.pipePath)) || Boolean(draft.debuggerMode);
      if (!canDbg) return;
      draft.debuggerMode = !draft.debuggerMode;
      rerender();
    });

    const applyAndClose = () => {
      if (!canModify) return;
      const raw = String(draft.pipePath || '').trim();
      if (raw) {
        const err = getSerialPipePathError(raw);
        if (err) {
          showNotice?.(err);
          return;
        }
      }
      port.pipePath = raw ? normalizeSerialPipePath(raw) : '';
      port.debuggerMode = Boolean(draft.debuggerMode);
      state.serialMode = 'list';
      state.serialDraft = null;
      rerender();
      showNotice?.(`已应用 ${port.name} 串口设置`);
    };

    host.querySelector('#serialSubPipeInput')?.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        applyAndClose();
      }
    });

    host.querySelector('#serialSubApplyBtn')?.addEventListener('click', applyAndClose);

    requestAnimationFrame(() => onLayoutNeeded?.());
    return;
  }

  // =========================================================================
  // View 1: 串口列表主视图（默认全部展开、无右侧箭头、齿轮进设置页、关闭直接清空）
  // =========================================================================
  const cardsHtml = ports
    .map((port, idx) => {
      const pipe = String(port.pipePath || '').trim();
      const isEnabled = Boolean(pipe);

      const badges = [];
      if (port.debuggerMode) badges.push('内核调试');
      const badgesHtml = badges.map((b) => `<span class="nic-head-badge">${escapeDeviceHtml(b)}</span>`).join('');

      const pipeDisplayText = isEnabled ? pipe : '未配置';

      return `
        <div class="dev-b-card is-expanded">
          <div class="dev-b-head is-static">
            <div class="tc-head-icon-box ${isEnabled ? '' : 'is-dimmed'}" style="width:38px;height:38px;font-size:17px;">
              <span class="fi fi-fluent">&#xF6A3;</span>
            </div>
            <div class="dev-b-identity">
              <div style="display:flex;align-items:center;gap:8px;min-width:0;flex-wrap:wrap;">
                <span class="dev-b-title">${escapeDeviceHtml(port.name)}</span>
                ${badgesHtml}
              </div>
            </div>
            <div class="dev-b-actions">
              <button
                class="round-icon-btn"
                title="配置串口设置"
                data-serial-open-config="${idx}"
                type="button"
              >
                <span class="fi" style="font-size:15px;">&#xE713;</span>
              </button>
              <div
                class="winui-switch ${isEnabled ? 'on' : ''} ${canModify ? '' : 'disabled'}"
                data-serial-enable="${idx}"
                data-custom-switch="true"
                role="switch"
                aria-checked="${isEnabled}"
                title="${canModify ? (isEnabled ? '关闭并清空命名管道' : '开启默认命名管道连接') : '关闭虚拟机后可修改'}"
              >
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
              </div>
            </div>
          </div>
          <div class="dev-b-body">
            <div class="dt-kv-grid is-title-first is-2col">
              ${renderTitleFirstKvItemHtml({
                title: '命名管道路径',
                stateText: escapeDeviceHtml(pipeDisplayText),
                copyVal: isEnabled ? escapeDeviceHtml(pipe) : '',
                wide: true
              })}
            </div>
          </div>
        </div>
      `;
    })
    .join('');

  host.innerHTML = `
    <div class="dev-stack">
      <div style="display:flex;flex-direction:column;gap:10px;">
        ${cardsHtml}
      </div>
      ${lockBannerHtml}
    </div>
  `;

  host.querySelectorAll('[data-serial-open-config]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const idx = Number(btn.getAttribute('data-serial-open-config'));
      const port = ports[idx];
      if (!port) return;
      state.serialEditVmId = vm.id;
      state.serialEditPortNumber = port.portNumber;
      state.serialDraft = null;
      state.serialMode = 'config';
      rerender();
    });
  });

  host.querySelectorAll('[data-serial-enable]').forEach((sw) => {
    sw.addEventListener('click', () => {
      if (!canModify) return;
      const idx = Number(sw.getAttribute('data-serial-enable'));
      const port = ports[idx];
      if (!port) return;
      const wasEnabled = Boolean(port.pipePath && port.pipePath.trim());
      if (wasEnabled) {
        port.pipePath = '';
        rerender();
        showNotice?.(`已关闭 ${port.name} 命名管道连接`);
      } else {
        port.pipePath = buildDefaultSerialPipePath(vm, port);
        rerender();
        showNotice?.(`已开启 ${port.name} 命名管道连接`);
      }
    });
  });

  requestAnimationFrame(() => onLayoutNeeded?.());
}

/* ============================================================================
 * vTPM Workbench (Restrained View 1 Card + Full Hidden WMI View 2 Settings)
 * ============================================================================ */

function ensureVtpmForVm(state, vm, initialByVm) {
  if (!state.vtpmByVm) state.vtpmByVm = {};
  if (!state.vtpmByVm[vm.id]) {
    const seed = initialByVm?.[vm.id];
    state.vtpmByVm[vm.id] = seed
      ? structuredClone(seed)
      : {
          kpConfigured: false,
          hasPreviousKey: false,
          boundHost: '',
          authorizedHosts: [],
          signingThumbprint: '',
          guestStateFile: `C:\\ProgramData\\Microsoft\\Windows\\Hyper-V\\${vm?.name || 'VM'}\\Virtual Machines\\${String(vm.id || 'vm').toUpperCase()}.vmgs`
        };
  }
  const vtpm = state.vtpmByVm[vm.id];
  if (!Array.isArray(vtpm.authorizedHosts)) {
    vtpm.authorizedHosts = vtpm.kpConfigured
      ? [
          {
            id: 'local',
            name: 'Saniye',
            thumbprint: vtpm.signingThumbprint || '839EAFC9E638F592D603684F60CC3514A2288CF5',
            isLocal: true
          }
        ]
      : [];
  }
  if (vtpm.hasPreviousKey == null) vtpm.hasPreviousKey = Boolean(vtpm.kpConfigured);
  return vtpm;
}

function snapshotVtpmKey(vtpm) {
  vtpm._prevKeySnapshot = {
    kpConfigured: Boolean(vtpm.kpConfigured),
    boundHost: String(vtpm.boundHost || ''),
    authorizedHosts: structuredClone(vtpm.authorizedHosts || []),
    signingThumbprint: String(vtpm.signingThumbprint || '')
  };
  vtpm.hasPreviousKey = true;
}

function bindVtpmToCurrentHost(vtpm, vm) {
  if (vtpm.kpConfigured) snapshotVtpmKey(vtpm);
  const vmIdRaw = String(vm?.id || 'vm').replace(/[^a-f0-9]/gi, '').toUpperCase().padEnd(12, 'A');
  const rev = (vtpm._revCount = (vtpm._revCount || 0) + 1);
  const hexTag = `${(0xA0 + (rev % 80)).toString(16).toUpperCase()}${vmIdRaw.slice(0, 6)}`;
  const thumb = `${hexTag}E9638F592D603684F60CC3514A2288CF5`.slice(0, 40);
  vtpm.kpConfigured = true;
  vtpm.hasPreviousKey = true;
  vtpm.boundHost = '当前宿主 (Saniye)';
  vtpm.signingThumbprint = thumb;
  vtpm.authorizedHosts = [
    {
      id: 'local',
      name: 'Saniye',
      thumbprint: thumb,
      isLocal: true
    }
  ];
  if (!vtpm.guestStateFile) {
    vtpm.guestStateFile = `C:\\ProgramData\\Microsoft\\Windows\\Hyper-V\\${vm?.name || 'VM'}\\Virtual Machines\\${String(vm.id || 'vm').toUpperCase()}.vmgs`;
  }
}

export function ensureVtpmHostKeyForVm(state, vm, initialByVm) {
  const vtpm = ensureVtpmForVm(state, vm, initialByVm);
  if (!vtpm.kpConfigured) {
    bindVtpmToCurrentHost(vtpm, vm);
  }
  return vtpm;
}

function restoreVtpmPreviousKey(vtpm) {
  if (!vtpm.hasPreviousKey) return false;
  const current = {
    kpConfigured: Boolean(vtpm.kpConfigured),
    boundHost: String(vtpm.boundHost || ''),
    authorizedHosts: structuredClone(vtpm.authorizedHosts || []),
    signingThumbprint: String(vtpm.signingThumbprint || '')
  };
  if (vtpm._prevKeySnapshot) {
    Object.assign(vtpm, structuredClone(vtpm._prevKeySnapshot));
    vtpm._prevKeySnapshot = current;
  } else {
    vtpm._prevKeySnapshot = current;
    const prevThumb = `74C8F1A0${String(vtpm.signingThumbprint || '92D603684F60CC3514A2288CF5').slice(8)}`.slice(0, 40);
    vtpm.signingThumbprint = prevThumb;
    if (Array.isArray(vtpm.authorizedHosts) && vtpm.authorizedHosts[0]) {
      vtpm.authorizedHosts[0].thumbprint = prevThumb;
    }
  }
  vtpm.hasPreviousKey = true;
  return true;
}

function parseGuardianHostName(certPath) {
  const base = String(certPath || '')
    .trim()
    .split(/[\\/]/)
    .pop()
    ?.replace(/\.(xml|cer|crt|pfx|pem|der|hgs)$/i, '')
    .replace(/-?Guardian$/i, '')
    .trim();
  return base || '';
}

function buildThumbprintForSeed(seedText) {
  let acc = 0x5A3C91F2;
  for (let i = 0; i < seedText.length; i += 1) {
    acc = ((acc * 131) ^ seedText.charCodeAt(i)) >>> 0;
  }
  return `${acc.toString(16).toUpperCase().padStart(8, '0')}B7C50129A88F3C10E4B792D18C650412`.slice(0, 40);
}

function buildVtpmGuardianCertXml(vtpm) {
  const hostName = 'Saniye';
  const thumb = String(vtpm?.signingThumbprint || buildThumbprintForSeed(hostName));
  return [
    '<?xml version="1.0" encoding="utf-8"?>',
    `<Guardian Name="${escapeDeviceHtml(hostName)}" SigningThumbprint="${escapeDeviceHtml(thumb)}" ExportedAt="${new Date().toISOString()}" />`,
    ''
  ].join('\n');
}

function parseGuardianCertPayload(input) {
  if (input && typeof input === 'object') {
    const fileName = String(input.fileName || input.name || '');
    const content = String(input.content || '');
    const attrNameMatch = content.match(/\b(?:HostName|Name)\s*=\s*["']([^"']+)["']/i);
    const elemNameMatch = content.match(/<(?:HostName|Name)\b[^>]*>([^<]+)<\/(?:HostName|Name)>/i);
    const thumbMatch = content.match(/\b(?:SigningThumbprint|Thumbprint)\s*=\s*["']([A-Fa-f0-9]{16,64})["']/i);
    const hostName = (attrNameMatch?.[1] || elemNameMatch?.[1] || '').trim() || parseGuardianHostName(fileName);
    return { hostName, thumbprint: thumbMatch?.[1]?.toUpperCase() || '' };
  }
  return { hostName: parseGuardianHostName(input), thumbprint: '' };
}

function getSameOriginPickerWindow() {
  if (typeof window === 'undefined') return null;
  try {
    if (window.top && window.top.location?.origin === window.location?.origin) {
      return window.top;
    }
  } catch {
    // Ignore cross-origin frame access errors and use current window
  }
  return window;
}

async function saveVtpmGuardianCertFile(suggestedName, content) {
  const pickerWin = getSameOriginPickerWindow();
  if (pickerWin && typeof pickerWin.showSaveFilePicker === 'function') {
    try {
      const handle = await pickerWin.showSaveFilePicker({
        suggestedName,
        types: [
          {
            description: '宿主证书文件',
            accept: {
              'text/xml': ['.xml'],
              'application/x-x509-ca-cert': ['.cer', '.crt']
            }
          }
        ]
      });
      const writable = await handle.createWritable();
      await writable.write(content);
      await writable.close();
      return { ok: true, fileName: handle.name || suggestedName };
    } catch (err) {
      if (err?.name === 'AbortError') return { ok: false, cancelled: true };
    }
  }
  if (typeof document !== 'undefined' && typeof URL !== 'undefined' && typeof URL.createObjectURL === 'function' && typeof Blob !== 'undefined') {
    const blob = new Blob([content], { type: 'application/xml;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = suggestedName;
    link.style.display = 'none';
    document.body?.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    return { ok: true, fileName: suggestedName };
  }
  return { ok: true, fileName: suggestedName };
}

function pickCertViaInputElement() {
  if (typeof document === 'undefined' || typeof document.createElement !== 'function') {
    return Promise.resolve(null);
  }
  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.xml,.cer,.crt,.pfx,.pem,.der,.hgs';
    input.style.display = 'none';
    let settled = false;
    const finish = (val) => {
      if (settled) return;
      settled = true;
      input.remove();
      resolve(val);
    };
    input.addEventListener('change', async () => {
      const file = input.files?.[0];
      if (!file) {
        finish(null);
        return;
      }
      const content = typeof file.text === 'function' ? await file.text().catch(() => '') : '';
      finish({ fileName: file.name, content });
    });
    input.addEventListener('cancel', () => finish(null));
    document.body?.appendChild(input);
    input.click();
  });
}

async function openVtpmGuardianCertFile() {
  const pickerWin = getSameOriginPickerWindow();
  if (pickerWin && typeof pickerWin.showOpenFilePicker === 'function') {
    try {
      const [handle] = await pickerWin.showOpenFilePicker({
        multiple: false,
        types: [
          {
            description: '宿主证书文件',
            accept: {
              'text/xml': ['.xml'],
              'application/x-x509-ca-cert': ['.cer', '.crt'],
              'application/x-pkcs12': ['.pfx']
            }
          }
        ]
      });
      if (!handle) return null;
      const file = await handle.getFile();
      const content = typeof file.text === 'function' ? await file.text().catch(() => '') : '';
      return { fileName: file.name || handle.name, content };
    } catch (err) {
      if (err?.name === 'AbortError') return null;
    }
  }
  return pickCertViaInputElement();
}

function grantHostCertificateToVtpm(vtpm, certInput) {
  if (!vtpm.kpConfigured) return { ok: false, reason: '请先启用 vTPM 或签发当前宿主密钥' };
  const { hostName, thumbprint } = parseGuardianCertPayload(certInput);
  if (!hostName) return { ok: false, reason: '请选择有效的证书文件' };
  const exists = (vtpm.authorizedHosts || []).some(
    (item) => item.name.toLowerCase() === hostName.toLowerCase() || (item.isLocal && hostName.toLowerCase() === 'saniye')
  );
  if (exists) return { ok: false, reason: `「${hostName}」宿主的证书已在授权列表中` };
  snapshotVtpmKey(vtpm);
  vtpm.authorizedHosts.push({
    id: `host-${hostName.toLowerCase().replace(/[^a-z0-9\u4e00-\u9fa5_-]/g, '-')}-${vtpm.authorizedHosts.length + 1}`,
    name: hostName,
    thumbprint: thumbprint || buildThumbprintForSeed(hostName),
    isLocal: false
  });
  return { ok: true, hostName };
}

function revokeHostFromVtpm(vtpm, hostId) {
  const idx = (vtpm.authorizedHosts || []).findIndex((item) => item.id === hostId && !item.isLocal);
  if (idx < 0) return null;
  snapshotVtpmKey(vtpm);
  const [removed] = vtpm.authorizedHosts.splice(idx, 1);
  return removed;
}

function buildVtpmInfobarHtml({
  isGen1,
  isRunning,
  isShielded,
  kpMissing,
  isSettingsView = false,
  settingsTab = 'keys'
}) {
  if (isGen1) return '';
  if (isRunning) {
    return buildDeviceInlineBannerHtml({
      severity: 'warning',
      title: isSettingsView ? '虚拟机运行中，密钥操作与安全策略已锁定' : '虚拟机运行中，vTPM 开关已锁定',
      desc: isSettingsView ? '关闭虚拟机后才可修改密钥授权与安全隔离策略' : '关闭虚拟机后才可开启或关闭 vTPM'
    });
  }
  if (kpMissing && !isSettingsView) {
    return buildDeviceInlineBannerHtml({
      severity: 'warning',
      title: '当前宿主无法解开此 vTPM',
      desc: '虚拟机可能从其他宿主拷贝而来，需重新绑定后才可启动',
      actionHtml: '<button class="winui-btn st-pinspect-fix-btn" id="vtpmRebindHostBtn" type="button">重新绑定当前宿主</button>'
    });
  }
  if (isShielded && (!isSettingsView || settingsTab === 'policy')) {
    return buildDeviceInlineBannerHtml({
      severity: 'warning',
      title: isSettingsView ? '已启用受防护模式，安全策略已锁定' : '已启用受防护模式，vTPM 已锁定开启',
      desc: isSettingsView ? '如需修改请先前往「高级 → 安全」关闭受防护模式，密钥修复不受影响' : '如需关闭请先前往「高级 → 安全」关闭受防护模式'
    });
  }
  return '';
}

export function renderVtpmWorkbench(host, ctx) {
  const {
    state,
    vm,
    INITIAL_VTPM_BY_VM = {},
    renderTitleFirstKvItemHtml,
    renderSettingLabelHtml = (title) => `<span>${escapeDeviceHtml(title)}</span>`,
    showNotice,
    onLayoutNeeded,
    bindHostControls,
    setPageTabsTrailingAction
  } = ctx;

  setPageTabsTrailingAction?.('', null);

  if (state.vtpmLastVmId !== vm.id) {
    state.vtpmLastVmId = vm.id;
    state.vtpmConfirmDisable = false;
    state.vtpmMode = 'main';
    state.vtpmSettingsTab = 'keys';
  }
  if (state.vtpmMode !== 'settings') state.vtpmMode = 'main';
  if (state.vtpmSettingsTab !== 'policy') state.vtpmSettingsTab = 'keys';

  const vtpm = ensureVtpmForVm(state, vm, INITIAL_VTPM_BY_VM);
  const adv = state.advSettings;
  const isGen1 = vm?.generation === 1 || vm?.gen === 1;
  const isRunning = Boolean(vm?.running);
  const isShielded = !isGen1 && Boolean(adv.shielding);
  const isTpmEnabled = !isGen1 && Boolean(adv.tpmEnabled);
  const isEncryptEnabled = !isGen1 && Boolean(adv.encryptState);
  const canToggleTpm = !isGen1 && !isRunning && !isShielded;
  const kpMissingWhenEnabled = isTpmEnabled && !vtpm.kpConfigured;
  const authorizedHosts = isGen1 ? [] : vtpm.authorizedHosts || [];

  const rerender = () => {
    renderVtpmWorkbench(host, ctx);
    bindHostControls?.(host);
    requestAnimationFrame(() => onLayoutNeeded?.());
  };

  // =========================================================================
  // View 2: vTPM 设置子页（密钥操作 + WMI 隐藏安全与迁移策略，与高级页无重复）
  // =========================================================================
  if (state.vtpmMode === 'settings') {
    const canModifyCommon = !isGen1 && !isRunning && !isShielded;
    const canModifyTpmPolicies = canModifyCommon && isTpmEnabled;
    const canResetKey = !isGen1 && !isRunning;
    const canRestoreKey = canResetKey && Boolean(vtpm.hasPreviousKey);
    const canExportCert = !isGen1;
    const canImportCert = canResetKey && Boolean(vtpm.kpConfigured);
    const lockReason = isGen1
      ? '仅第 2 代虚拟机支持'
      : isRunning
        ? '关闭虚拟机后可修改'
        : isShielded
          ? '已由受防护模式锁定'
          : '';

    const policySpecs = [
      {
        key: 'bindHostTpm',
        title: '绑定宿主物理 TPM',
        desc: '将解锁密钥绑定到当前宿主主板的物理 TPM 芯片，整机拷贝到其他宿主将无法启动，更换主板后也无法解锁',
        checked: isTpmEnabled && Boolean(adv.bindHostTpm),
        enabled: canModifyTpmPolicies,
        disabledTip: lockReason || '需先开启 vTPM 后才可修改',
        span2: false
      },
      {
        key: 'vbsOptOut',
        title: '禁用 VBS',
        desc: '关闭虚拟机内部的基于虚拟化的安全（VBS，如内核隔离与内存完整性），仅在兼容排障时开启',
        checked: !isGen1 && Boolean(adv.vbsOptOut),
        enabled: canModifyCommon,
        disabledTip: lockReason,
        span2: false
      },
      {
        key: 'migratableDisabled',
        title: '禁止跨宿主迁移',
        desc: '限制此虚拟机禁止在多台 Hyper-V 宿主之间进行实时迁移或导出受保护状态',
        checked: !isGen1 && Boolean(adv.migratableDisabled),
        enabled: canModifyCommon,
        disabledTip: lockReason,
        span2: true
      }
    ];

    const policyCardsHtml = policySpecs
      .map(
        (item) => `
          <div class="isa-card uniform-1row ${item.span2 ? 'span-2' : ''} ${item.enabled ? '' : 'is-disabled'}">
            <div class="isa-card-row">
              ${renderSettingLabelHtml(item.title, item.desc)}
              <div
                class="winui-switch ${item.checked ? 'on' : ''} ${item.enabled ? '' : 'disabled'}"
                data-vtpm-policy-key="${item.key}"
                data-custom-switch="true"
                role="switch"
                aria-checked="${Boolean(item.checked)}"
                aria-disabled="${!item.enabled}"
                title="${escapeDeviceHtml(item.enabled ? `切换${item.title}` : item.disabledTip)}"
              >
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
              </div>
            </div>
          </div>
        `
      )
      .join('');

    const authorizedListHtml =
      !isGen1 && authorizedHosts.length > 0
        ? `
          <div style="display:flex;flex-direction:column;gap:6px;margin-top:2px;">
            ${authorizedHosts
              .map(
                (entry) => `
                  <div style="display:flex;align-items:center;justify-content:space-between;gap:12px;padding:7px 10px;border-radius:6px;background:var(--SubtleFillColorTertiaryBrush);">
                    <div style="display:flex;align-items:center;gap:8px;min-width:0;">
                      <span style="font-size:12.5px;color:var(--TextFillColorPrimaryBrush);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${escapeDeviceHtml(entry.name)}</span>
                      <span class="nic-head-badge">${entry.isLocal ? '当前宿主' : '已授权'}</span>
                    </div>
                    <div style="display:flex;align-items:center;gap:8px;flex-shrink:0;">
                      <span style="font-family:Consolas,monospace;font-size:11.5px;color:var(--TextFillColorSecondaryBrush);">${escapeDeviceHtml(String(entry.thumbprint || '').slice(0, 12))}</span>
                      ${
                        entry.isLocal
                          ? ''
                          : `<button class="winui-btn" data-vtpm-revoke-host="${escapeDeviceHtml(entry.id)}" type="button" ${canImportCert ? '' : 'disabled'}>移除</button>`
                      }
                    </div>
                  </div>
                `
              )
              .join('')}
          </div>
        `
        : '';

    const settingsTab = state.vtpmSettingsTab === 'policy' ? 'policy' : 'keys';
    const lockBannerHtml = buildVtpmInfobarHtml({
      isGen1,
      isRunning,
      isShielded,
      kpMissing: false,
      isSettingsView: true,
      settingsTab
    });
    const settingsTabsHtml = renderPivotTrackHtml(
      `
        <button
          class="st-pivot-btn ${settingsTab === 'keys' ? 'active' : ''}"
          data-vtpm-settings-tab="keys"
          type="button"
          role="tab"
          aria-selected="${settingsTab === 'keys'}"
        >
          <span class="fi" style="font-size:14px;">&#xE72E;</span>
          <span>密钥</span>
        </button>
        <button
          class="st-pivot-btn ${settingsTab === 'policy' ? 'active' : ''}"
          data-vtpm-settings-tab="policy"
          type="button"
          role="tab"
          aria-selected="${settingsTab === 'policy'}"
        >
          <span class="fi" style="font-size:14px;">&#xE713;</span>
          <span>策略</span>
        </button>
      `,
      '<button class="winui-btn" id="vtpmSettingsBackBtn" type="button">返回</button>'
    );

    const settingsBodyHtml =
      settingsTab === 'keys'
        ? `
          <div class="isa-grid-2x2">
            <div class="isa-card uniform-1row span-2 ${canResetKey ? '' : 'is-disabled'}">
              <div class="isa-card-row">
                ${renderSettingLabelHtml(
                  '重新签发',
                  '使用当前宿主证书重新生成 vTPM 解锁密钥，适用于从其他宿主迁移后无法开机时修复。若系统内启用了 BitLocker，下次开机需要输入恢复密钥'
                )}
                <button class="winui-btn" id="vtpmResetLocalKeyBtn" type="button" ${canResetKey ? '' : 'disabled'}>签发</button>
              </div>
            </div>

            <div class="isa-card uniform-1row span-2 ${canRestoreKey ? '' : 'is-disabled'}">
              <div class="isa-card-row">
                ${renderSettingLabelHtml(
                  '撤销上次修改',
                  '恢复到上一次修改或重签之前还能正常启动的解锁密钥'
                )}
                <button class="winui-btn" id="vtpmRestoreKeyBtn" type="button" ${canRestoreKey ? '' : 'disabled'}>还原</button>
              </div>
            </div>

            <div class="isa-card uniform-1row span-2 ${canExportCert ? '' : 'is-disabled'}">
              <div class="isa-card-row">
                ${renderSettingLabelHtml(
                  '授权其他宿主启动',
                  '导出当前宿主证书供其他宿主导入，或选择其他宿主导出的证书文件以允许其直接启动此虚拟机'
                )}
                <div style="display:flex;align-items:center;gap:8px;flex-shrink:0;">
                  <button class="winui-btn" id="vtpmExportCertBtn" type="button" ${canExportCert ? '' : 'disabled'}>导出宿主证书</button>
                  <button class="winui-btn" id="vtpmImportCertBtn" type="button" ${canImportCert ? '' : 'disabled'}>添加授权</button>
                </div>
              </div>
              ${authorizedListHtml}
            </div>
          </div>
        `
        : `
          <div class="isa-grid-2x2">
            ${policyCardsHtml}
          </div>
        `;

    host.innerHTML = `
      <div class="dev-stack">
        ${settingsTabsHtml}
        ${settingsBodyHtml}
        ${lockBannerHtml}
      </div>
    `;

    host.querySelectorAll('[data-vtpm-settings-tab]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const nextTab = btn.getAttribute('data-vtpm-settings-tab');
        if (!nextTab || nextTab === state.vtpmSettingsTab) return;
        state.vtpmSettingsTab = nextTab;
        rerender();
      });
    });

    host.querySelector('#vtpmResetLocalKeyBtn')?.addEventListener('click', () => {
      if (!canResetKey) return;
      bindVtpmToCurrentHost(vtpm, vm);
      rerender();
      showNotice?.('已用当前宿主重新签发解锁密钥');
    });

    host.querySelector('#vtpmRestoreKeyBtn')?.addEventListener('click', () => {
      if (!canRestoreKey) return;
      if (restoreVtpmPreviousKey(vtpm)) {
        rerender();
        showNotice?.('已恢复到上一次可用的解锁密钥');
      }
    });

    host.querySelector('#vtpmExportCertBtn')?.addEventListener('click', async () => {
      if (!canExportCert) return;
      const suggestedName = 'Saniye-Guardian.xml';
      const xmlText = buildVtpmGuardianCertXml(vtpm);
      const saver = typeof ctx.saveVtpmCertFile === 'function' ? ctx.saveVtpmCertFile : saveVtpmGuardianCertFile;
      const result = await saver(suggestedName, xmlText);
      if (!result || result.cancelled || !result.ok) return;
      showNotice?.(`已导出当前宿主证书：${result.fileName || suggestedName}`);
    });

    host.querySelector('#vtpmImportCertBtn')?.addEventListener('click', async () => {
      if (!canImportCert) return;
      const picker = typeof ctx.pickVtpmCertFile === 'function' ? ctx.pickVtpmCertFile : openVtpmGuardianCertFile;
      const picked = await picker();
      if (!picked) return;
      const res = grantHostCertificateToVtpm(vtpm, picked);
      if (!res.ok) {
        showNotice?.(res.reason, 'warning');
        return;
      }
      rerender();
      showNotice?.(`已授权「${res.hostName}」启动此虚拟机`);
    });

    host.querySelectorAll('[data-vtpm-revoke-host]').forEach((btn) => {
      btn.addEventListener('click', () => {
        if (!canImportCert) return;
        const targetId = btn.getAttribute('data-vtpm-revoke-host');
        const removed = revokeHostFromVtpm(vtpm, targetId);
        if (removed) {
          rerender();
          showNotice?.(`已移除「${removed.name}」的启动授权`);
        }
      });
    });

    host.querySelectorAll('[data-vtpm-policy-key]').forEach((sw) => {
      sw.addEventListener('click', () => {
        const key = sw.getAttribute('data-vtpm-policy-key');
        const spec = policySpecs.find((p) => p.key === key);
        if (!spec || !spec.enabled) return;
        adv[key] = !adv[key];
        rerender();
        showNotice?.('已应用');
      });
    });

    host.querySelector('#vtpmSettingsBackBtn')?.addEventListener('click', () => {
      state.vtpmMode = 'main';
      rerender();
    });

    requestAnimationFrame(() => onLayoutNeeded?.());
    return;
  }

  // =========================================================================
  // View 1: vTPM 主视图（精简状态卡 + 齿轮进设置页）
  // =========================================================================
  const subParts = [];
  if (isGen1) {
    subParts.push('第 1 代虚拟机不支持');
  } else if (isTpmEnabled && vtpm.kpConfigured) {
    if (authorizedHosts.length > 1) {
      subParts.push(`已授权 ${authorizedHosts.length} 个宿主`);
    }
  } else if (kpMissingWhenEnabled) {
    subParts.push('缺少当前宿主密钥');
  } else {
    subParts.push('未启用');
  }
  const subtitleHtml = subParts.length
    ? `<div class="dev-b-sub ${kpMissingWhenEnabled ? 'is-caution' : ''}">${subParts
        .map((part) => `<span class="dev-b-sub-part">${escapeDeviceHtml(part)}</span>`)
        .join('')}</div>`
    : '';

  const inlineDisableConfirmHtml =
    state.vtpmConfirmDisable && canToggleTpm && isTpmEnabled
      ? `
        <div class="dev-inline-danger-box" data-stop-prop>
          <div class="dev-inline-danger-info">
            <span class="winui-floating-banner-badge is-warning" style="margin-top:1px;flex-shrink:0;">${ST_PARENT_BADGE_GLYPHS.warning}</span>
            <div class="dev-inline-danger-text">
              <div class="dev-inline-danger-title">确认关闭 vTPM？</div>
              <div class="dev-inline-danger-path" style="white-space:normal;">${
                isEncryptEnabled
                  ? '关闭将同时关闭「状态与迁移加密」，且系统内 BitLocker 可能要求恢复密钥。'
                  : '关闭后系统内依赖 vTPM 的 BitLocker 或安全解锁功能将受影响。'
              }</div>
            </div>
          </div>
          <div class="dev-inline-danger-actions">
            <button class="winui-btn" id="vtpmDisableCancelBtn" type="button">取消</button>
            <button class="winui-btn danger" id="vtpmDisableConfirmBtn" type="button">确认关闭</button>
          </div>
        </div>
      `
      : '';

  const boundHostText = isGen1 ? '不支持' : vtpm.kpConfigured ? vtpm.boundHost || '当前宿主' : '未绑定';
  const vmgsPath = !isGen1 && vtpm.guestStateFile ? vtpm.guestStateFile : '';
  const vmgsText = isGen1 ? '不适用' : vmgsPath || '未生成';

  const switchTooltip = isGen1
    ? '仅第 2 代虚拟机支持 vTPM'
    : isRunning
      ? '关闭虚拟机后可修改'
      : isShielded
        ? '已由受防护模式锁定开启'
        : isTpmEnabled
          ? '关闭 vTPM'
          : '启用 vTPM';

  host.innerHTML = `
    <div class="dev-stack">
      <div class="dev-b-card is-expanded">
        <div class="dev-b-head is-static">
          <div class="tc-head-icon-box ${isTpmEnabled ? '' : 'is-dimmed'}" style="width:38px;height:38px;font-size:17px;">
            <span class="fi fi-fluent">&#xEAD1;</span>
          </div>
          <div class="dev-b-identity">
            <div class="dev-b-title">vTPM</div>
            ${subtitleHtml}
          </div>
          <div class="dev-b-actions">
            <button
              class="round-icon-btn"
              id="vtpmOpenSettingsBtn"
              title="配置 vTPM 密钥与高级安全设置"
              type="button"
            >
              <span class="fi" style="font-size:15px;">&#xE713;</span>
            </button>
            <div
              class="winui-switch ${isTpmEnabled ? 'on' : ''} ${canToggleTpm ? '' : 'disabled'}"
              id="vtpmEnableSwitch"
              data-custom-switch="true"
              role="switch"
              aria-checked="${isTpmEnabled}"
              title="${escapeDeviceHtml(switchTooltip)}"
            >
              <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
            </div>
          </div>
        </div>
        ${inlineDisableConfirmHtml}
        <div class="dev-b-body">
          <div class="dt-kv-grid is-title-first is-2col">
            ${renderTitleFirstKvItemHtml({
              title: '绑定宿主',
              stateText: escapeDeviceHtml(boundHostText),
              copyVal: !isGen1 && vtpm.kpConfigured ? escapeDeviceHtml(vtpm.signingThumbprint) : '',
              wide: true
            })}
            ${renderTitleFirstKvItemHtml({
              title: '状态文件',
              stateText: escapeDeviceHtml(vmgsText),
              copyVal: vmgsPath ? escapeDeviceHtml(vmgsPath) : '',
              revealVal: vmgsPath ? escapeDeviceHtml(vmgsPath) : '',
              wide: true
            })}
          </div>
        </div>
      </div>

      ${buildVtpmInfobarHtml({
        isGen1,
        isRunning,
        isShielded,
        kpMissing: kpMissingWhenEnabled
      })}
    </div>
  `;

  host.querySelectorAll('[data-stop-prop]').forEach((el) => {
    el.addEventListener('click', (e) => e.stopPropagation());
  });

  host.querySelector('#vtpmOpenSettingsBtn')?.addEventListener('click', () => {
    state.vtpmConfirmDisable = false;
    state.vtpmMode = 'settings';
    state.vtpmSettingsTab = 'keys';
    rerender();
  });

  host.querySelector('#vtpmEnableSwitch')?.addEventListener('click', () => {
    if (!canToggleTpm) return;
    if (isTpmEnabled) {
      state.vtpmConfirmDisable = !state.vtpmConfirmDisable;
      rerender();
      return;
    }
    adv.tpmEnabled = true;
    ensureVtpmHostKeyForVm(state, vm, INITIAL_VTPM_BY_VM);
    state.vtpmConfirmDisable = false;
    rerender();
    showNotice?.('已启用 vTPM');
  });

  host.querySelector('#vtpmDisableCancelBtn')?.addEventListener('click', () => {
    state.vtpmConfirmDisable = false;
    rerender();
  });

  host.querySelector('#vtpmDisableConfirmBtn')?.addEventListener('click', () => {
    if (!canToggleTpm) return;
    adv.tpmEnabled = false;
    adv.encryptState = false;
    adv.bindHostTpm = false;
    state.vtpmConfirmDisable = false;
    rerender();
    showNotice?.('已关闭 vTPM');
  });

  host.querySelector('#vtpmRebindHostBtn')?.addEventListener('click', () => {
    if (isGen1 || isRunning) return;
    bindVtpmToCurrentHost(vtpm, vm);
    rerender();
    showNotice?.('已重新绑定到当前宿主');
  });

  requestAnimationFrame(() => onLayoutNeeded?.());
}

// ============================================================================
// PCIe Workbench (对齐存储物理磁盘直通交互：单机列表 + 全局 PCIe 设备池选择)
// ============================================================================
function resolvePcieClassGlyph(classType = '') {
  const t = String(classType).toLowerCase();
  if (t.includes('display') || t.includes('gpu')) return '&#xF211;';
  if (t.includes('disk') || t.includes('nvme') || t.includes('storage')) return '&#xEC59;';
  if (t.includes('net')) return '&#xE774;';
  if (t.includes('usb')) return '&#xECF0;';
  if (t.includes('media') || t.includes('audio') || t.includes('sound')) return '&#xE995;';
  return '&#xF0F5;';
}

function resolvePcieClassLabel(classType = '') {
  const t = String(classType).toLowerCase();
  if (t.includes('display')) return '显示适配器';
  if (t.includes('disk')) return '存储控制器';
  if (t.includes('net')) return '网络适配器';
  if (t.includes('usb')) return 'USB 控制器';
  if (t.includes('media') || t.includes('audio') || t.includes('sound')) return '声音与音频控制器';
  return classType || 'PCIe 设备';
}

export function renderPcieWorkbench(host, ctx) {
  const {
    state,
    vm,
    vmItems = [],
    HOST_PCIE_DEVICES = [],
    renderTitleFirstKvItemHtml,
    renderSettingLabelHtml,
    showNotice,
    onLayoutNeeded,
    bindHostControls,
    setPageTabsTrailingAction
  } = ctx;

  if (ctx.isLive) {
    state.hostPcieDevices = JSON.parse(JSON.stringify(HOST_PCIE_DEVICES));
  } else if (!Array.isArray(state.hostPcieDevices)) {
    state.hostPcieDevices = JSON.parse(JSON.stringify(HOST_PCIE_DEVICES));
  }
  if (!state.pcieDevicesByVm) state.pcieDevicesByVm = {};
  if (!Array.isArray(state.pcieDevicesByVm[vm.id])) state.pcieDevicesByVm[vm.id] = [];
  if (!state.pcieSettingsByVm) state.pcieSettingsByVm = {};
  if (!state.pcieSettingsByVm[vm.id]) {
    state.pcieSettingsByVm[vm.id] = { emulationEnabled: true, topology: '0', bootVpci: false };
  }

  const hostPool = state.hostPcieDevices;
  const devices = state.pcieDevicesByVm[vm.id];
  const running = Boolean(vm.running);
  const mode = state.pcieMode === 'settings' ? 'list' : (state.pcieMode || 'list');

  setPageTabsTrailingAction?.('', null);

  const rerender = () => {
    renderPcieWorkbench(host, ctx);
    bindHostControls?.(host);
    requestAnimationFrame(() => onLayoutNeeded?.());
  };

  const clearPcieTimer = () => {
    if (state.pcieTaskTimer) {
      clearTimeout(state.pcieTaskTimer);
      state.pcieTaskTimer = null;
    }
  };

  const assignDeviceToTarget = (hd, targetKind, targetVmId = '') => {
    if (hd.ownerKind === 'vm' && hd.ownerVmId && Array.isArray(state.pcieDevicesByVm[hd.ownerVmId])) {
      state.pcieDevicesByVm[hd.ownerVmId] = state.pcieDevicesByVm[hd.ownerVmId].filter(
        (x) => !(x.path && hd.path && x.path.toLowerCase() === hd.path.toLowerCase())
      );
    }
    if (targetKind === 'host') {
      hd.ownerKind = 'host';
      hd.ownerVmId = '';
      hd.ownerVmName = '';
      return;
    }
    const targetVm = vmItems.find((v) => v.id === targetVmId) || vm;
    hd.ownerKind = 'vm';
    hd.ownerVmId = targetVm.id;
    hd.ownerVmName = targetVm.name;
    if (!Array.isArray(state.pcieDevicesByVm[targetVm.id])) state.pcieDevicesByVm[targetVm.id] = [];
    state.pcieDevicesByVm[targetVm.id].push({
      id: `pcie-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`,
      hostId: hd.id,
      name: hd.name,
      classType: hd.classType,
      glyph: hd.glyph || '\uF0F5',
      guestMode: '0',
      instanceId: hd.instanceId,
      path: hd.path,
      vendor: hd.vendor,
      expanded: false
    });
  };

  const attachCompanionDevicesToVm = (parentHd, targetVmId) => {
    if (!Array.isArray(parentHd?.companions) || parentHd.companions.length === 0) return [];
    if (!Array.isArray(state.pcieDevicesByVm[targetVmId])) state.pcieDevicesByVm[targetVmId] = [];
    const addedNames = [];
    parentHd.companions.forEach((comp) => {
      const compPathLower = String(comp.path || '').toLowerCase();
      if (!compPathLower) return;
      Object.keys(state.pcieDevicesByVm).forEach((vid) => {
        if (vid === targetVmId || !Array.isArray(state.pcieDevicesByVm[vid])) return;
        state.pcieDevicesByVm[vid] = state.pcieDevicesByVm[vid].filter(
          (x) => String(x.path || '').toLowerCase() !== compPathLower
        );
      });
      const alreadyInTarget = state.pcieDevicesByVm[targetVmId].some(
        (x) => String(x.path || '').toLowerCase() === compPathLower
      );
      if (alreadyInTarget) return;
      state.pcieDevicesByVm[targetVmId].push({
        id: `pcie-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`,
        hostId: comp.id,
        name: comp.name,
        classType: comp.classType || 'MEDIA',
        glyph: comp.glyph || '\uE995',
        guestMode: '0',
        instanceId: comp.instanceId,
        path: comp.path,
        vendor: comp.vendor || parentHd.vendor,
        expanded: false
      });
      addedNames.push(comp.name);
    });
    return addedNames;
  };

  const schedulePcieQueueExecution = () => {
    clearPcieTimer();
    state.pcieTaskTimer = setTimeout(() => {
      const curP = state.pcieProgress;
      if (!curP || state.pcieMode !== 'progress') return;

      const succeeded = [];
      const failures = [];
      curP.queueIds.forEach((id) => {
        const hd = hostPool.find((x) => x.id === id);
        if (!hd) return;
        const failReason = String(hd.passthroughError || '').trim();
        if (failReason) {
          failures.push({ name: hd.name, reason: failReason });
          return;
        }
        assignDeviceToTarget(hd, 'vm', vm.id);
        succeeded.push(hd.name);
        if (curP.approveCompanions) {
          succeeded.push(...attachCompanionDevicesToVm(hd, vm.id));
        }
      });

      curP.succeededNames = succeeded;
      curP.failures = failures;
      curP.failed = failures.length > 0 && succeeded.length === 0;
      curP.partialFailed = failures.length > 0 && succeeded.length > 0;
      curP.activeIdx = curP.failed ? 2 : curP.steps.length;
      curP.done = true;
      if (failures.length === 0) {
        clearPcieTimer();
        state.pcieProgress = null;
        state.pcieSelectedHostIds = [];
        state.pcieMode = 'list';
        rerender();
        showNotice?.(`已为 ${vm.name} 添加 ${succeeded.length} 个 PCIe 设备`);
        return;
      }
      rerender();

      if (failures.length > 0) {
        const header =
          succeeded.length > 0
            ? `已为 ${vm.name} 分配 ${succeeded.length} 个设备，${failures.length} 个设备直通失败`
            : `${failures.length === 1 ? failures[0].name : `${failures.length} 个设备`} 直通失败`;
        const details = failures.map((f) => `${f.name}：${f.reason}`);
        const noticeText =
          failures.length === 1 && succeeded.length === 0
            ? `${failures[0].name} 直通失败：${failures[0].reason}`
            : `${header}\n${details.join('\n')}`;
        showNotice?.(noticeText, 'error');
      }
    }, 600);
  };

  const startPcieAssignFlow = (selectedHostObjs) => {
    clearPcieTimer();
    const queueIds = selectedHostObjs.map((x) => x.id);
    const companionItems = [];
    const seenCompanionPaths = new Set(devices.map((d) => String(d.path || '').toLowerCase()).filter(Boolean));
    selectedHostObjs.forEach((hd) => {
      if (!Array.isArray(hd.companions)) return;
      hd.companions.forEach((comp) => {
        const key = String(comp.path || comp.name || '').toLowerCase();
        if (!key || seenCompanionPaths.has(key)) return;
        seenCompanionPaths.add(key);
        companionItems.push({ ...comp, sourceName: hd.name });
      });
    });

    state.pcieMode = 'progress';
    state.pcieProgress = {
      steps: ['关闭虚拟机', '参数调整', '添加设备'],
      activeIdx: 0,
      done: false,
      failed: false,
      partialFailed: false,
      succeededNames: [],
      failures: [],
      decision: null, // 'PowerOff' | 'ConfirmCompanions'
      powerChecked: false,
      mmioChecked: false,
      safetyChecked: false,
      queueIds,
      companions: companionItems,
      companionPrompted: false,
      approveCompanions: false
    };
    rerender();

    const stepTick = () => {
      const p = state.pcieProgress;
      if (!p || state.pcieMode !== 'progress' || p.decision || p.done || p.failed) return;

      if (p.activeIdx === 0 && !p.powerChecked) {
        p.powerChecked = true;
        if (vm.running) {
          p.decision = 'PowerOff';
          rerender();
          return;
        }
      }

      if (p.activeIdx === 1 && !p.mmioChecked) {
        p.mmioChecked = true;
        if (!state.pcieMmioExpandedByVm) state.pcieMmioExpandedByVm = {};
        state.pcieMmioExpandedByVm[vm.id] = true;
      }

      if (p.activeIdx === 2 && !p.safetyChecked) {
        p.safetyChecked = true;
        if (!p.companionPrompted && p.companions.length > 0) {
          p.companionPrompted = true;
          p.decision = 'ConfirmCompanions';
          rerender();
          return;
        }
      }

      if (p.activeIdx === 2) {
        rerender();
        schedulePcieQueueExecution();
        return;
      }

      p.activeIdx += 1;
      rerender();
      state.pcieTaskTimer = setTimeout(stepTick, 450);
    };

    state.pcieTaskTimer = setTimeout(stepTick, 450);
  };

  // =========================================================================
  // View 3: PCIe 分配进度与交互确认视图（对齐 GPU 驱动部署 .dev-task-list）
  // =========================================================================
  if (mode === 'progress' && state.pcieProgress) {
    const p = state.pcieProgress;

    let inlineActionsStepIdx = -1;
    let inlineActionsHtml = '';
    let decisionBoxHtml = '';

    if (p.decision === 'PowerOff') {
      inlineActionsStepIdx = 0;
      inlineActionsHtml = `
        <div class="dev-action-row" style="margin-top:0;">
          <button class="winui-btn" id="pcieFlowCancelBtn" type="button">返回</button>
          <button class="winui-btn accent" id="pcieFlowPowerOffBtn" type="button">关机并继续</button>
        </div>
      `;
    } else if (p.decision === 'ConfirmCompanions') {
      const companionRowsHtml = (p.companions || [])
        .map(
          (comp) => `
            <div class="dev-task-row" style="padding:10px 14px;column-gap:12px;">
              <div class="dev-task-icon"><span class="fi" style="font-size:16px;color:var(--TextFillColorPrimaryBrush);">${resolvePcieClassGlyph(comp.classType)}</span></div>
              <div style="min-width:0;">
                <div style="font-size:13.5px;font-weight:400;color:var(--TextFillColorPrimaryBrush);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${escapeDeviceHtml(comp.name)}</div>
                <div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);margin-top:2px;">与 ${escapeDeviceHtml(comp.sourceName)} 同一设备</div>
              </div>
              <span class="dev-picker-tag">关联设备</span>
            </div>
          `
        )
        .join('');
      decisionBoxHtml = `
        <div class="dev-inline-decision">
          <div style="font-size:13px;font-weight:600;color:var(--TextFillColorPrimaryBrush);">检测到关联设备，是否一并分配给 ${escapeDeviceHtml(vm.name)}？</div>
          <div class="dev-picker-list">${companionRowsHtml}</div>
          <div class="dev-action-row" style="justify-content:flex-end;">
            <button class="winui-btn" id="pcieFlowGpuOnlyBtn" type="button">跳过</button>
            <button class="winui-btn accent" id="pcieFlowContinueBtn" type="button">确认</button>
          </div>
        </div>
      `;
    }

    const rowsHtml = p.steps
      .map((label, idx) => {
        const isLastStep = idx === p.steps.length - 1;
        const failedStep = isLastStep && p.failed;
        const partialStep = isLastStep && p.partialFailed;
        const hasStepError = failedStep || partialStep;
        const done = !hasStepError && (p.done || idx < p.activeIdx);
        const active = !p.done && idx === p.activeIdx;
        const iconHtml = hasStepError
          ? '<span class="fi" style="font-size:17px;color:#F1707A;">&#xEA39;</span>'
          : done
            ? '<span class="fi" style="font-size:18px;color:#3CCB5F;">&#xEC61;</span>'
            : active
              ? '<span class="dev-spinner" aria-hidden="true"></span>'
              : '<span class="fi" style="font-size:17px;color:var(--TextFillColorTertiaryBrush);">&#xEA3A;</span>';
        const rowRightHtml =
          active && idx === inlineActionsStepIdx
            ? inlineActionsHtml
            : partialStep
              ? `<span class="dev-status-muted">部分失败（已分配 ${p.succeededNames.length} 个）</span>`
              : failedStep
                ? '<span class="dev-status-muted">直通失败</span>'
                : '';
        const inlineHtml = active && decisionBoxHtml ? decisionBoxHtml : '';
        return `
          <div class="dev-task-row ${active || hasStepError ? 'is-active' : done ? 'is-done' : 'is-pending'}">
            <div class="dev-task-icon">${iconHtml}</div>
            <div style="font-size:14.5px;font-weight:600;color:var(--TextFillColorPrimaryBrush);min-width:0;">${escapeDeviceHtml(label)}</div>
            ${rowRightHtml}
          </div>
          ${inlineHtml}
        `;
      })
      .join('');

    const hasInlineFooter = Boolean(p.decision);
    host.innerHTML = `
      <div class="dev-stack">
        <div class="dev-task-list">${rowsHtml}</div>
        ${
          p.done
            ? `
              <div class="dev-action-row" style="justify-content:flex-end;">
                <button class="winui-btn accent" id="pcieFlowDoneBtn" type="button">返回</button>
              </div>
            `
            : !hasInlineFooter
              ? `
                <div class="dev-action-row" style="justify-content:flex-end;">
                  <button class="winui-btn" id="pcieFlowCancelBtn" type="button">返回</button>
                </div>
              `
              : ''
        }
      </div>
    `;

    const advanceAfterDecision = () => {
      p.decision = null;
      if (p.activeIdx === 0) {
        vm.running = false;
        p.activeIdx = 1;
        rerender();
        state.pcieTaskTimer = setTimeout(() => {
          const curP = state.pcieProgress;
          if (!curP || state.pcieMode !== 'progress') return;
          curP.mmioChecked = true;
          curP.activeIdx = 2;
          advanceAfterDecision();
        }, 450);
        return;
      }

      if (!p.companionPrompted && p.companions.length > 0) {
        p.safetyChecked = true;
        p.companionPrompted = true;
        p.decision = 'ConfirmCompanions';
        rerender();
        return;
      }

      rerender();
      schedulePcieQueueExecution();
    };

    host.querySelector('#pcieFlowPowerOffBtn')?.addEventListener('click', () => {
      advanceAfterDecision();
    });

    host.querySelector('#pcieFlowGpuOnlyBtn')?.addEventListener('click', () => {
      p.approveCompanions = false;
      advanceAfterDecision();
    });

    host.querySelector('#pcieFlowContinueBtn')?.addEventListener('click', () => {
      if (p.decision === 'ConfirmCompanions') {
        p.approveCompanions = true;
      }
      advanceAfterDecision();
    });

    const exitProgress = (toMode = 'list') => {
      clearPcieTimer();
      state.pcieProgress = null;
      state.pcieSelectedHostIds = [];
      state.pcieMode = toMode;
      rerender();
    };

    host.querySelector('#pcieFlowCancelBtn')?.addEventListener('click', () => exitProgress('add'));
    host.querySelector('#pcieFlowDoneBtn')?.addEventListener('click', () => exitProgress('list'));

    requestAnimationFrame(() => onLayoutNeeded?.());
    return;
  }

  // =========================================================================
  // View 2: 从宿主机 PCIe 设备池分配（完全对齐存储「物理磁盘」选择器）
  // =========================================================================
  if (mode === 'add') {
    const isOtherVmRunning = (hd) => {
      if (hd.ownerKind !== 'vm' || !hd.ownerVmId || hd.ownerVmId === vm.id) return false;
      const other = vmItems.find((v) => v.id === hd.ownerVmId);
      return Boolean(other?.running);
    };

    if (!Array.isArray(state.pcieSelectedHostIds)) state.pcieSelectedHostIds = [];

    const isAttachedToCurVm = (hd) =>
      (hd.ownerKind === 'vm' && hd.ownerVmId === vm.id) ||
      devices.some(
        (d) =>
          (d.hostId && d.hostId === hd.id) ||
          (d.path && hd.path && d.path.toLowerCase() === hd.path.toLowerCase())
      );

    const canSelectHostDevice = (hd) => {
      if (hd.canPassthrough === false) return false;
      if (isAttachedToCurVm(hd)) return false;
      if (isOtherVmRunning(hd)) return false;
      return true;
    };

    const validSelectedSet = new Set(
      state.pcieSelectedHostIds.filter((id) => {
        const hd = hostPool.find((x) => x.id === id);
        return hd && canSelectHostDevice(hd);
      })
    );
    state.pcieSelectedHostIds = [...validSelectedSet];

    const sortedHostPool = [...hostPool].sort((a, b) => {
      const aCan = canSelectHostDevice(a) ? 0 : 1;
      const bCan = canSelectHostDevice(b) ? 0 : 1;
      if (aCan !== bCan) return aCan - bCan;
      return String(a.name || '').localeCompare(String(b.name || ''), 'en', {
        sensitivity: 'base',
        numeric: true
      });
    });

    const poolCardsHtml = sortedHostPool
      .map((hd) => {
        const expanded = hd.poolExpanded === true;
        const attachedHere = isAttachedToCurVm(hd);
        const otherRunning = isOtherVmRunning(hd);
        const selectable = canSelectHostDevice(hd);
        const sel = selectable && validSelectedSet.has(hd.id);

        let tagHtml = '';
        if (attachedHere) {
          tagHtml = `<span class="dev-picker-tag">使用中</span>`;
        } else if (hd.canPassthrough === false) {
          const reasonText = hd.unavailabilityReason || '当前不可作为直通设备';
          tagHtml = `<span class="dev-picker-tag-wrap" data-stop-prop><span class="dev-picker-tag">不可直通</span><button class="setting-info-btn" style="width:18px;height:18px;padding:0;" data-info-desc="${escapeDeviceHtml(reasonText)}" title="${escapeDeviceHtml(reasonText)}" type="button"><span class="fi" style="font-size:14px;">&#xE946;</span></button></span>`;
        } else if (hd.ownerKind === 'vm') {
          const vmName = hd.ownerVmName || hd.ownerVmId;
          tagHtml = otherRunning
            ? `<span class="dev-picker-tag" title="当前由 ${escapeDeviceHtml(vmName)} 使用（运行中，请先关机）">其他虚拟机使用中（运行中）</span>`
            : `<span class="dev-picker-tag" title="当前由 ${escapeDeviceHtml(vmName)} 使用，选中后可转移到当前虚拟机">其他虚拟机使用中</span>`;
        } else if (hd.ownerKind === 'unassigned') {
          tagHtml = `<span class="dev-picker-tag">待分配</span>`;
        } else {
          tagHtml = `<span class="dev-picker-tag">宿主使用中</span>`;
        }

        const kvItems = [
          renderTitleFirstKvItemHtml({ title: '类别', stateText: hd.classType || '---' }),
          renderTitleFirstKvItemHtml({ title: '制造商', stateText: hd.vendor || '---' }),
          renderTitleFirstKvItemHtml({ title: '实例 ID', stateText: hd.instanceId || '---', copyVal: hd.instanceId || '', wide: true }),
          renderTitleFirstKvItemHtml({ title: '路径', stateText: hd.path || '---', copyVal: hd.path || '', wide: true })
        ].join('');

        return `
          <div class="dev-b-card ${expanded ? 'is-expanded' : ''} ${sel ? 'is-selected' : ''} ${!selectable ? 'is-disabled' : ''}" ${sel ? 'style="border-color:rgba(var(--VmAccentRgb),0.45);background:rgba(255,255,255,0.045);"' : ''}>
            <div class="dev-b-head" ${selectable ? `data-pcie-pick="${escapeDeviceHtml(hd.id)}" style="cursor:pointer;"` : ''}>
              <div class="${sel ? 'is-selected' : ''}" style="display:flex;align-items:center;gap:12px;flex-shrink:0;">
                <span class="dev-checkbox" style="${selectable ? '' : 'opacity:0.35;'}">${sel ? '<span class="fi">&#xE73E;</span>' : ''}</span>
                <div class="tc-head-icon-box" style="width:38px;height:38px;font-size:17px;">
                  <span class="fi">${resolvePcieClassGlyph(hd.classType)}</span>
                </div>
              </div>
              <div class="dev-b-identity">
                <div class="dev-b-title" style="font-weight:400;">${escapeDeviceHtml(hd.name)}</div>
              </div>
              <div class="dev-b-actions">
                ${tagHtml}
                <button class="round-icon-btn" data-stop-prop data-pcie-pool-toggle="${escapeDeviceHtml(hd.id)}" type="button" title="${expanded ? '收起详情' : '展开详情'}" style="width:28px;height:28px;">
                  <span class="fi" style="font-size:12px;">${expanded ? '&#xE70E;' : '&#xE70D;'}</span>
                </button>
              </div>
            </div>
            ${expanded ? `<div class="dev-b-body"><div class="dt-kv-grid is-title-first is-2col">${kvItems}</div></div>` : ''}
          </div>
        `;
      })
      .join('');

    const selectedHostObjs = hostPool.filter((hd) => validSelectedSet.has(hd.id));

    setPageTabsTrailingAction?.(
      `<button class="winui-btn" id="pcieAddBackBtn" type="button">返回</button><button class="winui-btn accent" id="pcieAddConfirmBtn" type="button" ${selectedHostObjs.length === 0 ? 'disabled' : ''}>添加</button>`,
      null
    );

    host.innerHTML = `
      <div class="dev-stack">
        <div style="display:flex;flex-direction:column;gap:8px;">
          ${poolCardsHtml}
        </div>
      </div>
    `;

    host.querySelectorAll('[data-stop-prop]').forEach((el) => {
      el.addEventListener('click', (e) => e.stopPropagation());
    });

    host.querySelectorAll('[data-pcie-pick]').forEach((el) => {
      el.addEventListener('click', (e) => {
        if (e.target.closest('[data-stop-prop]')) return;
        const id = el.getAttribute('data-pcie-pick');
        if (!id) return;
        const cur = state.pcieSelectedHostIds;
        state.pcieSelectedHostIds = cur.includes(id) ? cur.filter((x) => x !== id) : [...cur, id];
        rerender();
      });
    });

    host.querySelectorAll('[data-pcie-pool-toggle]').forEach((el) => {
      el.addEventListener('click', (e) => {
        e.stopPropagation();
        const id = el.getAttribute('data-pcie-pool-toggle');
        const target = hostPool.find((x) => x.id === id);
        if (!target) return;
        target.poolExpanded = !target.poolExpanded;
        rerender();
      });
    });

    document.getElementById('pcieAddConfirmBtn')?.addEventListener('click', () => {
      if (selectedHostObjs.length === 0) return;
      startPcieAssignFlow(selectedHostObjs);
    });

    host.querySelectorAll('[data-pcie-pool-return]').forEach((btn) => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const id = btn.getAttribute('data-pcie-pool-return');
        const hd = hostPool.find((x) => x.id === id);
        if (!hd) return;
        assignDeviceToTarget(hd, 'host');
        state.pcieSelectedHostIds = state.pcieSelectedHostIds.filter((x) => x !== id);
        rerender();
      });
    });

    document.getElementById('pcieAddBackBtn')?.addEventListener('click', () => {
      state.pcieSelectedHostIds = [];
      state.pcieMode = 'list';
      rerender();
    });

    bindHostControls?.(host);
    requestAnimationFrame(() => onLayoutNeeded?.());
    return;
  }

  // =========================================================================
  // View 1: PCIe 列表主视图（.dev-b-card + .dev-add-card-row）
  // =========================================================================
  const addRowHtml = (isEmpty) => `
    <button class="dev-add-card-row ${isEmpty ? 'is-empty' : ''}" id="pcieOpenAddBtn" type="button" ${running ? 'disabled title="请先关闭虚拟机后再分配 PCIe 设备"' : ''}>
      <div class="dev-add-card-left">
        <div class="dev-add-card-badge"><span class="fi">&#xE710;</span></div>
        <div>
          <div class="dev-add-card-title">添加 PCIe 设备</div>
        </div>
      </div>
    </button>
  `;

  const cardsHtml =
    devices.length === 0
      ? addRowHtml(true)
      : devices
          .map((d, idx) => {
            const expanded = d.expanded === true;

            const kvItems = [
              renderTitleFirstKvItemHtml({ title: '类别', stateText: d.classType || '---' }),
              renderTitleFirstKvItemHtml({ title: '制造商', stateText: d.vendor || '---' }),
              renderTitleFirstKvItemHtml({ title: '实例 ID', stateText: d.instanceId || '---', copyVal: d.instanceId || '', wide: true }),
              renderTitleFirstKvItemHtml({ title: 'PCIe 路径', stateText: d.path || '---', copyVal: d.path || '', wide: true })
            ].join('');

            return `
              <div class="dev-b-card ${expanded ? 'is-expanded' : ''}">
                <div class="dev-b-head" data-pcie-toggle="${idx}">
                  <div class="tc-head-icon-box" style="width:38px;height:38px;font-size:17px;">
                    <span class="fi">${resolvePcieClassGlyph(d.classType)}</span>
                  </div>
                  <div class="dev-b-identity">
                    <div class="dev-b-title" style="font-weight:400;">${escapeDeviceHtml(d.name)}</div>
                  </div>
                  <div class="dev-b-actions" data-stop-prop>
                    <button class="round-icon-btn" title="${running ? '关闭虚拟机后可断开直通' : '断开直通并归还宿主机'}" data-pcie-remove="${idx}" ${running ? 'disabled' : ''} type="button">
                      <span class="fi fi-fluent" style="font-size:16px;">&#xF60A;</span>
                    </button>
                    <span class="dev-b-chevron"><span class="fi">${expanded ? '&#xE70E;' : '&#xE70D;'}</span></span>
                  </div>
                </div>
                ${
                  expanded
                    ? `<div class="dev-b-body"><div class="dt-kv-grid is-title-first is-2col">${kvItems}</div></div>`
                    : ''
                }
              </div>
            `;
          })
          .join('');

  host.innerHTML = `
    <div class="dev-stack">
      <div style="display:flex;flex-direction:column;gap:10px;">
        ${cardsHtml}
        ${devices.length > 0 ? addRowHtml(false) : ''}
      </div>
    </div>
  `;

  host.querySelector('#pcieOpenAddBtn')?.addEventListener('click', () => {
    if (running) return;
    state.pcieMode = 'add';
    state.pcieRemovingId = null;
    rerender();
  });

  host.querySelectorAll('[data-pcie-toggle]').forEach((el) => {
    el.addEventListener('click', (e) => {
      if (e.target.closest('[data-stop-prop]')) return;
      const idx = Number(el.getAttribute('data-pcie-toggle'));
      if (!Number.isFinite(idx) || !devices[idx]) return;
      devices[idx].expanded = !devices[idx].expanded;
      rerender();
    });
  });

  host.querySelectorAll('[data-pcie-remove]').forEach((btn) => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      if (running) return;
      const idx = Number(btn.getAttribute('data-pcie-remove'));
      const removed = devices.splice(idx, 1)[0];
      if (!removed) return;
      const matchedHost = hostPool.find(
        (hd) =>
          (removed.hostId && hd.id === removed.hostId) ||
          (removed.path && hd.path && removed.path.toLowerCase() === hd.path.toLowerCase())
      );
      if (matchedHost) {
        matchedHost.ownerKind = 'host';
        matchedHost.ownerVmId = '';
        matchedHost.ownerVmName = '';
      }
      rerender();
      showNotice?.(`已断开 ${removed.name} 并归还给宿主机`);
    });
  });

  requestAnimationFrame(() => onLayoutNeeded?.());
}
