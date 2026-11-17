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

function resolveManu(g) {
  if (g.manu) return g.manu;
  const n = (g.name || '').toUpperCase();
  if (n.includes('AMD') || n.includes('RADEON')) return 'AMD';
  if (n.includes('INTEL')) return 'Intel';
  return 'NVIDIA';
}

function resolveVendor(g) {
  return g.vendor || resolveManu(g);
}

function getDeployStepsForMode(deployMode) {
  if (deployMode === 'update-drivers') {
    return ['检查分区', '识别系统', '部署驱动', '完成收尾'];
  }
  if (deployMode === 'assign-only') {
    return ['校验状态', '准备虚拟机', '分配分区', '完成配置'];
  }
  return ['校验状态', '关机准备', '分配分区', '识别系统', '部署驱动', '完成收尾'];
}

function getDeployActiveStepIdx(deployMode, decision) {
  const steps = getDeployStepsForMode(deployMode);
  if (decision === 'Completed') return steps.length;
  if (deployMode === 'update-drivers') {
    if (decision === 'PowerOff') return 0;
    if (decision === 'SelectPartition') return 1;
    return 2;
  }
  if (deployMode === 'assign-only') {
    return decision === 'PowerOff' ? 1 : 2;
  }
  if (decision === 'PowerOff') return 1;
  if (decision === 'SelectPartition') return 3;
  return 4;
}

export function renderGpuWorkbench(host, ctx) {
  const {
    state,
    vm,
    HOST_GPUS,
    INITIAL_GPU_PARTITIONS,
    LINUX_GPU_SCRIPTS,
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
  const assigns = state.gpuAssignmentsByVm[vm.id];
  const variant = state.gpuVariant || 'expander'; // 'expander' | 'tiles' | 'compact' | 'layered'

  // =========================================================================
  // View 2: 选择宿主显卡 (Restrained WinUI 3)
  // =========================================================================
  if (state.gpuMode === 'select') {
    const pickedValid = HOST_GPUS.find((g) => g.id === state.selectedHostGpuId && g.partitionable);
    const activePickId = pickedValid ? pickedValid.id : HOST_GPUS.find((g) => g.partitionable)?.id || '';
    state.selectedHostGpuId = activePickId;

    const hostRowsHtml = HOST_GPUS.map((g) => {
      const sel = g.id === activePickId;
      const disabled = !g.partitionable;
      const tagName = disabled ? 'div' : 'button';
      const rightNote = disabled
        ? `<span class="dev-status-muted">不可分区</span>`
        : `<span class="dev-status-muted">${escapeDeviceHtml(g.memoryText || '')}</span>`;

      return `
        <${tagName}
          class="dev-picker-row ${sel ? 'is-selected' : ''} ${disabled ? 'is-disabled' : ''}"
          ${disabled ? '' : `data-host-gpu="${escapeDeviceHtml(g.id)}" type="button"`}
        >
          <span class="dev-radio"></span>
          <span class="fi" style="font-size:20px;color:var(--TextFillColorPrimaryBrush);">&#xF211;</span>
          <div style="min-width:0;">
            <div style="font-size:14px;font-weight:600;color:var(--TextFillColorPrimaryBrush);">${escapeDeviceHtml(g.name)}</div>
            <div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">
              ${escapeDeviceHtml(disabled ? (g.unpartitionableReason || g.instanceId) : `${resolveVendor(g)} · 驱动 ${g.driverVersion} · ${g.instanceId}`)}
            </div>
          </div>
          ${rightNote}
        </${tagName}>
      `;
    }).join('');

    host.innerHTML = `
      <div class="dev-stack">
        <div class="dev-subhead">
          <div class="dev-subhead-left">
            <button class="dev-back-btn" id="gpuBackListBtn" type="button" title="返回显卡列表">
              <span class="fi" style="font-size:13px;">&#xE72B;</span>
            </button>
            <span style="font-size:16px;font-weight:600;">选择宿主显卡</span>
          </div>
          <button class="winui-btn" id="gpuScanHostBtn" type="button">刷新</button>
        </div>

        <div class="dev-picker-list">${hostRowsHtml}</div>

        <div class="winui-card" style="margin-bottom:0;">
          <div class="winui-card-row">
            ${renderSettingLabelHtml('安装驱动', '分配 GPU-PV 分区后继续识别来宾分区并部署驱动')}
            <div class="winui-switch ${state.installGpuDrivers ? 'on' : ''}" id="gpuInstallDrvSwitch" data-custom-switch="true" role="switch" aria-checked="${state.installGpuDrivers}">
              <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
            </div>
          </div>
        </div>

        <div class="dev-action-row" style="justify-content:flex-end;">
          <button class="winui-btn" id="gpuCancelSelectBtn" type="button">取消</button>
          <button class="winui-btn accent" id="gpuStartDeployBtn" type="button" ${activePickId ? '' : 'disabled'}>开始配置</button>
        </div>
      </div>
    `;

    host.querySelector('#gpuBackListBtn')?.addEventListener('click', () => {
      state.gpuMode = 'list';
      rerender();
    });
    host.querySelector('#gpuCancelSelectBtn')?.addEventListener('click', () => {
      state.gpuMode = 'list';
      rerender();
    });
    host.querySelector('#gpuScanHostBtn')?.addEventListener('click', () => {
      showNotice('已重新扫描宿主机可分区显卡');
    });
    host.querySelectorAll('[data-host-gpu]').forEach((el) => {
      el.addEventListener('click', () => {
        state.selectedHostGpuId = el.getAttribute('data-host-gpu');
        rerender();
      });
    });
    host.querySelector('#gpuInstallDrvSwitch')?.addEventListener('click', () => {
      state.installGpuDrivers = !state.installGpuDrivers;
      rerender();
    });
    host.querySelector('#gpuStartDeployBtn')?.addEventListener('click', () => {
      const picked = HOST_GPUS.find((g) => g.id === state.selectedHostGpuId && g.partitionable) || HOST_GPUS[0];
      const existing = state.gpuAssignmentsByVm[vm.id] || [];
      const newAssign = {
        id: `gpu-${Date.now()}`,
        name: picked.name,
        manu: picked.manu,
        vendor: picked.vendor || picked.manu,
        memoryText: picked.memoryText || '未知',
        driverVersion: picked.driverVersion,
        instanceId: picked.instanceId,
        partitionPath: picked.partitionPath,
        expanded: true
      };
      if (existing.some((x) => x.instanceId === picked.instanceId)) {
        state.gpuAssignmentsByVm[vm.id] = existing.map((x) =>
          x.instanceId === picked.instanceId ? { ...newAssign, id: x.id } : x
        );
      } else {
        state.gpuAssignmentsByVm[vm.id] = [newAssign];
      }
      state.gpuScenario = 'normal';
      state.gpuDeployMode = state.installGpuDrivers ? 'add-with-drivers' : 'assign-only';
      if (vm.running) {
        state.gpuDeployDecision = 'PowerOff';
      } else if (state.installGpuDrivers) {
        state.gpuDeployDecision = 'SelectPartition';
      } else {
        state.gpuDeployDecision = 'Completed';
      }
      state.gpuMode = 'deploy';
      state.gpuLogExpanded = false;
      renderDashboardSummary();
      rerender();
    });

    requestAnimationFrame(() => onLayoutNeeded?.());
    return;
  }

  // =========================================================================
  // View 3: 显卡配置与驱动部署工作台 (Restrained WinUI 3)
  // =========================================================================
  if (state.gpuMode === 'deploy') {
    const deployMode = state.gpuDeployMode || 'add-with-drivers';
    const decision = state.gpuDeployDecision || 'SelectPartition';
    const steps = getDeployStepsForMode(deployMode);
    const activeIdx = getDeployActiveStepIdx(deployMode, decision);
    const isDone = decision === 'Completed';
    const pct = isDone ? 100 : Math.round(((activeIdx + 0.4) / steps.length) * 100);
    const targetGpu = assigns[0] || INITIAL_GPU_ASSIGNMENTS[0];

    const stepRailHtml = steps
      .map((label, idx) => {
        const done = isDone || idx < activeIdx;
        const active = !isDone && idx === activeIdx;
        const status = done ? '已完成' : active ? '进行中' : '等待';
        return `
          <div class="dev-step-cell ${active ? 'is-active' : ''}">
            <div class="dev-step-name">${escapeDeviceHtml(label)}</div>
            <div class="dev-step-sub">${status}</div>
          </div>
        `;
      })
      .join('');

    let decisionBoxHtml = '';
    if (decision === 'PowerOff') {
      decisionBoxHtml = `
        <div class="dev-decision-box">
          <div style="font-size:15px;font-weight:600;">需要关闭虚拟机</div>
          <div style="font-size:13px;color:var(--TextFillColorSecondaryBrush);">分配 GPU-PV 分区前需先关闭「${escapeDeviceHtml(vm.name)}」。</div>
          <div class="dev-action-row">
            <button class="winui-btn accent" id="gpuDecisionPowerOffBtn" type="button">关闭虚拟机并继续</button>
            <button class="winui-btn" id="gpuDecisionCancelBtn" type="button">取消</button>
          </div>
        </div>
      `;
    } else if (decision === 'SelectPartition') {
      const partRows = INITIAL_GPU_PARTITIONS.map((p) => {
        const sel = state.gpuSelectedPartitionId === p.id;
        return `
          <div class="dev-picker-row ${sel ? 'is-selected' : ''} ${p.locked ? 'is-disabled' : ''}" ${p.locked ? '' : `data-gpu-part="${p.id}"`}>
            <span class="dev-radio"></span>
            <span class="fi" style="font-size:18px;color:var(--TextFillColorPrimaryBrush);">${p.locked ? '&#xE72E;' : '&#xEDA2;'}</span>
            <div style="min-width:0;">
              <div style="font-size:13.5px;font-weight:600;color:var(--TextFillColorPrimaryBrush);">${escapeDeviceHtml(p.title)}</div>
              <div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);margin-top:2px;">${escapeDeviceHtml(p.diskLabel)} · ${escapeDeviceHtml(p.osType)} · ${escapeDeviceHtml(p.fs)}</div>
            </div>
            <span class="dev-status-muted">${escapeDeviceHtml(p.sizeText)}</span>
          </div>
        `;
      }).join('');

      decisionBoxHtml = `
        <div class="dev-decision-box">
          <div style="font-size:15px;font-weight:600;">选择目标系统分区</div>
          <div class="dev-picker-list">${partRows}</div>
          <div class="dev-action-row">
            <button class="winui-btn accent" id="gpuDecisionPartNextBtn" type="button">使用所选分区继续</button>
            <button class="winui-btn" id="gpuDecisionSkipDriverBtn" type="button">跳过驱动部署</button>
          </div>
        </div>
      `;
    } else if (decision === 'InstallWindows') {
      decisionBoxHtml = `
        <div class="dev-decision-box">
          <div style="font-size:15px;font-weight:600;">注入 Windows 宿主驱动</div>
          <div style="font-size:13px;color:var(--TextFillColorSecondaryBrush);">目标分区 Windows 11 Pro (C:)，驱动版本 ${escapeDeviceHtml(targetGpu.driverVersion)}。</div>
          <div class="dev-action-row">
            <button class="winui-btn accent" id="gpuDecisionWinInjectBtn" type="button">开始注入驱动</button>
            <button class="winui-btn" id="gpuDecisionBackPartBtn" type="button">重选分区</button>
          </div>
        </div>
      `;
    } else if (decision === 'StartLinux') {
      decisionBoxHtml = `
        <div class="dev-decision-box">
          <div style="font-size:15px;font-weight:600;">启动虚拟机并连接 SSH</div>
          <div style="font-size:13px;color:var(--TextFillColorSecondaryBrush);">检测到目标为 Linux 分区，将先启动虚拟机再通过 SSH 部署驱动组件。</div>
          <div class="dev-action-row">
            <button class="winui-btn accent" id="gpuDecisionStartLinuxBtn" type="button">启动虚拟机并继续</button>
            <button class="winui-btn" id="gpuDecisionBackPartBtn" type="button">重选分区</button>
          </div>
        </div>
      `;
    } else if (decision === 'LinuxCredentials') {
      const f = state.gpuLinuxForm;
      decisionBoxHtml = `
        <div class="dev-decision-box">
          <div style="font-size:15px;font-weight:600;">Linux SSH 连接参数</div>
          <div class="dev-ssh-grid">
            <div class="dev-field-col">
              <label class="dev-field-lbl">主机 / IP</label>
              <input class="winui-textbox" id="gpuSshHostInput" type="text" value="${escapeDeviceHtml(f.host)}" />
            </div>
            <div class="dev-field-col">
              <label class="dev-field-lbl">端口</label>
              <input class="winui-textbox" id="gpuSshPortInput" type="text" value="${escapeDeviceHtml(f.port)}" />
            </div>
            <div class="dev-field-col">
              <label class="dev-field-lbl">用户名</label>
              <input class="winui-textbox" id="gpuSshUserInput" type="text" value="${escapeDeviceHtml(f.username)}" />
            </div>
            <div class="dev-field-col">
              <label class="dev-field-lbl">密码</label>
              <input class="winui-textbox" id="gpuSshPassInput" type="password" value="${escapeDeviceHtml(f.password)}" />
            </div>
            <div class="dev-field-col span-2">
              <label class="dev-field-lbl">部署脚本</label>
              <select class="winui-select" id="gpuSshScriptSelect">
                ${LINUX_GPU_SCRIPTS.map((s) => `<option value="${s.id}" ${f.scriptId === s.id ? 'selected' : ''}>${escapeDeviceHtml(s.label)}</option>`).join('')}
              </select>
            </div>
          </div>
          <div class="dev-action-row">
            <button class="winui-btn accent" id="gpuDecisionLinuxDeployBtn" type="button">开始 SSH 部署</button>
            <button class="winui-btn" id="gpuDecisionBackPartBtn" type="button">重选分区</button>
          </div>
        </div>
      `;
    } else {
      decisionBoxHtml = `
        <div class="dev-decision-box">
          <div style="font-size:15px;font-weight:600;">配置完成</div>
          <div style="font-size:13px;color:var(--TextFillColorSecondaryBrush);">已完成「${escapeDeviceHtml(targetGpu.name)}」显卡分区及驱动相关配置。</div>
          <div class="dev-action-row">
            <button class="winui-btn accent" id="gpuDecisionDoneBtn" type="button">返回显卡列表</button>
          </div>
        </div>
      `;
    }

    host.innerHTML = `
      <div class="dev-stack">
        <div class="dev-subhead">
          <div class="dev-subhead-left">
            <button class="dev-back-btn" id="gpuBackListFromDeployBtn" type="button" title="返回显卡列表">
              <span class="fi" style="font-size:13px;">&#xE72B;</span>
            </button>
            <span style="font-size:16px;font-weight:600;">${deployMode === 'update-drivers' ? '更新来宾显卡驱动' : '配置显卡与驱动'}</span>
          </div>
          <span class="dev-status-muted">${escapeDeviceHtml(targetGpu.name)}</span>
        </div>

        <div class="dev-step-box">
          <div class="dev-step-grid" style="grid-template-columns:repeat(${steps.length}, minmax(0, 1fr));">
            ${stepRailHtml}
          </div>
          <div class="storage-capacity-meter"><div class="storage-capacity-fill" style="width:${pct}%;"></div></div>
        </div>

        ${decisionBoxHtml}
      </div>
    `;

    const backToList = () => {
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
      state.gpuDeployDecision = state.gpuDeployMode === 'assign-only' ? 'Completed' : 'SelectPartition';
      rerender();
    });
    host.querySelectorAll('[data-gpu-part]').forEach((row) => {
      row.addEventListener('click', () => {
        state.gpuSelectedPartitionId = row.getAttribute('data-gpu-part');
        rerender();
      });
    });
    host.querySelector('#gpuDecisionPartNextBtn')?.addEventListener('click', () => {
      const part = INITIAL_GPU_PARTITIONS.find((p) => p.id === state.gpuSelectedPartitionId && !p.locked) || INITIAL_GPU_PARTITIONS[0];
      state.gpuDeployDecision = part.osType === 'Linux' ? (vm.running ? 'LinuxCredentials' : 'StartLinux') : 'InstallWindows';
      rerender();
    });
    host.querySelector('#gpuDecisionSkipDriverBtn')?.addEventListener('click', () => {
      state.gpuDeployMode = 'assign-only';
      state.gpuDeployDecision = 'Completed';
      rerender();
    });
    host.querySelector('#gpuDecisionBackPartBtn')?.addEventListener('click', () => {
      state.gpuDeployDecision = 'SelectPartition';
      rerender();
    });
    host.querySelector('#gpuDecisionWinInjectBtn')?.addEventListener('click', () => {
      state.gpuDeployDecision = 'Completed';
      showNotice('已完成 Windows 驱动注入');
      rerender();
    });
    host.querySelector('#gpuDecisionStartLinuxBtn')?.addEventListener('click', () => {
      vm.running = true;
      ctx.renderVmList?.();
      renderDashboardSummary();
      state.gpuDeployDecision = 'LinuxCredentials';
      rerender();
    });
    host.querySelector('#gpuSshHostInput')?.addEventListener('input', (e) => { state.gpuLinuxForm.host = e.target.value; });
    host.querySelector('#gpuSshPortInput')?.addEventListener('input', (e) => { state.gpuLinuxForm.port = e.target.value; });
    host.querySelector('#gpuSshUserInput')?.addEventListener('input', (e) => { state.gpuLinuxForm.username = e.target.value; });
    host.querySelector('#gpuSshPassInput')?.addEventListener('input', (e) => { state.gpuLinuxForm.password = e.target.value; });
    host.querySelector('#gpuSshScriptSelect')?.addEventListener('change', (e) => { state.gpuLinuxForm.scriptId = e.target.value; });
    host.querySelector('#gpuDecisionLinuxDeployBtn')?.addEventListener('click', () => {
      state.gpuDeployDecision = 'Completed';
      showNotice('已通过 SSH 部署 Linux 驱动');
      rerender();
    });

    requestAnimationFrame(() => onLayoutNeeded?.());
    return;
  }

  // =========================================================================
  // View 1: 主视图 — 4 个干净候选方案 (expander | tiles | compact | layered)
  // =========================================================================
  const isStale = state.gpuScenario === 'stale';
  const validCandidates = HOST_GPUS.filter((g) => g.partitionable);
  const hostPolicyOn = Boolean(state.gpuHostPolicyRelaxed);
  const vmReady = Boolean(state.gpuVmReady);

  // Stale InfoBar (only when stale scenario is active)
  let staleBannerHtml = '';
  if (isStale) {
    const picked = validCandidates[0];
    staleBannerHtml = `
      <div class="dev-infobar">
        <div class="dev-infobar-top">
          <div class="dev-infobar-msg">
            <span class="fi dev-infobar-icon">&#xE7BA;</span>
            <div>
              <div class="dev-infobar-title">检测到失效的 GPU-PV 分区配置</div>
              <div class="dev-infobar-desc">原宿主设备实例路径已变更，可重绑到当前可分区显卡「${escapeDeviceHtml(picked.name)}」或清理失效记录。</div>
            </div>
          </div>
          <div class="dev-toolbar-actions">
            <button class="winui-btn accent" id="gpuAutoRebindBtn" type="button">一键重绑</button>
            <button class="winui-btn" id="gpuClearStaleBtn" type="button">清理记录</button>
          </div>
        </div>
      </div>
    `;
  }

  let bodyHtml = '';

  // -------------------------------------------------------------------------
  // Candidate A: 纯正 WinUI Expander（对齐现有「存储 / 网卡」）
  // -------------------------------------------------------------------------
  if (variant === 'expander') {
    const cardsHtml =
      assigns.length === 0
        ? `<div class="dev-empty-line">此虚拟机尚未分配显卡。</div>`
        : assigns
            .map((g, idx) => {
              const expanded = g.expanded !== false;
              const rows = [
                { k: '芯片厂商', v: resolveManu(g), copy: resolveManu(g) },
                { k: '板卡厂商', v: resolveVendor(g), copy: resolveVendor(g) },
                { k: '宿主专用显存', v: g.memoryText || '未取得', copy: g.memoryText || '' },
                { k: '驱动版本', v: g.driverVersion || '---', copy: g.driverVersion || '' },
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
                      <span class="fi" style="font-size:24px;color:var(--TextFillColorPrimaryBrush);">&#xF211;</span>
                      <span style="font-size:15px;font-weight:500;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeDeviceHtml(g.name)}</span>
                      <div style="display:flex;gap:8px;" data-stop-prop>
                        <button class="winui-btn" data-gpu-update="${idx}" type="button">更新驱动</button>
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

    const mmioRight = vmReady
      ? `<span class="dev-status-muted">已就绪</span>`
      : `<div class="dev-env-right"><span class="dev-status-caution">待配置</span><button class="winui-btn accent" id="gpuOptimizeReadyBtn" type="button">一键优化</button></div>`;

    bodyHtml = `
      <div class="dev-toolbar">
        <button class="winui-btn" id="gpuRefreshBtn" type="button">刷新</button>
        <button class="winui-btn accent" id="gpuOpenSelectBtn" type="button">添加显卡</button>
      </div>
      ${staleBannerHtml}
      <div style="display:flex;flex-direction:column;gap:10px;">${cardsHtml}</div>
      <div>
        <div class="settings-section-title" style="margin:6px 0 8px 0;">环境与前置策略</div>
        <div class="winui-card" style="margin-bottom:0;">
          <div class="winui-card-row">
            ${renderSettingLabelHtml('显卡就绪优化', '检查并配置 GuestControlledCacheTypes 与高位 MMIO 空间，保障 GPU-PV 正常启动与识别。')}
            ${mmioRight}
          </div>
          <div class="winui-card-row">
            ${renderSettingLabelHtml('放宽宿主限制', '修改宿主注册表以允许更多显卡与分区分配至虚拟机（宿主全局生效）。')}
            <div class="winui-switch ${hostPolicyOn ? 'on' : ''}" id="gpuHostPolicySwitch" data-custom-switch="true" role="switch" aria-checked="${hostPolicyOn}">
              <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
            </div>
          </div>
        </div>
      </div>
    `;
  }

  // -------------------------------------------------------------------------
  // Candidate B: 状态同源 · 干净瓦片风（统一「状态页 + 内存页」）
  // -------------------------------------------------------------------------
  else if (variant === 'tiles') {
    const cardsHtml =
      assigns.length === 0
        ? `
          <div class="dev-empty-row">
            <div style="display:flex;align-items:center;gap:12px;">
              <div class="tc-head-icon-box"><span class="fi">&#xF211;</span></div>
              <div>
                <div style="font-size:14px;font-weight:600;color:var(--TextFillColorPrimaryBrush);">未分配显卡</div>
                <div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);">从宿主机可分区显卡中分配 GPU-PV 资源</div>
              </div>
            </div>
            <button class="winui-btn accent" id="gpuEmptyAddBtn" type="button">选择宿主显卡</button>
          </div>
        `
        : assigns
            .map((g, idx) => {
              const expanded = g.expanded !== false;
              const kvItems = [
                renderTitleFirstKvItemHtml({ title: '芯片厂商', stateText: resolveManu(g), copyVal: resolveManu(g) }),
                renderTitleFirstKvItemHtml({ title: '板卡厂商', stateText: resolveVendor(g), copyVal: resolveVendor(g) }),
                renderTitleFirstKvItemHtml({ title: '宿主专用显存', stateText: g.memoryText || '未取得', copyVal: g.memoryText || '' }),
                renderTitleFirstKvItemHtml({ title: '宿主驱动版本', stateText: g.driverVersion || '---', copyVal: g.driverVersion || '' }),
                renderTitleFirstKvItemHtml({ title: '设备实例路径', stateText: g.instanceId || '---', copyVal: g.instanceId || '', wide: true })
              ].join('');

              return `
                <div class="dev-b-card ${expanded ? 'is-expanded' : ''}">
                  <div class="dev-b-head" data-gpu-toggle="${idx}">
                    <div class="tc-head-icon-box"><span class="fi">&#xF211;</span></div>
                    <div class="dev-b-identity">
                      <div class="dev-b-title">${escapeDeviceHtml(g.name)}</div>
                      <div class="dev-b-sub">${escapeDeviceHtml(`${resolveVendor(g)} · ${g.memoryText || '未取得'}`)}</div>
                    </div>
                    <div class="dev-b-actions" data-stop-prop>
                      <button class="winui-btn" data-gpu-update="${idx}" type="button">更新驱动</button>
                      <button class="winui-btn" data-gpu-remove="${idx}" type="button">移除</button>
                      <span class="dev-b-chevron"><span class="fi">${expanded ? '&#xE70E;' : '&#xE70D;'}</span></span>
                    </div>
                  </div>
                  ${expanded ? `<div class="dev-b-body"><div class="dt-kv-grid is-title-first is-2col">${kvItems}</div></div>` : ''}
                </div>
              `;
            })
            .join('');

    const mmioRight = vmReady
      ? `<span class="dev-status-muted">已就绪</span>`
      : `<button class="winui-btn accent" id="gpuOptimizeReadyBtn" type="button">一键优化</button>`;

    bodyHtml = `
      <div class="dev-toolbar is-split">
        <div class="dev-toolbar-left">
          <span class="dev-toolbar-title">已分配显卡</span>
          <span class="dev-toolbar-sub">${assigns.length > 0 ? `GPU-PV · ${assigns.length} 项` : 'GPU-PV'}</span>
        </div>
        <div class="dev-toolbar-actions">
          <button class="winui-btn" id="gpuRefreshBtn" type="button">刷新</button>
          <button class="winui-btn accent" id="gpuOpenSelectBtn" type="button">添加显卡</button>
        </div>
      </div>
      ${staleBannerHtml}
      <div style="display:flex;flex-direction:column;gap:10px;">${cardsHtml}</div>
      <div>
        <div class="settings-section-title" style="margin:6px 0 8px 0;">环境与前置策略</div>
        <div class="isa-grid-2x2">
          <div class="isa-card uniform-1row">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('显卡就绪优化', '检查并配置 GuestControlledCacheTypes 与高位 MMIO 空间，确保 GPU-PV 正常启动与识别。')}
              <div class="dev-env-right">${mmioRight}</div>
            </div>
          </div>
          <div class="isa-card uniform-1row">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('放宽宿主限制', '修改宿主注册表以允许更多显卡与分区分配至虚拟机（宿主全局生效）。')}
              <div class="winui-switch ${hostPolicyOn ? 'on' : ''}" id="gpuHostPolicySwitch" data-custom-switch="true" role="switch" aria-checked="${hostPolicyOn}">
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
              </div>
            </div>
          </div>
        </div>
      </div>
    `;
  }

  // -------------------------------------------------------------------------
  // Candidate C: 高密度紧凑型 · 图标动作条（多卡/多设备列表最优）
  // -------------------------------------------------------------------------
  else if (variant === 'compact') {
    const cardsHtml =
      assigns.length === 0
        ? `
          <div class="dev-empty-row">
            <span class="dev-status-muted">尚未分配 GPU-PV 显卡</span>
            <button class="winui-btn" id="gpuEmptyAddBtn" type="button">选择宿主显卡</button>
          </div>
        `
        : assigns
            .map((g, idx) => {
              const expanded = g.expanded !== false;
              const body = expanded
                ? `
                  <div class="dev-c-kv-4col">
                    <div class="dev-c-kv-cell"><span class="dev-c-kv-lbl">芯片厂商</span><span class="dev-c-kv-val">${escapeDeviceHtml(resolveManu(g))}</span></div>
                    <div class="dev-c-kv-cell"><span class="dev-c-kv-lbl">板卡厂商</span><span class="dev-c-kv-val">${escapeDeviceHtml(resolveVendor(g))}</span></div>
                    <div class="dev-c-kv-cell"><span class="dev-c-kv-lbl">宿主专用显存</span><span class="dev-c-kv-val">${escapeDeviceHtml(g.memoryText || '未取得')}</span></div>
                    <div class="dev-c-kv-cell"><span class="dev-c-kv-lbl">驱动版本</span><span class="dev-c-kv-val">${escapeDeviceHtml(g.driverVersion || '---')}</span></div>
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
                    <span class="fi" style="font-size:19px;color:var(--TextFillColorPrimaryBrush);">&#xF211;</span>
                    <div class="dev-c-title-inline">
                      <span class="dev-c-name">${escapeDeviceHtml(g.name)}</span>
                      <span class="dev-c-spec">${escapeDeviceHtml(`${g.memoryText || ''} · ${g.driverVersion || ''}`)}</span>
                    </div>
                    <div style="display:inline-flex;align-items:center;gap:4px;" data-stop-prop>
                      <button class="round-icon-btn" title="更新驱动" data-gpu-update="${idx}" type="button">
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

    const mmioRight = vmReady
      ? `<span class="dev-status-muted">已就绪</span>`
      : `<button class="winui-btn accent" id="gpuOptimizeReadyBtn" type="button" style="height:28px;padding:0 10px;font-size:12.5px;">优化</button>`;

    bodyHtml = `
      <div class="dev-toolbar is-split">
        <div class="dev-toolbar-left">
          <span class="dev-toolbar-title">GPU-PV 显卡</span>
          <span class="dev-toolbar-sub">${assigns.length} 项</span>
        </div>
        <div class="dev-toolbar-actions">
          <button class="winui-btn" id="gpuRefreshBtn" type="button">刷新</button>
          <button class="winui-btn accent" id="gpuOpenSelectBtn" type="button">添加显卡</button>
        </div>
      </div>
      ${staleBannerHtml}
      <div style="display:flex;flex-direction:column;gap:8px;">${cardsHtml}</div>
      <div class="dev-c-env-bar">
        <div class="dev-c-env-cell">
          ${renderSettingLabelHtml('显卡就绪优化', '检查并配置 GuestControlledCacheTypes 与高位 MMIO。')}
          ${mmioRight}
        </div>
        <div class="dev-c-env-sep"></div>
        <div class="dev-c-env-cell">
          ${renderSettingLabelHtml('放宽宿主限制', '宿主机注册表全局允许非常规显卡分配。')}
          <div class="winui-switch ${hostPolicyOn ? 'on' : ''}" id="gpuHostPolicySwitch" data-custom-switch="true" role="switch" aria-checked="${hostPolicyOn}">
            <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
          </div>
        </div>
      </div>
    `;
  }

  // -------------------------------------------------------------------------
  // Candidate D: 极纯主画布 + 底部折叠策略组（极致留白）
  // -------------------------------------------------------------------------
  else {
    const cardsHtml =
      assigns.length === 0
        ? `<div class="dev-empty-line">未分配 GPU-PV 显卡</div>`
        : assigns
            .map((g, idx) => {
              return `
                <div class="dev-d-card">
                  <div class="dev-d-head">
                    <div class="tc-head-icon-box" style="width:32px;height:32px;font-size:15px;"><span class="fi">&#xF211;</span></div>
                    <span style="font-size:15px;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeDeviceHtml(g.name)}</span>
                    <div class="dev-toolbar-actions">
                      <button class="winui-btn" data-gpu-update="${idx}" type="button">更新驱动</button>
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
                        <span class="dev-d-val">${escapeDeviceHtml(g.driverVersion || '---')}</span>
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

    const envExpanded = Boolean(state.gpuEnvExpanded);
    const summaryText = `${vmReady ? 'MMIO 已就绪' : 'MMIO 待优化'} · 宿主限制${hostPolicyOn ? '已放宽' : '未开启'}`;
    const mmioRowAction = vmReady
      ? `<span class="dev-status-muted">已就绪</span>`
      : `<div class="dev-env-right"><span class="dev-status-caution">待优化</span><button class="winui-btn accent" id="gpuOptimizeReadyBtn" type="button">一键优化</button></div>`;

    bodyHtml = `
      <div class="dev-toolbar">
        <button class="winui-btn" id="gpuRefreshBtn" type="button">刷新</button>
        <button class="winui-btn accent" id="gpuOpenSelectBtn" type="button">添加显卡</button>
      </div>
      ${staleBannerHtml}
      <div style="display:flex;flex-direction:column;gap:12px;">${cardsHtml}</div>
      <div class="winui-expander ${envExpanded ? 'expanded' : ''}">
        <div class="winui-expander-header" id="gpuEnvExpanderHead" style="min-height:48px;padding:10px 16px;">
          <div style="display:flex;align-items:center;justify-content:space-between;gap:12px;">
            <span style="font-size:14px;font-weight:500;">环境与前置策略</span>
            <span class="${vmReady ? 'dev-status-muted' : 'dev-status-caution'}">${summaryText}</span>
          </div>
          <span class="fi winui-expander-chevron">${envExpanded ? '&#xE70E;' : '&#xE70D;'}</span>
        </div>
        ${
          envExpanded
            ? `
          <div class="winui-expander-body" style="padding:0;">
            <div class="winui-card-row">
              ${renderSettingLabelHtml('显卡就绪优化', '检查并配置 GuestControlledCacheTypes 与高位 MMIO。')}
              ${mmioRowAction}
            </div>
            <div class="winui-card-row">
              ${renderSettingLabelHtml('放宽宿主限制', '在宿主机注册表允许更多显卡与分区分配至虚拟机（全局生效）。')}
              <div class="winui-switch ${hostPolicyOn ? 'on' : ''}" id="gpuHostPolicySwitch" data-custom-switch="true" role="switch" aria-checked="${hostPolicyOn}">
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
              </div>
            </div>
          </div>
        `
            : ''
        }
      </div>
    `;
  }

  host.innerHTML = `<div class="dev-stack">${bodyHtml}</div>`;

  // Event Bindings
  host.querySelectorAll('[data-stop-prop]').forEach((el) => {
    el.addEventListener('click', (e) => e.stopPropagation());
  });
  host.querySelectorAll('[data-gpu-toggle]').forEach((hdr) => {
    hdr.addEventListener('click', () => {
      const idx = Number(hdr.getAttribute('data-gpu-toggle'));
      if (assigns[idx]) {
        assigns[idx].expanded = !(assigns[idx].expanded !== false);
        rerender();
      }
    });
  });

  const openSelect = () => {
    state.gpuMode = 'select';
    rerender();
  };
  host.querySelector('#gpuOpenSelectBtn')?.addEventListener('click', openSelect);
  host.querySelector('#gpuEmptyAddBtn')?.addEventListener('click', openSelect);
  host.querySelector('#gpuRefreshBtn')?.addEventListener('click', () => {
    showNotice('已重新扫描宿主 GPU-PV 与虚拟机分配记录');
  });

  host.querySelectorAll('[data-gpu-update]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.gpuDeployMode = 'update-drivers';
      state.gpuDeployDecision = vm.running ? 'PowerOff' : 'SelectPartition';
      state.gpuMode = 'deploy';
      rerender();
    });
  });

  host.querySelectorAll('[data-gpu-remove]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const idx = Number(btn.getAttribute('data-gpu-remove'));
      assigns.splice(idx, 1);
      if (assigns.length === 0) state.gpuScenario = 'normal';
      renderDashboardSummary();
      rerender();
      showNotice('已移除显卡分配');
    });
  });

  host.querySelector('#gpuAutoRebindBtn')?.addEventListener('click', () => {
    const picked = validCandidates[0];
    state.gpuAssignmentsByVm[vm.id] = [
      {
        id: `gpu-${Date.now()}`,
        name: picked.name,
        manu: picked.manu,
        vendor: picked.vendor,
        memoryText: picked.memoryText,
        driverVersion: picked.driverVersion,
        instanceId: picked.instanceId,
        partitionPath: picked.partitionPath,
        expanded: true
      }
    ];
    state.gpuScenario = 'normal';
    renderDashboardSummary();
    rerender();
    showNotice(`已重绑到「${picked.name}」`);
  });

  host.querySelector('#gpuClearStaleBtn')?.addEventListener('click', () => {
    state.gpuAssignmentsByVm[vm.id] = [];
    state.gpuScenario = 'normal';
    renderDashboardSummary();
    rerender();
    showNotice('已清理失效记录');
  });

  host.querySelector('#gpuOptimizeReadyBtn')?.addEventListener('click', () => {
    state.gpuVmReady = true;
    rerender();
    showNotice('已完成显卡就绪优化');
  });

  host.querySelector('#gpuHostPolicySwitch')?.addEventListener('click', () => {
    state.gpuHostPolicyRelaxed = !state.gpuHostPolicyRelaxed;
    rerender();
    showNotice(state.gpuHostPolicyRelaxed ? '已开启宿主 GPU 分配放宽策略' : '已恢复默认宿主 GPU 校验策略');
  });

  host.querySelector('#gpuEnvExpanderHead')?.addEventListener('click', () => {
    state.gpuEnvExpanded = !state.gpuEnvExpanded;
    rerender();
  });

  requestAnimationFrame(() => onLayoutNeeded?.());
}
