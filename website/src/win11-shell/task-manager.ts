import type { WindowInstance } from './types';
import { getAppIconSvg } from './icons';
import { collectStorageProbeInfo, type DeviceProbeReport, type StorageProbeInfo } from './device-probe';
import { renderTmGlyph } from './tm-fluent-icons';
import type { CpuCacheSpec, FieldEstimation, HardwareProfile, UnknownReason } from 'hw-profiler';

export type TaskManagerTab = 'processes' | 'performance';
export type PerfResourceKey = 'cpu' | 'memory' | 'disk' | 'wifi' | 'npu' | 'gpu';

export interface TaskManagerMountOptions {
  container: HTMLElement;
  getWindows: () => WindowInstance[];
  getInitialReport: () => DeviceProbeReport | null;
  onReportUpdated: (report: DeviceProbeReport) => void;
  onCloseWindow: (pid: number) => void;
  onOpenNewTask: () => void;
}

export interface TaskManagerController {
  refreshWindows: () => void;
  setReport: (report: DeviceProbeReport) => void;
  destroy: () => void;
}

const HISTORY_LEN = 60;
const PERF_LIST_MIN_WIDTH = 172;
const PERF_LIST_DEFAULT_WIDTH = 248;
const PERF_DETAIL_MIN_WIDTH = 380;

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function escapeAttr(text: string): string {
  return escapeHtml(text).replace(/"/g, '&quot;');
}

const UNKNOWN_REASON_LABEL: Record<UnknownReason, string> = {
  hardware_decoupled: '浏览器沙箱不可枚举的解耦硬件，当前为模拟占位',
  discrete_gpu_occluded: '独立显卡遮蔽核显 PCI ID，按信息边界不反推宿主平台',
  insecure_context: '当前非 HTTPS/localhost 安全上下文，高熵信号受限',
  privacy_masked: '浏览器或软件渲染器屏蔽了底层硬件指纹',
  browser_unsupported: '当前浏览器未暴露此 API 信号',
  kb_miss: '静态指纹库未收录匹配条目，当前为模拟占位',
};

function fake(text: string | number, reasonOrHint?: UnknownReason | string): string {
  const hint =
    reasonOrHint && reasonOrHint in UNKNOWN_REASON_LABEL
      ? UNKNOWN_REASON_LABEL[reasonOrHint as UnknownReason]
      : reasonOrHint || '浏览器沙箱无法获取，当前为模拟数据';
  return `<span class="w11-tm-fake" title="${escapeAttr(hint)}">${escapeHtml(String(text))}</span>`;
}

function inferred(text: string | number, tooltip = '静态硬件指纹库推理'): string {
  return `<span class="w11-tm-inferred" title="${escapeAttr(tooltip)}">${escapeHtml(String(text))}</span>`;
}

function realVal(text: string | number, tooltip?: string): string {
  const safe = escapeHtml(String(text));
  return tooltip ? `<span title="${escapeAttr(tooltip)}">${safe}</span>` : safe;
}

function formatEstimation<T>(
  est: FieldEstimation<T> | undefined,
  formatter: (v: T) => string,
  fallbackText: string,
): string {
  if (!est) return fake(fallbackText);
  if (est.status === 'real') {
    const tip = est.capped ? '浏览器实测值（在隐私上限处封顶）' : undefined;
    return realVal(formatter(est.value), tip);
  }
  if (est.status === 'inferred_exact') {
    return inferred(formatter(est.value));
  }
  if (est.status === 'inferred_candidates' && est.candidates.length > 0) {
    const top = formatter(est.candidates[0].value);
    return top ? inferred(top) : fake(fallbackText);
  }
  return fake(fallbackText, est.status === 'unknown' ? est.reason : undefined);
}

function formatNumericText(
  est: FieldEstimation<number> | undefined,
  decimals: number,
  suffix: string,
  fallbackText: string,
): string {
  const fmt = (n: number) => (decimals > 0 ? n.toFixed(decimals) : String(Math.round(n)));
  if (!est || est.status === 'unknown') return fallbackText;
  if (est.status === 'real' || est.status === 'inferred_exact') {
    return `${fmt(est.value)}${suffix}`;
  }
  if (est.status === 'inferred_candidates' && est.candidates.length > 0) {
    return `${fmt(est.candidates[0].value)}${suffix}`;
  }
  return fallbackText;
}

function formatNumericRange(
  est: FieldEstimation<number> | undefined,
  decimals: number,
  suffix: string,
  fallbackText: string,
): string {
  if (!est) return fake(fallbackText);
  const fmt = (n: number) => (decimals > 0 ? n.toFixed(decimals) : String(Math.round(n)));
  if (est.status === 'real') return `${fmt(est.value)}${suffix}`;
  if (est.status === 'inferred_exact') {
    return inferred(`${fmt(est.value)}${suffix}`);
  }
  if (est.status === 'inferred_candidates' && est.candidates.length > 0) {
    return inferred(`${fmt(est.candidates[0].value)}${suffix}`);
  }
  return fake(fallbackText, est.status === 'unknown' ? est.reason : undefined);
}

function formatCacheSizeKB(kb: number): string {
  if (kb >= 1024) return `${(kb / 1024).toFixed(1)} MB`;
  return `${kb} KB`;
}

function pickConsensusCache(est: FieldEstimation<CpuCacheSpec> | undefined): {
  l1?: string;
  l2?: string;
  l3?: string;
  tooltip?: string;
} {
  if (!est || est.status === 'unknown' || est.status === 'real') return {};
  const spec = est.status === 'inferred_exact' ? est.value : est.candidates[0]?.value;
  if (!spec) return {};
  return {
    l1: typeof spec.l1KB === 'number' && spec.l1KB > 0 ? formatCacheSizeKB(spec.l1KB) : undefined,
    l2: typeof spec.l2KB === 'number' && spec.l2KB > 0 ? formatCacheSizeKB(spec.l2KB) : undefined,
    l3: typeof spec.l3KB === 'number' && spec.l3KB > 0 ? formatCacheSizeKB(spec.l3KB) : undefined,
    tooltip: '静态硬件指纹库推理',
  };
}

function cleanGpuName(renderer: string): string {
  if (!renderer || renderer === '不支持 WebGL') return 'Microsoft Basic Render Driver';
  const angleMatch = renderer.match(/^ANGLE\s*\(([^,]+),\s*(.+?)(?:,\s*(?:OpenGL|Direct3D|Vulkan|Metal).*)?\)$/i);
  let name = angleMatch ? angleMatch[2].trim() : renderer.trim();
  name = name
    .replace(/\s*\(0x[0-9a-f]+\)/gi, '')
    .replace(/\s+Direct3D\d+.*$/i, '')
    .replace(/\s+vs_\d+_\d+.*$/i, '')
    .trim();
  return name || renderer;
}

function formatUptime(ms: number): string {
  const totalSec = Math.max(0, Math.floor(ms / 1000));
  const days = Math.floor(totalSec / 86400);
  const hours = Math.floor((totalSec % 86400) / 3600);
  const mins = Math.floor((totalSec % 3600) / 60);
  const secs = totalSec % 60;
  return `${days}:${String(hours).padStart(2, '0')}:${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
}

function seedSpikyHistory(base: number, amp: number, spikeChance = 0.2): number[] {
  const arr: number[] = [];
  let burstLeft = 0;
  let burstLevel = base;
  for (let i = 0; i < HISTORY_LEN; i++) {
    if (burstLeft <= 0 && Math.random() < spikeChance) {
      burstLeft = 1 + Math.floor(Math.random() * 2);
      burstLevel = base + amp * (0.45 + Math.random() * 0.6);
    }
    let val: number;
    if (burstLeft > 0) {
      burstLeft -= 1;
      val = burstLevel * (0.75 + Math.random() * 0.3);
    } else {
      val = Math.max(0.8, base * (0.35 + Math.random() * 0.85));
    }
    arr.push(Math.max(1, Math.min(95, Math.round(val))));
  }
  return arr;
}

function buildChartSvg(
  values: number[],
  stroke: string,
  fill: string,
  opts: { grid?: boolean; dashed?: boolean; topLineY?: number; vCols?: number; hRows?: number } = {},
): string {
  const w = 240;
  const h = 80;
  const step = w / Math.max(1, values.length - 1);
  const pts = values.map((v, i) => {
    const x = +(i * step).toFixed(1);
    const pct = Math.max(0.6, Math.min(98.5, v));
    const y = +(h - pct * (h / 100)).toFixed(1);
    return `${x},${y}`;
  });
  const linePath = `M ${pts.join(' L ')}`;
  const areaPath = `${linePath} L ${w},${h} L 0,${h} Z`;

  let grid = '';
  if (opts.grid !== false) {
    const hRows = opts.hRows ?? 10;
    const vCols = opts.vCols ?? 12;
    const lines: string[] = [];
    for (let r = 1; r < hRows; r++) {
      const y = +((h * r) / hRows).toFixed(2);
      lines.push(`<line x1="0" y1="${y}" x2="${w}" y2="${y}" />`);
    }
    for (let c = 1; c < vCols; c++) {
      const x = +((w * c) / vCols).toFixed(2);
      lines.push(`<line x1="${x}" y1="0" x2="${x}" y2="${h}" />`);
    }
    grid = `<g class="w11-tm-grid-lines">${lines.join('')}</g>`;
  }

  const refLine =
    typeof opts.topLineY === 'number'
      ? `<line x1="0" y1="${opts.topLineY}" x2="${w}" y2="${opts.topLineY}" stroke="${stroke}" stroke-width="1" opacity="0.85" vector-effect="non-scaling-stroke" />`
      : '';

  const dashAttr = opts.dashed ? 'stroke-dasharray="2.5 2"' : '';

  return `<svg viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" class="w11-tm-svg">
    ${grid}
    ${refLine}
    <path d="${areaPath}" fill="${fill}" />
    <path d="${linePath}" fill="none" stroke="${stroke}" stroke-width="1" stroke-linejoin="round" ${dashAttr} vector-effect="non-scaling-stroke" />
  </svg>`;
}

interface ProcessRow {
  id: string;
  pid: number;
  name: string;
  iconHtml: string;
  isRealWindow: boolean;
  minimized: boolean;
  cpu: number;
  memMb: number;
  diskMbps: number;
  netMbps: number;
  gpuPct: number;
}

export function mountTaskManager(options: TaskManagerMountOptions): TaskManagerController {
  const { container, getWindows, onCloseWindow, onOpenNewTask } = options;

  let activeTab: TaskManagerTab = 'performance';
  let activePerf: PerfResourceKey = 'cpu';
  let navExpanded = false;
  let searchQuery = '';
  let sortCol: 'name' | 'cpu' | 'mem' | 'disk' | 'net' | 'gpu' = 'cpu';
  let sortDesc = true;
  let selectedProcessId: string | null = null;
  let perfSidebarWidth = PERF_LIST_DEFAULT_WIDTH;
  let splitterDrag: {
    startX: number;
    startW: number;
    scale: number;
    maxW: number;
    listEl: HTMLElement;
    splitterEl: HTMLElement;
  } | null = null;
  let report: DeviceProbeReport | null = options.getInitialReport();
  let liveStorage: StorageProbeInfo | null = report?.storage ?? null;
  let storageInFlight = false;
  let sampleTick = 0;
  let destroyed = false;

  async function refreshStorageProbe() {
    if (storageInFlight || destroyed) return;
    storageInFlight = true;
    try {
      const nextStorage = await collectStorageProbeInfo();
      if (destroyed) return;
      liveStorage = nextStorage;
      if (report) {
        report = { ...report, storage: nextStorage };
      }
      render();
    } finally {
      storageInFlight = false;
    }
  }

  let coreHistories: number[][] = [];
  const cpuHistory: number[] = seedSpikyHistory(9, 28, 0.25);
  let procCount = 328 + Math.round((Math.random() - 0.5) * 6);
  let threadCount = 5719 + Math.round((Math.random() - 0.5) * 90);
  let handleCount = 166474 + Math.round((Math.random() - 0.5) * 1600);
  let cpuFreqOffset = 0;
  const memHistory: number[] = Array.from({ length: HISTORY_LEN }, (_, i) => +(77 + ((i % 7) - 3) * 0.4).toFixed(1));
  const diskActiveHistory: number[] = seedSpikyHistory(2, 16, 0.12);
  const diskRateHistory: number[] = seedSpikyHistory(16, 52, 0.24);
  const wifiRxHistory: number[] = seedSpikyHistory(10, 74, 0.16);
  const npuHistory: number[] = Array(HISTORY_LEN).fill(0);
  const npuMemHistory: number[] = Array(HISTORY_LEN).fill(0);
  const gpu3dHistory: number[] = seedSpikyHistory(18, 38, 0.28);
  const gpuZeroHistory: number[] = Array(HISTORY_LEN).fill(0);
  const gpuMemHistory: number[] = Array.from({ length: HISTORY_LEN }, (_, i) => +(26 + (i % 3) * 0.8).toFixed(1));

  function ensureCores(cores: number) {
    const count = Math.max(1, Math.min(32, cores));
    if (coreHistories.length === count) return;
    coreHistories = Array.from({ length: count }, (_, idx) => {
      if (idx < Math.ceil(count * 0.42)) return seedSpikyHistory(16, 48, 0.24);
      if (idx < Math.ceil(count * 0.67)) return seedSpikyHistory(6, 36, 0.16);
      return seedSpikyHistory(2, 26, 0.09);
    });
  }

  ensureCores(report?.cpu.hardwareConcurrency ?? navigator.hardwareConcurrency ?? 8);

  function pushSample() {
    const cores = report?.cpu.hardwareConcurrency ?? navigator.hardwareConcurrency ?? 8;
    ensureCores(cores);

    let sum = 0;
    coreHistories.forEach((arr, idx) => {
      const prev = arr[arr.length - 1] ?? 5;
      let target: number;
      if (idx < Math.ceil(cores * 0.42)) {
        target = Math.random() < 0.24 ? 26 + Math.random() * 44 : 5 + Math.random() * 18;
      } else if (idx < Math.ceil(cores * 0.67)) {
        target = Math.random() < 0.16 ? 18 + Math.random() * 34 : 2 + Math.random() * 9;
      } else {
        target = Math.random() < 0.1 ? 10 + Math.random() * 26 : 1 + Math.random() * 3;
      }
      const next = Math.max(1, Math.min(95, Math.round(prev * 0.22 + target * 0.78)));
      arr.push(next);
      if (arr.length > HISTORY_LEN) arr.shift();
      sum += next;
    });

    cpuHistory.push(Math.round(sum / coreHistories.length));
    if (cpuHistory.length > HISTORY_LEN) cpuHistory.shift();

    procCount = Math.max(316, Math.min(342, procCount + Math.round((Math.random() - 0.5) * 2)));
    threadCount = Math.max(5540, Math.min(5910, threadCount + Math.round((Math.random() - 0.5) * 44)));
    handleCount = Math.max(163200, Math.min(169800, handleCount + Math.round((Math.random() - 0.5) * 760)));
    cpuFreqOffset = Math.max(-0.04, Math.min(0.03, +(cpuFreqOffset + (Math.random() - 0.5) * 0.025).toFixed(3)));

    const memPrev = memHistory[memHistory.length - 1] ?? 78;
    memHistory.push(Math.max(58, Math.min(86, +(memPrev + (Math.random() - 0.5) * 0.8).toFixed(1))));
    if (memHistory.length > HISTORY_LEN) memHistory.shift();

    diskActiveHistory.push(Math.max(1, Math.min(26, Math.round(Math.random() < 0.16 ? 5 + Math.random() * 14 : 1 + Math.random() * 2))));
    if (diskActiveHistory.length > HISTORY_LEN) diskActiveHistory.shift();

    diskRateHistory.push(Math.max(2, Math.min(94, Math.round(Math.random() < 0.22 ? 25 + Math.random() * 64 : 4 + Math.random() * 16))));
    if (diskRateHistory.length > HISTORY_LEN) diskRateHistory.shift();

    wifiRxHistory.push(Math.max(2, Math.min(96, Math.round(Math.random() < 0.16 ? 48 + Math.random() * 48 : 3 + Math.random() * 15))));
    if (wifiRxHistory.length > HISTORY_LEN) wifiRxHistory.shift();

    const gPrev = gpu3dHistory[gpu3dHistory.length - 1] ?? 18;
    gpu3dHistory.push(Math.max(4, Math.min(58, Math.round(gPrev * 0.4 + (10 + Math.random() * 34) * 0.6))));
    if (gpu3dHistory.length > HISTORY_LEN) gpu3dHistory.shift();

    gpuZeroHistory.push(0);
    if (gpuZeroHistory.length > HISTORY_LEN) gpuZeroHistory.shift();

    gpuMemHistory.push(27);
    if (gpuMemHistory.length > HISTORY_LEN) gpuMemHistory.shift();

    npuHistory.push(0);
    if (npuHistory.length > HISTORY_LEN) npuHistory.shift();

    npuMemHistory.push(0);
    if (npuMemHistory.length > HISTORY_LEN) npuMemHistory.shift();
  }

  function getHwProfile(): HardwareProfile | undefined {
    return report?.hardwareProfile;
  }

  function getMemoryInfo() {
    const hp = getHwProfile();
    const memEst = hp?.memory.browserDeviceMemoryGiB;
    const realDeviceGb =
      report && typeof report.memory.deviceMemoryGiB === 'number'
        ? report.memory.deviceMemoryGiB
        : memEst?.status === 'real'
          ? memEst.value
          : null;
    const capped = memEst?.status === 'real' ? Boolean(memEst.capped) : realDeviceGb === 8;
    const totalGb = realDeviceGb ?? 16;
    const usableGb = totalGb;
    const fmtGb = (n: number) => (Number.isInteger(n) ? `${n}.0 GB` : `${n} GB`);
    const capText = fmtGb(totalGb);
    const capTip = capped
      ? `navigator.deviceMemory 在 ${capText} 隐私上限处封顶（实际物理内存 ≥ ${capText}，按读数 ${capText} 展示）`
      : `navigator.deviceMemory 实测档位值：${capText}（离散 2 的幂分桶，按整数容量展示，非精确物理读取）`;
    const pct = Math.round(memHistory[memHistory.length - 1] ?? 78);
    const usedGb = +((usableGb * pct) / 100).toFixed(1);
    const availGb = +(usableGb - usedGb).toFixed(1);
    const capHtml = realDeviceGb ? realVal(capText, capTip) : fake(capText);
    return {
      realDeviceGb,
      capped,
      totalGb,
      usableGb,
      usedGb,
      availGb,
      pct,
      headerCapHtml: capHtml,
      chartTopHtml: capHtml,
      sidebarCapHtml: capHtml,
    };
  }

  function formatStorageBytes(bytes: number): string {
    if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GiB`;
    if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(2)} MB`;
    if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${bytes} B`;
  }

  function getDiskStorageDisplay() {
    const st = liveStorage ?? report?.storage ?? null;
    if (!st) {
      const pending = realVal('探测中...');
      return {
        sidebarLine1: pending,
        sidebarUsageHtml: pending,
        headerHtml: pending,
        quotaHtml: pending,
        usageHtml: pending,
        persistHtml: pending,
        opfsFsHtml: pending,
      };
    }

    const opfsLabel = st.opfsReady
      ? 'OPFS 可读写'
      : st.opfsSupported
        ? 'OPFS 受限'
        : '不支持 OPFS';
    const fsLabel = st.fsAccessSupported ? '支持目录挂载' : '无目录选择器';
    const reason = !st.isSecureContext ? 'insecure_context' : 'browser_unsupported';

    if (!st.supported || st.quotaBytes === null) {
      const errTip = st.error || UNKNOWN_REASON_LABEL[reason];
      return {
        sidebarLine1: fake('配额不可用', errTip),
        sidebarUsageHtml: fake('无存储配额', errTip),
        headerHtml: fake('站点存储不可用', errTip),
        quotaHtml: fake(st.error || '浏览器未暴露配额', reason),
        usageHtml: fake('不可读', reason),
        persistHtml: fake('未支持', reason),
        opfsFsHtml: realVal(`${opfsLabel} · ${fsLabel}`),
      };
    }

    const quotaGiBStr = formatStorageBytes(st.quotaBytes);
    const quotaTip =
      `navigator.storage.estimate().quota 实测: ${quotaGiBStr} / ${st.quotaGB ?? '?'} GB (${st.quotaBytes.toLocaleString()} 字节)。` +
      `注意: 现代浏览器常对普通站点配额做隐私封顶/脱敏，并非宿主物理硬盘容量。`;

    const usageBytes = st.usageBytes ?? 0;
    const usageStr = formatStorageBytes(usageBytes);
    const pctStr =
      st.quotaBytes > 0 ? ` (${((usageBytes / st.quotaBytes) * 100).toFixed(4)}%)` : '';
    const detailEntries = st.usageDetails
      ? Object.entries(st.usageDetails)
          .map(([k, v]) => `${k}: ${formatStorageBytes(v)}`)
          .join(', ')
      : '';
    const usageTip =
      `navigator.storage.estimate().usage 实测: ${usageBytes.toLocaleString()} 字节` +
      (detailEntries ? `（明细: ${detailEntries}）` : '');

    const persistTip =
      st.persisted === true
        ? 'navigator.storage.persisted() = true：站点数据已获持久化保护'
        : st.persisted === false
          ? 'navigator.storage.persisted() = false：默认临时配额（磁盘紧张时可能被浏览器回收）'
          : '当前浏览器未暴露 navigator.storage.persisted()';

    const persistHtml =
      st.persisted === true
        ? realVal('已授权持久', persistTip)
        : st.persisted === false
          ? realVal('临时 (Best-Effort)', persistTip)
          : fake('未支持', 'browser_unsupported');

    const opfsTip = `OPFS (navigator.storage.getDirectory): ${opfsLabel}；File System Access API: ${fsLabel}`;

    return {
      sidebarLine1: realVal(`配额 ${quotaGiBStr}`, quotaTip),
      sidebarUsageHtml: realVal(`已用 ${usageStr}`, usageTip),
      headerHtml: realVal(`浏览器站点配额 (${opfsLabel})`, `${quotaTip}\n${opfsTip}`),
      quotaHtml: realVal(`${quotaGiBStr} (${st.quotaGB ?? '?'} GB)`, quotaTip),
      usageHtml: realVal(`${usageStr}${pctStr}`, usageTip),
      persistHtml,
      opfsFsHtml: realVal(`${opfsLabel} · ${fsLabel}`, opfsTip),
    };
  }

  function extractNumericEstimation(est: FieldEstimation<number> | undefined): number | null {
    if (!est || est.status === 'unknown') return null;
    if (est.status === 'real' || est.status === 'inferred_exact') return est.value;
    if (est.status === 'inferred_candidates' && est.candidates.length > 0) {
      return est.candidates[0].value;
    }
    return null;
  }

  function extractBrandClockGhz(hp: HardwareProfile | undefined): number | null {
    const est = hp?.cpu.winBrandString;
    const text =
      est?.status === 'real' || est?.status === 'inferred_exact'
        ? est.value
        : est?.status === 'inferred_candidates' && est.candidates.length > 0
          ? est.candidates[0].value
          : '';
    const match = text.match(/@\s*(\d+(?:\.\d+)?)\s*GHz/i);
    return match ? Number(match[1]) : null;
  }

  function getCpuClockGhz(cpuPct: number, hp: HardwareProfile | undefined): string {
    const skuEst = hp?.cpu.skuCandidates;
    const topSku =
      skuEst?.status === 'inferred_exact'
        ? skuEst.value
        : skuEst?.status === 'inferred_candidates' && skuEst.candidates.length > 0
          ? skuEst.candidates[0].value
          : undefined;
    const base = topSku?.baseClockGHz ?? extractNumericEstimation(hp?.cpu.baseClockGHz) ?? 3.4;
    const brandClk = extractBrandClockGhz(hp);
    const boost =
      typeof topSku?.boostClockGHz === 'number' && topSku.boostClockGHz > 0.2
        ? topSku.boostClockGHz
        : typeof brandClk === 'number' && brandClk > base
          ? brandClk
          : base;
    const maxClk = Math.max(base, boost);
    const hasTurbo = maxClk > base + 0.01;
    const floorClk = hasTurbo ? +(base * 0.86).toFixed(2) : +(maxClk * 0.94).toFixed(2);
    const load = Math.max(0, Math.min(1, cpuPct / 100));
    const ratio = hasTurbo ? Math.pow(load, 0.72) : 0.58 + 0.4 * load;
    const raw = floorClk + (maxClk - floorClk) * ratio + cpuFreqOffset * maxClk * 0.12;
    return Math.max(floorClk, Math.min(maxClk, +raw.toFixed(2))).toFixed(2);
  }

  function getCpuLabelHtml(): string {
    const hp = getHwProfile();
    const brandEst = hp?.cpu.winBrandString;
    if (brandEst) {
      if (brandEst.status === 'real') return realVal(brandEst.value);
      if (brandEst.status === 'inferred_exact') return realVal(brandEst.value, '静态硬件指纹库推理');
      if (brandEst.status === 'inferred_candidates' && brandEst.candidates.length > 0) {
        return realVal(brandEst.candidates[0].value, '静态硬件指纹库推理');
      }
    }

    const modelEst = hp?.cpu.model;
    if (modelEst && (modelEst.status === 'inferred_exact' || modelEst.status === 'inferred_candidates')) {
      const modelText =
        modelEst.status === 'inferred_exact' ? modelEst.value : modelEst.candidates[0]?.value ?? '';
      const skuEst = hp?.cpu.skuCandidates;
      const topSku =
        skuEst?.status === 'inferred_exact'
          ? skuEst.value
          : skuEst?.status === 'inferred_candidates' && skuEst.candidates.length > 0
            ? skuEst.candidates[0].value
            : undefined;
      const clkText =
        typeof topSku?.boostClockGHz === 'number' && topSku.boostClockGHz > 0.2
          ? `${topSku.boostClockGHz.toFixed(2)} GHz`
          : formatNumericText(hp?.cpu.baseClockGHz, 2, ' GHz', '3.40 GHz');
      return realVal(`${modelText} @ ${clkText}`, '静态硬件指纹库推理');
    }

    const cores = report?.cpu.hardwareConcurrency ?? navigator.hardwareConcurrency ?? 8;
    const ua = report?.context.uaData;
    const arch =
      typeof ua === 'object' && ua !== null && ua.architecture
        ? `${ua.architecture.toUpperCase()}${ua.bitness || ''}`
        : report?.context.platformLegacy || '';
    const realPart = `${arch ? `${arch} ` : ''}${cores}-core`;
    return realVal(`${realPart} @ 3.40 GHz`);
  }

  function getGpuInfoHtml(): { sidebarHtml: string; detailHtml: string } {
    const hp = getHwProfile();
    const rawName = report?.gpu.webglRenderer ? cleanGpuName(report.gpu.webglRenderer) : '';
    const gpuEst = hp?.gpu.name;
    const pciTip =
      hp?.gpu.pciDeviceIdHex.status === 'real'
        ? `PCI ID: ${hp.gpu.pciDeviceIdHex.value}${hp.gpu.architecture.status === 'inferred_exact' ? ` · ${hp.gpu.architecture.value}` : ''}`
        : undefined;

    if (rawName && !/^Microsoft Basic Render Driver$/i.test(rawName)) {
      const html = realVal(rawName, pciTip);
      return { sidebarHtml: html, detailHtml: html };
    }
    if (gpuEst && (gpuEst.status === 'inferred_exact' || gpuEst.status === 'inferred_candidates')) {
      const html = formatEstimation(gpuEst, (s) => s, 'GPU');
      return { sidebarHtml: html, detailHtml: html };
    }
    if (rawName) {
      return { sidebarHtml: realVal(rawName, '软件渲染或基础驱动'), detailHtml: realVal(rawName, '软件渲染或基础驱动') };
    }
    return { sidebarHtml: fake('GPU'), detailHtml: fake('GPU') };
  }

  function getNpuDisplayInfo(): { sidebarHtml: string; detailHtml: string } {
    const hp = getHwProfile();
    const npuEst = hp?.npu.hardware;
    const webnnEst = hp?.npu.webnnNpuSupported;
    const webnnNote =
      webnnEst?.status === 'real'
        ? ` · WebNN NPU: ${webnnEst.value ? '已就绪' : '未就绪'}`
        : '';

    if (npuEst && (npuEst.status === 'inferred_exact' || npuEst.status === 'inferred_candidates')) {
      const npuSpec = npuEst.status === 'inferred_exact' ? npuEst.value : npuEst.candidates[0]?.value;
      if (npuSpec) {
        const topsText = typeof npuSpec.topsPeak === 'number' ? ` (${npuSpec.topsPeak} TOPS)` : '';
        const tip = `静态硬件指纹库推理${topsText}${webnnNote}`;
        if (npuSpec.winDisplayName) {
          const html = inferred(npuSpec.winDisplayName, tip);
          return { sidebarHtml: html, detailHtml: html };
        }
        const npuNameHtml = inferred(npuSpec.name, tip);
        const cpuEst = hp?.cpu.model;
        let detailPrefix = '';
        if (cpuEst && (cpuEst.status === 'inferred_exact' || cpuEst.status === 'inferred_candidates')) {
          const cpuText = cpuEst.status === 'inferred_exact' ? cpuEst.value : cpuEst.candidates[0]?.value;
          if (cpuText) detailPrefix = `${inferred(cpuText, tip)} - `;
        }
        return {
          sidebarHtml: npuNameHtml,
          detailHtml: `${detailPrefix}${npuNameHtml}`,
        };
      }
    }

    if (webnnEst?.status === 'real' && webnnEst.value) {
      return {
        sidebarHtml: realVal('WebNN NPU', '通过 navigator.ml.createContext({ deviceType: "npu" }) 实测支持'),
        detailHtml: realVal('WebNN NPU', '通过 navigator.ml.createContext({ deviceType: "npu" }) 实测支持'),
      };
    }

    const reason = npuEst?.status === 'unknown' ? npuEst.reason : undefined;
    return {
      sidebarHtml: fake('未检出 NPU (模拟占位)', reason),
      detailHtml: fake('NPU 模拟占位 (浏览器无法反推或无内置 NPU)', reason),
    };
  }

  function buildProcessRows(): ProcessRow[] {
    const wins = getWindows();
    const cpuNow = cpuHistory[cpuHistory.length - 1] ?? 8;
    const rows: ProcessRow[] = wins.map((w, idx) => ({
      id: `win-${w.pid}`,
      pid: w.pid,
      name: w.title,
      iconHtml: getAppIconSvg(w.iconKey, 16),
      isRealWindow: true,
      minimized: w.minimized,
      cpu: w.minimized ? 0 : +(0.4 + ((idx * 3) % 5) * 0.4).toFixed(1),
      memMb: +(48.5 + idx * 24.3 + (w.appId === 'exhyperv' ? 140 : 0)).toFixed(1),
      diskMbps: 0.1,
      netMbps: 0,
      gpuPct: w.minimized ? 0 : +(0.2 + idx * 0.6).toFixed(1),
    }));

    const simSystem: ProcessRow[] = [
      {
        id: 'sys-system',
        pid: 4,
        name: 'System',
        iconHtml: getAppIconSvg('settings', 16),
        isRealWindow: false,
        minimized: false,
        cpu: +(1.5 + (cpuNow % 3) * 0.4).toFixed(1),
        memMb: 0.1,
        diskMbps: 1.4,
        netMbps: 0,
        gpuPct: 0,
      },
      {
        id: 'sys-dwm',
        pid: 1024,
        name: '桌面窗口管理器',
        iconHtml: getAppIconSvg('this-pc', 16),
        isRealWindow: false,
        minimized: false,
        cpu: +(2.4 + (cpuNow % 4) * 0.5).toFixed(1),
        memMb: 94.0,
        diskMbps: 0.1,
        netMbps: 0,
        gpuPct: 6.4,
      },
      {
        id: 'sys-chrome',
        pid: 8820,
        name: 'Google Chrome (18)',
        iconHtml: getAppIconSvg('browser', 16),
        isRealWindow: false,
        minimized: false,
        cpu: +(1.1 + (cpuNow % 3) * 0.3).toFixed(1),
        memMb: 961.3,
        diskMbps: 0.1,
        netMbps: 0.1,
        gpuPct: 1.4,
      },
    ];

    const all = [...rows, ...simSystem].filter((r) =>
      searchQuery.trim()
        ? r.name.toLowerCase().includes(searchQuery.trim().toLowerCase()) || String(r.pid).includes(searchQuery.trim())
        : true,
    );

    all.sort((a, b) => {
      let diff = 0;
      if (sortCol === 'name') diff = a.name.localeCompare(b.name, 'zh-CN');
      else if (sortCol === 'cpu') diff = a.cpu - b.cpu;
      else if (sortCol === 'mem') diff = a.memMb - b.memMb;
      else if (sortCol === 'disk') diff = a.diskMbps - b.diskMbps;
      else if (sortCol === 'net') diff = a.netMbps - b.netMbps;
      else if (sortCol === 'gpu') diff = a.gpuPct - b.gpuPct;
      return sortDesc ? -diff : diff;
    });

    return all;
  }

  function heatColor(ratio: number): string {
    const alpha = (0.06 + Math.max(0, Math.min(1, ratio)) * 0.5).toFixed(2);
    return `rgba(0, 120, 212, ${alpha})`;
  }

  function renderPerfSidebar(): string {
    const hp = getHwProfile();
    const cpuNow = cpuHistory[cpuHistory.length - 1] ?? 6;
    const ghz = getCpuClockGhz(cpuNow, hp);
    const mem = getMemoryInfo();
    const diskNow = diskActiveHistory[diskActiveHistory.length - 1] ?? 1;
    const gpuNow = gpu3dHistory[gpu3dHistory.length - 1] ?? 18;
    const gpuInfo = getGpuInfoHtml();
    const npuInfo = getNpuDisplayInfo();
    const diskInfo = getDiskStorageDisplay();
    const conn = report?.network.connection;
    const isWifiReal = typeof conn === 'object' && conn !== null && conn.type === 'wifi';

    const cards: Array<{ key: PerfResourceKey; title: string; line1: string; line2?: string; svg: string; cls: string }> = [
      {
        key: 'cpu',
        title: 'CPU',
        line1: realVal(`${cpuNow}% ${ghz} GHz`),
        svg: buildChartSvg(cpuHistory, '#2c88b0', 'rgba(17, 125, 187, 0.24)', { grid: false }),
        cls: 'is-cpu',
      },
      {
        key: 'memory',
        title: '内存',
        line1: `${fake(`${mem.usedGb}`)}/${mem.sidebarCapHtml} ${fake(`(${mem.pct}%)`)}`,
        svg: buildChartSvg(memHistory, '#4f86d6', 'rgba(59, 130, 246, 0.28)', { grid: false }),
        cls: 'is-mem',
      },
      {
        key: 'disk',
        title: '磁盘 0 (C:)',
        line1: diskInfo.sidebarLine1,
        line2: `${diskInfo.sidebarUsageHtml} · ${fake(`${diskNow}%`)}`,
        svg: buildChartSvg(diskActiveHistory, '#6db33f', 'rgba(109, 179, 63, 0.2)', { grid: false }),
        cls: 'is-disk',
      },
      {
        key: 'wifi',
        title: 'Wi-Fi',
        line1: isWifiReal ? 'WLAN' : fake('WLAN'),
        line2: fake(`发送: ${(wifiRxHistory[wifiRxHistory.length - 1] * 0.4).toFixed(1)} 接收: ${(wifiRxHistory[wifiRxHistory.length - 1] * 0.8).toFixed(1)} Kbps`),
        svg: buildChartSvg(wifiRxHistory, '#d14d8a', 'rgba(209, 77, 138, 0.22)', { grid: false, topLineY: 14 }),
        cls: 'is-wifi',
      },
      {
        key: 'npu',
        title: 'NPU 0',
        line1: npuInfo.sidebarHtml,
        line2: `${fake('0%')} ${fake('无')}`,
        svg: buildChartSvg(npuHistory, '#2c88b0', 'rgba(17, 125, 187, 0.18)', { grid: false }),
        cls: 'is-npu',
      },
      {
        key: 'gpu',
        title: 'GPU 0',
        line1: gpuInfo.sidebarHtml,
        line2: `${fake(`${gpuNow}%`)} ${fake('无')}`,
        svg: buildChartSvg(gpu3dHistory, '#b65ce0', 'rgba(182, 92, 224, 0.24)', { grid: false }),
        cls: 'is-gpu',
      },
    ];

    return cards
      .map(
        (c) => `
      <button type="button" class="w11-tm-perf-card${activePerf === c.key ? ' is-active' : ''}" data-tm-perf="${c.key}">
        <div class="w11-tm-perf-thumb ${c.cls}">${c.svg}</div>
        <div class="w11-tm-perf-meta">
          <div class="w11-tm-perf-title">${c.title}</div>
          <div class="w11-tm-perf-sub">${c.line1}</div>
          ${c.line2 ? `<div class="w11-tm-perf-sub">${c.line2}</div>` : ''}
        </div>
      </button>`,
      )
      .join('');
  }

  function renderPerfDetail(): string {
    const cores = report?.cpu.hardwareConcurrency ?? navigator.hardwareConcurrency ?? 8;
    const cpuNow = cpuHistory[cpuHistory.length - 1] ?? 6;
    const hp = getHwProfile();
    const curGhz = getCpuClockGhz(cpuNow, hp);
    const mem = getMemoryInfo();
    const uptime = formatUptime(performance.now() + 3600000 * 29 + 84000);

    if (activePerf === 'cpu') {
      const cols = cores >= 8 ? 4 : cores >= 4 ? 2 : 1;
      const rows = Math.ceil(coreHistories.length / cols);
      const cellVCols = cols >= 4 ? 3 : cols === 2 ? 6 : 12;
      const coreBoxes = coreHistories
        .map(
          (hist) => `
        <div class="w11-tm-core-cell is-cpu">
          ${buildChartSvg(hist, '#2c88b0', 'rgba(17, 125, 187, 0.24)', { vCols: cellVCols })}
        </div>`,
        )
        .join('');
      const baseSpeedHtml = realVal(formatNumericText(hp?.cpu.baseClockGHz, 2, ' GHz', '3.40 GHz'));
      const physCoresHtml = realVal(formatNumericText(hp?.cpu.physicalCores, 0, '', String(cores)));
      const cache = pickConsensusCache(hp?.cpu.cache);
      const cacheRowsHtml =
        cache.l1 || cache.l2 || cache.l3
          ? [
              cache.l1 ? `<div class="w11-tm-kv-label">L1 缓存:</div><div class="w11-tm-kv-val">${realVal(cache.l1, cache.tooltip)}</div>` : '',
              cache.l2 ? `<div class="w11-tm-kv-label">L2 缓存:</div><div class="w11-tm-kv-val">${realVal(cache.l2, cache.tooltip)}</div>` : '',
              cache.l3 ? `<div class="w11-tm-kv-label">L3 缓存:</div><div class="w11-tm-kv-val">${realVal(cache.l3, cache.tooltip)}</div>` : '',
            ].join('')
          : `<div class="w11-tm-kv-label">L1 缓存:</div><div class="w11-tm-kv-val">${realVal('3.4 MB')}</div>
            <div class="w11-tm-kv-label">L2 缓存:</div><div class="w11-tm-kv-val">${realVal('36.0 MB')}</div>`;

      return `
        <div class="w11-tm-detail-head">
          <h2>CPU</h2>
          <span class="w11-tm-detail-hw">${getCpuLabelHtml()}</span>
        </div>
        <div class="w11-tm-chart-caption">
          <span>60 秒内的利用率 %</span>
          <span>100%</span>
        </div>
        <div class="w11-tm-core-grid" style="grid-template-columns: repeat(${cols}, minmax(0, 1fr)); grid-template-rows: repeat(${rows}, minmax(0, 1fr));">
          ${coreBoxes}
        </div>
        <div class="w11-tm-bottom-stats">
          <div class="w11-tm-kpis">
            <div class="w11-tm-kpi-row">
              <div class="w11-tm-kpi"><span>利用率</span><strong>${realVal(`${cpuNow}%`)}</strong></div>
              <div class="w11-tm-kpi"><span>速度</span><strong>${realVal(`${curGhz} GHz`)}</strong></div>
            </div>
            <div class="w11-tm-kpi-row">
              <div class="w11-tm-kpi"><span>进程</span><strong>${realVal(procCount)}</strong></div>
              <div class="w11-tm-kpi"><span>线程</span><strong>${realVal(threadCount)}</strong></div>
              <div class="w11-tm-kpi"><span>句柄</span><strong>${realVal(handleCount)}</strong></div>
            </div>
            <div class="w11-tm-kpi"><span>正常运行时间</span><strong>${realVal(uptime)}</strong></div>
          </div>
          <div class="w11-tm-kv-table is-cpu-kv">
            <div class="w11-tm-kv-label">基准速度:</div><div class="w11-tm-kv-val">${baseSpeedHtml}</div>
            <div class="w11-tm-kv-label">插槽:</div><div class="w11-tm-kv-val">${realVal('1')}</div>
            <div class="w11-tm-kv-label">内核:</div><div class="w11-tm-kv-val">${physCoresHtml}</div>
            <div class="w11-tm-kv-label">逻辑处理器:</div><div class="w11-tm-kv-val">${realVal(cores)}</div>
            <div class="w11-tm-kv-label">虚拟化:</div><div class="w11-tm-kv-val">${realVal('已启用')}</div>
            ${cacheRowsHtml}
          </div>
        </div>
      `;
    }

    if (activePerf === 'memory') {
      const memSpec = hp?.memory.spec;
      const memGenHtml = formatEstimation(memSpec, (m) => m.generations.join(' / '), 'LPDDR5');
      const hasMemSpeed =
        memSpec &&
        memSpec.status !== 'unknown' &&
        (memSpec.status === 'inferred_exact'
          ? memSpec.value?.maxSpeedMTs != null
          : (memSpec.candidates ?? []).some((c) => c.value.maxSpeedMTs != null));
      const memSpeedHtml = hasMemSpeed
        ? formatEstimation(memSpec, (m) => (m.maxSpeedMTs != null ? `${m.maxSpeedMTs} MT/秒` : ''), '8448 MT/秒')
        : fake('8448 MT/秒');
      const hasFormFactor =
        memSpec &&
        memSpec.status !== 'unknown' &&
        (memSpec.status === 'inferred_exact'
          ? Boolean(memSpec.value?.formFactorHint)
          : (memSpec.candidates ?? []).some((c) => Boolean(c.value.formFactorHint)));
      const memFormHtml = hasFormFactor
        ? formatEstimation(memSpec, (m) => m.formFactorHint ?? '', 'TSOP')
        : fake('TSOP');
      return `
        <div class="w11-tm-detail-head">
          <h2>内存</h2>
          <span class="w11-tm-detail-hw">${mem.headerCapHtml} ${memGenHtml}</span>
        </div>
        <div class="w11-tm-chart-caption">
          <span>内存使用量</span>
          <span>${mem.chartTopHtml}</span>
        </div>
        <div class="w11-tm-single-chart is-mem">
          ${buildChartSvg(memHistory, '#4f86d6', 'rgba(59, 130, 246, 0.25)')}
        </div>
        <div class="w11-tm-chart-caption"><span>60 秒</span><span>0</span></div>
        <div class="w11-tm-chart-caption" style="margin-top:4px;"><span>内存组合</span><span></span></div>
        <div class="w11-tm-mem-bar">
          <div class="w11-tm-mem-used" style="width:${mem.pct}%"></div>
          <div class="w11-tm-mem-compressed"></div>
          <div class="w11-tm-mem-standby" style="width:${Math.max(4, Math.min(18, 100 - mem.pct - 5))}%"></div>
        </div>
        <div class="w11-tm-bottom-stats">
          <div class="w11-tm-kpis">
            <div class="w11-tm-kpi-row">
              <div class="w11-tm-kpi"><span>使用中(已压缩)</span><strong>${fake(`${mem.usedGb} GB (308 MB)`)}</strong></div>
              <div class="w11-tm-kpi"><span>可用</span><strong>${fake(`${mem.availGb} GB`)}</strong></div>
            </div>
            <div class="w11-tm-kpi-row">
              <div class="w11-tm-kpi"><span>已提交</span><strong>${fake(`${(mem.usedGb + 10.2).toFixed(1)}/${(mem.totalGb + 19.6).toFixed(1)} GB`)}</strong></div>
              <div class="w11-tm-kpi"><span>已缓存</span><strong>${fake('2.5 GB')}</strong></div>
            </div>
            <div class="w11-tm-kpi-row">
              <div class="w11-tm-kpi"><span>分页缓冲池</span><strong>${fake('924 MB')}</strong></div>
              <div class="w11-tm-kpi"><span>非分页缓冲池</span><strong>${fake('816 MB')}</strong></div>
            </div>
          </div>
          <div class="w11-tm-kv-table is-mem-kv">
            <div class="w11-tm-kv-label">速度:</div><div class="w11-tm-kv-val">${memSpeedHtml}</div>
            <div class="w11-tm-kv-label">已使用的插槽:</div><div class="w11-tm-kv-val">${fake('1/1')}</div>
            <div class="w11-tm-kv-label">外形规格:</div><div class="w11-tm-kv-val">${memFormHtml}</div>
          </div>
        </div>
      `;
    }

    if (activePerf === 'disk') {
      const diskInfo = getDiskStorageDisplay();
      const dAct = diskActiveHistory[diskActiveHistory.length - 1] ?? 1;
      const readKb = 180 + Math.round((diskRateHistory[diskRateHistory.length - 1] ?? 20) * 3.1);
      const writeKb = 410 + Math.round((diskRateHistory[diskRateHistory.length - 1] ?? 20) * 4.5);
      return `
        <div class="w11-tm-detail-head">
          <h2>磁盘 0 (C:)</h2>
          <span class="w11-tm-detail-hw">${diskInfo.headerHtml}</span>
        </div>
        <div class="w11-tm-chart-caption"><span>活动时间</span><span>100%</span></div>
        <div class="w11-tm-single-chart is-disk" style="flex:2;">
          ${buildChartSvg(diskActiveHistory, '#6db33f', 'rgba(109, 179, 63, 0.2)')}
        </div>
        <div class="w11-tm-chart-caption"><span>60 秒</span><span>0</span></div>
        <div class="w11-tm-chart-caption" style="margin-top:4px;"><span>磁盘传输速率</span><span>100 KB/秒</span></div>
        <div class="w11-tm-single-chart is-disk" style="flex:0.8; min-height:48px;">
          ${buildChartSvg(diskRateHistory, '#6db33f', 'rgba(109, 179, 63, 0.24)', { grid: false })}
        </div>
        <div class="w11-tm-chart-caption"><span>60 秒</span><span>0</span></div>
        <div class="w11-tm-bottom-stats">
          <div class="w11-tm-kpis">
            <div class="w11-tm-kpi-row">
              <div class="w11-tm-kpi"><span>活动时间</span><strong>${fake(`${dAct}%`)}</strong></div>
              <div class="w11-tm-kpi"><span>平均响应时间</span><strong>${fake('0.9 毫秒')}</strong></div>
            </div>
            <div class="w11-tm-kpi-row">
              <div class="w11-tm-kpi"><span>读取速度</span><strong>${fake(`${readKb} KB/秒`)}</strong></div>
              <div class="w11-tm-kpi"><span>写入速度</span><strong>${fake(`${writeKb} KB/秒`)}</strong></div>
            </div>
          </div>
          <div class="w11-tm-kv-table is-disk-kv">
            <div class="w11-tm-kv-label">站点配额(Quota):</div><div class="w11-tm-kv-val">${diskInfo.quotaHtml}</div>
            <div class="w11-tm-kv-label">当前已用(Usage):</div><div class="w11-tm-kv-val">${diskInfo.usageHtml}</div>
            <div class="w11-tm-kv-label">持久化状态:</div><div class="w11-tm-kv-val">${diskInfo.persistHtml}</div>
            <div class="w11-tm-kv-label">OPFS / 目录挂载:</div><div class="w11-tm-kv-val">${diskInfo.opfsFsHtml}</div>
            <div class="w11-tm-kv-label">物理磁盘容量:</div><div class="w11-tm-kv-val">${fake('不可读 (浏览器隔离)', 'hardware_decoupled')}</div>
            <div class="w11-tm-kv-label">物理盘类型:</div><div class="w11-tm-kv-val">${fake('SSD (NVMe) 模拟占位', 'hardware_decoupled')}</div>
          </div>
        </div>
      `;
    }

    if (activePerf === 'wifi') {
      const conn = report?.network.connection;
      const connObj = typeof conn === 'object' && conn !== null ? conn : null;
      const isWifiReal = connObj?.type === 'wifi';
      const rx = wifiRxHistory[wifiRxHistory.length - 1] ?? 12;
      const wifiHwHtml = formatEstimation(hp?.network.hardwareName, (s) => s, 'WLAN 适配器 (浏览器解耦占位)');
      return `
        <div class="w11-tm-detail-head">
          <h2>Wi-Fi</h2>
          <span class="w11-tm-detail-hw">${wifiHwHtml}</span>
        </div>
        <div class="w11-tm-chart-caption"><span>吞吐量</span><span>500 Kbps</span></div>
        <div class="w11-tm-single-chart is-wifi">
          ${buildChartSvg(wifiRxHistory, '#d14d8a', 'rgba(209, 77, 138, 0.22)', { dashed: true, topLineY: 12 })}
        </div>
        <div class="w11-tm-chart-caption"><span>60 秒</span><span>0</span></div>
        <div class="w11-tm-bottom-stats">
          <div class="w11-tm-kpis">
            <div class="w11-tm-kpi"><span>发送</span><strong>${fake(`${(rx * 0.4).toFixed(1)} Kbps`)}</strong></div>
            <div class="w11-tm-kpi"><span>接收</span><strong>${fake(`${(rx * 0.8).toFixed(1)} Kbps`)}</strong></div>
          </div>
          <div class="w11-tm-kv-table">
            <div class="w11-tm-kv-label">适配器名称:</div><div class="w11-tm-kv-val">${isWifiReal ? realVal('WLAN') : fake('WLAN')}</div>
            <div class="w11-tm-kv-label">SSID:</div><div class="w11-tm-kv-val">${fake('ZTE-5G')}</div>
            <div class="w11-tm-kv-label">连接类型:</div><div class="w11-tm-kv-val">${connObj?.effectiveType ? `${escapeHtml(connObj.effectiveType.toUpperCase())} ${fake('/ 802.11be')}` : fake('802.11be')}</div>
            <div class="w11-tm-kv-label">IPv4 地址:</div><div class="w11-tm-kv-val">${fake('192.168.5.87')}</div>
            <div class="w11-tm-kv-label">往返延迟:</div><div class="w11-tm-kv-val">${connObj?.rttMs != null ? realVal(`${connObj.rttMs} ms`) : fake('24 ms')}</div>
          </div>
        </div>
      `;
    }

    if (activePerf === 'npu') {
      const npuNow = npuHistory[npuHistory.length - 1] ?? 0;
      const npuInfo = getNpuDisplayInfo();
      return `
        <div class="w11-tm-detail-head">
          <h2>NPU</h2>
          <span class="w11-tm-detail-hw">${npuInfo.detailHtml}</span>
        </div>
        <div class="w11-tm-chart-caption"><span>⌄ Compute</span><span>${fake(`${npuNow}%`)}</span></div>
        <div class="w11-tm-single-chart is-npu">
          ${buildChartSvg(npuHistory, '#6b7a8c', 'rgba(107, 122, 140, 0.16)')}
        </div>
        <div class="w11-tm-chart-caption" style="margin-top:4px;"><span>共享内存</span><span>${fake('7.8 GB')}</span></div>
        <div class="w11-tm-single-chart is-npu" style="flex:0 0 46px; min-height:46px;">
          ${buildChartSvg(npuMemHistory, '#6b7a8c', 'rgba(107, 122, 140, 0.16)', { hRows: 2, vCols: 12 })}
        </div>
        <div class="w11-tm-bottom-stats">
          <div class="w11-tm-kpis">
            <div class="w11-tm-kpi-row">
              <div class="w11-tm-kpi"><span>利用率</span><strong>${fake(`${npuNow}%`)}</strong></div>
              <div class="w11-tm-kpi"><span>共享内存</span><strong>${fake('0.0/7.8 GB')}</strong></div>
            </div>
            <div class="w11-tm-kpi-row">
              <div class="w11-tm-kpi"><span>总内存</span><strong>${fake('0.0/7.8 GB')}</strong></div>
              <div class="w11-tm-kpi"><span>温度</span><strong>${fake('无')}</strong></div>
            </div>
          </div>
          <div class="w11-tm-kv-table">
            <div class="w11-tm-kv-label">驱动程序版本:</div><div class="w11-tm-kv-val">${fake('30.0.220.3000')}</div>
            <div class="w11-tm-kv-label">驱动程序日期:</div><div class="w11-tm-kv-val">${fake('2026/1/8')}</div>
            <div class="w11-tm-kv-label">DirectX 版本:</div><div class="w11-tm-kv-val">${fake('12 (FL 1.0: 计算)')}</div>
          </div>
        </div>
      `;
    }

    // GPU
    const gpuNow = gpu3dHistory[gpu3dHistory.length - 1] ?? 18;
    const gpuInfo = getGpuInfoHtml();
    return `
      <div class="w11-tm-detail-head">
        <h2>GPU</h2>
        <span class="w11-tm-detail-hw">${gpuInfo.detailHtml}</span>
      </div>
      <div class="w11-tm-gpu-grid">
        <div class="w11-tm-gpu-box">
          <div class="w11-tm-chart-caption"><span>⌄ 3D</span><span>${fake(`${gpuNow}%`)}</span></div>
          <div class="w11-tm-core-cell is-gpu">${buildChartSvg(gpu3dHistory, '#b65ce0', 'rgba(182, 92, 224, 0.24)', { vCols: 6 })}</div>
        </div>
        <div class="w11-tm-gpu-box">
          <div class="w11-tm-chart-caption"><span>⌄ Video Processing</span><span>${fake('0%')}</span></div>
          <div class="w11-tm-core-cell is-gpu">${buildChartSvg(gpuZeroHistory, '#b65ce0', 'rgba(182, 92, 224, 0.14)', { vCols: 6 })}</div>
        </div>
        <div class="w11-tm-gpu-box">
          <div class="w11-tm-chart-caption"><span>⌄ HW Rotator</span><span>${fake('0%')}</span></div>
          <div class="w11-tm-core-cell is-gpu">${buildChartSvg(gpuZeroHistory, '#b65ce0', 'rgba(182, 92, 224, 0.14)', { vCols: 6 })}</div>
        </div>
        <div class="w11-tm-gpu-box">
          <div class="w11-tm-chart-caption"><span>⌄ Video Engine 1</span><span>${fake('0%')}</span></div>
          <div class="w11-tm-core-cell is-gpu">${buildChartSvg(gpuZeroHistory, '#b65ce0', 'rgba(182, 92, 224, 0.14)', { vCols: 6 })}</div>
        </div>
      </div>
      <div class="w11-tm-chart-caption"><span>共享 GPU 内存</span><span>${fake('7.8 GB')}</span></div>
      <div class="w11-tm-single-chart is-gpu" style="flex:0 0 46px; min-height:46px;">
        ${buildChartSvg(gpuMemHistory, '#b65ce0', 'rgba(182, 92, 224, 0.24)')}
      </div>
      <div class="w11-tm-bottom-stats">
        <div class="w11-tm-kpis">
          <div class="w11-tm-kpi-row">
            <div class="w11-tm-kpi"><span>利用率</span><strong>${fake(`${gpuNow}%`)}</strong></div>
            <div class="w11-tm-kpi"><span>共享 GPU 内存</span><strong>${fake('2.1/7.8 GB')}</strong></div>
          </div>
          <div class="w11-tm-kpi-row">
            <div class="w11-tm-kpi"><span>GPU 内存</span><strong>${fake('2.1/7.8 GB')}</strong></div>
            <div class="w11-tm-kpi"><span>温度</span><strong>${fake('无')}</strong></div>
          </div>
        </div>
        <div class="w11-tm-kv-table">
          <div class="w11-tm-kv-label">驱动程序版本:</div><div class="w11-tm-kv-val">${fake('31.0.170.0')}</div>
          <div class="w11-tm-kv-label">驱动程序日期:</div><div class="w11-tm-kv-val">${fake('2026/7/25')}</div>
          <div class="w11-tm-kv-label">DirectX 版本:</div><div class="w11-tm-kv-val">${fake('12 (FL 12.1)')}</div>
        </div>
      </div>
    `;
  }

  function renderProcessesView(): string {
    const rows = buildProcessRows();
    const cpuNow = cpuHistory[cpuHistory.length - 1] ?? 6;
    const mem = getMemoryInfo();
    const diskNow = diskActiveHistory[diskActiveHistory.length - 1] ?? 1;
    const gpuNow = gpu3dHistory[gpu3dHistory.length - 1] ?? 18;
    const arrow = (col: typeof sortCol) => (sortCol === col ? (sortDesc ? ' ⌄' : ' ⌃') : '');

    const bodyRows = rows
      .map(
        (r) => `
      <tr class="w11-tm-proc-row${selectedProcessId === r.id ? ' is-selected' : ''}" data-tm-proc="${r.id}">
        <td>
          <div class="w11-tm-name">
            ${r.iconHtml}
            <span class="${r.isRealWindow ? '' : 'w11-tm-fake'}">${escapeHtml(r.name)}</span>
          </div>
        </td>
        <td class="w11-tm-status-cell">${r.minimized ? '效率模式' : ''}</td>
        <td class="w11-tm-num-cell" style="background:${heatColor(r.cpu / 30)}">${fake(`${r.cpu.toFixed(1)}%`)}</td>
        <td class="w11-tm-num-cell" style="background:${heatColor(r.memMb / 500)}">${fake(`${r.memMb.toFixed(1)} MB`)}</td>
        <td class="w11-tm-num-cell" style="background:${heatColor(r.diskMbps / 5)}">${fake(`${r.diskMbps.toFixed(1)} MB/秒`)}</td>
        <td class="w11-tm-num-cell" style="background:${heatColor(r.netMbps / 5)}">${fake(`${r.netMbps.toFixed(1)} Mbps`)}</td>
        <td class="w11-tm-num-cell" style="background:${heatColor(r.gpuPct / 30)}">${fake(`${r.gpuPct.toFixed(1)}%`)}</td>
      </tr>`,
      )
      .join('');

    return `
      <div class="w11-tm-proc-wrap">
        <table class="w11-tm-proc-table">
          <thead>
            <tr>
              <th data-tm-sort="name">名称${arrow('name')}</th>
              <th>状态</th>
              <th class="is-num" data-tm-sort="cpu"><div>${fake(`${cpuNow}%`)}</div><span>CPU${arrow('cpu')}</span></th>
              <th class="is-num" data-tm-sort="mem"><div>${fake(`${mem.pct}%`)}</div><span>内存${arrow('mem')}</span></th>
              <th class="is-num" data-tm-sort="disk"><div>${fake(`${diskNow}%`)}</div><span>磁盘${arrow('disk')}</span></th>
              <th class="is-num" data-tm-sort="net"><div>${fake('0%')}</div><span>网络${arrow('net')}</span></th>
              <th class="is-num" data-tm-sort="gpu"><div>${fake(`${gpuNow}%`)}</div><span>GPU${arrow('gpu')}</span></th>
            </tr>
          </thead>
          <tbody>${bodyRows || '<tr><td colspan="7" class="w11-tm-empty">没有匹配的进程</td></tr>'}</tbody>
        </table>
      </div>
    `;
  }

  function render() {
    if (destroyed) return;
    const selectedRealWin = buildProcessRows().find((r) => r.id === selectedProcessId && r.isRealWindow);

    container.innerHTML = `
      <div class="w11-tm-shell${navExpanded ? ' is-nav-expanded' : ''}">
        <aside class="w11-tm-nav">
          <div class="w11-tm-nav-top">
            <button type="button" class="w11-tm-nav-btn" data-tm-toggle-nav title="展开/收起导航">
              <span class="w11-tm-nav-icon">${renderTmGlyph('menu')}</span>
              <span class="w11-tm-nav-text">导航</span>
            </button>
            <button type="button" class="w11-tm-nav-btn${activeTab === 'processes' ? ' is-active' : ''}" data-tm-tab="processes" title="进程">
              <span class="w11-tm-nav-icon">${renderTmGlyph('processes')}</span>
              <span class="w11-tm-nav-text">进程</span>
            </button>
            <button type="button" class="w11-tm-nav-btn${activeTab === 'performance' ? ' is-active' : ''}" data-tm-tab="performance" title="性能">
              <span class="w11-tm-nav-icon">${renderTmGlyph('performance')}</span>
              <span class="w11-tm-nav-text">性能</span>
            </button>
            <button type="button" class="w11-tm-nav-btn is-muted" title="应用历史记录" disabled>
              <span class="w11-tm-nav-icon">${renderTmGlyph('history')}</span>
              <span class="w11-tm-nav-text">应用历史记录</span>
            </button>
            <button type="button" class="w11-tm-nav-btn is-muted" title="启动应用" disabled>
              <span class="w11-tm-nav-icon">${renderTmGlyph('startup')}</span>
              <span class="w11-tm-nav-text">启动应用</span>
            </button>
            <button type="button" class="w11-tm-nav-btn is-muted" title="用户" disabled>
              <span class="w11-tm-nav-icon">${renderTmGlyph('users')}</span>
              <span class="w11-tm-nav-text">用户</span>
            </button>
            <button type="button" class="w11-tm-nav-btn is-muted" title="详细信息" disabled>
              <span class="w11-tm-nav-icon">${renderTmGlyph('details')}</span>
              <span class="w11-tm-nav-text">详细信息</span>
            </button>
            <button type="button" class="w11-tm-nav-btn is-muted" title="服务" disabled>
              <span class="w11-tm-nav-icon">${renderTmGlyph('services')}</span>
              <span class="w11-tm-nav-text">服务</span>
            </button>
          </div>
          <button type="button" class="w11-tm-nav-btn is-muted" title="设置" disabled>
            <span class="w11-tm-nav-icon">${renderTmGlyph('settings')}</span>
            <span class="w11-tm-nav-text">设置</span>
          </button>
        </aside>
        <div class="w11-tm-workspace">
          <header class="w11-tm-topbar">
            <h2 class="w11-tm-page-title">${activeTab === 'performance' ? '性能' : '进程'}</h2>
            ${
              activeTab === 'processes'
                ? `<input type="search" class="w11-tm-search" data-tm-search value="${escapeHtml(searchQuery)}" placeholder="键入要搜索的名称、发布者或 PID" />`
                : ''
            }
            <div class="w11-tm-cmd-actions">
              <button type="button" class="w11-tm-cmd-btn" data-tm-action="new-task">${renderTmGlyph('newTask')}<span>运行新任务</span></button>
              ${
                activeTab === 'processes'
                  ? `<button type="button" class="w11-tm-cmd-btn" data-tm-action="end-task" ${selectedRealWin ? '' : 'disabled'}>${renderTmGlyph('endTask')}<span>结束任务</span></button>
                     <button type="button" class="w11-tm-cmd-btn" disabled>${renderTmGlyph('effMode')}<span>效率模式</span></button>`
                  : ''
              }
              <button type="button" class="w11-tm-cmd-more" title="更多操作" aria-label="更多操作">${renderTmGlyph('more')}</button>
            </div>
          </header>
          <div class="w11-tm-content">
            ${
              activeTab === 'performance'
                ? `<div class="w11-tm-perf-layout">
                    <div class="w11-tm-perf-list" data-tm-perf-list style="width:${perfSidebarWidth}px">${renderPerfSidebar()}</div>
                    <div class="w11-tm-perf-splitter" data-tm-perf-splitter role="separator" aria-orientation="vertical" aria-label="拖动调整左侧图表栏宽度"></div>
                    <div class="w11-tm-perf-detail">${renderPerfDetail()}</div>
                  </div>`
                : renderProcessesView()
            }
          </div>
        </div>
      </div>
    `;

    const searchInput = container.querySelector<HTMLInputElement>('[data-tm-search]');
    if (searchInput && document.activeElement?.closest('[data-tm-search]')) {
      searchInput.focus();
    }
  }

  container.addEventListener('click', (e) => {
    const target = e.target as HTMLElement;
    if (target.closest('[data-tm-toggle-nav]')) {
      navExpanded = !navExpanded;
      render();
      return;
    }
    const tabBtn = target.closest<HTMLElement>('[data-tm-tab]');
    if (tabBtn) {
      activeTab = (tabBtn.dataset.tmTab as TaskManagerTab) || 'performance';
      render();
      return;
    }
    const perfBtn = target.closest<HTMLElement>('[data-tm-perf]');
    if (perfBtn) {
      activePerf = (perfBtn.dataset.tmPerf as PerfResourceKey) || 'cpu';
      render();
      if (activePerf === 'disk') {
        void refreshStorageProbe();
      }
      return;
    }
    const sortTh = target.closest<HTMLElement>('[data-tm-sort]');
    if (sortTh) {
      const col = sortTh.dataset.tmSort as typeof sortCol;
      if (sortCol === col) sortDesc = !sortDesc;
      else {
        sortCol = col;
        sortDesc = col !== 'name';
      }
      render();
      return;
    }
    const procRow = target.closest<HTMLElement>('[data-tm-proc]');
    if (procRow) {
      selectedProcessId = procRow.dataset.tmProc || null;
      render();
      return;
    }
    const actionBtn = target.closest<HTMLElement>('[data-tm-action]');
    if (actionBtn) {
      const act = actionBtn.dataset.tmAction;
      if (act === 'new-task') onOpenNewTask();
      else if (act === 'end-task' && selectedProcessId?.startsWith('win-')) {
        const pid = Number(selectedProcessId.replace('win-', ''));
        if (!Number.isNaN(pid)) {
          onCloseWindow(pid);
          selectedProcessId = null;
          render();
        }
      }
    }
  });

  container.addEventListener('input', (e) => {
    const target = e.target as HTMLElement;
    if (target.matches('[data-tm-search]')) {
      searchQuery = (target as HTMLInputElement).value;
      render();
    }
  });

  container.addEventListener('pointerdown', (e) => {
    const target = e.target as HTMLElement;
    const splitterEl = target.closest<HTMLElement>('[data-tm-perf-splitter]');
    if (!splitterEl) return;
    const layoutEl = splitterEl.closest<HTMLElement>('.w11-tm-perf-layout');
    const listEl = layoutEl?.querySelector<HTMLElement>('[data-tm-perf-list]');
    if (!layoutEl || !listEl) return;
    e.preventDefault();
    const rect = container.getBoundingClientRect();
    const scale = container.offsetWidth > 0 ? rect.width / container.offsetWidth : 1;
    const maxW = Math.max(220, Math.min(440, (layoutEl.clientWidth || 720) - PERF_DETAIL_MIN_WIDTH));
    splitterDrag = {
      startX: e.clientX,
      startW: listEl.offsetWidth || perfSidebarWidth,
      scale: scale || 1,
      maxW,
      listEl,
      splitterEl,
    };
    splitterEl.classList.add('is-dragging');
  });

  const onWindowPointerMove = (ev: PointerEvent) => {
    if (!splitterDrag) return;
    const delta = (ev.clientX - splitterDrag.startX) / splitterDrag.scale;
    const nextW = Math.round(Math.max(PERF_LIST_MIN_WIDTH, Math.min(splitterDrag.maxW, splitterDrag.startW + delta)));
    perfSidebarWidth = nextW;
    splitterDrag.listEl.style.width = `${nextW}px`;
  };

  const endSplitterDrag = () => {
    if (!splitterDrag) return;
    splitterDrag.splitterEl.classList.remove('is-dragging');
    splitterDrag = null;
  };

  window.addEventListener('pointermove', onWindowPointerMove);
  window.addEventListener('pointerup', endSplitterDrag);
  window.addEventListener('pointercancel', endSplitterDrag);

  render();
  void refreshStorageProbe();

  const timerId = window.setInterval(() => {
    const isTyping = container.contains(document.activeElement) && document.activeElement?.tagName === 'INPUT';
    sampleTick += 1;
    pushSample();
    if (!isTyping && !splitterDrag) render();
    if (activePerf === 'disk' && sampleTick % 4 === 0) {
      void refreshStorageProbe();
    }
  }, 1000);

  return {
    refreshWindows: () => render(),
    setReport: (next: DeviceProbeReport) => {
      report = next;
      liveStorage = next.storage;
      ensureCores(next.cpu.hardwareConcurrency);
      render();
    },
    destroy: () => {
      destroyed = true;
      window.clearInterval(timerId);
      window.removeEventListener('pointermove', onWindowPointerMove);
      window.removeEventListener('pointerup', endSplitterDrag);
      window.removeEventListener('pointercancel', endSplitterDrag);
    },
  };
}
