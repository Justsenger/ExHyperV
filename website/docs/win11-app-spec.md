# Windows 11 Web 桌面应用接入规范

`website/src/win11-shell/` 模拟完整的 Windows 11 Web 桌面外壳。所有桌面元素（如 `ExHyperV`、任务管理器、设置、全屏命令以及后续移植的小游戏和实用工具）均作为**已安装应用**通过统一契约接入桌面环境，避免在桌面外壳核心代码中硬编码单个应用逻辑。

## 目录结构

```text
website/
├─ public/
│  ├─ _headers                       # 静态资源响应头（/apps/* 允许同源 iframe 嵌入）
│  └─ win11/icons/                   # 应用图标 PNG 资源
└─ src/
   ├─ pages/apps/                    # 同源独立 Astro 应用页（如 exhyperv.astro）
   └─ win11-shell/
      ├─ types.ts                    # Manifest、Window、HostContext、Lifecycle 类型定义
      ├─ registry.ts                 # 应用注册表 APP_REGISTRY（仅声明式元数据）
      ├─ icons.ts                    # 图标映射
      ├─ store.ts                    # 窗口实例与桌面偏好状态仓库
      ├─ app-protocol.ts             # win11-app/v1 postMessage 协议与客户端 SDK
      ├─ desktop.ts                  # 桌面外壳宿主（窗口管理、广播分发、任务栏/开始菜单）
      ├─ virtual-hardware.ts         # 共享虚拟硬件层（真实探测 + hw-profiler 推理 + 实时遥测）
      ├─ task-manager.ts             # 内置应用：任务管理器（实现 WinAppModule）
      └─ apps/
         ├─ index.ts                 # 应用运行时分发器
         ├─ iframe-host.ts           # iframe 应用适配器
         ├─ settings-app.ts          # 内置应用：设置（实现 WinAppModule）
         ├─ placeholder-app.ts       # 内置系统占位应用
         └─ commands.ts              # 无窗口命令型应用（如 fullscreen）
```

---

## 1. 三种应用运行时 (`runtime`)

在 [registry.ts](../src/win11-shell/registry.ts) 的 `APP_REGISTRY` 中声明 `WinAppManifest`，按运行时分为三类：

| 运行时 | 适用场景 | 示例 | 隔离与生命周期 |
| :--- | :--- | :--- | :--- |
| **`builtin`** | 需要直接读写桌面外壳状态、硬件探测或高频访问宿主能力的系统工具 | `task-manager`、`settings` | 挂载到窗口 `.w11-win-body`，通过 `WinAppHostContext` 与外壳交互，关闭窗口时调用 `destroy()` |
| **`iframe`** | 独立 Web 应用、不同框架页面、Emscripten/Wasm 游戏（如三维弹球）、外部站点 | `exhyperv` | 渲染为隔离 `<iframe class="w11-win-iframe">`，通过 `win11-app/v1` 协议通信，关闭窗口立即释放内存与音频 |
| **`command`** | 点击即执行动作、不产生窗口的系统项 | `fullscreen` | 执行 `APP_COMMANDS[id]`，不进窗口列表，支持 `getDynamicLabel` 动态切换显示名称 |

---

## 2. 应用清单规范 (`WinAppManifest`)

完整类型见 [types.ts](../src/win11-shell/types.ts)：

```ts
{
  id: 'pinball',
  name: '三维弹球',
  iconKey: 'pinball',
  description: '经典太空军校生弹球游戏。',
  category: 'game',                 // 'system' | 'app' | 'utility' | 'game'
  runtime: 'iframe',                // 'builtin' | 'iframe' | 'command'
  onDesktop: true,
  pinnedToStart: true,
  pinnedToTaskbar: false,
  singleton: true,                  // 默认 true（单实例聚焦）；false 允许打开多窗口
  window: {
    defaultWidth: 660,
    defaultHeight: 540,
    minWidth: 480,
    minHeight: 400,
    resizable: true,
    maximizable: true,
    seamlessTitlebar: false,
    background: '#000000',          // 加载期底色，避免白屏闪烁
  },
  iframe: {
    entryUrl: '/apps/pinball/',
    allow: 'autoplay; fullscreen; gamepad',
    passShellQuery: true,           // 同源路径自动附带 ?theme=&volume=&appId=&pid=
  },
  capabilities: [
    'theme',
    'volume',
    'focus',
    'visibility',
    'window-control',
  ],
  telemetry: {
    publisher: 'Open-Cadet / MIT',
    estimatedMemMb: 96,             // 任务管理器进程列表估算内存基线
  },
}
```

### 权限能力 (`capabilities`)

桌面宿主按清单声明的能力校验应用主动发起的请求：

* `theme`：接收系统深色/浅色主题同步
* `volume`：接收任务栏快速设置中的系统音量（`0–100`）与静音状态
* `focus`：接收窗口焦点获得/失去事件（便于游戏自动恢复或失焦暂停）
* `visibility`：接收窗口最小化/还原事件（便于挂起 `requestAnimationFrame` 或音频）
* `hardware-probe`：读取/触发桌面设备硬件探测报告（`DeviceProbeReport`）
* `window-control`：允许 `iframe` 应用主动请求最小化、最大化/还原、关闭自身窗口
* `shell-control`：允许应用通过 `openApp` 唤起桌面内其他已安装应用
* `app-messaging`：允许在桌面插件/宿主面板与应用之间收发自定义通道消息

---

## 3. `builtin` 应用接口 (`WinAppModule`)

内置应用导出符合 `WinAppModule` 的对象并在 [apps/index.ts](../src/win11-shell/apps/index.ts) 的 `BUILTIN_APP_MODULES` 中注册：

```ts
export const myApp: WinAppModule = {
  mount(ctx: WinAppHostContext): WinAppLifecycle {
    const { container, pid, manifest, shell, launchArgs } = ctx;
    // 1. 仅在 mount 时初始化 DOM 与事件，不在外部 renderWindows 期间重刷
    // 2. 通过 shell.getTheme() / shell.getVolume() / shell.getWindows() 读取状态
    return {
      onThemeChange(theme) {},
      onVolumeChange(volume) {},
      onFocusChange(focused) {},
      onVisibilityChange(minimized) {},
      onWindowsChanged() {},
      onShellPrefsChange() {},
      onHardwareReport(report) {},
      onMessage(channel, payload) {},
      destroy() {
        // 清理定时器、全局监听器等
      },
    };
  },
};
```

---

## 4. `iframe` 应用通信协议 (`win11-app/v1`)

实现位于 [app-protocol.ts](../src/win11-shell/app-protocol.ts)，宿主适配器位于 [apps/iframe-host.ts](../src/win11-shell/apps/iframe-host.ts)。

### 启动查询参数

当 `iframe.passShellQuery` 为 `true`（默认）且 `entryUrl` 以 `/` 开头时，桌面壳自动附加初始参数：

```text
/apps/exhyperv/?theme=dark&volume=65&appId=exhyperv&pid=1002
```

### 使用 TypeScript / Astro SDK

同源 Astro 页面（参见 [exhyperv.astro](../src/pages/apps/exhyperv.astro)）可直接导入 `createWin11AppClient`：

```ts
import { createWin11AppClient } from '../../win11-shell/app-protocol';

const app = createWin11AppClient({
  onInit(init) {
    // init: { pid, appId, theme, volume, muted, focused, minimized, launchArgs, hardware? }
  },
  onThemeChange(theme) { /* 'dark' | 'light' */ },
  onVolumeChange(volume, muted) { /* volume: 0..100 */ },
  onFocusChange(focused) {},
  onVisibilityChange(minimized) {},
  onHardwareUpdate(hardware) { /* VirtualHardwareSnapshot，需要 'hardware-probe' 能力 */ },
  onAppMessage(channel, payload) {},
});

// 常见上行调用
app.setTitle('三维弹球 - 125,000');
app.focusSelf();
app.windowControl('close'); // 需要 'window-control' 能力
app.requestHardware(true); // 需要 'hardware-probe' 能力，true 表示强制重新探测
app.emitEvent('score-updated', { score: 125000 }); // 需要 'app-messaging' 能力
```

### 纯静态 HTML / Wasm 页面原生接入（无需打包器）

若直接将静态编译产物（如 Emscripten Wasm 游戏）放入 `website/public/apps/<id>/index.html`，只需通过标准 `window.postMessage` 发送与监听带 `protocol: 'win11-app/v1'` 的 JSON 消息即可：

* **应用 -> 外壳（`window.parent.postMessage(msg, window.location.origin)`）**
  * 握手就绪：`{ protocol: 'win11-app/v1', type: 'w11:ready' }`（外壳收到后立即下发 `w11:init` 并冲刷排队消息）
  * 更新窗口标题：`{ protocol: 'win11-app/v1', type: 'w11:set-title', title: '...' }`
  * 点击内部时置顶窗口：`{ protocol: 'win11-app/v1', type: 'w11:focus-self' }`
  * 窗口控制：`{ protocol: 'win11-app/v1', type: 'w11:window-control', action: 'minimize' | 'maximize' | 'restore' | 'close' }`
  * 启动其他应用：`{ protocol: 'win11-app/v1', type: 'w11:open-app', appId: 'settings', args: {} }`
  * 请求虚拟硬件：`{ protocol: 'win11-app/v1', type: 'w11:request-hardware', force?: boolean }`（需 `'hardware-probe'` 能力）
  * 上报自定义事件：`{ protocol: 'win11-app/v1', type: 'w11:app-event', channel: '...', payload: ... }`

* **外壳 -> 应用（校验 `event.source === window.parent && event.origin === window.location.origin`）**
  * 初始化：`{ protocol: 'win11-app/v1', type: 'w11:init', pid, appId, theme, volume, muted, focused, minimized, launchArgs, hardware? }`
  * 主题变更：`{ protocol: 'win11-app/v1', type: 'w11:theme', theme }`
  * 音量变更：`{ protocol: 'win11-app/v1', type: 'w11:volume', volume, muted }`
  * 焦点变更：`{ protocol: 'win11-app/v1', type: 'w11:focus', focused }`
  * 最小化状态变更：`{ protocol: 'win11-app/v1', type: 'w11:visibility', minimized }`
  * 虚拟硬件推送：`{ protocol: 'win11-app/v1', type: 'w11:hardware', hardware }`（需 `'hardware-probe'` 能力）
  * 下行自定义消息：`{ protocol: 'win11-app/v1', type: 'w11:app-message', channel, payload }`

---

## 5. 共享虚拟硬件层 (`VirtualHardwareSnapshot`)

实现位于 [virtual-hardware.ts](../src/win11-shell/virtual-hardware.ts)，统一聚合了浏览器硬件探测（[device-probe.ts](../src/win11-shell/device-probe.ts)）、`hw-profiler` 指纹库推理以及每秒一次的 60 点实时遥测序列。

* **统一数据源**：只要存在任一已打开且具备 `hardware-probe` 能力的应用（如 `task-manager`、`exhyperv`），桌面宿主自动开启共享采样并将同一份 `VirtualHardwareSnapshot` 同步分发给所有授权应用；全部关闭后自动停止采样定时器。
* **内置应用对接**：通过 `ctx.shell.getVirtualHardware()`、`ctx.shell.refreshVirtualHardware()`、`ctx.shell.refreshVirtualStorage()` 和生命周期钩子 `onHardwareUpdate(snapshot)` 接收更新（参见 [task-manager.ts](../src/win11-shell/task-manager.ts)）。
* **WoW (`iframe`) 应用对接**：在清单声明 `'hardware-probe'` 能力后，在 `w11:init.hardware` 与后续每秒 `w11:hardware` 推送中自动获得同一份快照（参见 [exhyperv.astro](../src/pages/apps/exhyperv.astro)、[ui-design-host.js](../src/scripts/ui-design-host.js) 与 [ui-design-app.js](../src/scripts/ui-design-app.js)）。
* **可信度标识**：CPU、系统架构、内存、GPU、NPU、网络适配器的关键字段附带 `provenance`（`'real' | 'inferred' | 'simulated'`），便于应用区分浏览器实测值、指纹库推理值与沙箱隔离占位值。

---

## 6. 批量移植新应用的标准清单

1. **放置应用入口**
   * Astro 页面放在 `website/src/pages/apps/<id>.astro`（访问路径 `/apps/<id>/`）；
   * 或纯静态/Wasm 资产放在 `website/public/apps/<id>/index.html`（确保入口路由位于 `/apps/*` 下，以匹配 [\_headers](../public/_headers) 的 `frame-ancestors 'self'` 策略）。
2. **添加应用图标**
   * 将图标放到 `website/public/win11/icons/<iconKey>.png`，并在 [icons.ts](../src/win11-shell/icons.ts) 的 `PNG_ICON_MAP` 中登记。
3. **注册 Manifest**
   * 在 [registry.ts](../src/win11-shell/registry.ts) 的 `APP_REGISTRY` 添加完整配置（指定 `runtime`、`window`、`iframe`、`capabilities` 与 `telemetry`）。
4. **对接生命周期（推荐）**
   * 对媒体/游戏类应用，务必处理 `w11:volume`（对接 WebAudio/SDL 增益）、`w11:visibility`（最小化时暂停主循环/静音）及 `pointerdown` -> `w11:focus-self`。
5. **合规声明**
   * 若应用包含第三方开源引擎（如 MIT）或开源资产（如 CC0），在 `website/public/licenses/` 及 [THIRD_PARTY_NOTICES.txt](../public/licenses/THIRD_PARTY_NOTICES.txt) 中补充对应许可证和来源记录；避免在仓库中直接分发受专有版权保护的二进制资源。
