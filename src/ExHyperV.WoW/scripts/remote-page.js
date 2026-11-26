/**
 * Dedicated WoW "远程" page: switch cleanly between Demo mode (default) and Live ExHyperV.Server mode.
 */

let selectedUiMode = 'demo';

function esc(text) {
  return String(text ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function infoBtnHtml(desc) {
  if (!desc) return '';
  const safe = esc(desc);
  return `<button class="setting-info-btn" type="button" data-info-desc="${safe}" title="${safe}"><span class="fi" style="font-size:15px;">&#xE946;</span></button>`;
}

export function renderRemotePage(root, adapter) {
  if (!root || !adapter) return;

  const st = adapter.state;
  const isLive = st.mode === 'live' && st.status === 'connected';
  const isConnecting = st.status === 'connecting';
  if (isLive || isConnecting || st.status === 'error') {
    selectedUiMode = 'remote';
  }
  const isRemoteView = selectedUiMode === 'remote';

  const info = st.serverInfo;
  const toolsets = Array.isArray(info?.toolsets) ? info.toolsets : [];
  const originHint = typeof window !== 'undefined' && window.location ? window.location.origin : '';
  const hasSavedSession = Boolean(adapter.client?.hasStoredSessionForUrl?.(st.serverUrl));

  const modeDesc = '演示模式使用离线模拟数据，不会向宿主机发送真实指令。切换到远程模式并连接 ExHyperV.Server 后，可控制真实宿主机。';
  const configDesc = originHint
    ? `如跨源连接，请在宿主机启动参数中添加 --cors-origins ${originHint}`
    : '默认监听本机回环 http://127.0.0.1:5191';

  const profileChipsHtml = st.profiles.length > 0
    ? `<div class="remote-history-row">
        <span class="remote-history-label">最近</span>
        <div class="remote-history-chips">
          ${st.profiles
            .map((p) => `<button type="button" class="winui-btn remote-profile-chip ${p === st.serverUrl ? 'is-active' : ''}" data-remote-profile="${esc(p)}">${esc(p)}</button>`)
            .join('')}
        </div>
      </div>`
    : '';

  const errorBannerHtml = st.lastError
    ? `<div class="remote-inline-alert is-error">
        <span class="fi">&#xE711;</span>
        <div class="remote-inline-alert-body">
          <div class="remote-inline-alert-title">无法建立连接</div>
          <div class="remote-inline-alert-desc">${esc(st.lastError)}</div>
        </div>
      </div>`
    : st.lastProbeResult?.ok && !isLive
      ? `<div class="remote-inline-alert is-info">
          <span class="fi">&#xE73E;</span>
          <div class="remote-inline-alert-body">
            <div class="remote-inline-alert-title">服务在线（ExHyperV.Server ${esc(st.lastProbeResult.version)}）</div>
            <div class="remote-inline-alert-desc">${st.lastProbeResult.authRequired ? '已启用访问鉴权，输入口令或令牌即可连接' : '本机回环免密模式，可直接连接'}</div>
          </div>
        </div>`
      : '';

  const configCardHtml = isRemoteView
    ? `<div class="isa-card">
        <div class="isa-card-row">
          <div class="setting-label-grid">
            <span class="app-settings-row-title">连接配置</span>
            ${infoBtnHtml(configDesc)}
          </div>
        </div>

        <div class="remote-form-grid">
          <label class="remote-field">
            <span class="remote-field-label">服务地址</span>
            <input type="url" class="winui-textbox remote-input" id="remoteUrlInput" value="${esc(st.serverUrl)}" placeholder="http://127.0.0.1:5191" spellcheck="false" autocomplete="url" />
          </label>

          <label class="remote-field">
            <span class="remote-field-label">访问凭证</span>
            <div class="remote-secret-wrap">
              <input type="password" class="winui-textbox remote-input" id="remoteSecretInput" placeholder="${hasSavedSession ? '已缓存会话，可直接连接' : '留空表示本机免密'}" autocomplete="current-password" />
              <button type="button" class="winui-btn remote-eye-btn" id="remoteToggleSecretBtn" title="显示或隐藏凭证">显示</button>
            </div>
          </label>
        </div>

        ${profileChipsHtml}

        <div class="remote-form-footer">
          <label class="remote-remember-label">
            <input type="checkbox" id="remoteRememberCheck" ${st.rememberSession ? 'checked' : ''} />
            <span>记住登录会话（24 小时）</span>
          </label>
          <div class="remote-actions-row">
            <button type="button" class="winui-btn" id="remoteProbeBtn" ${isConnecting ? 'disabled' : ''}>检测</button>
            ${isLive
              ? `<button type="button" class="winui-btn" id="remoteRefreshBtn">同步</button>
                 <button type="button" class="winui-btn" id="remoteDisconnectBtn">断开</button>`
              : `<button type="button" class="winui-btn accent" id="remoteConnectBtn" ${isConnecting ? 'disabled' : ''}>${isConnecting ? '连接中...' : '连接'}</button>`}
          </div>
        </div>

        ${errorBannerHtml}
      </div>`
    : '';

  const serverInfoCardHtml = isRemoteView && isLive && info
    ? `<div class="isa-card">
        <div class="isa-card-row">
          <div class="setting-label-grid">
            <span class="app-settings-row-title">宿主机状态</span>
            ${infoBtnHtml('当前由真实 ExHyperV.Server 驱动虚拟机管理、遥测推送与控制台流')}
          </div>
          <span class="remote-status-pill is-live">已连接</span>
        </div>
        <div class="remote-info-grid">
          <div class="remote-kv-cell">
            <span class="remote-kv-label">Server 版本</span>
            <span class="remote-kv-val">${esc(info.version || '未知')}</span>
          </div>
          <div class="remote-kv-cell">
            <span class="remote-kv-label">宿主机架构</span>
            <span class="remote-kv-val">${esc(info.osArchitecture || '未知')}</span>
          </div>
          <div class="remote-kv-cell">
            <span class="remote-kv-label">服务权限</span>
            <span class="remote-kv-val">${info.isAdministrator ? '管理员权限' : '标准权限'}</span>
          </div>
          <div class="remote-kv-cell">
            <span class="remote-kv-label">访问控制</span>
            <span class="remote-kv-val">${info.readOnly ? '只读模式' : '完全读写'}</span>
          </div>
          <div class="remote-kv-cell">
            <span class="remote-kv-label">鉴权方式</span>
            <span class="remote-kv-val">${esc(info.authenticationMode || 'loopback')}</span>
          </div>
          <div class="remote-kv-cell">
            <span class="remote-kv-label">最近同步</span>
            <span class="remote-kv-val">${esc(st.lastConnectedAt || '刚刚')}</span>
          </div>
        </div>
        <div class="remote-toolsets-row">
          <span class="remote-history-label">已开放工具组</span>
          <div class="remote-toolsets-wrap">
            ${toolsets.map((t) => `<span class="remote-toolset-tag">${esc(t)}</span>`).join('')}
          </div>
        </div>
        <div class="remote-fingerprint-row">
          <span class="remote-history-label">Core 契约指纹</span>
          <code class="remote-fingerprint-val">${esc(info.contractFingerprint || '')}</code>
        </div>
      </div>`
    : '';

  root.innerHTML = `
    <div class="app-settings-body remote-page-body">
      <div class="app-settings-stack remote-page-stack">
        <div class="isa-card uniform-1row">
          <div class="isa-card-row">
            <div class="setting-label-grid">
              <span class="app-settings-row-title">运行模式</span>
              ${infoBtnHtml(modeDesc)}
            </div>
            <div class="isa-pill-track" style="width:220px;flex-shrink:0;" role="radiogroup" aria-label="运行模式">
              <button type="button" class="isa-pill-btn ${!isRemoteView ? 'active' : ''}" id="remoteModeDemoBtn">演示模式</button>
              <button type="button" class="isa-pill-btn ${isRemoteView ? 'active' : ''}" id="remoteModeLiveBtn">远程模式</button>
            </div>
          </div>
        </div>

        ${configCardHtml}

        ${serverInfoCardHtml}
      </div>
    </div>
  `;

  const urlInput = root.querySelector('#remoteUrlInput');
  const secretInput = root.querySelector('#remoteSecretInput');
  const rememberCheck = root.querySelector('#remoteRememberCheck');

  urlInput?.addEventListener('input', () => {
    st.serverUrl = urlInput.value;
  });

  rememberCheck?.addEventListener('change', () => {
    adapter.setRememberSession(rememberCheck.checked);
  });

  const toggleSecretBtn = root.querySelector('#remoteToggleSecretBtn');
  toggleSecretBtn?.addEventListener('click', () => {
    if (!secretInput) return;
    const nextShow = secretInput.type === 'password';
    secretInput.type = nextShow ? 'text' : 'password';
    toggleSecretBtn.textContent = nextShow ? '隐藏' : '显示';
  });

  root.querySelectorAll('[data-remote-profile]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const val = btn.getAttribute('data-remote-profile') || '';
      if (urlInput) urlInput.value = val;
      st.serverUrl = val;
      renderRemotePage(root, adapter);
    });
  });

  async function doConnect() {
    const rawUrl = urlInput ? urlInput.value : st.serverUrl;
    const rawSecret = secretInput ? secretInput.value.trim() : '';
    const isRootToken = rawSecret.length >= 32 && /^[A-Za-z0-9\-._~+/=]+$/.test(rawSecret);
    try {
      await adapter.connectToServer({
        url: rawUrl,
        password: isRootToken ? '' : rawSecret,
        rootToken: isRootToken ? rawSecret : '',
        rememberSession: Boolean(rememberCheck?.checked),
        useStoredSession: !rawSecret
      });
    } catch {
      renderRemotePage(root, adapter);
    }
  }

  root.querySelector('#remoteProbeBtn')?.addEventListener('click', async () => {
    const rawUrl = urlInput ? urlInput.value : st.serverUrl;
    try {
      await adapter.probeServer(rawUrl);
    } catch {}
    renderRemotePage(root, adapter);
  });

  root.querySelector('#remoteConnectBtn')?.addEventListener('click', doConnect);
  secretInput?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      void doConnect();
    }
  });
  urlInput?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      void doConnect();
    }
  });

  root.querySelector('#remoteRefreshBtn')?.addEventListener('click', async () => {
    try {
      await Promise.all([adapter.refreshVmList(), adapter.refreshHostSnapshot()]);
    } catch (err) {
      st.lastError = err.message;
    }
    renderRemotePage(root, adapter);
  });

  root.querySelector('#remoteDisconnectBtn')?.addEventListener('click', async () => {
    selectedUiMode = 'demo';
    await adapter.switchToDemoMode();
    renderRemotePage(root, adapter);
  });

  root.querySelector('#remoteModeDemoBtn')?.addEventListener('click', async () => {
    selectedUiMode = 'demo';
    if (isLive) {
      await adapter.switchToDemoMode();
    }
    renderRemotePage(root, adapter);
  });

  root.querySelector('#remoteModeLiveBtn')?.addEventListener('click', () => {
    if (selectedUiMode !== 'remote') {
      selectedUiMode = 'remote';
      renderRemotePage(root, adapter);
      root.querySelector('#remoteUrlInput')?.focus();
    }
  });
}
