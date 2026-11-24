export function createConsoleController({
  getVm,
  getAllVms,
  getAdvSettings,
  getTheme,
  getAccentHex,
  getOpenVmIds,
  isHostedInWinOnWeb = false,
  onVmPowerChanged,
  onActiveVmChanged,
  onFocusExistingWindow,
  onOpenNewWindow,
  onDragSpacerRect,
  onRequestClose
}) {
  const win = document.getElementById('vmConsoleWindow');
  if (!win) return { open: () => {}, openTab: () => {}, syncTheme: () => {}, syncVmState: () => {} };

  const titleBar = document.getElementById('consoleTitleBar');
  const dragSpacer = document.getElementById('consoleDragSpacer');

  const switcherBtn = document.getElementById('consoleSwitcherBtn');
  const switcherOsIcon = document.getElementById('consoleSwitcherOsIcon');
  const switcherName = document.getElementById('consoleSwitcherName');
  const switcherFlyout = document.getElementById('consoleSwitcherFlyout');
  const switcherList = document.getElementById('consoleSwitcherList');

  const minBtn = document.getElementById('consoleMinimizeBtn');
  const maxBtn = document.getElementById('consoleMaximizeBtn');
  const maxIcon = document.getElementById('consoleMaximizeIcon');
  const closeBtn = document.getElementById('consoleCloseBtn');

  const powerStartBtn = document.getElementById('consolePowerStartBtn');
  const powerSplit = document.getElementById('consolePowerSplit');
  const powerMainBtn = document.getElementById('consolePowerMainBtn');
  const powerDropBtn = document.getElementById('consolePowerDropBtn');
  const powerFlyout = document.getElementById('consolePowerFlyout');

  const resBtn = document.getElementById('consoleResBtn');
  const resLabel = document.getElementById('consoleResLabel');
  const resFlyout = document.getElementById('consoleResFlyout');

  const scaleBtn = document.getElementById('consoleScaleBtn');
  const scaleLabel = document.getElementById('consoleScaleLabel');
  const scaleFlyout = document.getElementById('consoleScaleFlyout');

  const modeBtn = document.getElementById('consoleModeBtn');
  const modeLabel = document.getElementById('consoleModeLabel');
  const modeFlyout = document.getElementById('consoleModeFlyout');

  const cadBtn = document.getElementById('consoleCadBtn');
  const shotBtn = document.getElementById('consoleShotBtn');
  const audioBtn = document.getElementById('consoleAudioBtn');
  const audioIcon = document.getElementById('consoleAudioIcon');
  const fullscreenBtn = document.getElementById('consoleFullscreenBtn');
  const fullscreenIcon = document.getElementById('consoleFullscreenIcon');

  const fsBar = document.getElementById('consoleFsBar');
  const fsPinBtn = document.getElementById('consoleFsPinBtn');
  const fsVmName = document.getElementById('consoleFsVmName');
  const fsCadBtn = document.getElementById('consoleFsCadBtn');
  const fsModeBtn = document.getElementById('consoleFsModeBtn');
  const fsModeLabel = document.getElementById('consoleFsModeLabel');
  const fsExitBtn = document.getElementById('consoleFsExitBtn');

  const viewport = document.getElementById('consoleViewport');
  const flashOverlay = document.getElementById('consoleFlashOverlay');
  const toastEl = document.getElementById('consoleToast');
  const toastTextEl = document.getElementById('consoleToastText');
  const screenSurface = document.getElementById('consoleScreenSurface');
  const guestSessionTag = document.getElementById('consoleGuestSessionTag');
  const guestHostname = document.getElementById('consoleGuestHostname');
  const guestResLabel = document.getElementById('consoleGuestResLabel');

  const emptyState = document.getElementById('consoleEmptyState');
  const emptySpinner = document.getElementById('consoleEmptySpinner');
  const emptyOs = document.getElementById('consoleEmptyOs');
  const emptyTitle = document.getElementById('consoleEmptyTitle');
  const emptyDesc = document.getElementById('consoleEmptyDesc');
  const primaryActionBtn = document.getElementById('consolePrimaryActionBtn');

  const sessions = new Map();
  const consoleState = {
    isOpen: false,
    isFullscreen: false,
    fsPinned: true,
    activeVmId: null
  };

  let toastTimer = null;
  let fsHideTimer = null;

  function activeSession() {
    return consoleState.activeVmId ? sessions.get(consoleState.activeVmId) || null : null;
  }

  function showToast(text) {
    if (!toastEl || !toastTextEl) return;
    toastTextEl.textContent = text;
    toastEl.hidden = false;
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(() => {
      toastEl.hidden = true;
    }, 2000);
  }

  function formatResText(resKey) {
    return String(resKey || '1280x720').replace('x', ' × ');
  }

  function escapeHtml(text) {
    return String(text ?? '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function closeAllFlyouts() {
    if (powerFlyout) powerFlyout.hidden = true;
    if (switcherFlyout) switcherFlyout.hidden = true;
    if (resFlyout) resFlyout.hidden = true;
    if (scaleFlyout) scaleFlyout.hidden = true;
    if (modeFlyout) modeFlyout.hidden = true;
  }

  function syncTheme() {
    const theme = getTheme ? getTheme() : 'dark';
    const accent = getAccentHex ? getAccentHex() : '#4CC2FF';
    win.setAttribute('data-theme', theme === 'light' ? 'light' : 'dark');
    win.style.setProperty('--VmHeaderCheckedBrush', accent);
  }

  function notifyDragSpacerRect() {
    if (!dragSpacer || typeof onDragSpacerRect !== 'function') return;
    requestAnimationFrame(() => {
      const r = dragSpacer.getBoundingClientRect();
      onDragSpacerRect({ left: Math.round(r.left), width: Math.max(0, Math.round(r.width)) });
    });
  }

  if (dragSpacer && typeof ResizeObserver !== 'undefined') {
    const ro = new ResizeObserver(() => notifyDragSpacerRect());
    ro.observe(dragSpacer);
  }

  function startSessionConnecting(session, targetMode) {
    if (session.connectTimer) clearTimeout(session.connectTimer);
    session.mode = targetMode;
    session.connState = 'connecting';
    session.inputCaptured = false;
    render();
    session.connectTimer = setTimeout(() => {
      const isGen1 = session.vm.generation === 1 || session.vm.gen === 1;
      const consoleOff = !isGen1 && (Boolean(session.adv.shielding) || session.adv.consoleSupport === false);
      if (!session.vm.running) {
        session.connState = 'stopped';
      } else if (consoleOff) {
        session.connState = 'disabled';
      } else {
        session.connState = 'connected';
      }
      render();
    }, 440);
  }

  function render() {
    const session = activeSession();
    if (!session) return;

    const vm = session.vm;
    const adv = session.adv;
    const isGen1 = vm.generation === 1 || vm.gen === 1;
    const consoleOff = !isGen1 && (Boolean(adv.shielding) || adv.consoleSupport === false);
    const isRunning = Boolean(vm.running);

    if (!isRunning && session.connState !== 'connecting') {
      session.connState = 'stopped';
      session.inputCaptured = false;
    } else if (consoleOff && session.connState !== 'connecting') {
      session.connState = 'disabled';
      session.inputCaptured = false;
    }

    const vmName = vm.name || '虚拟机';
    const osPath = vm.osIcon ? `/ui-design/os/${vm.osIcon}` : '/ui-design/os/Windows.svg';

    if (switcherOsIcon) switcherOsIcon.src = osPath;
    if (switcherName) switcherName.textContent = vmName;

    if (emptyOs) emptyOs.src = osPath;
    if (fsVmName) fsVmName.textContent = vmName;
    if (guestHostname) guestHostname.textContent = vmName.toUpperCase().replace(/[^A-Z0-9-]/g, '-');

    const isEnhanced = session.mode === 'enhanced';
    const connected = session.connState === 'connected';
    const canChangeRes = isRunning && isEnhanced && !isGen1;
    const zoomVal = session.scaleMode === 'actual' ? '100%' : (session.scaleMode || 'fit');
    const isMuted = Boolean(session.muted);

    if (powerStartBtn) powerStartBtn.hidden = isRunning;
    if (powerSplit) powerSplit.hidden = !isRunning;

    if (resBtn) resBtn.disabled = !canChangeRes;
    if (resLabel) resLabel.textContent = (isRunning && isEnhanced) ? formatResText(session.resolution) : '-';
    resFlyout?.querySelectorAll('[data-console-res]').forEach((item) => {
      item.classList.toggle('is-selected', item.getAttribute('data-console-res') === session.resolution);
    });

    if (scaleLabel) scaleLabel.textContent = zoomVal === 'fit' ? '适应窗口' : zoomVal;
    scaleFlyout?.querySelectorAll('[data-console-zoom]').forEach((item) => {
      item.classList.toggle('is-selected', item.getAttribute('data-console-zoom') === zoomVal);
    });

    if (modeLabel) modeLabel.textContent = isEnhanced ? '增强会话' : '基本会话';
    modeFlyout?.querySelectorAll('[data-console-session]').forEach((item) => {
      const m = item.getAttribute('data-console-session');
      item.classList.toggle('is-selected', m === session.mode);
      if (m === 'enhanced') item.disabled = consoleOff;
    });
    if (fsModeBtn) fsModeBtn.classList.toggle('is-active', isEnhanced);
    if (fsModeLabel) fsModeLabel.textContent = isEnhanced ? '增强会话' : '基本会话';

    if (cadBtn) {
      cadBtn.hidden = isEnhanced;
      cadBtn.disabled = !connected;
    }
    if (fsCadBtn) {
      fsCadBtn.hidden = isEnhanced;
      fsCadBtn.disabled = !connected;
    }
    if (shotBtn) shotBtn.disabled = !connected;

    if (audioBtn) {
      audioBtn.classList.toggle('is-checked', isMuted);
      audioBtn.title = isMuted ? '已静音（点击开启音频）' : '音频开启（点击静音）';
    }
    if (audioIcon) audioIcon.innerHTML = isMuted ? '&#xEB4B;' : '&#xEB43;';

    if (fullscreenIcon) fullscreenIcon.innerHTML = consoleState.isFullscreen ? '&#xE688;' : '&#xE685;';
    if (fullscreenBtn) fullscreenBtn.title = consoleState.isFullscreen ? '退出全屏 (Esc)' : '全屏显示';

    const displayRes = isEnhanced ? formatResText(session.resolution) : '1024 × 768';
    if (guestResLabel) guestResLabel.textContent = displayRes;
    if (guestSessionTag) guestSessionTag.textContent = isEnhanced ? '增强会话' : '基本会话';

    if (viewport) viewport.classList.toggle('is-actual-scale', zoomVal !== 'fit');
    if (screenSurface) {
      screenSurface.classList.toggle('is-captured', connected && session.inputCaptured);
      if (zoomVal !== 'fit') {
        const pct = parseFloat(zoomVal) || 100;
        const [rw, rh] = (isEnhanced ? String(session.resolution || '1280x720') : '1024x768').split('x').map(Number);
        screenSurface.style.width = `${Math.round((rw || 1280) * (pct / 100))}px`;
        screenSurface.style.height = `${Math.round((rh || 720) * (pct / 100))}px`;
      } else {
        screenSurface.style.width = '';
        screenSurface.style.height = '';
      }
    }

    if (emptyState && emptySpinner && emptyOs && emptyTitle && emptyDesc && primaryActionBtn) {
      if (connected) {
        emptyState.hidden = true;
      } else {
        emptyState.hidden = false;
        if (session.connState === 'connecting') {
          emptySpinner.hidden = false;
          emptyOs.hidden = true;
          emptyTitle.textContent = isEnhanced ? '正在建立增强会话' : '正在建立基本会话';
          emptyDesc.textContent = `正在连接 ${vmName} 的控制台通道，请稍候`;
          primaryActionBtn.hidden = true;
        } else if (session.connState === 'disabled') {
          emptySpinner.hidden = true;
          emptyOs.hidden = false;
          emptyTitle.textContent = '控制台已被禁用';
          emptyDesc.textContent = '已在虚拟机设置中关闭控制台支持，请在高级设置中重新启用';
          primaryActionBtn.hidden = true;
        } else if (session.connState === 'disconnected') {
          emptySpinner.hidden = true;
          emptyOs.hidden = false;
          emptyTitle.textContent = '控制台连接已断开';
          emptyDesc.textContent = '虚拟机仍在运行，可以随时重新建立会话连接';
          primaryActionBtn.hidden = false;
          primaryActionBtn.textContent = '重新连接';
        } else {
          emptySpinner.hidden = true;
          emptyOs.hidden = false;
          emptyTitle.textContent = '虚拟机未运行';
          emptyDesc.textContent = '启动虚拟机后将自动建立控制台画面连接';
          primaryActionBtn.hidden = false;
          primaryActionBtn.textContent = '启动并连接';
        }
      }
    }

    win.classList.toggle('is-fullscreen', consoleState.isFullscreen);
    if (fsBar) fsBar.hidden = !consoleState.isFullscreen;
    if (fsPinBtn) fsPinBtn.classList.toggle('is-pinned', consoleState.fsPinned);

    notifyDragSpacerRect();
  }

  function buildSessionPayload(session) {
    return {
      id: session.vm.id,
      name: session.vm.name,
      running: Boolean(session.vm.running),
      osIcon: session.vm.osIcon || 'Windows.svg',
      generation: session.vm.generation ?? session.vm.gen ?? 2,
      consoleSupport: session.adv.consoleSupport !== false,
      shielding: Boolean(session.adv.shielding),
      mode: session.mode,
      scaleMode: session.scaleMode,
      resolution: session.resolution,
      accentHex: getAccentHex ? getAccentHex() : '#4CC2FF'
    };
  }

  function openTab(vmPayload, advPayload) {
    syncTheme();
    consoleState.isOpen = true;
    win.hidden = false;

    const vmData = vmPayload || getVm?.() || { id: 'vm-1', name: 'Windows 11', running: true, osIcon: 'Windows.svg', generation: 2 };
    const advData = advPayload || getAdvSettings?.() || { consoleSupport: true, shielding: false };
    const id = vmData.id || `vm-${Date.now()}`;

    let session = sessions.get(id);
    if (session) {
      session.vm = { ...session.vm, ...vmData };
      session.adv = { ...session.adv, ...advData };
      consoleState.activeVmId = id;
      onActiveVmChanged?.(id);
      render();
      return;
    }

    session = {
      id,
      vm: { ...vmData },
      adv: { ...advData },
      mode: vmData.mode || 'enhanced',
      connState: 'stopped',
      scaleMode: vmData.scaleMode || 'fit',
      resolution: vmData.resolution || '1280x720',
      muted: Boolean(vmData.muted),
      inputCaptured: false,
      connectTimer: null
    };
    sessions.set(id, session);
    consoleState.activeVmId = id;
    onActiveVmChanged?.(id);

    const isGen1 = session.vm.generation === 1 || session.vm.gen === 1;
    const consoleOff = !isGen1 && (Boolean(session.adv.shielding) || session.adv.consoleSupport === false);
    if (!session.vm.running) {
      session.connState = 'stopped';
      render();
    } else if (consoleOff) {
      session.connState = 'disabled';
      render();
    } else {
      startSessionConnecting(session, session.mode);
    }
  }

  function open() {
    openTab(getVm?.(), getAdvSettings?.());
  }

  function close() {
    closeAllFlyouts();
    sessions.forEach((s) => {
      if (s.connectTimer) clearTimeout(s.connectTimer);
    });
    sessions.clear();
    consoleState.activeVmId = null;
    consoleState.isOpen = false;
    consoleState.isFullscreen = false;
    win.classList.remove('is-maximized', 'is-fullscreen');
    if (isHostedInWinOnWeb && typeof onRequestClose === 'function') {
      onRequestClose();
      return;
    }
    win.hidden = true;
  }

  // Left-top VM Switcher menu (switch in-place or pop out a separate window)
  switcherBtn?.addEventListener('click', (e) => {
    e.stopPropagation();
    const next = switcherFlyout ? switcherFlyout.hidden : false;
    closeAllFlyouts();
    if (!switcherFlyout || !switcherList) return;
    if (!next) {
      switcherFlyout.hidden = true;
      return;
    }
    const allVms = getAllVms ? getAllVms() : [];
    const openVmIds = getOpenVmIds ? getOpenVmIds() : new Set();
    switcherList.innerHTML = allVms.length
      ? allVms
          .map((item) => {
            const isActive = item.id === consoleState.activeVmId;
            const isOpenElsewhere = !isActive && openVmIds.has(item.id);
            const cached = sessions.get(item.id);
            const running = cached ? cached.vm.running : item.running;
            const osPath = item.osIcon ? `/ui-design/os/${escapeHtml(item.osIcon)}` : '/ui-design/os/Windows.svg';
            const statusText = isActive ? '当前' : isOpenElsewhere ? '已打开' : running ? '运行中' : '已关机';
            const popBtnHtml = !isActive && !isOpenElsewhere
              ? `<button type="button" class="console-switcher-pop" data-pop-vm-id="${escapeHtml(item.id)}" title="在新独立窗口中打开">
                   <span class="fi">&#xE8A7;</span>
                 </button>`
              : '';
            return `
              <div class="console-switcher-row">
                <button type="button" class="console-switcher-item ${isActive ? 'is-selected' : ''}" data-switch-vm-id="${escapeHtml(item.id)}" data-open-elsewhere="${isOpenElsewhere ? 'true' : 'false'}">
                  <img class="console-os-icon" src="${osPath}" alt="" draggable="false" />
                  <span style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeHtml(item.name)}</span>
                  <span style="font-size:11px;opacity:0.6;">${statusText}</span>
                </button>
                ${popBtnHtml}
              </div>
            `;
          })
          .join('')
      : '<div style="padding:8px 10px;font-size:12px;opacity:0.65;">暂无其他可用虚拟机</div>';

    switcherList.querySelectorAll('[data-switch-vm-id]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const vid = btn.getAttribute('data-switch-vm-id');
        const isElsewhere = btn.getAttribute('data-open-elsewhere') === 'true';
        const target = allVms.find((v) => v.id === vid);
        closeAllFlyouts();
        if (!target) return;
        if (isElsewhere) {
          onFocusExistingWindow?.(target.id);
          return;
        }
        openTab(target, getAdvSettings?.());
      });
    });

    switcherList.querySelectorAll('[data-pop-vm-id]').forEach((btn) => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const vid = btn.getAttribute('data-pop-vm-id');
        const target = allVms.find((v) => v.id === vid);
        closeAllFlyouts();
        if (!target) return;
        if (typeof onOpenNewWindow === 'function') {
          const cached = sessions.get(target.id);
          onOpenNewWindow(cached ? buildSessionPayload(cached) : { ...target, accentHex: getAccentHex?.() || '#4CC2FF' });
        } else {
          showToast('请在 WinOnWeb 桌面模式中打开独立窗口');
        }
      });
    });

    switcherFlyout.hidden = false;
  });

  closeBtn?.addEventListener('click', close);
  minBtn?.addEventListener('click', close);
  maxBtn?.addEventListener('click', () => {
    if (consoleState.isFullscreen || isHostedInWinOnWeb) return;
    const isMax = win.classList.toggle('is-maximized');
    if (maxIcon) maxIcon.innerHTML = isMax ? '&#xE923;' : '&#xE922;';
  });

  function toggleFlyoutMenu(flyout, e) {
    e?.stopPropagation();
    const open = flyout ? flyout.hidden : false;
    closeAllFlyouts();
    if (flyout) flyout.hidden = !open;
  }

  function applySessionMode(nextMode) {
    const session = activeSession();
    if (!session || session.mode === nextMode) return;
    closeAllFlyouts();
    if (session.vm.running) {
      startSessionConnecting(session, nextMode);
      showToast(nextMode === 'enhanced' ? '正在切换到增强会话' : '正在切换到基本会话');
    } else {
      session.mode = nextMode;
      render();
    }
  }

  modeBtn?.addEventListener('click', (e) => toggleFlyoutMenu(modeFlyout, e));
  modeFlyout?.querySelectorAll('[data-console-session]').forEach((item) => {
    item.addEventListener('click', () => {
      const targetMode = item.getAttribute('data-console-session');
      closeAllFlyouts();
      if (targetMode) applySessionMode(targetMode);
    });
  });
  fsModeBtn?.addEventListener('click', () => {
    const session = activeSession();
    if (!session) return;
    applySessionMode(session.mode === 'enhanced' ? 'basic' : 'enhanced');
  });

  resBtn?.addEventListener('click', (e) => {
    if (resBtn.disabled) return;
    toggleFlyoutMenu(resFlyout, e);
  });
  resFlyout?.querySelectorAll('[data-console-res]').forEach((item) => {
    item.addEventListener('click', () => {
      const nextRes = item.getAttribute('data-console-res');
      closeAllFlyouts();
      const session = activeSession();
      if (!session || !nextRes) return;
      session.resolution = nextRes;
      render();
      showToast(`已切换分辨率为 ${formatResText(nextRes)}`);
    });
  });

  scaleBtn?.addEventListener('click', (e) => toggleFlyoutMenu(scaleFlyout, e));
  scaleFlyout?.querySelectorAll('[data-console-zoom]').forEach((item) => {
    item.addEventListener('click', () => {
      const nextZoom = item.getAttribute('data-console-zoom');
      closeAllFlyouts();
      const session = activeSession();
      if (!session || !nextZoom) return;
      session.scaleMode = nextZoom;
      render();
    });
  });

  function stopActiveVm() {
    const session = activeSession();
    if (!session) return;
    session.vm.running = false;
    session.connState = 'stopped';
    onVmPowerChanged?.(session.vm);
    render();
  }

  function startActiveVm() {
    const session = activeSession();
    if (!session) return;
    if (!session.vm.running) {
      session.vm.running = true;
      onVmPowerChanged?.(session.vm);
    }
    startSessionConnecting(session, session.mode);
  }

  powerStartBtn?.addEventListener('click', () => {
    closeAllFlyouts();
    startActiveVm();
  });

  powerMainBtn?.addEventListener('click', () => {
    closeAllFlyouts();
    stopActiveVm();
    showToast('已发送关机指令');
  });

  powerDropBtn?.addEventListener('click', (e) => toggleFlyoutMenu(powerFlyout, e));

  powerFlyout?.querySelectorAll('[data-console-power]').forEach((item) => {
    item.addEventListener('click', () => {
      const act = item.getAttribute('data-console-power');
      closeAllFlyouts();
      const session = activeSession();
      if (!session) return;
      if (act === 'restart') {
        startSessionConnecting(session, session.mode);
        showToast('正在重启虚拟机');
      } else if (act === 'shutdown' || act === 'pause' || act === 'save' || act === 'turnoff') {
        stopActiveVm();
      }
    });
  });

  primaryActionBtn?.addEventListener('click', startActiveVm);

  function sendCad() {
    closeAllFlyouts();
    showToast('已发送 Ctrl + Alt + Del');
  }
  cadBtn?.addEventListener('click', sendCad);
  fsCadBtn?.addEventListener('click', sendCad);

  function takeScreenshot() {
    const session = activeSession();
    if (!session || session.connState !== 'connected') return;
    closeAllFlyouts();
    if (flashOverlay) {
      flashOverlay.hidden = true;
      void flashOverlay.offsetWidth;
      flashOverlay.hidden = false;
      setTimeout(() => {
        flashOverlay.hidden = true;
      }, 300);
    }
    showToast('已截取当前控制台画面');
  }
  shotBtn?.addEventListener('click', takeScreenshot);

  audioBtn?.addEventListener('click', () => {
    const session = activeSession();
    if (!session) return;
    closeAllFlyouts();
    session.muted = !session.muted;
    render();
    showToast(session.muted ? '控制台音频已静音' : '控制台音频已开启');
  });

  function setFullscreen(flag) {
    consoleState.isFullscreen = Boolean(flag);
    closeAllFlyouts();
    render();
  }
  fullscreenBtn?.addEventListener('click', () => setFullscreen(true));
  fsExitBtn?.addEventListener('click', () => setFullscreen(false));
  fsPinBtn?.addEventListener('click', () => {
    consoleState.fsPinned = !consoleState.fsPinned;
    render();
  });

  win.addEventListener('mousemove', (e) => {
    if (!consoleState.isFullscreen || consoleState.fsPinned || !fsBar) return;
    const rect = win.getBoundingClientRect();
    if (e.clientY - rect.top <= 48) {
      fsBar.hidden = false;
      if (fsHideTimer) clearTimeout(fsHideTimer);
    } else if (!fsBar.hidden) {
      if (fsHideTimer) clearTimeout(fsHideTimer);
      fsHideTimer = setTimeout(() => {
        if (consoleState.isFullscreen && !consoleState.fsPinned) {
          fsBar.hidden = true;
        }
      }, 1200);
    }
  });

  screenSurface?.addEventListener('click', () => {
    const session = activeSession();
    if (!session || session.connState !== 'connected') return;
    session.inputCaptured = true;
    render();
  });

  document.addEventListener('keydown', (e) => {
    if (!consoleState.isOpen) return;
    const session = activeSession();
    if (e.key === 'Escape') {
      if (session && session.inputCaptured) {
        session.inputCaptured = false;
        render();
      } else if (consoleState.isFullscreen) {
        setFullscreen(false);
      }
    }
  });

  win.addEventListener('click', (e) => {
    if (!e.target.closest('.console-anchor')) {
      closeAllFlyouts();
    }
  });

  // Fallback window dragging (when opened outside WinOnWeb desktop)
  if (!isHostedInWinOnWeb) {
    let dragging = false;
    let dragStartX = 0;
    let dragStartY = 0;
    let startLeft = 0;
    let startTop = 0;

    titleBar?.addEventListener('mousedown', (e) => {
      if (
        e.target.closest('.caption-buttons') ||
        e.target.closest('.console-anchor') ||
        e.target.closest('.console-icon-btn') ||
        e.target.closest('.console-hdr-btn') ||
        e.target.closest('.console-split-group') ||
        win.classList.contains('is-maximized') ||
        consoleState.isFullscreen
      ) {
        return;
      }
      dragging = true;
      const rect = win.getBoundingClientRect();
      win.style.left = `${rect.left}px`;
      win.style.top = `${rect.top}px`;
      win.style.transform = 'none';
      dragStartX = e.clientX;
      dragStartY = e.clientY;
      startLeft = rect.left;
      startTop = rect.top;
    });

    window.addEventListener('mousemove', (e) => {
      if (!dragging) return;
      const nextLeft = Math.max(8, Math.min(window.innerWidth - 180, startLeft + (e.clientX - dragStartX)));
      const nextTop = Math.max(8, Math.min(window.innerHeight - 80, startTop + (e.clientY - dragStartY)));
      win.style.left = `${nextLeft}px`;
      win.style.top = `${nextTop}px`;
    });

    window.addEventListener('mouseup', () => {
      dragging = false;
    });
  }

  return {
    open,
    openTab,
    close,
    syncTheme,
    syncVmState: () => {
      if (consoleState.isOpen) render();
    }
  };
}
