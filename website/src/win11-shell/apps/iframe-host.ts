import {
  isWin11AppToShellMessage,
  WIN11_APP_PROTOCOL,
  type Win11ShellToAppMessage,
} from '../app-protocol';
import { hasAppCapability } from '../registry';
import type { ThemeMode, WinAppHostContext, WinAppHostHooks, WinAppLifecycle } from '../types';

function buildIframeSrc(ctx: WinAppHostContext): string {
  const iframeSpec = ctx.manifest.iframe;
  if (!iframeSpec) return 'about:blank';

  const rawUrl = iframeSpec.entryUrl;
  const shouldPassQuery = iframeSpec.passShellQuery !== false && rawUrl.startsWith('/');
  if (!shouldPassQuery) return rawUrl;

  const url = new URL(rawUrl, window.location.origin);
  url.searchParams.set('theme', ctx.shell.getTheme());
  url.searchParams.set('volume', String(ctx.shell.getVolume()));
  url.searchParams.set('appId', ctx.appId);
  url.searchParams.set('pid', String(ctx.pid));
  return `${url.pathname}${url.search}${url.hash}`;
}

function resolveExpectedOrigin(entryUrl: string): string {
  try {
    return new URL(entryUrl, window.location.origin).origin;
  } catch {
    return window.location.origin;
  }
}

export function mountIframeApp(
  ctx: WinAppHostContext,
  hooks: WinAppHostHooks = {},
): WinAppLifecycle {
  const { container, manifest, pid, appId, shell } = ctx;
  const iframeSpec = manifest.iframe;
  if (!iframeSpec) {
    return {};
  }

  const expectedOrigin = resolveExpectedOrigin(iframeSpec.entryUrl);
  const frame = document.createElement('iframe');
  frame.className = 'w11-win-iframe';
  frame.title = manifest.name;
  frame.src = buildIframeSrc(ctx);
  if (iframeSpec.allow) {
    frame.setAttribute('allow', iframeSpec.allow);
  }
  if (iframeSpec.sandbox) {
    frame.setAttribute('sandbox', iframeSpec.sandbox);
  }

  let loaded = false;
  let ready = false;
  let lastFocused = true;
  let lastMinimized = false;
  const pendingMessages: Array<{ channel: string; payload: unknown }> = [];

  function postToFrame(message: Win11ShellToAppMessage) {
    if (!frame.contentWindow) return;
    try {
      frame.contentWindow.postMessage(message, expectedOrigin);
    } catch {
      // Ignore cross-origin or detached iframe errors.
    }
  }

  function postLegacyTheme(theme: ThemeMode) {
    if (!frame.contentWindow || expectedOrigin !== window.location.origin) return;
    try {
      frame.contentWindow.postMessage({ type: 'w11-theme', theme }, expectedOrigin);
    } catch {
      // Ignore detached iframe errors.
    }
  }

  function sendInit() {
    const win = shell.getWindows().find((w) => w.pid === pid);
    const volume = shell.getVolume();
    postToFrame({
      protocol: WIN11_APP_PROTOCOL,
      type: 'w11:init',
      pid,
      appId,
      theme: shell.getTheme(),
      volume,
      muted: volume === 0,
      focused: lastFocused,
      minimized: win?.minimized ?? lastMinimized,
      launchArgs: ctx.launchArgs,
      ...(hasAppCapability(manifest, 'hardware-probe')
        ? { hardware: shell.getVirtualHardware() }
        : {}),
    });
  }

  function flushPendingMessages() {
    if (!frame.contentWindow || pendingMessages.length === 0) return;
    const queued = pendingMessages.splice(0, pendingMessages.length);
    queued.forEach(({ channel, payload }) => {
      postToFrame({
        protocol: WIN11_APP_PROTOCOL,
        type: 'w11:app-message',
        channel,
        payload,
      });
    });
  }

  function onLoad() {
    loaded = true;
    sendInit();
    // 静态第三方页面可能不主动发送 w11:ready，在 load 完成后补发队列消息
    if (!ready && expectedOrigin !== window.location.origin) {
      flushPendingMessages();
    }
  }

  function onWindowMessage(event: MessageEvent) {
    if (!frame.contentWindow || event.source !== frame.contentWindow) return;
    if (event.origin !== expectedOrigin) return;

    const data = event.data;
    if (!isWin11AppToShellMessage(data)) return;

    switch (data.type) {
      case 'w11:ready':
        ready = true;
        sendInit();
        flushPendingMessages();
        break;
      case 'w11:focus-self':
        shell.focusWindow(pid);
        break;
      case 'w11:set-title':
        shell.setWindowTitle(pid, data.title);
        break;
      case 'w11:window-control':
        if (!hasAppCapability(manifest, 'window-control')) return;
        if (data.action === 'minimize') shell.minimizeWindow(pid);
        else if (data.action === 'maximize' || data.action === 'restore') shell.toggleMaximizeWindow(pid);
        else if (data.action === 'close') shell.closeWindow(pid);
        break;
      case 'w11:open-app':
        if (!hasAppCapability(manifest, 'shell-control')) return;
        shell.openApp(data.appId, data.args);
        break;
      case 'w11:request-hardware':
        if (!hasAppCapability(manifest, 'hardware-probe')) return;
        if (data.force) {
          void shell.refreshVirtualHardware(true).then((hardware) => {
            postToFrame({ protocol: WIN11_APP_PROTOCOL, type: 'w11:hardware', hardware });
          });
        } else {
          postToFrame({
            protocol: WIN11_APP_PROTOCOL,
            type: 'w11:hardware',
            hardware: shell.getVirtualHardware(),
          });
        }
        break;
      case 'w11:app-event':
        if (!hasAppCapability(manifest, 'app-messaging')) return;
        hooks.onAppEvent?.(appId, pid, data.channel, data.payload);
        break;
    }
  }

  frame.addEventListener('load', onLoad);
  window.addEventListener('message', onWindowMessage);
  container.replaceChildren(frame);

  return {
    onThemeChange: (theme) => {
      if (!loaded && !ready) return;
      postToFrame({ protocol: WIN11_APP_PROTOCOL, type: 'w11:theme', theme });
      postLegacyTheme(theme);
    },
    onVolumeChange: (volume) => {
      if (!loaded && !ready) return;
      postToFrame({
        protocol: WIN11_APP_PROTOCOL,
        type: 'w11:volume',
        volume,
        muted: volume === 0,
      });
    },
    onFocusChange: (focused) => {
      lastFocused = focused;
      if (!loaded && !ready) return;
      postToFrame({ protocol: WIN11_APP_PROTOCOL, type: 'w11:focus', focused });
    },
    onVisibilityChange: (minimized) => {
      lastMinimized = minimized;
      if (!loaded && !ready) return;
      postToFrame({ protocol: WIN11_APP_PROTOCOL, type: 'w11:visibility', minimized });
    },
    onHardwareUpdate: (hardware) => {
      if (!hasAppCapability(manifest, 'hardware-probe')) return;
      if (!loaded && !ready) return;
      postToFrame({ protocol: WIN11_APP_PROTOCOL, type: 'w11:hardware', hardware });
    },
    onMessage: (channel, payload) => {
      if (!hasAppCapability(manifest, 'app-messaging')) return;
      if (!ready && !(loaded && expectedOrigin !== window.location.origin)) {
        pendingMessages.push({ channel, payload });
        return;
      }
      postToFrame({
        protocol: WIN11_APP_PROTOCOL,
        type: 'w11:app-message',
        channel,
        payload,
      });
    },
    destroy: () => {
      frame.removeEventListener('load', onLoad);
      window.removeEventListener('message', onWindowMessage);
      try {
        frame.src = 'about:blank';
      } catch {
        // Ignore teardown errors.
      }
      container.replaceChildren();
    },
  };
}
