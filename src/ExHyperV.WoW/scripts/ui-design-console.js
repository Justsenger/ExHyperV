export function createConsoleController({
  getVm,
  getAllVms,
  getAdvSettings,
  getTheme,
  getAccentHex,
  isHostedInWinOnWeb = false,
  onVmPowerChanged,
  onTabDragMove,
  onTabDragDrop,
  onDragSpacerRect,
  onRequestClose
}) {
  const win = document.getElementById('vmConsoleWindow');
  if (!win) return { open: () => {}, openTab: () => {}, closeTab: () => {}, syncTheme: () => {}, syncVmState: () => {} };

  const titleBar = document.getElementById('consoleTitleBar');
  const tabsStrip = document.getElementById('consoleTabsStrip');
  const dragSpacer = document.getElementById('consoleDragSpacer');
  const newTabBtn = document.getElementById('consoleNewTabBtn');
  const newTabFlyout = document.getElementById('consoleNewTabFlyout');
  const newTabList = document.getElementById('consoleNewTabList');
  const dragGhost = document.getElementById('consoleDragGhost');
  const dragGhostIcon = document.getElementById('consoleDragGhostIcon');
  const dragGhostName = document.getElementById('consoleDragGhostName');

  const minBtn = document.getElementById('consoleMinimizeBtn');
  const maxBtn = document.getElementById('consoleMaximizeBtn');
  const maxIcon = document.getElementById('consoleMaximizeIcon');
  const closeBtn = document.getElementById('consoleCloseBtn');

  const powerBtn = document.getElementById('consolePowerBtn');
  const powerFlyout = document.getElementById('consolePowerFlyout');
  const cadBtn = document.getElementById('consoleCadBtn');
  const modeBtn = document.getElementById('consoleModeBtn');

  const moreBtn = document.getElementById('consoleMoreBtn');
  const moreFlyout = document.getElementById('consoleMoreFlyout');
  const scaleToggleItem = document.getElementById('consoleScaleToggleItem');
  const scaleCheckIcon = document.getElementById('consoleScaleCheckIcon');
  const powerPrimaryItem = document.getElementById('consolePowerPrimaryItem');
  const powerPrimaryIcon = document.getElementById('consolePowerPrimaryIcon');
  const powerPrimaryText = document.getElementById('consolePowerPrimaryText');
  const powerRunningGroup = document.getElementById('consolePowerRunningGroup');

  const shotBtn = document.getElementById('consoleShotBtn');
  const fullscreenBtn = document.getElementById('consoleFullscreenBtn');

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

  const consoleState = {
    isOpen: false,
    isFullscreen: false,
    fsPinned: true,
    activeTabId: null,
    tabs: []
  };

  let toastTimer = null;
  let fsHideTimer = null;

  function activeTab() {
    return consoleState.tabs.find((t) => t.id === consoleState.activeTabId) || consoleState.tabs[0] || null;
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
    if (moreFlyout) moreFlyout.hidden = true;
    if (newTabFlyout) newTabFlyout.hidden = true;
  }

  function syncTheme() {
    const theme = getTheme ? getTheme() : 'dark';
    const accent = getAccentHex ? getAccentHex() : '#4CC2FF';
    win.setAttribute('data-theme', theme === 'light' ? 'light' : 'dark');
    win.style.setProperty('--VmHeaderCheckedBrush', accent);
  }

  function startTabConnecting(tab, targetMode) {
    if (tab.connectTimer) clearTimeout(tab.connectTimer);
    tab.mode = targetMode;
    tab.connState = 'connecting';
    tab.inputCaptured = false;
    render();
    tab.connectTimer = setTimeout(() => {
      const isGen1 = tab.vm.generation === 1 || tab.vm.gen === 1;
      const consoleOff = !isGen1 && (Boolean(tab.adv.shielding) || tab.adv.consoleSupport === false);
      if (!tab.vm.running) {
        tab.connState = 'stopped';
      } else if (consoleOff) {
        tab.connState = 'disabled';
      } else {
        tab.connState = 'connected';
      }
      render();
    }, 460);
  }

  function renderTabsStrip() {
    if (!tabsStrip) return;
    tabsStrip.innerHTML = consoleState.tabs
      .map((tab) => {
        const isActive = tab.id === consoleState.activeTabId;
        const osPath = tab.vm.osIcon ? `/ui-design/os/${escapeHtml(tab.vm.osIcon)}` : '/ui-design/os/Windows.svg';
        const dotCls =
          tab.connState === 'connected'
            ? 'is-connected'
            : tab.connState === 'connecting'
              ? 'is-connecting'
              : '';
        return `
          <div class="console-tab ${isActive ? 'is-active' : ''}" data-console-tab-id="${escapeHtml(tab.id)}" role="tab" aria-selected="${isActive}">
            <img class="console-os-icon" src="${osPath}" alt="" draggable="false" />
            <span class="console-tab-dot ${dotCls}"></span>
            <span class="console-tab-name">${escapeHtml(tab.vm.name)}</span>
            <button type="button" class="console-tab-close" data-console-tab-close="${escapeHtml(tab.id)}" title="关闭此页签">
              <span class="fi">&#xE8BB;</span>
            </button>
          </div>
        `;
      })
      .join('');

    tabsStrip.querySelectorAll('[data-console-tab-id]').forEach((el) => {
      el.addEventListener('pointerdown', (e) => {
        if (e.button !== 0 || e.target.closest('[data-console-tab-close]')) return;
        const tid = el.getAttribute('data-console-tab-id');
        if (!tid) return;

        // Switch tab immediately without rebuilding tabsStrip DOM (preserves pointer capture)
        if (tid !== consoleState.activeTabId) {
          const prev = activeTab();
          if (prev) prev.inputCaptured = false;
          consoleState.activeTabId = tid;
          tabsStrip.querySelectorAll('[data-console-tab-id]').forEach((node) => {
            const active = node.getAttribute('data-console-tab-id') === tid;
            node.classList.toggle('is-active', active);
            node.setAttribute('aria-selected', String(active));
          });
          closeAllFlyouts();
          renderActiveTabContent();
        }

        const startX = e.clientX;
        const startY = e.clientY;
        let isDraggingOut = false;
        let hasMoved = false;
        let lastMoveSent = 0;
        const tabObj = consoleState.tabs.find((t) => t.id === tid);
        if (!tabObj) return;

        try {
          el.setPointerCapture(e.pointerId);
        } catch {}

        const onMove = (ev) => {
          const dx = ev.clientX - startX;
          const dy = ev.clientY - startY;
          if (!hasMoved && Math.hypot(dx, dy) < 6) return;
          hasMoved = true;

          const outOfWindow =
            ev.clientX < 0 ||
            ev.clientY < 0 ||
            ev.clientX > window.innerWidth ||
            ev.clientY > window.innerHeight;
          const tearThreshold = Math.abs(dy) > 28 || outOfWindow;

          if (tearThreshold && isHostedInWinOnWeb) {
            isDraggingOut = true;
            el.classList.add('is-dragging-out');
            if (dragGhost && dragGhostIcon && dragGhostName) {
              dragGhostIcon.src = tabObj.vm.osIcon
                ? `/ui-design/os/${tabObj.vm.osIcon}`
                : '/ui-design/os/Windows.svg';
              dragGhostName.textContent = tabObj.vm.name || '虚拟机';
              dragGhost.style.left = `${Math.max(4, Math.min(window.innerWidth - 140, ev.clientX + 10))}px`;
              dragGhost.style.top = `${Math.max(4, Math.min(window.innerHeight - 36, ev.clientY - 14))}px`;
              dragGhost.hidden = false;
            }
            if (typeof onTabDragMove === 'function') {
              const now = performance.now();
              if (now - lastMoveSent > 40) {
                lastMoveSent = now;
                onTabDragMove({ active: true, clientX: ev.clientX, clientY: ev.clientY });
              }
            }
          } else {
            if (isDraggingOut && typeof onTabDragMove === 'function') {
              onTabDragMove({ active: false, clientX: ev.clientX, clientY: ev.clientY });
            }
            isDraggingOut = false;
            el.classList.remove('is-dragging-out');
            if (dragGhost) dragGhost.hidden = true;

            // In-strip horizontal reorder using insertBefore (preserves DOM node & pointer capture)
            if (consoleState.tabs.length > 1) {
              const curIdx = consoleState.tabs.findIndex((t) => t.id === tid);
              const tabEls = Array.from(tabsStrip.querySelectorAll('[data-console-tab-id]'));
              for (let i = 0; i < tabEls.length; i++) {
                if (i === curIdx) continue;
                const rect = tabEls[i].getBoundingClientRect();
                const midX = rect.left + rect.width / 2;
                if (i < curIdx && ev.clientX < midX) {
                  const [moved] = consoleState.tabs.splice(curIdx, 1);
                  consoleState.tabs.splice(i, 0, moved);
                  tabsStrip.insertBefore(el, tabEls[i]);
                  break;
                }
                if (i > curIdx && ev.clientX > midX) {
                  const [moved] = consoleState.tabs.splice(curIdx, 1);
                  consoleState.tabs.splice(i, 0, moved);
                  tabsStrip.insertBefore(el, tabEls[i].nextSibling);
                  break;
                }
              }
            }
          }
        };

        const cleanup = () => {
          el.removeEventListener('pointermove', onMove);
          el.removeEventListener('pointerup', onUp);
          el.removeEventListener('pointercancel', onCancel);
          el.classList.remove('is-dragging-out');
          if (dragGhost) dragGhost.hidden = true;
          try {
            el.releasePointerCapture(e.pointerId);
          } catch {}
        };

        const onUp = (ev) => {
          cleanup();
          if (typeof onTabDragMove === 'function') {
            onTabDragMove({ active: false, clientX: ev.clientX, clientY: ev.clientY });
          }
          if (!isDraggingOut || typeof onTabDragDrop !== 'function') return;
          const currentTab = consoleState.tabs.find((t) => t.id === tid);
          if (!currentTab) return;
          onTabDragDrop({
            tabId: tid,
            tabCount: consoleState.tabs.length,
            clientX: ev.clientX,
            clientY: ev.clientY,
            payload: buildTabPayload(currentTab)
          });
        };

        const onCancel = () => {
          cleanup();
          if (typeof onTabDragMove === 'function') {
            onTabDragMove({ active: false, clientX: 0, clientY: 0 });
          }
        };

        el.addEventListener('pointermove', onMove);
        el.addEventListener('pointerup', onUp);
        el.addEventListener('pointercancel', onCancel);
      });
    });

    tabsStrip.querySelectorAll('[data-console-tab-close]').forEach((btn) => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const tid = btn.getAttribute('data-console-tab-close');
        if (tid) closeTab(tid);
      });
    });

    notifyDragSpacerRect();
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
  tabsStrip?.addEventListener('wheel', (e) => {
    if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) {
      tabsStrip.scrollLeft += e.deltaY;
      e.preventDefault();
    }
  }, { passive: false });

  function render() {
    renderTabsStrip();
    renderActiveTabContent();
  }

  function renderActiveTabContent() {
    const tab = activeTab();
    if (!tab) return;

    const vm = tab.vm;
    const adv = tab.adv;
    const isGen1 = vm.generation === 1 || vm.gen === 1;
    const consoleOff = !isGen1 && (Boolean(adv.shielding) || adv.consoleSupport === false);
    const isRunning = Boolean(vm.running);

    if (!isRunning && tab.connState !== 'connecting') {
      tab.connState = 'stopped';
      tab.inputCaptured = false;
    } else if (consoleOff && tab.connState !== 'connecting') {
      tab.connState = 'disabled';
      tab.inputCaptured = false;
    }

    const vmName = vm.name || '虚拟机';
    const osPath = vm.osIcon ? `/ui-design/os/${vm.osIcon}` : '/ui-design/os/Windows.svg';

    if (emptyOs) emptyOs.src = osPath;
    if (fsVmName) fsVmName.textContent = vmName;
    if (guestHostname) guestHostname.textContent = vmName.toUpperCase().replace(/[^A-Z0-9-]/g, '-');

    const isEnhanced = tab.mode === 'enhanced';
    const connected = tab.connState === 'connected';

    if (powerBtn) {
      powerBtn.classList.toggle('is-running', isRunning);
      powerBtn.classList.toggle('is-stopped', !isRunning);
      powerBtn.title = isRunning ? '电源操作（运行中）' : '启动虚拟机';
    }
    if (modeBtn) {
      modeBtn.classList.toggle('is-enhanced-on', isEnhanced);
      modeBtn.title = isEnhanced ? '当前为增强会话（点击切换为基本会话）' : '当前为基本会话（点击切换为增强会话）';
    }
    if (scaleCheckIcon) {
      scaleCheckIcon.classList.toggle('is-unchecked', tab.scaleMode !== 'fit');
    }
    if (fsModeLabel) {
      fsModeLabel.textContent = isEnhanced ? '增强会话' : '基本会话';
    }

    if (powerPrimaryIcon && powerPrimaryText && powerRunningGroup) {
      powerPrimaryIcon.innerHTML = isRunning ? '&#xE7E8;' : '&#xE768;';
      powerPrimaryText.textContent = isRunning ? '关机' : '启动';
      powerRunningGroup.hidden = !isRunning;
    }

    if (cadBtn) cadBtn.disabled = !connected;
    if (fsCadBtn) fsCadBtn.disabled = !connected;
    if (shotBtn) shotBtn.disabled = !connected;

    const displayRes = isEnhanced ? formatResText(tab.resolution) : '1024 × 768';
    if (guestResLabel) guestResLabel.textContent = displayRes;
    if (guestSessionTag) guestSessionTag.textContent = isEnhanced ? '增强会话' : '基本会话';

    if (viewport) {
      viewport.classList.toggle('is-actual-scale', tab.scaleMode === 'actual');
    }
    if (screenSurface) {
      screenSurface.classList.toggle('is-captured', connected && tab.inputCaptured);
    }

    if (emptyState && emptySpinner && emptyOs && emptyTitle && emptyDesc && primaryActionBtn) {
      if (connected) {
        emptyState.hidden = true;
      } else {
        emptyState.hidden = false;
        if (tab.connState === 'connecting') {
          emptySpinner.hidden = false;
          emptyOs.hidden = true;
          emptyTitle.textContent = isEnhanced ? '正在建立增强会话' : '正在建立基本会话';
          emptyDesc.textContent = `正在连接 ${vmName} 的控制台通道，请稍候`;
          primaryActionBtn.hidden = true;
        } else if (tab.connState === 'disabled') {
          emptySpinner.hidden = true;
          emptyOs.hidden = false;
          emptyTitle.textContent = '控制台已被禁用';
          emptyDesc.textContent = '已在虚拟机设置中关闭控制台支持，请在高级设置中重新启用';
          primaryActionBtn.hidden = true;
        } else if (tab.connState === 'disconnected') {
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
    if (fsBar) {
      fsBar.hidden = !consoleState.isFullscreen;
    }
    if (fsPinBtn) {
      fsPinBtn.classList.toggle('is-pinned', consoleState.fsPinned);
    }
  }

  function openTab(vmPayload, advPayload) {
    syncTheme();
    consoleState.isOpen = true;
    win.hidden = false;

    const vmData = vmPayload || getVm?.() || { id: 'vm-1', name: 'Windows 11', running: true, osIcon: 'Windows.svg', generation: 2 };
    const advData = advPayload || getAdvSettings?.() || { consoleSupport: true, shielding: false };
    const tabId = vmData.id || `vm-${Date.now()}`;

    const existing = consoleState.tabs.find((t) => t.id === tabId);
    if (existing) {
      existing.vm = { ...existing.vm, ...vmData };
      existing.adv = { ...existing.adv, ...advData };
      consoleState.activeTabId = existing.id;
      render();
      return;
    }

    const newTab = {
      id: tabId,
      vm: { ...vmData },
      adv: { ...advData },
      mode: vmData.mode || 'enhanced',
      connState: 'stopped',
      scaleMode: vmData.scaleMode || 'fit',
      resolution: vmData.resolution || '1280x720',
      clipboardEnabled: vmData.clipboardEnabled !== false,
      inputCaptured: false,
      connectTimer: null
    };
    consoleState.tabs.push(newTab);
    consoleState.activeTabId = newTab.id;

    const isGen1 = newTab.vm.generation === 1 || newTab.vm.gen === 1;
    const consoleOff = !isGen1 && (Boolean(newTab.adv.shielding) || newTab.adv.consoleSupport === false);
    if (!newTab.vm.running) {
      newTab.connState = 'stopped';
      render();
    } else if (consoleOff) {
      newTab.connState = 'disabled';
      render();
    } else {
      startTabConnecting(newTab, newTab.mode);
    }
  }

  function closeTab(tabId) {
    const idx = consoleState.tabs.findIndex((t) => t.id === tabId);
    if (idx === -1) return;
    const removed = consoleState.tabs[idx];
    if (removed.connectTimer) clearTimeout(removed.connectTimer);
    consoleState.tabs.splice(idx, 1);

    if (consoleState.tabs.length === 0) {
      close();
      return;
    }
    if (consoleState.activeTabId === tabId) {
      const next = consoleState.tabs[Math.min(idx, consoleState.tabs.length - 1)];
      consoleState.activeTabId = next.id;
    }
    render();
  }

  function buildTabPayload(tab) {
    return {
      id: tab.vm.id,
      name: tab.vm.name,
      running: Boolean(tab.vm.running),
      osIcon: tab.vm.osIcon || 'Windows.svg',
      generation: tab.vm.generation ?? tab.vm.gen ?? 2,
      consoleSupport: tab.adv.consoleSupport !== false,
      shielding: Boolean(tab.adv.shielding),
      mode: tab.mode,
      scaleMode: tab.scaleMode,
      resolution: tab.resolution,
      clipboardEnabled: tab.clipboardEnabled,
      accentHex: getAccentHex ? getAccentHex() : '#4CC2FF'
    };
  }

  function open() {
    openTab(getVm?.(), getAdvSettings?.());
  }

  function close() {
    closeAllFlyouts();
    consoleState.tabs.forEach((t) => {
      if (t.connectTimer) clearTimeout(t.connectTimer);
    });
    consoleState.tabs = [];
    consoleState.activeTabId = null;
    consoleState.isOpen = false;
    consoleState.isFullscreen = false;
    win.classList.remove('is-maximized', 'is-fullscreen');
    if (isHostedInWinOnWeb && typeof onRequestClose === 'function') {
      onRequestClose();
      return;
    }
    win.hidden = true;
  }

  // New tab menu (+)
  newTabBtn?.addEventListener('click', (e) => {
    e.stopPropagation();
    const next = newTabFlyout ? newTabFlyout.hidden : false;
    closeAllFlyouts();
    if (!newTabFlyout || !newTabList) return;
    if (!next) {
      newTabFlyout.hidden = true;
      return;
    }
    const allVms = getAllVms ? getAllVms() : [];
    newTabList.innerHTML = allVms.length
      ? allVms
          .map((item) => {
            const alreadyOpen = consoleState.tabs.some((t) => t.id === item.id);
            const osPath = item.osIcon ? `/ui-design/os/${escapeHtml(item.osIcon)}` : '/ui-design/os/Windows.svg';
            const stateTxt = item.running ? '运行中' : '已关机';
            return `
              <button type="button" class="console-flyout-item ${alreadyOpen ? 'is-selected' : ''}" data-newtab-vm-id="${escapeHtml(item.id)}">
                <img class="console-newtab-os" src="${osPath}" alt="" draggable="false" />
                <span style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${escapeHtml(item.name)}</span>
                <span style="font-size:11px;opacity:0.65;">${alreadyOpen ? '已打开' : stateTxt}</span>
              </button>
            `;
          })
          .join('')
      : '<div style="padding:8px 10px;font-size:12px;opacity:0.65;">暂无其他可用虚拟机</div>';

    newTabList.querySelectorAll('[data-newtab-vm-id]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const vid = btn.getAttribute('data-newtab-vm-id');
        const target = allVms.find((v) => v.id === vid);
        closeAllFlyouts();
        if (target) openTab(target, getAdvSettings?.());
      });
    });
    newTabFlyout.hidden = false;
  });

  closeBtn?.addEventListener('click', close);
  minBtn?.addEventListener('click', close);
  maxBtn?.addEventListener('click', () => {
    if (consoleState.isFullscreen || isHostedInWinOnWeb) return;
    const isMax = win.classList.toggle('is-maximized');
    if (maxIcon) maxIcon.innerHTML = isMax ? '&#xE923;' : '&#xE922;';
  });

  function toggleSessionMode() {
    const tab = activeTab();
    if (!tab) return;
    const nextMode = tab.mode === 'enhanced' ? 'basic' : 'enhanced';
    closeAllFlyouts();
    if (tab.vm.running) {
      startTabConnecting(tab, nextMode);
      showToast(nextMode === 'enhanced' ? '正在切换到增强会话' : '正在切换到基本会话');
    } else {
      tab.mode = nextMode;
      render();
    }
  }

  modeBtn?.addEventListener('click', toggleSessionMode);
  fsModeBtn?.addEventListener('click', toggleSessionMode);

  scaleToggleItem?.addEventListener('click', () => {
    const tab = activeTab();
    if (!tab) return;
    tab.scaleMode = tab.scaleMode === 'fit' ? 'actual' : 'fit';
    closeAllFlyouts();
    render();
  });

  powerBtn?.addEventListener('click', (e) => {
    e.stopPropagation();
    const next = powerFlyout ? powerFlyout.hidden : false;
    closeAllFlyouts();
    if (powerFlyout) powerFlyout.hidden = !next;
  });

  powerFlyout?.querySelectorAll('[data-console-power]').forEach((item) => {
    item.addEventListener('click', () => {
      const act = item.getAttribute('data-console-power');
      closeAllFlyouts();
      const tab = activeTab();
      if (!tab) return;
      if (act === 'primary') {
        if (tab.vm.running) {
          tab.vm.running = false;
          tab.connState = 'stopped';
          onVmPowerChanged?.(tab.vm);
          render();
        } else {
          tab.vm.running = true;
          onVmPowerChanged?.(tab.vm);
          startTabConnecting(tab, tab.mode);
        }
      } else if (act === 'restart') {
        startTabConnecting(tab, tab.mode);
        showToast('正在重启虚拟机');
      } else if (act === 'pause' || act === 'save' || act === 'turnoff') {
        tab.vm.running = false;
        tab.connState = 'stopped';
        onVmPowerChanged?.(tab.vm);
        render();
      }
    });
  });

  primaryActionBtn?.addEventListener('click', () => {
    const tab = activeTab();
    if (!tab) return;
    if (!tab.vm.running) {
      tab.vm.running = true;
      onVmPowerChanged?.(tab.vm);
    }
    startTabConnecting(tab, tab.mode);
  });

  function sendCad() {
    closeAllFlyouts();
    showToast('已发送 Ctrl + Alt + Del');
  }
  cadBtn?.addEventListener('click', sendCad);
  fsCadBtn?.addEventListener('click', sendCad);

  moreBtn?.addEventListener('click', (e) => {
    e.stopPropagation();
    const next = moreFlyout ? moreFlyout.hidden : false;
    closeAllFlyouts();
    if (moreFlyout) moreFlyout.hidden = !next;
  });

  function takeScreenshot() {
    const tab = activeTab();
    if (!tab || tab.connState !== 'connected') return;
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
    const tab = activeTab();
    if (!tab || tab.connState !== 'connected') return;
    tab.inputCaptured = true;
    render();
  });

  document.addEventListener('keydown', (e) => {
    if (!consoleState.isOpen) return;
    const tab = activeTab();
    if (e.key === 'Escape') {
      if (tab && tab.inputCaptured) {
        tab.inputCaptured = false;
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

  // Fallback window dragging (only when not hosted inside WinOnWeb window chrome)
  if (!isHostedInWinOnWeb) {
    let dragging = false;
    let dragStartX = 0;
    let dragStartY = 0;
    let startLeft = 0;
    let startTop = 0;

    titleBar?.addEventListener('mousedown', (e) => {
      if (
        e.target.closest('.caption-buttons') ||
        e.target.closest('.console-tab') ||
        e.target.closest('.console-tab-add-btn') ||
        e.target.closest('.console-tab-detach-btn') ||
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
    closeTab,
    close,
    syncTheme,
    syncVmState: () => {
      if (consoleState.isOpen) render();
    }
  };
}
