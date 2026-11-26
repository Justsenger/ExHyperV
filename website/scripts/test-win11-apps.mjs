import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { after, before, test } from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';

const websiteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const exhypervWowRoot = path.resolve(websiteRoot, '../src/ExHyperV.WoW');

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
let exhvConfigMod;
let registryMod;
let storeMod;
let kernelMod;
let contractsMod;
let protocolMod;
let syscallProxyMod;
let commandsMod;
let iframeRuntimeMod;
let settingsAppMod;
let placeholderAppMod;
let appsIndexMod;
let overlaysMod;
let virtualHwMod;
let virtualStorageMod;
let win11FsSkeletonMod;
let uiHostMod;

before(async () => {
  const vite = await loadVite();
  viteServer = await vite.createServer({
    root: websiteRoot,
    cacheDir: path.join(os.tmpdir(), 'exhyperv-vite-test-cache'),
    optimizeDeps: { noDiscovery: true },
    server: { middlewareMode: true, watch: null, ws: false, fs: { allow: [websiteRoot, exhypervWowRoot] } },
    appType: 'custom',
    logLevel: 'silent',
  });
  const hostConfigMod = await viteServer.ssrLoadModule('/src/shells/win11/host-config.ts');
  exhvConfigMod = await viteServer.ssrLoadModule(path.join(exhypervWowRoot, 'data/exhyperv-desktop-config.ts'));
  hostConfigMod.configureWinDesktopHost(exhvConfigMod.EXHYPERV_HOST_CONFIG);

  registryMod = await viteServer.ssrLoadModule('/src/shells/win11/shell/registry.ts');
  storeMod = await viteServer.ssrLoadModule('/src/shells/win11/shell/store.ts');
  kernelMod = await viteServer.ssrLoadModule('/src/wow/kernel/index.ts');
  contractsMod = await viteServer.ssrLoadModule('/src/wow/contracts/index.ts');
  protocolMod = await viteServer.ssrLoadModule('/src/wow/runtime/protocol.ts');
  syscallProxyMod = await viteServer.ssrLoadModule('/src/wow/runtime/syscall-proxy.ts');
  commandsMod = await viteServer.ssrLoadModule('/src/shells/win11/apps/commands.ts');
  iframeRuntimeMod = await viteServer.ssrLoadModule('/src/wow/runtime/iframe-runtime.ts');
  settingsAppMod = await viteServer.ssrLoadModule('/src/shells/win11/apps/settings-app.ts');
  placeholderAppMod = await viteServer.ssrLoadModule('/src/shells/win11/apps/placeholder-app.ts');
  appsIndexMod = await viteServer.ssrLoadModule('/src/shells/win11/apps/index.ts');
  overlaysMod = await viteServer.ssrLoadModule('/src/shells/win11/shared/overlays.ts');
  virtualHwMod = await viteServer.ssrLoadModule('/src/wow/hal/hardware.ts');
  virtualStorageMod = await viteServer.ssrLoadModule('/src/shells/win11/system/virtual-storage.ts');
  win11FsSkeletonMod = await viteServer.ssrLoadModule('/src/shells/win11/system/win11-fs-skeleton.ts');
  uiHostMod = await viteServer.ssrLoadModule(path.join(exhypervWowRoot, 'scripts/ui-design-host.js'));
});

after(async () => {
  await viteServer?.close();
});

test('getAppRegistry manifests conform to runtime and capability contracts', () => {
  const { getAppRegistry, getManifestById, hasAppCapability, getAppDisplayName } = registryMod;
  const { APP_COMMANDS } = commandsMod;

  const apps = getAppRegistry();
  const ids = apps.map((app) => app.id);
  assert.equal(new Set(ids).size, ids.length, 'App IDs must be unique');

  for (const app of apps) {
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
  assert.equal(exhv?.systemTrusted, true);
  assert.equal(hasAppCapability(exhv, 'app-messaging'), true);
  assert.equal(hasAppCapability(exhv, 'hardware-probe'), false);
  assert.equal(hasAppCapability(exhv, 'shell-control'), false);

  const fsManifest = getManifestById('fullscreen');
  assert.equal(getAppDisplayName(fsManifest, { isFullscreen: false }), '全屏体验');
  assert.equal(getAppDisplayName(fsManifest, { isFullscreen: true }), '退出全屏');
});

test('shell store enforces command filtering, singleton focus, title updates, and maximize rules', () => {
  const processes = kernelMod.createProcessManager(() => {});
  const store = storeMod.createShellStore(processes);
  const vp = { width: 1280, height: 800 };

  assert.equal(store.openApp('fullscreen', vp), null, 'command app must not open a window');
  assert.equal(store.openApp('non-existent', vp), null);

  const tmWid = store.openApp('task-manager', vp);
  assert.ok(typeof tmWid === 'number');
  assert.equal(store.state.windows.length, 1);

  const exhvWid = store.openApp('exhyperv', vp, { vm: 'test' });
  assert.ok(typeof exhvWid === 'number');
  assert.equal(store.state.windows.length, 2);
  assert.equal(store.state.activeWid, exhvWid);

  // Singleton re-focuses existing window instead of spawning a second window
  const exhvWidAgain = store.openApp('exhyperv', vp, { vm: 'updated' });
  assert.equal(exhvWidAgain, exhvWid);
  assert.equal(store.state.windows.length, 2);
  assert.deepEqual(store.state.windows.find((w) => w.wid === exhvWid)?.launchArgs, { vm: 'updated' });

  // Dynamic title update and empty reset
  store.setWindowTitle(exhvWid, 'ExHyperV - test');
  assert.equal(store.state.windows.find((w) => w.wid === exhvWid)?.title, 'ExHyperV - test');
  store.setWindowTitle(exhvWid, '   ');
  assert.equal(store.state.windows.find((w) => w.wid === exhvWid)?.title, 'ExHyperV');

  // maximizable: false blocks maximize
  const win = store.state.windows.find((w) => w.wid === exhvWid);
  win.maximizable = false;
  store.toggleMaximizeWindow(exhvWid, vp);
  assert.equal(win.maximized, false);
});

test('syscallProxy enforces capability declarations and validates wow/v1 parameters', async () => {
  const { syscallProxy } = syscallProxyMod;
  const { ERROR } = contractsMod;

  const granted = new Set(['app-messaging', 'hardware-probe']);
  const calls = [];
  const handler = syscallProxy(
    {
      'fs.pickOpen': () => null,
      'fs.pickSave': () => null,
      'fs.listDir': () => [],
      'fs.readFile': () => null,
      'fs.writeFile': () => null,
      'storage.get': () => null,
      'storage.set': () => undefined,
      'storage.remove': () => undefined,
      'storage.clear': () => undefined,
      'notify.show': () => true,
      'hw.getSnapshot': () => ({ cpu: { shortName: 'X', logicalCores: 8 }, memory: { totalGb: 16 } }),
      'hw.refresh': () => ({}),
      'shell.openFile': () => null,
      'shell.openApp': (p) => {
        calls.push(['shell.openApp', p]);
        return 'inst-2';
      },
      'window.title': () => undefined,
      'window.control': () => undefined,
      'app.send': () => true,
      'app.call': () => null,
      'app.event': (p) => calls.push(['app.event', p]),
      'data.open': () => undefined,
    },
    async (cap) => granted.has(cap),
  );

  const signal = new AbortController().signal;
  await handler('app.event', { channel: 'exhyperv:host-sync', payload: { ok: true } }, [], signal);
  assert.deepEqual(calls, [['app.event', { channel: 'exhyperv:host-sync', payload: { ok: true } }]]);

  await assert.rejects(
    () => handler('shell.openApp', { appId: 'settings' }, [], signal),
    (err) => err?.code === ERROR.denied,
  );
  await assert.rejects(
    () => handler('unknown.method', {}, [], signal),
    (err) => err?.code === ERROR.unknown,
  );
  await assert.rejects(
    () => handler('app.event', { channel: '' }, [], signal),
    (err) => err?.code === ERROR.invalid,
  );
});

test('mountIframeApp and createWowClient complete MessageChannel handshake, queue messages, and gate capabilities', async () => {
  const prevWindow = globalThis.window;
  const prevDocument = globalThis.document;
  const prevLocation = globalThis.location;
  const prevParent = globalThis.parent;
  try {
    const hostWinListeners = new Map();
    const clientWinListeners = new Map();
    const hostOrigin = 'https://exhyperv.example';
    let createdFrame = null;
    let clientWindowProxy = null;

    const addListener = (map, type, fn) => {
      if (!map.has(type)) map.set(type, new Set());
      map.get(type).add(fn);
    };
    const removeListener = (map, type, fn) => map.get(type)?.delete(fn);
    const dispatchMessage = (map, data, origin, source, ports = []) => {
      map.get('message')?.forEach((fn) => fn({ data, origin, source, ports }));
    };

    const hostWindow = {
      location: { origin: hostOrigin, href: `${hostOrigin}/desktop/` },
      addEventListener: (type, fn) => addListener(hostWinListeners, type, fn),
      removeEventListener: (type, fn) => removeListener(hostWinListeners, type, fn),
      postMessage(msg, _targetOrigin, ports = []) {
        dispatchMessage(hostWinListeners, msg, hostOrigin, clientWindowProxy, ports);
      },
    };

    class FakeIframe extends EventTarget {
      attributes = new Map();
      className = '';
      title = '';
      src = '';
      contentWindow = {
        postMessage(msg, targetOrigin, ports = []) {
          dispatchMessage(clientWinListeners, msg, targetOrigin, hostWindow, ports);
        },
      };
      setAttribute(name, val) {
        this.attributes.set(name, val);
      }
      toggleAttribute(name, force) {
        if (force) this.attributes.set(name, '');
        else this.attributes.delete(name);
      }
      remove() {
        this.src = 'about:blank';
      }
    }

    const fakeDocument = {
      referrer: `${hostOrigin}/desktop/`,
      querySelectorAll: () => [],
      addEventListener: () => {},
      removeEventListener: () => {},
      createElement(tag) {
        assert.equal(tag, 'iframe');
        createdFrame = new FakeIframe();
        clientWindowProxy = createdFrame.contentWindow;
        return createdFrame;
      },
    };

    globalThis.location = hostWindow.location;
    globalThis.window = hostWindow;
    globalThis.document = fakeDocument;
    globalThis.parent = hostWindow;

    const containerChildren = [];
    const fakeContainer = {
      replaceChildren(...nodes) {
        containerChildren.splice(0, containerChildren.length, ...nodes);
      },
    };

    const processes = kernelMod.createProcessManager(() => {});
    const hwService = virtualHwMod.createVirtualHardwareService(processes.snapshot);
    const calls = [];
    const appEvents = [];
    const manifest = registryMod.getManifestById('exhyperv');

    const lifecycle = iframeRuntimeMod.mountIframeApp({
      container: fakeContainer,
      entryUrl: manifest.iframe.entryUrl,
      title: manifest.name,
      className: 'w11-win-iframe',
      passQuery: true,
      boot: () => ({
        appId: 'exhyperv',
        pid: 1002,
        wid: 2,
        instanceId: 'inst-exhv-2',
        theme: 'dark',
        volume: 65,
        focused: true,
        minimized: false,
      }),
      authorize: async (cap) => (manifest.capabilities ?? []).includes(cap),
      observeGuest: () => () => {},
      ownPort: () => () => {},
      releaseInput: () => {},
      methods: {
        'fs.pickOpen': () => null,
        'fs.pickSave': () => null,
        'fs.listDir': () => [],
        'fs.readFile': () => null,
        'fs.writeFile': () => null,
        'storage.get': () => null,
        'storage.set': () => undefined,
        'storage.remove': () => undefined,
        'storage.clear': () => undefined,
        'notify.show': () => true,
        'hw.getSnapshot': () => hwService.getSnapshot(),
        'hw.refresh': async () => hwService.getSnapshot(),
        'shell.openFile': () => null,
        'shell.openApp': (p) => {
          calls.push({ op: 'openApp', appId: p.appId });
          return 'inst-3';
        },
        'window.title': (p) => calls.push({ op: 'setWindowTitle', title: p.title }),
        'window.control': (p) => calls.push({ op: 'windowControl', action: p.action }),
        'app.send': () => true,
        'app.call': () => null,
        'app.event': (p) => appEvents.push({ channel: p.channel, payload: p.payload }),
        'data.open': () => undefined,
      },
    });

    assert.equal(containerChildren[0], createdFrame);
    assert.equal(
      createdFrame.src,
      `${hostOrigin}/apps/exhyperv/?theme=dark&volume=65&pid=1002&appId=exhyperv`,
    );

    // Messages sent before MessageChannel handshake completes are queued
    lifecycle.onMessage('exhyperv:gpu-candidate', { variant: 'layered' });

    // Switch window globals briefly for client-side createWowClient handshake inside iframe
    const clientWindow = {
      location: { origin: hostOrigin, href: createdFrame.src },
      addEventListener: (type, fn) => addListener(clientWinListeners, type, fn),
      removeEventListener: (type, fn) => removeListener(clientWinListeners, type, fn),
    };
    globalThis.window = clientWindow;
    globalThis.parent = hostWindow;

    const themes = [];
    const clientMessages = [];
    const clientHw = [];
    let bootInfo = null;

    createdFrame.dispatchEvent(new Event('load'));
    const client = protocolMod.createWowClient({
      shellOrigin: hostOrigin,
      onInit: (info) => {
        bootInfo = info;
      },
      onThemeChange: (t) => themes.push(t),
      onEvent: (name, val) => {
        if (name === 'message') clientMessages.push(val);
        if (name === 'hardware') clientHw.push(val);
      },
    });

    await new Promise((r) => setTimeout(r, 20));
    assert.equal(bootInfo?.appId, 'exhyperv');
    assert.deepEqual(themes, ['dark']);
    assert.deepEqual(clientMessages, [{ channel: 'exhyperv:gpu-candidate', payload: { variant: 'layered' }, from: undefined }]);

    hwService.tick();
    const tickedSnap = hwService.getSnapshot();
    lifecycle.onHardwareUpdate(tickedSnap);
    lifecycle.onThemeChange('light');
    await new Promise((r) => setTimeout(r, 20));
    assert.ok(clientHw.some((h) => h.cpu?.usagePct === tickedSnap.cpu.usagePct));
    assert.deepEqual(themes, ['dark', 'light']);

    await assert.rejects(
      () => client.getHwSnapshot(),
      (err) => err?.code === contractsMod.ERROR.denied,
      'getHwSnapshot must be blocked without hardware-probe',
    );

    await assert.rejects(
      () => client.openApp('settings'),
      (err) => err?.code === contractsMod.ERROR.denied,
      'openApp must be blocked without shell-control',
    );
    assert.equal(calls.some((c) => c.op === 'openApp'), false);

    await client.windowControl('minimize');
    assert.deepEqual(calls.at(-1), { op: 'windowControl', action: 'minimize' });

    await client.request('app.event', { channel: 'exhyperv:gpu-sync', payload: { variant: 'layered' } });
    assert.deepEqual(appEvents, [{ channel: 'exhyperv:gpu-sync', payload: { variant: 'layered' } }]);

    client.destroy();
    assert.equal(clientWinListeners.get('message')?.size ?? 0, 0);
    globalThis.window = hostWindow;
    lifecycle.destroy();
    assert.equal(createdFrame.src, 'about:blank');
    assert.equal(hostWinListeners.get('message')?.size ?? 0, 0);
  } finally {
    globalThis.window = prevWindow;
    globalThis.document = prevDocument;
    globalThis.location = prevLocation;
    globalThis.parent = prevParent;
  }
});

test('builtin settings and placeholder apps implement WinAppModule cleanly', () => {
  let theme = 'dark';
  let iconSize = 'medium';
  const listeners = new Map();

  const fakeContainer = {
    innerHTML: '',
    closest: () => null,
    addEventListener(type, fn) {
      if (!listeners.has(type)) listeners.set(type, new Set());
      listeners.get(type).add(fn);
    },
    removeEventListener(type, fn) {
      const set = listeners.get(type);
      if (!set) return;
      set.delete(fn);
      if (set.size === 0) listeners.delete(type);
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
    resetDesktopLayout: () => {},
    getWindows: () => [],
    openApp: () => null,
    closeWindow: () => {},
    focusWindow: () => {},
    minimizeWindow: () => {},
    toggleMaximizeWindow: () => {},
    setWindowTitle: () => {},
    isFullscreen: () => false,
    toggleFullscreen: () => {},
    renderAppIcon: (iconKey) => `<svg data-icon="${iconKey}"></svg>`,
    getVirtualHardware: () => ({}),
    refreshVirtualHardware: async () => ({}),
    refreshVirtualStorage: async () => ({}),
    getHardwareReport: () => null,
    refreshHardwareReport: async () => ({}),
  };

  const settingsLife = settingsAppMod.settingsApp.mount({
    pid: 1003,
    wid: 3,
    instanceId: 'inst-1003',
    appId: 'settings',
    manifest: registryMod.getManifestById('settings'),
    container: fakeContainer,
    shell,
  });

  assert.ok(fakeContainer.innerHTML.includes('w11-settings-shell'));
  assert.ok(fakeContainer.innerHTML.includes('推荐设置'));
  const clickHandlers = [...(listeners.get('click') ?? [])];
  assert.ok(clickHandlers.length >= 1);
  const clickHandler = clickHandlers[0];

  clickHandler({
    target: {
      closest: (sel) => (sel === '[data-action]' ? { dataset: { action: 'theme-light' } } : null),
    },
  });
  assert.equal(theme, 'light');

  clickHandler({
    target: {
      closest: (sel) => (sel === '[data-action]' ? { dataset: { action: 'icon-large' } } : null),
    },
  });
  assert.equal(iconSize, 'large');

  settingsLife.onThemeChange();
  assert.ok(fakeContainer.innerHTML.includes('data-settings-theme="light"'));
  settingsLife.onMessage?.('navigate', 'taskbar');
  assert.ok(fakeContainer.innerHTML.includes('w11-taskbar-preferences'));
  settingsLife.destroy();
  assert.equal(listeners.size, 0);

  const stubContainer = { innerHTML: '' };
  placeholderAppMod.placeholderApp.mount({
    pid: 1004,
    wid: 4,
    instanceId: 'inst-1004',
    appId: 'this-pc',
    manifest: registryMod.getManifestById('this-pc'),
    container: stubContainer,
    shell,
  });
  assert.ok(stubContainer.innerHTML.includes('此电脑'));
  assert.ok(stubContainer.innerHTML.includes('PID 1004'));
});

test('public/_headers permits same-origin framing under /apps/* and /wow/apps/* while denying global framing', async () => {
  const headersText = await readFile(path.join(websiteRoot, 'public/_headers'), 'utf8');
  assert.match(headersText, /\/\*\s+[\s\S]*X-Frame-Options:\s*DENY/);
  assert.match(headersText, /\/apps\/\*\s+[\s\S]*!\s*X-Frame-Options[\s\S]*Content-Security-Policy:\s*frame-ancestors 'self'/);
  assert.match(headersText, /\/wow\/apps\/\*\s+[\s\S]*!\s*X-Frame-Options[\s\S]*Content-Security-Policy:\s*frame-ancestors 'self'/);
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
    const createdEls = [];
    const autoSlotSelectors = new Set([
      '[data-tm-topbar]',
      '[data-tm-content]',
      '[data-tm-nav-toggle]',
      '.w11-tm-title-search-input',
      '.w11-tm-title-search-clear',
      '.w11-win-controls',
      '.w11-win-title',
      '.w11-win-titlebar',
      '.w11-tm-modal-foot',
      '.w11-tm-modal-x',
      '[data-run-input]',
    ]);
    const createMockEl = (tag = 'div') => {
      const elListeners = new Map();
      const children = [];
      const slots = new Map();
      const el = {
        tagName: tag.toUpperCase(),
        className: '',
        innerHTML: '',
        textContent: '',
        type: '',
        hidden: false,
        value: '',
        scrollTop: 0,
        offsetWidth: 800,
        offsetHeight: 600,
        dataset: {},
        style: { setProperty: () => {}, removeProperty: () => {} },
        classList: { toggle: () => {}, add: () => {}, remove: () => {}, contains: () => false },
        appendChild(child) {
          children.push(child);
          return child;
        },
        append(...nodes) {
          children.push(...nodes);
        },
        insertBefore(node) {
          children.push(node);
          return node;
        },
        remove() {},
        focus() {},
        select() {},
        setAttribute() {},
        contains: (other) => other === el || children.includes(other),
        closest: () => null,
        getBoundingClientRect: () => ({ left: 0, top: 0, width: 800, height: 600 }),
        querySelector(sel) {
          if (!autoSlotSelectors.has(sel)) return null;
          if (!slots.has(sel)) {
            const sub = createMockEl('div');
            if (sel === '.w11-tm-title-search-input' || sel === '[data-run-input]') sub.tagName = 'INPUT';
            slots.set(sel, sub);
          }
          return slots.get(sel);
        },
        querySelectorAll: () => [],
        addEventListener: (type, fn) => elListeners.set(type, fn),
        removeEventListener: (type) => elListeners.delete(type),
        _listeners: elListeners,
        _slots: slots,
      };
      return el;
    };

    const titlebarEl = createMockEl('div');
    const winEl = createMockEl('div');
    winEl.querySelector = (sel) => (sel === '.w11-win-titlebar' ? titlebarEl : createMockEl('div'));

    globalThis.window = fakeWindow;
    globalThis.document = {
      activeElement: null,
      createElement: (tag) => {
        const el = createMockEl(tag);
        createdEls.push(el);
        return el;
      },
      querySelectorAll: () => [],
      addEventListener: () => {},
      removeEventListener: () => {},
    };

    const processes = kernelMod.createProcessManager(() => {});
    const hwService = virtualHwMod.createVirtualHardwareService(processes.snapshot);
    const receivedSnaps = [];
    const unsub = hwService.subscribe((snap) => receivedSnaps.push(snap));
    assert.equal(intervals.size, 2, 'subscribe starts shared tick + lag probe timers');

    hwService.tick();
    assert.equal(receivedSnaps.length, 1);
    const sharedSnap = receivedSnaps[0];
    assert.equal(sharedSnap.cpu.history.length, 60);
    assert.equal(sharedSnap.memory.history.length, 60);
    assert.equal(sharedSnap.gpus[0].pts3d.length, 60);

    const fakeContainer = createMockEl('div');
    fakeContainer.closest = (sel) => (sel === '.w11-window' ? winEl : null);

    const openedApps = [];
    const terminatedPids = [];
    const windows = [
      { wid: 2, instanceId: 'inst-exhv-2', pid: 1002, appId: 'exhyperv', title: 'ExHyperV', iconKey: 'exhyperv', minimized: false },
    ];
    const procList = [
      { pid: 1002, appId: 'exhyperv', views: [2] },
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
      getMachineName: () => exhvConfigMod.EXHYPERV_HOST_CONFIG.machineName,
      getHostBootAppId: () => exhvConfigMod.EXHYPERV_HOST_CONFIG.bootAppId,
      getStartupHostItems: () => exhvConfigMod.EXHYPERV_HOST_CONFIG.startupItems,
      getInstalledApps: () => registryMod.getAppRegistry(),
      getAppManifest: (appId) => registryMod.getManifestById(appId),
      getAppUsageHistory: () => ({ since: Date.now(), apps: [] }),
      getAppData: () => null,
      setAppData: () => {},
      renderAppIcon: (iconKey) => `<svg data-icon="${iconKey}"></svg>`,
      getWindows: () => windows,
      getProcesses: () => procList,
      terminateProcess: (pid) => terminatedPids.push(pid),
      openApp: (id) => {
        openedApps.push(id);
        return 1003;
      },
      closeWindow: () => {},
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
      wid: 1,
      instanceId: 'inst-tm-1',
      appId: 'task-manager',
      manifest: registryMod.getManifestById('task-manager'),
      container: fakeContainer,
      resources: {
        pid: 1001,
        assertLive: () => {},
        manage: () => () => {},
        observeGuest: () => () => {},
      },
      shell,
    });

    const tmShellEl = createdEls[0];
    const contentEl = tmShellEl.querySelector('[data-tm-content]');
    const clickHandler = tmShellEl._listeners.get('click');
    const titleSearchWrap = createdEls.find((e) => e.className === 'w11-tm-title-search');
    const titleSearchInput = titleSearchWrap.querySelector('.w11-tm-title-search-input');

    // Switch to Performance tab and verify shared CPU % from VirtualHardwareSnapshot
    clickHandler({
      target: { closest: (sel) => (sel === '[data-tm-tab]' ? { dataset: { tmTab: 'performance' } } : null) },
    });
    assert.ok(contentEl.innerHTML.includes(`${sharedSnap.cpu.usagePct}%`));

    // Pushing an updated snapshot updates Task Manager view
    hwService.tick();
    const nextSnap = hwService.getSnapshot();
    lifecycle.onHardwareUpdate(nextSnap);
    assert.ok(contentEl.innerHTML.includes(`${nextSnap.cpu.usagePct}%`));

    // Switch back to Processes tab and verify manifest-driven process metadata
    clickHandler({
      target: { closest: (sel) => (sel === '[data-tm-tab]' ? { dataset: { tmTab: 'processes' } } : null) },
    });
    assert.ok(contentEl.innerHTML.includes('ExHyperV'));
    assert.ok(contentEl.innerHTML.includes('win-1002'));
    assert.ok(contentEl.innerHTML.includes('0.0 MB'));

    // Search by publisher from APP_REGISTRY.telemetry.publisher
    titleSearchInput.value = 'ExHyperV Project';
    titleSearchInput._listeners.get('input')?.();
    assert.ok(contentEl.innerHTML.includes('ExHyperV'));
    assert.ok(!contentEl.innerHTML.includes('Google Chrome'));

    // New task opens modal and launches via ctx.shell.openApp on submit
    clickHandler({
      target: { closest: (sel) => (sel === '[data-tm-cmd]' ? { dataset: { tmCmd: 'new-task' } } : null) },
    });
    const dialogEl = createdEls.find((e) => e.className === 'w11-tm-modal');
    assert.ok(dialogEl, 'new-task should open Run New Task dialog');
    dialogEl.querySelector('[data-run-input]').value = 'exhyperv';
    const primaryOkBtn = createdEls.find((e) => e.tagName === 'BUTTON' && e.className.includes('is-primary'));
    primaryOkBtn._listeners.get('click')?.();
    assert.deepEqual(openedApps, ['exhyperv']);

    // Select real process and End Task via ctx.shell.terminateProcess
    clickHandler({
      target: { closest: (sel) => (sel === '[data-tm-proc]' ? { dataset: { tmProc: 'win-1002' } } : null) },
    });
    clickHandler({
      target: { closest: (sel) => (sel === '[data-tm-cmd]' ? { dataset: { tmCmd: 'end-task' } } : null) },
    });
    assert.deepEqual(terminatedPids, [1002]);

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

test('ExHyperV WoW uses standalone local-machine mock hardware without connecting to WoW HAL', () => {
  const prevWindow = globalThis.window;
  const prevDocument = globalThis.document;
  try {
    const pageListeners = new Map();
    const fakePageHost = {
      hidden: false,
      dataset: {},
      innerHTML: '',
      contains: () => false,
      addEventListener: (type, fn) => pageListeners.set(type, fn),
      querySelectorAll: () => [],
      querySelector: () => null,
    };
    const fakeWinListeners = new Map();
    const fakeWindow = {
      __WIN11_HARDWARE__: {
        cpu: { displayName: { value: 'Browser Override CPU' }, logicalCores: 4 },
        gpus: [{ id: 'gpu-0', name: 'Browser WebGL Adapter', webglVersion: 'WebGL 2.0' }],
      },
      addEventListener(type, fn) {
        if (!fakeWinListeners.has(type)) fakeWinListeners.set(type, new Set());
        fakeWinListeners.get(type).add(fn);
      },
      removeEventListener(type, fn) {
        fakeWinListeners.get(type)?.delete(fn);
      },
    };
    globalThis.window = fakeWindow;
    globalThis.document = {
      activeElement: null,
      addEventListener: () => {},
      getElementById: (id) => (id === 'pageHost' ? fakePageHost : null),
    };

    assert.equal(uiHostMod.applyVirtualHardwareToHost, undefined);
    assert.equal(uiHostMod.buildVirtualGpuEntries, undefined);

    uiHostMod.initHostPage();
    assert.equal(fakeWinListeners.has('win11:hardware'), false);
    // The hardware details live on the performance tab.
    pageListeners.get('click')({ target: { closest: (selector) => selector === '[data-host-tab]' ? { getAttribute: () => 'state' } : null } });
    assert.ok(fakePageHost.innerHTML.includes('ARM64'));
    assert.ok(fakePageHost.innerHTML.includes('Snapdragon(R) X 12-core X1E80100 @ 3.40 GHz'));
    assert.ok(fakePageHost.innerHTML.includes('Qualcomm(R) Adreno(TM) X1-85 GPU'));
    assert.ok(fakePageHost.innerHTML.includes('WDC CH SN530 SDBPTPZ-1T00-1024'));
    assert.ok(!fakePageHost.innerHTML.includes('Browser Override CPU'));
    assert.ok(!fakePageHost.innerHTML.includes('Browser WebGL Adapter'));
    assert.ok(!fakePageHost.innerHTML.includes('浏览器受限'));
    assert.ok(!fakePageHost.innerHTML.includes('WebGL'));
    assert.ok(!fakePageHost.innerHTML.includes('RTX 4090'));
  } finally {
    globalThis.window = prevWindow;
    globalThis.document = prevDocument;
  }
});

test('createTmTooltipController preserves visible tooltip across preserveDuring DOM rebuilds without flickering', () => {
  const prevWindow = globalThis.window;
  const prevDocument = globalThis.document;
  let scheduledCb = null;

  function makeNode(tag, attrs = {}) {
    const listeners = new Map();
    const children = [];
    const classes = new Set();
    const node = {
      tagName: tag.toUpperCase(),
      className: '',
      classList: {
        add: (...tokens) => tokens.forEach((t) => classes.add(t)),
        remove: (...tokens) => tokens.forEach((t) => classes.delete(t)),
        contains: (token) => classes.has(token),
        toggle(token, force) {
          const next = force === undefined ? !classes.has(token) : Boolean(force);
          if (next) classes.add(token);
          else classes.delete(token);
          return next;
        },
      },
      hidden: false,
      innerHTML: '',
      style: {},
      dataset: { ...attrs },
      offsetWidth: 800,
      offsetHeight: 600,
      isConnected: true,
      parentElement: null,
      get childElementCount() {
        return this.innerHTML ? 1 : 0;
      },
      appendChild(child) {
        child.parentElement = node;
        child.isConnected = node.isConnected;
        children.push(child);
        return child;
      },
      remove() {
        node.isConnected = false;
      },
      contains(other) {
        if (other === node) return true;
        return children.includes(other);
      },
      closest(sel) {
        if (sel === '[data-tm-tip]' && node.dataset.tmTip) return node;
        return node.parentElement ? node.parentElement.closest(sel) : null;
      },
      getBoundingClientRect() {
        return { left: 0, top: 0, width: 800, height: 600 };
      },
      addEventListener(type, fn) {
        listeners.set(type, fn);
      },
      removeEventListener(type) {
        listeners.delete(type);
      },
      dispatch(type, evt) {
        listeners.get(type)?.(evt);
      },
      querySelectorAll() {
        return children.filter((c) => Boolean(c.dataset?.tmTip));
      },
      _children: children,
    };
    return node;
  }

  const hostEl = makeNode('div');
  let currentHitTarget = null;

  globalThis.window = {
    setTimeout(fn) {
      scheduledCb = fn;
      return 1;
    },
    clearTimeout() {
      scheduledCb = null;
    },
  };
  globalThis.document = {
    createElement(tag) {
      return makeNode(tag);
    },
    elementFromPoint() {
      return currentHitTarget;
    },
  };

  try {
    const tipCtrl = overlaysMod.createTooltipController(hostEl);
    const tipEl = hostEl._children[0];
    assert.equal(tipEl.hidden, true);

    const memBar1 = makeNode('div', { tmTip: '内存组合', tmTipKey: 'mem-used', tmTipBody: '使用中(4,024 MB)' });
    hostEl.appendChild(memBar1);
    currentHitTarget = memBar1;

    // Hover and trigger 320ms timer
    hostEl.dispatch('pointerover', { target: memBar1, clientX: 220, clientY: 180 });
    assert.ok(typeof scheduledCb === 'function');
    const fireTimer = scheduledCb;
    scheduledCb = null;
    fireTimer();
    assert.equal(tipEl.hidden, false);
    assert.ok(tipEl.innerHTML.includes('4,024 MB'));

    // Simulate periodic renderContent() replacing DOM node under cursor
    let memBar2;
    tipCtrl.preserveDuring(() => {
      memBar1.isConnected = false;
      hostEl._children.splice(hostEl._children.indexOf(memBar1), 1);
      memBar2 = makeNode('div', { tmTip: '内存组合', tmTipKey: 'mem-used', tmTipBody: '使用中(4,031 MB)' });
      hostEl.appendChild(memBar2);
      currentHitTarget = memBar2;
    });

    // Tooltip stays open without hiding and updates live text in place
    assert.equal(tipEl.hidden, false);
    assert.ok(tipEl.innerHTML.includes('4,031 MB'));

    // Synthetic pointerover from Chromium on the newly inserted node must NOT hide or reset tooltip
    hostEl.dispatch('pointerover', { target: memBar2, clientX: 220, clientY: 180 });
    assert.equal(tipEl.hidden, false);
    assert.equal(scheduledCb, null);

    tipCtrl.destroy();
  } finally {
    globalThis.window = prevWindow;
    globalThis.document = prevDocument;
  }
});

test('explorerApp mounts Win11 File Explorer shell for this-pc and explorer manifests', () => {
  const manifest = registryMod.getManifestById('this-pc');
  assert.equal(manifest?.runtime, 'builtin');
  assert.equal(manifest?.window.seamlessTitlebar, true);

  const container = {
    innerHTML: '',
    listeners: new Map(),
    addEventListener(type, fn) {
      this.listeners.set(type, fn);
    },
    removeEventListener(type) {
      this.listeners.delete(type);
    },
    querySelector() {
      return null;
    },
  };

  const storageSvc = virtualStorageMod.createVirtualStorageService({
    resources: {
      pid: 0,
      assertLive: () => {},
      manage: () => () => {},
      observeGuest: () => () => {},
    },
    getDesktopShortcuts: () => [],
  });

  const lifecycle = appsIndexMod.mountAppInstance({
    pid: 1099,
    wid: 99,
    instanceId: 'inst-1099',
    appId: 'this-pc',
    manifest,
    container,
    resources: {
      pid: 1099,
      assertLive: () => {},
      manage: () => () => {},
      observeGuest: () => () => {},
    },
    shell: {
      getTheme: () => 'dark',
      getVirtualStorage: () => storageSvc,
      getVirtualHardware: () => ({
        storage: { activePct: 0, rateHistory: [], readKb: 0, writeKb: 0, probe: null },
      }),
      getMachineName: () => exhvConfigMod.EXHYPERV_HOST_CONFIG.machineName,
      setWindowTitle: () => {},
      closeWindow: () => {},
    },
  });

  assert.ok(container.innerHTML.includes('w11-exp-shell'));
  assert.ok(container.innerHTML.includes('w11-exp-navbar'));
  assert.ok(container.innerHTML.includes('w11-exp-cmdbar'));
  assert.ok(container.innerHTML.includes('w11-exp-navpane'));

  // Navigate via sidebar to 此电脑 (this-pc) to verify Devices & Drives view
  container.listeners.get('click')?.({
    target: { closest: (sel) => (sel === '[data-exp-nav]' ? { dataset: { expNav: 'this-pc' } } : null) },
  });
  assert.ok(container.innerHTML.includes('设备和驱动器'));
  assert.ok(container.innerHTML.includes('本地磁盘 (C:)'));
  assert.ok(!container.innerHTML.includes('蝴蝶泉边'));
  assert.ok(!container.innerHTML.includes('练习题'));

  lifecycle.destroy?.();
});

test('win11-fs-skeleton provides rich multi-level Windows 11 C: folder tree', () => {
  const nodes = win11FsSkeletonMod.createWin11FsNodes();
  const cDrive = nodes['drive-c'];
  assert.ok(cDrive, 'drive-c node must exist');
  const cChildNames = new Set((cDrive.children ?? []).map((id) => nodes[id]?.name));
  for (const expected of ['Windows', 'Program Files', 'ProgramData', 'Users']) {
    assert.ok(cChildNames.has(expected), `C:\\ should include ${expected}`);
  }

  const allNames = new Set(Object.values(nodes).map((n) => n.name));
  for (const deepExpected of ['System32', 'WinSxS']) {
    assert.ok(allNames.has(deepExpected), `C: skeleton should include ${deepExpected}`);
  }

  // Verify parent/child linkage integrity for real hierarchy nodes (excluding virtual shortcut roots like home/gallery/libraries)
  const virtualRoots = new Set(['home', 'gallery', 'libraries']);
  for (const node of Object.values(nodes)) {
    if (virtualRoots.has(node.id)) continue;
    for (const childId of node.children ?? []) {
      assert.ok(nodes[childId], `Missing child node ${childId} under ${node.id}`);
      assert.equal(nodes[childId].parentId, node.id);
    }
  }
});

test('mergeWin11SkeletonText merges gzipped Explorer c-skeleton.txt.gz cleanly', async () => {
  const { gunzipSync } = await import('node:zlib');
  const gzBuf = await readFile(path.join(websiteRoot, 'public', 'win11', 'explorer', 'c-skeleton.txt.gz'));
  const rawText = gunzipSync(gzBuf).toString('utf8');

  const nodes = win11FsSkeletonMod.createWin11FsNodes();
  const added = win11FsSkeletonMod.mergeWin11SkeletonText(nodes, rawText);
  assert.ok(added > 250000, `Expected > 250000 merged dir+file entries, got ${added}`);
  assert.ok(nodes['u-admin'], 'Existing sanitized Users\\Administrator node must be preserved');
  assert.ok((nodes['c-windows']?.children?.length ?? 0) > 20, 'Windows folder should have full subdirs');

  // Lazily hydrate System32 files and verify realistic file count + icons
  const sys32Id = (nodes['c-windows']?.children ?? []).find((cid) => nodes[cid]?.name === 'System32');
  assert.ok(sys32Id, 'System32 node must exist under Windows');
  const hydrated = win11FsSkeletonMod.hydrateWin11FolderFiles(nodes, sys32Id);
  assert.ok(hydrated > 4000, `Expected > 4000 System32 files hydrated, got ${hydrated}`);
  assert.ok((nodes[sys32Id]?.children?.length ?? 0) > 4500, 'System32 should have 4500+ total items');
  assert.equal(win11FsSkeletonMod.hydrateWin11FolderFiles(nodes, sys32Id), 0);

  // Verify idempotency when merged a second time
  const addedSecond = win11FsSkeletonMod.mergeWin11SkeletonText(nodes, rawText);
  assert.equal(addedSecond, 0);
});

test('console-wire resolves host cursor CSS strings and safely guards oversize or hidden pointers', async () => {
  const wire = await viteServer.ssrLoadModule(path.join(exhypervWowRoot, 'scripts/console-wire.js'));
  assert.equal(wire.resolveRemoteCursorStyle(null), null);
  assert.equal(wire.resolveRemoteCursorStyle({ type: 'cursor', visible: false }), null);
  assert.equal(
    wire.resolveRemoteCursorStyle({ type: 'cursor', visible: true, width: 32, height: 32, hotX: 40, hotY: -3, png: 'AQI=' }),
    'url("data:image/png;base64,AQI=") 31 0, default',
  );
  assert.equal(
    await wire.resolveRemoteCursorStyle({ type: 'cursor', visible: true, width: 192, height: 192, hotX: 10, hotY: 10, png: 'AQI=' }),
    'default',
  );
  assert.equal(wire.hostDisplayScalePercent(1), 100);
  assert.equal(wire.hostDisplayScalePercent(1.5), 150);
  assert.equal(wire.hostDisplayScalePercent(2.25), 225);
  assert.equal(wire.hostDisplayScalePercent(4), 300);
  assert.equal(wire.resolveEnhancedDesktopScale(0, 1280, 2), 100);
  assert.equal(wire.resolveEnhancedDesktopScale(0, 1920, 2), 200);
  assert.equal(wire.resolveEnhancedDesktopScale(175, 1280, 2), 175);
  assert.equal(wire.resolveEnhancedDesktopScale(600, 1280, 1), 500);
});

test('console-wire decodes H.264 type-3 packets, swaps unaligned BGRA fast, and selects optimal encodings', async () => {
  const wire = await viteServer.ssrLoadModule(path.join(exhypervWowRoot, 'scripts/console-wire.js'));

  // 1. Packet type 3 parsing + SPS codec extraction
  const annexB = new Uint8Array([0x00, 0x00, 0x00, 0x01, 0x67, 0x4d, 0x40, 0x1f, 0x00, 0x00, 0x00, 0x01, 0x65, 0x88]);
  const h264Buf = new ArrayBuffer(34 + annexB.length);
  const h264Bytes = new Uint8Array(h264Buf);
  const h264View = new DataView(h264Buf);
  h264Bytes[0] = 0x45;
  h264Bytes[1] = 0x58;
  h264Bytes[2] = 3;
  h264Bytes[3] = 3; // keyFrame + hardware
  h264Bytes[4] = 0;
  h264View.setBigInt64(6, 77n, true);
  h264View.setBigInt64(14, 16667n, true);
  h264View.setUint16(22, 1920, true);
  h264View.setUint16(24, 1080, true);
  h264View.setUint32(26, 16667, true);
  h264Bytes.set(annexB, 34);

  const parsedH264 = wire.parseConsoleBinaryPacket(h264Buf);
  assert.equal(parsedH264?.kind, 'h264');
  assert.equal(parsedH264?.isKeyFrame, true);
  assert.equal(parsedH264?.isHardware, true);
  assert.equal(parsedH264?.sequence, 77);
  assert.equal(parsedH264?.timestampUs, 16667);
  assert.equal(parsedH264?.width, 1920);
  assert.equal(parsedH264?.height, 1080);
  assert.equal(wire.extractH264CodecFromAnnexB(parsedH264.payload), 'avc1.4d401f');
  assert.equal(wire.extractH264CodecFromAnnexB(new Uint8Array([0, 0, 1, 0x67, 0x42, 0xe0, 0x1e, 0])), 'avc1.42e01e');

  // 2. Fast 32-bit BGRA -> RGBA swap on unaligned offset-34 payload
  const bgraPacket = new Uint8Array(34 + 8);
  bgraPacket[0] = 0x45;
  bgraPacket[1] = 0x58;
  bgraPacket[2] = 1;
  bgraPacket[3] = 1;
  bgraPacket[4] = 0;
  const pv = new DataView(bgraPacket.buffer);
  pv.setBigInt64(6, 1n, true);
  pv.setBigInt64(14, 0n, true);
  pv.setUint16(22, 2, true);
  pv.setUint16(24, 1, true);
  pv.setUint16(30, 2, true);
  pv.setUint16(32, 1, true);
  bgraPacket.set([10, 20, 30, 0, 40, 50, 60, 128], 34); // BGRA -> RGBA with A=255
  const parsedBgra = wire.parseConsoleBinaryPacket(bgraPacket);
  assert.equal(parsedBgra.payload.byteOffset, 34);

  const rgbaOut = new Uint8ClampedArray(8);
  wire.swapBgraToRgba32(parsedBgra.payload, new Uint32Array(rgbaOut.buffer), 2, new Uint8Array(rgbaOut.buffer));
  assert.deepEqual(Array.from(rgbaOut), [30, 20, 10, 255, 60, 50, 40, 255]);

  // 3. Encoding auto-selection + backward compatibility with older server
  assert.equal(wire.isLoopbackHost('http://127.0.0.1:5191'), true);
  assert.equal(wire.isLoopbackHost('http://localhost:5191'), true);
  assert.equal(wire.isLoopbackHost('http://[::1]:5191'), true);
  assert.equal(wire.isLoopbackHost('https://192.168.1.50:5191'), false);

  assert.equal(wire.selectInitialConsoleEncoding({ serverUrl: 'http://127.0.0.1:5191', preference: 'auto', hasWebCodecs: true, hasDecompressionStream: true }), 'bgra');
  assert.equal(wire.selectInitialConsoleEncoding({ serverUrl: 'https://192.168.1.50:5191', preference: 'bgra', hasWebCodecs: true, hasDecompressionStream: true }), 'bgra');
  // Without known server capabilities yet (could be old ExHyperV.Server.exe), remote starts safely on deflate
  assert.equal(wire.selectInitialConsoleEncoding({ serverUrl: 'https://192.168.1.50:5191', preference: 'auto', hasWebCodecs: true, hasDecompressionStream: true }), 'deflate');
  assert.equal(wire.selectInitialConsoleEncoding({ serverUrl: 'https://192.168.1.50:5191', preference: 'auto', serverSupportedEncodings: ['bgra', 'deflate', 'png', 'h264'], hasWebCodecs: true, hasDecompressionStream: true }), 'h264');

  assert.equal(wire.selectNegotiatedConsoleEncoding({ serverUrl: 'https://192.168.1.50:5191', preference: 'auto', capabilities: { supportedEncodings: ['bgra', 'deflate', 'png', 'h264'] }, hasWebCodecs: true, hasDecompressionStream: true }), 'h264');
  assert.equal(wire.selectNegotiatedConsoleEncoding({ serverUrl: 'https://192.168.1.50:5191', preference: 'auto', capabilities: { supportedEncodings: ['bgra', 'deflate', 'png', 'h264'] }, hasWebCodecs: false, hasDecompressionStream: true }), 'deflate');
  assert.equal(wire.selectNegotiatedConsoleEncoding({ serverUrl: 'https://192.168.1.50:5191', preference: 'auto', capabilities: { supportedEncodings: ['bgra', 'deflate', 'png', 'h264'] }, hasWebCodecs: false, hasDecompressionStream: false }), 'png');
  assert.equal(wire.selectNegotiatedConsoleEncoding({ serverUrl: 'https://192.168.1.50:5191', preference: 'auto', capabilities: {}, hasWebCodecs: true, hasDecompressionStream: true }), 'deflate');

  // 4. DPR physical scaling
  assert.deepEqual(wire.computeScaledSurfaceCssDimensions(1920, 1080, 'physical', 2), { width: 960, height: 540, crisp: true });
  assert.deepEqual(wire.computeScaledSurfaceCssDimensions(1024, 768, '200%', 2), { width: 2048, height: 1536, crisp: true });
});

test('createConsoleRenderer coalesces BGRA frames non-blockingly and handles WebCodecs H.264 + fallback', async () => {
  const wire = await viteServer.ssrLoadModule(path.join(exhypervWowRoot, 'scripts/console-wire.js'));
  const prevImageData = globalThis.ImageData;
  const prevVideoDecoder = globalThis.VideoDecoder;
  const prevEncodedChunk = globalThis.EncodedVideoChunk;

  try {
    globalThis.ImageData = class MockImageData {
      constructor(a, b, c) {
        if (a instanceof Uint8ClampedArray) {
          this.data = a;
          this.width = b;
          this.height = c;
        } else {
          this.width = a;
          this.height = b;
          this.data = new Uint8ClampedArray(a * b * 4);
        }
      }
    };

    const scheduledCallbacks = [];
    const putCalls = [];
    const drawImageCalls = [];
    const fakeCtx = {
      putImageData(imgData, x, y) {
        putCalls.push({ x, y, width: imgData.width, height: imgData.height, firstRgba: Array.from(imgData.data.slice(0, 4)) });
      },
      drawImage(img, x, y, w, h) {
        drawImageCalls.push({ id: img.id, x, y, w, h });
      },
      clearRect() {}
    };
    const fakeCanvas = { width: 0, height: 0, getContext: () => fakeCtx };

    const decoderEvents = { configured: [], decoded: [], closedCount: 0 };
    let decoderCallbacks = null;
    globalThis.EncodedVideoChunk = class MockEncodedVideoChunk {
      constructor(init) {
        Object.assign(this, init);
      }
    };
    globalThis.VideoDecoder = class MockVideoDecoder {
      constructor(cb) {
        decoderCallbacks = cb;
        this.state = 'configured';
      }
      configure(cfg) {
        decoderEvents.configured.push(cfg);
      }
      decode(chunk) {
        decoderEvents.decoded.push(chunk);
      }
      close() {
        this.state = 'closed';
        decoderEvents.closedCount++;
      }
    };

    const codecErrors = [];
    const renderer = wire.createConsoleRenderer(fakeCanvas, {
      scheduleFrame: (fn) => {
        scheduledCallbacks.push(fn);
        return scheduledCallbacks.length;
      },
      cancelFrame: () => {},
      onCodecError: (codec, err) => codecErrors.push({ codec, err })
    });

    // Enqueue partial BGRA then superseded by fullFrame BGRA before rAF runs
    renderer.enqueueVideo({
      kind: 'video',
      encoding: 'bgra',
      fullFrame: false,
      width: 4,
      height: 4,
      rect: { x: 0, y: 0, width: 1, height: 1 },
      payload: new Uint8Array([1, 2, 3, 255])
    });
    renderer.enqueueVideo({
      kind: 'video',
      encoding: 'bgra',
      fullFrame: true,
      width: 4,
      height: 4,
      rect: { x: 0, y: 0, width: 4, height: 4 },
      payload: new Uint8Array(4 * 4 * 4).fill(9)
    });
    // Contained dirty rect followed by covering dirty rect
    renderer.enqueueVideo({
      kind: 'video',
      encoding: 'bgra',
      fullFrame: false,
      width: 4,
      height: 4,
      rect: { x: 1, y: 1, width: 1, height: 1 },
      payload: new Uint8Array([10, 20, 30, 255])
    });
    renderer.enqueueVideo({
      kind: 'video',
      encoding: 'bgra',
      fullFrame: false,
      width: 4,
      height: 4,
      rect: { x: 0, y: 0, width: 2, height: 2 },
      payload: new Uint8Array([11, 22, 33, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255])
    });

    assert.equal(putCalls.length, 0, 'enqueueVideo must defer painting to rAF');
    scheduledCallbacks.shift()();
    assert.equal(putCalls.length, 2, 'only latest fullFrame + covering dirty rect should be drawn');
    assert.deepEqual(putCalls[0], { x: 0, y: 0, width: 4, height: 4, firstRgba: [9, 9, 9, 255] });
    assert.deepEqual(putCalls[1], { x: 0, y: 0, width: 2, height: 2, firstRgba: [33, 22, 11, 255] });

    // A delta before the keyframe is skipped. The keyframe configures VideoDecoder and outputs frames.
    renderer.enqueueVideo({
      kind: 'h264',
      isKeyFrame: false,
      width: 128,
      height: 128,
      timestampUs: 1000,
      payload: new Uint8Array([0, 0, 0, 1, 0x41, 1])
    });
    assert.equal(decoderEvents.configured.length, 0);

    renderer.enqueueVideo({
      kind: 'h264',
      isKeyFrame: true,
      width: 128,
      height: 128,
      timestampUs: 2000,
      payload: new Uint8Array([0, 0, 0, 1, 0x67, 0x42, 0xe0, 0x1e, 0, 0, 0, 1, 0x65, 1])
    });
    assert.equal(decoderEvents.configured[0].codec, 'avc1.42e01e');
    assert.equal(decoderEvents.decoded.length, 1);

    // Emit two decoded VideoFrames before rAF + 1 idle lossless BGRA compensation frame after
    const closedFrames = [];
    decoderCallbacks.output({ id: 'f1', displayWidth: 128, displayHeight: 128, close: () => closedFrames.push('f1') });
    decoderCallbacks.output({ id: 'f2', displayWidth: 128, displayHeight: 128, close: () => closedFrames.push('f2') });
    assert.deepEqual(closedFrames, ['f1'], 'superseded H.264 VideoFrame must close immediately');

    renderer.enqueueVideo({
      kind: 'video',
      encoding: 'bgra',
      fullFrame: false,
      width: 128,
      height: 128,
      rect: { x: 8, y: 8, width: 1, height: 1 },
      payload: new Uint8Array([5, 6, 7, 255])
    });

    putCalls.length = 0;
    scheduledCallbacks.shift()();
    assert.deepEqual(drawImageCalls, [{ id: 'f2', x: 0, y: 0, w: 128, h: 128 }]);
    assert.deepEqual(closedFrames, ['f1', 'f2']);
    assert.equal(putCalls.length, 1);
    assert.deepEqual(putCalls[0].firstRgba, [7, 6, 5, 255]);

    // Codec error triggers fallback callback cleanly
    decoderCallbacks.error(new Error('decode failed'));
    assert.equal(codecErrors.length, 1);
    assert.equal(codecErrors[0].codec, 'h264');
    renderer.destroy();
  } finally {
    globalThis.ImageData = prevImageData;
    globalThis.VideoDecoder = prevVideoDecoder;
    globalThis.EncodedVideoChunk = prevEncodedChunk;
  }
});

test('console-wire encodes and decodes UTF-16LE clipboard text and Type 3 PCM audio input packets', async () => {
  const wire = await viteServer.ssrLoadModule(path.join(exhypervWowRoot, 'scripts/console-wire.js'));
  const msg = wire.encodeClipboardTextMessage('Hello\n世界!');
  assert.equal(msg.type, 'clipboard');
  assert.equal(msg.formats.length, 1);
  assert.equal(msg.formats[0].id, 13);
  assert.equal(msg.formats[0].name, null);
  const rawClip = Buffer.from(msg.formats[0].data, 'base64');
  assert.equal(rawClip.length % 2, 0);
  assert.equal(rawClip[rawClip.length - 1], 0);
  assert.equal(rawClip[rawClip.length - 2], 0);
  assert.equal(wire.decodeClipboardTextMessage(msg), 'Hello\n世界!');
  assert.equal(wire.decodeClipboardTextMessage({ formats: [{ id: 8, data: 'AA==' }] }), null);

  const s16Packet = wire.encodeConsoleAudioInputPacket(
    { sampleRate: 48000, channels: 2, sampleFormat: 'Signed16LittleEndian' },
    [new Float32Array(441).fill(0.5)],
    44100,
    123456n,
  );
  assert.ok(s16Packet instanceof Uint8Array);
  assert.equal(s16Packet[0], 0x45);
  assert.equal(s16Packet[1], 0x58);
  assert.equal(s16Packet[2], 3);
  assert.equal(s16Packet[3], 0);
  assert.equal(s16Packet[4], 1);
  assert.equal(s16Packet[5], 2);
  const s16View = new DataView(s16Packet.buffer, s16Packet.byteOffset, s16Packet.byteLength);
  assert.equal(s16View.getInt32(6, true), 48000);
  assert.equal(s16View.getBigInt64(10, true), 123456n);
  assert.equal((s16Packet.length - 18) % 4, 0);

  const f32Packet = wire.encodeConsoleAudioInputPacket(
    { sampleRate: 44100, channels: 1, sampleFormat: 'Float32LittleEndian' },
    [new Float32Array([0.25, -0.25]), new Float32Array([0.25, -0.25])],
    44100,
    0n,
  );
  assert.ok(f32Packet instanceof Uint8Array);
  assert.equal(f32Packet[4], 2);
  assert.equal(f32Packet[5], 1);
  assert.equal(f32Packet.length, 18 + 8);
});

test('console v2 reconstructs moves from the displayed base and rejects malformed or mandatory extensions', async () => {
  const wire = await viteServer.ssrLoadModule(path.join(exhypervWowRoot, 'scripts/console-wire.js'));
  const packet = (sequence, full, dirty, moves) => {
    const header = 40 + dirty.length * 8 + moves.length * 12;
    const bytes = new Uint8Array(header + dirty.reduce((n, r) => n + r.values.length * 4, 0));
    const v = new DataView(bytes.buffer);
    bytes.set([69, 88, 4, Number(full), 0, 2]);
    v.setUint32(6, bytes.length, true);
    v.setBigInt64(10, BigInt(sequence), true);
    v.setBigInt64(18, BigInt(sequence - 1), true);
    v.setUint16(26, 5, true); v.setUint16(28, 1, true);
    v.setUint16(30, dirty.length, true); v.setUint16(32, moves.length, true); v.setUint32(34, header, true);
    let meta = 40, data = header;
    function fields(numbers) { for (const value of numbers) { v.setUint16(meta, value, true); meta += 2; } }
    for (const r of dirty) {
      fields([r.x, 0, r.values.length, 1]);
      for (const b of r.values) { bytes.set([b, b, b, 255], data); data += 4; }
    }
    for (const [from, to, width] of moves) fields([from, 0, to, 0, width, 1]);
    return bytes;
  };
  const pixels = new Uint8Array(5);
  const context = {
    putImageData(image, x, y, sx = 0, sy = 0, width = image.width) {
      for (let i = sx; i < sx + width; i++) pixels[x + i] = image.data[i * 4];
    },
    getImageData() { return { width: 5, data: Uint8Array.from([...pixels].flatMap(v => [v, v, v, 255])) }; }
  };
  let refreshed = 0;
  const displayed = [];
  const renderer = wire.createConsoleRenderer({ getContext: () => context }, {
    onDisplayed: n => displayed.push(n), onNeedKeyFrame: () => refreshed++
  });
  const full = packet(1, true, [{ x: 0, values: [1, 2, 3, 4, 5] }], []);
  const delta = packet(2, false, [{ x: 0, values: [9] }, { x: 4, values: [8] }], [[2, 1, 2], [1, 3, 1]]);
  assert.equal(wire.parseConsoleBinaryPacket(full), null);
  renderer.enqueueVideo(wire.parseConsoleBinaryPacket(full, 2));
  renderer.enqueueVideo(wire.parseConsoleBinaryPacket(delta, 2));
  assert.deepEqual([...pixels], [9, 3, 4, 2, 8]);
  assert.deepEqual(displayed, [1, 2]);
  renderer.enqueueVideo(wire.parseConsoleBinaryPacket(packet(5, false, [], []), 2));
  assert.equal(refreshed, 1);
  for (const offset of [3, 4, 5, 6, 30, 32, 34, 38, 40, 56]) {
    const bad = delta.slice(); bad[offset] = 255;
    assert.equal(wire.parseConsoleBinaryPacket(bad, 2), null, 'field at ' + offset);
  }
  assert.equal(wire.parseConsoleBinaryPacket(delta.subarray(0, -1), 2), null);
});

test('console client negotiates scoped v2 and preserves requested rights when connecting to v1', async () => {
  const { requestConsoleConnection } = await viteServer.ssrLoadModule(path.join(exhypervWowRoot, 'scripts/server-adapter.js'));
  const issued = [];
  const client = {
    serverInfo: { authenticationMode: 'password' },
    issueWsTicket: async scope => { issued.push(scope); return { ticket: 'test', serverUrl: 'https://server.test' }; },
    consoleBackends: async () => ({ backends: [{ backendId: 'RdpBasic', rights: ['Watch', 'Control'] }] })
  };
  assert.equal((await requestConsoleConnection(client, 'vm', 'basic')).protocol, 1);
  assert.deepEqual(issued, [{}]);
  await assert.rejects(requestConsoleConnection(client, 'vm', 'basic', true));
  await assert.rejects(requestConsoleConnection(client, 'vm', 'external'));
  assert.equal(issued.length, 1);
  client.serverInfo.consoleProtocolVersions = [1, 2];
  assert.equal((await requestConsoleConnection(client, 'vm', 'basic', true)).protocol, 2);
  assert.deepEqual(issued[1], { vmId: 'vm', backendId: 'RdpBasic', channel: 'console', requestedRights: ['Watch'] });
  assert.deepEqual((await requestConsoleConnection(client, 'vm', 'basic', false, ['Watch'])).rights, ['Watch']);
  client.issueWsTicket = async () => { throw Error('denied'); };
  await assert.rejects(requestConsoleConnection(client, 'vm', 'basic'), /denied/);
});
