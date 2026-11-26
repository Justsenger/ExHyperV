/**
 * Mode switcher + Live Server state/operations adapter for ExHyperV.WoW.
 * Default mode is 'demo' (offline static demo data, zero network probes).
 * Switching to 'live' connects to ExHyperV.Server and maps Core APIs & telemetry into the UI.
 */

import { OS_TYPES } from './ui-design-data.js';
import { createServerClient, pickField } from './server-client.js';

const STORAGE_PROFILES_KEY = 'exhyperv.wow.remoteProfiles';
const STORAGE_PREFS_KEY = 'exhyperv.wow.remotePrefs';

const RUNNING_STATES = new Set(['Running', 'Starting', 'WaitingToStart', 'Resuming', 'Saving', 'Stopping', 'ShuttingDown', 'Suspending', 'ForceShuttingDown', 'ForceRestarting']);

function mapVmStateText(state) {
  const map = {
    Running: '运行中',
    PoweredOff: '已关机',
    Saved: '已保存',
    FastSaved: '已保存',
    Hibernated: '已休眠',
    Suspended: '已暂停',
    Paused: '已暂停',
    Starting: '正在启动',
    WaitingToStart: '等待启动',
    ShuttingDown: '正在关机',
    Stopping: '正在停止',
    Saving: '正在保存',
    FastSaving: '正在保存',
    Suspending: '正在暂停',
    Resuming: '正在恢复',
    MergingDisks: '正在合并磁盘',
    ForceShuttingDown: '正在强制关闭',
    ForceRestarting: '正在重启',
    Processing: '正在处理',
    Unknown: '未知状态',
    Other: '其他状态'
  };
  return map[state] || state || '已关机';
}

export function resolveOsDisplay(osType) {
  const raw = String(osType || '').trim();
  if (!raw) return { osText: 'Windows', osIcon: 'Windows.svg', osImage: '/ui-design/os/Windows.svg' };
  const exact = OS_TYPES.find((item) => item.toLowerCase() === raw.toLowerCase());
  const matched = exact || OS_TYPES.find((item) => raw.toLowerCase().includes(item.toLowerCase()));
  const iconName = matched ? `${matched}.svg` : 'Windows.svg';
  return {
    osText: matched || raw,
    osIcon: iconName,
    osImage: `/ui-design/os/${iconName}`
  };
}

export function formatTimespanText(raw) {
  if (!raw) return '00:00:00';
  if (typeof raw === 'string') {
    const match = raw.trim().match(/^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})/);
    if (match) {
      const days = Number(match[1] || 0);
      const hours = days * 24 + Number(match[2] || 0);
      return `${String(hours).padStart(2, '0')}:${match[3]}:${match[4]}`;
    }
    return raw.split('.')[0] || '00:00:00';
  }
  return '00:00:00';
}

export function formatBytesCompact(bytes) {
  const val = Number(bytes || 0);
  if (!Number.isFinite(val) || val <= 0) return '0 B';
  if (val >= 1024 ** 4) return `${(val / 1024 ** 4).toFixed(1)} TB`;
  if (val >= 1024 ** 3) return `${(val / 1024 ** 3).toFixed(val >= 10 * 1024 ** 3 ? 0 : 1)} GB`;
  if (val >= 1024 ** 2) return `${Math.round(val / 1024 ** 2)} MB`;
  if (val >= 1024) return `${Math.round(val / 1024)} KB`;
  return `${Math.round(val)} B`;
}

export function formatRateBytes(bytesPerSec) {
  const val = Number(bytesPerSec || 0);
  if (!Number.isFinite(val) || val <= 0) return '0 B/s';
  if (val >= 1024 ** 3) return `${(val / 1024 ** 3).toFixed(2)} GB/s`;
  if (val >= 1024 ** 2) return `${(val / 1024 ** 2).toFixed(1)} MB/s`;
  if (val >= 1024) return `${Math.round(val / 1024)} KB/s`;
  return `${Math.round(val)} B/s`;
}

export function formatBitRate(bytesPerSec) {
  const bits = Number(bytesPerSec || 0) * 8;
  if (!Number.isFinite(bits) || bits <= 0) return '0 Kbps';
  if (bits >= 1e9) return `${(bits / 1e9).toFixed(2)} Gbps`;
  if (bits >= 1e6) return `${(bits / 1e6).toFixed(1)} Mbps`;
  if (bits >= 1e3) return `${Math.round(bits / 1e3)} Kbps`;
  return `${Math.round(bits)} bps`;
}

function pushSeries(arr, val, maxLen = 20) {
  const list = Array.isArray(arr) ? arr.slice(-(maxLen - 1)) : [];
  const n = Number(val);
  list.push(Number.isFinite(n) ? Math.max(0, Math.min(100, n)) : 0);
  while (list.length < 7) list.unshift(list[0] ?? 0);
  return list;
}

function readLocalJson(key, fallback) {
  try {
    if (typeof localStorage === 'undefined') return fallback;
    const raw = localStorage.getItem(key);
    return raw ? JSON.parse(raw) : fallback;
  } catch {
    return fallback;
  }
}

function writeLocalJson(key, value) {
  try {
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem(key, JSON.stringify(value));
    }
  } catch {}
}

export async function requestConsoleConnection(client, vmId, mode, watchOnly = false, ceiling = null) {
  const info = client.serverInfo;
  const backendId = mode === 'enhanced' ? 'RdpEnhanced' : mode === 'external' ? info.extensionBackends?.[0] || 'External' : 'RdpBasic';
  const scoped = Boolean(vmId && info.consoleProtocolVersions?.includes(2) && info.authenticationMode !== 'loopback');
  if (!scoped && (watchOnly || ceiling || info.readOnly || mode === 'external')) {
    throw new Error(mode === 'external' ? '宿主扩展不可达（EXT-001），请在宿主桌面端查看扩展状态' : '此连接无法授权仅观看会话');
  }
  let scope = {}, extension = null;
  if (scoped) {
    const directory = await client.consoleBackends(vmId);
    const entry = directory.backends.find((item) => item.backendId === backendId);
    if (!entry?.rights?.includes('Watch')) {
      const id = entry?.extension?.diagnosticId;
      const reason = entry?.extension?.reason || '扩展不可用';
      throw new Error(mode === 'external' ? reason + '，请在宿主桌面端查看' + (/^[a-z0-9-]{1,32}$/i.test(id || '') ? '（' + id + '）' : '') : '观看权限未获准');
    }
    extension = entry.extension;
    const allowed = Array.isArray(ceiling) ? entry.rights.filter(right => ceiling.includes(right)) : entry.rights;
    if (!allowed.includes('Watch')) throw new Error('观看权限未获准');
    scope = { vmId, backendId, channel: 'console', requestedRights: watchOnly ? ['Watch'] : allowed };
  }
  const { ticket, serverUrl } = await client.issueWsTicket(scope);
  return { mode: 'live', serverUrl, ticket, backendId, protocol: scoped ? 2 : 1, rights: scope.requestedRights, extension, supportedEncodings: info.supportedConsoleEncodings };
}

export function createServerAdapter(hooks = {}) {
  const client = createServerClient();

  const savedPrefs = readLocalJson(STORAGE_PREFS_KEY, {});
  const savedProfiles = readLocalJson(STORAGE_PROFILES_KEY, []);

  const adapterState = {
    mode: 'demo', // 'demo' | 'live'
    status: 'demo', // 'demo' | 'connecting' | 'connected' | 'error'
    serverUrl: typeof savedPrefs.lastUrl === 'string' && savedPrefs.lastUrl ? savedPrefs.lastUrl : 'http://127.0.0.1:5191',
    rememberSession: Boolean(savedPrefs.rememberSession),
    profiles: Array.isArray(savedProfiles) ? savedProfiles : [],
    serverInfo: null,
    lastError: '',
    lastProbeResult: null,
    lastConnectedAt: '',
    vmQueryHandle: null,
    vmGpuHandle: null,
    telemetrySocket: null,
    telemetryKey: '',
    telemetryRetryTimer: null,
    liveHostSnapshot: null,
    liveVms: [],
    vmDetailsByVm: {},
    secureBootTemplates: null
  };
  const inflightVmDetails = new Map();
  let livePollTimer = null;
  let inflightVmPoll = null;

  function formatDashedMac(raw = '') {
    const clean = String(raw || '').replace(/[^0-9a-fA-F]/g, '').toUpperCase();
    if (clean.length === 12) return clean.match(/.{1,2}/g).join('-');
    return String(raw || '');
  }

  function mapSwitchTypeLabel(modeVal = '') {
    const m = String(modeVal || '').toLowerCase();
    if (m === 'bridge' || m === 'external') return '外部桥接';
    if (m === 'nat' || m === 'internal') return 'NAT';
    if (m === 'isolated' || m === 'private') return '仅内网';
    return modeVal || '虚拟交换机';
  }

  function emitStateChange(extra = {}) {
    hooks.onStatusChange?.({
      mode: adapterState.mode,
      status: adapterState.status,
      serverUrl: adapterState.serverUrl,
      serverInfo: adapterState.serverInfo,
      lastError: adapterState.lastError,
      lastProbeResult: adapterState.lastProbeResult,
      lastConnectedAt: adapterState.lastConnectedAt,
      rememberSession: adapterState.rememberSession,
      profiles: adapterState.profiles,
      ...extra
    });
  }

  function rememberProfile(url) {
    const clean = String(url || '').trim().replace(/\/+$/, '');
    if (!clean) return;
    const list = [clean, ...adapterState.profiles.filter((u) => u !== clean)].slice(0, 8);
    adapterState.profiles = list;
    adapterState.serverUrl = clean;
    writeLocalJson(STORAGE_PROFILES_KEY, list);
    writeLocalJson(STORAGE_PREFS_KEY, {
      lastUrl: clean,
      rememberSession: adapterState.rememberSession
    });
  }

  function hasToolset(name) {
    const list = adapterState.serverInfo?.toolsets;
    return Array.isArray(list) ? list.includes(name) : true;
  }

  async function ensureVmQueryHandle() {
    if (adapterState.vmQueryHandle) return adapterState.vmQueryHandle;
    adapterState.vmQueryHandle = await client.retainService('M:ExHyperV.Services.VmQueryService.#ctor');
    return adapterState.vmQueryHandle;
  }

  async function ensureVmGpuHandle() {
    if (adapterState.vmGpuHandle) return adapterState.vmGpuHandle;
    const vmQueryHandle = await ensureVmQueryHandle();
    adapterState.vmGpuHandle = await client.retainService(
      'M:ExHyperV.Services.VmGpuService.#ctor(ExHyperV.Services.VmQueryService)',
      { queryService: { $handle: vmQueryHandle } }
    );
    return adapterState.vmGpuHandle;
  }

  function closeTelemetrySocket() {
    if (adapterState.telemetryRetryTimer) {
      clearTimeout(adapterState.telemetryRetryTimer);
      adapterState.telemetryRetryTimer = null;
    }
    adapterState.telemetryKey = '';
    const sock = adapterState.telemetrySocket;
    adapterState.telemetrySocket = null;
    if (sock) {
      try {
        if (sock.readyState === 1) sock.send(JSON.stringify({ type: 'unsubscribe' }));
      } catch {}
      try {
        sock.close();
      } catch {}
    }
  }

  function mapDiskTypeLabel(rawType, isDvd) {
    if (isDvd) return '光盘映像';
    const low = String(rawType || '').toLowerCase();
    if (low.includes('fixed') || low.includes('固定')) return '固定大小';
    if (low.includes('diff') || low.includes('差')) return '差异';
    if (low.includes('phys')) return '物理磁盘';
    return '动态扩展';
  }

  function mapStorageAttachmentPair(st, idx, rawDiskStats = [], prevDisk = null) {
    const driveType = String(pickField(st, 'DriveType', 'driveType') || 'HardDisk');
    const diskType = String(pickField(st, 'DiskType', 'diskType') || 'Virtual');
    const isDvd = driveType === 'DvdDrive';
    const isPhysical = diskType === 'Physical';
    const pathVal = String(pickField(st, 'PathOrDiskNumber', 'pathOrDiskNumber') || '');
    const diskNum = pickField(st, 'DiskNumber', 'diskNumber');
    const diskModel = String(pickField(st, 'DiskModel', 'diskModel') || '');
    const capBytesRaw = pickField(st, 'VirtualCapacityBytes', 'virtualCapacityBytes');
    const capGbRaw = pickField(st, 'VirtualCapacityGiB', 'virtualCapacityGiB') ?? pickField(st, 'DiskSizeGiB', 'diskSizeGiB');
    const fileBytesRaw = pickField(st, 'FileSizeBytes', 'fileSizeBytes');
    const stat = rawDiskStats.find((d) => {
      const dp = String(pickField(d, 'Path', 'path') || '');
      return dp && pathVal && dp.toLowerCase() === pathVal.toLowerCase();
    });
    const capBytes = capBytesRaw ?? pickField(stat, 'MaxSize', 'maxSize') ?? (capGbRaw ? capGbRaw * 1024 ** 3 : null);
    const fileBytes = fileBytesRaw ?? pickField(stat, 'CurrentSize', 'currentSize');
    const vhdType = String(pickField(st, 'VhdType', 'vhdType') || pickField(stat, 'DiskType', 'diskType') || (isDvd ? 'ISO' : 'Dynamic'));
    const vhdFormat = String(
      pickField(st, 'VhdFormat', 'vhdFormat') || (pathVal.toLowerCase().endsWith('.vhd') ? 'VHD' : 'VHDX')
    )
      .replace(/^\./, '')
      .toUpperCase();
    const vhdLabel = mapDiskTypeLabel(vhdType, isDvd);
    const hasIsoMedia = isDvd ? Boolean(pickField(st, 'HasIsoMedia', 'hasIsoMedia') || pathVal) : false;
    const ctrlType = String(pickField(st, 'ControllerType', 'controllerType') || 'SCSI');
    const ctrlNum = Number(pickField(st, 'ControllerNumber', 'controllerNumber') ?? 0);
    const ctrlLoc = Number(pickField(st, 'ControllerLocation', 'controllerLocation') ?? idx);
    const fileName = isPhysical
      ? (diskModel || `PhysicalDrive${diskNum ?? idx}`)
      : pathVal
        ? pathVal.split(/[\\/]/).pop()
        : isDvd
          ? '空光驱'
          : `磁盘 ${idx + 1}`;
    const capText = capGbRaw
      ? `${Number(capGbRaw).toFixed(capGbRaw >= 10 ? 0 : 1)} GB`
      : capBytes
        ? formatBytesCompact(capBytes)
        : '---';
    const fileSizeText = fileBytes != null ? formatBytesCompact(fileBytes) : isPhysical ? capText : '---';
    const readRate = prevDisk?.readRate || (stat ? formatRateBytes(pickField(stat, 'ReadBytesPerSecond')) : '0 B/s');
    const writeRate = prevDisk?.writeRate || (stat ? formatRateBytes(pickField(stat, 'WriteBytesPerSecond')) : '0 B/s');
    const key = String(pickField(st, 'SelectionToken', 'selectionToken') || prevDisk?.key || `disk-${ctrlType}-${ctrlNum}-${ctrlLoc}`);
    const serialNumber = String(pickField(st, 'SerialNumber', 'serialNumber') || '');
    const parentPath = String(pickField(st, 'ParentPath', 'parentPath') || '');
    const isStale = Boolean(pickField(st, 'IsPassthroughStale', 'isPassthroughStale'));

    const diskView = {
      key,
      title: fileName || '虚拟磁盘',
      subtitle: vhdLabel,
      controllerLoc: `${ctrlType} ${ctrlNum}:${ctrlLoc}`,
      controllerType: ctrlType,
      controllerNumber: ctrlNum,
      controllerLocation: ctrlLoc,
      driveType,
      diskType,
      vhdType,
      vhdFormat: vhdLabel,
      glyph: isDvd ? '\uE958' : isPhysical ? '\uEDA2' : '\uEC59',
      fileSizeText,
      capacityText: capText,
      ratio: capBytes && fileBytes ? Math.max(0, Math.min(1, fileBytes / capBytes)) : 0,
      readRate,
      writeRate,
      readPts: prevDisk?.readPts || [0, 0, 0, 0, 0, 0, 0],
      writePts: prevDisk?.writePts || [0, 0, 0, 0, 0, 0, 0],
      readIops: stat ? Number(pickField(stat, 'ReadOperationsPerSecond') || 0) : 0,
      writeIops: stat ? Number(pickField(stat, 'WriteOperationsPerSecond') || 0) : 0,
      queueLength: stat ? Number(pickField(stat, 'QueueLength') || 0) : 0,
      parentPath,
      hasIsoMedia,
      isPassthroughStale: isStale,
      physicalInfo: isPhysical
        ? {
            model: diskModel || fileName,
            diskNumber: diskNum ?? 0,
            serialNumber: serialNumber || '未取得',
            capacityText: capText,
            readRate,
            writeRate,
            readIops: stat ? Number(pickField(stat, 'ReadOperationsPerSecond') || 0) : 0,
            writeIops: stat ? Number(pickField(stat, 'WriteOperationsPerSecond') || 0) : 0
          }
        : undefined,
      path: pathVal,
      _raw: st
    };

    const storageItem = {
      id: key,
      driveType,
      diskType,
      vhdType,
      vhdFormat: `${vhdFormat} ${vhdLabel}`.trim(),
      displayName: fileName,
      sizeText: fileSizeText,
      capacityText: capText,
      parentPath,
      controllerType: ctrlType,
      controllerNumber: ctrlNum,
      controllerLocation: ctrlLoc,
      path: isPhysical && diskNum != null && !pathVal ? `PhysicalDrive${diskNum}` : pathVal,
      hasIsoMedia,
      isPassthroughStale: isStale,
      diskNumber: diskNum,
      diskModel,
      serialNumber,
      expanded: false,
      _raw: st
    };

    return { diskView, storageItem };
  }

  function mapFallbackDiskPair(stat, idx, prevDisk = null) {
    const rawDiskType = String(pickField(stat, 'DiskType', 'diskType') || 'Dynamic');
    const rawPath = String(pickField(stat, 'Path', 'path') || '');
    const rawName = String(pickField(stat, 'Name', 'name') || '');
    const isIso = rawDiskType.toUpperCase() === 'ISO' || rawPath.toLowerCase().endsWith('.iso');
    const isPhysical = rawDiskType.toLowerCase().includes('physical');
    const driveType = isIso ? 'DvdDrive' : 'HardDisk';
    const diskType = isPhysical ? 'Physical' : 'Virtual';
    const maxSize = Number(pickField(stat, 'MaxSize', 'maxSize') || 0);
    const curSize = Number(pickField(stat, 'CurrentSize', 'currentSize') || 0);
    const vhdLabel = mapDiskTypeLabel(rawDiskType, isIso);
    const vhdType = rawDiskType.toLowerCase().includes('fixed')
      ? 'Fixed'
      : rawDiskType.toLowerCase().includes('diff')
        ? 'Differencing'
        : isIso
          ? 'ISO'
          : 'Dynamic';
    const vhdFormat = rawPath.toLowerCase().endsWith('.vhd') ? 'VHD' : 'VHDX';
    const capText = maxSize > 0 ? formatBytesCompact(maxSize) : '---';
    const fileSizeText = curSize > 0 ? formatBytesCompact(curSize) : isIso ? capText : '---';
    const numMatch = rawPath.match(/PhysicalDrive(\d+)/i);
    const diskNumber = numMatch ? Number(numMatch[1]) : idx;
    const readRate = prevDisk?.readRate || formatRateBytes(pickField(stat, 'ReadBytesPerSecond') || 0);
    const writeRate = prevDisk?.writeRate || formatRateBytes(pickField(stat, 'WriteBytesPerSecond') || 0);
    const key = prevDisk?.key || `disk-stat-${idx}`;
    const fileName = rawName || (rawPath ? rawPath.split(/[\\/]/).pop() : `磁盘 ${idx + 1}`);

    const diskView = {
      key,
      title: fileName,
      subtitle: vhdLabel,
      controllerLoc: `SCSI 0:${idx}`,
      controllerType: 'SCSI',
      controllerNumber: 0,
      controllerLocation: idx,
      driveType,
      diskType,
      vhdType,
      vhdFormat: vhdLabel,
      glyph: isIso ? '\uE958' : isPhysical ? '\uEDA2' : '\uEC59',
      fileSizeText,
      capacityText: capText,
      ratio: maxSize > 0 ? Math.max(0, Math.min(1, curSize / maxSize)) : 0,
      readRate,
      writeRate,
      readPts: prevDisk?.readPts || [0, 0, 0, 0, 0, 0, 0],
      writePts: prevDisk?.writePts || [0, 0, 0, 0, 0, 0, 0],
      readIops: Number(pickField(stat, 'ReadOperationsPerSecond') || 0),
      writeIops: Number(pickField(stat, 'WriteOperationsPerSecond') || 0),
      queueLength: Number(pickField(stat, 'QueueLength') || 0),
      parentPath: '',
      hasIsoMedia: isIso && Boolean(rawPath),
      isPassthroughStale: false,
      physicalInfo: isPhysical
        ? {
            model: rawName || `PhysicalDrive${diskNumber}`,
            diskNumber,
            serialNumber: String(pickField(stat, 'PnpDeviceId', 'pnpDeviceId') || '未取得'),
            capacityText: capText,
            readRate,
            writeRate,
            readIops: Number(pickField(stat, 'ReadOperationsPerSecond') || 0),
            writeIops: Number(pickField(stat, 'WriteOperationsPerSecond') || 0)
          }
        : undefined,
      path: rawPath,
      _raw: stat
    };

    const storageItem = {
      id: key,
      driveType,
      diskType,
      vhdType,
      vhdFormat: `${vhdFormat} ${vhdLabel}`.trim(),
      displayName: fileName,
      sizeText: fileSizeText,
      capacityText: capText,
      parentPath: '',
      controllerType: 'SCSI',
      controllerNumber: 0,
      controllerLocation: idx,
      path: rawPath,
      hasIsoMedia: isIso && Boolean(rawPath),
      isPassthroughStale: false,
      diskNumber,
      diskModel: isPhysical ? rawName : '',
      serialNumber: '',
      expanded: false,
      _raw: stat
    };

    return { diskView, storageItem };
  }

  function mapVmInstance(rawVm, existingVm) {
    const id = String(pickField(rawVm, 'Id', 'id') || '');
    const name = String(pickField(rawVm, 'Name', 'name') || id);
    const stateStr = String(pickField(rawVm, 'State', 'state') || 'PoweredOff');
    const isRunning = Boolean(pickField(rawVm, 'IsRunning', 'isRunning')) || RUNNING_STATES.has(stateStr);
    const generation = Number(pickField(rawVm, 'Generation', 'generation') || 2);
    const version = String(pickField(rawVm, 'Version', 'version') || '');
    const osType = String(pickField(rawVm, 'OsType', 'osType') || 'Windows');
    const osMeta = resolveOsDisplay(osType);
    const cores = Math.max(1, Number(pickField(rawVm, 'CpuCount', 'cpuCount') || 1));
    const memGb = Number(pickField(rawVm, 'MemoryGiB', 'memoryGiB') || 0);
    const assignedGb = Number(pickField(rawVm, 'AssignedMemoryGiB', 'assignedMemoryGiB') || memGb);
    const demandGb = Number(pickField(rawVm, 'DemandMemoryGiB', 'demandMemoryGiB') || 0);
    const rawUptime = pickField(rawVm, 'RawUptime', 'rawUptime');
    const ip = String(pickField(rawVm, 'IpAddress', 'ipAddress') || '');
    const mac = String(pickField(rawVm, 'MacAddress', 'macAddress') || '');

    const rawStorage = Array.isArray(pickField(rawVm, 'StorageItems', 'storageItems'))
      ? pickField(rawVm, 'StorageItems', 'storageItems')
      : [];
    const rawDiskStats = Array.isArray(pickField(rawVm, 'Disks', 'disks'))
      ? pickField(rawVm, 'Disks', 'disks')
      : [];
    const cachedDetail = adapterState.vmDetailsByVm[id];
    const effectiveStorage =
      rawStorage.length > 0
        ? rawStorage
        : Array.isArray(cachedDetail?.storageRaw) && cachedDetail.storageRaw.length > 0
          ? cachedDetail.storageRaw
          : [];

    const prevDisks = Array.isArray(existingVm?.disks) ? existingVm.disks : [];
    const pairs =
      effectiveStorage.length > 0
        ? effectiveStorage.map((st, idx) => mapStorageAttachmentPair(st, idx, rawDiskStats, prevDisks[idx]))
        : rawDiskStats.map((stat, idx) => mapFallbackDiskPair(stat, idx, prevDisks[idx]));
    const disks = pairs.map((p) => p.diskView);
    const storageItems = pairs.map((p) => p.storageItem);

    const rawNets = Array.isArray(pickField(rawVm, 'NetworkAdapters', 'networkAdapters'))
      ? pickField(rawVm, 'NetworkAdapters', 'networkAdapters')
      : [];
    const prevAdapters = Array.isArray(existingVm?.adapters) ? existingVm.adapters : [];
    const hostSwitches = Array.isArray(adapterState.liveHostSnapshot?.switches)
      ? adapterState.liveHostSnapshot.switches
      : [];
    const adapters = rawNets.map((net, idx) => {
      const key = String(pickField(net, 'DeviceId', 'deviceId') || `net-${idx}`);
      const prevNet = prevAdapters.find((a) => a.key === key) || prevAdapters[idx];
      const ips = Array.isArray(pickField(net, 'IpAddresses', 'ipAddresses')) ? pickField(net, 'IpAddresses', 'ipAddresses') : [];
      const isConn = Boolean(pickField(net, 'IsConnected', 'isConnected'));
      const switchNameRaw = String(pickField(net, 'SwitchName', 'switchName') || '');
      const connectedReal = isConn && Boolean(switchNameRaw) && switchNameRaw !== '未连接';
      const swObj = connectedReal ? hostSwitches.find((s) => s.name === switchNameRaw) : null;
      const sendBpsRaw = Number(pickField(net, 'SendBytesPerSecond', 'sendBytesPerSecond') || 0);
      const recvBpsRaw = Number(pickField(net, 'ReceiveBytesPerSecond', 'receiveBytesPerSecond') || 0);
      const sendRate = prevNet?.sendRate || formatBitRate(sendBpsRaw);
      const recvRate = prevNet?.recvRate || formatBitRate(recvBpsRaw);
      const vlanMode = String(pickField(net, 'VlanMode', 'vlanMode') || 'Off');
      const accessVlanId = Number(pickField(net, 'AccessVlanId', 'accessVlanId') || 0);
      const macStr = String(pickField(net, 'MacAddress', 'macAddress') || '');
      const adapterTitle = String(pickField(net, 'Name', 'name') || '网络适配器');
      return {
        key,
        id: key,
        name: adapterTitle,
        title: adapterTitle,
        isConnected: connectedReal,
        switchName: connectedReal ? switchNameRaw : '',
        switchType: connectedReal ? mapSwitchTypeLabel(swObj?.mode || prevNet?.switchType || 'NAT') : '—',
        subtitle: connectedReal ? switchNameRaw : '未连接',
        mac: macStr,
        macAddress: macStr,
        isStaticMac: Boolean(pickField(net, 'IsStaticMac', 'isStaticMac')),
        vlanMode: vlanMode === 'Unknown' ? 'Off' : vlanMode,
        accessVlanId,
        vlanText: vlanMode === 'Access' && accessVlanId > 0 ? `VLAN ${accessVlanId}` : vlanMode === 'Trunk' ? 'Trunk' : '未启用',
        ips: ips.length > 0 ? ips : prevNet?.ips || [],
        ipv4: ips.find((x) => x && !x.includes(':')) || prevNet?.ipv4 || '',
        ipv6: ips.find((x) => x && x.includes(':')) || prevNet?.ipv6 || '',
        sendRate,
        recvRate,
        txRate: sendRate,
        rxRate: recvRate,
        sendPts: prevNet?.sendPts || [0, 0, 0, 0, 0, 0, 0],
        recvPts: prevNet?.recvPts || [0, 0, 0, 0, 0, 0, 0],
        _raw: net
      };
    });

    const rawGpus = Array.isArray(pickField(rawVm, 'AssignedGpus', 'assignedGpus'))
      ? pickField(rawVm, 'AssignedGpus', 'assignedGpus')
      : [];
    const gpus = rawGpus.map((gpu, idx) => {
      const memBytes = pickField(gpu, 'MemoryBytes', 'memoryBytes');
      return {
        key: String(pickField(gpu, 'AdapterId', 'adapterId') || `gpu-${idx}`),
        title: String(pickField(gpu, 'Name', 'name') || 'GPU 分区'),
        manu: String(pickField(gpu, 'Manu', 'manu', 'Vendor', 'vendor') || ''),
        vendor: String(pickField(gpu, 'Vendor', 'vendor') || ''),
        memoryText: memBytes != null ? formatBytesCompact(memBytes) : '未知显存',
        driverVersion: String(pickField(gpu, 'DriverVersion', 'driverVersion') || '---'),
        instanceId: String(pickField(gpu, 'HostDeviceInstanceId', 'hostDeviceInstanceId') || ''),
        _raw: gpu
      };
    });

    const diskSpecs = disks.slice(0, 3).map((d) => d.capacityText).filter((t) => t && t !== '---');
    const configParts = [`${cores} 核`, `${memGb.toFixed(memGb % 1 === 0 ? 0 : 1)} GB`];
    if (diskSpecs.length > 0) configParts.push(diskSpecs.join(' + '));

    const prev = existingVm && existingVm.id === id ? existingVm : null;
    const cpuHist = prev?.telemetry?.cpuHistory || [0, 0, 0, 0, 0, 0, 0];
    const memHist = prev?.telemetry?.memHistory || [0, 0, 0, 0, 0, 0, 0];

    return {
      id,
      guid: id.toUpperCase(),
      name,
      gen: generation,
      generation,
      versionText: version ? `版本 ${version}` : '',
      osImage: osMeta.osImage,
      osIcon: osMeta.osIcon,
      osText: osMeta.osText,
      running: isRunning,
      stateRaw: stateStr,
      stateText: mapVmStateText(stateStr),
      uptime: isRunning ? formatTimespanText(rawUptime) : '00:00:00',
      liveUptime: isRunning ? formatTimespanText(rawUptime) : '00:00:00',
      cores,
      memoryGb: assignedGb || memGb,
      usedMemoryGb: prev?.usedMemoryGb ?? (demandGb || 0),
      demandMemoryGb: prev?.demandMemoryGb ?? (demandGb || 0),
      configText: configParts.join(' / '),
      generationText: '',
      gpuText: String(pickField(rawVm, 'GpuName', 'gpuName') || gpus[0]?.title || ''),
      ipWhenRunning: prev?.ipWhenRunning || ip || adapters.flatMap((a) => a.ips).find((x) => x && !x.includes(':')) || '---',
      ipv6WhenRunning: adapters.flatMap((a) => a.ips).find((x) => x && x.includes(':')) || '',
      macAddress: mac,
      gpus,
      disks,
      storageItems,
      adapters,
      processorRaw: pickField(rawVm, 'Processor', 'processor') || cachedDetail?.procRaw || null,
      memoryRaw: pickField(rawVm, 'MemorySettings', 'memorySettings') || cachedDetail?.memRaw || null,
      mmioRaw: pickField(rawVm, 'MmioSettings', 'mmioSettings') || null,
      telemetry: {
        cpuPct: prev?.telemetry?.cpuPct || 0,
        cpuHistory: cpuHist,
        perCoreHistories: prev?.telemetry?.perCoreHistories || {},
        memHistory: memHist,
        gpu3dPct: prev?.telemetry?.gpu3dPct || 0,
        gpuCopyPct: prev?.telemetry?.gpuCopyPct || 0,
        gpuEncPct: prev?.telemetry?.gpuEncPct || 0,
        gpuDecPct: prev?.telemetry?.gpuDecPct || 0,
        gpu3dHistory: prev?.telemetry?.gpu3dHistory || [0, 0, 0, 0, 0, 0, 0],
        gpuCopyHistory: prev?.telemetry?.gpuCopyHistory || [0, 0, 0, 0, 0, 0, 0],
        gpuEncHistory: prev?.telemetry?.gpuEncHistory || [0, 0, 0, 0, 0, 0, 0],
        gpuDecHistory: prev?.telemetry?.gpuDecHistory || [0, 0, 0, 0, 0, 0, 0],
        gpuHistory: prev?.telemetry?.gpuHistory || [0, 0, 0, 0, 0, 0, 0]
      },
      _raw: rawVm
    };
  }

  async function refreshVmList() {
    if (adapterState.mode !== 'live' || !client.isConnected) return [];
    if (!hasToolset('vm')) return [];
    if (inflightVmPoll) return inflightVmPoll;
    inflightVmPoll = (async () => {
      const handle = await ensureVmQueryHandle();
      const rawResult = await client.call('M:ExHyperV.Services.VmQueryService.GetVmListAsync', {}, { targetObjectId: handle });
      const list = Array.isArray(rawResult)
        ? rawResult
        : pickField(rawResult, 'VirtualMachines', 'virtualMachines');
      const prevById = new Map(adapterState.liveVms.map((v) => [v.id, v]));
      const next = Array.isArray(list) ? list.map((raw) => mapVmInstance(raw, prevById.get(String(pickField(raw, 'Id', 'id') || '')))) : [];
      adapterState.liveVms = next;
      if (adapterState.liveHostSnapshot?.sys) {
        const snap = adapterState.liveHostSnapshot;
        snap.liveVms = next;
        snap.sys.runningVmCount = next.filter((v) => v.running).length;
        snap.sys.totalVmCount = next.length;
        snap.sys.vmAllocatedRamGb = Number(next.reduce((sum, v) => sum + (v.running ? v.memoryGb || 0 : 0), 0).toFixed(1));
        snap.sys.vmAllocatedVcpu = next.reduce((sum, v) => sum + (v.running ? v.cores || 0 : 0), 0);
      }
      hooks.onVmsUpdated?.(next);
      return next;
    })().finally(() => {
      inflightVmPoll = null;
    });
    return inflightVmPoll;
  }

  async function safeCall(apiId, args = {}, fallback = null, opts = {}) {
    try {
      return await client.call(apiId, args, opts);
    } catch {
      return fallback;
    }
  }

  async function refreshHostSnapshot() {
    if (adapterState.mode !== 'live' || !client.isConnected) return null;
    if (!hasToolset('host')) return null;

    const [
      sysInfo,
      memTuple,
      hvDiag,
      virtEnabled,
      iommuEnabled,
      schedRunning,
      hostSettings,
      numaSpanning,
      autoMount,
      gpusRaw,
      disksRaw,
      opticalDrivesRaw,
      netInfoTuple,
      cpuGroupsRaw,
      powerPlans,
      activePlanId,
      hasBattery,
      affinityProcs,
      pcieInfoRes,
      vmGroupsRes,
      serverProduct,
      nativeNvme,
      openHcl
    ] = await Promise.all([
      safeCall('M:ExHyperV.Services.HostSystemInfoService.GetSystemInfoAsync'),
      safeCall('M:ExHyperV.Services.HostSystemInfoService.TryReadHostMemoryMiB'),
      safeCall('M:ExHyperV.Services.HyperVHostService.GetHyperVDiagnosticsAsync'),
      safeCall('M:ExHyperV.Services.HyperVHostService.IsVirtualizationEnabled', {}, true),
      safeCall('M:ExHyperV.Services.HyperVHostService.IsIommuEnabled', {}, false),
      safeCall('M:ExHyperV.Services.HyperVSchedulerService.GetSchedulerType', {}, 'Core'),
      safeCall('M:ExHyperV.Services.HyperVHostSettingsService.GetAsync'),
      safeCall('M:ExHyperV.Services.HyperVNumaService.GetNumaSpanningEnabledAsync', {}, true),
      safeCall('M:ExHyperV.Services.HostVolumeAutoMountService.GetState', {}, true),
      safeCall('M:ExHyperV.Services.HostGpuService.GetHostGpusAsync', {}, []),
      safeCall('M:ExHyperV.Services.HostDiskService.GetHostDisksAsync', {}, []),
      safeCall('M:ExHyperV.Services.HostDiskService.GetHostOpticalDrivesAsync', {}, []),
      safeCall('M:ExHyperV.Services.HyperVSwitchService.GetNetworkInfoAsync'),
      safeCall('M:ExHyperV.Services.HostCpuGroupService.GetAllAsync(System.Threading.CancellationToken)', {}, []),
      safeCall('M:ExHyperV.Services.HostPowerPlanService.GetPowerPlans', {}, []),
      safeCall('M:ExHyperV.Services.HostPowerPlanService.GetActivePowerPlanId', {}, ''),
      safeCall('M:ExHyperV.Services.HostPowerPlanService.HasBattery', {}, false),
      safeCall('M:ExHyperV.Services.HostProcessorService.GetAffinityProcessorsAsync', {}, []),
      safeCall('M:ExHyperV.Services.HostPcieService.GetPCIeInfoAsync', {}, null),
      safeCall('M:ExHyperV.Services.VmGroupService.ReadAsync', {}, null),
      safeCall('M:ExHyperV.Services.HostProductTypeService.IsServerSystem', {}, false),
      safeCall('M:ExHyperV.Services.HostNvmeService.IsNativeNvmeEnabled', {}, false),
      safeCall('M:ExHyperV.Services.HostOpenHclService.IsFirmwareLoadFromFileEnabled', {}, false)
    ]);

    const totalMiB = pickField(memTuple, 'Item2', 'TotalMiB', 'item2') ?? 0;
    const availMiB = pickField(memTuple, 'Item1', 'AvailableMiB', 'item1') ?? 0;
    const totalGb = totalMiB > 0 ? totalMiB / 1024 : 16;
    const usedGb = totalMiB > 0 ? Math.max(0, (totalMiB - availMiB) / 1024) : 0;
    const memPct = totalGb > 0 ? Math.round((usedGb / totalGb) * 100) : 0;
    const lpCount = Array.isArray(affinityProcs) && affinityProcs.length > 0 ? affinityProcs.length : 12;
    const physicalCores = Array.isArray(affinityProcs) && affinityProcs.length > 0
      ? new Set(affinityProcs.map((p) => pickField(p, 'CoreId', 'coreId')).filter((x) => x !== null && x !== undefined)).size || lpCount
      : lpCount;

    const hvReady = Boolean(pickField(hvDiag, 'IsReady', 'isReady'));
    const hvInstalled = pickField(hvDiag, 'IsInstalled', 'isInstalled');
    const vmmsState = String(pickField(pickField(hvDiag, 'VmmsService', 'vmmsService'), 'State', 'state') || '');
    let hvServiceState = 'ready';
    if (hvInstalled === false) hvServiceState = 'not-installed';
    else if (!hvReady && vmmsState && vmmsState !== 'Running') hvServiceState = 'vmms-stopped';
    else if (!hvReady) hvServiceState = 'unknown';

    const switchesRaw = Array.isArray(pickField(netInfoTuple, 'Item1', 'item1')) ? pickField(netInfoTuple, 'Item1', 'item1') : [];
    const upstreamsRaw = Array.isArray(pickField(netInfoTuple, 'Item2', 'item2')) ? pickField(netInfoTuple, 'Item2', 'item2') : [];

    const mappedGpus = (Array.isArray(gpusRaw) ? gpusRaw : []).map((g, idx) => {
      const memBytes = pickField(g, 'MemoryBytes', 'memoryBytes');
      const instanceId = String(pickField(g, 'InstanceId', 'instanceId') || `gpu-${idx}`);
      const assignedVms = adapterState.liveVms
        .filter((vm) =>
          (vm.gpus || []).some((vg) => (vg.instanceId || '').toLowerCase() === instanceId.toLowerCase())
        )
        .map((vm) => vm.name);
      return {
        id: instanceId,
        name: String(pickField(g, 'Name', 'name') || '显卡'),
        manu: String(pickField(g, 'Manu', 'manu', 'Vendor', 'vendor') || ''),
        vendor: String(pickField(g, 'Vendor', 'vendor', 'Manu', 'manu') || ''),
        vram: memBytes != null ? formatBytesCompact(memBytes) : '未知显存',
        driver: String(pickField(g, 'DriverVersion', 'driverVersion') || '---'),
        instanceId,
        partitionable: Boolean(pickField(g, 'IsPartitionable', 'isPartitionable')),
        assignedVms,
        pct: 0,
        pts3d: [0, 0, 0, 0, 0, 0, 0],
        ptsCopy: [0, 0, 0, 0, 0, 0, 0],
        ptsEnc: [0, 0, 0, 0, 0, 0, 0],
        ptsDec: [0, 0, 0, 0, 0, 0, 0]
      };
    });

    const mappedDisks = (Array.isArray(disksRaw) ? disksRaw : []).map((d, idx) => {
      const num = pickField(d, 'Number', 'number') ?? idx;
      const sizeGiB = Number(pickField(d, 'SizeGiB', 'sizeGiB') || 0);
      const sizeText = sizeGiB >= 1000 ? `${(sizeGiB / 1024).toFixed(2)} TB` : `${sizeGiB.toFixed(1)} GB`;
      const isOffline = Boolean(pickField(d, 'IsOffline', 'isOffline'));
      const canPassthrough = Boolean(pickField(d, 'CanPassthrough', 'canPassthrough'));
      const friendlyName = String(pickField(d, 'FriendlyName', 'friendlyName') || `物理磁盘 ${num}`);
      const serial = String(pickField(d, 'SerialNumber', 'serialNumber') || '---');
      const busType = String(pickField(d, 'BusType', 'busType') || 'Disk');
      const statusText = String(pickField(d, 'Status', 'status') || (isOffline ? 'Offline' : 'Online'));
      return {
        id: `disk-${num}`,
        number: num,
        name: friendlyName,
        friendlyName,
        model: friendlyName,
        busType,
        sizeText,
        serial,
        serialNumber: serial,
        path: `\\\\.\\PhysicalDrive${num}`,
        status: statusText,
        offline: isOffline,
        readOnly: false,
        canPassthrough,
        unavailabilityReason: canPassthrough ? '' : String(pickField(d, 'UnavailabilityReason', 'unavailabilityReason') || '包含系统引导卷或当前不可直通'),
        pct: 0,
        readRate: '0 B/s',
        writeRate: '0 B/s',
        readPts: [0, 0, 0, 0, 0, 0, 0],
        writePts: [0, 0, 0, 0, 0, 0, 0]
      };
    });

    const mappedOpticalDrives = (Array.isArray(opticalDrivesRaw) ? opticalDrivesRaw : []).map((o, idx) => ({
      id: `opt-${idx}`,
      number: idx,
      drive: String(pickField(o, 'Drive', 'drive') || ''),
      model: String(pickField(o, 'Model', 'model') || `物理光驱 ${idx}`),
      busType: 'CDROM',
      mediaStatus: '在位',
      pnpDeviceId: String(pickField(o, 'PnpDeviceId', 'pnpDeviceId') || ''),
      canPassthrough: true,
      unavailabilityReason: ''
    }));

    const mappedSwitches = switchesRaw.map((sw, idx) => {
      const swName = String(pickField(sw, 'SwitchName', 'switchName') || `交换机 ${idx + 1}`);
      const swId = String(pickField(sw, 'Id', 'id') || `sw-${idx}`);
      const modeVal = String(pickField(sw, 'SwitchType', 'switchType') || 'Isolated');
      const kindVal = String(pickField(sw, 'SwitchKind', 'switchKind') || 'Private');
      const upstreamObj = pickField(sw, 'Upstream', 'upstream');
      const upstreamName = upstreamObj ? String(pickField(upstreamObj, 'DisplayName', 'displayName', 'Name', 'name') || '') : '';
      const endpoints = adapterState.liveVms
        .filter((vm) => (vm.adapters || []).some((a) => a.subtitle === swName))
        .map((vm) => vm.name);
      return {
        id: swId,
        name: swName,
        switchKind: kindVal,
        natEngine: String(pickField(sw, 'NatEngine', 'natEngine') || 'None'),
        subnetPrefix: pickField(sw, 'SubnetPrefix', 'subnetPrefix') || null,
        mode: modeVal,
        desc: mapSwitchTypeLabel(modeVal),
        allowManagementOs: Boolean(pickField(sw, 'AllowManagementOS', 'allowManagementOS')),
        upstream: upstreamName || '无宿主或外网连接',
        upstreamName,
        ipv4: String(pickField(sw, 'SubnetPrefix', 'subnetPrefix') || '---'),
        sendRate: '0 Kbps',
        recvRate: '0 Kbps',
        sendPts: [0, 0, 0, 0, 0, 0, 0],
        recvPts: [0, 0, 0, 0, 0, 0, 0],
        endpoints,
        expanded: idx === 0
      };
    });

    const mappedNics = upstreamsRaw.map((nic, idx) => {
      const rawKind = String(pickField(nic, 'MediaKind', 'mediaKind') || 'Wired');
      const media = rawKind.toLowerCase();
      const linkSpeedBps = Number(pickField(nic, 'LinkSpeed', 'linkSpeed') || 0);
      const nicName = String(pickField(nic, 'Name', 'name') || `网卡 ${idx + 1}`);
      const nicDesc = String(pickField(nic, 'Description', 'description') || '');
      const boundSw = mappedSwitches.find(
        (sw) => sw.upstreamName && (sw.upstreamName === nicName || sw.upstreamName === nicDesc)
      );
      return {
        id: String(pickField(nic, 'ConnectionId', 'connectionId') || `nic-${idx}`),
        deviceId: String(pickField(nic, 'DeviceId', 'deviceId') || ''),
        name: nicName,
        media: media === 'wireless' ? 'wifi' : media,
        canBridge: Boolean(pickField(nic, 'CanBridge', 'canBridge')),
        mediaNote: rawKind,
        description: nicDesc,
        boundSwitch: boundSw ? boundSw.name : '空闲',
        linkUp: Boolean(pickField(nic, 'LinkUp', 'linkUp')),
        speed: linkSpeedBps > 0 ? formatBitRate(linkSpeedBps / 8) : '---',
        sendRate: '0 Kbps',
        recvRate: '0 Kbps',
        sendPts: [0, 0, 0, 0, 0, 0, 0],
        recvPts: [0, 0, 0, 0, 0, 0, 0],
        ipv4: String(pickField(nic, 'ConnectionProfileName', 'connectionProfileName') || '---'),
        mac: String(pickField(nic, 'MacAddress', 'macAddress') || '---')
      };
    });

    const mapPcieGlyph = (cls) => {
      const c = String(cls || '').toLowerCase();
      if (c.includes('display')) return '\uF211';
      if (c.includes('net')) return '\uE774';
      if (c.includes('scsi') || c.includes('disk')) return '\uEC59';
      if (c.includes('usb')) return '\uECF0';
      return '\uE964';
    };

    const pcieRawDevs = Array.isArray(pickField(pcieInfoRes, 'Devices', 'devices'))
      ? pickField(pcieInfoRes, 'Devices', 'devices')
      : [];
    const mappedPcieDevices = pcieRawDevs.map((pd, idx) => {
      const assign = pickField(pd, 'Assignment', 'assignment') || {};
      const assignKind = String(pickField(assign, 'Kind', 'kind') || 'Host');
      const ownerVmId = String(pickField(assign, 'VirtualMachineId', 'virtualMachineId') || '');
      const ownerVmName = String(pickField(assign, 'VirtualMachineName', 'virtualMachineName') || '');
      const ownerKind = assignKind === 'VirtualMachine' ? 'vm' : assignKind === 'Host' ? 'host' : 'unassigned';
      const instId = String(pickField(pd, 'InstanceId', 'instanceId') || `host-pcie-${idx}`);
      const cls = String(pickField(pd, 'ClassType', 'classType') || 'System');
      return {
        id: instId,
        name: String(pickField(pd, 'FriendlyName', 'friendlyName') || 'PCIe 设备'),
        classType: cls,
        displayClassType: String(pickField(pd, 'DisplayClassType', 'displayClassType') || cls),
        glyph: mapPcieGlyph(cls),
        vendor: String(pickField(pd, 'Vendor', 'vendor') || ''),
        instanceId: instId,
        path: String(pickField(pd, 'Path', 'path') || ''),
        ownerKind,
        ownerVmId,
        ownerVmName,
        ownerName: ownerKind === 'vm' ? ownerVmName : ownerKind === 'host' ? '宿主' : '已从宿主卸下（待分配）',
        canPassthrough: Boolean(pickField(pd, 'CanPassthrough', 'canPassthrough') ?? true),
        unavailabilityReason: '',
        expanded: false,
        _raw: pd
      };
    });

    const vmGroupRaw = Array.isArray(pickField(vmGroupsRes, 'Groups', 'groups'))
      ? pickField(vmGroupsRes, 'Groups', 'groups')
      : [];
    const mappedVmGroups = vmGroupRaw.map((g, idx) => ({
      id: String(pickField(g, 'Id', 'id') || `vg-${idx}`),
      name: String(pickField(g, 'Name', 'name') || '分组'),
      kind: String(pickField(g, 'Kind', 'kind') || 'VirtualMachines'),
      children: pickField(g, 'Children', 'children') || [],
      vms: pickField(g, 'VmIds', 'vmIds') || []
    }));

    const mappedAffinityProcs = (Array.isArray(affinityProcs) ? affinityProcs : []).map((p, idx) => ({
      index: Number(pickField(p, 'Index', 'index') ?? idx),
      packageId: Number(pickField(p, 'PackageId', 'packageId') ?? 0),
      coreId: Number(pickField(p, 'CoreId', 'coreId') ?? idx),
      numaNode: Number(pickField(p, 'NumaNode', 'numaNode') ?? 0),
      coreType: String(pickField(p, 'CoreType', 'coreType') || 'Performance')
    }));

    const mappedCpuGroups = (Array.isArray(cpuGroupsRaw) ? cpuGroupsRaw : []).map((grp) => ({
      groupId: String(pickField(grp, 'GroupId', 'groupId') || ''),
      revision: String(pickField(grp, 'Revision', 'revision') || ''),
      logicalProcessors: pickField(grp, 'LogicalProcessors', 'logicalProcessors') || [],
      cpuCap: pickField(grp, 'CpuCap', 'cpuCap'),
      priority: pickField(grp, 'Priority', 'priority'),
      idleProcessorReserve: pickField(grp, 'IdleProcessorReserve', 'idleProcessorReserve'),
      relativeWeight: pickField(grp, 'RelativeWeight', 'relativeWeight'),
      members: pickField(grp, 'Members', 'members') || []
    }));

    const infoUrlHost = (() => {
      try {
        return new URL(adapterState.serverUrl).host;
      } catch {
        return 'HYPERV-HOST';
      }
    })();

    const hostSys = {
      os: String(pickField(sysInfo, 'Caption', 'caption') || 'Windows Hyper-V 宿主机'),
      arch: String(pickField(sysInfo, 'OSArchitecture', 'osArchitecture') || adapterState.serverInfo?.osArchitecture || 'x64'),
      hostName: infoUrlHost,
      uptime: '实时在线',
      cpu: String(pickField(sysInfo, 'CpuModel', 'cpuModel') || '宿主处理器'),
      cpuShort: String(pickField(sysInfo, 'CpuModel', 'cpuModel') || 'CPU').slice(0, 28),
      cores: physicalCores,
      threads: lpCount,
      baseClock: '---',
      curClock: '---',
      numaNodes: 1,
      memoryGb: Number(totalGb.toFixed(1)),
      usedMemoryGb: Number(usedGb.toFixed(1)),
      cpuPct: adapterState.liveHostSnapshot?.sys?.cpuPct || 0,
      cpuHistory: adapterState.liveHostSnapshot?.sys?.cpuHistory || [0, 0, 0, 0, 0, 0, 0],
      coreHistories: adapterState.liveHostSnapshot?.sys?.coreHistories || Array.from({ length: lpCount }, () => [0, 0, 0, 0, 0, 0, 0]),
      memHistory: adapterState.liveHostSnapshot?.sys?.memHistory || [memPct, memPct, memPct, memPct, memPct, memPct, memPct],
      memPct,
      memNote: pickField(sysInfo, 'MemCap', 'memCap') || `物理内存 ${totalGb.toFixed(1)} GB`,
      runningVmCount: adapterState.liveVms.filter((v) => v.running).length,
      totalVmCount: adapterState.liveVms.length,
      vmAllocatedRamGb: Number(adapterState.liveVms.reduce((sum, v) => sum + (v.running ? v.memoryGb || 0 : 0), 0).toFixed(1)),
      vmAllocatedVcpu: adapterState.liveVms.reduce((sum, v) => sum + (v.running ? v.cores || 0 : 0), 0)
    };

    const pciePolicyRelaxed = await safeCall('M:ExHyperV.Services.HostGpuService.GetAssignmentRestrictionsRelaxed', {}, false);

    const snapshot = {
      sys: hostSys,
      hyperVServiceState: hvServiceState,
      hyperVEnabled: hvReady || hvInstalled === true,
      cpuVirtOk: Boolean(virtEnabled),
      gpuPvOk: mappedGpus.some((g) => g.partitionable),
      iommuOk: Boolean(iommuEnabled),
      isWindowsServer: Boolean(serverProduct),
      saved: {
        powerPlanId: String(activePlanId || 'balanced'),
        scheduler: String(schedRunning || 'Core'),
        numaSpanning: Boolean(numaSpanning),
        defaultVmPath: String(pickField(hostSettings, 'DefaultVirtualMachinePath', 'defaultVirtualMachinePath') || ''),
        defaultVhdPath: String(pickField(hostSettings, 'DefaultVirtualHardDiskPath', 'defaultVirtualHardDiskPath') || ''),
        enhancedSession: Boolean(pickField(hostSettings, 'EnhancedSessionModeEnabled', 'enhancedSessionModeEnabled')),
        volumeAutoMount: Boolean(autoMount),
        macMin: formatDashedMac(pickField(hostSettings, 'MinimumMacAddress', 'minimumMacAddress') || '00-15-5D-01-00-00'),
        macMax: formatDashedMac(pickField(hostSettings, 'MaximumMacAddress', 'maximumMacAddress') || '00-15-5D-01-FF-FF'),
        pciePolicyUnlocked: Boolean(pciePolicyRelaxed),
        serverProductTypeSpoof: Boolean(serverProduct),
        nativeNvme: Boolean(nativeNvme),
        openHclFirmware: Boolean(openHcl),
        autoOpenConsole: false,
        defaultPowerAction: 'Shutdown'
      },
      powerHasBattery: Boolean(hasBattery),
      powerPlans: Array.isArray(powerPlans)
        ? powerPlans.map((p) => ({ id: String(pickField(p, 'Id', 'id')), name: String(pickField(p, 'Name', 'name')) }))
        : [],
      nics: mappedNics,
      switches: mappedSwitches,
      gpus: mappedGpus,
      disks: mappedDisks,
      opticalDrives: mappedOpticalDrives,
      pcieDevices: mappedPcieDevices,
      vmGroups: mappedVmGroups,
      affinityProcs: mappedAffinityProcs,
      cpuGroups: mappedCpuGroups,
      liveVms: adapterState.liveVms
    };

    adapterState.liveVms.forEach((vm) => {
      (vm.adapters || []).forEach((a) => {
        if (a.isConnected && a.subtitle) {
          const sw = mappedSwitches.find((s) => s.name === a.subtitle);
          if (sw) a.switchType = mapSwitchTypeLabel(sw.mode);
        }
      });
    });

    adapterState.liveHostSnapshot = snapshot;
    hooks.onHostUpdated?.(snapshot);
    return snapshot;
  }

  function applyTelemetryPacket(msg) {
    if (!msg || msg.type !== 'telemetry' || adapterState.mode !== 'live') return;

    if (msg.host && adapterState.liveHostSnapshot) {
      const h = msg.host;
      const snap = adapterState.liveHostSnapshot;
      const sys = snap.sys;

      if (Array.isArray(h.cpu) && h.cpu.length > 0) {
        let sum = 0;
        let validCount = 0;
        const nextCores = Array.isArray(sys.coreHistories) ? [...sys.coreHistories] : [];
        h.cpu.forEach((c, idx) => {
          const coreIdx = typeof c.coreId === 'number' ? c.coreId : idx;
          const usage = typeof c.usage === 'number' ? c.usage : null;
          if (usage !== null) {
            sum += usage;
            validCount++;
            nextCores[coreIdx] = pushSeries(nextCores[coreIdx], Math.round(usage));
          }
        });
        if (validCount > 0) {
          const avg = Math.round(sum / validCount);
          sys.cpuPct = avg;
          sys.cpuHistory = pushSeries(sys.cpuHistory, avg);
          sys.coreHistories = nextCores;
          sys.threads = Math.max(sys.threads || 1, h.cpu.length);
        }
      }

      if (h.memory && typeof h.memory.totalMiB === 'number' && h.memory.totalMiB > 0) {
        const totalGb = h.memory.totalMiB / 1024;
        const usedGb = Math.max(0, (h.memory.totalMiB - (h.memory.availableMiB || 0)) / 1024);
        const pct = Math.round((usedGb / totalGb) * 100);
        sys.memoryGb = Number(totalGb.toFixed(1));
        sys.usedMemoryGb = Number(usedGb.toFixed(1));
        sys.memPct = pct;
        sys.memHistory = pushSeries(sys.memHistory, pct);
      }

      if (h.gpu && typeof h.gpu === 'object' && !h.gpu.issue) {
        snap.gpus.forEach((gpu) => {
          const reading = h.gpu[gpu.id] || Object.entries(h.gpu).find(([k]) => k.toLowerCase() === gpu.id.toLowerCase())?.[1];
          if (reading && !reading.issue) {
            const p3d = Math.round(Number(reading.pct3D ?? 0));
            const pCopy = Math.round(Number(reading.pctCopy ?? 0));
            const pEnc = Math.round(Number(reading.pctEnc ?? 0));
            const pDec = Math.round(Number(reading.pctDec ?? 0));
            const peak = Math.max(p3d, pCopy, pEnc, pDec);
            gpu.pct = peak;
            gpu.pts3d = pushSeries(gpu.pts3d, p3d);
            gpu.ptsCopy = pushSeries(gpu.ptsCopy, pCopy);
            gpu.ptsEnc = pushSeries(gpu.ptsEnc, pEnc);
            gpu.ptsDec = pushSeries(gpu.ptsDec, pDec);
          }
        });
      }

      if (h.disk && typeof h.disk === 'object' && !h.disk.issue) {
        snap.disks.forEach((disk) => {
          const reading = h.disk[String(disk.number)] ?? h.disk[disk.number];
          if (reading && !reading.issue) {
            const pct = Math.round(Number(reading.activePercent || 0));
            const readBps = Number(reading.readBytesPerSec || 0);
            const writeBps = Number(reading.writeBytesPerSec || 0);
            disk.pct = pct;
            disk.readRate = formatRateBytes(readBps);
            disk.writeRate = formatRateBytes(writeBps);
            disk.readPts = pushSeries(disk.readPts, Math.min(100, Math.round(pct || readBps / (1024 * 1024))));
            disk.writePts = pushSeries(disk.writePts, Math.min(100, Math.round(writeBps / (1024 * 1024))));
          }
        });
      }

      if (h.network && typeof h.network === 'object' && !h.network.issue) {
        snap.nics.forEach((nic) => {
          const entry = h.network[nic.id];
          const tp = entry?.reading?.throughput;
          if (tp) {
            const send = Number(tp.bytesSentPerSec || 0);
            const recv = Number(tp.bytesReceivedPerSec || 0);
            nic.sendRate = formatBitRate(send);
            nic.recvRate = formatBitRate(recv);
            nic.sendPts = pushSeries(nic.sendPts, Math.min(100, Math.round((send * 8) / 1e6)));
            nic.recvPts = pushSeries(nic.recvPts, Math.min(100, Math.round((recv * 8) / 1e6)));
          }
        });
      }

      if (h.hostThreads && Array.isArray(h.hostThreads.threads)) {
        snap.liveHostThreadReport = h.hostThreads;
      }

      sys.runningVmCount = adapterState.liveVms.filter((v) => v.running).length;
      sys.totalVmCount = adapterState.liveVms.length;
      hooks.onHostTelemetryTick?.(snap);
    }

    if (msg.vms && typeof msg.vms === 'object') {
      let changed = false;
      const isValidSample = (entry) =>
        entry &&
        typeof entry.value === 'number' &&
        Number.isFinite(entry.value) &&
        (entry.state === 'Available' || entry.state === 'Valid' || entry.state === 1 || entry.state == null);
      for (const [vmId, vmMetrics] of Object.entries(msg.vms)) {
        const vm = adapterState.liveVms.find((v) => v.id.toLowerCase() === vmId.toLowerCase());
        if (!vm || !vmMetrics || typeof vmMetrics !== 'object') continue;
        changed = true;
        const cpuVals = vmMetrics.cpu?.values || {};
        const cpuUsageObj = Object.values(cpuVals).find((x) => isValidSample(x));
        if (cpuUsageObj) {
          const pct = Number(Math.max(0, Math.min(100, cpuUsageObj.value)).toFixed(1));
          vm.telemetry.cpuPct = pct;
          vm.telemetry.cpuHistory = pushSeries(vm.telemetry.cpuHistory, Math.round(pct));
        }
        const coreRows = vmMetrics.cpu?.collections?.Processors?.rows;
        if (Array.isArray(coreRows)) {
          vm.telemetry.perCoreHistories ||= {};
          coreRows.forEach((row, idx) => {
            const keyIdx = Number(String(row.key || '').split(':').pop());
            const coreIdx = Number.isFinite(keyIdx) ? keyIdx : idx;
            const cVal = Object.values(row.values || {}).find((x) => isValidSample(x));
            if (cVal) {
              vm.telemetry.perCoreHistories[coreIdx] = pushSeries(
                vm.telemetry.perCoreHistories[coreIdx],
                Math.round(Math.max(0, Math.min(100, cVal.value)))
              );
            }
          });
        }
        const memVals = vmMetrics.memory?.values || {};
        const assignedEntry = Object.entries(memVals).find(([k]) => k.toLowerCase().includes('assigned'));
        const demandEntry = Object.entries(memVals).find(([k]) => k.toLowerCase().includes('demand'));
        const guestAvailEntry = Object.entries(memVals).find(([k]) => k.toLowerCase().includes('guestavailable'));
        const assignedMiB = isValidSample(assignedEntry?.[1]) ? assignedEntry[1].value : null;
        const demandMiB = isValidSample(demandEntry?.[1]) ? demandEntry[1].value : null;
        const guestAvailMiB = isValidSample(guestAvailEntry?.[1]) ? guestAvailEntry[1].value : null;
        if (assignedMiB !== null && assignedMiB > 0) {
          vm.memoryGb = Number((assignedMiB / 1024).toFixed(2));
        }
        if (demandMiB !== null) {
          vm.demandMemoryGb = Number((demandMiB / 1024).toFixed(2));
        }
        if (guestAvailMiB !== null && vm.memoryGb > 0) {
          const usedGiB = Math.max(0, Math.min(vm.memoryGb, vm.memoryGb - guestAvailMiB / 1024));
          vm.usedMemoryGb = Number(usedGiB.toFixed(2));
        } else if (demandMiB !== null && vm.memoryGb > 0) {
          const clampedGiB = Math.max(0, Math.min(vm.memoryGb, demandMiB / 1024));
          vm.usedMemoryGb = Number(clampedGiB.toFixed(2));
        }
        if (vm.memoryGb > 0 && (guestAvailMiB !== null || demandMiB !== null)) {
          const mp = Math.min(100, Math.max(0, Math.round((vm.usedMemoryGb / vm.memoryGb) * 100)));
          vm.telemetry.memHistory = pushSeries(vm.telemetry.memHistory, mp);
        }

        const diskRows = vmMetrics.disk?.collections?.Storage?.rows;
        if (Array.isArray(diskRows) && Array.isArray(vm.disks) && vm.disks.length > 0) {
          diskRows.forEach((row, idx) => {
            const rVals = row.values || {};
            const readEntry = Object.entries(rVals).find(([k]) => k.toLowerCase().includes('readbytes'))?.[1];
            const writeEntry = Object.entries(rVals).find(([k]) => k.toLowerCase().includes('writebytes'))?.[1];
            const keyParts = String(row.key || '').split('\\');
            const locCandidate = keyParts.length >= 4 ? Number(keyParts[keyParts.length - 2]) : NaN;
            const targetDisk =
              vm.disks.find((d) => Number.isFinite(locCandidate) && d.controllerLocation === locCandidate) || vm.disks[idx];
            if (targetDisk) {
              if (isValidSample(readEntry)) {
                const readBps = Number(readEntry.value || 0);
                targetDisk.readRate = formatRateBytes(readBps);
                targetDisk.readPts = pushSeries(targetDisk.readPts, Math.min(100, Math.round(readBps / (512 * 1024))));
              }
              if (isValidSample(writeEntry)) {
                const writeBps = Number(writeEntry.value || 0);
                targetDisk.writeRate = formatRateBytes(writeBps);
                targetDisk.writePts = pushSeries(targetDisk.writePts, Math.min(100, Math.round(writeBps / (512 * 1024))));
              }
            }
          });
        }

        const netRows = vmMetrics.network?.collections?.NetworkAdapters?.rows;
        if (Array.isArray(netRows) && Array.isArray(vm.adapters)) {
          netRows.forEach((row, idx) => {
            const nVals = row.values || {};
            const sendEntry = Object.entries(nVals).find(([k]) => k.toLowerCase().includes('sendbytes'))?.[1];
            const recvEntry = Object.entries(nVals).find(([k]) => k.toLowerCase().includes('receivebytes'))?.[1];
            const macVal = String(Object.entries(nVals).find(([k]) => k.toLowerCase().includes('macaddress'))?.[1]?.value || '');
            const devPart = String(row.key || '').split('\\').pop()?.toLowerCase() || '';
            const adapter =
              vm.adapters.find((a) => devPart && a.key.toLowerCase() === devPart) ||
              vm.adapters.find((a) => macVal && a.mac.toLowerCase() === macVal.toLowerCase()) ||
              vm.adapters[idx];
            if (adapter) {
              if (isValidSample(sendEntry)) {
                const sendBps = Number(sendEntry.value || 0);
                adapter.sendRate = formatBitRate(sendBps);
                adapter.txRate = adapter.sendRate;
                adapter.sendPts = pushSeries(adapter.sendPts, Math.min(100, Math.round((sendBps * 8) / 1e6)));
              }
              if (isValidSample(recvEntry)) {
                const recvBps = Number(recvEntry.value || 0);
                adapter.recvRate = formatBitRate(recvBps);
                adapter.rxRate = adapter.recvRate;
                adapter.recvPts = pushSeries(adapter.recvPts, Math.min(100, Math.round((recvBps * 8) / 1e6)));
              }
              if (typeof row.observedIPv4 === 'string' && row.observedIPv4) {
                adapter.ipv4 = row.observedIPv4;
                if (!adapter.ips.includes(row.observedIPv4)) adapter.ips = [row.observedIPv4, ...adapter.ips];
                vm.ipWhenRunning = row.observedIPv4;
              }
            }
          });
        }

        const netIp = vmMetrics.network?.observedIPv4;
        if (typeof netIp === 'string' && netIp) {
          vm.ipWhenRunning = netIp;
        }
        const gpuVals = vmMetrics.gpu?.values || {};
        const g3dEntry = Object.entries(gpuVals).find(([k]) => k.toLowerCase().includes('3d'))?.[1];
        const gCopyEntry = Object.entries(gpuVals).find(([k]) => k.toLowerCase().includes('copy'))?.[1];
        const gEncEntry = Object.entries(gpuVals).find(([k]) => k.toLowerCase().includes('encode'))?.[1];
        const gDecEntry = Object.entries(gpuVals).find(([k]) => k.toLowerCase().includes('decode'))?.[1];
        if (isValidSample(g3dEntry) || isValidSample(gCopyEntry) || isValidSample(gEncEntry) || isValidSample(gDecEntry)) {
          const g3d = isValidSample(g3dEntry) ? Math.round(Math.max(0, Math.min(100, g3dEntry.value))) : vm.telemetry.gpu3dPct;
          const gCopy = isValidSample(gCopyEntry) ? Math.round(Math.max(0, Math.min(100, gCopyEntry.value))) : vm.telemetry.gpuCopyPct;
          const gEnc = isValidSample(gEncEntry) ? Math.round(Math.max(0, Math.min(100, gEncEntry.value))) : vm.telemetry.gpuEncPct;
          const gDec = isValidSample(gDecEntry) ? Math.round(Math.max(0, Math.min(100, gDecEntry.value))) : vm.telemetry.gpuDecPct;
          vm.telemetry.gpu3dPct = g3d;
          vm.telemetry.gpuCopyPct = gCopy;
          vm.telemetry.gpuEncPct = gEnc;
          vm.telemetry.gpuDecPct = gDec;
          vm.telemetry.gpu3dHistory = pushSeries(vm.telemetry.gpu3dHistory, g3d);
          vm.telemetry.gpuCopyHistory = pushSeries(vm.telemetry.gpuCopyHistory, gCopy);
          vm.telemetry.gpuEncHistory = pushSeries(vm.telemetry.gpuEncHistory, gEnc);
          vm.telemetry.gpuDecHistory = pushSeries(vm.telemetry.gpuDecHistory, gDec);
          vm.telemetry.gpuHistory = vm.telemetry.gpu3dHistory;
        }
      }
      if (changed) hooks.onVmTelemetryTick?.(adapterState.liveVms);
    }
  }

  async function syncTelemetrySubscription({ host = false, vmIds = [] } = {}) {
    if (adapterState.mode !== 'live' || !client.isConnected) {
      closeTelemetrySocket();
      return;
    }

    const wantHost = Boolean(host) && hasToolset('host');
    const cleanVmIds = hasToolset('vm')
      ? Array.from(new Set(vmIds.map((x) => String(x || '').trim()).filter((x) => /^[0-9a-f-]{36}$/i.test(x)))).slice(0, 256)
      : [];

    if (!wantHost && cleanVmIds.length === 0) {
      closeTelemetrySocket();
      return;
    }

    const metrics = wantHost
      ? ['cpu', 'memory', 'disk', 'network', 'gpu', 'hostThreads']
      : ['cpu', 'memory', 'disk', 'network', 'gpu'];
    const subMsg = { type: 'subscribe', host: wantHost, vmIds: cleanVmIds, metrics, intervalMs: 1000 };
    const nextKey = JSON.stringify(subMsg);

    if (adapterState.telemetrySocket && adapterState.telemetrySocket.readyState === 1) {
      if (adapterState.telemetryKey === nextKey) return;
      adapterState.telemetryKey = nextKey;
      adapterState.telemetrySocket.send(nextKey);
      return;
    }

    closeTelemetrySocket();
    try {
      const sock = await client.openWebSocket('telemetry');
      if (adapterState.mode !== 'live') {
        sock.close();
        return;
      }
      adapterState.telemetrySocket = sock;
      adapterState.telemetryKey = nextKey;
      sock.addEventListener('open', () => {
        if (adapterState.mode === 'live' && sock.readyState === 1) {
          sock.send(nextKey);
        }
      });
      sock.addEventListener('message', (ev) => {
        if (typeof ev.data !== 'string') return;
        try {
          applyTelemetryPacket(JSON.parse(ev.data));
        } catch {}
      });
      sock.addEventListener('close', () => {
        if (adapterState.telemetrySocket === sock) {
          adapterState.telemetrySocket = null;
          adapterState.telemetryKey = '';
          if (adapterState.mode === 'live' && client.isConnected) {
            adapterState.telemetryRetryTimer = setTimeout(() => {
              syncTelemetrySubscription({ host: wantHost, vmIds: cleanVmIds }).catch(() => {});
            }, 3000);
          }
        }
      });
    } catch {}
  }

  async function probeServer(url) {
    adapterState.serverUrl = String(url || '').trim();
    adapterState.lastError = '';
    adapterState.lastProbeResult = null;
    try {
      const health = await client.probeHealth(adapterState.serverUrl);
      adapterState.lastProbeResult = {
        ok: true,
        version: pickField(health, 'version') || '未知版本',
        authRequired: Boolean(pickField(health, 'authRequired'))
      };
      emitStateChange();
      return adapterState.lastProbeResult;
    } catch (err) {
      adapterState.lastProbeResult = { ok: false, error: err.message };
      adapterState.lastError = err.message;
      emitStateChange();
      throw err;
    }
  }

  async function connectToServer({ url, password = '', rootToken = '', rememberSession = false, useStoredSession = true } = {}) {
    closeTelemetrySocket();
    adapterState.serverUrl = String(url || adapterState.serverUrl || '').trim();
    adapterState.rememberSession = Boolean(rememberSession);
    adapterState.status = 'connecting';
    adapterState.lastError = '';
    emitStateChange();

    try {
      const session = await client.connect({
        url: adapterState.serverUrl,
        password,
        rootToken,
        rememberSession,
        useStoredSession
      });
      rememberProfile(session.url);
      adapterState.mode = 'live';
      adapterState.status = 'connected';
      adapterState.serverInfo = session.info;
      adapterState.lastConnectedAt = new Date().toLocaleTimeString('zh-CN', { hour12: false });
      adapterState.lastProbeResult = {
        ok: true,
        version: session.health?.version || session.info?.version || '',
        authRequired: Boolean(session.health?.authRequired)
      };

      await refreshVmList();
      await refreshHostSnapshot();
      if (livePollTimer) {
        clearInterval(livePollTimer);
        livePollTimer = null;
      }
      livePollTimer = setInterval(() => {
        if (adapterState.mode === 'live' && client.isConnected) {
          void refreshVmList();
        }
      }, 4000);
      emitStateChange();
      hooks.onModeSwitched?.('live');
      return session;
    } catch (err) {
      if (livePollTimer) {
        clearInterval(livePollTimer);
        livePollTimer = null;
      }
      adapterState.mode = 'demo';
      adapterState.status = 'error';
      adapterState.serverInfo = null;
      adapterState.lastError = err.message || '连接 ExHyperV.Server 失败';
      emitStateChange();
      throw err;
    }
  }

  async function switchToDemoMode() {
    if (livePollTimer) {
      clearInterval(livePollTimer);
      livePollTimer = null;
    }
    closeTelemetrySocket();
    adapterState.vmQueryHandle = null;
    adapterState.vmGpuHandle = null;
    await client.disconnect();
    adapterState.mode = 'demo';
    adapterState.status = 'demo';
    adapterState.serverInfo = null;
    adapterState.lastError = '';
    adapterState.liveHostSnapshot = null;
    adapterState.liveVms = [];
    adapterState.vmDetailsByVm = {};
    adapterState.secureBootTemplates = null;
    inflightVmDetails.clear();
    emitStateChange();
    hooks.onModeSwitched?.('demo');
  }

  async function executeVmPower(vmId, actionName) {
    const mapAction = {
      start: 'Start',
      stop: 'Stop',
      shutdown: 'Stop',
      turnoff: 'TurnOff',
      restart: 'Restart',
      save: 'Save',
      pause: 'Suspend',
      suspend: 'Suspend'
    };
    const action = mapAction[String(actionName || '').toLowerCase()] || actionName;
    await client.call(
      'M:ExHyperV.Services.VmPowerService.ExecuteControlActionAsync(System.Guid,ExHyperV.Models.VmPowerAction)',
      { vmId, action }
    );
    await refreshVmList();
  }

  async function setVmOsType(vmId, osType) {
    await client.call(
      'M:ExHyperV.Services.VmMetadataService.SetOperatingSystemTypeAsync(System.Guid,System.String)',
      { vmId, osType }
    );
    await refreshVmList();
  }

  async function executeHostAction(action, payload = {}) {
    if (adapterState.mode !== 'live' || !client.isConnected) return false;
    switch (action) {
      case 'scheduler':
        await client.call('M:ExHyperV.Services.HyperVSchedulerService.SetSchedulerTypeAsync(ExHyperV.Services.HyperVSchedulerType)', {
          type: payload.type
        });
        break;
      case 'powerPlan':
        await client.call('M:ExHyperV.Services.HostPowerPlanService.SetActivePowerPlan(System.Guid)', {
          planId: payload.planId
        });
        break;
      case 'defaultVmPath':
        await client.call('M:ExHyperV.Services.HyperVHostSettingsService.SetDefaultVirtualMachinePathAsync(System.String)', {
          path: payload.path
        });
        break;
      case 'defaultVhdPath':
        await client.call('M:ExHyperV.Services.HostDiskService.SetDefaultVirtualHardDiskPathAsync(System.String)', {
          path: payload.path
        });
        break;
      case 'enhancedSession':
        await client.call('M:ExHyperV.Services.HostConsoleService.SetEnhancedSessionModeEnabledAsync(System.Boolean)', {
          enabled: Boolean(payload.enabled)
        });
        break;
      case 'numaSpanning':
        await client.call('M:ExHyperV.Services.HyperVNumaService.SetNumaSpanningEnabledAsync(System.Boolean)', {
          enabled: Boolean(payload.enabled)
        });
        break;
      case 'volumeAutoMount':
        await client.call('M:ExHyperV.Services.HostVolumeAutoMountService.SetEnabledAsync(System.Boolean)', {
          enabled: Boolean(payload.enabled)
        });
        break;
      case 'pciePolicyUnlocked':
        await client.call('M:ExHyperV.Services.HostGpuService.SetAssignmentRestrictionsRelaxed(System.Boolean)', {
          relaxed: Boolean(payload.enabled)
        });
        break;
      case 'nativeNvme':
        await client.call('M:ExHyperV.Services.HostNvmeService.SetNativeNvmeEnabled(System.Boolean)', {
          enabled: Boolean(payload.enabled)
        });
        break;
      case 'serverProductTypeSpoof':
        await client.call('M:ExHyperV.Services.HostProductTypeService.ApplySwitch(System.Boolean)', {
          toServer: Boolean(payload.enabled)
        });
        break;
      case 'openHclFirmware':
        await client.call('M:ExHyperV.Services.HostOpenHclService.SetFirmwareLoadFromFileEnabled(System.Boolean)', {
          enabled: Boolean(payload.enabled)
        });
        break;
      case 'macRange':
        await client.call('M:ExHyperV.Services.HostNetworkService.SetDynamicMacAddressRangeAsync(System.String,System.String)', {
          minimum: payload.minMac,
          maximum: payload.maxMac
        });
        break;
      case 'createSwitch':
        await client.call(
          'M:ExHyperV.Services.HyperVSwitchService.CreateSwitchAsync(System.String,ExHyperV.Models.SwitchMode,ExHyperV.Models.SwitchUpstreamAdapter)',
          { name: payload.name, mode: payload.mode || 'Isolated', upstream: payload.upstream || null }
        );
        break;
      case 'toggleHyperV':
        await client.call('M:ExHyperV.Services.HyperVHostService.SetHyperVEnabledAsync(System.Boolean)', {
          enabled: Boolean(payload.enabled)
        });
        break;
      default:
        return false;
    }
    await refreshHostSnapshot();
    return true;
  }

  async function saveProcessorSettings(vmId, values = {}, changedProperties = []) {
    if (!changedProperties || changedProperties.length === 0) return;
    const base = adapterState.vmDetailsByVm[vmId]?.procRaw || {};
    const payload = { ...base };
    for (const k of changedProperties) {
      if (k === 'Reservation' || k === 'Limit') {
        payload[k] = Math.round(Number(values[k] ?? 0) * 1000);
      } else {
        payload[k] = values[k];
      }
    }
    await client.call(
      'M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})',
      { vmId, newSettings: payload, changedProperties }
    );
    await Promise.all([refreshVmList(), loadVmDetails(vmId, true)]);
  }

  async function saveProcessorCount(vmId, rawSettings, nextCount) {
    await saveProcessorSettings(vmId, { Count: Number(nextCount) }, ['Count']);
  }

  async function saveCpuAffinity(vmId, coreIndices = []) {
    await client.call(
      'M:ExHyperV.Services.CpuAffinityService.SaveAsync(System.Guid,System.Collections.Generic.List{System.Int32})',
      { vmId, coreIndices: (coreIndices || []).map((n) => Number(n)) }
    );
    await loadVmDetails(vmId, true);
  }

  async function saveMemorySettings(vmId, values = {}, changedProperties = []) {
    if (!changedProperties || changedProperties.length === 0) return;
    const base = adapterState.vmDetailsByVm[vmId]?.memRaw || {};
    const payload = { ...base, ...values };
    await client.call(
      'M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})',
      { vmId, newSettings: payload, changedProperties }
    );
    await Promise.all([refreshVmList(), loadVmDetails(vmId, true)]);
  }

  const KNOWN_SB_TEMPLATE_GUIDS = {
    'Microsoft Windows': '1734c6e8-3154-4dda-ba5f-a874cc483422',
    'Microsoft UEFI 证书颁发机构': '272e7447-90a4-4563-a4b9-8e4ab00526ce',
    '受防护的开源 VM': '4292ae2b-ee2c-42b5-a969-dd8f8689f6f3'
  };

  async function saveSecuritySetting(vmId, key, value) {
    if (key === 'secureBoot') {
      await client.call('M:ExHyperV.Services.VmSecurityService.SetSecureBootAsync(System.Guid,System.Boolean)', {
        vmId,
        enabled: Boolean(value)
      });
    } else if (key === 'secureBootTemplate') {
      const templates = adapterState.secureBootTemplates || [];
      let templateId = KNOWN_SB_TEMPLATE_GUIDS[value] || '';
      if (!templateId) {
        const hit = templates.find((t) => String(t.Name ?? t.name ?? '') === value);
        templateId = hit ? String(hit.Id ?? hit.id ?? '') : '';
      }
      if (!templateId) throw new Error('未能解析安全启动模板 GUID');
      await client.call('M:ExHyperV.Services.VmSecurityService.SetSecureBootTemplateAsync(System.Guid,System.Guid)', {
        vmId,
        templateId
      });
    } else if (key === 'encryptState') {
      await client.call('M:ExHyperV.Services.VmSecurityService.SetEncryptionAsync(System.Guid,System.Boolean)', {
        vmId,
        enabled: Boolean(value)
      });
    } else if (key === 'shielding') {
      await client.call('M:ExHyperV.Services.VmSecurityService.SetShieldingAsync(System.Guid,System.Boolean)', {
        vmId,
        enabled: Boolean(value)
      });
    } else if (key === 'tpmEnabled') {
      await client.call('M:ExHyperV.Services.VmSecurityService.SetTpmAsync(System.Guid,System.Boolean)', {
        vmId,
        enabled: Boolean(value)
      });
    }
    await loadVmDetails(vmId, true);
  }

  async function savePowerSetting(vmId, key, enabled) {
    if (key === 'hibernation') {
      await client.call('M:ExHyperV.Services.VmPowerService.SetHibernationEnabledAsync(System.Guid,System.Boolean)', {
        vmId,
        enabled: Boolean(enabled)
      });
    } else if (key === 'turnOffOnRestart') {
      await client.call('M:ExHyperV.Services.VmPowerService.SetTurnOffOnGuestRestartAsync(System.Guid,System.Boolean)', {
        vmId,
        enabled: Boolean(enabled)
      });
    } else if (key === 'batteryPassthrough') {
      await client.call('M:ExHyperV.Services.VmPowerService.SetBatteryEnabledAsync(System.Guid,System.Boolean)', {
        vmId,
        enabled: Boolean(enabled)
      });
    }
    await loadVmDetails(vmId, true);
  }

  async function saveConsoleSetting(vmId, key, val) {
    if (key === 'consoleSupport') {
      await client.call('M:ExHyperV.Services.VmConsoleSupportService.SetConsoleSupportAsync(System.Guid,System.Boolean)', {
        vmId,
        enable: Boolean(val)
      });
    } else if (key === 'lockOnDisconnect') {
      await client.call('M:ExHyperV.Services.VmConsoleSupportService.SetLockOnDisconnectAsync(System.Guid,System.Boolean)', {
        vmId,
        enabled: Boolean(val)
      });
    } else if (key === 'enhancedFeatures') {
      await client.call(
        'M:ExHyperV.Services.VmConsoleSupportService.SetEnhancedFeaturesAsync(System.Guid,System.Boolean,System.Boolean,System.Boolean)',
        {
          vmId,
          clipboard: val?.clipboard !== false,
          audioOutput: val?.audioOutput !== false,
          audioInput: val?.audioInput !== false
        }
      );
    } else if (key === 'resolution') {
      const m = String(val || '').match(/^(\d+)\s*[xX×]\s*(\d+)$/);
      if (m) {
        await client.call('M:ExHyperV.Services.VmDisplaySettingsService.SetResolutionAsync(System.Guid,System.Int32,System.Int32,System.Int32)', {
          vmId,
          resolutionType: 3,
          width: Number(m[1]),
          height: Number(m[2])
        });
      } else {
        await client.call('M:ExHyperV.Services.VmDisplaySettingsService.SetResolutionAsync(System.Guid,System.Int32,System.Int32,System.Int32)', {
          vmId,
          resolutionType: 4,
          width: 0,
          height: 0
        });
      }
    }
    await loadVmDetails(vmId, true);
  }

  async function saveCompatSetting(vmId, key, enabled) {
    if (key === 'fullScsi') {
      await client.call('M:ExHyperV.Services.VmStorageService.SetFullScsiCommandSetEnabledAsync(System.Guid,System.Boolean)', {
        vmId,
        enabled: Boolean(enabled)
      });
    } else if (key === 'bootVpci') {
      await client.call('M:ExHyperV.Services.VmPcieService.SetBootPciExpressAsync(System.Guid,System.Boolean)', {
        vmId,
        enabled: Boolean(enabled)
      });
    } else if (key === 'numLock') {
      await client.call('M:ExHyperV.Services.VmBootService.SetBootNumLockAsync(System.Guid,System.Boolean)', {
        vmId,
        enable: Boolean(enabled)
      });
    }
    await loadVmDetails(vmId, true);
  }

  async function saveSerialPort(vmId, portNumber, pipePath, debuggerMode) {
    await client.call(
      'M:ExHyperV.Services.VmSerialPortService.SetPortSettingsAsync(System.Guid,System.Int32,System.String,System.Nullable{System.Boolean})',
      { vmId, portNumber: Number(portNumber), pipePath: pipePath || '', debuggerMode }
    );
    await loadVmDetails(vmId, true);
  }

  async function executeCheckpointAction(vmId, action, payload = {}) {
    if (action === 'create') {
      await client.call('M:ExHyperV.Services.VmCheckpointService.CreateSnapshotAsync(System.Guid)', { vmId });
    } else if (action === 'restore' && payload.node?._raw) {
      const nodes = (adapterState.vmDetailsByVm[vmId]?.checkpointNodes || []).map((n) => n._raw).filter(Boolean);
      await client.call(
        'M:ExHyperV.Services.VmCheckpointService.ApplySnapshotAsync(ExHyperV.Models.CheckpointNode,System.Guid,System.Collections.Generic.IEnumerable{ExHyperV.Models.CheckpointNode})',
        { node: payload.node._raw, vmId, nodes }
      );
    } else if (action === 'rename' && payload.node?._raw) {
      await client.call(
        'M:ExHyperV.Services.VmCheckpointService.RenameSnapshotAsync(System.Guid,ExHyperV.Models.CheckpointNode,System.String)',
        { vmId, node: payload.node._raw, newName: payload.newName || '' }
      );
    } else if (action === 'delete' && payload.node?._raw) {
      await client.call(
        'M:ExHyperV.Services.VmCheckpointService.DeleteSnapshotAsync(System.Guid,ExHyperV.Models.CheckpointNode)',
        { vmId, node: payload.node._raw }
      );
    } else if (action === 'merge' && payload.node?._raw) {
      await client.call(
        'M:ExHyperV.Services.VmCheckpointService.MergeSnapshotAsync(System.Guid,ExHyperV.Models.CheckpointNode)',
        { vmId, node: payload.node._raw }
      );
    }
    await Promise.all([refreshVmList(), loadVmDetails(vmId, true)]);
  }

  async function removeVmGpu(vmId, gpuAssignmentRaw) {
    const handle = await ensureVmGpuHandle();
    await client.call(
      'M:ExHyperV.Services.VmGpuService.RemoveGpuPartitionAsync(System.Guid,ExHyperV.Models.VmGpuAssignment)',
      { vmId, assignment: gpuAssignmentRaw },
      { targetObjectId: handle }
    );
    await refreshVmList();
  }

  function mapBootKindGlyph(kind) {
    const k = String(kind ?? '').toLowerCase();
    if (k === 'opticaldrive' || k === '2') return '\uE958';
    if (k === 'harddisk' || k === 'floppy' || k === '3' || k === '1') return '\uEDA2';
    if (k === 'network' || k === '4') return '\uE774';
    return '\uE74C';
  }

  function mapCheckpointNodes(rawList) {
    const arr = Array.isArray(rawList) ? rawList : [];
    const mapped = arr.map((n, idx) => {
      const rawType = String(pickField(n, 'NodeType', 'nodeType') || 'Snapshot');
      const isCurrent = Boolean(pickField(n, 'IsCurrent', 'isCurrent'));
      const rawDate = String(pickField(n, 'CreatedDate', 'createdDate') || '');
      const dateMatch = rawDate.match(/^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})/);
      const dateStr = dateMatch ? `${dateMatch[1]} ${dateMatch[2]}` : rawDate.slice(0, 19).replace('T', ' ');
      const vhdPath = String(pickField(n, 'VhdPath', 'vhdPath') || '');
      return {
        id: String(pickField(n, 'Id', 'id') || `cp-${idx}`),
        parentId: pickField(n, 'ParentId', 'parentId') || null,
        name: String(pickField(n, 'Name', 'name') || '检查点'),
        date: dateStr,
        type: rawType === 'Origin' ? 'Origin' : rawType === 'Current' ? 'Current' : 'Snapshot',
        isCurrent,
        historicalDisk: Boolean(pickField(n, 'IsHistoricalDiskMounted', 'isHistoricalDiskMounted')),
        diskName: vhdPath ? vhdPath.split(/[\\/]/).pop() : '',
        vhdPath,
        note: vhdPath || '',
        _raw: n
      };
    });
    if (mapped.length > 0 && !mapped.some((m) => m.type === 'Current')) {
      const activeLeaf = mapped.find((m) => m.isCurrent) || mapped[mapped.length - 1];
      mapped.push({
        id: 'CURRENT_STATE',
        parentId: activeLeaf?.id || null,
        name: '当前运行状态',
        date: '现在',
        type: 'Current',
        isCurrent: true,
        historicalDisk: false,
        note: ''
      });
    }
    return mapped;
  }

  async function loadVmDetails(vmId, force = false) {
    if (!vmId || adapterState.mode !== 'live' || !client.isConnected || !hasToolset('vm')) return null;
    if (!force && adapterState.vmDetailsByVm[vmId]) {
      return adapterState.vmDetailsByVm[vmId];
    }
    if (!force && inflightVmDetails.has(vmId)) {
      return inflightVmDetails.get(vmId);
    }

    const reqPromise = (async () => {
      const args = { vmId };
      const [
        storageRes,
        bootRes,
        numLockRes,
        cpRes,
        cpEnabledRes,
        procRes,
        memRes,
        affinityRes,
        netAdaptersRes,
        secRes,
        vtpmRes,
        serialRes,
        consoleRes,
        consoleSupportRes,
        consoleFeaturesRes,
        displayRes,
        batteryRes,
        pcieRes,
        sbTemplates
      ] = await Promise.all([
        safeCall('M:ExHyperV.Services.VmStorageService.GetVmStorageItemsAsync(System.Guid)', args, []),
        safeCall('M:ExHyperV.Services.VmBootService.GetBootOrderAsync(System.Guid)', args, []),
        safeCall('M:ExHyperV.Services.VmBootService.GetBootNumLockAsync(System.Guid)', args, false),
        safeCall('M:ExHyperV.Services.VmCheckpointService.GetCheckpointNodesAsync(System.Guid)', args, []),
        safeCall('M:ExHyperV.Services.VmCheckpointService.GetCheckpointsEnabledAsync(System.Guid)', args, true),
        safeCall('M:ExHyperV.Services.VmProcessorService.GetVmProcessorAsync(System.Guid)', args, null),
        safeCall('M:ExHyperV.Services.VmMemoryService.GetVmMemorySettingsAsync(System.Guid)', args, null),
        safeCall('M:ExHyperV.Services.CpuAffinityService.ReadStateAsync(System.Guid)', args, null),
        safeCall('M:ExHyperV.Services.VmNetworkService.GetNetworkAdaptersAsync(System.Guid)', args, []),
        safeCall('M:ExHyperV.Services.VmSecurityService.GetSecuritySettingsAsync(System.Guid)', args, null),
        safeCall('M:ExHyperV.Services.VmSecurityService.GetVtpmStateAsync(System.Guid)', args, null),
        safeCall('M:ExHyperV.Services.VmSerialPortService.GetSerialPortsAsync(System.Guid)', args, []),
        safeCall('M:ExHyperV.Services.VmConsoleSupportService.GetConsoleStateAsync(System.Guid)', args, null),
        safeCall('M:ExHyperV.Services.VmConsoleSupportService.IsConsoleSupportEnabledAsync(System.Guid)', args, null),
        safeCall('M:ExHyperV.Services.VmConsoleSupportService.GetEnhancedFeaturesAsync(System.Guid)', args, null),
        safeCall('M:ExHyperV.Services.VmDisplaySettingsService.GetResolutionAsync(System.Guid)', args, null),
        safeCall('M:ExHyperV.Services.VmPowerService.GetBatteryStateAsync(System.Guid)', args, null),
        safeCall('M:ExHyperV.Services.VmPcieService.GetSettingsAsync(System.Guid)', args, null),
        adapterState.secureBootTemplates
          ? Promise.resolve(adapterState.secureBootTemplates)
          : safeCall('M:ExHyperV.Services.VmSecurityService.GetSecureBootTemplatesAsync', {}, [])
      ]);
      if (!adapterState.secureBootTemplates && Array.isArray(sbTemplates) && sbTemplates.length > 0) {
        adapterState.secureBootTemplates = sbTemplates;
      }

      const storageRaw = Array.isArray(storageRes) ? storageRes : [];
      const vm = adapterState.liveVms.find((v) => v.id === vmId);
      const rawDiskStats = Array.isArray(pickField(vm?._raw, 'Disks', 'disks')) ? pickField(vm._raw, 'Disks', 'disks') : [];
      const prevDisks = Array.isArray(vm?.disks) ? vm.disks : [];
      const pairs =
        storageRaw.length > 0
          ? storageRaw.map((st, idx) => mapStorageAttachmentPair(st, idx, rawDiskStats, prevDisks[idx]))
          : rawDiskStats.map((stat, idx) => mapFallbackDiskPair(stat, idx, prevDisks[idx]));

      const disks = pairs.map((p) => p.diskView);
      const storageItems = pairs.map((p) => p.storageItem);

      const bootRaw = Array.isArray(bootRes) ? bootRes : [];
      const bootEntries = bootRaw.map((b, idx) => ({
        id: `boot-${vmId}-${idx}`,
        name: String(pickField(b, 'Name', 'name') || `引导项 ${idx + 1}`),
        description: String(pickField(b, 'Description', 'description') || ''),
        kind: String(pickField(b, 'Kind', 'kind') || 'FirmwareFile'),
        glyph: mapBootKindGlyph(pickField(b, 'Kind', 'kind')),
        _raw: b
      }));

      const checkpointNodes = mapCheckpointNodes(cpRes);
      const procValues = pickField(procRes, 'Values', 'values') || procRes || null;
      const memValues = pickField(memRes, 'Values', 'values') || memRes || null;
      const procStates = pickField(procRes, 'States', 'states') || null;
      const memStates = pickField(memRes, 'States', 'states') || null;
      const prevAdapters = Array.isArray(vm?.adapters) ? vm.adapters : [];
      const hostSwitches = Array.isArray(adapterState.liveHostSnapshot?.switches)
        ? adapterState.liveHostSnapshot.switches
        : [];
      const networkAdapters = (Array.isArray(netAdaptersRes) ? netAdaptersRes : []).map((n, idx) => {
        const key = String(pickField(n, 'DeviceId', 'deviceId') || prevAdapters[idx]?.key || `net-${idx}`);
        const prevNet = prevAdapters.find((a) => a.key === key) || prevAdapters[idx];
        const isConn = Boolean(pickField(n, 'IsConnected', 'isConnected'));
        const switchNameRaw = String(pickField(n, 'SwitchName', 'switchName') || '');
        const connectedReal = isConn && Boolean(switchNameRaw) && switchNameRaw !== '未连接';
        const swObj = connectedReal ? hostSwitches.find((s) => s.name === switchNameRaw) : null;
        const rawMac = String(pickField(n, 'MacAddress', 'macAddress') || '');
        const nicName = String(pickField(n, 'Name', 'name') || '网络适配器');
        const vlanRaw = String(pickField(n, 'VlanMode', 'vlanMode') || 'Off');
        const vlanMode = vlanRaw === 'Unknown' || vlanRaw === 'Untagged' ? 'Off' : vlanRaw;
        const accessVlanId = Number(pickField(n, 'AccessVlanId', 'accessVlanId') || 0);
        const trunkArr = pickField(n, 'TrunkAllowedVlanIds', 'trunkAllowedVlanIds');
        const limitBps = Number(pickField(n, 'BandwidthLimitBitsPerSecond', 'bandwidthLimitBitsPerSecond') || 0);
        const ips = Array.isArray(pickField(n, 'IpAddresses', 'ipAddresses'))
          ? pickField(n, 'IpAddresses', 'ipAddresses')
          : prevNet?.ips || [];
        return {
          key,
          id: key,
          name: nicName,
          title: nicName,
          isConnected: connectedReal,
          switchName: connectedReal ? switchNameRaw : '',
          switchType: connectedReal ? mapSwitchTypeLabel(swObj?.mode || prevNet?.switchType || 'NAT') : '—',
          subtitle: connectedReal ? switchNameRaw : '未连接',
          mac: rawMac,
          macAddress: rawMac,
          isStaticMac: Boolean(pickField(n, 'IsStaticMac', 'isStaticMac')),
          isLegacy: Boolean(pickField(n, 'IsLegacy', 'isLegacy')),
          vlanMode,
          accessVlanId,
          nativeVlanId: Number(pickField(n, 'NativeVlanId', 'nativeVlanId') || 1),
          trunkAllowedVlans: Array.isArray(trunkArr) ? trunkArr.join(',') : '',
          pvlanMode: String(pickField(n, 'PvlanMode', 'pvlanMode') || 'Isolated'),
          pvlanPrimaryId: Number(pickField(n, 'PvlanPrimaryId', 'pvlanPrimaryId') || 100),
          pvlanSecondaryId: Number(pickField(n, 'PvlanSecondaryId', 'pvlanSecondaryId') || 101),
          vlanText: vlanMode === 'Access' && accessVlanId > 0 ? `VLAN ${accessVlanId}` : vlanMode === 'Trunk' ? 'Trunk' : '未启用',
          bandwidthLimitMbps: limitBps > 0 ? Math.round(limitBps / 1_000_000) : 0,
          macSpoofing: Boolean(pickField(n, 'MacSpoofingAllowed', 'macSpoofingAllowed')),
          teamingAllowed: Boolean(pickField(n, 'TeamingAllowed', 'teamingAllowed')),
          dhcpGuard: Boolean(pickField(n, 'DhcpGuardEnabled', 'dhcpGuardEnabled')),
          routerGuard: Boolean(pickField(n, 'RouterGuardEnabled', 'routerGuardEnabled')),
          monitorMode: String(pickField(n, 'MonitorMode', 'monitorMode') || 'None'),
          stormLimit: Number(pickField(n, 'StormLimit', 'stormLimit') || 0),
          sriovEnabled: Boolean(pickField(n, 'SriovEnabled', 'sriovEnabled')),
          vmqEnabled: Boolean(pickField(n, 'VmqEnabled', 'vmqEnabled')),
          ipsecOffloadEnabled: Boolean(pickField(n, 'IpsecOffloadEnabled', 'ipsecOffloadEnabled')),
          ips,
          ipv4: ips.find((x) => x && !x.includes(':')) || prevNet?.ipv4 || '',
          ipv6: ips.find((x) => x && x.includes(':')) || prevNet?.ipv6 || '',
          sendRate: prevNet?.sendRate || '0 bps',
          recvRate: prevNet?.recvRate || '0 bps',
          txRate: prevNet?.txRate || '0 bps',
          rxRate: prevNet?.rxRate || '0 bps',
          sendPts: prevNet?.sendPts || [0, 0, 0, 0, 0, 0, 0],
          recvPts: prevNet?.recvPts || [0, 0, 0, 0, 0, 0, 0],
          expanded: false,
          _raw: n
        };
      });

      const serialPorts = (Array.isArray(serialRes) ? serialRes : []).map((p, idx) => ({
        portNumber: Number(pickField(p, 'PortNumber', 'portNumber') || idx + 1),
        name: String(pickField(p, 'Name', 'name') || `COM ${idx + 1}`),
        pipePath: String(pickField(p, 'PipePath', 'pipePath') || ''),
        debuggerMode: Boolean(pickField(p, 'DebuggerMode', 'debuggerMode')),
        debuggerModeAvailable: Boolean(pickField(p, 'DebuggerModeAvailable', 'debuggerModeAvailable')),
        isConnected: Boolean(pickField(p, 'IsConnected', 'isConnected')),
        _raw: p
      }));

      const pcieDevs = (Array.isArray(pickField(pcieRes, 'Devices', 'devices')) ? pickField(pcieRes, 'Devices', 'devices') : []).map((d, idx) => ({
        id: String(pickField(d, 'DeviceInstanceId', 'deviceInstanceId') || `pcie-${idx}`),
        name: String(pickField(d, 'Name', 'name') || 'PCIe 设备'),
        classType: String(pickField(d, 'ClassType', 'classType') || ''),
        vendor: String(pickField(d, 'Vendor', 'vendor') || ''),
        instanceId: String(pickField(d, 'DeviceInstanceId', 'deviceInstanceId') || ''),
        path: String(pickField(d, 'Path', 'path') || ''),
        guestMode: String(pickField(d, 'GuestMode', 'guestMode') ?? '0'),
        expanded: false,
        _raw: d
      }));

      const detail = {
        vmId,
        storageRaw,
        disks,
        storageItems,
        bootEntries,
        numLock: Boolean(numLockRes),
        checkpointNodes,
        checkpointsEnabled: cpEnabledRes !== false,
        procRaw: procValues,
        procStates,
        memRaw: memValues,
        memStates,
        affinityState: affinityRes || null,
        networkAdapters,
        sec: secRes || null,
        vtpm: vtpmRes || null,
        serialPorts,
        consoleState: consoleRes || null,
        consoleSupportEnabled: consoleSupportRes,
        consoleFeatures: consoleFeaturesRes || { clipboard: true, audioOutput: true, audioInput: true },
        displayResolution: displayRes || null,
        battery: batteryRes || null,
        pcie: pcieRes || null,
        pcieDevices: pcieDevs,
        secureBootTemplates: adapterState.secureBootTemplates || sbTemplates || []
      };

      adapterState.vmDetailsByVm[vmId] = detail;
      if (vm) {
        vm.disks = disks;
        vm.storageItems = storageItems;
        if (networkAdapters.length > 0) vm.adapters = networkAdapters;
        if (procValues) vm.processorRaw = procValues;
        if (memValues) vm.memoryRaw = memValues;
        const diskSpecs = disks.slice(0, 3).map((d) => d.capacityText).filter((t) => t && t !== '---');
        const parts = [`${vm.cores} 核`, `${vm.memoryGb.toFixed(vm.memoryGb % 1 === 0 ? 0 : 1)} GB`];
        if (diskSpecs.length > 0) parts.push(diskSpecs.join(' + '));
        vm.configText = parts.join(' / ');
      }
      hooks.onVmDetailsUpdated?.(vmId, detail);
      return detail;
    })().finally(() => {
      inflightVmDetails.delete(vmId);
    });

    inflightVmDetails.set(vmId, reqPromise);
    return reqPromise;
  }

  async function saveVmBootOrder(vmId, bootEntries) {
    const rawItems = (bootEntries || []).map((b) => b._raw).filter(Boolean);
    await client.call(
      'M:ExHyperV.Services.VmBootService.SetBootOrderAsync(System.Guid,System.Collections.Generic.List{ExHyperV.Models.BootOrderItem})',
      { vmId, items: rawItems }
    );
    await loadVmDetails(vmId, true);
  }

  async function setVmBootNumLock(vmId, enabled) {
    await client.call('M:ExHyperV.Services.VmBootService.SetBootNumLockAsync(System.Guid,System.Boolean)', {
      vmId,
      enabled: Boolean(enabled)
    });
    await loadVmDetails(vmId, true);
  }

  async function issueConsoleTicket(vmId, mode, watchOnly = false, ceiling = null) {
    if (adapterState.mode !== 'live' || !client.isConnected) return null;
    return requestConsoleConnection(client, vmId, mode, watchOnly, ceiling);
  }

  return {
    client,
    get state() {
      return adapterState;
    },
    get isLive() {
      return adapterState.mode === 'live' && client.isConnected;
    },
    probeServer,
    connectToServer,
    switchToDemoMode,
    refreshVmList,
    refreshHostSnapshot,
    syncTelemetrySubscription,
    executeVmPower,
    setVmOsType,
    executeHostAction,
    saveProcessorSettings,
    saveProcessorCount,
    saveCpuAffinity,
    saveMemorySettings,
    saveSecuritySetting,
    savePowerSetting,
    saveConsoleSetting,
    saveCompatSetting,
    saveSerialPort,
    executeCheckpointAction,
    removeVmGpu,
    loadVmDetails,
    saveVmBootOrder,
    setVmBootNumLock,
    issueConsoleTicket,
    setRememberSession: (val) => {
      adapterState.rememberSession = Boolean(val);
      writeLocalJson(STORAGE_PREFS_KEY, {
        lastUrl: adapterState.serverUrl,
        rememberSession: adapterState.rememberSession
      });
    },
    hasToolset
  };
}
