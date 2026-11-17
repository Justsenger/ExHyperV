import type { CpuCacheSpec, FieldEstimation, HardwareProfile, MemorySpec, UnknownReason } from 'hw-profiler';
import {
  collectDeviceHardwareReport,
  collectStorageProbeInfo,
  type DeviceProbeReport,
  type StorageProbeInfo,
} from './device-probe';

const HISTORY_LEN = 60;

export type VirtualHardwareProvenance = 'real' | 'inferred' | 'simulated';

export interface VirtualHardwareValue<T> {
  value: T;
  provenance: VirtualHardwareProvenance;
  tooltip?: string;
  unknownReason?: UnknownReason;
}

export interface VirtualHardwareGpuInfo {
  id: string;
  index: number;
  name: string;
  vendor: string;
  architecture?: string;
  pciDeviceIdHex?: string;
  environment?: 'integrated_only' | 'discrete_primary' | 'dual_gpu_detected' | 'software_emulated';
  isSoftwareRenderer: boolean;
  webglVersion?: string;
  usagePct: number;
  pts3d: number[];
  ptsCopy: number[];
  ptsEnc: number[];
  ptsDec: number[];
  zeroHistory: number[];
  memUtilPct: number;
  memPts: number[];
  provenance: VirtualHardwareProvenance;
  tooltip?: string;
}

export interface VirtualHardwareNpuInfo {
  name: string;
  displayName: string;
  sidebarName: string;
  vendor: string;
  topsPeak?: number;
  webnnSupported?: boolean;
  usagePct: number;
  history: number[];
  memHistory: number[];
  provenance: VirtualHardwareProvenance;
  tooltip: string;
}

export interface VirtualHardwareSnapshot {
  timestamp: string;
  probed: boolean;
  system: {
    osName: VirtualHardwareValue<string>;
    osVersion: string;
    architecture: VirtualHardwareValue<string>;
    formFactor: string;
  };
  cpu: {
    displayName: VirtualHardwareValue<string>;
    shortName: string;
    vendor: string;
    architecture: string;
    logicalCores: number;
    physicalCores: VirtualHardwareValue<number>;
    isVmFallback: boolean;
    baseClockGhz: VirtualHardwareValue<number>;
    currentClockGhz: number;
    boostClockGhz: number;
    cache: {
      l1?: string;
      l2?: string;
      l3?: string;
      tooltip?: string;
    };
    usagePct: number;
    history: number[];
    coreHistories: number[][];
    procCount: number;
    threadCount: number;
    handleCount: number;
  };
  memory: {
    totalGb: number;
    usableGb: number;
    usedGb: number;
    availGb: number;
    usagePct: number;
    history: number[];
    realDeviceGb: number | null;
    capped: boolean;
    capText: string;
    capTip?: string;
    compressedMb: number;
    cachedText: string;
    standbyPct: number;
    committedGb: number;
    commitLimitGb: number;
    pagedPoolMb: number;
    nonPagedPoolMb: number;
    spec?: MemorySpec;
    specProvenance: VirtualHardwareProvenance;
  };
  gpus: VirtualHardwareGpuInfo[];
  npu: VirtualHardwareNpuInfo | null;
  storage: {
    probe: StorageProbeInfo | null;
    activePct: number;
    activeHistory: number[];
    rateHistory: number[];
    readKb: number;
    writeKb: number;
  };
  network: {
    onLine: boolean;
    isWifiReal: boolean;
    adapterName: VirtualHardwareValue<string>;
    connectionType?: string;
    effectiveType?: string;
    downlinkMbps?: number;
    rttMs?: number;
    saveData?: boolean;
    rxHistory: number[];
    sendKbps: number;
    recvKbps: number;
  };
  display: DeviceProbeReport['display'] | null;
  battery: DeviceProbeReport['battery'] | null;
  peripheralsAndApis: DeviceProbeReport['peripheralsAndApis'] | null;
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

function extractEstimationValue<T>(est: FieldEstimation<T> | undefined): {
  value: T | null;
  provenance: VirtualHardwareProvenance;
  capped?: boolean;
  unknownReason?: UnknownReason;
} {
  if (!est) return { value: null, provenance: 'simulated' };
  if (est.status === 'real') {
    return { value: est.value, provenance: 'real', capped: est.capped };
  }
  if (est.status === 'inferred_exact') {
    return { value: est.value, provenance: 'inferred' };
  }
  if (est.status === 'inferred_candidates' && est.candidates.length > 0) {
    return { value: est.candidates[0].value, provenance: 'inferred' };
  }
  return {
    value: null,
    provenance: 'simulated',
    unknownReason: est.status === 'unknown' ? est.reason : undefined,
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

function inferGpuVendorFromText(name: string, fallbackVendor?: string): string {
  const src = `${name} ${fallbackVendor ?? ''}`.toLowerCase();
  if (src.includes('nvidia') || src.includes('geforce') || src.includes('rtx') || src.includes('gtx')) return 'NVIDIA';
  if (src.includes('amd') || src.includes('radeon')) return 'AMD';
  if (src.includes('intel') || src.includes('arc') || src.includes('uhd') || src.includes('iris')) return 'Intel';
  if (src.includes('qualcomm') || src.includes('adreno')) return 'Qualcomm';
  if (src.includes('apple')) return 'Apple';
  return fallbackVendor?.trim() || 'Virtual GPU';
}

function shortenCpuName(fullName: string): string {
  return fullName
    .replace(/@\s*\d+(?:\.\d+)?\s*GHz.*$/i, '')
    .replace(/\s+\d+-Core Processor/i, '')
    .replace(/\(R\)|\(TM\)/gi, '')
    .replace(/\s+/g, ' ')
    .trim();
}

function resolveCpuArchitectureLabel(report: DeviceProbeReport | null): string {
  const ua = report?.context.uaData;
  const uaArch =
    typeof ua === 'object' && ua !== null && typeof ua.architecture === 'string'
      ? ua.architecture.trim().toLowerCase()
      : '';
  const uaBitness =
    typeof ua === 'object' && ua !== null && typeof ua.bitness === 'string'
      ? ua.bitness.trim()
      : '';

  if (uaArch === 'x86') return uaBitness === '64' ? 'x64' : 'x86';
  if (uaArch === 'arm') return uaBitness === '64' ? 'ARM64' : 'ARM';

  const navPlatform = typeof navigator !== 'undefined' ? navigator.platform : '';
  const navUa = typeof navigator !== 'undefined' ? navigator.userAgent : '';
  const rawHint = [uaArch, report?.context.platformLegacy ?? navPlatform, report?.context.userAgent ?? navUa]
    .join(' ')
    .toLowerCase();

  if (/\b(arm64|aarch64)\b/.test(rawHint)) return 'ARM64';
  if (/\b(armv\d+\w*|armhf|arm)\b/.test(rawHint)) return 'ARM';
  if (/\briscv64\b/.test(rawHint)) return 'RISC-V 64';
  if (/\briscv32\b/.test(rawHint)) return 'RISC-V 32';
  if (/\bloongarch64\b/.test(rawHint)) return 'LoongArch64';
  if (/\b(ppc64le|ppc64)\b/.test(rawHint)) return 'PowerPC 64';
  if (/\bs390x\b/.test(rawHint)) return 's390x';
  if (/\bmips64\b/.test(rawHint)) return 'MIPS64';
  if (/\b(win64|wow64|x86_64|amd64|x64)\b/.test(rawHint)) return 'x64';
  if (/\b(i[3-6]86|x86)\b/.test(rawHint)) return 'x86';

  return uaArch ? uaArch.toUpperCase() : 'x64';
}

function extractBrandClockGhz(hp: HardwareProfile | undefined): number | null {
  const est = extractEstimationValue(hp?.cpu.winBrandString);
  if (!est.value) return null;
  const match = est.value.match(/@\s*(\d+(?:\.\d+)?)\s*GHz/i);
  return match ? Number(match[1]) : null;
}

export function createVirtualHardwareService() {
  let report: DeviceProbeReport | null = null;
  let liveStorage: StorageProbeInfo | null = null;
  let probeInFlight: Promise<DeviceProbeReport> | null = null;
  let storageInFlight = false;
  let tickTimer: number | null = null;
  let sampleTick = 0;

  const subscribers = new Set<(snap: VirtualHardwareSnapshot) => void>();

  let coreHistories: number[][] = [];
  const cpuHistory: number[] = seedSpikyHistory(9, 28, 0.25);
  let procCount = 328 + Math.round((Math.random() - 0.5) * 6);
  let threadCount = 5719 + Math.round((Math.random() - 0.5) * 90);
  let handleCount = 166474 + Math.round((Math.random() - 0.5) * 1600);
  let cpuFreqOffset = 0;
  const memHistory: number[] = Array.from({ length: HISTORY_LEN }, (_, i) =>
    +(25 + Math.sin(i / 9) * 0.35 + ((i % 5) - 2) * 0.06).toFixed(1),
  );
  const diskActiveHistory: number[] = seedSpikyHistory(2, 16, 0.12);
  const diskRateHistory: number[] = seedSpikyHistory(16, 52, 0.24);
  const wifiRxHistory: number[] = seedSpikyHistory(10, 74, 0.16);
  const npuHistory: number[] = Array(HISTORY_LEN).fill(0);
  const npuMemHistory: number[] = Array(HISTORY_LEN).fill(0);
  const gpu3dHistory: number[] = seedSpikyHistory(18, 38, 0.28);
  const gpuCopyHistory: number[] = seedSpikyHistory(3, 8, 0.12);
  const gpuEncHistory: number[] = Array(HISTORY_LEN).fill(0);
  const gpuDecHistory: number[] = seedSpikyHistory(5, 14, 0.15);
  const gpuZeroHistory: number[] = Array(HISTORY_LEN).fill(0);
  const gpuMemHistory: number[] = Array.from({ length: HISTORY_LEN }, (_, i) => +(26 + (i % 3) * 0.8).toFixed(1));

  function getLogicalCoreCount(): number {
    const raw =
      report?.cpu.hardwareConcurrency ??
      (typeof navigator !== 'undefined' ? navigator.hardwareConcurrency : undefined) ??
      8;
    return Math.max(1, Math.min(32, raw));
  }

  function ensureCores(cores: number) {
    const count = Math.max(1, Math.min(32, cores));
    if (coreHistories.length === count) return;
    coreHistories = Array.from({ length: count }, (_, idx) => {
      if (idx < Math.ceil(count * 0.42)) return seedSpikyHistory(16, 48, 0.24);
      if (idx < Math.ceil(count * 0.67)) return seedSpikyHistory(6, 36, 0.16);
      return seedSpikyHistory(2, 26, 0.09);
    });
  }

  ensureCores(getLogicalCoreCount());

  function advanceSeries() {
    ensureCores(getLogicalCoreCount());
    const cores = coreHistories.length;
    let sum = 0;

    coreHistories.forEach((arr, idx) => {
      const prev = arr[arr.length - 1] ?? 6;
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

    const memPrev = memHistory[memHistory.length - 1] ?? 25;
    const memPull = (25 - memPrev) * 0.08;
    const memNext = Math.max(21, Math.min(29, +(memPrev + memPull + (Math.random() - 0.5) * 0.28).toFixed(1)));
    memHistory.push(memNext);
    if (memHistory.length > HISTORY_LEN) memHistory.shift();

    diskActiveHistory.push(
      Math.max(1, Math.min(26, Math.round(Math.random() < 0.16 ? 5 + Math.random() * 14 : 1 + Math.random() * 2))),
    );
    if (diskActiveHistory.length > HISTORY_LEN) diskActiveHistory.shift();

    diskRateHistory.push(
      Math.max(2, Math.min(94, Math.round(Math.random() < 0.22 ? 25 + Math.random() * 64 : 4 + Math.random() * 16))),
    );
    if (diskRateHistory.length > HISTORY_LEN) diskRateHistory.shift();

    wifiRxHistory.push(
      Math.max(2, Math.min(96, Math.round(Math.random() < 0.16 ? 48 + Math.random() * 48 : 3 + Math.random() * 15))),
    );
    if (wifiRxHistory.length > HISTORY_LEN) wifiRxHistory.shift();

    const gPrev = gpu3dHistory[gpu3dHistory.length - 1] ?? 18;
    gpu3dHistory.push(Math.max(4, Math.min(58, Math.round(gPrev * 0.4 + (10 + Math.random() * 34) * 0.6))));
    if (gpu3dHistory.length > HISTORY_LEN) gpu3dHistory.shift();

    gpuCopyHistory.push(Math.max(0, Math.min(25, Math.round(1 + Math.random() * 6))));
    if (gpuCopyHistory.length > HISTORY_LEN) gpuCopyHistory.shift();

    gpuEncHistory.push(0);
    if (gpuEncHistory.length > HISTORY_LEN) gpuEncHistory.shift();

    gpuDecHistory.push(Math.max(0, Math.min(30, Math.round(2 + Math.random() * 9))));
    if (gpuDecHistory.length > HISTORY_LEN) gpuDecHistory.shift();

    gpuZeroHistory.push(0);
    if (gpuZeroHistory.length > HISTORY_LEN) gpuZeroHistory.shift();

    gpuMemHistory.push(27);
    if (gpuMemHistory.length > HISTORY_LEN) gpuMemHistory.shift();

    npuHistory.push(0);
    if (npuHistory.length > HISTORY_LEN) npuHistory.shift();

    npuMemHistory.push(0);
    if (npuMemHistory.length > HISTORY_LEN) npuMemHistory.shift();
  }

  function buildSnapshot(): VirtualHardwareSnapshot {
    const hp = report?.hardwareProfile;
    const logicalCores = getLogicalCoreCount();
    ensureCores(logicalCores);

    const archCode = resolveCpuArchitectureLabel(report);
    const osNameEst = extractEstimationValue(hp?.device.osName);
    const osVerEst = extractEstimationValue(hp?.device.osVersion);
    const formEst = extractEstimationValue(hp?.device.formFactor);
    const fallbackOs =
      typeof navigator !== 'undefined' && /mac/i.test(navigator.platform)
        ? 'macOS'
        : typeof navigator !== 'undefined' && /linux/i.test(navigator.platform)
          ? 'Linux'
          : 'Windows';

    // CPU
    const brandEst = extractEstimationValue(hp?.cpu.winBrandString);
    const modelEst = extractEstimationValue(hp?.cpu.model);
    const vendorEst = extractEstimationValue(hp?.cpu.vendor);
    const skuEst = extractEstimationValue(hp?.cpu.skuCandidates);
    const physEst = extractEstimationValue(hp?.cpu.physicalCores);
    const baseClkEst = extractEstimationValue(hp?.cpu.baseClockGHz);
    const cache = pickConsensusCache(hp?.cpu.cache);
    const isVmFallback = physEst.value === null && !cache.l1 && !cache.l2 && !cache.l3;

    let cpuDisplay: VirtualHardwareValue<string>;
    if (brandEst.value) {
      cpuDisplay = {
        value: brandEst.value,
        provenance: brandEst.provenance,
        tooltip: brandEst.provenance === 'inferred' ? '静态硬件指纹库推理' : undefined,
      };
    } else if (modelEst.value) {
      const clkText =
        typeof skuEst.value?.boostClockGHz === 'number' && skuEst.value.boostClockGHz > 0.2
          ? `${skuEst.value.boostClockGHz.toFixed(2)} GHz`
          : baseClkEst.value != null
            ? `${baseClkEst.value.toFixed(2)} GHz`
            : '3.40 GHz';
      cpuDisplay = {
        value: `${modelEst.value} @ ${clkText}`,
        provenance: 'inferred',
        tooltip: '静态硬件指纹库推理',
      };
    } else {
      cpuDisplay = {
        value: archCode ? `${archCode} Processor` : 'Processor',
        provenance: 'real',
      };
    }

    const baseClock = skuEst.value?.baseClockGHz ?? baseClkEst.value ?? 2.8;
    const brandClk = extractBrandClockGhz(hp);
    const boostClock =
      typeof skuEst.value?.boostClockGHz === 'number' && skuEst.value.boostClockGHz > 0.2
        ? skuEst.value.boostClockGHz
        : typeof brandClk === 'number' && brandClk > baseClock
          ? brandClk
          : baseClock;
    const maxClk = Math.max(baseClock, boostClock);
    const hasTurbo = maxClk > baseClock + 0.01;
    const floorClk = hasTurbo ? +(baseClock * 0.86).toFixed(2) : +(maxClk * 0.94).toFixed(2);
    const cpuNow = cpuHistory[cpuHistory.length - 1] ?? 6;
    const load = Math.max(0, Math.min(1, cpuNow / 100));
    const ratio = hasTurbo ? Math.pow(load, 0.72) : 0.58 + 0.4 * load;
    const rawClk = floorClk + (maxClk - floorClk) * ratio + cpuFreqOffset * maxClk * 0.12;
    const currentClockGhz = +Math.max(floorClk, Math.min(maxClk, +rawClk.toFixed(2))).toFixed(2);

    // Memory
    const memEst = extractEstimationValue(hp?.memory.browserDeviceMemoryGiB);
    const realDeviceGb =
      report && typeof report.memory.deviceMemoryGiB === 'number'
        ? report.memory.deviceMemoryGiB
        : memEst.provenance === 'real' && typeof memEst.value === 'number'
          ? memEst.value
          : null;
    const capped = memEst.provenance === 'real' ? Boolean(memEst.capped) : realDeviceGb === 8;
    const totalGb = realDeviceGb ?? 16;
    const usableGb = totalGb;
    const capText = Number.isInteger(totalGb) ? `${totalGb}.0 GB` : `${totalGb} GB`;
    const capTip = realDeviceGb
      ? capped
        ? `navigator.deviceMemory 在 ${capText} 处封顶（实际物理内存可能 ≥ ${capText}，按读数 ${capText} 展示）`
        : `navigator.deviceMemory 实测容量：${capText}`
      : undefined;
    const rawMemPct = memHistory[memHistory.length - 1] ?? 25;
    const usedGb = +((usableGb * rawMemPct) / 100).toFixed(1);
    const memPct = Math.round((usedGb / Math.max(0.1, usableGb)) * 100);
    const availGb = +Math.max(0, usableGb - usedGb).toFixed(1);
    const compressedMb = Math.round(Math.max(18, usedGb * 12 + 24));
    const cachedGbRaw = Math.min(availGb * 0.65, Math.max(0.3, totalGb * 0.22 + usedGb * 0.15));
    const cachedText =
      cachedGbRaw < 1
        ? `${Math.max(16, Math.round(cachedGbRaw * 1024))} MB`
        : `${cachedGbRaw.toFixed(1)} GB`;
    const standbyPct = Math.max(0, Math.min(100 - memPct, (cachedGbRaw / Math.max(0.1, usableGb)) * 100));
    const commitLimitGb = +(totalGb * 1.35).toFixed(1);
    const committedGb = Math.min(+(commitLimitGb - 0.3).toFixed(1), +(usedGb * 1.28 + 0.5).toFixed(1));
    const pagedPoolMb = Math.round(240 + totalGb * 22);
    const nonPagedPoolMb = Math.round(160 + totalGb * 20);
    const memSpecEst = extractEstimationValue(hp?.memory.spec);

    // GPU
    const rawGpuName = report?.gpu.webglRenderer ? cleanGpuName(report.gpu.webglRenderer) : '';
    const gpuNameEst = extractEstimationValue(hp?.gpu.name);
    const gpuVendorEst = extractEstimationValue(hp?.gpu.vendor);
    const gpuArchEst = extractEstimationValue(hp?.gpu.architecture);
    const gpuPciEst = extractEstimationValue(hp?.gpu.pciDeviceIdHex);
    const gpuEnvEst = extractEstimationValue(hp?.gpu.environment);
    const isSoftwareRenderer =
      /^Microsoft Basic Render Driver$/i.test(rawGpuName) ||
      /swiftshader|llvmpipe/i.test(rawGpuName) ||
      gpuEnvEst.value === 'software_emulated';

    let gpuName = 'GPU';
    let gpuProvenance: VirtualHardwareProvenance = 'simulated';
    let gpuTooltip: string | undefined;

    if (rawGpuName && !/^Microsoft Basic Render Driver$/i.test(rawGpuName)) {
      gpuName = rawGpuName;
      gpuProvenance = 'real';
      if (gpuPciEst.value && gpuPciEst.provenance === 'real') {
        gpuTooltip = `PCI ID: ${gpuPciEst.value}${gpuArchEst.value ? ` · ${gpuArchEst.value}` : ''}`;
      }
    } else if (gpuNameEst.value) {
      gpuName = gpuNameEst.value;
      gpuProvenance = gpuNameEst.provenance;
      gpuTooltip = '静态硬件指纹库推理';
    } else if (rawGpuName) {
      gpuName = rawGpuName;
      gpuProvenance = 'real';
      gpuTooltip = '软件渲染或基础驱动';
    }

    const gpuNow = gpu3dHistory[gpu3dHistory.length - 1] ?? 18;
    const primaryGpu: VirtualHardwareGpuInfo = {
      id: 'gpu-0',
      index: 0,
      name: gpuName,
      vendor: gpuVendorEst.value ?? inferGpuVendorFromText(gpuName, report?.gpu.webglVendor),
      architecture: gpuArchEst.value ?? undefined,
      pciDeviceIdHex: gpuPciEst.value ?? undefined,
      environment: gpuEnvEst.value ?? undefined,
      isSoftwareRenderer,
      webglVersion: report?.gpu.webglVersion,
      usagePct: gpuNow,
      pts3d: [...gpu3dHistory],
      ptsCopy: [...gpuCopyHistory],
      ptsEnc: [...gpuEncHistory],
      ptsDec: [...gpuDecHistory],
      zeroHistory: [...gpuZeroHistory],
      memUtilPct: gpuMemHistory[gpuMemHistory.length - 1] ?? 27,
      memPts: [...gpuMemHistory],
      provenance: gpuProvenance,
      tooltip: gpuTooltip,
    };

    // NPU
    const npuEst = extractEstimationValue(hp?.npu.hardware);
    const webnnEst = extractEstimationValue(hp?.npu.webnnNpuSupported);
    const webnnNote =
      webnnEst.provenance === 'real' && webnnEst.value !== null
        ? ` · WebNN NPU: ${webnnEst.value ? '已就绪' : '未就绪'}`
        : '';

    let npu: VirtualHardwareNpuInfo | null = null;
    if (npuEst.value) {
      const npuSpec = npuEst.value;
      const topsText = typeof npuSpec.topsPeak === 'number' ? ` (${npuSpec.topsPeak} TOPS)` : '';
      const tip = `静态硬件指纹库推理${topsText}${webnnNote}`;
      const display = npuSpec.winDisplayName
        ? npuSpec.winDisplayName
        : modelEst.value
          ? `${modelEst.value} - ${npuSpec.name}`
          : npuSpec.name;
      npu = {
        name: npuSpec.name,
        displayName: display,
        sidebarName: npuSpec.winDisplayName || npuSpec.name,
        vendor: npuSpec.vendor,
        topsPeak: npuSpec.topsPeak,
        webnnSupported: webnnEst.value ?? undefined,
        usagePct: npuHistory[npuHistory.length - 1] ?? 0,
        history: [...npuHistory],
        memHistory: [...npuMemHistory],
        provenance: 'inferred',
        tooltip: tip,
      };
    } else if (webnnEst.provenance === 'real' && webnnEst.value === true) {
      npu = {
        name: 'WebNN NPU',
        displayName: 'WebNN NPU',
        sidebarName: 'WebNN NPU',
        vendor: 'WebNN',
        webnnSupported: true,
        usagePct: npuHistory[npuHistory.length - 1] ?? 0,
        history: [...npuHistory],
        memHistory: [...npuMemHistory],
        provenance: 'real',
        tooltip: '通过 navigator.ml.createContext({ deviceType: "npu" }) 实测支持',
      };
    }

    // Storage
    const diskAct = diskActiveHistory[diskActiveHistory.length - 1] ?? 1;
    const diskRate = diskRateHistory[diskRateHistory.length - 1] ?? 20;

    // Network
    const conn = report?.network.connection;
    const connObj = typeof conn === 'object' && conn !== null ? conn : null;
    const isWifiReal = connObj?.type === 'wifi';
    const netHwEst = extractEstimationValue(hp?.network.hardwareName);
    const rxNow = wifiRxHistory[wifiRxHistory.length - 1] ?? 12;

    return {
      timestamp: report?.timestamp ?? new Date().toLocaleString(),
      probed: Boolean(report),
      system: {
        osName: {
          value: osNameEst.value ?? fallbackOs,
          provenance: osNameEst.value ? osNameEst.provenance : 'simulated',
        },
        osVersion: osVerEst.value ?? '',
        architecture: {
          value: archCode,
          provenance: 'real',
        },
        formFactor: formEst.value ?? 'unknown',
      },
      cpu: {
        displayName: cpuDisplay,
        shortName: shortenCpuName(cpuDisplay.value),
        vendor: vendorEst.value ?? 'Generic',
        architecture: archCode,
        logicalCores,
        physicalCores: {
          value: physEst.value ?? logicalCores,
          provenance: physEst.value !== null ? physEst.provenance : 'simulated',
        },
        isVmFallback,
        baseClockGhz: {
          value: +baseClock.toFixed(2),
          provenance: baseClkEst.value !== null ? baseClkEst.provenance : 'simulated',
        },
        currentClockGhz,
        boostClockGhz: +boostClock.toFixed(2),
        cache,
        usagePct: cpuNow,
        history: [...cpuHistory],
        coreHistories: coreHistories.map((arr) => [...arr]),
        procCount,
        threadCount,
        handleCount,
      },
      memory: {
        totalGb,
        usableGb,
        usedGb,
        availGb,
        usagePct: memPct,
        history: [...memHistory],
        realDeviceGb,
        capped,
        capText,
        capTip,
        compressedMb,
        cachedText,
        standbyPct,
        committedGb,
        commitLimitGb,
        pagedPoolMb,
        nonPagedPoolMb,
        spec: memSpecEst.value ?? undefined,
        specProvenance: memSpecEst.value ? memSpecEst.provenance : 'simulated',
      },
      gpus: [primaryGpu],
      npu,
      storage: {
        probe: liveStorage ?? report?.storage ?? null,
        activePct: diskAct,
        activeHistory: [...diskActiveHistory],
        rateHistory: [...diskRateHistory],
        readKb: 180 + Math.round(diskRate * 3.1),
        writeKb: 410 + Math.round(diskRate * 4.5),
      },
      network: {
        onLine: report?.network.onLine ?? (typeof navigator !== 'undefined' ? navigator.onLine : true),
        isWifiReal,
        adapterName: {
          value: netHwEst.value ?? 'WLAN 适配器 (浏览器解耦占位)',
          provenance: netHwEst.value ? netHwEst.provenance : 'simulated',
          unknownReason: netHwEst.unknownReason,
        },
        connectionType: connObj?.type,
        effectiveType: connObj?.effectiveType,
        downlinkMbps: connObj?.downlinkMbps,
        rttMs: connObj?.rttMs,
        saveData: connObj?.saveData,
        rxHistory: [...wifiRxHistory],
        sendKbps: +(rxNow * 0.4).toFixed(1),
        recvKbps: +(rxNow * 0.8).toFixed(1),
      },
      display: report?.display ?? null,
      battery: report?.battery ?? null,
      peripheralsAndApis: report?.peripheralsAndApis ?? null,
    };
  }

  function notifySubscribers() {
    if (subscribers.size === 0) return;
    const snap = buildSnapshot();
    subscribers.forEach((fn) => fn(snap));
  }

  function ensureTicker() {
    if (tickTimer !== null || subscribers.size === 0) return;
    if (typeof window === 'undefined') return;
    tickTimer = window.setInterval(() => {
      sampleTick += 1;
      advanceSeries();
      notifySubscribers();
      if (sampleTick % 4 === 0) {
        void refreshStorage();
      }
    }, 1000);
  }

  function stopTickerIfIdle() {
    if (subscribers.size > 0 || tickTimer === null) return;
    if (typeof window !== 'undefined') {
      window.clearInterval(tickTimer);
    }
    tickTimer = null;
  }

  async function refreshProbe(force = false): Promise<VirtualHardwareSnapshot> {
    if (probeInFlight && !force) {
      await probeInFlight;
      return buildSnapshot();
    }
    const task = collectDeviceHardwareReport()
      .then((next) => {
        report = next;
        liveStorage = next.storage;
        ensureCores(getLogicalCoreCount());
        notifySubscribers();
        return next;
      })
      .finally(() => {
        if (probeInFlight === task) {
          probeInFlight = null;
        }
      });
    probeInFlight = task;
    await task;
    return buildSnapshot();
  }

  async function refreshStorage(): Promise<StorageProbeInfo> {
    if (storageInFlight && liveStorage) return liveStorage;
    storageInFlight = true;
    try {
      const next = await collectStorageProbeInfo();
      liveStorage = next;
      if (report) {
        report = { ...report, storage: next };
      }
      notifySubscribers();
      return next;
    } finally {
      storageInFlight = false;
    }
  }

  function subscribe(listener: (snap: VirtualHardwareSnapshot) => void): () => void {
    subscribers.add(listener);
    ensureTicker();
    return () => {
      subscribers.delete(listener);
      stopTickerIfIdle();
    };
  }

  return {
    getSnapshot: buildSnapshot,
    getProbeReport: () => report,
    refreshProbe,
    refreshStorage,
    subscribe,
    tick: () => {
      sampleTick += 1;
      advanceSeries();
      notifySubscribers();
    },
    destroy: () => {
      subscribers.clear();
      stopTickerIfIdle();
    },
  };
}

export type VirtualHardwareService = ReturnType<typeof createVirtualHardwareService>;
