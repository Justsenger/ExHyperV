import {
  computeDynamicScaleMax,
  formatNetRateKbps,
  formatThroughputKb,
  renderMemoryCompositionBar,
  renderPerfChartSvg,
  TM_SERIES_COLORS,
} from './tm-charts';
import {
  createInitialAppHistory,
  createInitialProcesses,
  createInitialServices,
  createInitialStartupApps,
  formatMemMb,
  getHeatClass,
  syncWindowsToProcesses,
  TM_GROUP_META,
  TM_PROC_COLUMNS,
  type TmAppHistoryRow,
  type TmPriority,
  type TmProcColumnKey,
  type TmProcessNode,
  type TmProcGroup,
  type TmResourceDisplayMode,
  type TmServiceRow,
  type TmStartupRow,
  type TmTabId,
} from './tm-data';
import { renderTmGlyph } from './tm-fluent-icons';
import { createTmMenuController, createTmTooltipController, showTmModal, type TmMenuItem } from './tm-overlays';
import {
  flattenDetailProcesses,
  matchesSearch,
  renderDetailsPage,
  renderHistoryPage,
  renderServicesPage,
  renderSettingsPage,
  renderStartupPage,
  renderUsersPage,
  type TmSettingsState,
  type TmThemePref,
  type TmUpdateSpeed,
} from './tm-pages';
import type { WinAppHostContext, WinAppLifecycle } from './types';
import type { VirtualHardwareGpuInfo, VirtualHardwareSnapshot } from './virtual-hardware';

const TM_PREFS_KEY = 'win11-web-shell:tm-prefs:v2';

type PerfResourceKey = 'cpu' | 'memory' | 'disk' | 'network' | 'npu' | `gpu-${number}`;

interface PersistedTmPrefs {
  settings: TmSettingsState;
  visibleCols: TmProcColumnKey[];
  groupByType: boolean;
  hideGraphs: boolean;
  cpuGraphMode: 'overall' | 'logical' | 'numa';
  showKernelTimes: boolean;
  memDisplayMode: TmResourceDisplayMode;
  diskDisplayMode: TmResourceDisplayMode;
  netDisplayMode: TmResourceDisplayMode;
}

const DEFAULT_SETTINGS: TmSettingsState = {
  themePref: 'system',
  defaultTab: 'processes',
  updateSpeed: 'normal',
  winMgmtExpanded: true,
  otherOptsExpanded: true,
  alwaysOnTop: false,
  minimizeOnUse: true,
  hideWhenMinimized: false,
  showFullAccountName: false,
  showAllHistory: true,
  confirmEfficiencyMode: true,
};

function esc(v: string): string {
  return v.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function formatUptime(sec: number): string {
  const s = Math.max(0, Math.floor(sec));
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  const r = s % 60;
  return `${d}:${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}`;
}

function formatGiB(bytes: number): string {
  const gib = bytes / (1024 * 1024 * 1024);
  return gib >= 100 ? `${Math.round(gib)} GB` : `${gib.toFixed(1)} GB`;
}

function getGpuKindLabel(gpu: VirtualHardwareGpuInfo | undefined): string {
  if (!gpu) return '未知';
  if (gpu.vendor === 'NVIDIA' || gpu.environment === 'discrete_primary') return '独立 (Discrete)';
  if (gpu.vendor === 'Apple') return '统一架构 (Unified)';
  if (gpu.isSoftwareRenderer) return '软件渲染器';
  return '集成 (Integrated)';
}

function loadTmPrefs(): Partial<PersistedTmPrefs> {
  try {
    const raw = localStorage.getItem(TM_PREFS_KEY);
    return raw ? (JSON.parse(raw) as Partial<PersistedTmPrefs>) : {};
  } catch {
    return {};
  }
}

export function mountTaskManager(ctx: WinAppHostContext): WinAppLifecycle {
  const { container, shell, pid } = ctx;
  const saved = loadTmPrefs();

  const settings: TmSettingsState = { ...DEFAULT_SETTINGS, ...(saved.settings ?? {}) };
  let activeTab: TmTabId = settings.defaultTab ?? 'processes';
  let navExpanded = false;
  let searchQuery = '';

  // Processes state
  const defaultCols = TM_PROC_COLUMNS.filter((c) => c.defaultVisible).map((c) => c.key);
  let visibleCols = new Set<TmProcColumnKey>(
    Array.isArray(saved.visibleCols) && saved.visibleCols.length > 0 ? saved.visibleCols : defaultCols,
  );
  let groupByType = typeof saved.groupByType === 'boolean' ? saved.groupByType : true;
  let memDisplayMode: TmResourceDisplayMode = saved.memDisplayMode ?? 'value';
  let diskDisplayMode: TmResourceDisplayMode = saved.diskDisplayMode ?? 'value';
  let netDisplayMode: TmResourceDisplayMode = saved.netDisplayMode ?? 'value';
  let procSortCol: 'name' | TmProcColumnKey = 'name';
  let procSortDir: 'asc' | 'desc' = 'asc';
  const expandedProcIds = new Set<string>(['tm-self']);
  let selectedProcId: string | null = 'tm-self';

  // Performance state
  let activePerfKey: PerfResourceKey = 'cpu';
  let sidebarWidth = 246;
  let hideGraphs = typeof saved.hideGraphs === 'boolean' ? saved.hideGraphs : false;
  let cpuGraphMode: 'overall' | 'logical' | 'numa' = saved.cpuGraphMode ?? 'logical';
  let showKernelTimes = typeof saved.showKernelTimes === 'boolean' ? saved.showKernelTimes : false;
  let summaryMode: 'none' | 'perf-cards' | 'graph-only' = 'none';

  // Other tabs state
  let processes: TmProcessNode[] = createInitialProcesses();
  let appHistory: TmAppHistoryRow[] = createInitialAppHistory();
  let historySince = new Date(Date.now() - 86400000 * 3).toLocaleDateString('zh-CN');
  let selectedHistoryId: string | null = null;
  let startupApps: TmStartupRow[] = createInitialStartupApps();
  const expandedStartupIds = new Set<string>();
  let selectedStartupId: string | null = null;
  let usersExpanded = true;
  let selectedUserId: string | null = 'user-admin';
  let selectedDetailPid: number | null = 4820;
  let detailSortKey: 'name' | 'pid' | 'status' | 'user' | 'cpu' | 'mem' | 'arch' | 'desc' = 'name';
  let detailSortDir: 'asc' | 'desc' = 'asc';
  let services: TmServiceRow[] = createInitialServices();
  let selectedServiceName: string | null = null;
  let serviceSortKey: 'name' | 'pid' | 'desc' | 'status' | 'group' = 'name';
  let serviceSortDir: 'asc' | 'desc' = 'asc';

  let hw: VirtualHardwareSnapshot = shell.getVirtualHardware();
  let uptimeSec = Math.max(9124, Math.floor((typeof performance !== 'undefined' ? performance.now() : 0) / 1000) + 12340);
  let lastTickAt = Date.now();
  let diskWritePts: number[] = hw.storage.rateHistory.map((v, i) => Math.max(4, Math.round(v * 0.48 + ((i * 7) % 15))));
  let netSendPts: number[] = hw.network.rxHistory.map((v, i) => Math.max(2, Math.round(v * 0.42 + ((i * 5) % 9))));
  const hasRealDom = typeof document !== 'undefined' && typeof document.createElement === 'function';

  function persistPrefs() {
    try {
      const payload: PersistedTmPrefs = {
        settings,
        visibleCols: [...visibleCols],
        groupByType,
        hideGraphs,
        cpuGraphMode,
        showKernelTimes,
        memDisplayMode,
        diskDisplayMode,
        netDisplayMode,
      };
      localStorage.setItem(TM_PREFS_KEY, JSON.stringify(payload));
    } catch {}
  }

  function renderNavMarkup(): string {
    const activeCls = (t: TmTabId) => (activeTab === t ? ' is-active' : '');
    return `
      <nav class="w11-tm-nav" aria-label="任务管理器导航">
        <div class="w11-tm-nav-top">
          <button type="button" class="w11-tm-nav-btn" data-tm-nav-toggle data-tm-tip="${navExpanded ? '关闭导航' : '打开导航'}">
            <span class="w11-tm-nav-icon">${renderTmGlyph('menu')}</span>
            <span class="w11-tm-nav-text">导航</span>
          </button>
          <button type="button" class="w11-tm-nav-btn${activeCls('processes')}" data-tm-tab="processes" data-tm-tip="进程">
            <span class="w11-tm-nav-icon">${renderTmGlyph('processes')}</span>
            <span class="w11-tm-nav-text">进程</span>
          </button>
          <button type="button" class="w11-tm-nav-btn${activeCls('performance')}" data-tm-tab="performance" data-tm-tip="性能">
            <span class="w11-tm-nav-icon">${renderTmGlyph('performance')}</span>
            <span class="w11-tm-nav-text">性能</span>
          </button>
          <button type="button" class="w11-tm-nav-btn${activeCls('history')}" data-tm-tab="history" data-tm-tip="应用历史记录">
            <span class="w11-tm-nav-icon">${renderTmGlyph('history')}</span>
            <span class="w11-tm-nav-text">应用历史记录</span>
          </button>
          <button type="button" class="w11-tm-nav-btn${activeCls('startup')}" data-tm-tab="startup" data-tm-tip="启动应用">
            <span class="w11-tm-nav-icon">${renderTmGlyph('startup')}</span>
            <span class="w11-tm-nav-text">启动应用</span>
          </button>
          <button type="button" class="w11-tm-nav-btn${activeCls('users')}" data-tm-tab="users" data-tm-tip="用户">
            <span class="w11-tm-nav-icon">${renderTmGlyph('users')}</span>
            <span class="w11-tm-nav-text">用户</span>
          </button>
          <button type="button" class="w11-tm-nav-btn${activeCls('details')}" data-tm-tab="details" data-tm-tip="详细信息">
            <span class="w11-tm-nav-icon">${renderTmGlyph('details')}</span>
            <span class="w11-tm-nav-text">详细信息</span>
          </button>
          <button type="button" class="w11-tm-nav-btn${activeCls('services')}" data-tm-tab="services" data-tm-tip="服务">
            <span class="w11-tm-nav-icon">${renderTmGlyph('services')}</span>
            <span class="w11-tm-nav-text">服务</span>
          </button>
        </div>
        <div class="w11-tm-nav-bottom">
          <button type="button" class="w11-tm-nav-btn${activeCls('settings')}" data-tm-tab="settings" data-tm-tip="设置">
            <span class="w11-tm-nav-icon">${renderTmGlyph('settings')}</span>
            <span class="w11-tm-nav-text">设置</span>
          </button>
        </div>
      </nav>
    `;
  }

  let tmShell: HTMLElement | null = null;
  let topbarEl: HTMLElement | null = null;
  let contentEl: HTMLElement | null = null;
  let navToggleBtn: HTMLElement | null = null;
  let menuCtrl: ReturnType<typeof createTmMenuController> | null = null;
  let tipCtrl: ReturnType<typeof createTmTooltipController> | null = null;
  const hostWindowEl = typeof container.closest === 'function' ? container.closest<HTMLElement>('.w11-window') : null;
  const hostTitlebarEl = hostWindowEl?.querySelector<HTMLElement>('.w11-win-titlebar') ?? null;
  let titleSearchWrap: HTMLElement | null = null;
  let titleSearchInput: HTMLInputElement | null = null;
  let titleSearchClear: HTMLButtonElement | null = null;

  if (hasRealDom) {
    container.innerHTML = '';
    tmShell = document.createElement('div');
    tmShell.className = 'w11-tm-shell';
    tmShell.innerHTML = `
      ${renderNavMarkup()}
      <div class="w11-tm-workspace">
        <header class="w11-tm-topbar" data-tm-topbar></header>
        <div class="w11-tm-content" data-tm-content></div>
      </div>
    `;
    container.appendChild(tmShell);

    topbarEl = tmShell.querySelector<HTMLElement>('[data-tm-topbar]')!;
    contentEl = tmShell.querySelector<HTMLElement>('[data-tm-content]')!;
    navToggleBtn = tmShell.querySelector<HTMLElement>('[data-tm-nav-toggle]')!;
    menuCtrl = createTmMenuController(tmShell);
    tipCtrl = createTmTooltipController(tmShell);

    if (hostTitlebarEl) {
      titleSearchWrap = document.createElement('div');
      titleSearchWrap.className = 'w11-tm-title-search';
      titleSearchWrap.setAttribute('data-no-drag', 'true');
      titleSearchWrap.innerHTML = `
        <span class="w11-tm-title-search-ico" aria-hidden="true">
          <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
            <circle cx="7" cy="7" r="4.5" stroke="currentColor" stroke-width="1.35"/>
            <path d="M10.4 10.4L13.5 13.5" stroke="currentColor" stroke-width="1.35" stroke-linecap="round"/>
          </svg>
        </span>
        <input type="text" class="w11-tm-title-search-input" data-tm-search placeholder="键入要搜索的名称、发布者或 PID" aria-label="键入要搜索的名称、发布者或 PID" autocomplete="off" />
        <button type="button" class="w11-tm-title-search-clear" hidden aria-label="清除搜索">✕</button>
      `;
      const controlsEl = hostTitlebarEl.querySelector('.w11-win-controls');
      hostTitlebarEl.insertBefore(titleSearchWrap, controlsEl);
      titleSearchInput = titleSearchWrap.querySelector<HTMLInputElement>('.w11-tm-title-search-input');
      titleSearchClear = titleSearchWrap.querySelector<HTMLButtonElement>('.w11-tm-title-search-clear');

      titleSearchInput?.addEventListener('input', () => {
        searchQuery = titleSearchInput?.value ?? '';
        if (titleSearchClear) titleSearchClear.hidden = searchQuery.length === 0;
        renderContent();
      });
      titleSearchClear?.addEventListener('click', () => {
        searchQuery = '';
        if (titleSearchInput) {
          titleSearchInput.value = '';
          titleSearchInput.focus();
        }
        if (titleSearchClear) titleSearchClear.hidden = true;
        renderContent();
      });
    }
  }

  function applyTheme() {
    const resolved = settings.themePref === 'system' ? shell.getTheme() : settings.themePref;
    if (tmShell) tmShell.dataset.tmTheme = resolved;
    if (hostWindowEl) hostWindowEl.dataset.tmTheme = resolved;
  }

  function syncProcessesWithLive() {
    processes = syncWindowsToProcesses(processes, shell.getWindows(), hw);
  }

  function findProcessById(id: string | null): TmProcessNode | null {
    if (!id) return null;
    for (const p of processes) {
      if (p.id === id) return p;
      const child = p.children?.find((c) => c.id === id);
      if (child) return child;
    }
    return null;
  }

  function findProcessByPid(targetPid: number | null): TmProcessNode | null {
    if (targetPid === null) return null;
    return flattenDetailProcesses(processes).find((p) => p.pid === targetPid) ?? null;
  }

  function removeProcessByPid(targetPid: number) {
    processes = processes
      .filter((p) => p.pid !== targetPid)
      .map((p) =>
        p.children ? { ...p, children: p.children.filter((c) => c.pid !== targetPid) } : p,
      );
    if (selectedDetailPid === targetPid) selectedDetailPid = null;
    const sel = findProcessById(selectedProcId);
    if (sel?.pid === targetPid) selectedProcId = null;
  }

  function toggleEfficiencyForProcess(node: TmProcessNode) {
    const enable = node.status !== 'efficiency';
    const applyFn = () => {
      const target = findProcessById(node.id) ?? findProcessByPid(node.pid);
      if (!target) return;
      target.status = enable ? 'efficiency' : '';
      target.priority = enable ? '低' : '正常';
      renderTopbar();
      renderContent();
    };
    if (enable && settings.confirmEfficiencyMode) {
      showTmModal(tmShell, {
        title: '是否要启用效率模式?',
        width: 430,
        bodyHtml: `
          <p class="w11-tm-modal-text">效率模式将降低进程优先级并提高电源效率，但可能会导致某些进程不稳定。</p>
          <p class="w11-tm-modal-sub">进程: <strong>${esc(node.name)}</strong> (PID ${node.pid})</p>
          <label class="w11-tm-set-check"><input type="checkbox" data-eff-noask /><span>不再询问我</span></label>
        `,
        footerButtons: [
          {
            label: '启用效率模式',
            primary: true,
            onClick: (m, close) => {
              const noAsk = m.querySelector<HTMLInputElement>('[data-eff-noask]')?.checked;
              if (noAsk) {
                settings.confirmEfficiencyMode = false;
                persistPrefs();
              }
              applyFn();
              close();
            },
          },
          { label: '取消', onClick: (_, close) => close() },
        ],
      });
    } else {
      applyFn();
    }
  }

  function openRunNewTaskDialog() {
    showTmModal(tmShell, {
      title: '新建任务',
      width: 410,
      bodyHtml: `
        <div class="w11-tm-run-row">
          <div class="w11-tm-run-ico" aria-hidden="true">⊞</div>
          <div class="w11-tm-run-desc">输入程序、文件夹、文档或 Internet 资源的名称，Windows 将为你打开它。</div>
        </div>
        <label class="w11-tm-run-field">
          <span>打开(O):</span>
          <input type="text" class="w11-tm-input" list="tmRunPresets" value="cmd" data-run-input />
          <datalist id="tmRunPresets">
            <option value="cmd"></option>
            <option value="powershell"></option>
            <option value="exhyperv"></option>
            <option value="settings"></option>
            <option value="terminal"></option>
            <option value="notepad"></option>
            <option value="calc"></option>
          </datalist>
        </label>
        <label class="w11-tm-set-check">
          <input type="checkbox" checked data-run-admin />
          <span>以系统管理权限创建此任务。</span>
        </label>
      `,
      footerButtons: [
        {
          label: '确定',
          primary: true,
          onClick: (m, close) => {
            const raw = (m.querySelector<HTMLInputElement>('[data-run-input]')?.value ?? '').trim();
            close();
            if (!raw) return;
            const lower = raw.toLowerCase().replace(/\.exe$/, '');
            if (lower === 'exhyperv' || lower === 'settings' || lower === 'terminal') {
              shell.openApp(lower);
            } else {
              const newPid = 12000 + Math.floor(Math.random() * 7000);
              const label = raw.endsWith('.exe') ? raw.slice(0, -4) : raw;
              const exeName = raw.endsWith('.exe') ? raw : `${raw}.exe`;
              const newProc: TmProcessNode = {
                id: `user-run-${newPid}`,
                pid: newPid,
                name: label === 'cmd' ? 'Windows 命令处理程序' : label === 'powershell' ? 'Windows PowerShell' : label,
                processName: exeName,
                commandLine: `C:\\Windows\\System32\\${exeName}`,
                publisher: 'Microsoft Windows',
                group: 'app',
                status: '',
                cpu: 0.4,
                memMb: 12.6,
                diskMbS: 0,
                netMbps: 0,
                gpuPct: 0,
                gpuEngine: '',
                npuPct: 0,
                npuEngine: '',
                powerUsage: '低',
                powerTrend: '极低',
                isolation: '无',
                arch: 'ARM64',
                user: 'admin',
                desc: exeName,
                priority: '正常',
                filePath: `C:\\Windows\\System32\\${exeName}`,
              };
              processes.unshift(newProc);
              selectedProcId = newProc.id;
              renderTopbar();
              renderContent();
            }
          },
        },
        { label: '取消', onClick: (_, close) => close() },
      ],
      onMount: (m) => {
        const inp = m.querySelector<HTMLInputElement>('[data-run-input]');
        inp?.focus();
        inp?.select();
      },
    });
  }

  function openPropertiesModal(opts: { name: string; exe: string; path: string; publisher: string; arch?: string; pid?: number }) {
    showTmModal(tmShell, {
      title: `${opts.name} 属性`,
      width: 390,
      bodyHtml: `
        <div class="w11-tm-prop-grid">
          <div class="w11-tm-prop-head">
            <span class="w11-tm-appdot is-app is-lg"></span>
            <div><strong>${esc(opts.exe)}</strong><div class="w11-tm-muted-text">${esc(opts.publisher)}</div></div>
          </div>
          <div class="w11-tm-prop-sep"></div>
          <div><span>类型:</span><span>应用程序 (.exe)</span></div>
          <div><span>位置:</span><span>${esc(opts.path.replace(/\\[^\\]+$/, ''))}</span></div>
          <div><span>体系结构:</span><span>${esc(opts.arch ?? 'ARM64')}</span></div>
          ${opts.pid ? `<div><span>PID:</span><span>${opts.pid}</span></div>` : ''}
          <div><span>数字签名:</span><span>Microsoft Windows Publisher</span></div>
        </div>
      `,
      footerButtons: [{ label: '确定', primary: true, onClick: (_, close) => close() }],
    });
  }

  function openAffinityModal(node: TmProcessNode) {
    const cores = Math.max(1, hw.cpu.logicalCores);
    const checks = Array.from({ length: cores }, (_, i) => `
      <label class="w11-tm-set-check"><input type="checkbox" checked /><span>CPU ${i}</span></label>
    `).join('');
    showTmModal(tmShell, {
      title: '处理器相关性',
      width: 340,
      bodyHtml: `
        <p class="w11-tm-modal-text">允许 “${esc(node.processName)}” 在哪些处理器上运行?</p>
        <div class="w11-tm-aff-list">
          <label class="w11-tm-set-check"><input type="checkbox" checked data-aff-all /><span>&lt;全部处理器&gt;</span></label>
          ${checks}
        </div>
      `,
      footerButtons: [
        { label: '确定', primary: true, onClick: (_, close) => close() },
        { label: '取消', onClick: (_, close) => close() },
      ],
      onMount: (m) => {
        const all = m.querySelector<HTMLInputElement>('[data-aff-all]');
        const items = [...m.querySelectorAll<HTMLInputElement>('input:not([data-aff-all])')];
        all?.addEventListener('change', () => items.forEach((c) => { c.checked = all.checked; }));
      },
    });
  }

  function openNetworkDetailsModal() {
    const speedStr = hw.network.downlinkMbps ? `${hw.network.downlinkMbps} Mbps` : '1 Gbps';
    const adapterLabel = hw.network.isWifiReal ? 'WLAN' : '以太网';
    const throughputMbps = ((hw.network.sendKbps + hw.network.recvKbps) / 1000).toFixed(2);
    showTmModal(tmShell, {
      title: '网络详细信息',
      width: 660,
      bodyHtml: `
        <div class="w11-tm-netdet-wrap">
          <table class="w11-tm-proc-table">
            <thead>
              <tr>
                <th>网络名</th>
                <th class="is-num">网络使用率</th>
                <th class="is-num">链接速度</th>
                <th>状态</th>
                <th>类型</th>
                <th class="is-num">发送/接收 (Mbps)</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>${esc(hw.network.adapterName.value)}</td>
                <td class="w11-tm-num-cell">0%</td>
                <td class="w11-tm-num-cell">${esc(speedStr)}</td>
                <td>${hw.network.onLine ? '操作' : '已断开连接'}</td>
                <td>${adapterLabel}</td>
                <td class="w11-tm-num-cell">${throughputMbps} Mbps</td>
              </tr>
              <tr>
                <td>vEthernet (Default Switch)</td>
                <td class="w11-tm-num-cell">0%</td>
                <td class="w11-tm-num-cell">10 Gbps</td>
                <td>操作</td>
                <td>Hyper-V Virtual Ethernet</td>
                <td class="w11-tm-num-cell">0.00 Mbps</td>
              </tr>
            </tbody>
          </table>
        </div>
      `,
      footerButtons: [{ label: '关闭', primary: true, onClick: (_, close) => close() }],
    });
  }

  function copyPerfSummary() {
    const lines = [
      `CPU (${hw.cpu.displayName.value}): ${hw.cpu.usagePct}% @ ${hw.cpu.currentClockGhz.toFixed(2)} GHz`,
      `内存: ${hw.memory.usedGb.toFixed(1)}/${hw.memory.usableGb.toFixed(1)} GB (${hw.memory.usagePct}%)`,
      `磁盘 0 (C:): 活动 ${hw.storage.activePct}% | 读 ${hw.storage.readKb} KB/s | 写 ${hw.storage.writeKb} KB/s`,
      `${hw.network.isWifiReal ? 'WLAN' : '以太网'}: 发送 ${hw.network.sendKbps} Kbps | 接收 ${hw.network.recvKbps} Kbps`,
      ...hw.gpus.map((g) => `GPU ${g.index} (${g.name}): ${g.usagePct}%`),
    ];
    try {
      void navigator.clipboard?.writeText(lines.join('\n'));
    } catch {}
  }

  function getPerfCardDescriptors() {
    const adapterLabel = hw.network.isWifiReal ? 'WLAN' : '以太网';
    const items: Array<{
      key: PerfResourceKey;
      title: string;
      sub: string;
      tipTitle: string;
      pts: number[];
      secondaryPts?: number[];
      maxY?: number;
      color: (typeof TM_SERIES_COLORS)['cpu'];
    }> = [
      {
        key: 'cpu',
        title: 'CPU',
        sub: `${hw.cpu.usagePct}% ${hw.cpu.currentClockGhz.toFixed(2)} GHz`,
        tipTitle: 'CPU 利用率百分比与当前频率',
        pts: hw.cpu.history,
        color: TM_SERIES_COLORS.cpu,
      },
      {
        key: 'memory',
        title: '内存',
        sub: `${hw.memory.usedGb.toFixed(1)}/${hw.memory.usableGb.toFixed(1)} GB (${hw.memory.usagePct}%)`,
        tipTitle: '物理内存使用量与使用率',
        pts: hw.memory.history,
        color: TM_SERIES_COLORS.mem,
      },
      {
        key: 'disk',
        title: '磁盘 0 (C:)',
        sub: `${hw.storage.probe?.type ?? 'NVMe SSD'} · ${hw.storage.activePct}%`,
        tipTitle: '磁盘处理读取或写入请求的时间百分比',
        pts: hw.storage.activeHistory,
        color: TM_SERIES_COLORS.disk,
      },
      {
        key: 'network',
        title: adapterLabel,
        sub: hw.network.onLine
          ? `发送: ${hw.network.sendKbps.toFixed(1)} 接收: ${hw.network.recvKbps.toFixed(1)} Kbps`
          : '已断开连接',
        tipTitle: '此网络上的发送和接收活动',
        pts: hw.network.rxHistory,
        secondaryPts: netSendPts,
        maxY: computeDynamicScaleMax(Math.max(100, ...hw.network.rxHistory, ...netSendPts), [100, 500, 1000, 10000]),
        color: TM_SERIES_COLORS.net,
      },
    ];

    if (hw.npu) {
      items.push({
        key: 'npu',
        title: 'NPU 0',
        sub: `${hw.npu.sidebarName} · ${hw.npu.usagePct}%`,
        tipTitle: 'NPU 神经网络处理单元利用率',
        pts: hw.npu.history,
        color: TM_SERIES_COLORS.npu,
      });
    }

    hw.gpus.forEach((gpu, idx) => {
      items.push({
        key: `gpu-${idx}`,
        title: `GPU ${idx}`,
        sub: `${gpu.name} · ${gpu.usagePct}%`,
        tipTitle: `${gpu.name} 引擎综合利用率`,
        pts: gpu.pts3d,
        color: TM_SERIES_COLORS.gpu,
      });
    });

    return items;
  }

  function openPerfCardMenu(clientX: number, clientY: number) {
    menuCtrl.openAt(clientX, clientY, [
      {
        label: '摘要视图(W)',
        checked: summaryMode === 'perf-cards',
        checkedType: 'check',
        onSelect: () => {
          summaryMode = summaryMode === 'perf-cards' ? 'none' : 'perf-cards';
          renderChromeAndLayout();
        },
      },
      {
        label: '隐藏图形(H)',
        checked: hideGraphs,
        checkedType: 'check',
        onSelect: () => {
          hideGraphs = !hideGraphs;
          persistPrefs();
          renderContent();
        },
      },
      { type: 'divider' },
      { label: '复制(C)', shortcut: 'Ctrl+C', onSelect: copyPerfSummary },
    ]);
  }

  function openPerfChartMenu(clientX: number, clientY: number) {
    const viewItems: TmMenuItem[] = [
      { label: 'CPU(P)', checked: activePerfKey === 'cpu', checkedType: 'radio', onSelect: () => { activePerfKey = 'cpu'; renderContent(); } },
      { label: '内存(M)', checked: activePerfKey === 'memory', checkedType: 'radio', onSelect: () => { activePerfKey = 'memory'; renderContent(); } },
      { label: '磁盘(D)', checked: activePerfKey === 'disk', checkedType: 'radio', onSelect: () => { activePerfKey = 'disk'; renderContent(); } },
      { label: '网络(N)', checked: activePerfKey === 'network', checkedType: 'radio', onSelect: () => { activePerfKey = 'network'; renderContent(); } },
      ...hw.gpus.map((_, i) => ({
        label: hw.gpus.length > 1 ? `GPU ${i}` : 'GPU(G)',
        checked: activePerfKey === `gpu-${i}`,
        checkedType: 'radio' as const,
        onSelect: () => { activePerfKey = `gpu-${i}`; renderContent(); },
      })),
    ];

    const items: TmMenuItem[] = [];
    if (activePerfKey === 'cpu') {
      items.push(
        {
          label: '将图形更改为(H)',
          submenu: [
            { label: '总体利用率(O)', checked: cpuGraphMode === 'overall', checkedType: 'radio', onSelect: () => { cpuGraphMode = 'overall'; persistPrefs(); renderContent(); } },
            { label: '逻辑处理器(L)', checked: cpuGraphMode === 'logical', checkedType: 'radio', onSelect: () => { cpuGraphMode = 'logical'; persistPrefs(); renderContent(); } },
            { label: 'NUMA 节点(N)', checked: cpuGraphMode === 'numa', checkedType: 'radio', onSelect: () => { cpuGraphMode = 'numa'; persistPrefs(); renderContent(); } },
          ],
        },
        {
          label: '显示内核时间(K)',
          checked: showKernelTimes,
          checkedType: 'check',
          onSelect: () => { showKernelTimes = !showKernelTimes; persistPrefs(); renderContent(); },
        },
        { type: 'divider' },
      );
    }
    if (activePerfKey === 'network') {
      items.push(
        { label: '查看网络详细信息(D)', onSelect: openNetworkDetailsModal },
        { type: 'divider' },
      );
    }

    items.push(
      {
        label: '图形摘要视图(W)',
        checked: summaryMode === 'graph-only',
        checkedType: 'check',
        onSelect: () => {
          summaryMode = summaryMode === 'graph-only' ? 'none' : 'graph-only';
          renderChromeAndLayout();
        },
      },
      { label: '查看(V)', submenu: viewItems },
      { type: 'divider' },
      { label: '复制(C)', shortcut: 'Ctrl+C', onSelect: copyPerfSummary },
    );

    menuCtrl.openAt(clientX, clientY, items);
  }

  function renderPerfDetail(isGraphOnly = false): string {
    if (activePerfKey === 'cpu') {
      const cores = Math.max(1, hw.cpu.logicalCores);
      const cols = cores <= 2 ? cores : cores <= 4 ? 2 : cores <= 9 ? 3 : cores <= 12 ? 4 : cores <= 20 ? 5 : 6;
      const kernelOverall = showKernelTimes ? hw.cpu.history.map((v) => Math.max(1, Math.round(v * 0.32))) : undefined;

      const chartHtml =
        cpuGraphMode === 'logical'
          ? `
            <div class="w11-tm-chart-caption"><span>60 秒内的利用率 %</span><span>100%</span></div>
            <div class="w11-tm-core-grid" data-perf-chart-area style="grid-template-columns:repeat(${cols},minmax(0,1fr));grid-template-rows:repeat(${Math.ceil(cores / cols)},minmax(0,1fr));">
              ${hw.cpu.coreHistories
                .map((pts, i) => {
                  const kPts = showKernelTimes ? pts.map((v) => Math.max(1, Math.round(v * 0.3))) : undefined;
                  return `<div class="w11-tm-core-cell" data-tm-tip="CPU ${i}" data-tm-tip-body="当前利用率: ${pts[pts.length - 1] ?? 0}%">${renderPerfChartSvg({
                    primaryPts: pts,
                    secondaryPts: kPts,
                    secondaryMode: 'kernel-overlay',
                    cols: 5,
                    rows: 4,
                    color: TM_SERIES_COLORS.cpu,
                  })}</div>`;
                })
                .join('')}
            </div>
          `
          : `
            <div class="w11-tm-chart-caption"><span>${cpuGraphMode === 'numa' ? 'NUMA 节点 0 利用率 %' : '% 利用率'}</span><span>100%</span></div>
            <div class="w11-tm-single-chart" data-perf-chart-area data-tm-tip="CPU 活动" data-tm-tip-body="当前总体利用率: ${hw.cpu.usagePct}%">
              ${renderPerfChartSvg({
                primaryPts: hw.cpu.history,
                secondaryPts: kernelOverall,
                secondaryMode: 'kernel-overlay',
                width: 600,
                height: 220,
                color: TM_SERIES_COLORS.cpu,
              })}
            </div>
            <div class="w11-tm-chart-caption is-footer"><span>60 秒</span><span>0</span></div>
          `;

      if (isGraphOnly) {
        return `<div class="w11-tm-perf-detail is-graph-only"><div class="w11-tm-detail-head is-compact"><h2>CPU</h2><span class="w11-tm-detail-hw">${hw.cpu.usagePct}% ${hw.cpu.currentClockGhz.toFixed(2)} GHz</span></div>${chartHtml}</div>`;
      }

      const l1 = hw.cpu.isVmFallback ? '无' : hw.cpu.cache.l1 ?? '无';
      const l2 = hw.cpu.isVmFallback ? '无' : hw.cpu.cache.l2 ?? '无';
      const l3 = hw.cpu.isVmFallback ? '无' : hw.cpu.cache.l3 ?? '无';

      return `
        <div class="w11-tm-perf-detail">
          <div class="w11-tm-detail-head">
            <h2>CPU</h2>
            <span class="w11-tm-detail-hw">${esc(hw.cpu.displayName.value)}</span>
          </div>
          ${chartHtml}
          <div class="w11-tm-bottom-stats">
            <div class="w11-tm-kpis">
              <div class="w11-tm-kpi-row">
                <div class="w11-tm-kpi"><span>利用率</span><strong>${hw.cpu.usagePct}%</strong></div>
                <div class="w11-tm-kpi"><span>速度</span><strong>${hw.cpu.currentClockGhz.toFixed(2)} GHz</strong></div>
              </div>
              <div class="w11-tm-kpi-row">
                <div class="w11-tm-kpi"><span>进程</span><strong>${hw.cpu.procCount}</strong></div>
                <div class="w11-tm-kpi"><span>线程</span><strong>${hw.cpu.threadCount}</strong></div>
                <div class="w11-tm-kpi"><span>句柄</span><strong>${hw.cpu.handleCount}</strong></div>
              </div>
              <div class="w11-tm-kpi-row">
                <div class="w11-tm-kpi"><span>正常运行时间</span><strong>${formatUptime(uptimeSec)}</strong></div>
              </div>
            </div>
            <div class="w11-tm-kv-table is-cpu-kv">
              <span class="w11-tm-kv-label">基准速度:</span><span class="w11-tm-kv-val">${hw.cpu.baseClockGhz.value.toFixed(2)} GHz</span>
              <span class="w11-tm-kv-label">插槽:</span><span class="w11-tm-kv-val">1</span>
              <span class="w11-tm-kv-label">${hw.cpu.isVmFallback ? '虚拟处理器:' : '内核:'}</span><span class="w11-tm-kv-val">${hw.cpu.isVmFallback ? cores : hw.cpu.physicalCores.value}</span>
              ${hw.cpu.isVmFallback ? `<span class="w11-tm-kv-label">虚拟机:</span><span class="w11-tm-kv-val">是</span><span class="w11-tm-kv-label">L1 缓存:</span><span class="w11-tm-kv-val">${esc(l1)}</span>` : `<span class="w11-tm-kv-label">逻辑处理器:</span><span class="w11-tm-kv-val">${cores}</span><span class="w11-tm-kv-label">虚拟化:</span><span class="w11-tm-kv-val">已启用</span><span class="w11-tm-kv-label">L1 缓存:</span><span class="w11-tm-kv-val">${esc(l1)}</span><span class="w11-tm-kv-label">L2 缓存:</span><span class="w11-tm-kv-val">${esc(l2)}</span><span class="w11-tm-kv-label">L3 缓存:</span><span class="w11-tm-kv-val">${esc(l3)}</span>`}
            </div>
          </div>
        </div>
      `;
    }

    if (activePerfKey === 'memory') {
      const sp = hw.memory.spec;
      const chartPart = `
        <div class="w11-tm-chart-caption"><span>内存使用情况</span><span>${hw.memory.usableGb.toFixed(1)} GB</span></div>
        <div class="w11-tm-single-chart" data-perf-chart-area data-tm-tip="使用中">
          ${renderPerfChartSvg({ primaryPts: hw.memory.history, width: 600, height: 210, color: TM_SERIES_COLORS.mem })}
        </div>
        <div class="w11-tm-chart-caption is-footer"><span>60 秒</span><span>0</span></div>
        <div class="w11-tm-chart-caption" style="margin-top:8px"><span>内存组合</span><span></span></div>
        ${renderMemoryCompositionBar(hw)}
      `;
      if (isGraphOnly) {
        return `<div class="w11-tm-perf-detail is-graph-only"><div class="w11-tm-detail-head is-compact"><h2>内存</h2><span class="w11-tm-detail-hw">${hw.memory.usedGb.toFixed(1)}/${hw.memory.usableGb.toFixed(1)} GB (${hw.memory.usagePct}%)</span></div>${chartPart}</div>`;
      }
      return `
        <div class="w11-tm-perf-detail">
          <div class="w11-tm-detail-head">
            <h2>内存</h2>
            <span class="w11-tm-detail-hw">${hw.memory.usableGb.toFixed(1)} GB${sp?.formFactor && !hw.cpu.isVmFallback ? ` ${esc(sp.formFactor)}` : ''}</span>
          </div>
          ${chartPart}
          <div class="w11-tm-bottom-stats">
            <div class="w11-tm-kpis">
              <div class="w11-tm-kpi-row">
                <div class="w11-tm-kpi"><span>使用中(已压缩)</span><strong>${hw.memory.usedGb.toFixed(1)} GB (${hw.memory.compressedMb} MB)</strong></div>
                <div class="w11-tm-kpi"><span>可用</span><strong>${hw.memory.availGb.toFixed(1)} GB</strong></div>
              </div>
              <div class="w11-tm-kpi-row">
                <div class="w11-tm-kpi"><span>已提交</span><strong>${hw.memory.committedGb.toFixed(1)}/${hw.memory.commitLimitGb.toFixed(1)} GB</strong></div>
                <div class="w11-tm-kpi"><span>已缓存</span><strong>${esc(hw.memory.cachedText)}</strong></div>
              </div>
              <div class="w11-tm-kpi-row">
                <div class="w11-tm-kpi"><span>分页缓冲池</span><strong>${hw.memory.pagedPoolMb} MB</strong></div>
                <div class="w11-tm-kpi"><span>非分页缓冲池</span><strong>${hw.memory.nonPagedPoolMb} MB</strong></div>
              </div>
            </div>
            <div class="w11-tm-kv-table is-mem-kv">
              ${
                hw.cpu.isVmFallback
                  ? `<span class="w11-tm-kv-label">最大内存:</span><span class="w11-tm-kv-val">${hw.memory.usableGb.toFixed(1)} GB</span>
                     <span class="w11-tm-kv-label">已使用的插槽:</span><span class="w11-tm-kv-val">无</span>
                     <span class="w11-tm-kv-label">为硬件保留的内存:</span><span class="w11-tm-kv-val">1.1 MB</span>`
                  : `<span class="w11-tm-kv-label">速度:</span><span class="w11-tm-kv-val">${sp?.speedMhz ?? 5600} MT/秒</span>
                     <span class="w11-tm-kv-label">已使用的插槽:</span><span class="w11-tm-kv-val">${sp?.slotsUsed ?? 2} / ${sp?.slotsTotal ?? 2}</span>
                     <span class="w11-tm-kv-label">外形规格:</span><span class="w11-tm-kv-val">${esc(sp?.formFactor ?? 'SODIMM')}</span>
                     <span class="w11-tm-kv-label">为硬件保留的内存:</span><span class="w11-tm-kv-val">${sp?.hardwareReservedMb ?? 256} MB</span>`
              }
            </div>
          </div>
        </div>
      `;
    }

    if (activePerfKey === 'disk') {
      const sp = hw.storage.probe;
      const diskCap = sp?.quotaBytes ? formatGiB(sp.quotaBytes) : '128 GB';
      const diskModel = sp?.supported ? `${diskCap} 浏览器存储配额` : 'Microsoft Virtual Disk';
      const readRatePts = hw.storage.rateHistory.map((v) => Math.max(0, v * 12));
      const writeRatePts = diskWritePts.map((v) => Math.max(0, v * 10));
      const peakKb = Math.max(80, ...readRatePts, ...writeRatePts);
      const scaleMaxKb = computeDynamicScaleMax(peakKb, [100, 500, 1024, 5120, 20480]);

      const chartsHtml = `
        <div class="w11-tm-chart-caption"><span>活动时间</span><span>100%</span></div>
        <div class="w11-tm-single-chart" data-perf-chart-area style="flex:1.15" data-tm-tip="磁盘处理读取或写入请求的时间百分比">
          ${renderPerfChartSvg({ primaryPts: hw.storage.activeHistory, width: 600, height: 150, rows: 6, color: TM_SERIES_COLORS.disk })}
        </div>
        <div class="w11-tm-chart-caption" style="margin-top:8px"><span>磁盘传输速率</span><span>${formatThroughputKb(scaleMaxKb)}</span></div>
        <div class="w11-tm-single-chart is-rate" data-perf-chart-area style="flex:0.85" data-tm-tip="此磁盘上的读取和写入活动" data-tm-tip-body="峰值: ${formatThroughputKb(peakKb)}">
          ${renderPerfChartSvg({
            primaryPts: readRatePts,
            secondaryPts: writeRatePts,
            secondaryMode: 'dashed-series',
            maxY: scaleMaxKb,
            peakValue: peakKb,
            width: 600,
            height: 110,
            rows: 4,
            color: TM_SERIES_COLORS.disk,
          })}
          <span class="w11-tm-peak-label">${formatThroughputKb(peakKb)}</span>
        </div>
        <div class="w11-tm-chart-caption is-footer"><span>60 秒</span><span>0</span></div>
      `;

      if (isGraphOnly) {
        return `<div class="w11-tm-perf-detail is-graph-only"><div class="w11-tm-detail-head is-compact"><h2>磁盘 0 (C:)</h2><span class="w11-tm-detail-hw">${hw.storage.activePct}%</span></div>${chartsHtml}</div>`;
      }

      return `
        <div class="w11-tm-perf-detail">
          <div class="w11-tm-detail-head">
            <h2>磁盘 0 (C:)</h2>
            <span class="w11-tm-detail-hw">${esc(diskModel)}</span>
          </div>
          ${chartsHtml}
          <div class="w11-tm-bottom-stats">
            <div class="w11-tm-kpis">
              <div class="w11-tm-kpi-row">
                <div class="w11-tm-kpi"><span>活动时间</span><strong>${hw.storage.activePct}%</strong></div>
                <div class="w11-tm-kpi"><span>平均响应时间</span><strong>0.2 毫秒</strong></div>
              </div>
              <div class="w11-tm-kpi-row">
                <div class="w11-tm-kpi is-bar is-solid-disk"><span>读取速度</span><strong>${formatThroughputKb(hw.storage.readKb)}</strong></div>
                <div class="w11-tm-kpi is-bar is-dashed-disk"><span>写入速度</span><strong>${formatThroughputKb(hw.storage.writeKb)}</strong></div>
              </div>
            </div>
            <div class="w11-tm-kv-table is-disk-kv">
              <span class="w11-tm-kv-label">容量:</span><span class="w11-tm-kv-val">${esc(diskCap)}</span>
              <span class="w11-tm-kv-label">已格式化:</span><span class="w11-tm-kv-val">${esc(diskCap)}</span>
              <span class="w11-tm-kv-label">系统磁盘:</span><span class="w11-tm-kv-val">是</span>
              <span class="w11-tm-kv-label">页面文件:</span><span class="w11-tm-kv-val">是</span>
              <span class="w11-tm-kv-label">类型:</span><span class="w11-tm-kv-val">${esc(sp?.type ?? 'NVMe SSD')}</span>
            </div>
          </div>
        </div>
      `;
    }

    if (activePerfKey === 'network') {
      const adapterTitle = hw.network.isWifiReal ? 'WLAN' : '以太网';
      const peakKbps = Math.max(40, ...hw.network.rxHistory, ...netSendPts);
      const scaleMax = computeDynamicScaleMax(peakKbps, [100, 500, 1000, 10000, 100000]);
      const chartHtml = `
        <div class="w11-tm-chart-caption"><span>吞吐量</span><span>${formatNetRateKbps(scaleMax)}</span></div>
        <div class="w11-tm-single-chart is-rate" data-perf-chart-area data-tm-tip="此网络上的发送和接收活动" data-tm-tip-body="峰值: ${formatNetRateKbps(peakKbps)}">
          ${renderPerfChartSvg({
            primaryPts: hw.network.rxHistory,
            secondaryPts: netSendPts,
            secondaryMode: 'dashed-series',
            maxY: scaleMax,
            peakValue: peakKbps,
            width: 600,
            height: 220,
            color: TM_SERIES_COLORS.net,
          })}
          <span class="w11-tm-peak-label">${formatNetRateKbps(peakKbps)}</span>
        </div>
        <div class="w11-tm-chart-caption is-footer"><span>60 秒</span><span>0</span></div>
      `;
      if (isGraphOnly) {
        return `<div class="w11-tm-perf-detail is-graph-only"><div class="w11-tm-detail-head is-compact"><h2>${adapterTitle}</h2><span class="w11-tm-detail-hw">S: ${hw.network.sendKbps} / R: ${hw.network.recvKbps} Kbps</span></div>${chartHtml}</div>`;
      }
      return `
        <div class="w11-tm-perf-detail">
          <div class="w11-tm-detail-head">
            <h2>${adapterTitle}</h2>
            <span class="w11-tm-detail-hw">${esc(hw.network.adapterName.value)}</span>
          </div>
          ${chartHtml}
          <div class="w11-tm-bottom-stats">
            <div class="w11-tm-kpis">
              <div class="w11-tm-kpi-row">
                <div class="w11-tm-kpi is-bar is-dashed-net"><span>发送</span><strong>${hw.network.sendKbps.toFixed(1)} Kbps</strong></div>
                <div class="w11-tm-kpi is-bar is-solid-net"><span>接收</span><strong>${hw.network.recvKbps.toFixed(1)} Kbps</strong></div>
              </div>
            </div>
            <div class="w11-tm-kv-table is-mem-kv">
              <span class="w11-tm-kv-label">适配器名称:</span><span class="w11-tm-kv-val">${esc(hw.network.adapterName.value)}</span>
              <span class="w11-tm-kv-label">连接类型:</span><span class="w11-tm-kv-val">${hw.network.isWifiReal ? '802.11' : '以太网'}</span>
              <span class="w11-tm-kv-label">下行带宽:</span><span class="w11-tm-kv-val">${hw.network.downlinkMbps ? `${hw.network.downlinkMbps} Mbps` : '1.0 Gbps'}</span>
              <span class="w11-tm-kv-label">RTT 延迟:</span><span class="w11-tm-kv-val">${hw.network.rttMs !== undefined ? `${hw.network.rttMs} ms` : '12 ms'}</span>
            </div>
          </div>
        </div>
      `;
    }

    if (activePerfKey === 'npu' && hw.npu) {
      const npu = hw.npu;
      const chartHtml = `
        <div class="w11-tm-chart-caption"><span>Compute</span><span>100%</span></div>
        <div class="w11-tm-single-chart" data-perf-chart-area data-tm-tip="NPU 利用率">
          ${renderPerfChartSvg({ primaryPts: npu.history, width: 600, height: 220, color: TM_SERIES_COLORS.npu })}
        </div>
        <div class="w11-tm-chart-caption is-footer"><span>60 秒</span><span>0</span></div>
      `;
      if (isGraphOnly) {
        return `<div class="w11-tm-perf-detail is-graph-only"><div class="w11-tm-detail-head is-compact"><h2>NPU 0</h2><span class="w11-tm-detail-hw">${npu.usagePct}%</span></div>${chartHtml}</div>`;
      }
      return `
        <div class="w11-tm-perf-detail">
          <div class="w11-tm-detail-head"><h2>NPU 0</h2><span class="w11-tm-detail-hw">${esc(npu.displayName)}</span></div>
          ${chartHtml}
          <div class="w11-tm-bottom-stats">
            <div class="w11-tm-kpis"><div class="w11-tm-kpi"><span>利用率</span><strong>${npu.usagePct}%</strong></div></div>
            <div class="w11-tm-kv-table">
              <span class="w11-tm-kv-label">厂商:</span><span class="w11-tm-kv-val">${esc(npu.vendor)}</span>
              <span class="w11-tm-kv-label">算力峰值:</span><span class="w11-tm-kv-val">${npu.topsPeak ? `${npu.topsPeak} TOPS` : '—'}</span>
            </div>
          </div>
        </div>
      `;
    }

    const gpuIndex = activePerfKey.startsWith('gpu-') ? Number(activePerfKey.slice(4)) : 0;
    const gpu = hw.gpus[gpuIndex] ?? hw.gpus[0];
    const engines = [
      { name: '3D', pts: gpu?.pts3d ?? [] },
      { name: 'Copy', pts: gpu?.ptsCopy ?? [] },
      { name: 'Video Encode', pts: gpu?.ptsEnc ?? [] },
      { name: 'Video Decode', pts: gpu?.ptsDec ?? [] },
    ];
    const sharedTotalGb = Math.max(2, +(hw.memory.usableGb / 2).toFixed(1));
    const sharedUsedGb = +(sharedTotalGb * ((gpu?.memUtilPct ?? 6) / 100)).toFixed(1);

    const gridHtml = `
      <div class="w11-tm-gpu-grid" data-perf-chart-area>
        ${engines
          .map(
            (eng) => `
              <div class="w11-tm-gpu-box">
                <div class="w11-tm-chart-caption"><span>${eng.name}</span><span>100%</span></div>
                <div class="w11-tm-core-cell" data-tm-tip="${eng.name}" data-tm-tip-body="引擎利用率: ${eng.pts[eng.pts.length - 1] ?? 0}%">
                  ${renderPerfChartSvg({ primaryPts: eng.pts, cols: 6, rows: 4, color: TM_SERIES_COLORS.gpu })}
                </div>
              </div>
            `,
          )
          .join('')}
      </div>
      <div class="w11-tm-chart-caption"><span>共享 GPU 内存使用率</span><span>${sharedTotalGb.toFixed(1)} GB</span></div>
      <div class="w11-tm-single-chart" data-perf-chart-area style="flex:0 0 88px;min-height:74px" data-tm-tip="共享 GPU 内存">
        ${renderPerfChartSvg({ primaryPts: gpu?.memPts ?? [5, 5], width: 600, height: 90, rows: 3, color: TM_SERIES_COLORS.gpu })}
      </div>
      <div class="w11-tm-chart-caption is-footer"><span>60 秒</span><span>0</span></div>
    `;
    if (isGraphOnly) {
      return `<div class="w11-tm-perf-detail is-graph-only"><div class="w11-tm-detail-head is-compact"><h2>GPU ${gpu?.index ?? 0}</h2><span class="w11-tm-detail-hw">${gpu?.usagePct ?? 0}%</span></div>${gridHtml}</div>`;
    }
    return `
      <div class="w11-tm-perf-detail">
        <div class="w11-tm-detail-head">
          <h2>GPU ${gpu?.index ?? 0}</h2>
          <span class="w11-tm-detail-hw">${esc(gpu?.name ?? '未知显卡')}</span>
        </div>
        ${gridHtml}
        <div class="w11-tm-bottom-stats">
          <div class="w11-tm-kpis">
            <div class="w11-tm-kpi-row">
              <div class="w11-tm-kpi"><span>利用率</span><strong>${gpu?.usagePct ?? 0}%</strong></div>
              <div class="w11-tm-kpi"><span>共享 GPU 内存</span><strong>${sharedUsedGb}/${sharedTotalGb.toFixed(1)} GB</strong></div>
            </div>
          </div>
          <div class="w11-tm-kv-table">
            <span class="w11-tm-kv-label">GPU 架构:</span><span class="w11-tm-kv-val">${esc(getGpuKindLabel(gpu))}</span>
            <span class="w11-tm-kv-label">图形 API:</span><span class="w11-tm-kv-val">${esc(gpu?.webglVersion ?? 'WebGL 2.0')}</span>
          </div>
        </div>
      </div>
    `;
  }

  function renderPerfSidebarCards(summaryOnly = false): string {
    const cards = getPerfCardDescriptors();
    if (!cards.some((c) => c.key === activePerfKey)) activePerfKey = 'cpu';
    return cards
      .map((c) => {
        const active = !summaryOnly && c.key === activePerfKey ? 'is-active' : '';
        const visual = hideGraphs
          ? `<span class="w11-tm-perf-dot" style="background:${c.color.dot}"></span>`
          : `<div class="w11-tm-perf-thumb">${renderPerfChartSvg({
              primaryPts: c.pts,
              secondaryPts: c.secondaryPts,
              secondaryMode: 'dashed-series',
              maxY: c.maxY ?? 100,
              color: c.color,
              thumb: true,
            })}</div>`;
        return `
          <button type="button" class="w11-tm-perf-card ${active}" data-perf-key="${c.key}" data-tm-tip="${esc(c.tipTitle)}">
            ${visual}
            <div class="w11-tm-perf-meta">
              <div class="w11-tm-perf-title">${esc(c.title)}</div>
              <div class="w11-tm-perf-sub">${esc(c.sub)}</div>
            </div>
          </button>
        `;
      })
      .join('');
  }

  function renderPerformancePage(): string {
    return `
      <div class="w11-tm-perf-layout">
        <aside class="w11-tm-perf-list" data-tm-perf-list style="width:${sidebarWidth}px">
          ${renderPerfSidebarCards(false)}
        </aside>
        <div class="w11-tm-perf-splitter" data-tm-perf-splitter title="拖动调整侧栏宽度"></div>
        ${renderPerfDetail(false)}
      </div>
    `;
  }

  function getActiveProcCols(): readonly (typeof TM_PROC_COLUMNS)[number][] {
    return TM_PROC_COLUMNS.filter((c) => visibleCols.has(c.key));
  }

  function getProcMetricValue(p: TmProcessNode, col: TmProcColumnKey): { text: string; numericVal: number; heat: number; isPercentMode?: boolean } {
    const totalMemMb = hw.memory.usableGb * 1024;
    switch (col) {
      case 'cpu':
        return { text: `${p.cpu === 0 ? '0' : p.cpu.toFixed(1)}%`, numericVal: p.cpu, heat: p.cpu / 18 };
      case 'mem':
        return {
          text: formatMemMb(p.memMb, memDisplayMode, totalMemMb),
          numericVal: p.memMb,
          heat: p.memMb / 110,
          isPercentMode: memDisplayMode === 'percent',
        };
      case 'disk': {
        const pct = Math.min(99, p.diskMbS * 12);
        return {
          text: diskDisplayMode === 'percent' ? `${pct.toFixed(0)}%` : `${p.diskMbS === 0 ? '0' : p.diskMbS.toFixed(1)} MB/秒`,
          numericVal: p.diskMbS,
          heat: p.diskMbS / 2.2,
        };
      }
      case 'net': {
        const pct = Math.min(99, p.netMbps * 6);
        return {
          text: netDisplayMode === 'percent' ? `${pct.toFixed(0)}%` : `${p.netMbps === 0 ? '0' : p.netMbps.toFixed(1)} Mbps`,
          numericVal: p.netMbps,
          heat: p.netMbps / 3.5,
        };
      }
      case 'gpu':
        return { text: `${p.gpuPct === 0 ? '0' : p.gpuPct.toFixed(1)}%`, numericVal: p.gpuPct, heat: p.gpuPct / 20 };
      case 'npu':
        return { text: `${p.npuPct}%`, numericVal: p.npuPct, heat: p.npuPct / 20 };
      case 'pid':
        return { text: String(p.pid), numericVal: p.pid, heat: 0 };
      case 'status':
        return { text: p.status, numericVal: 0, heat: 0 };
      case 'type':
        return { text: p.group === 'app' ? '应用' : p.group === 'bg' ? '后台进程' : 'Windows 进程', numericVal: 0, heat: 0 };
      case 'publisher':
        return { text: p.publisher, numericVal: 0, heat: 0 };
      case 'processName':
        return { text: p.processName, numericVal: 0, heat: 0 };
      case 'commandLine':
        return { text: p.commandLine, numericVal: 0, heat: 0 };
      case 'gpuEngine':
        return { text: p.gpuEngine, numericVal: 0, heat: 0 };
      case 'npuEngine':
        return { text: p.npuEngine, numericVal: 0, heat: 0 };
      case 'powerUsage':
        return { text: p.powerUsage, numericVal: 0, heat: p.powerUsage === '高' ? 0.5 : p.powerUsage === '中' ? 0.2 : 0 };
      case 'powerTrend':
        return { text: p.powerTrend, numericVal: 0, heat: p.powerTrend === '高' ? 0.5 : 0 };
      case 'isolation':
        return { text: p.isolation, numericVal: 0, heat: 0 };
    }
  }

  function renderProcCell(p: TmProcessNode, colKey: TmProcColumnKey): string {
    if (colKey === 'status') {
      const icon =
        p.status === 'efficiency'
          ? `<span class="w11-tm-status-ico is-leaf" data-tm-tip="效率模式">${renderTmGlyph('effMode')}</span>`
          : p.status === 'suspended'
            ? `<span class="w11-tm-status-ico is-paused" data-tm-tip="此 UWP 进程已挂起以改进系统性能。">⏸</span>`
            : '';
      return `<td class="w11-tm-status-cell">${icon}</td>`;
    }
    const def = TM_PROC_COLUMNS.find((c) => c.key === colKey)!;
    const metric = getProcMetricValue(p, colKey);
    const heat = metric.heat > 0 ? getHeatClass(metric.heat) : '';
    const numCls = def.numeric ? 'w11-tm-num-cell' : '';
    return `<td class="${numCls} ${heat}"><span class="w11-tm-ellipsis">${esc(metric.text)}</span></td>`;
  }

  function renderProcessesPage(): string {
    const cols = getActiveProcCols();
    const sortCaret = (k: 'name' | TmProcColumnKey) => (procSortCol === k ? (procSortDir === 'asc' ? ' ∧' : ' ∨') : '');
    const compareProcs = (a: TmProcessNode, b: TmProcessNode) => {
      const m = procSortDir === 'asc' ? 1 : -1;
      if (procSortCol === 'name') return a.name.localeCompare(b.name, 'zh-CN') * m;
      const va = getProcMetricValue(a, procSortCol);
      const vb = getProcMetricValue(b, procSortCol);
      const colDef = TM_PROC_COLUMNS.find((c) => c.key === procSortCol);
      if (colDef?.numeric) return (va.numericVal - vb.numericVal) * m;
      return va.text.localeCompare(vb.text, 'zh-CN') * m;
    };

    const headerMetricMap: Partial<Record<TmProcColumnKey, string>> = {
      cpu: `${hw.cpu.usagePct}%`,
      mem: `${hw.memory.usagePct}%`,
      disk: `${hw.storage.activePct}%`,
      net: '0%',
      gpu: `${hw.gpus[0]?.usagePct ?? 0}%`,
      npu: hw.npu ? `${hw.npu.usagePct}%` : '0%',
    };

    const rowsHtml: string[] = [];
    const renderNodeAndChildren = (node: TmProcessNode) => {
      const hasKids = Boolean(node.children && node.children.length > 0);
      const isExpanded = expandedProcIds.has(node.id) || (searchQuery.trim().length > 0 && hasKids);
      const sel = selectedProcId === node.id ? 'is-selected' : '';
      const countBadge = hasKids ? ` (${node.children!.length})` : '';
      rowsHtml.push(`
        <tr class="w11-tm-proc-row ${sel}" data-proc-id="${esc(node.id)}" data-tm-proc="${esc(node.id)}">
          <td class="w11-tm-name-cell">
            ${
              hasKids
                ? `<button type="button" class="w11-tm-exp-btn" data-proc-toggle="${esc(node.id)}" aria-label="展开或折叠">${isExpanded ? '⌄' : '›'}</button>`
                : '<span class="w11-tm-exp-pad"></span>'
            }
            <span class="w11-tm-appdot ${node.iconKey === 'edge' ? 'is-edge' : node.group === 'win' ? 'is-win' : 'is-app'}"></span>
            <span class="w11-tm-ellipsis">${esc(node.name)}${countBadge}</span>
          </td>
          ${cols.map((c) => renderProcCell(node, c.key)).join('')}
          <td class="w11-tm-spacer-cell"></td>
        </tr>
      `);
      if (hasKids && isExpanded) {
        for (const c of node.children!) {
          const cSel = selectedProcId === c.id ? 'is-selected' : '';
          rowsHtml.push(`
            <tr class="w11-tm-proc-row is-child ${cSel}" data-proc-id="${esc(c.id)}" data-tm-proc="${esc(c.id)}">
              <td class="w11-tm-name-cell">
                <span class="w11-tm-exp-pad"></span>
                <span class="w11-tm-exp-pad"></span>
                <span class="w11-tm-appdot is-child"></span>
                <span class="w11-tm-ellipsis">${esc(c.name)}</span>
              </td>
              ${cols.map((col) => renderProcCell(c, col.key)).join('')}
              <td class="w11-tm-spacer-cell"></td>
            </tr>
          `);
        }
      }
    };

    const matchesNode = (p: TmProcessNode) =>
      matchesSearch(searchQuery, p.name, p.publisher, p.pid, p.processName) ||
      Boolean(p.children?.some((c) => matchesSearch(searchQuery, c.name, c.publisher, c.pid, c.processName)));

    if (groupByType) {
      for (const gm of TM_GROUP_META) {
        const list = processes.filter((p) => p.group === gm.id && matchesNode(p)).sort(compareProcs);
        rowsHtml.push(`
          <tr class="w11-tm-group-row">
            <td colspan="${cols.length + 2}">${gm.title} (${list.length})</td>
          </tr>
        `);
        list.forEach(renderNodeAndChildren);
      }
    } else {
      processes.filter(matchesNode).sort(compareProcs).forEach(renderNodeAndChildren);
    }

    return `
      <div class="w11-tm-proc-wrap">
        <table class="w11-tm-proc-table is-bounded">
          <thead>
            <tr>
              <th data-proc-sort="name" data-tm-col-hdr style="width:276px">名称${sortCaret('name')}</th>
              ${cols
                .map((c) => {
                  const metric = headerMetricMap[c.key];
                  return `<th data-proc-sort="${c.key}" data-tm-col-hdr class="${c.numeric ? 'is-num' : ''}" style="width:${c.widthPx}px">${metric ? `<div>${metric}</div>` : ''}${c.label}${sortCaret(c.key)}</th>`;
                })
                .join('')}
              <th class="is-spacer" data-tm-col-hdr aria-hidden="true"></th>
            </tr>
          </thead>
          <tbody>${rowsHtml.join('')}</tbody>
        </table>
      </div>
    `;
  }

  function buildTopbarHtml(): string {
    if (titleSearchWrap) {
      const showSearch = activeTab !== 'performance' && activeTab !== 'settings';
      titleSearchWrap.style.display = showSearch ? 'flex' : 'none';
    }

    const titles: Record<TmTabId, string> = {
      processes: '进程',
      performance: '性能',
      history: '应用历史记录',
      startup: '启动应用',
      users: '用户',
      details: '详细信息',
      services: '服务',
      settings: '设置',
    };

    const newTaskBtn = `<button type="button" class="w11-tm-cmd-btn" data-tm-cmd="new-task">${renderTmGlyph('newTask')}<span>运行新任务</span></button>`;
    const moreBtn = `<button type="button" class="w11-tm-cmd-more" data-tm-cmd="more" aria-label="更多操作">${renderTmGlyph('more')}</button>`;
    let actions = '';

    if (activeTab === 'processes') {
      const sel = findProcessById(selectedProcId);
      const canAct = Boolean(sel);
      actions = `
        ${newTaskBtn}
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="end-task" ${canAct ? '' : 'disabled'}>${renderTmGlyph('endTask')}<span>结束任务</span></button>
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="eff-mode" ${canAct ? '' : 'disabled'}>${renderTmGlyph('effMode')}<span>${sel?.status === 'efficiency' ? '退出效率模式' : '效率模式'}</span></button>
        ${moreBtn}
      `;
    } else if (activeTab === 'performance') {
      actions = `${newTaskBtn}${moreBtn}`;
    } else if (activeTab === 'history') {
      actions = `${newTaskBtn}${moreBtn}`;
    } else if (activeTab === 'startup') {
      const st = startupApps.find((s) => s.id === selectedStartupId) ?? startupApps.flatMap((s) => s.children ?? []).find((c) => c.id === selectedStartupId);
      actions = `
        ${newTaskBtn}
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="startup-enable" ${st && st.status === '已禁用' ? '' : 'disabled'}><span>启用</span></button>
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="startup-disable" ${st && st.status === '已启用' ? '' : 'disabled'}><span>禁用</span></button>
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="startup-props" ${st ? '' : 'disabled'}><span>属性</span></button>
        ${moreBtn}
      `;
    } else if (activeTab === 'users') {
      actions = `
        ${newTaskBtn}
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="user-disconnect" ${selectedUserId === 'user-admin' ? '' : 'disabled'}><span>断开连接</span></button>
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="user-manage"><span>管理用户账户</span></button>
        ${moreBtn}
      `;
    } else if (activeTab === 'details') {
      actions = `
        ${newTaskBtn}
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="det-end-task" ${selectedDetailPid !== null ? '' : 'disabled'}>${renderTmGlyph('endTask')}<span>结束任务</span></button>
        ${moreBtn}
      `;
    } else if (activeTab === 'services') {
      const svc = services.find((s) => s.name === selectedServiceName);
      actions = `
        ${newTaskBtn}
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="svc-start" ${svc && svc.status === '已停止' ? '' : 'disabled'}><span>启动</span></button>
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="svc-stop" ${svc && svc.status === '正在运行' ? '' : 'disabled'}><span>停止</span></button>
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="svc-restart" ${svc ? '' : 'disabled'}><span>重新启动</span></button>
        <button type="button" class="w11-tm-cmd-btn" data-tm-cmd="svc-open"><span>打开服务</span></button>
        ${moreBtn}
      `;
    }

    return `<h2 class="w11-tm-page-title">${titles[activeTab]}</h2>${actions ? `<div class="w11-tm-cmd-actions">${actions}</div>` : ''}`;
  }

  function renderTopbar() {
    if (topbarEl) topbarEl.innerHTML = buildTopbarHtml();
  }

  function buildContentHtml(): string {
    if (summaryMode === 'perf-cards' && activeTab === 'performance') {
      return `<div class="w11-tm-perf-list is-summary-only">${renderPerfSidebarCards(true)}</div>`;
    }
    if (summaryMode === 'graph-only' && activeTab === 'performance') {
      return renderPerfDetail(true);
    }

    switch (activeTab) {
      case 'processes':
        return renderProcessesPage();
      case 'performance':
        return renderPerformancePage();
      case 'history':
        return renderHistoryPage({
          rows: appHistory,
          selectedId: selectedHistoryId,
          search: searchQuery,
          showAllHistory: settings.showAllHistory,
          historySince,
        });
      case 'startup':
        return renderStartupPage({
          rows: startupApps,
          expandedIds: expandedStartupIds,
          selectedId: selectedStartupId,
          search: searchQuery,
        });
      case 'users':
        return renderUsersPage({
          processes,
          hw,
          expanded: usersExpanded,
          selectedId: selectedUserId,
          search: searchQuery,
          showFullAccountName: settings.showFullAccountName,
          memMode: memDisplayMode,
        });
      case 'details':
        return renderDetailsPage({
          processes,
          selectedPid: selectedDetailPid,
          search: searchQuery,
          sortKey: detailSortKey,
          sortDir: detailSortDir,
          showFullAccountName: settings.showFullAccountName,
        });
      case 'services':
        return renderServicesPage({
          services,
          selectedName: selectedServiceName,
          search: searchQuery,
          sortKey: serviceSortKey,
          sortDir: serviceSortDir,
        });
      case 'settings':
        return renderSettingsPage(settings);
    }
  }

  function renderHeadlessSnapshot() {
    container.innerHTML = `
      <div class="w11-tm-shell${navExpanded ? ' is-nav-expanded' : ''}">
        ${renderNavMarkup()}
        <div class="w11-tm-workspace">
          <header class="w11-tm-topbar">${buildTopbarHtml()}</header>
          <div class="w11-tm-content">${buildContentHtml()}</div>
        </div>
      </div>
    `;
  }

  function renderContent() {
    if (!contentEl) {
      renderHeadlessSnapshot();
      return;
    }
    const scrollHost = contentEl.querySelector<HTMLElement>('.w11-tm-proc-wrap, .w11-tm-perf-detail, .w11-tm-perf-list, .w11-tm-settings-scroll');
    const savedScrollTop = scrollHost?.scrollTop ?? 0;
    contentEl.innerHTML = buildContentHtml();
    if (summaryMode === 'none') {
      const nextScrollHost = contentEl.querySelector<HTMLElement>('.w11-tm-proc-wrap, .w11-tm-perf-detail, .w11-tm-perf-list, .w11-tm-settings-scroll');
      if (nextScrollHost && savedScrollTop > 0) nextScrollHost.scrollTop = savedScrollTop;
    }
  }

  function renderChromeAndLayout() {
    applyTheme();
    if (!tmShell) {
      renderHeadlessSnapshot();
      return;
    }
    const isSummary = activeTab === 'performance' && summaryMode !== 'none';
    tmShell.classList.toggle('is-nav-expanded', navExpanded);
    tmShell.classList.toggle('is-summary-cards', isSummary && summaryMode === 'perf-cards');
    tmShell.classList.toggle('is-summary-graph', isSummary && summaryMode === 'graph-only');
    navToggleBtn?.setAttribute('data-tm-tip', navExpanded ? '关闭导航' : '打开导航');

    tmShell.querySelectorAll<HTMLElement>('[data-tm-tab]').forEach((btn) => {
      btn.classList.toggle('is-active', btn.dataset.tmTab === activeTab);
    });
    renderTopbar();
    renderContent();
  }

  function buildResourceValueMenu(): TmMenuItem[] {
    return [
      {
        label: '内存(M)',
        submenu: [
          { label: '值(V)', checked: memDisplayMode === 'value', checkedType: 'radio', onSelect: () => { memDisplayMode = 'value'; persistPrefs(); renderContent(); } },
          { label: '百分比(P)', checked: memDisplayMode === 'percent', checkedType: 'radio', onSelect: () => { memDisplayMode = 'percent'; persistPrefs(); renderContent(); } },
        ],
      },
      {
        label: '磁盘(I)',
        submenu: [
          { label: '值(V)', checked: diskDisplayMode === 'value', checkedType: 'radio', onSelect: () => { diskDisplayMode = 'value'; persistPrefs(); renderContent(); } },
          { label: '百分比(P)', checked: diskDisplayMode === 'percent', checkedType: 'radio', onSelect: () => { diskDisplayMode = 'percent'; persistPrefs(); renderContent(); } },
        ],
      },
      {
        label: '网络(N)',
        submenu: [
          { label: '值(V)', checked: netDisplayMode === 'value', checkedType: 'radio', onSelect: () => { netDisplayMode = 'value'; persistPrefs(); renderContent(); } },
          { label: '百分比(P)', checked: netDisplayMode === 'percent', checkedType: 'radio', onSelect: () => { netDisplayMode = 'percent'; persistPrefs(); renderContent(); } },
        ],
      },
    ];
  }

  // Event delegation on tmShell
  tmShell.addEventListener('click', (e) => {
    const target = e.target as HTMLElement;

    if (target.closest('[data-tm-nav-toggle]')) {
      navExpanded = !navExpanded;
      renderChromeAndLayout();
      return;
    }

    const tabBtn = target.closest<HTMLElement>('[data-tm-tab]');
    if (tabBtn?.dataset.tmTab) {
      activeTab = tabBtn.dataset.tmTab as TmTabId;
      summaryMode = 'none';
      renderChromeAndLayout();
      return;
    }

    const cmdBtn = target.closest<HTMLElement>('[data-tm-cmd]');
    if (cmdBtn) {
      const cmd = cmdBtn.dataset.tmCmd;
      if (cmd === 'new-task') {
        openRunNewTaskDialog();
      } else if (cmd === 'end-task') {
        const node = findProcessById(selectedProcId);
        if (node) {
          if (node.appId) {
            const win = shell.getWindows().find((w) => w.appId === node.appId);
            if (win) shell.closeWindow(win.pid);
          }
          if (node.id === 'tm-self' || node.id === 'tm-self-main') {
            shell.closeWindow(pid);
            return;
          }
          removeProcessByPid(node.pid);
          renderTopbar();
          renderContent();
        }
      } else if (cmd === 'eff-mode') {
        const node = findProcessById(selectedProcId);
        if (node) toggleEfficiencyForProcess(node);
      } else if (cmd === 'det-end-task' && selectedDetailPid !== null) {
        if (selectedDetailPid === 4820) { shell.closeWindow(pid); return; }
        removeProcessByPid(selectedDetailPid);
        renderTopbar();
        renderContent();
      } else if (cmd === 'startup-enable' || cmd === 'startup-disable') {
        const next: '已启用' | '已禁用' = cmd === 'startup-enable' ? '已启用' : '已禁用';
        startupApps.forEach((s) => {
          if (s.id === selectedStartupId) s.status = next;
          s.children?.forEach((c) => { if (c.id === selectedStartupId) c.status = next; });
        });
        renderTopbar();
        renderContent();
      } else if (cmd === 'startup-props') {
        const st = startupApps.find((s) => s.id === selectedStartupId);
        if (st) openPropertiesModal({ name: st.name, exe: st.name, path: st.filePath, publisher: st.publisher });
      } else if (cmd === 'user-manage') {
        shell.openApp('settings');
      } else if (cmd === 'svc-start' || cmd === 'svc-stop' || cmd === 'svc-restart') {
        const svc = services.find((s) => s.name === selectedServiceName);
        if (svc) {
          svc.status = cmd === 'svc-stop' ? '已停止' : '正在运行';
          svc.pid = svc.status === '已停止' ? null : svc.pid ?? 2400 + Math.floor(Math.random() * 800);
          renderTopbar();
          renderContent();
        }
      } else if (cmd === 'more') {
        if (activeTab === 'processes') {
          menuCtrl.openBelowElement(cmdBtn, [
            {
              label: '视图(V)',
              submenu: [
                {
                  label: '按类型分组(G)',
                  checked: groupByType,
                  checkedType: 'check',
                  onSelect: () => { groupByType = !groupByType; persistPrefs(); renderContent(); },
                },
                { type: 'divider' },
                { label: '全部展开(E)', onSelect: () => { processes.forEach((p) => { if (p.children?.length) expandedProcIds.add(p.id); }); renderContent(); } },
                { label: '全部折叠(C)', onSelect: () => { expandedProcIds.clear(); renderContent(); } },
              ],
            },
          ]);
        } else if (activeTab === 'performance') {
          menuCtrl.openBelowElement(cmdBtn, [
            { label: '复制(C)', shortcut: 'Ctrl+C', onSelect: copyPerfSummary },
            {
              label: '资源监视器(R)',
              onSelect: () => {
                showTmModal(tmShell, {
                  title: '资源监视器',
                  width: 560,
                  bodyHtml: `
                    <div class="w11-tm-resmon">
                      <div><strong>CPU:</strong> ${hw.cpu.usagePct}% · 最大频率 100% · ${hw.cpu.threadCount} 线程</div>
                      <div><strong>内存:</strong> 使用中 ${Math.round(hw.memory.usedGb * 1024)} MB · 备用 ${hw.memory.availGb.toFixed(1)} GB</div>
                      <div><strong>磁盘:</strong> 读取 ${hw.storage.readKb} KB/秒 · 写入 ${hw.storage.writeKb} KB/秒</div>
                      <div><strong>网络:</strong> 发送 ${hw.network.sendKbps} Kbps · 接收 ${hw.network.recvKbps} Kbps</div>
                    </div>
                  `,
                  footerButtons: [{ label: '关闭', primary: true, onClick: (_, close) => close() }],
                });
              },
            },
          ]);
        } else if (activeTab === 'history') {
          menuCtrl.openBelowElement(cmdBtn, [
            {
              label: '显示所有进程的历史记录',
              checked: settings.showAllHistory,
              checkedType: 'check',
              onSelect: () => { settings.showAllHistory = !settings.showAllHistory; persistPrefs(); renderContent(); },
            },
            { label: '删除使用情况历史记录', onSelect: () => { appHistory = appHistory.map((r) => ({ ...r, cpuTimeSec: 0, netMb: 0, meteredNetMb: 0, tileUpdatesMb: 0, notifications: 0 })); historySince = new Date().toLocaleDateString('zh-CN'); renderContent(); } },
          ]);
        } else {
          menuCtrl.openBelowElement(cmdBtn, [{ label: '刷新(F5)', onSelect: () => { void shell.refreshVirtualHardware(true); } }]);
        }
      }
      return;
    }

    // Subbar actions
    if (target.closest('[data-hist-clear]')) {
      appHistory = appHistory.map((r) => ({ ...r, cpuTimeSec: 0, netMb: 0, meteredNetMb: 0, tileUpdatesMb: 0, notifications: 0 }));
      historySince = new Date().toLocaleDateString('zh-CN');
      renderContent();
      return;
    }

    // Expander toggles
    const procExp = target.closest<HTMLElement>('[data-proc-toggle]');
    if (procExp?.dataset.procToggle) {
      const id = procExp.dataset.procToggle;
      if (expandedProcIds.has(id)) expandedProcIds.delete(id);
      else expandedProcIds.add(id);
      renderContent();
      return;
    }
    const stExp = target.closest<HTMLElement>('[data-startup-toggle]');
    if (stExp?.dataset.startupToggle) {
      const id = stExp.dataset.startupToggle;
      if (expandedStartupIds.has(id)) expandedStartupIds.delete(id);
      else expandedStartupIds.add(id);
      renderContent();
      return;
    }
    if (target.closest('[data-user-toggle]')) {
      usersExpanded = !usersExpanded;
      renderContent();
      return;
    }

    // Column sorting
    const procSort = target.closest<HTMLElement>('[data-proc-sort]');
    if (procSort?.dataset.procSort) {
      const k = procSort.dataset.procSort as 'name' | TmProcColumnKey;
      if (procSortCol === k) procSortDir = procSortDir === 'asc' ? 'desc' : 'asc';
      else { procSortCol = k; procSortDir = k === 'name' ? 'asc' : 'desc'; }
      renderContent();
      return;
    }
    const detSort = target.closest<HTMLElement>('[data-det-sort]');
    if (detSort?.dataset.detSort) {
      const k = detSort.dataset.detSort as typeof detailSortKey;
      if (detailSortKey === k) detailSortDir = detailSortDir === 'asc' ? 'desc' : 'asc';
      else { detailSortKey = k; detailSortDir = 'asc'; }
      renderContent();
      return;
    }
    const svcSort = target.closest<HTMLElement>('[data-svc-sort]');
    if (svcSort?.dataset.svcSort) {
      const k = svcSort.dataset.svcSort as typeof serviceSortKey;
      if (serviceSortKey === k) serviceSortDir = serviceSortDir === 'asc' ? 'desc' : 'asc';
      else { serviceSortKey = k; serviceSortDir = 'asc'; }
      renderContent();
      return;
    }

    // Row selection
    const procRow = target.closest<HTMLElement>('[data-proc-id]');
    if (procRow?.dataset.procId) {
      selectedProcId = procRow.dataset.procId;
      renderTopbar();
      renderContent();
      return;
    }
    const detRow = target.closest<HTMLElement>('[data-det-pid]');
    if (detRow?.dataset.detPid) {
      selectedDetailPid = Number(detRow.dataset.detPid);
      renderTopbar();
      renderContent();
      return;
    }
    const svcRow = target.closest<HTMLElement>('[data-svc-name]');
    if (svcRow?.dataset.svcName) {
      selectedServiceName = svcRow.dataset.svcName;
      renderTopbar();
      renderContent();
      return;
    }
    const rowEl = target.closest<HTMLElement>('[data-row-id]');
    if (rowEl?.dataset.rowId) {
      const rid = rowEl.dataset.rowId;
      if (activeTab === 'history') selectedHistoryId = rid;
      else if (activeTab === 'startup') selectedStartupId = rid;
      else if (activeTab === 'users') selectedUserId = rid;
      renderTopbar();
      renderContent();
      return;
    }

    // Perf card click
    const cardBtn = target.closest<HTMLElement>('[data-perf-key]');
    if (cardBtn?.dataset.perfKey) {
      activePerfKey = cardBtn.dataset.perfKey as PerfResourceKey;
      if (summaryMode === 'perf-cards') summaryMode = 'none';
      renderChromeAndLayout();
      return;
    }

    // Settings expanders
    const expHead = target.closest<HTMLElement>('[data-set-exp]');
    if (expHead?.dataset.setExp) {
      const key = expHead.dataset.setExp as 'winMgmtExpanded' | 'otherOptsExpanded';
      settings[key] = !settings[key];
      persistPrefs();
      renderContent();
    }
  });

  // Settings selects & checkboxes
  tmShell.addEventListener('change', (e) => {
    const t = e.target as HTMLElement;
    if (t instanceof HTMLSelectElement && t.dataset.setSelect) {
      const key = t.dataset.setSelect;
      if (key === 'themePref') {
        settings.themePref = t.value as TmThemePref;
        applyTheme();
      } else if (key === 'defaultTab') {
        settings.defaultTab = t.value as Exclude<TmTabId, 'settings'>;
      } else if (key === 'updateSpeed') {
        settings.updateSpeed = t.value as TmUpdateSpeed;
      }
      persistPrefs();
    } else if (t instanceof HTMLInputElement && t.dataset.setChk) {
      const key = t.dataset.setChk as keyof TmSettingsState;
      if (typeof settings[key] === 'boolean') {
        (settings[key] as boolean) = t.checked;
        persistPrefs();
      }
    }
  });

  // Double click performance charts -> toggle Graph Summary View (tm_31)
  tmShell.addEventListener('dblclick', (e) => {
    const target = e.target as HTMLElement;
    if (activeTab === 'performance' && (target.closest('[data-perf-chart-area]') || target.closest('.w11-tm-perf-detail.is-graph-only'))) {
      summaryMode = summaryMode === 'graph-only' ? 'none' : 'graph-only';
      renderChromeAndLayout();
      return;
    }
    const cardBtn = target.closest<HTMLElement>('[data-perf-key]');
    if (cardBtn?.dataset.perfKey && summaryMode === 'perf-cards') {
      activePerfKey = cardBtn.dataset.perfKey as PerfResourceKey;
      summaryMode = 'none';
      renderChromeAndLayout();
    }
  });

  // Draggable performance splitter
  tmShell.addEventListener('pointerdown', (e) => {
    const splitter = (e.target as HTMLElement).closest<HTMLElement>('[data-tm-perf-splitter]');
    if (!splitter || e.button !== 0) return;
    const listEl = tmShell.querySelector<HTMLElement>('[data-tm-perf-list]');
    if (!listEl) return;
    e.preventDefault();
    splitter.classList.add('is-dragging');
    const startX = e.clientX;
    const startW = listEl.offsetWidth || sidebarWidth;
    const maxW = Math.min(440, Math.max(220, (contentEl.clientWidth || 720) - 320));
    const onMove = (me: PointerEvent) => {
      sidebarWidth = Math.max(172, Math.min(maxW, Math.round(startW + me.clientX - startX)));
      listEl.style.width = `${sidebarWidth}px`;
    };
    const onUp = () => {
      splitter.classList.remove('is-dragging');
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      window.removeEventListener('pointercancel', onUp);
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onUp);
  });

  // Context Menus across all tabs!
  tmShell.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    const t = e.target as HTMLElement;
    tipCtrl.hide();

    // 1. Processes column header picker (tm_13, tm_65)
    if (t.closest('[data-tm-col-hdr]') && activeTab === 'processes') {
      const items: TmMenuItem[] = TM_PROC_COLUMNS.map((c) => ({
        label: c.label,
        checked: visibleCols.has(c.key),
        checkedType: 'check',
        disabled: (c.key === 'npu' || c.key === 'npuEngine') && !hw.npu,
        onSelect: () => {
          if (visibleCols.has(c.key)) visibleCols.delete(c.key);
          else visibleCols.add(c.key);
          persistPrefs();
          renderContent();
        },
      }));
      items.push({ type: 'divider' }, { label: '资源值(V)', submenu: buildResourceValueMenu() });
      menuCtrl.openAt(e.clientX, e.clientY, items);
      return;
    }

    // 2. Processes row context menu (tm_08, tm_11)
    const procRow = t.closest<HTMLElement>('[data-proc-id]');
    if (procRow?.dataset.procId && activeTab === 'processes') {
      selectedProcId = procRow.dataset.procId;
      const node = findProcessById(selectedProcId);
      renderTopbar();
      renderContent();
      if (!node) return;
      const hasKids = Boolean(node.children && node.children.length > 0);
      const isExp = expandedProcIds.has(node.id);
      menuCtrl.openAt(e.clientX, e.clientY, [
        ...(hasKids
          ? [{
              label: isExp ? '折叠' : '展开',
              onSelect: () => { if (isExp) expandedProcIds.delete(node.id); else expandedProcIds.add(node.id); renderContent(); },
            }]
          : []),
        ...(node.appId ? [{ label: '切换到(T)', onSelect: () => shell.openApp(node.appId!) }] : []),
        {
          label: '结束任务(E)',
          onSelect: () => {
            if (node.id === 'tm-self' || node.id === 'tm-self-main') { shell.closeWindow(pid); return; }
            removeProcessByPid(node.pid);
            renderTopbar();
            renderContent();
          },
        },
        { label: '资源值(V)', submenu: buildResourceValueMenu() },
        { type: 'divider' },
        {
          label: '效能模式(M)',
          checked: node.status === 'efficiency',
          checkedType: 'check',
          disabled: node.group === 'win',
          onSelect: () => toggleEfficiencyForProcess(node),
        },
        { label: '创建内存转储文件(C)', onSelect: () => showTmModal(tmShell, { title: '正在转储进程', width: 380, bodyHtml: `<p class="w11-tm-modal-text">已创建 <strong>${esc(node.processName)}</strong> (${node.pid}) 的完全转储文件：<br/><code>C:\\Users\\admin\\AppData\\Local\\Temp\\${esc(node.processName)}.DMP</code></p>`, footerButtons: [{ label: '确定', primary: true, onClick: (_, close) => close() }] }) },
        { type: 'divider' },
        { label: '转到详细信息(G)', onSelect: () => { selectedDetailPid = node.pid; activeTab = 'details'; renderChromeAndLayout(); } },
        { label: '打开文件所在的位置(O)', onSelect: () => showTmModal(tmShell, { title: '文件位置', width: 410, bodyHtml: `<p class="w11-tm-modal-text">路径: <code>${esc(node.filePath)}</code></p>`, footerButtons: [{ label: '确定', primary: true, onClick: (_, close) => close() }] }) },
        { label: '在线搜索(S)', onSelect: () => window.open(`https://www.bing.com/search?q=${encodeURIComponent(node.processName)}`, '_blank', 'noopener') },
        { label: '属性(I)', onSelect: () => openPropertiesModal({ name: node.name, exe: node.processName, path: node.filePath, publisher: node.publisher, arch: node.arch, pid: node.pid }) },
      ]);
      return;
    }

    // 3. Performance cards & charts context menus (tm_34, tm_35, tm_38, tm_44)
    if (activeTab === 'performance') {
      const card = t.closest<HTMLElement>('[data-perf-key]');
      if (card) {
        activePerfKey = card.dataset.perfKey as PerfResourceKey;
        renderChromeAndLayout();
        openPerfCardMenu(e.clientX, e.clientY);
        return;
      }
      if (t.closest('[data-perf-chart-area], .w11-tm-perf-detail')) {
        openPerfChartMenu(e.clientX, e.clientY);
        return;
      }
    }

    // 4. Details row context menu (tm_66)
    const detRow = t.closest<HTMLElement>('[data-det-pid]');
    if (detRow?.dataset.detPid && activeTab === 'details') {
      selectedDetailPid = Number(detRow.dataset.detPid);
      const node = findProcessByPid(selectedDetailPid);
      renderTopbar();
      renderContent();
      if (!node) return;
      const priorities: TmPriority[] = ['实时', '高', '高于正常', '正常', '低于正常', '低'];
      menuCtrl.openAt(e.clientX, e.clientY, [
        { label: '结束任务(E)', onSelect: () => { removeProcessByPid(node.pid); renderTopbar(); renderContent(); } },
        { label: '结束进程树(T)', onSelect: () => { removeProcessByPid(node.pid); renderTopbar(); renderContent(); } },
        { type: 'divider' },
        { label: '效能模式(M)', checked: node.status === 'efficiency', checkedType: 'check', onSelect: () => toggleEfficiencyForProcess(node) },
        {
          label: '设置优先级(P)',
          submenu: priorities.map((pr) => ({
            label: pr,
            checked: node.priority === pr,
            checkedType: 'radio',
            onSelect: () => { node.priority = pr; renderContent(); },
          })),
        },
        { label: '设置相关性(F)', onSelect: () => openAffinityModal(node) },
        { type: 'divider' },
        { label: '分析等待链(A)', onSelect: () => showTmModal(tmShell, { title: '分析等待链', width: 380, bodyHtml: `<p class="w11-tm-modal-text">${esc(node.processName)} 正在正常运行，未在等待网络、I/O 或其他进程。</p>`, footerButtons: [{ label: '确定', primary: true, onClick: (_, close) => close() }] }) },
        { label: 'UAC 虚拟化(V)', checked: node.uacVirt === '已启用', checkedType: 'check', disabled: node.uacVirt === '不允许', onSelect: () => { node.uacVirt = node.uacVirt === '已启用' ? '已禁用' : '已启用'; } },
        { label: '创建内存转储文件(C)', onSelect: () => showTmModal(tmShell, { title: '正在转储进程', width: 380, bodyHtml: `<p class="w11-tm-modal-text">已创建 <code>${esc(node.processName)}.DMP</code>。</p>`, footerButtons: [{ label: '确定', primary: true, onClick: (_, close) => close() }] }) },
        { type: 'divider' },
        { label: '在线搜索(N)', onSelect: () => window.open(`https://www.bing.com/search?q=${encodeURIComponent(node.processName)}`, '_blank', 'noopener') },
        { label: '属性(R)', onSelect: () => openPropertiesModal({ name: node.name, exe: node.processName, path: node.filePath, publisher: node.publisher, arch: node.arch, pid: node.pid }) },
        ...(node.serviceNames?.length
          ? [{ label: '转到服务(S)', onSelect: () => { selectedServiceName = node.serviceNames![0]; activeTab = 'services'; renderChromeAndLayout(); } }]
          : []),
      ]);
      return;
    }

    // 5. Services row context menu
    const svcRow = t.closest<HTMLElement>('[data-svc-name]');
    if (svcRow?.dataset.svcName && activeTab === 'services') {
      selectedServiceName = svcRow.dataset.svcName;
      const svc = services.find((s) => s.name === selectedServiceName);
      renderTopbar();
      renderContent();
      if (!svc) return;
      menuCtrl.openAt(e.clientX, e.clientY, [
        { label: '开始(S)', disabled: svc.status === '正在运行', onSelect: () => { svc.status = '正在运行'; svc.pid = 2400 + Math.floor(Math.random() * 800); renderChromeAndLayout(); } },
        { label: '停止(O)', disabled: svc.status === '已停止', onSelect: () => { svc.status = '已停止'; svc.pid = null; renderChromeAndLayout(); } },
        { label: '重新启动(R)', onSelect: () => { svc.status = '正在运行'; svc.pid = svc.pid ?? 2400 + Math.floor(Math.random() * 800); renderChromeAndLayout(); } },
        { type: 'divider' },
        { label: '打开服务(V)', onSelect: () => showTmModal(tmShell, { title: '服务 (services.msc)', width: 380, bodyHtml: `<p class="w11-tm-modal-text">服务名: <strong>${esc(svc.name)}</strong><br/>状态: ${esc(svc.status)}<br/>说明: ${esc(svc.desc)}</p>`, footerButtons: [{ label: '确定', primary: true, onClick: (_, close) => close() }] }) },
        { label: '在线搜索(S)', onSelect: () => window.open(`https://www.bing.com/search?q=${encodeURIComponent(`Windows service ${svc.name}`)}`, '_blank', 'noopener') },
        { label: '转到详细信息(G)', disabled: svc.pid === null, onSelect: () => { if (svc.pid !== null) { selectedDetailPid = svc.pid; activeTab = 'details'; renderChromeAndLayout(); } } },
      ]);
      return;
    }
  });

  // Periodic UI cadence respecting updateSpeed ('high' 500ms, 'normal' 1000ms, 'low' 2000ms, 'paused')
  const refreshTimer = window.setInterval(() => {
    if (settings.updateSpeed === 'paused') return;
    const now = Date.now();
    const interval = settings.updateSpeed === 'high' ? 480 : settings.updateSpeed === 'low' ? 1950 : 950;
    if (now - lastTickAt < interval) return;
    lastTickAt = now;
    uptimeSec += 1;

    // Skip rebuilding Settings page if select dropdown is open, or list if user has a floating context menu open
    if (activeTab === 'settings') return;
    if (tmShell.querySelector('.w11-tm-menu-layer') || tmShell.querySelector('.w11-tm-modal-backdrop')) return;
    if (tmShell.matches(':hover') && (activeTab === 'processes' || activeTab === 'details' || activeTab === 'services') && document.activeElement !== titleSearchInput) {
      // Gently jitter CPU/memory numbers only on Performance or unpaused overview, keeping tables responsive
    }

    // Update throughput histories smoothly from snapshot
    diskWritePts = [...diskWritePts.slice(1), Math.max(2, Math.round((hw.storage.writeKb / 12) + Math.random() * 6))];
    netSendPts = [...netSendPts.slice(1), Math.max(1, Math.round(hw.network.sendKbps + Math.random() * 4))];
    syncProcessesWithLive();

    // Update process metrics slightly
    processes.forEach((p, idx) => {
      if (p.id === 'tm-self') p.cpu = +(0.6 + (hw.cpu.usagePct % 4) * 0.35).toFixed(1);
      else if (p.group === 'app' || idx % 3 === 0) p.cpu = Math.max(0, +(p.cpu + (Math.random() - 0.5) * 0.2).toFixed(1));
    });

    if (activeTab === 'performance') {
      renderContent();
    } else if (activeTab === 'processes' || activeTab === 'users') {
      renderContent();
    }
  }, 500);

  syncProcessesWithLive();
  renderChromeAndLayout();

  return {
    onThemeChange: () => applyTheme(),
    onWindowsChanged: () => {
      syncProcessesWithLive();
      if (activeTab === 'processes') renderContent();
    },
    onHardwareUpdate: (next) => {
      hw = next;
      if (settings.updateSpeed !== 'paused' && activeTab === 'performance' && !tmShell.querySelector('.w11-tm-menu-layer')) {
        renderContent();
      }
    },
    destroy: () => {
      window.clearInterval(refreshTimer);
      menuCtrl.destroy();
      tipCtrl.destroy();
      titleSearchWrap?.remove();
    },
  };
}

export const taskManagerApp = {
  mount: mountTaskManager,
};
