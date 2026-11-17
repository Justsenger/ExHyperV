import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { after, before, test } from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';

const websiteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

async function loadVite() {
  try {
    return await import('vite');
  } catch {
    const pnpmDir = path.join(websiteRoot, 'node_modules', '.pnpm');
    const viteEntry = readdirSync(pnpmDir).find((name) => name.startsWith('vite@'));
    if (!viteEntry) {
      throw new Error('Could not locate vite in website/node_modules');
    }
    const vitePath = path.join(pnpmDir, viteEntry, 'node_modules', 'vite', 'dist', 'node', 'index.js');
    return await import(pathToFileURL(vitePath).href);
  }
}

let viteServer;
let registryMod;
let storeMod;
let protocolMod;
let commandsMod;
let iframeHostMod;
let settingsAppMod;
let placeholderAppMod;
let appsIndexMod;
let taskManagerMod;
let virtualHwMod;

before(async () => {
  const vite = await loadVite();
  viteServer = await vite.createServer({
    root: websiteRoot,
    server: { middlewareMode: true },
    appType: 'custom',
    logLevel: 'silent',
  });
  registryMod = await viteServer.ssrLoadModule('/src/win11-shell/registry.ts');
  storeMod = await viteServer.ssrLoadModule('/src/win11-shell/store.ts');
  protocolMod = await viteServer.ssrLoadModule('/src/win11-shell/app-protocol.ts');
  commandsMod = await viteServer.ssrLoadModule('/src/win11-shell/apps/commands.ts');
  iframeHostMod = await viteServer.ssrLoadModule('/src/win11-shell/apps/iframe-host.ts');
  settingsAppMod = await viteServer.ssrLoadModule('/src/win11-shell/apps/settings-app.ts');
  placeholderAppMod = await viteServer.ssrLoadModule('/src/win11-shell/apps/placeholder-app.ts');
  appsIndexMod = await viteServer.ssrLoadModule('/src/win11-shell/apps/index.ts');
  taskManagerMod = await viteServer.ssrLoadModule('/src/win11-shell/task-manager.ts');
  virtualHwMod = await viteServer.ssrLoadModule('/src/win11-shell/virtual-hardware.ts');
});

after(async () => {
  await viteServer?.close();
});

test('APP_REGISTRY manifests conform to runtime and capability contracts', () => {
  const { APP_REGISTRY, getManifestById, hasAppCapability, getAppDisplayName } = registryMod;
  const { APP_COMMANDS } = commandsMod;

  const ids = APP_REGISTRY.map((app) => app.id);
  assert.equal(new Set(ids).size, ids.length, 'App IDs must be unique');

  for (const app of APP_REGISTRY) {
    assert.ok(['builtin', 'iframe', 'command'].includes(app.runtime), `Invalid runtime on ${app.id}`);
    assert.ok(['system', 'app', 'utility', 'game'].includes(app.category), `Invalid category on ${app.id}`);
    if (app.runtime === 'iframe') {
      assert.ok(app.iframe?.entryUrl, `iframe app ${app.id} must define iframe.entryUrl`);
    }
    if (app.runtime === 'command') {
      assert.equal(typeof APP_COMMANDS[app.id], 'function', `command app ${app.id} must register a command handler`);
    }
  }

  const exhv = getManifestById('exhyperv');
  assert.equal(exhv?.runtime, 'iframe');
  assert.equal(hasAppCapability(exhv, 'app-messaging'), true);
  assert.equal(hasAppCapability(exhv, 'hardware-probe'), true);
  assert.equal(hasAppCapability(exhv, 'shell-control'), false);

  const fsManifest = getManifestById('fullscreen');
  assert.equal(getAppDisplayName(fsManifest, { isFullscreen: false }), '全屏体验');
  assert.equal(getAppDisplayName(fsManifest, { isFullscreen: true }), '退出全屏');
});

test('shell store enforces command filtering, singleton focus, title updates, and maximize rules', () => {
  const store = storeMod.createShellStore();
  const vp = { width: 1280, height: 800 };

  assert.equal(store.openApp('fullscreen', vp), null, 'command app must not open a window');
  assert.equal(store.openApp('non-existent', vp), null);

  const tmPid = store.openApp('task-manager', vp);
  assert.ok(typeof tmPid === 'number');
  assert.equal(store.state.windows.length, 1);

  const exhvPid = store.openApp('exhyperv', vp, { vm: 'test' });
  assert.ok(typeof exhvPid === 'number');
  assert.equal(store.state.windows.length, 2);
  assert.equal(store.state.activePid, exhvPid);

  // Singleton re-focuses existing window instead of spawning a second window
  const exhvPidAgain = store.openApp('exhyperv', vp, { vm: 'updated' });
  assert.equal(exhvPidAgain, exhvPid);
  assert.equal(store.state.windows.length, 2);
  assert.deepEqual(store.state.windows.find((w) => w.pid === exhvPid)?.launchArgs, { vm: 'updated' });

  // Dynamic title update and empty reset
  store.setWindowTitle(exhvPid, 'ExHyperV - test');
  assert.equal(store.state.windows.find((w) => w.pid === exhvPid)?.title, 'ExHyperV - test');
  store.setWindowTitle(exhvPid, '   ');
  assert.equal(store.state.windows.find((w) => w.pid === exhvPid)?.title, 'ExHyperV');

  // maximizable: false blocks maximize
  const win = store.state.windows.find((w) => w.pid === exhvPid);
  win.maximizable = false;
  store.toggleMaximizeWindow(exhvPid, vp);
  assert.equal(win.maximized, false);
});

test('win11-app/v1 protocol validates messages and parses boot query params', () => {
  const {
    WIN11_APP_PROTOCOL,
    isWin11ShellToAppMessage,
    isWin11AppToShellMessage,
    readWin11BootQuery,
  } = protocolMod;

  assert.equal(
    isWin11ShellToAppMessage({
      protocol: WIN11_APP_PROTOCOL,
      type: 'w11:volume',
      volume: 42,
      muted: false,
    }),
    true,
  );
  assert.equal(isWin11ShellToAppMessage({ protocol: 'wrong', type: 'w11:volume' }), false);
  assert.equal(isWin11ShellToAppMessage({ protocol: WIN11_APP_PROTOCOL, type: 'w11:ready' }), false);

  assert.equal(
    isWin11AppToShellMessage({
      protocol: WIN11_APP_PROTOCOL,
      type: 'w11:app-event',
      channel: 'exhyperv:gpu-sync',
      payload: { mode: 'list' },
    }),
    true,
  );
  assert.equal(isWin11AppToShellMessage({ type: 'w11:ready' }), false);

  const query = readWin11BootQuery('?theme=light&volume=140&appId=pinball&pid=1009');
  assert.deepEqual(query, {
    theme: 'light',
    volume: 100,
    appId: 'pinball',
    pid: 1009,
  });
});

test('createWin11AppClient handles handshake, trusted origin checks, and legacy theme messages', () => {
  const prevWindow = globalThis.window;
  try {
    const listeners = new Map();
    const posted = [];
    const parentWin = {
      postMessage(msg, targetOrigin) {
        posted.push({ msg, targetOrigin });
      },
    };
    const fakeWindow = {
      parent: parentWin,
      location: { origin: 'https://exhyperv.example', search: '?theme=dark&volume=80' },
      addEventListener(type, fn) {
        if (!listeners.has(type)) listeners.set(type, new Set());
        listeners.get(type).add(fn);
      },
      removeEventListener(type, fn) {
        listeners.get(type)?.delete(fn);
      },
    };
    globalThis.window = fakeWindow;

    const themes = [];
    const volumes = [];
    const messages = [];

    const client = protocolMod.createWin11AppClient({
      onThemeChange: (t) => themes.push(t),
      onVolumeChange: (v, m) => volumes.push({ v, m }),
      onAppMessage: (c, p) => messages.push({ c, p }),
    });

    assert.equal(client.isEmbedded, true);
    assert.deepEqual(themes, ['dark']);
    assert.deepEqual(volumes, [{ v: 80, m: false }]);
    assert.equal(posted[0].msg.type, 'w11:ready');
    assert.equal(posted[0].targetOrigin, 'https://exhyperv.example');

    const dispatch = (event) => {
      listeners.get('message')?.forEach((fn) => fn(event));
    };

    // Untrusted origin is ignored
    dispatch({
      source: parentWin,
      origin: 'https://evil.example',
      data: { protocol: 'win11-app/v1', type: 'w11:theme', theme: 'light' },
    });
    assert.deepEqual(themes, ['dark']);

    // Trusted init & app message
    dispatch({
      source: parentWin,
      origin: 'https://exhyperv.example',
      data: {
        protocol: 'win11-app/v1',
        type: 'w11:app-message',
        channel: 'exhyperv:gpu-candidate',
        payload: { variant: 'tiles' },
      },
    });
    assert.deepEqual(messages, [{ c: 'exhyperv:gpu-candidate', p: { variant: 'tiles' } }]);

    // Legacy theme fallback works
    dispatch({
      source: parentWin,
      origin: 'https://exhyperv.example',
      data: { type: 'w11-theme', theme: 'light' },
    });
    assert.deepEqual(themes, ['dark', 'light']);

    client.setTitle('ExHyperV Ready');
    assert.equal(posted.at(-1).msg.type, 'w11:set-title');
    assert.equal(posted.at(-1).msg.title, 'ExHyperV Ready');

    client.destroy();
    assert.equal(listeners.get('message').size, 0);
  } finally {
    globalThis.window = prevWindow;
  }
});

test('mountIframeApp queues messages until w11:ready and enforces manifest capabilities', () => {
  const prevWindow = globalThis.window;
  const prevDocument = globalThis.document;
  try {
    const winListeners = new Map();
    const postedToFrame = [];
    let createdFrame = null;

    class FakeIframe extends EventTarget {
      attributes = new Map();
      className = '';
      title = '';
      src = '';
      contentWindow = {
        postMessage(msg, targetOrigin) {
          postedToFrame.push({ msg, targetOrigin });
        },
      };
      setAttribute(name, val) {
        this.attributes.set(name, val);
      }
    }

    const fakeDocument = {
      createElement(tag) {
        assert.equal(tag, 'iframe');
        createdFrame = new FakeIframe();
        return createdFrame;
      },
    };
    const fakeWindow = {
      location: { origin: 'https://exhyperv.example' },
      addEventListener(type, fn) {
        if (!winListeners.has(type)) winListeners.set(type, new Set());
        winListeners.get(type).add(fn);
      },
      removeEventListener(type, fn) {
        winListeners.get(type)?.delete(fn);
      },
    };

    globalThis.document = fakeDocument;
    globalThis.window = fakeWindow;

    const containerChildren = [];
    const fakeContainer = {
      replaceChildren(...nodes) {
        containerChildren.splice(0, containerChildren.length, ...nodes);
      },
    };

    const calls = [];
    const hwService = virtualHwMod.createVirtualHardwareService();
    const shell = {
      getTheme: () => 'dark',
      setTheme: () => {},
      getVolume: () => 65,
      setVolume: () => {},
      getBrightness: () => 100,
      setBrightness: () => {},
      getIconSize: () => 'medium',
      setIconSize: () => {},
      resetDesktopLayout: () => {},
      getWindows: () => [{ pid: 1002, appId: 'exhyperv', minimized: false }],
      openApp: (appId, args) => {
        calls.push({ op: 'openApp', appId, args });
        return 1003;
      },
      closeWindow: (pid) => calls.push({ op: 'closeWindow', pid }),
      focusWindow: (pid) => calls.push({ op: 'focusWindow', pid }),
      minimizeWindow: (pid) => calls.push({ op: 'minimizeWindow', pid }),
      toggleMaximizeWindow: (pid) => calls.push({ op: 'toggleMaximizeWindow', pid }),
      setWindowTitle: (pid, title) => calls.push({ op: 'setWindowTitle', pid, title }),
      isFullscreen: () => false,
      toggleFullscreen: () => {},
      getVirtualHardware: () => hwService.getSnapshot(),
      refreshVirtualHardware: async () => hwService.getSnapshot(),
      refreshVirtualStorage: async () => ({}),
      getHardwareReport: () => null,
      refreshHardwareReport: async () => ({}),
    };

    const appEvents = [];
    const manifest = registryMod.getManifestById('exhyperv');
    const lifecycle = iframeHostMod.mountIframeApp(
      {
        pid: 1002,
        appId: 'exhyperv',
        manifest,
        container: fakeContainer,
        shell,
      },
      {
        onAppEvent: (appId, pid, channel, payload) => appEvents.push({ appId, pid, channel, payload }),
      },
    );

    assert.equal(containerChildren[0], createdFrame);
    assert.equal(
      createdFrame.src,
      '/apps/exhyperv/?theme=dark&volume=65&appId=exhyperv&pid=1002',
    );

    // Messages sent before w11:ready are queued
    lifecycle.onMessage('exhyperv:gpu-candidate', { variant: 'layered' });
    assert.equal(postedToFrame.length, 0);

    // Simulate w11:ready from iframe
    const dispatchMsg = (data, origin = 'https://exhyperv.example', source = createdFrame.contentWindow) => {
      winListeners.get('message')?.forEach((fn) => fn({ data, origin, source }));
    };

    dispatchMsg({ protocol: 'win11-app/v1', type: 'w11:ready' });
    assert.equal(postedToFrame[0].msg.type, 'w11:init');
    assert.ok(postedToFrame[0].msg.hardware, 'exhyperv init payload should include VirtualHardwareSnapshot');
    assert.equal(postedToFrame[1].msg.type, 'w11:app-message');
    assert.deepEqual(postedToFrame[1].msg.payload, { variant: 'layered' });

    // Forward hardware updates and honor w11:request-hardware
    hwService.tick();
    const tickedSnap = hwService.getSnapshot();
    lifecycle.onHardwareUpdate(tickedSnap);
    assert.ok(
      postedToFrame.some((p) => p.msg.type === 'w11:hardware' && p.msg.hardware.cpu.usagePct === tickedSnap.cpu.usagePct),
    );

    const beforeReqCount = postedToFrame.length;
    dispatchMsg({ protocol: 'win11-app/v1', type: 'w11:request-hardware', force: false });
    assert.equal(postedToFrame.length, beforeReqCount + 1);
    assert.equal(postedToFrame.at(-1).msg.type, 'w11:hardware');

    // Forward volume & theme
    lifecycle.onThemeChange('light');
    lifecycle.onVolumeChange(0);
    assert.ok(postedToFrame.some((p) => p.msg.type === 'w11:theme' && p.msg.theme === 'light'));
    assert.ok(postedToFrame.some((p) => p.msg.type === 'w11:volume' && p.msg.muted === true));

    // App -> Shell events & capability gating (exhyperv lacks shell-control, has window-control + app-messaging)
    dispatchMsg({ protocol: 'win11-app/v1', type: 'w11:open-app', appId: 'settings' });
    assert.equal(calls.some((c) => c.op === 'openApp'), false, 'open-app must be blocked without shell-control');

    dispatchMsg({ protocol: 'win11-app/v1', type: 'w11:window-control', action: 'minimize' });
    assert.deepEqual(calls.at(-1), { op: 'minimizeWindow', pid: 1002 });

    dispatchMsg({
      protocol: 'win11-app/v1',
      type: 'w11:app-event',
      channel: 'exhyperv:gpu-sync',
      payload: { variant: 'layered' },
    });
    assert.deepEqual(appEvents, [
      { appId: 'exhyperv', pid: 1002, channel: 'exhyperv:gpu-sync', payload: { variant: 'layered' } },
    ]);

    lifecycle.destroy();
    assert.equal(createdFrame.src, 'about:blank');
    assert.equal(winListeners.get('message').size, 0);
  } finally {
    globalThis.window = prevWindow;
    globalThis.document = prevDocument;
  }
});

test('builtin settings and placeholder apps implement WinAppModule cleanly', () => {
  let theme = 'dark';
  let iconSize = 'medium';
  let resetCalls = 0;
  const listeners = new Map();

  const fakeContainer = {
    innerHTML: '',
    addEventListener(type, fn) {
      listeners.set(type, fn);
    },
    removeEventListener(type, fn) {
      if (listeners.get(type) === fn) listeners.delete(type);
    },
  };

  const shell = {
    getTheme: () => theme,
    setTheme: (t) => {
      theme = t;
    },
    getVolume: () => 65,
    setVolume: () => {},
    getBrightness: () => 100,
    setBrightness: () => {},
    getIconSize: () => iconSize,
    setIconSize: (s) => {
      iconSize = s;
    },
    resetDesktopLayout: () => {
      resetCalls += 1;
    },
    getWindows: () => [],
    openApp: () => null,
    closeWindow: () => {},
    focusWindow: () => {},
    minimizeWindow: () => {},
    toggleMaximizeWindow: () => {},
    setWindowTitle: () => {},
    isFullscreen: () => false,
    toggleFullscreen: () => {},
    getVirtualHardware: () => ({}),
    refreshVirtualHardware: async () => ({}),
    refreshVirtualStorage: async () => ({}),
    getHardwareReport: () => null,
    refreshHardwareReport: async () => ({}),
  };

  const settingsLife = settingsAppMod.settingsApp.mount({
    pid: 1003,
    appId: 'settings',
    manifest: registryMod.getManifestById('settings'),
    container: fakeContainer,
    shell,
  });

  assert.ok(fakeContainer.innerHTML.includes('个性化与桌面设置'));
  const clickHandler = listeners.get('click');
  assert.equal(typeof clickHandler, 'function');

  clickHandler({
    target: {
      closest: () => ({ dataset: { action: 'theme-light' } }),
    },
  });
  assert.equal(theme, 'light');

  clickHandler({
    target: {
      closest: () => ({ dataset: { action: 'icon-large' } }),
    },
  });
  assert.equal(iconSize, 'large');

  clickHandler({
    target: {
      closest: () => ({ dataset: { action: 'reset-layout' } }),
    },
  });
  assert.equal(resetCalls, 1);

  settingsLife.onThemeChange();
  assert.ok(fakeContainer.innerHTML.includes('data-action="theme-light"'));
  settingsLife.destroy();
  assert.equal(listeners.size, 0);

  const stubContainer = { innerHTML: '' };
  placeholderAppMod.placeholderApp.mount({
    pid: 1004,
    appId: 'this-pc',
    manifest: registryMod.getManifestById('this-pc'),
    container: stubContainer,
    shell,
  });
  assert.ok(stubContainer.innerHTML.includes('此电脑'));
  assert.ok(stubContainer.innerHTML.includes('PID 1004'));
});

test('public/_headers permits same-origin framing under /apps/* while denying global framing', async () => {
  const headersText = await readFile(path.join(websiteRoot, 'public/_headers'), 'utf8');
  assert.match(headersText, /\/\*\s+[\s\S]*X-Frame-Options:\s*DENY/);
  assert.match(headersText, /\/apps\/\*\s+[\s\S]*!\s*X-Frame-Options[\s\S]*Content-Security-Policy:\s*frame-ancestors 'self'/);
});

test('virtualHardwareService shares identical live snapshots across taskManagerApp and iframe apps', () => {
  const prevWindow = globalThis.window;
  const prevDocument = globalThis.document;
  try {
    const winListeners = new Map();
    const intervals = new Map();
    let nextTimerId = 10;
    const clearedTimers = [];
    const fakeWindow = {
      isSecureContext: false,
      location: { origin: 'https://exhyperv.example' },
      setInterval: (fn) => {
        const id = ++nextTimerId;
        intervals.set(id, fn);
        return id;
      },
      clearInterval: (id) => {
        clearedTimers.push(id);
        intervals.delete(id);
      },
      setTimeout: () => 1,
      addEventListener: (type, fn) => {
        if (!winListeners.has(type)) winListeners.set(type, new Set());
        winListeners.get(type).add(fn);
      },
      removeEventListener: (type, fn) => winListeners.get(type)?.delete(fn),
    };
    globalThis.window = fakeWindow;
    globalThis.document = { activeElement: null };

    const hwService = virtualHwMod.createVirtualHardwareService();
    const receivedSnaps = [];
    const unsub = hwService.subscribe((snap) => receivedSnaps.push(snap));
    assert.equal(intervals.size, 1, 'subscribe starts shared 1s ticker');

    hwService.tick();
    assert.equal(receivedSnaps.length, 1);
    const sharedSnap = receivedSnaps[0];
    assert.equal(sharedSnap.cpu.history.length, 60);
    assert.equal(sharedSnap.memory.history.length, 60);
    assert.equal(sharedSnap.gpus[0].pts3d.length, 60);

    const containerListeners = new Map();
    const fakeContainer = {
      innerHTML: '',
      contains: () => false,
      querySelector: () => null,
      addEventListener: (type, fn) => containerListeners.set(type, fn),
      removeEventListener: (type, fn) => containerListeners.delete(type),
    };

    const openedApps = [];
    const closedPids = [];
    const windows = [
      { pid: 1002, appId: 'exhyperv', title: 'ExHyperV', iconKey: 'exhyperv', minimized: false },
    ];

    const shell = {
      getTheme: () => 'dark',
      setTheme: () => {},
      getVolume: () => 65,
      setVolume: () => {},
      getBrightness: () => 100,
      setBrightness: () => {},
      getIconSize: () => 'medium',
      setIconSize: () => {},
      resetDesktopLayout: () => {},
      getWindows: () => windows,
      openApp: (id) => {
        openedApps.push(id);
        return 1003;
      },
      closeWindow: (pid) => closedPids.push(pid),
      focusWindow: () => {},
      minimizeWindow: () => {},
      toggleMaximizeWindow: () => {},
      setWindowTitle: () => {},
      isFullscreen: () => false,
      toggleFullscreen: () => {},
      getVirtualHardware: () => hwService.getSnapshot(),
      refreshVirtualHardware: async () => hwService.getSnapshot(),
      refreshVirtualStorage: async () => ({}),
      getHardwareReport: () => null,
      refreshHardwareReport: async () => ({}),
    };

    const lifecycle = appsIndexMod.mountAppInstance({
      pid: 1001,
      appId: 'task-manager',
      manifest: registryMod.getManifestById('task-manager'),
      container: fakeContainer,
      shell,
    });

    // Task Manager renders shared CPU % from VirtualHardwareSnapshot
    assert.ok(fakeContainer.innerHTML.includes(`${sharedSnap.cpu.usagePct}%`));

    // Pushing an updated snapshot updates Task Manager view
    hwService.tick();
    const nextSnap = hwService.getSnapshot();
    lifecycle.onHardwareUpdate(nextSnap);
    assert.ok(fakeContainer.innerHTML.includes(`${nextSnap.cpu.usagePct}%`));

    // Switch to Processes tab and verify manifest-driven memory baseline (188.5 MB)
    const clickHandler = containerListeners.get('click');
    clickHandler({
      target: { closest: (sel) => (sel === '[data-tm-tab]' ? { dataset: { tmTab: 'processes' } } : null) },
    });
    assert.ok(fakeContainer.innerHTML.includes('ExHyperV'));
    assert.ok(fakeContainer.innerHTML.includes('188.5 MB'));

    // Search by publisher from APP_REGISTRY.telemetry.publisher
    const inputHandler = containerListeners.get('input');
    inputHandler({
      target: { matches: (sel) => sel === '[data-tm-search]', value: 'ExHyperV Project' },
    });
    assert.ok(fakeContainer.innerHTML.includes('ExHyperV'));
    assert.ok(!fakeContainer.innerHTML.includes('Google Chrome'));

    // New task launches via ctx.shell.openApp
    clickHandler({
      target: { closest: (sel) => (sel === '[data-tm-action]' ? { dataset: { tmAction: 'new-task' } } : null) },
    });
    assert.deepEqual(openedApps, ['exhyperv']);

    // Select real window and End Task via ctx.shell.closeWindow
    clickHandler({
      target: { closest: (sel) => (sel === '[data-tm-proc]' ? { dataset: { tmProc: 'win-1002' } } : null) },
    });
    clickHandler({
      target: { closest: (sel) => (sel === '[data-tm-action]' ? { dataset: { tmAction: 'end-task' } } : null) },
    });
    assert.deepEqual(closedPids, [1002]);

    // Command dispatch works uniformly
    let fsToggled = false;
    assert.equal(
      appsIndexMod.executeCommandApp('fullscreen', { ...shell, toggleFullscreen: () => { fsToggled = true; } }),
      true,
    );
    assert.equal(fsToggled, true);

    lifecycle.destroy();
    unsub();
    assert.equal(intervals.size, 0, 'unsubscribing stops shared ticker');
    assert.ok(clearedTimers.length >= 1);
  } finally {
    globalThis.window = prevWindow;
    globalThis.document = prevDocument;
  }
});
