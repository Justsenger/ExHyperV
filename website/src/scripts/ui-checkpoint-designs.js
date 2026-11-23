// Fixed Winning Card Skeleton + Unified Directions A & C Snapshot Placeholder Art
import {
  DEFAULT_CHECKPOINT_ART_MODE,
  checkpointArtUsesBrandIcon,
  getCheckpointArtDataUrl,
  normalizeCheckpointArtMode
} from './checkpoint-placeholder-art.js';

function renderNodeThumbContent(homeMode) {
  const mode = normalizeCheckpointArtMode(homeMode);
  const dataUrl = getCheckpointArtDataUrl(mode);
  const showBrand = checkpointArtUsesBrandIcon(mode);
  return `
    <div class="cp-art-thumb is-${mode}" aria-hidden="true">
      ${dataUrl ? `<img class="cp-art-img" src="${dataUrl}" alt="" draggable="false" />` : ''}
      ${showBrand ? '<img class="cp-art-brand-icon" src="/ui-design/AppIcon.png" alt="" draggable="false" />' : ''}
    </div>
  `;
}

export function renderCheckpointWorkbench(host, { state, initialNodes }) {
  const homeMode = state.checkpointCandidate || DEFAULT_CHECKPOINT_ART_MODE;
  const nodes = state.checkpointNodes;
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const childrenMap = new Map();
  nodes.forEach((n) => childrenMap.set(n.id, []));
  nodes.forEach((n) => {
    if (n.parentId && childrenMap.has(n.parentId)) {
      childrenMap.get(n.parentId).push(n);
    }
  });

  const currentNode = nodes.find((n) => n.type === 'Current') || nodes[nodes.length - 1];
  const sel = byId.get(state.checkpointSelectedId) || currentNode;
  state.checkpointSelectedId = sel.id;
  const openNode = state.checkpointMenuOpenId ? byId.get(state.checkpointMenuOpenId) : null;

  const activeChainIds = new Set();
  let cursor = currentNode;
  let guard = 0;
  while (cursor && guard < 100) {
    activeChainIds.add(cursor.id);
    cursor = cursor.parentId ? byId.get(cursor.parentId) : null;
    guard++;
  }

  const shortTime = (d) => (d ? d.slice(5, 16) : '');

  const createCheckpoint = () => {
    const newId = `node-${Date.now()}`;
    const newName = (state.checkpointNewName || '').trim() || `检查点 (${nodes.length})`;
    const newSnap = {
      id: newId,
      parentId: currentNode.parentId,
      name: newName,
      date: '2026-11-22 15:20:00',
      type: 'Snapshot'
    };
    currentNode.parentId = newId;
    nodes.splice(nodes.indexOf(currentNode), 0, newSnap);
    state.checkpointNewName = '';
    state.checkpointMenuOpenId = null;
    state.checkpointSelectedId = newId;
    renderCheckpointWorkbench(host, { state, initialNodes });
  };

  const canMergeNode = (node) => {
    if (!node || node.type !== 'Snapshot' || !node.parentId) return false;
    const parent = byId.get(node.parentId);
    if (!parent || parent.type !== 'Snapshot') return false;
    const parentKids = childrenMap.get(parent.id) || [];
    return parentKids.length === 1 && parentKids[0]?.id === node.id;
  };

  const performAction = (act, targetId) => {
    const target = byId.get(targetId);
    if (!target) return;
    if (act === 'restore') {
      currentNode.parentId = target.id;
      state.checkpointSelectedId = currentNode.id;
      state.checkpointMenuOpenId = null;
    } else if (act === 'delete') {
      const removeIds = new Set();
      const collect = (id) => {
        if (id === currentNode.id) return;
        removeIds.add(id);
        (childrenMap.get(id) || []).forEach((k) => collect(k.id));
      };
      collect(target.id);
      if (removeIds.has(currentNode.parentId) && target.parentId) {
        currentNode.parentId = target.parentId;
      }
      state.checkpointNodes = nodes.filter((n) => !removeIds.has(n.id));
      state.checkpointSelectedId = currentNode.id;
      state.checkpointMenuOpenId = null;
      state.checkpointConfirmDeleteId = null;
    } else if (act === 'merge') {
      if (!canMergeNode(target)) return;
      const parent = byId.get(target.parentId);
      nodes.forEach((n) => {
        if (n.parentId === target.id) {
          n.parentId = parent.id;
        }
      });
      parent.date = target.date;
      state.checkpointNodes = nodes.filter((n) => n.id !== target.id);
      state.checkpointSelectedId = parent.id;
      state.checkpointMenuOpenId = null;
    }
    renderCheckpointWorkbench(host, { state, initialNodes });
  };

  const layoutTopo = () => {
    const roots = nodes.filter((n) => !n.parentId || !byId.has(n.parentId));
    const pos = new Map();
    const colStep = 212;
    const rowStep = 136;
    let leafRow = 0;
    const assign = (node, depth) => {
      const kids = childrenMap.get(node.id) || [];
      const x = 16 + depth * colStep;
      if (kids.length === 0) {
        const y = 20 + leafRow * rowStep;
        leafRow++;
        pos.set(node.id, { x, y });
        return y;
      }
      const ys = kids.map((k) => assign(k, depth + 1));
      const y = (ys[0] + ys[ys.length - 1]) / 2;
      pos.set(node.id, { x, y });
      return y;
    };
    roots.forEach((r) => assign(r, 0));
    let maxX = 800;
    let maxY = 380;
    pos.forEach((p) => {
      maxX = Math.max(maxX, p.x + 210);
      maxY = Math.max(maxY, p.y + 155);
    });
    return { pos, width: maxX, height: maxY };
  };

  const renderContextMenu = (node) => {
    if (node.type === 'Current') {
      return `
        <div class="cp-ctx-input-row" style="padding:4px 6px;">
          <input class="cp-input" id="cp2NewNameInput" type="text" placeholder="名称（可选）" value="${state.checkpointNewName || ''}" style="width:148px;height:25px;" />
        </div>
        <div class="cp-ctx-sep"></div>
        <button type="button" class="cp-ctx-item" id="cp2CreateBtn">
          <span class="fi">&#xE710;</span><span>新建检查点</span>
        </button>
      `;
    }
    if (state.checkpointRenaming) {
      return `
        <div style="display:flex;align-items:center;gap:4px;padding:4px;">
          <input class="cp-input" id="cp2RenameInput" type="text" value="${node.name}" style="width:136px;height:25px;" />
          <button class="cp2-mini-btn is-primary" type="button" id="cp2RenameSave">保存</button>
          <button class="cp2-mini-btn" type="button" id="cp3CancelRename">取消</button>
        </div>
      `;
    }
    const mergeAllowed = canMergeNode(node);
    const mergeTip = mergeAllowed ? '' : '父节点为初始起点或存在其他分支，不可向父节点合并';
    return `
      <button type="button" class="cp-ctx-item" data-cp2-act="restore" data-cp2-id="${node.id}">
        <span class="fi">&#xE8AB;</span><span>应用</span>
      </button>
      <button type="button" class="cp-ctx-item" data-cp2-rename="${node.id}">
        <span class="fi">&#xE70F;</span><span>重命名</span>
      </button>
      <div class="cp-ctx-sep"></div>
      <button type="button" class="cp-ctx-item" data-cp2-act="merge" data-cp2-id="${node.id}" ${mergeAllowed ? '' : 'disabled'} title="${mergeTip}">
        <span class="fi">&#xE97A;</span><span>合并</span>
      </button>
      <button type="button" class="cp-ctx-item is-danger" data-cp2-delete-prompt="${node.id}">
        <span class="fi">&#xE74D;</span><span>删除</span>
      </button>
    `;
  };

  const { pos, width, height } = layoutTopo();
  const cardW = 176;
  const cardH = 110;

  const pathsSvg = nodes
    .filter((n) => n.parentId && pos.has(n.parentId) && pos.has(n.id))
    .map((n) => {
      const pPos = pos.get(n.parentId);
      const nPos = pos.get(n.id);
      const x1 = pPos.x + cardW;
      const y1 = pPos.y + cardH / 2;
      const x2 = nPos.x;
      const y2 = nPos.y + cardH / 2;
      const cx = (x1 + x2) / 2;
      const onChain = activeChainIds.has(n.id) && activeChainIds.has(n.parentId);
      if (n.wormhole) {
        return `<path d="M ${x1} ${y1} C ${cx} ${y1}, ${cx} ${y2}, ${x2} ${y2}" fill="none" stroke="#f59e0b" stroke-width="2.4" />`;
      }
      if (onChain) {
        return `<path d="M ${x1} ${y1} C ${cx} ${y1}, ${cx} ${y2}, ${x2} ${y2}" fill="none" stroke="var(--VmAccentBrush)" stroke-width="2.4" />`;
      }
      return `<path d="M ${x1} ${y1} C ${cx} ${y1}, ${cx} ${y2}, ${x2} ${y2}" fill="none" stroke="rgba(160,160,160,0.45)" stroke-width="1.6" stroke-dasharray="5,4" />`;
    })
    .join('');

  const nodesHtml = nodes
    .map((n) => {
      const p = pos.get(n.id);
      const isCur = n.type === 'Current';
      const isSel = n.id === sel.id;
      const badge = isCur ? '<span class="cp-badge is-live">当前</span>' : '';

      return `
        <div class="cp-topo-node ${isSel ? 'is-selected' : ''} ${n.wormhole ? 'is-wormhole' : ''}" data-st-node="${n.id}">
          <div class="cp-node-thumb">
            ${renderNodeThumbContent(homeMode)}
            ${badge ? `<div class="cp-node-badges">${badge}</div>` : ''}
          </div>
          <div class="cp-node-body">
            <div class="cp-node-title" title="${n.name}">${n.name}</div>
            <div class="cp-node-meta"><span>${shortTime(n.date)}</span></div>
          </div>
        </div>
      `.replace(
        `data-st-node="${n.id}"`,
        `data-st-node="${n.id}" style="left:${p.x}px;top:${p.y}px;width:${cardW}px;height:${cardH}px;padding:0;display:block;"`
      );
    })
    .join('');

  let flyoutHtml = '';
  if (openNode && openNode.type !== 'Genesis' && pos.has(openNode.id)) {
    const pOpen = pos.get(openNode.id);
    const menuX = Number.isFinite(state.checkpointCtxX)
      ? Math.max(8, Math.min(state.checkpointCtxX, width - 170))
      : Math.min(pOpen.x + cardW - 18, width - 170);
    const menuY = Number.isFinite(state.checkpointCtxY)
      ? Math.max(8, Math.min(state.checkpointCtxY, height - 150))
      : pOpen.y + 42;
    flyoutHtml = `<div class="cp-ctx-menu" data-cp-keep-open="1" style="left:${menuX}px;top:${menuY}px;">${renderContextMenu(openNode)}</div>`;
  }

  const confirmDeleteNode = state.checkpointConfirmDeleteId
    ? byId.get(state.checkpointConfirmDeleteId)
    : null;
  const confirmDialogHtml = confirmDeleteNode
    ? `
      <div class="cp-confirm-backdrop" id="cpConfirmBackdrop" data-cp-keep-open="1">
        <div class="cp-confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="cpConfirmTitle">
          <div class="cp-confirm-title" id="cpConfirmTitle">确认删除检查点分支</div>
          <div class="cp-confirm-body">
            <p>将删除「<strong>${confirmDeleteNode.name}</strong>」以及它后面的所有子检查点，此操作不可撤销。</p>
            <p class="cp-confirm-sub">若仅需移除当前节点并保留后续节点，请使用「合并」。</p>
          </div>
          <div class="cp-confirm-actions">
            <button type="button" class="cp-confirm-btn is-danger" id="cpConfirmDeleteOk">删除</button>
            <button type="button" class="cp-confirm-btn" id="cpConfirmDeleteCancel">取消</button>
          </div>
        </div>
      </div>
    `
    : '';

  const panX = Number(state.checkpointPanX) || 0;
  const panY = Number(state.checkpointPanY) || 0;
  const zoom = Number(state.checkpointZoom) || 1;
  const isFullscreen = Boolean(state.checkpointFullscreen);
  const winEl = document.getElementById('winuiWindow');
  if (winEl && typeof winEl.classList?.toggle === 'function') {
    winEl.classList.toggle('is-cp-fullscreen', isFullscreen);
  }

  host.innerHTML = `
    <div class="cp2-root" style="padding:0 12px 12px 12px;">
      <div class="cp6-stage" id="cpCanvasBg">
        <div class="cp6-pan-layer" id="cpPanLayer" style="width:${width}px;height:${height}px;transform:translate3d(${panX}px, ${panY}px, 0) scale(${zoom});">
          <svg class="cp-topo-svg" width="${width}" height="${height}">${pathsSvg}</svg>
          ${nodesHtml}
          ${flyoutHtml}
        </div>
        ${confirmDialogHtml}
        <button type="button" class="cp6-reset-pill cp6-icon-btn ${isFullscreen ? 'is-active' : ''}" id="cpFullscreenBtn" data-cp-keep-open="1" title="${isFullscreen ? '退出全屏' : '全屏'}" aria-label="${isFullscreen ? '退出全屏' : '全屏'}"><span class="fi">${isFullscreen ? '&#xE73F;' : '&#xE740;'}</span></button>
      </div>
    </div>
  `;

  // Pan & Zoom
  let justDragged = false;
  const bg = document.getElementById('cpCanvasBg');
  const panLayer = document.getElementById('cpPanLayer');
  if (bg && panLayer) {
    let dragging = false;
    let startClientX = 0;
    let startClientY = 0;
    let origPanX = panX;
    let origPanY = panY;

    bg.addEventListener('pointerdown', (e) => {
      if (e.button !== 0 && e.button !== 1) return;
      if (e.target.closest('button') || e.target.closest('input') || e.target.closest('[data-cp-keep-open]')) return;
      dragging = true;
      justDragged = false;
      startClientX = e.clientX;
      startClientY = e.clientY;
        origPanX = Number(state.checkpointPanX) || 0;
        origPanY = Number(state.checkpointPanY) || 0;
        bg.setPointerCapture(e.pointerId);
      });

    bg.addEventListener('pointermove', (e) => {
      if (!dragging) return;
      const dx = e.clientX - startClientX;
      const dy = e.clientY - startClientY;
      if (!justDragged && Math.hypot(dx, dy) > 4) {
        justDragged = true;
        bg.classList.add('is-dragging');
      }
      if (justDragged) {
        state.checkpointPanX = origPanX + dx;
        state.checkpointPanY = origPanY + dy;
        const z = Number(state.checkpointZoom) || 1;
        panLayer.style.transform = `translate3d(${state.checkpointPanX}px, ${state.checkpointPanY}px, 0) scale(${z})`;
      }
    });

    const endDrag = (e) => {
      if (!dragging) return;
      dragging = false;
      bg.classList.remove('is-dragging');
      try {
        bg.releasePointerCapture(e.pointerId);
      } catch {}
    };
    bg.addEventListener('pointerup', endDrag);
    bg.addEventListener('pointercancel', endDrag);

    bg.addEventListener(
      'wheel',
      (e) => {
        e.preventDefault();
        const rect = bg.getBoundingClientRect();
        const curZoom = Number(state.checkpointZoom) || 1;
        const factor = e.deltaY < 0 ? 1.1 : 0.9;
        const nextZoom = Math.min(1.8, Math.max(0.45, +(curZoom * factor).toFixed(3)));
        if (nextZoom === curZoom) return;
        const mx = e.clientX - rect.left;
        const my = e.clientY - rect.top;
        const curX = Number(state.checkpointPanX) || 0;
        const curY = Number(state.checkpointPanY) || 0;
        const ratio = nextZoom / curZoom;
        state.checkpointPanX = mx - (mx - curX) * ratio;
        state.checkpointPanY = my - (my - curY) * ratio;
        state.checkpointZoom = nextZoom;
        renderCheckpointWorkbench(host, { state, initialNodes });
      },
      { passive: false }
    );

    bg.addEventListener('click', (e) => {
      if (justDragged) {
        justDragged = false;
        return;
      }
      if (e.target.closest('[data-cp-keep-open]')) return;
      if (state.checkpointMenuOpenId) {
        state.checkpointMenuOpenId = null;
        state.checkpointRenaming = false;
        renderCheckpointWorkbench(host, { state, initialNodes });
      }
    });
  }

  const fullscreenBtn = document.getElementById('cpFullscreenBtn');
  if (fullscreenBtn) {
    fullscreenBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      state.checkpointFullscreen = !state.checkpointFullscreen;
      renderCheckpointWorkbench(host, { state, initialNodes });
    });
  }

  host.querySelectorAll('[data-st-node]').forEach((el) => {
    el.addEventListener('click', (e) => {
      if (justDragged) {
        justDragged = false;
        return;
      }
      if (e.target.closest('button') || e.target.closest('input') || e.target.closest('[data-cp-keep-open]')) return;
      e.stopPropagation();
      const clickedId = el.getAttribute('data-st-node');
      state.checkpointSelectedId = clickedId;
      state.checkpointMenuOpenId = null;
      state.checkpointRenaming = false;
      renderCheckpointWorkbench(host, { state, initialNodes });
    });

    el.addEventListener('contextmenu', (e) => {
      const clickedId = el.getAttribute('data-st-node');
      const clickedNode = byId.get(clickedId);
      if (!clickedNode || clickedNode.type === 'Genesis') return;
      e.preventDefault();
      e.stopPropagation();
      if (bg && typeof bg.getBoundingClientRect === 'function') {
        const rect = bg.getBoundingClientRect();
        const curX = Number(state.checkpointPanX) || 0;
        const curY = Number(state.checkpointPanY) || 0;
        const curZ = Number(state.checkpointZoom) || 1;
        state.checkpointCtxX = Math.round((e.clientX - rect.left - curX) / curZ);
        state.checkpointCtxY = Math.round((e.clientY - rect.top - curY) / curZ);
      } else {
        state.checkpointCtxX = undefined;
        state.checkpointCtxY = undefined;
      }
      state.checkpointSelectedId = clickedId;
      state.checkpointMenuOpenId = clickedId;
      state.checkpointRenaming = false;
      renderCheckpointWorkbench(host, { state, initialNodes });
    });
  });

  const nameInput = document.getElementById('cp2NewNameInput');
  if (nameInput) {
    nameInput.addEventListener('input', (e) => {
      state.checkpointNewName = e.target.value;
    });
    nameInput.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        createCheckpoint();
      } else if (e.key === 'Escape') {
        e.preventDefault();
        state.checkpointMenuOpenId = null;
        renderCheckpointWorkbench(host, { state, initialNodes });
      }
    });
  }

  const createBtn = document.getElementById('cp2CreateBtn');
  if (createBtn) {
    createBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      createCheckpoint();
    });
  }

  host.querySelectorAll('[data-cp2-act]').forEach((btn) => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      performAction(btn.getAttribute('data-cp2-act'), btn.getAttribute('data-cp2-id'));
    });
  });

  host.querySelectorAll('[data-cp2-delete-prompt]').forEach((btn) => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      state.checkpointConfirmDeleteId = btn.getAttribute('data-cp2-delete-prompt');
      state.checkpointMenuOpenId = null;
      renderCheckpointWorkbench(host, { state, initialNodes });
    });
  });

  const confirmDeleteOk = document.getElementById('cpConfirmDeleteOk');
  if (confirmDeleteOk) {
    confirmDeleteOk.addEventListener('click', (e) => {
      e.stopPropagation();
      if (state.checkpointConfirmDeleteId) {
        performAction('delete', state.checkpointConfirmDeleteId);
      }
    });
  }

  const confirmDeleteCancel = document.getElementById('cpConfirmDeleteCancel');
  if (confirmDeleteCancel) {
    confirmDeleteCancel.focus?.();
    confirmDeleteCancel.addEventListener('click', (e) => {
      e.stopPropagation();
      state.checkpointConfirmDeleteId = null;
      renderCheckpointWorkbench(host, { state, initialNodes });
    });
  }

  const confirmBackdrop = document.getElementById('cpConfirmBackdrop');
  if (confirmBackdrop) {
    confirmBackdrop.addEventListener('click', (e) => {
      if (e.target === confirmBackdrop) {
        state.checkpointConfirmDeleteId = null;
        renderCheckpointWorkbench(host, { state, initialNodes });
      }
    });
  }

  host.querySelectorAll('[data-cp2-rename]').forEach((btn) => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      state.checkpointSelectedId = btn.getAttribute('data-cp2-rename');
      state.checkpointRenaming = true;
      renderCheckpointWorkbench(host, { state, initialNodes });
    });
  });

  const commitRename = () => {
    const target = openNode || sel;
    const val = (document.getElementById('cp2RenameInput')?.value || '').trim();
    if (val && target) target.name = val;
    state.checkpointRenaming = false;
    state.checkpointMenuOpenId = null;
    renderCheckpointWorkbench(host, { state, initialNodes });
  };

  const renameInput = document.getElementById('cp2RenameInput');
  if (renameInput) {
    renameInput.focus?.();
    renameInput.select?.();
    renameInput.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        commitRename();
      } else if (e.key === 'Escape') {
        e.preventDefault();
        state.checkpointRenaming = false;
        renderCheckpointWorkbench(host, { state, initialNodes });
      }
    });
  }

  const renameSave = document.getElementById('cp2RenameSave');
  if (renameSave) {
    renameSave.addEventListener('click', (e) => {
      e.stopPropagation();
      commitRename();
    });
  }

  const cancelRename = document.getElementById('cp3CancelRename');
  if (cancelRename) {
    cancelRename.addEventListener('click', (e) => {
      e.stopPropagation();
      state.checkpointRenaming = false;
      renderCheckpointWorkbench(host, { state, initialNodes });
    });
  }

  if (typeof document !== 'undefined' && !host.__cpKeyBound) {
    host.__cpKeyBound = true;
    document.addEventListener('keydown', (e) => {
      if (e.key !== 'Escape') return;
      if (state.activeTabKey !== 'checkpoints' || state.navTarget !== 'vm') return;
      if (state.checkpointConfirmDeleteId) {
        state.checkpointConfirmDeleteId = null;
        renderCheckpointWorkbench(host, { state, initialNodes });
      } else if (state.checkpointMenuOpenId) {
        state.checkpointRenaming = false;
        state.checkpointMenuOpenId = null;
        renderCheckpointWorkbench(host, { state, initialNodes });
      } else if (state.checkpointFullscreen) {
        state.checkpointFullscreen = false;
        renderCheckpointWorkbench(host, { state, initialNodes });
      }
    });
  }
}
