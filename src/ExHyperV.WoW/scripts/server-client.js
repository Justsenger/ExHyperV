/**
 * HTTP + WebSocket transport client for ExHyperV.Server (/api/v1 & /ws/v1).
 * Safe to import in non-browser SSR/Node test environments.
 */

const SESSION_KEY_PREFIX = 'exhyperv.wow.serverSession.';

export function pickField(obj, ...names) {
  if (!obj || typeof obj !== 'object') return undefined;
  for (const name of names) {
    if (Object.prototype.hasOwnProperty.call(obj, name)) return obj[name];
  }
  const lowerMap = new Map();
  for (const key of Object.keys(obj)) {
    lowerMap.set(key.toLowerCase(), key);
  }
  for (const name of names) {
    const matched = lowerMap.get(String(name).toLowerCase());
    if (matched !== undefined) return obj[matched];
  }
  return undefined;
}

export function normalizeServerUrl(raw) {
  const trimmed = String(raw || '').trim().replace(/\/+$/, '');
  if (!trimmed) {
    throw new Error('请输入 ExHyperV.Server 服务地址');
  }
  let parsed;
  try {
    parsed = new URL(trimmed);
  } catch {
    throw new Error('服务地址格式无效，请输入完整的 http:// 或 https:// 地址');
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    throw new Error('仅支持 http:// 或 https:// 协议的服务地址');
  }
  if (!parsed.port && parsed.protocol === 'http:') {
    return `${parsed.protocol}//${parsed.hostname}`;
  }
  return `${parsed.protocol}//${parsed.host}`;
}

export function isLoopbackHostname(hostname) {
  const h = String(hostname || '').toLowerCase().replace(/^\[|\]$/g, '');
  return h === 'localhost' || h === '127.0.0.1' || h === '::1';
}

export function getBrowserConnectionHint(serverUrl) {
  if (typeof window === 'undefined' || !window.location) return '';
  try {
    const target = new URL(serverUrl);
    if (window.location.protocol === 'https:' && target.protocol === 'http:' && !isLoopbackHostname(target.hostname)) {
      return '当前页面为 HTTPS 安全上下文，浏览器会阻止直连非本机 HTTP 地址，请为 ExHyperV.Server 启用 HTTPS 证书。';
    }
  } catch {}
  return '';
}

export function unwrapApiResponse(rawResult) {
  if (!rawResult || typeof rawResult !== 'object') return rawResult;
  const hasSuccess = pickField(rawResult, 'Success', 'success') !== undefined;
  const hasData = pickField(rawResult, 'HasData', 'hasData');
  const data = pickField(rawResult, 'Data', 'data');
  const err = pickField(rawResult, 'Error', 'error');
  if (hasSuccess) {
    const ok = Boolean(pickField(rawResult, 'Success', 'success'));
    if (!ok) {
      throw new Error(err || '宿主机操作失败');
    }
    if (data !== undefined || hasData !== undefined) return data;
    return rawResult;
  }
  if (hasData !== undefined) {
    if (!hasData && err) throw new Error(err);
    return data;
  }
  return rawResult;
}

function formatHttpFailure(status, body, baseUrl, actionLabel) {
  const msg = pickField(body, 'error', 'Error', 'message') || '';
  if (status === 401) {
    return msg || '访问口令或令牌无效，或会话已过期';
  }
  if (status === 403) {
    const origin = typeof window !== 'undefined' && window.location ? window.location.origin : '';
    if (msg.includes('来源') && origin) {
      return `${msg}（请在服务端参数中添加 --cors-origins ${origin}）`;
    }
    return msg || '宿主机拒绝了当前来源或无凭证连接请求';
  }
  if (status === 429) {
    return msg || '登录尝试过于频繁，请等待 60 秒后重试';
  }
  return msg || `${actionLabel}失败（HTTP ${status}）`;
}

export function createServerClient() {
  let baseUrl = '';
  let token = '';
  let sessionId = '';
  let expiresAt = '';
  let authMode = 'loopback';
  let serverInfo = null;
  const retainedHandles = new Set();

  function clearSessionState() {
    baseUrl = '';
    token = '';
    sessionId = '';
    expiresAt = '';
    authMode = 'loopback';
    serverInfo = null;
    retainedHandles.clear();
  }

  function storageKey(url) {
    return `${SESSION_KEY_PREFIX}${url}`;
  }

  function readStoredSession(url) {
    try {
      const normalized = normalizeServerUrl(url);
      const raw =
        (typeof sessionStorage !== 'undefined' && sessionStorage.getItem(storageKey(normalized))) ||
        (typeof localStorage !== 'undefined' && localStorage.getItem(storageKey(normalized))) ||
        '';
      if (!raw) return null;
      const parsed = JSON.parse(raw);
      if (!parsed || typeof parsed !== 'object' || !parsed.token) return null;
      if (parsed.expiresAt && Date.parse(parsed.expiresAt) <= Date.now() + 10000) {
        clearStoredSession(normalized);
        return null;
      }
      return parsed;
    } catch {
      return null;
    }
  }

  function saveStoredSession(url, data, persistLocal = false) {
    try {
      const normalized = normalizeServerUrl(url);
      const serialized = JSON.stringify({
        token: data.token,
        sessionId: data.sessionId || '',
        expiresAt: data.expiresAt || '',
        authMode: data.authMode || 'password',
        savedAt: new Date().toISOString()
      });
      if (typeof sessionStorage !== 'undefined') {
        sessionStorage.setItem(storageKey(normalized), serialized);
      }
      if (typeof localStorage !== 'undefined') {
        if (persistLocal) localStorage.setItem(storageKey(normalized), serialized);
        else localStorage.removeItem(storageKey(normalized));
      }
    } catch {}
  }

  function clearStoredSession(url) {
    try {
      const key = storageKey(url);
      sessionStorage?.removeItem(key);
      localStorage?.removeItem(key);
    } catch {}
  }

  async function requestJson(url, method = 'GET', body = undefined, extraHeaders = {}, overrideBaseUrl = null) {
    const root = overrideBaseUrl || baseUrl;
    if (!root) throw new Error('尚未连接到 ExHyperV.Server');
    const hint = getBrowserConnectionHint(root);
    if (hint) throw new Error(hint);

    const headers = { ...extraHeaders };
    if (token && !overrideBaseUrl) {
      headers.Authorization = `Bearer ${token}`;
    }
    const init = { method, headers };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }

    let response;
    try {
      response = await fetch(`${root}${url}`, init);
    } catch (err) {
      const origin = typeof window !== 'undefined' && window.location ? window.location.origin : '';
      const rawMsg = err && err.message ? String(err.message) : '网络不可达';
      throw new Error(
        `无法访问 ${root}（${rawMsg}）。请确认 ExHyperV.Server 正在运行${origin ? `，且已配置 --cors-origins ${origin}` : ''}`
      );
    }

    if (response.status === 204) return null;
    let payload = null;
    const text = await response.text();
    if (text) {
      try {
        payload = JSON.parse(text);
      } catch {
        payload = { error: text };
      }
    }
    if (!response.ok) {
      const formatted = formatHttpFailure(response.status, payload, root, `${method} ${url}`);
      const error = new Error(formatted);
      error.status = response.status;
      error.payload = payload;
      throw error;
    }
    return payload;
  }

  async function probeHealth(rawUrl) {
    const normalized = normalizeServerUrl(rawUrl);
    return requestJson('/api/v1/health', 'GET', undefined, {}, normalized);
  }

  async function connect({ url, password = '', rootToken = '', rememberSession = false, useStoredSession = true } = {}) {
    const normalized = normalizeServerUrl(url);
    const health = await probeHealth(normalized);
    const authRequired = Boolean(pickField(health, 'authRequired'));

    let nextToken = '';
    let nextSessionId = '';
    let nextExpiresAt = '';
    let nextAuthMode = authRequired ? 'password' : 'loopback';

    if (authRequired) {
      const cleanToken = String(rootToken || '').trim();
      const cleanPass = String(password || '');
      if (!cleanPass && !cleanToken && useStoredSession) {
        const stored = readStoredSession(normalized);
        if (stored?.token) {
          nextToken = stored.token;
          nextSessionId = stored.sessionId || '';
          nextExpiresAt = stored.expiresAt || '';
          nextAuthMode = stored.authMode || 'session';
        }
      }

      if (!nextToken) {
        if (!cleanPass && !cleanToken) {
          throw new Error('该宿主机已启用鉴权，请输入访问口令或 32 位以上根令牌');
        }
        const loginBody = cleanToken
          ? { token: cleanToken, clientName: 'ExHyperV.WoW' }
          : { password: cleanPass, clientName: 'ExHyperV.WoW' };
        const loginReply = await requestJson('/api/v1/auth/login', 'POST', loginBody, {}, normalized);
        nextToken = pickField(loginReply, 'token') || '';
        nextSessionId = pickField(loginReply, 'sessionId') || '';
        nextExpiresAt = pickField(loginReply, 'expiresAt') || '';
        nextAuthMode = cleanToken ? 'token' : 'password';
        if (!nextToken) {
          throw new Error('登录响应中缺少会话令牌');
        }
      }
    }

    baseUrl = normalized;
    token = nextToken;
    sessionId = nextSessionId;
    expiresAt = nextExpiresAt;
    authMode = nextAuthMode;

    try {
      serverInfo = await requestJson('/api/v1/server/info', 'GET');
    } catch (err) {
      clearStoredSession(normalized);
      clearSessionState();
      throw err;
    }

    if (authRequired && token) {
      saveStoredSession(normalized, { token, sessionId, expiresAt, authMode }, rememberSession);
    }

    return {
      url: baseUrl,
      health,
      info: serverInfo,
      authMode,
      expiresAt
    };
  }

  async function disconnect() {
    const oldUrl = baseUrl;
    const oldToken = token;
    const handles = Array.from(retainedHandles);
    retainedHandles.clear();
    if (oldUrl) {
      for (const id of handles) {
        try {
          await requestJson(`/api/v1/objects/${id}`, 'DELETE');
        } catch {}
      }
      if (oldToken && sessionId) {
        try {
          await requestJson('/api/v1/auth/logout', 'POST', {});
        } catch {}
      }
      clearStoredSession(oldUrl);
    }
    clearSessionState();
  }

  async function waitOperation(operationId, onProgress) {
    const deadline = Date.now() + 180000;
    while (Date.now() < deadline) {
      const status = await requestJson(`/api/v1/operations/${operationId}?eventLimit=50`, 'GET');
      if (onProgress && Array.isArray(status?.events)) {
        onProgress(status.events, status);
      }
      if (status?.state === 'Completed' || status?.state === 'Failed' || status?.state === 'Cancelled' || status?.state === 'RequiresVerification') {
        try {
          await requestJson(`/api/v1/operations/${operationId}`, 'DELETE');
        } catch {}
        return status;
      }
      await new Promise((resolve) => setTimeout(resolve, 350));
    }
    throw new Error('宿主机后台操作等待超时');
  }

  async function call(apiId, args = {}, opts = {}) {
    const body = {
      apiId,
      arguments: args && typeof args === 'object' ? args : {},
      background: Boolean(opts.background),
      retainResult: Boolean(opts.retainResult)
    };
    if (opts.targetObjectId) body.targetObjectId = opts.targetObjectId;
    if (opts.typeArguments) body.typeArguments = opts.typeArguments;
    if (opts.methodArguments) body.methodArguments = opts.methodArguments;

    let reply = await requestJson('/api/v1/core/call', 'POST', body);
    if (reply && reply.operationId && reply.success === undefined && opts.waitBackground !== false) {
      reply = await waitOperation(reply.operationId, opts.onProgress);
    } else if (reply?.operationId && reply.success !== undefined && !opts.retainResult) {
      // Clean up completed synchronous operation entries asynchronously when possible
      requestJson(`/api/v1/operations/${reply.operationId}`, 'DELETE').catch(() => {});
    }

    if (reply && reply.success === false) {
      throw new Error(reply.error || `调用 ${apiId} 失败`);
    }
    const rawResult = reply ? reply.result : null;
    if (opts.retainResult) {
      const handle = pickField(rawResult, '$handle');
      if (handle) retainedHandles.add(handle);
    }
    if (opts.raw) return rawResult;
    return unwrapApiResponse(rawResult);
  }

  async function retainService(ctorApiId, args = {}) {
    const raw = await call(ctorApiId, args, { retainResult: true, raw: true });
    const handle = pickField(raw, '$handle');
    if (!handle) {
      throw new Error(`服务实例创建未返回对象句柄（${ctorApiId}）`);
    }
    return handle;
  }

  async function releaseObject(objectId) {
    if (!objectId || !baseUrl) return;
    retainedHandles.delete(objectId);
    try {
      await requestJson(`/api/v1/objects/${objectId}`, 'DELETE');
    } catch {}
  }

  async function getProp(apiId, targetObjectId = null, args = {}) {
    const body = { apiId, arguments: args };
    if (targetObjectId) body.targetObjectId = targetObjectId;
    const reply = await requestJson('/api/v1/core/get', 'POST', body);
    return pickField(reply, 'value');
  }

  async function setProp(apiId, value, targetObjectId = null, args = {}) {
    const body = { apiId, arguments: args, value };
    if (targetObjectId) body.targetObjectId = targetObjectId;
    return requestJson('/api/v1/core/set', 'POST', body);
  }

  async function issueWsTicket(scope = {}) {
    if (!baseUrl) throw new Error('尚未连接到 ExHyperV.Server');
    if (!serverInfo?.authenticationMode || serverInfo.authenticationMode === 'loopback') {
      return { ticket: null, serverUrl: baseUrl };
    }
    const res = await requestJson('/api/v1/auth/ws-ticket', 'POST', scope);
    return { ticket: pickField(res, 'ticket'), expiresAt: pickField(res, 'expiresAt'), serverUrl: baseUrl };
  }

  async function openWebSocket(wsPath, queryParams = {}) {
    if (!baseUrl) throw new Error('尚未连接到 ExHyperV.Server');
    const url = new URL(`/ws/v1/${String(wsPath).replace(/^\/+/, '')}`, baseUrl);
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
    const { ticket } = await issueWsTicket();
    if (ticket) {
      url.searchParams.set('ticket', ticket);
    }
    for (const [k, v] of Object.entries(queryParams || {})) {
      if (v !== undefined && v !== null && v !== '') {
        url.searchParams.set(k, String(v));
      }
    }
    const socket = new WebSocket(url.toString());
    socket.binaryType = 'arraybuffer';
    return socket;
  }

  return {
    get isConnected() {
      return Boolean(baseUrl && serverInfo);
    },
    get baseUrl() {
      return baseUrl;
    },
    get serverInfo() {
      return serverInfo;
    },
    get authMode() {
      return authMode;
    },
    get hasStoredSession() {
      return Boolean(baseUrl ? readStoredSession(baseUrl) : null);
    },
    hasStoredSessionForUrl: (url) => Boolean(readStoredSession(url)),
    probeHealth,
    connect,
    disconnect,
    refreshInfo: async () => {
      serverInfo = await requestJson('/api/v1/server/info', 'GET');
      return serverInfo;
    },
    call,
    retainService,
    releaseObject,
    getProp,
    setProp,
    consoleBackends: (vmId) => requestJson(`/api/v1/vms/${encodeURIComponent(vmId)}/console/backends`, 'GET'),
    issueWsTicket,
    openWebSocket,
    clearStoredSession
  };
}
