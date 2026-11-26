const HOST_SYS = {
  os: 'Windows 11 专业工作站版 (28000)',
  arch: 'ARM64',
  hostName: 'SURFACE-PRO-11',
  uptime: '3 天 14:49:43',
  cpu: 'Snapdragon(R) X 12-core X1E80100 @ 3.40 GHz',
  cpuShort: 'Snapdragon X Elite',
  cores: 12,
  threads: 12,
  baseClock: '3.40 GHz',
  curClock: '3.42 GHz',
  numaNodes: 1,
  memoryGb: 16,
  usedMemoryGb: 9.4,
  cpuPct: 18,
  cpuHistory: [15, 20, 17, 24, 19, 21, 18],
  coreHistories: Array.from({ length: 12 }, (_, i) => {
    const base = 10 + ((i * 7) % 28);
    return [base - 4, base + 5, base - 2, base + 8, base + 2, base + 4, base];
  }),
  memHistory: [54, 56, 58, 57, 59, 58, 59],
  memPct: 59,
  memNote: '物理内存 16 GB',
  runningVmCount: 2,
  totalVmCount: 3,
  vmAllocatedRamGb: 6.0,
  vmAllocatedVcpu: 12
};

const POWER_PLANS = [
  { id: 'balanced', name: '平衡' },
  { id: 'high', name: '高性能' },
  { id: 'ultimate', name: '卓越性能' },
  { id: 'saver', name: '节能' }
];

const SCHEDULER_OPTIONS = [
  { value: 'Classic', label: '经典', fullLabel: '经典调度器' },
  { value: 'Core', label: '核心', fullLabel: '核心调度器' },
  { value: 'Root', label: '根', fullLabel: '根调度器' }
];

function getSchedulerLabel(value, full = false) {
  const opt = SCHEDULER_OPTIONS.find((o) => o.value === value);
  if (!opt) return value || '';
  return full ? opt.fullLabel : opt.label;
}

const PATH_PRESETS = {
  vm: [
    'C:\\ProgramData\\Microsoft\\Windows\\Hyper-V',
    'D:\\Hyper-V\\Virtual Machines',
    'E:\\VMs'
  ],
  vhd: [
    'C:\\Users\\Public\\Documents\\Hyper-V\\Virtual Hard Disks',
    'D:\\Hyper-V\\Virtual Hard Disks',
    'E:\\VHDs'
  ]
};

const INITIAL_NICS = [
  {
    id: 'nic-eth',
    name: '以太网',
    media: 'wired',
    canBridge: true,
    mediaNote: '有线以太网，支持 Bridge / NAT',
    description: 'Intel(R) Ethernet Controller I226-V',
    linkUp: true,
    speed: '2.5 Gbps',
    ipv4: '192.168.1.100',
    mac: 'A8-A1-59-7B-2C-10',
    boundSwitch: 'External vSwitch (Bridge)',
    sendRate: '1.2 Mbps',
    recvRate: '4.8 Mbps',
    sendPts: [12, 18, 15, 26, 20, 24, 18],
    recvPts: [22, 34, 28, 46, 38, 42, 36]
  },
  {
    id: 'nic-wifi',
    name: 'WLAN',
    media: 'wifi',
    canBridge: true,
    mediaNote: '802.11 Wi-Fi，Msvm_WiFiPort 可桥接',
    description: 'Qualcomm FastConnect 7800 Wi-Fi 7',
    linkUp: true,
    speed: '1.2 Gbps',
    ipv4: '192.168.1.108',
    mac: '4C-D5-77-9A-12-E4',
    boundSwitch: '空闲（可桥接 / NAT）',
    sendRate: '180 Kbps',
    recvRate: '640 Kbps',
    sendPts: [6, 9, 7, 14, 10, 12, 9],
    recvPts: [14, 20, 16, 28, 22, 24, 19]
  },
  {
    id: 'nic-cell',
    name: '手机网络',
    media: 'cellular',
    canBridge: false,
    mediaNote: '蜂窝 SIM (MBIM)，不可桥接（仅 NAT），按流量计费',
    description: 'Qualcomm Snapdragon X65 5G Modem',
    linkUp: true,
    speed: '5G NR (420 Mbps)',
    ipv4: '10.168.42.19',
    mac: '---（Raw IP 无二层 MAC）',
    boundSwitch: 'Default Switch 自动路由备选',
    sendRate: '24 Kbps',
    recvRate: '96 Kbps',
    sendPts: [2, 3, 2, 5, 3, 4, 2],
    recvPts: [5, 8, 6, 12, 9, 10, 7]
  },
  {
    id: 'nic-tun',
    name: 'Clash TUN',
    media: 'tunnel',
    canBridge: false,
    mediaNote: '三层 Wintun 隧道，不可桥接，可承载宿主路由',
    description: 'Meta / Wintun Userspace Tunnel',
    linkUp: true,
    speed: '虚拟隧道',
    ipv4: '198.18.0.1',
    mac: '---（三层隧道）',
    boundSwitch: '宿主路由 / ICS 代理出口',
    sendRate: '420 Kbps',
    recvRate: '2.4 Mbps',
    sendPts: [8, 12, 10, 16, 14, 15, 11],
    recvPts: [18, 26, 22, 38, 30, 34, 28]
  }
];

const INITIAL_SWITCHES = [
  {
    id: 'sw-ext',
    name: 'External vSwitch',
    mode: 'Bridge',
    allowManagementOs: true,
    upstream: 'Intel(R) Ethernet Controller I226-V',
    ipv4: '192.168.1.100',
    sendRate: '184 Kbps',
    recvRate: '428 Kbps',
    sendPts: [18, 34, 24, 58, 30, 42, 36],
    recvPts: [36, 78, 44, 88, 52, 74, 62],
    endpoints: ['ubuntu-dev'],
    expanded: true
  },
  {
    id: 'sw-default',
    name: 'Default Switch',
    mode: 'NAT',
    allowManagementOs: true,
    upstream: '宿主当前活跃网络 (NAT)',
    ipv4: '172.30.240.1',
    sendRate: '0.4 Mbps',
    recvRate: '1.6 Mbps',
    sendPts: [6, 9, 8, 14, 11, 12, 10],
    recvPts: [12, 18, 15, 24, 20, 22, 18],
    endpoints: ['test', 'win11-arm64', 'ubuntu-dev'],
    expanded: false
  },
  {
    id: 'sw-iso',
    name: 'Lab Isolated',
    mode: 'Isolated',
    allowManagementOs: false,
    upstream: '无上游出口',
    ipv4: '---',
    sendRate: '12.4 Mbps',
    recvRate: '12.8 Mbps',
    sendPts: [18, 28, 22, 36, 30, 34, 29],
    recvPts: [20, 30, 24, 38, 32, 36, 31],
    endpoints: ['ubuntu-dev', 'win11-arm64'],
    expanded: false
  }
];

const INITIAL_GPUS = [
  {
    id: 'gpu-0',
    name: 'Qualcomm(R) Adreno(TM) X1-85 GPU',
    manu: 'Qualcomm Incorporated',
    vendor: 'Qualcomm Incorporated',
    vram: '128 MB',
    driver: '31.0.170.0',
    instanceId: 'ACPI\\VEN_QCOM&DEV_0C36&SUBSYS_CRD08380&REV_0088\\0',
    partitionable: true,
    assignedVms: ['test', 'Windows11-ARM64'],
    pct: 14,
    pts3d: [10, 14, 19, 15, 18, 14],
    ptsCopy: [2, 4, 5, 3, 4, 4],
    ptsEnc: [0, 0, 0, 0, 0, 0],
    ptsDec: [4, 6, 9, 5, 7, 6]
  }
];

const INITIAL_DISKS = [
  {
    number: 0,
    name: 'WDC CH SN530 SDBPTPZ-1T00-1024',
    sizeText: '867.0 GB',
    serial: '21481E801024',
    path: '\\\\.\\PhysicalDrive0',
    status: '系统盘（不可直通）',
    offline: false,
    readOnly: false,
    canPassthrough: false,
    pct: 2,
    readRate: '18.4 MB/s',
    writeRate: '6.2 MB/s',
    readPts: [8, 14, 12, 22, 16, 18],
    writePts: [4, 6, 5, 9, 7, 6]
  },
  {
    number: 1,
    name: 'Samsung SSD 990 PRO 2TB',
    sizeText: '1863.02 GB',
    serial: 'S73WNJ0W412089',
    path: '\\\\.\\PhysicalDrive1',
    status: '脱机，可直通',
    offline: true,
    readOnly: false,
    canPassthrough: true,
    pct: 0,
    readRate: '0 KB/s',
    writeRate: '0 KB/s',
    readPts: [0, 0, 0, 0, 0, 0],
    writePts: [0, 0, 0, 0, 0, 0]
  }
];

const INITIAL_PCIE = [
  {
    id: 'pcie-nvme',
    name: 'Samsung NVMe SSD 990 PRO 2TB',
    classType: 'DiskDrive',
    vendor: 'Samsung Electronics',
    instanceId: 'PCI\\VEN_144D&DEV_A80C&SUBSYS_A801144D\\4&19A8F1&0&0009',
    path: 'PCIROOT(0)#PCI(0102)#PCI(0000)',
    ownerKind: 'host',
    ownerName: '宿主',
    expanded: false
  },
  {
    id: 'pcie-nic',
    name: 'Intel(R) Ethernet Controller I226-V',
    classType: 'Net',
    vendor: 'Intel Corporation',
    instanceId: 'PCI\\VEN_8086&DEV_125C&SUBSYS_00008086\\6&3B912A&0&0010',
    path: 'PCIROOT(0)#PCI(0201)#PCI(0000)',
    ownerKind: 'host',
    ownerName: '宿主',
    expanded: false
  },
  {
    id: 'pcie-usb',
    name: 'AMD USB 3.20 eXtensible Host Controller',
    classType: 'USB',
    vendor: 'AMD',
    instanceId: 'PCI\\VEN_1022&DEV_43F7&SUBSYS_11421B21\\4&8A12F&0&00A3',
    path: 'PCIROOT(0)#PCI(0801)#PCI(0003)',
    ownerKind: 'unassigned',
    ownerName: '已从宿主卸下（待分配）',
    expanded: true
  }
];

const INITIAL_CPU_GROUPS = [
  {
    id: '3fa85f64-5717-4562-b3fc-2c963f66afa6',
    name: '高性能核心组 (0-7)',
    processors: '0 - 7',
    members: ['test', 'Windows11-ARM64'],
    cpuCap: 100,
    priority: 5,
    idleReserve: 2,
    weight: 1000,
    expanded: true
  },
  {
    id: '8c12a109-1b2c-4d3e-9a01-99100234ab12',
    name: '能效后台组 (8-11)',
    processors: '8 - 11',
    members: ['Win7-Gen1'],
    cpuCap: 75,
    priority: 2,
    idleReserve: 0,
    weight: 200,
    expanded: false
  }
];

const INITIAL_VM_GROUPS = [
  { id: 'grp-prod', name: '日常主力环境', kind: 'Groups', children: ['Windows 桌面', 'Linux 编译节点'], vms: [] },
  { id: 'grp-desk', name: 'Windows 桌面', kind: 'VirtualMachines', children: [], vms: ['test', 'win11-arm64'] },
  { id: 'grp-lin', name: 'Linux 编译节点', kind: 'VirtualMachines', children: [], vms: ['ubuntu-dev'] }
];

const HOST_TABS_BASE = [
  { key: 'overview', label: '总览', icon: '&#xE80F;' },
  { key: 'state', label: '性能', icon: '&#xE9D9;' },
  { key: 'env', label: '环境', icon: '&#xE9D5;' },
  { key: 'policy', label: '设置', icon: '&#xE713;' },
  { key: 'switches', label: '交换机', icon: '&#xE774;' }
];

const CARD_STYLE_CANDIDATES = ['vm-3row', 'compact-2row', 'kpi-matrix', 'merged-tabs', 'slim-bar'];
const CARD_CONTENT_CANDIDATES = ['identity-ready', 'ops-summary', 'full-workstation'];
const CPU_DETAIL_STYLES = [
  'art-winui-bar',
  'art-fill-x',
  'art-fill-y',
  'art-history-bars',
  'art-micro-leds',
  'art-chunky-bar',
  'art-ring',
  'art-aurora',
  'art-blocks'
];
const HIGHLIGHT_STYLES = ['fluent-tint', 'pill-accent', 'elevated-card', 'focus-ring', 'top-accent'];

const HOST_LED_HOST_COLOR = 'var(--VmAccentBrush, #4CC2FF)';
const HOST_LED_GUEST_COLOR = '#60CDFF';
const HOST_LED_OTHER_COLOR = '#4B5563';
const HOST_LED_VM_PALETTE = ['#6CCB5F', '#4AA86C', '#348E7B', '#3D7A8A', '#4B6B7F', '#566475', '#5C616B'];
const HOST_LED_MOCK_NAMES = [
  'test',
  'win11-arm64',
  'Windows11-Enterprise-Dev-ARM64',
  'ubuntu-build-runner-01',
  'sql-prod-replica',
  'docker-swarm-node-alpha',
  'macos-ci-sandbox',
  'debian-kernel-lab',
  'win10-ltsc-compat-test',
  'freebsd-router-gw',
  'alpine-edge-proxy',
  'arch-dev-workstation'
];
const HOST_VM_CPU_ALLOCATIONS = [
  { name: 'test', running: true, vcpu: 8, pct: 18, weight: 100, limit: 100, reserve: 0, affinity: '绑定 0-7', allowedLps: [0, 1, 2, 3, 4, 5, 6, 7], color: '#FFB900', cpuGroup: '宿主逻辑核心组' },
  { name: 'win11-arm64', running: true, vcpu: 4, pct: 11, weight: 100, limit: 100, reserve: 10, affinity: '自动调度', allowedLps: null, color: '#6CCB5F', cpuGroup: '宿主逻辑核心组' },
  { name: 'ubuntu-dev', running: false, vcpu: 4, pct: 0, weight: 100, limit: 80, reserve: 0, affinity: '自动调度', allowedLps: null, color: '#C299FF', cpuGroup: '未分组' }
];

function getRunningLedVms(hostThreads) {
  const count = hostState.mockVmCount;
  if (!count) return HOST_VM_CPU_ALLOCATIONS.filter((v) => v.running);
  return Array.from({ length: count }, (_, i) => {
    const name = HOST_LED_MOCK_NAMES[i] || `vm-node-${i + 1}`;
    const vcpu = Math.min(hostThreads, i % 3 === 0 ? 8 : 4);
    const hasSubset = i % 2 === 0 && hostThreads >= 8;
    const allowedLps = hasSubset
      ? Array.from({ length: Math.max(2, Math.floor(hostThreads * 0.65)) }, (_, k) => (k + i) % hostThreads)
      : null;
    return {
      name,
      running: true,
      vcpu,
      pct: Math.max(4, 22 - i * 2),
      color: i < HOST_LED_VM_PALETTE.length ? HOST_LED_VM_PALETTE[i] : HOST_LED_OTHER_COLOR,
      allowedLps
    };
  });
}

const hostState = {
  cardStyle: 'compact-2row', // 'vm-3row' | 'compact-2row' | 'kpi-matrix' | 'merged-tabs' | 'slim-bar'
  cardContent: 'ops-summary', // 'identity-ready' | 'ops-summary' | 'full-workstation'
  cpuDetailStyle: 'art-ring',
  highlightStyle: 'elevated-card', // 'fluent-tint' | 'pill-accent' | 'elevated-card' | 'focus-ring' | 'top-accent'
  legendPlacement: 'right', // 'right' | 'bottom'
  mockThreads: '12',
  mockVmCount: '2',
  overviewResource: 'cpu',
  selectedLedVm: null,
  hoveredLedVm: null,
  hoveredLedLp: null,
  selectedMemKey: null,
  hoveredMemKey: null,
  hoveredMemSeg: null,
  activeTab: 'overview',
  policySub: 'scheduler',
  powerComboOpen: false,
  selectedRail: { kind: 'cpu', idx: 0 }, // 'cpu' | 'mem' | 'nic' | 'switch' | 'gpu' | 'disk'

  hyperVEnabled: true,
  cpuVirtOk: true,
  gpuPvOk: true,
  iommuOk: true,
  isWindowsServer: false,

  notice: null,

  saved: {
    powerPlanId: 'high',
    scheduler: 'Core',
    numaSpanning: true,
    defaultVmPath: PATH_PRESETS.vm[1],
    defaultVhdPath: PATH_PRESETS.vhd[1],
    enhancedSession: true,
    volumeAutoMount: true,
    macMin: '00-15-5D-01-00-00',
    macMax: '00-15-5D-01-FF-FF',
    pciePolicyUnlocked: false,
    serverProductTypeSpoof: false,
    nativeNvme: false,
    openHclFirmware: false,
    autoOpenConsole: true,
    defaultPowerAction: 'Shutdown'
  },
  draft: null,

  nics: structuredClone(INITIAL_NICS),
  switches: structuredClone(INITIAL_SWITCHES),
  gpus: structuredClone(INITIAL_GPUS),
  disks: structuredClone(INITIAL_DISKS),
  pcieDevices: structuredClone(INITIAL_PCIE),
  cpuGroups: structuredClone(INITIAL_CPU_GROUPS),
  vmGroups: structuredClone(INITIAL_VM_GROUPS)
};

hostState.draft = structuredClone(hostState.saved);

function esc(text) {
  return String(text ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

function isAccentMode() {
  const sw = document.getElementById('settingsAccentSwitch');
  return sw ? sw.classList.contains('on') : true;
}

function labelHtml(title, desc) {
  const infoBtn = desc
    ? `<button type="button" class="setting-info-btn" data-info-desc="${esc(desc)}" title="${esc(desc)}"><span class="fi" style="font-size:15px;">&#xE946;</span></button>`
    : '';
  return `
    <div class="setting-label-grid">
      <span class="setting-label-title">${esc(title)}</span>
      ${infoBtn}
    </div>
  `;
}

function toggleFieldHtml(key, title, desc, { span2 = false, disabled = false } = {}) {
  const on = Boolean(hostState.draft[key]);
  return `
    <div class="field-editor-toggle-row ${span2 ? 'host-span-2' : ''}">
      ${labelHtml(title, desc)}
      <div class="winui-switch ${on ? 'on' : ''} ${disabled ? 'disabled' : ''}" data-host-draft-switch="${key}" role="switch" aria-checked="${on}" tabindex="${disabled ? -1 : 0}">
        <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
        <span class="switch-state-label">${on ? '启用' : '关闭'}</span>
      </div>
    </div>
  `;
}

function isGroupDirty(keys) {
  return keys.some((k) => hostState.draft[k] !== hostState.saved[k]);
}

function actionRowHtml(groupId, keys) {
  const dirty = isGroupDirty(keys);
  return `
    <div class="host-actions-row">
      <button type="button" class="winui-btn primary" data-host-apply="${groupId}" ${dirty ? '' : 'disabled'}>应用</button>
      <button type="button" class="winui-btn" data-host-revert="${groupId}" ${dirty ? '' : 'disabled'}>还原</button>
    </div>
  `;
}

function buildChartSvg(points, isGpu = false) {
  const w = 240;
  const h = 100;
  const step = w / Math.max(1, points.length - 1);
  const coords = points.map((v, idx) => {
    const x = (idx * step).toFixed(1);
    const y = (h - Math.max(2, Math.min(96, v))).toFixed(1);
    return `${x},${y}`;
  });
  const linePts = coords.join(' ');
  const areaPts = `0,${h} ${linePts} ${w},${h}`;
  const fillVar = isGpu ? 'var(--VmGpuChartFillBrush)' : 'var(--VmChartFillBrush)';
  return `
    <svg class="vm-usage-chart-svg" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none">
      <polygon points="${areaPts}" fill="${fillVar}" />
      <polyline points="${linePts}" fill="none" stroke="var(--VmAccentBrush)" stroke-width="1.5" />
    </svg>
  `;
}

function createUsageChartHtml(caption, active, valueText, points = [], isGpu = false, customHeight = null) {
  const styleAttr = customHeight ? `style="height:${customHeight}px;"` : '';
  return `
    <div class="vm-usage-chart" ${styleAttr}>
      <span class="vm-usage-chart-caption">${esc(caption)}</span>
      ${active && points.length > 1 ? buildChartSvg(points, isGpu) : ''}
      ${active ? `<span class="vm-usage-chart-value">${esc(valueText)}</span>` : ''}
    </div>
  `;
}

function renderSparkThumbSvg(points, active, strokeColor = 'var(--VmAccentBrush)', fillColor = 'var(--VmChartFillBrush)') {
  const w = 52;
  const h = 46;
  if (!active || points.length < 2) return '';
  const step = w / (points.length - 1);
  const coords = points.map((v, idx) => {
    const x = (idx * step).toFixed(1);
    const y = (h - Math.max(3, Math.min(h - 4, (v / 100) * (h - 6) + 3))).toFixed(1);
    return `${x},${y}`;
  });
  const linePts = coords.join(' ');
  const areaPts = `0,${h} ${linePts} ${w},${h}`;
  return `
    <svg viewBox="0 0 ${w} ${h}" preserveAspectRatio="none">
      <polygon points="${areaPts}" fill="var(--spark-fill, ${fillColor})" />
      <polyline points="${linePts}" fill="none" stroke="var(--spark-stroke, ${strokeColor})" stroke-width="1.45" />
    </svg>
  `;
}

function renderTitleFirstKvItemHtml({ title, stateText, caution = false, wide = false, copyVal = '' }) {
  const itemCls = wide ? 'dt-kv-item is-wide' : 'dt-kv-item';
  const stateCls = `${caution ? 'dt-kv-state is-caution' : 'dt-kv-state'}${wide ? ' tc-mid-fit' : ''}`;
  const copyBtn = copyVal
    ? `<button class="dt-kv-head-copy" data-copy="${esc(copyVal)}" title="点击复制：${esc(copyVal)}" type="button"><span class="fi" style="font-size:11.5px;">&#xE8C8;</span></button>`
    : '';
  return `
    <div class="${itemCls}">
      <div class="dt-kv-head">
        <span class="dt-kv-title">${esc(title)}</span>
        ${copyBtn}
      </div>
      <span class="${stateCls}" title="${esc(stateText)}">${esc(stateText)}</span>
    </div>
  `;
}

function renderTmScaleBar(leftText, rightText) {
  return `
    <div class="host-tm-scale-row">
      <span>${esc(leftText)}</span>
      <span>${esc(rightText)}</span>
    </div>
  `;
}

function annularSectorPath(cx, cy, rIn, rOut, startDeg, endDeg, cornerR = 2.5) {
  const sweep = endDeg - startDeg;
  if (sweep <= 0.6) return '';
  const rad = Math.PI / 180;
  const pt = (r, deg) => [cx + r * Math.cos(deg * rad), cy + r * Math.sin(deg * rad)];
  const maxCr = Math.min(cornerR, (rOut - rIn) * 0.42, (sweep * rad * rIn) * 0.42);
  if (maxCr < 0.6) {
    const [x1, y1] = pt(rOut, startDeg);
    const [x2, y2] = pt(rOut, endDeg);
    const [x3, y3] = pt(rIn, endDeg);
    const [x4, y4] = pt(rIn, startDeg);
    const large = sweep > 180 ? 1 : 0;
    return `M${x1.toFixed(2)},${y1.toFixed(2)} A${rOut},${rOut} 0 ${large} 1 ${x2.toFixed(2)},${y2.toFixed(2)} L${x3.toFixed(2)},${y3.toFixed(2)} A${rIn},${rIn} 0 ${large} 0 ${x4.toFixed(2)},${y4.toFixed(2)} Z`;
  }
  const dOut = (maxCr / rOut) / rad;
  const dIn = (maxCr / rIn) / rad;
  const [p1x, p1y] = pt(rOut, startDeg + dOut);
  const [p2x, p2y] = pt(rOut, endDeg - dOut);
  const [p3x, p3y] = pt(rOut - maxCr, endDeg);
  const [p4x, p4y] = pt(rIn + maxCr, endDeg);
  const [p5x, p5y] = pt(rIn, endDeg - dIn);
  const [p6x, p6y] = pt(rIn, startDeg + dIn);
  const [p7x, p7y] = pt(rIn + maxCr, startDeg);
  const [p8x, p8y] = pt(rOut - maxCr, startDeg);
  const largeOut = (endDeg - dOut) - (startDeg + dOut) > 180 ? 1 : 0;
  const largeIn = (endDeg - dIn) - (startDeg + dIn) > 180 ? 1 : 0;
  return [
    `M${p1x.toFixed(2)},${p1y.toFixed(2)}`,
    `A${rOut},${rOut} 0 ${largeOut} 1 ${p2x.toFixed(2)},${p2y.toFixed(2)}`,
    `A${maxCr.toFixed(2)},${maxCr.toFixed(2)} 0 0 1 ${p3x.toFixed(2)},${p3y.toFixed(2)}`,
    `L${p4x.toFixed(2)},${p4y.toFixed(2)}`,
    `A${maxCr.toFixed(2)},${maxCr.toFixed(2)} 0 0 1 ${p5x.toFixed(2)},${p5y.toFixed(2)}`,
    `A${rIn},${rIn} 0 ${largeIn} 0 ${p6x.toFixed(2)},${p6y.toFixed(2)}`,
    `A${maxCr.toFixed(2)},${maxCr.toFixed(2)} 0 0 1 ${p7x.toFixed(2)},${p7y.toFixed(2)}`,
    `L${p8x.toFixed(2)},${p8y.toFixed(2)}`,
    `A${maxCr.toFixed(2)},${maxCr.toFixed(2)} 0 0 1 ${p1x.toFixed(2)},${p1y.toFixed(2)}`,
    'Z'
  ].join(' ');
}

function formatCompactRate(sendRate, recvRate, format = hostState.railRateFormat) {
  const parse = (s) => {
    const m = String(s || '0 Kbps').trim().match(/^([\d.]+)\s*(Kbps|Mbps|Gbps)$/i);
    return m ? { val: m[1], unit: m[2] } : { val: '0', unit: 'Kbps' };
  };
  const tx = parse(sendRate);
  const rx = parse(recvRate);
  if (tx.val === '0' && rx.val === '0') return '0 Kbps';
  const sameUnit = tx.unit.toLowerCase() === rx.unit.toLowerCase();
  const shortU = (u) => (u[0] || '').toUpperCase();
  const txStr = sameUnit ? tx.val : `${tx.val}${shortU(tx.unit)}`;
  const rxStr = sameUnit ? rx.val : `${rx.val}${shortU(rx.unit)}`;
  const tail = sameUnit ? ` ${rx.unit}` : '';
  if (format === 'arrow-compact') return `↑ ${txStr} ↓ ${rxStr}${tail}`;
  if (format === 'slash-compact') return `${txStr} / ${rxStr}${tail}`;
  return `发 ${txStr} 收 ${rxStr}${tail}`;
}

function getSwitchModeLabel(sw, style = hostState.railSwitchSubStyle) {
  const mode = sw.mode || 'Isolated';
  if (style === 'isolated-3way') {
    if (mode === 'Bridge') return '桥接';
    return mode === 'NAT' ? 'NAT' : '隔离';
  }
  if (style === 'intranet-3way') {
    if (mode === 'Bridge') return '桥接';
    return mode === 'NAT' ? 'NAT' : '仅内网';
  }
  if (style === 'en-3way') {
    return mode;
  }
  // 'upstream-3way'（用户初始设计：桥接 / NAT / 无上游）
  if (mode === 'Bridge') return '桥接';
  return mode === 'NAT' ? 'NAT' : '无上游';
}

function getNicRailCopy(nic, layout = hostState.railNicLayout) {
  const mediaTitles = {
    wired: '以太网',
    wifi: 'Wi-Fi',
    cellular: '蜂窝网络',
    tunnel: '虚拟隧道'
  };
  const mediaCap = {
    wired: nic.linkUp ? `有线 ${nic.speed}` : '有线以太网',
    wifi: nic.linkUp ? `Wi-Fi ${nic.speed}` : 'Wi-Fi 无线',
    cellular: '蜂窝移动 计费连接',
    tunnel: '三层虚拟隧道'
  };
  if (layout === 'media-name-rate') {
    return { title: mediaTitles[nic.media] || '网络适配器', sub: nic.name };
  }
  if (layout === 'name-media-rate') {
    return { title: nic.name, sub: mediaCap[nic.media] || nic.mediaNote || '' };
  }
  if (layout === 'name-rate-2line') {
    return { title: nic.name, sub: '' };
  }
  // 'name-model-rate'
  return { title: nic.name, sub: nic.description };
}

function getSwitchRailCopy(sw, layout = hostState.railSwitchLayout) {
  const modeLabel = getSwitchModeLabel(sw);
  const rate = formatCompactRate(sw.sendRate, sw.recvRate);
  if (layout === 'kind-name-rate') {
    return { title: modeLabel, sub: sw.name, stat: rate, modeLabel };
  }
  if (layout === 'name-rate-2line') {
    return { title: sw.name, sub: '', stat: rate, modeLabel };
  }
  if (layout === 'name-inline-2line') {
    return { title: sw.name, sub: '', stat: `${modeLabel} ${rate}`, modeLabel };
  }
  // 'name-l2l3-rate'
  return { title: sw.name, sub: modeLabel, stat: rate, modeLabel };
}

// ============================================================================
// Host Memory Object Model (Step 1: Pure Object Ownership without Diagrams)
// ============================================================================
function buildHostMemoryBreakdown() {
  const totalGb = Math.max(1, Number(HOST_SYS.memoryGb) || 16);
  const rawUsed = Number(HOST_SYS.usedMemoryGb) || totalGb * 0.55;
  const vmCount = Math.max(1, Number(hostState.mockVmCount) || HOST_SYS.runningVmCount || 2);

  const inUseGb = Number(Math.min(totalGb - 0.1, Math.max(0.4, rawUsed)).toFixed(1));
  const freeGb = Number(Math.max(0.1, totalGb - inUseGb).toFixed(1));

  const vmShareOfUsed = vmCount >= 12 ? 0.78 : vmCount >= 4 ? 0.68 : 0.54;
  const vmBudgetGb = inUseGb * vmShareOfUsed;
  const weightTable = Array.from({ length: vmCount }, (_, i) => {
    const baseName = HOST_LED_MOCK_NAMES[i] || `vm-node-${String(i + 1).padStart(2, '0')}`;
    const isDynamic = i % 2 === 1;
    const weight = Math.max(0.35, 1.4 - i * 0.05);
    return {
      name: baseName,
      dynamic: isDynamic,
      locked: !isDynamic,
      modeText: isDynamic ? '动态内存' : '固定内存',
      weight
    };
  });
  const totalWeight = weightTable.reduce((s, item) => s + item.weight, 0) || 1;

  let vmSumGb = 0;
  const sortedVms = weightTable
    .map((item) => {
      const assignedGb = Number(Math.max(0.1, vmBudgetGb * (item.weight / totalWeight)).toFixed(1));
      vmSumGb += assignedGb;
      return {
        key: `vm:${item.name}`,
        name: item.name,
        modeText: item.modeText,
        locked: item.locked,
        dynamic: item.dynamic,
        assignedGb,
        gb: assignedGb,
        sharePct: Math.max(1, Math.round((assignedGb / totalGb) * 100))
      };
    })
    .sort((a, b) => b.assignedGb - a.assignedGb)
    .map((item, idx) => ({
      ...item,
      kind: 'vm',
      tag: item.modeText,
      color: idx < HOST_LED_VM_PALETTE.length ? HOST_LED_VM_PALETTE[idx] : HOST_LED_OTHER_COLOR
    }));

  vmSumGb = Number(vmSumGb.toFixed(1));
  const hostGb = Number(Math.max(0, inUseGb - vmSumGb).toFixed(1));
  const hostSharePct = hostGb > 0 ? Math.max(1, Math.round((hostGb / totalGb) * 100)) : 0;
  const freePct = Math.max(1, Math.round((freeGb / totalGb) * 100));

  const hostItem = {
    key: '__host__',
    name: '宿主',
    gb: hostGb,
    sharePct: hostSharePct,
    color: HOST_LED_HOST_COLOR,
    kind: 'host',
    tag: '宿主系统与进程占用'
  };
  const freeItem = {
    key: '__free__',
    name: '空闲内存',
    gb: freeGb,
    sharePct: freePct,
    color: 'rgba(128, 136, 148, 0.32)',
    kind: 'free',
    tag: ''
  };

  return {
    totalGb,
    inUseGb,
    inUsePct: Math.max(1, Math.round((inUseGb / totalGb) * 100)),
    hostGb,
    hostSharePct,
    vmSumGb,
    freeGb,
    freePct,
    vms: sortedVms,
    hostItem,
    freeItem,
    orderedItems: [hostItem, ...sortedVms, freeItem]
  };
}

function renderHostMemoryLegendSidebar(mem, focusKey) {
  const makeChip = (it, extraCls = '') => `
    <button type="button"
      class="host-led-chip host-mem-side-chip ${extraCls} ${hostState.selectedMemKey === it.key ? 'is-selected' : ''} ${focusKey === it.key ? 'is-hovered' : ''}"
      data-mem-key="${esc(it.key)}"
      title="${esc(it.name)} (${it.gb.toFixed(1)} GB)"
      style="--led-color:${it.color};">
      <span class="host-led-swatch"></span>
      <span class="host-led-chip-name">${esc(it.name)}</span>
      <span class="host-mem-chip-gb">${it.gb.toFixed(1)}G</span>
    </button>
  `;

  return `
    <div class="host-rel-summary-box">
      <div class="host-led-pin-host">
        ${makeChip(mem.hostItem)}
      </div>
      <div class="host-led-legend host-led-vm-scroll">
        ${mem.vms.map((v) => makeChip(v)).join('')}
      </div>
    </div>
  `;
}

function renderHostMemoryPieView(mem, focusKey, hlCls) {
  const focusItem = focusKey ? mem.orderedItems.find((it) => it.key === focusKey) : null;
  const cx = 180;
  const cy = 180;
  const rIn = 96;
  const rOutBase = 152;
  const rMid = (rIn + rOutBase) / 2;
  const ringW = rOutBase - rIn;

  const positive = mem.orderedItems.filter((it) => it.gb > 0);
  const sumGb = positive.reduce((s, it) => s + it.gb, 0) || 1;
  const nonFreeCount = positive.filter((it) => it.kind !== 'free').length;
  const gapDeg = nonFreeCount > 14 ? 0.85 : nonFreeCount > 6 ? 1.4 : 2.1;

  let curDeg = -90;
  const slicesHtml = positive
    .map((it) => {
      const spanDeg = (it.gb / sumGb) * 360;
      const isFree = it.kind === 'free';
      const isFocused = focusKey === it.key;
      const isDimmed = Boolean(focusKey) && !isFocused;
      const effGap = spanDeg > gapDeg * 2.2 ? gapDeg : spanDeg > 1.2 ? 0.35 : 0;
      const aStart = curDeg + effGap / 2;
      const aEnd = curDeg + spanDeg - effGap / 2;
      curDeg += spanDeg;

      const rOut = isFocused ? rOutBase + 5 : rOutBase;
      const d = annularSectorPath(cx, cy, rIn, rOut, aStart, aEnd, spanDeg >= 6 ? 5 : 2.5);
      if (!d) return '';

      if (isFree) {
        const freeCls = ['host-mem-pie-slice', 'is-free', isFocused ? 'is-focused' : ''].filter(Boolean).join(' ');
        return `<path class="${freeCls}" d="${d}" data-mem-key="${esc(it.key)}" />`;
      }

      const op = isDimmed ? 0.16 : 1;
      const sliceCls = ['host-mem-pie-slice', isFocused ? 'is-focused' : ''].filter(Boolean).join(' ');
      return `<path class="${sliceCls}" d="${d}" fill="${it.color}" opacity="${op}" data-mem-key="${esc(it.key)}" />`;
    })
    .join('');

  const centerTitle = focusItem ? focusItem.name : '物理内存已用';
  const centerPct = focusItem ? focusItem.sharePct : mem.inUsePct;
  const centerColor = focusItem && focusItem.kind !== 'free' ? focusItem.color : '';
  const centerGb = `${(focusItem ? focusItem.gb : mem.inUseGb).toFixed(1)} / ${mem.totalGb.toFixed(1)} GB`;
  const centerSub = focusItem ? (focusItem.tag || '') : `空闲 ${mem.freeGb.toFixed(1)} GB`;

  return `
    <div class="host-rel-wrap is-legend-right ${hlCls}">
      <div class="host-mem-pie-card">
        <div class="host-mem-pie-stage-box">
          <svg class="host-mem-pie-svg" viewBox="0 0 360 360">
            <circle cx="${cx}" cy="${cy}" r="${rMid}" fill="none" stroke="color-mix(in srgb, var(--TextFillColorPrimaryBrush) 6%, transparent)" stroke-width="${ringW}" />
            ${slicesHtml}
          </svg>
          <div class="host-mem-pie-center">
            <span class="host-mem-pie-label" title="${esc(centerTitle)}">${esc(centerTitle)}</span>
            <span class="host-mem-pie-pct" style="${centerColor ? `color:${centerColor};` : ''}">${centerPct}%</span>
            <span class="host-mem-pie-gb">${esc(centerGb)}</span>
            ${centerSub ? `<span class="host-mem-pie-sub" title="${esc(centerSub)}">${esc(centerSub)}</span>` : ''}
          </div>
        </div>
      </div>
      ${renderHostMemoryLegendSidebar(mem, focusKey)}
    </div>
  `;
}

// ============================================================================
// Tab 1: 状态 (Task Manager-style Left Rail + Host Telemetry & Inventory)
// ============================================================================
function renderResourceTab(overview = false) {
  const accentMode = isAccentMode();
  const strokeOf = (fallback) => (accentMode ? 'var(--VmAccentBrush)' : fallback);
  const fillOf = (fallback) => (accentMode ? 'var(--VmChartFillBrush)' : fallback);

  const memPct = HOST_SYS.memPct ?? Math.round((HOST_SYS.usedMemoryGb / Math.max(0.1, HOST_SYS.memoryGb)) * 100);
  if (hostState.selectedRail.kind === 'overview') {
    hostState.selectedRail = { kind: 'cpu', idx: 0 };
  }
  const isSel = (kind, idx) => hostState.selectedRail.kind === kind && hostState.selectedRail.idx === idx;

  const railEntries = [
    {
      kind: 'cpu',
      idx: 0,
      title: 'CPU',
      sub: '',
      stat: `${HOST_SYS.cpuPct}% ${HOST_SYS.curClock}`,
      active: true,
      pts: HOST_SYS.cpuHistory,
      color: '#3898EC',
      fill: 'rgba(56, 152, 236, 0.22)',
      tip: HOST_SYS.cpu
    },
    {
      kind: 'mem',
      idx: 0,
      title: '内存',
      sub: '',
      stat: `${HOST_SYS.usedMemoryGb.toFixed(1)}/${HOST_SYS.memoryGb.toFixed(1)} GB (${memPct}%)`,
      active: true,
      pts: HOST_SYS.memHistory,
      color: '#9D5CE6',
      fill: 'rgba(157, 92, 230, 0.22)',
      tip: HOST_SYS.memNote
    }
  ];

  // 显卡（宿主端网络和磁盘较多，显卡放在内存之下）
  hostState.gpus.forEach((g, idx) => {
    railEntries.push({
      kind: 'gpu',
      idx,
      title: `GPU ${idx}`,
      sub: g.name,
      stat: `${g.pct}%`,
      active: true,
      pts: g.pts3d,
      color: '#8B5CF6',
      fill: 'rgba(139, 92, 246, 0.24)',
      tip: g.name
    });
  });

  // 存储磁盘
  hostState.disks.forEach((d, idx) => {
    railEntries.push({
      kind: 'disk',
      idx,
      title: `磁盘 ${d.number}`,
      sub: d.name,
      stat: d.offline ? '已脱机' : `${d.pct}%`,
      active: !d.offline,
      pts: d.readPts,
      color: '#6CCB5F',
      fill: 'rgba(108, 203, 95, 0.22)',
      tip: `${d.name}\n${d.path}`
    });
  });

  // 上游网卡（仅列出已连接的网卡，按有线 / Wi-Fi / 蜂窝 / 隧道区分配色与图标）
  const nicPalette = {
    wired: { color: '#46B3E6', fill: 'rgba(70, 179, 230, 0.22)' },
    wifi: { color: '#38BDF8', fill: 'rgba(56, 189, 248, 0.22)' },
    cellular: { color: '#F59E0B', fill: 'rgba(245, 158, 11, 0.22)' },
    tunnel: { color: '#10B981', fill: 'rgba(16, 185, 129, 0.22)' }
  };
  hostState.nics.forEach((nic, idx) => {
    if (!nic.linkUp) return;
    const pal = nicPalette[nic.media] || nicPalette.wired;
    const nicCopy = getNicRailCopy(nic);
    railEntries.push({
      kind: 'nic',
      idx,
      title: nicCopy.title,
      sub: nicCopy.sub,
      stat: formatCompactRate(nic.sendRate, nic.recvRate),
      active: true,
      pts: nic.recvPts,
      color: pal.color,
      fill: pal.fill,
      tip: `${nic.name} (${nic.mediaNote || ''})\n${nic.description}`
    });
  });

  // 虚拟交换机（中游 2.0：支持多维度行结构与类型文案组合对比）
  hostState.switches.forEach((sw, idx) => {
    const swCopy = getSwitchRailCopy(sw);
    railEntries.push({
      kind: 'switch',
      idx,
      title: swCopy.title,
      sub: swCopy.sub,
      stat: swCopy.stat,
      active: true,
      pts: sw.recvPts,
      color: '#00B7C3',
      fill: 'rgba(0, 183, 195, 0.20)',
      tip: `${sw.name} (${swCopy.modeLabel}，${sw.upstream})`
    });
  });

  const railHtml = railEntries
    .map(
      (r) => `
      <button type="button" class="tmw-item ${accentMode ? 'accent-mode' : ''} ${isSel(r.kind, r.idx) ? 'selected' : ''}" data-host-rail="${r.kind}:${r.idx}" title="${esc(r.tip)}">
        <div class="tmw-thumb">${renderSparkThumbSvg(r.pts, r.active, strokeOf(r.color), fillOf(r.fill))}</div>
        <div class="tmw-info">
          <div class="tmw-title-row"><span class="tmw-title">${esc(r.title)}</span></div>
          ${r.sub ? `<div class="tmw-sub">${esc(r.sub)}</div>` : ''}
          <div class="tmw-stat">${esc(r.stat)}</div>
        </div>
      </button>
    `
    )
    .join('');

  const sel = overview ? { kind: hostState.overviewResource } : hostState.selectedRail;
  let icon = '&#xF58E;';
  let rotate = 90;
  let title = 'CPU';
  let hwSubtitle = HOST_SYS.cpu;
  let reading = `${HOST_SYS.cpuPct}%`;
  let jumpTab = 'policy';
  let jumpSub = 'scheduler';
  let jumpLabel = '配置 CPU 调度策略';
  let bodyHtml = '';

  if (sel.kind === 'cpu' || sel.kind === 'overview') {
    icon = '&#xF58E;';
    rotate = 90;
    title = 'CPU';
    reading = `${HOST_SYS.cpuPct}%`;
    if (!overview) {
      title = HOST_SYS.cpu;
      hwSubtitle = '';
      const coreCharts = HOST_SYS.coreHistories
        .map((pts, i) => {
          const cur = pts[pts.length - 1] ?? 0;
          return createUsageChartHtml(String(i), true, `${cur}%`, pts, false, 84);
        })
        .join('');
      const kvItems = [
        renderTitleFirstKvItemHtml({ title: '核心 / 逻辑处理器', stateText: `${HOST_SYS.cores} 核 / ${HOST_SYS.threads} 线程` }),
        renderTitleFirstKvItemHtml({ title: '基准 / 当前速度', stateText: `${HOST_SYS.baseClock} / ${HOST_SYS.curClock}` }),
        renderTitleFirstKvItemHtml({ title: '虚拟化', stateText: hostState.cpuVirtOk ? '已启用' : '未启用', caution: !hostState.cpuVirtOk }),
        renderTitleFirstKvItemHtml({ title: 'Hyper-V 平台', stateText: hostState.hyperVEnabled ? '已启用' : '未启用', caution: !hostState.hyperVEnabled }),
        renderTitleFirstKvItemHtml({ title: '核心调度器', stateText: hostState.saved.scheduler }),
        renderTitleFirstKvItemHtml({ title: 'NUMA 节点', stateText: `${HOST_SYS.numaNodes}` }),
        renderTitleFirstKvItemHtml({ title: '系统架构', stateText: HOST_SYS.arch }),
        renderTitleFirstKvItemHtml({ title: '操作系统', stateText: HOST_SYS.os, copyVal: HOST_SYS.os })
      ];
      bodyHtml = `
        <div class="dt-core-grid">${coreCharts}</div>
        <div class="dt-kv-grid is-title-first is-2col">${kvItems.join('')}</div>
      `;
    } else {
      const hostThreads = Math.max(1, Number(hostState.mockThreads) || HOST_SYS.threads || HOST_SYS.coreHistories.length || 12);
      const runningVms = getRunningLedVms(hostThreads);
      const totalAllocVcpu = runningVms.reduce((sum, v) => sum + v.vcpu, 0);
      const isClassicDegraded = hostState.cpuDetailStyle === 'led-classic';
      hwSubtitle = '';

    // Build per-thread time-slice breakdown from live coreHistories
    const lpRows = Array.from({ length: hostThreads }, (_, lp) => {
      const hist = HOST_SYS.coreHistories[lp] || HOST_SYS.coreHistories[lp % Math.max(1, HOST_SYS.coreHistories.length)] || [12];
      const rawTotal = Math.max(4, Math.min(96, Math.round((hist[hist.length - 1] ?? 12) + ((lp * 5) % 14))));
      const slices = [];
      let vmUsed = 0;

      const eligibleVms = runningVms.filter((v) => !v.allowedLps || v.allowedLps.includes(lp));
      const vmBudget = Math.max(0, rawTotal - 2);
      eligibleVms.forEach((vm, idx) => {
        if (vmUsed >= vmBudget) return;
        const remain = vmBudget - vmUsed;
        const share = idx === eligibleVms.length - 1
          ? Math.min(remain, Math.max(1, Math.round(vmBudget / Math.max(1, eligibleVms.length))))
          : Math.min(remain, Math.max(1, Math.round((vmBudget / Math.max(1, eligibleVms.length)) * (((lp + idx) % 2 === 0) ? 1.15 : 0.78))));
        if (share > 0) {
          slices.push({ vm: vm.name, label: vm.name, pct: share, color: vm.color });
          vmUsed += share;
        }
      });
      const hostPct = Math.max(2, rawTotal - vmUsed);
      slices.push({ vm: '__host__', label: '宿主', pct: hostPct, color: HOST_LED_HOST_COLOR });
      const totalPct = Math.min(100, vmUsed + hostPct);
      const dominant = slices.slice().sort((a, b) => b.pct - a.pct)[0];
      return { lp, totalPct, vmUsed, hostPct, slices, dominant };
    });

    const hovered = hostState.hoveredLedVm || hostState.selectedLedVm;
    const isRightLegend = hostState.legendPlacement !== 'bottom';
    const hostChipHtml = `
      <button type="button" class="host-led-chip ${hostState.selectedLedVm === '__host__' ? 'is-selected' : ''} ${hovered === '__host__' ? 'is-hovered' : ''}" data-host-led-vm="__host__" style="--led-color:${HOST_LED_HOST_COLOR};">
        <span class="host-led-swatch"></span>
        <span class="host-led-chip-name">宿主</span>
      </button>
    `;
    const guestChipHtml = isClassicDegraded ? `
      <button type="button" class="host-led-chip ${hostState.selectedLedVm === '__guest__' ? 'is-selected' : ''} ${hovered === '__guest__' ? 'is-hovered' : ''}" data-host-led-vm="__guest__" style="--led-color:${HOST_LED_GUEST_COLOR};">
        <span class="host-led-swatch"></span>
        <span class="host-led-chip-name">虚拟机</span>
      </button>
    ` : '';
    const vmChipsHtml = runningVms.map((v) => `
      <button type="button" class="host-led-chip ${hostState.selectedLedVm === v.name ? 'is-selected' : ''} ${hovered === v.name ? 'is-hovered' : ''}" data-host-led-vm="${esc(v.name)}" title="${esc(v.name)}" style="--led-color:${v.color};">
        <span class="host-led-swatch"></span>
        <span class="host-led-chip-name">${esc(v.name)}</span>
      </button>
    `).join('');

    const legendHtml = isRightLegend
      ? `
        <div class="host-led-pin-host">
          ${hostChipHtml}
          ${guestChipHtml}
        </div>
        <div class="host-led-legend host-led-vm-scroll">
          ${vmChipsHtml}
        </div>
      `
      : `
        <div class="host-led-legend">
          ${hostChipHtml}
          ${guestChipHtml}
          ${vmChipsHtml}
        </div>
      `;

    const artStyle = hostState.cpuDetailStyle || 'art-winui-bar';
    const tiles = lpRows.map((row) => {
      const hasHovered = !hovered || row.slices.some((s) => s.vm === hovered && s.pct > 0);
      const focusSlice = hovered ? row.slices.find((s) => s.vm === hovered) : row.dominant;
      const focusColor = focusSlice?.color || row.dominant.color;
      const cls = [
        'host-led-tile',
        'is-art',
        hovered && !hasHovered ? 'is-dimmed' : '',
        hovered && hasHovered ? 'is-highlighted' : ''
      ].filter(Boolean).join(' ');
      const fullTip = row.slices.map((s) => `${s.label}  ${s.pct}%`).join('\n');

      if (artStyle === 'art-winui-bar') {
        const activeSlice = hovered ? row.slices.find((s) => s.vm === hovered) : null;
        const displayPct = hovered ? (activeSlice ? activeSlice.pct : 0) : row.totalPct;
        const activeColor = hovered
          ? (hovered === '__host__'
              ? HOST_LED_HOST_COLOR
              : (runningVms.find((v) => v.name === hovered)?.color || HOST_LED_GUEST_COLOR))
          : '';
        const segsHtml = row.slices.map((s) => {
          const op = hovered && s.vm !== hovered ? 0.2 : 1;
          return `<div class="host-led-winui-seg" style="width:${s.pct}%;background:${s.color};opacity:${op};"></div>`;
        }).join('');
        return `
          <div class="${cls}" style="--led-color:${focusColor};" data-led-lp="${row.lp}" data-led-tip="${esc(`线程 ${row.lp}\n${fullTip}`)}">
            <div class="host-led-tile-head">
              <span class="host-led-lp">线程 ${row.lp}</span>
              <span class="host-led-pct" style="${activeColor ? `color:${activeColor};` : ''}">${displayPct}%</span>
            </div>
            <div class="host-led-winui-bar">${segsHtml}</div>
          </div>
        `;
      }

      if (artStyle === 'art-fill-x' || artStyle === 'art-fill-y') {
        const isVert = artStyle === 'art-fill-y';
        const fillSlices = row.slices.map((s) => {
          const dimOp = hovered && s.vm !== hovered ? 0.14 : 0.34;
          const dimProp = isVert ? `height:${s.pct}%;` : `width:${s.pct}%;`;
          return `<div class="host-led-fill-slice" style="${dimProp}--slice-color:${s.color};background:color-mix(in srgb, ${s.color} ${Math.round(dimOp * 100)}%, transparent);"></div>`;
        }).join('');
        return `
          <div class="${cls}" style="--led-color:${focusColor};" data-led-lp="${row.lp}" data-led-tip="${esc(`线程 ${row.lp}\n${fullTip}`)}">
            <div class="host-led-fill-bg ${isVert ? 'is-vertical' : ''}">${fillSlices}</div>
            <div class="host-led-tile-head">
              <span class="host-led-lp">线程 ${row.lp}</span>
              <span class="host-led-pct">${row.totalPct}%</span>
            </div>
          </div>
        `;
      }

      if (artStyle === 'art-history-bars') {
        const hist = HOST_SYS.coreHistories[row.lp] || [12];
        const colsHtml = Array.from({ length: 12 }, (_, ci) => {
          const base = Math.max(4, Math.min(95, Math.round(hist[ci % hist.length] ?? row.totalPct)));
          const segsHtml = row.slices.map((s) => {
            const ratioShare = Math.max(1, Math.round((s.pct / Math.max(1, row.totalPct)) * base));
            const op = hovered && s.vm !== hovered ? 0.18 : 0.92;
            return `<div class="host-led-hist-seg" style="height:${ratioShare}%;background:${s.color};opacity:${op};"></div>`;
          }).join('');
          return `<div class="host-led-hist-col">${segsHtml}</div>`;
        }).join('');
        return `
          <div class="${cls}" style="--led-color:${focusColor};" data-led-lp="${row.lp}" data-led-tip="${esc(`线程 ${row.lp}\n${fullTip}`)}">
            <div class="host-led-tile-head">
              <span class="host-led-lp">线程 ${row.lp}</span>
              <span class="host-led-pct">${row.totalPct}%</span>
            </div>
            <div class="host-led-hist">${colsHtml}</div>
          </div>
        `;
      }

      if (artStyle === 'art-micro-leds') {
        const dotSlots = [];
        row.slices.forEach((s) => {
          const count = Math.round((s.pct / 100) * 20);
          for (let i = 0; i < count && dotSlots.length < 20; i++) {
            dotSlots.push({ color: s.color, vm: s.vm });
          }
        });
        while (dotSlots.length < 20) dotSlots.push(null);
        const dotsHtml = dotSlots.map((d) => {
          if (!d) return '<div class="host-led-microdot"></div>';
          const op = hovered && d.vm !== hovered ? 0.16 : 1;
          return `<div class="host-led-microdot is-lit" style="--dot-color:${d.color};background:${d.color};opacity:${op};"></div>`;
        }).join('');
        return `
          <div class="${cls}" style="--led-color:${focusColor};" data-led-lp="${row.lp}" data-led-tip="${esc(`线程 ${row.lp}\n${fullTip}`)}">
            <div class="host-led-tile-head">
              <span class="host-led-lp">线程 ${row.lp}</span>
              <span class="host-led-pct">${row.totalPct}%</span>
            </div>
            <div class="host-led-microgrid">${dotsHtml}</div>
          </div>
        `;
      }

      if (artStyle === 'art-chunky-bar') {
        const segsHtml = row.slices.map((s) => {
          const op = hovered && s.vm !== hovered ? 0.2 : 0.92;
          return `<div class="host-led-chunky-seg" style="width:${s.pct}%;background:${s.color};opacity:${op};">${s.pct >= 14 ? `${s.pct}%` : ''}</div>`;
        }).join('');
        return `
          <div class="${cls}" style="--led-color:${focusColor};" data-led-lp="${row.lp}" data-led-tip="${esc(`线程 ${row.lp}\n${fullTip}`)}">
            <div class="host-led-tile-head">
              <span class="host-led-lp">线程 ${row.lp}</span>
              <span class="host-led-pct">${row.totalPct}%</span>
            </div>
            <div class="host-led-chunky">${segsHtml}</div>
          </div>
        `;
      }

      if (artStyle === 'art-ring') {
        const rIn = 12;
        const rOut = 23;
        const rMid = (rIn + rOut) / 2;
        const ringW = rOut - rIn;
        const fullTipJson = JSON.stringify({
          lp: row.lp,
          slices: row.slices.filter((s) => s.pct > 0).map((s) => ({ label: s.label, pct: s.pct, color: s.color }))
        });
        // Aggregate tiny tail slices (< 2% or beyond top 5) into one clean "other" arc
        const sortedForArc = row.slices.filter((s) => s.pct > 0);
        const keptArcs = [];
        let otherArcPct = 0;
        sortedForArc.forEach((s) => {
          const isFocused = hovered && s.vm === hovered;
          if (isFocused || (keptArcs.length < 5 && s.pct >= 2)) {
            keptArcs.push(s);
          } else {
            otherArcPct += s.pct;
          }
        });
        if (otherArcPct > 0) {
          keptArcs.push({ vm: '__other__', label: '其他', pct: otherArcPct, color: HOST_LED_OTHER_COLOR });
        }
        let curDeg = -90;
        const arcsHtml = keptArcs.map((s) => {
          const spanDeg = (s.pct / 100) * 360;
          const gap = spanDeg > 5 ? 2.4 : 0;
          const d = annularSectorPath(29, 29, rIn, rOut, curDeg + gap / 2, curDeg + spanDeg - gap / 2, 2.5);
          curDeg += spanDeg;
          if (!d) return '';
          const op = hovered && s.vm !== hovered ? 0.16 : 1;
          return `<path d="${d}" fill="${s.color}" opacity="${op}" />`;
        }).join('');
        const activeSlice = hovered ? row.slices.find((s) => s.vm === hovered) : null;
        const displayPct = hovered ? (activeSlice ? activeSlice.pct : 0) : row.totalPct;
        const activeColor = hovered
          ? (displayPct > 0
              ? (hovered === '__host__'
                  ? HOST_LED_HOST_COLOR
                  : (runningVms.find((v) => v.name === hovered)?.color || HOST_LED_GUEST_COLOR))
              : 'var(--TextFillColorTertiaryBrush)')
          : '';
        return `
          <div class="${cls}" style="--led-color:${focusColor};" data-led-lp="${row.lp}" data-led-tip="${esc(fullTipJson)}">
            <div class="host-led-ring-wrap">
              <svg class="host-led-ring-svg" viewBox="0 0 58 58">
                <circle cx="29" cy="29" r="${rMid}" fill="none" stroke="color-mix(in srgb, var(--TextFillColorPrimaryBrush) 6%, transparent)" stroke-width="${ringW}" />
                ${arcsHtml}
              </svg>
              <div class="host-led-ring-meta">
                <span class="host-led-lp">线程 ${row.lp}</span>
                <span class="host-led-pct" style="${activeColor ? `color:${activeColor};` : ''}">${displayPct}%</span>
              </div>
            </div>
          </div>
        `;
      }

      if (artStyle === 'art-aurora') {
        const bgStops = row.slices
          .filter((s) => s.pct > 0)
          .map((s, idx) => {
            const x = idx === 0 ? '20% 80%' : idx === 1 ? '75% 75%' : '50% 25%';
            const alpha = Math.max(12, Math.min(48, Math.round(s.pct * 0.9 + 10)));
            return `radial-gradient(circle at ${x}, color-mix(in srgb, ${s.color} ${alpha}%, transparent) 0%, transparent 72%)`;
          })
          .join(', ');
        const stripHtml = row.slices.map((s) => `<div class="host-led-seg" style="width:${s.pct}%;background:${s.color};opacity:${hovered && s.vm !== hovered ? 0.2 : 1};"></div>`).join('');
        return `
          <div class="${cls}" style="--led-color:${focusColor};" data-led-lp="${row.lp}" data-led-tip="${esc(`线程 ${row.lp}\n${fullTip}`)}">
            <div class="host-led-aurora-bg" style="background:${bgStops};"></div>
            <div class="host-led-tile-head">
              <span class="host-led-lp">线程 ${row.lp}</span>
            </div>
            <div class="host-led-aurora-center">
              <span class="host-led-aurora-pct">${row.totalPct}%</span>
              <div class="host-led-aurora-strip">${stripHtml}</div>
            </div>
          </div>
        `;
      }

      // 8. art-blocks: Proportional Rounded Blocks
      const idlePct = Math.max(0, 100 - row.totalPct);
      const blocksHtml = [
        ...row.slices.map((s) => {
          const op = hovered && s.vm !== hovered ? 0.2 : 0.9;
          return `<div class="host-led-block" style="flex:${Math.max(2, s.pct)} 1 0;background:${s.color};opacity:${op};">${s.pct >= 18 ? `${s.pct}%` : ''}</div>`;
        }),
        idlePct > 0 ? `<div class="host-led-block is-idle" style="flex:${idlePct} 1 0;"></div>` : ''
      ].join('');
      return `
        <div class="${cls}" style="--led-color:${focusColor};" data-led-lp="${row.lp}" data-led-tip="${esc(`线程 ${row.lp}：${fullTip}`)}">
          <div class="host-led-tile-head">
            <span class="host-led-lp">线程 ${row.lp}</span>
            <span class="host-led-pct">${row.totalPct}%</span>
          </div>
          <div class="host-led-blocks">${blocksHtml}</div>
        </div>
      `;
    }).join('');

    const gridDensityCls =
      !isRightLegend && hostThreads <= 4
        ? 'is-cols-2'
        : hostThreads > 32
          ? 'is-cols-8'
          : hostThreads > 16
            ? 'is-cols-6'
            : '';
    const hlCls = `is-hl-${hostState.highlightStyle || 'elevated-card'}`;
      bodyHtml = `
        <div class="host-rel-wrap ${isRightLegend ? 'is-legend-right' : ''} ${hlCls}">
          <div class="host-led-tile-grid ${gridDensityCls}">${tiles}</div>
          <div class="host-rel-summary-box">
            ${legendHtml}
          </div>
        </div>
      `;
    }
  } else if (sel.kind === 'mem') {
    if (!overview) {
      icon = '&#xEEA0;';
      rotate = 0;
      title = '内存';
      hwSubtitle = HOST_SYS.memNote || `物理内存 ${HOST_SYS.memoryGb.toFixed(1)} GB`;
      reading = `${HOST_SYS.usedMemoryGb.toFixed(1)} / ${HOST_SYS.memoryGb.toFixed(1)} GB`;
      jumpTab = 'policy';
      jumpSub = 'scheduler';
      jumpLabel = '配置 NUMA 与电源';
      const kvItems = [
        renderTitleFirstKvItemHtml({ title: '使用中', stateText: `${HOST_SYS.usedMemoryGb.toFixed(1)} GB` }),
        renderTitleFirstKvItemHtml({ title: '可用', stateText: `${Math.max(0, HOST_SYS.memoryGb - HOST_SYS.usedMemoryGb).toFixed(1)} GB` }),
        renderTitleFirstKvItemHtml({ title: '物理总容量', stateText: `${HOST_SYS.memoryGb.toFixed(1)} GB` }),
        renderTitleFirstKvItemHtml({ title: 'NUMA 跨越', stateText: hostState.saved.numaSpanning ? '开启' : '关闭' }),
        renderTitleFirstKvItemHtml({ title: '电源方案', stateText: POWER_PLANS.find((p) => p.id === hostState.saved.powerPlanId)?.name || '高性能' })
      ];
      bodyHtml = `
        ${createUsageChartHtml('内存使用趋势', true, `${memPct}%`, HOST_SYS.memHistory, false, 136)}
        <div class="dt-kv-grid is-title-first is-2col">${kvItems.join('')}</div>
      `;
    } else {
      const mem = buildHostMemoryBreakdown();
      const rawFocusKey = hostState.hoveredMemKey || hostState.selectedMemKey;
      const focusKey = rawFocusKey && mem.orderedItems.some((it) => it.key === rawFocusKey) ? rawFocusKey : null;
      const hlCls = `is-hl-${hostState.highlightStyle || 'elevated-card'}`;

      bodyHtml = renderHostMemoryPieView(mem, focusKey, hlCls);
    }
  } else if (sel.kind === 'nic') {
    const nic = hostState.nics[sel.idx] || hostState.nics[0];
    const mediaIcons = {
      wired: '&#xE839;',
      wifi: '&#xE701;',
      cellular: '&#xEC3B;',
      tunnel: '&#xE705;'
    };
    icon = mediaIcons[nic.media] || '&#xE774;';
    rotate = 0;
    title = nic.name;
    hwSubtitle = nic.description;
    reading = nic.linkUp ? nic.speed : '未连接';
    jumpTab = 'switches';
    jumpSub = 'list';
    jumpLabel = '管理虚拟交换机';
    const kvItems = [
      renderTitleFirstKvItemHtml({ title: '链路状态', stateText: nic.linkUp ? `已连接 (${nic.speed})` : '未连接', caution: !nic.linkUp }),
      renderTitleFirstKvItemHtml({ title: '介质与桥接能力', stateText: nic.mediaNote || (nic.canBridge ? '支持桥接' : '仅支持 NAT') }),
      renderTitleFirstKvItemHtml({ title: '当前用途 / 绑定交换机', stateText: nic.boundSwitch }),
      renderTitleFirstKvItemHtml({ title: 'IPv4 地址', stateText: nic.ipv4, copyVal: nic.ipv4 !== '---' ? nic.ipv4 : '' }),
      renderTitleFirstKvItemHtml({ title: 'MAC 地址', stateText: nic.mac, copyVal: !nic.mac.startsWith('---') ? nic.mac : '' }),
      renderTitleFirstKvItemHtml({ title: '适配器名称', stateText: nic.description, copyVal: nic.description })
    ];
    bodyHtml = `
      ${renderTmScaleBar('物理网卡吞吐量', nic.linkUp ? nic.speed : '无链路')}
      <div class="transfer-charts-grid">
        ${createUsageChartHtml('发送', nic.linkUp, nic.sendRate, nic.sendPts, false, 136)}
        ${createUsageChartHtml('接收', nic.linkUp, nic.recvRate, nic.recvPts, false, 136)}
      </div>
      <div class="dt-kv-grid is-title-first is-2col" style="margin-top:8px;">${kvItems.join('')}</div>
    `;
  } else if (sel.kind === 'switch') {
    const sw = hostState.switches[sel.idx] || hostState.switches[0];
    const modeLabel = getSwitchModeLabel(sw);
    icon = '&#xE774;';
    rotate = 0;
    title = sw.name;
    hwSubtitle = sw.upstream;
    reading = modeLabel;
    jumpTab = 'switches';
    jumpSub = 'list';
    jumpLabel = '管理虚拟交换机';
    const kvItems = [
      renderTitleFirstKvItemHtml({ title: '连接模式', stateText: modeLabel }),
      renderTitleFirstKvItemHtml({ title: '宿主机接入', stateText: sw.allowManagementOs ? '已接入' : '未接入' }),
      renderTitleFirstKvItemHtml({ title: '宿主端口 IPv4', stateText: sw.ipv4, copyVal: sw.ipv4 !== '---' ? sw.ipv4 : '' }),
      renderTitleFirstKvItemHtml({ title: '已接入虚拟机', stateText: sw.endpoints.length ? sw.endpoints.join(', ') : '无' }),
      renderTitleFirstKvItemHtml({ title: '动态 MAC 池', stateText: `${hostState.saved.macMin} → ${hostState.saved.macMax}`, wide: true })
    ];
    bodyHtml = `
      ${renderTmScaleBar('交换机吞吐量', modeLabel)}
      <div class="transfer-charts-grid">
        ${createUsageChartHtml('发送', true, sw.sendRate, sw.sendPts, false, 136)}
        ${createUsageChartHtml('接收', true, sw.recvRate, sw.recvPts, false, 136)}
      </div>
      <div class="dt-kv-grid is-title-first is-2col" style="margin-top:8px;">${kvItems.join('')}</div>
    `;
  } else if (sel.kind === 'gpu') {
    const g = hostState.gpus[sel.idx] || hostState.gpus[0];
    icon = '&#xF211;';
    rotate = 0;
    title = `GPU ${sel.idx}`;
    hwSubtitle = g.name;
    reading = `${g.pct}%`;
    jumpTab = 'policy';
    jumpSub = 'compat';
    jumpLabel = '查看兼容策略';
    const kvItems = [
      renderTitleFirstKvItemHtml({ title: '芯片厂商', stateText: g.manu, copyVal: g.manu }),
      renderTitleFirstKvItemHtml({ title: '板卡厂商', stateText: g.vendor, copyVal: g.vendor }),
      renderTitleFirstKvItemHtml({ title: '专用显存', stateText: g.vram, copyVal: g.vram }),
      renderTitleFirstKvItemHtml({ title: '驱动版本', stateText: g.driver, copyVal: g.driver }),
      renderTitleFirstKvItemHtml({ title: 'GPU-PV 分区', stateText: g.partitionable ? `支持 (已分配 ${g.assignedVms.length} 台 VM)` : '不支持' }),
      renderTitleFirstKvItemHtml({ title: '分区策略', stateText: hostState.saved.pciePolicyUnlocked ? '已解锁' : '默认策略' }),
      renderTitleFirstKvItemHtml({ title: '设备实例路径', stateText: g.instanceId, copyVal: g.instanceId, wide: true })
    ];
    bodyHtml = `
      ${renderTmScaleBar('GPU 引擎利用率', '100%')}
      <div class="gpu-charts-grid">
        ${createUsageChartHtml('3D', true, `${g.pct}%`, g.pts3d, true, 82)}
        ${createUsageChartHtml('数据复制', true, `${g.ptsCopy.at(-1)}%`, g.ptsCopy, true, 82)}
        ${createUsageChartHtml('视频编码', true, `${g.ptsEnc.at(-1)}%`, g.ptsEnc, true, 82)}
        ${createUsageChartHtml('视频解码', true, `${g.ptsDec.at(-1)}%`, g.ptsDec, true, 82)}
      </div>
      <div class="dt-kv-grid is-title-first is-2col" style="margin-top:8px;">${kvItems.join('')}</div>
    `;
  } else if (sel.kind === 'disk') {
    const d = hostState.disks[sel.idx] || hostState.disks[0];
    icon = '&#xEDA2;';
    rotate = 0;
    title = `磁盘 ${d.number}`;
    hwSubtitle = d.name;
    reading = d.offline ? '已脱机' : d.sizeText;
    jumpTab = 'policy';
    jumpSub = 'storage';
    jumpLabel = '管理存储策略';
    const kvItems = [
      renderTitleFirstKvItemHtml({ title: '磁盘状态', stateText: d.status, caution: d.offline }),
      renderTitleFirstKvItemHtml({ title: '容量', stateText: d.sizeText }),
      renderTitleFirstKvItemHtml({ title: '直通资格', stateText: d.canPassthrough ? '支持直通' : '系统盘排除' }),
      renderTitleFirstKvItemHtml({ title: '只读保护', stateText: d.readOnly ? '只读' : '读写' }),
      renderTitleFirstKvItemHtml({ title: '自动挂载盘符', stateText: hostState.saved.volumeAutoMount ? '开启' : '关闭' }),
      renderTitleFirstKvItemHtml({ title: '原生 NVMe 栈', stateText: hostState.saved.nativeNvme ? '已开启' : '关闭' }),
      renderTitleFirstKvItemHtml({ title: '序列号', stateText: d.serial, copyVal: d.serial }),
      renderTitleFirstKvItemHtml({ title: '设备路径', stateText: d.path, copyVal: d.path })
    ];
    bodyHtml = `
      ${renderTmScaleBar('活动时间 / 传输速率', d.sizeText)}
      <div class="transfer-charts-grid">
        ${createUsageChartHtml('读取', !d.offline, d.readRate, d.readPts, false, 136)}
        ${createUsageChartHtml('写入', !d.offline, d.writeRate, d.writePts, false, 136)}
      </div>
      <div class="dt-kv-grid is-title-first is-2col" style="margin-top:8px;">${kvItems.join('')}</div>
    `;
  }

  const identitySingleLine = !hwSubtitle;
  if (overview) {
    const memory = buildHostMemoryBreakdown();
    const running = memory.vms.length;
    const total = running + HOST_VM_CPU_ALLOCATIONS.filter((vm) => !vm.running).length;
    return `
      <div class="vm-settings-area">
        <div class="settings-subnav">${[['cpu', 'CPU'], ['mem', '内存']].map(([key, label]) =>
          `<button type="button" class="subnav-btn ${sel.kind === key ? 'selected' : ''}" aria-pressed="${sel.kind === key}" data-overview-resource="${key}"><span>${label}</span></button>`
        ).join('')}</div>
        <div class="vm-settings-right">
          <div class="settings-host-scroll host-resource-overview">
            <div class="host-overview-summary">
              <span>虚拟机运行 <strong>${running} / ${total}</strong></span>
              <span>CPU 占用 <strong>${HOST_SYS.cpuPct}%</strong></span>
              <span>可用内存 <strong>${memory.freeGb.toFixed(1)} / ${memory.totalGb} GB</strong></span>
            </div>
            ${bodyHtml}
          </div>
        </div>
      </div>`;
  }
  const isCpu = sel.kind === 'cpu';
  const canvasHeadHtml = `
            <div class="tc-canvas-head">
              <div class="tc-canvas-identity ${identitySingleLine ? 'is-single-line' : ''}">
                <div class="tc-head-icon-box">
                  <span class="fi" style="${rotate ? `transform:rotate(${rotate}deg);` : ''}">${icon}</span>
                </div>
                <div class="tc-canvas-title-wrap">
                  <div class="tc-canvas-title-row">
                    <span class="tc-canvas-title${isCpu ? ' is-model-title' : ''}"${isCpu ? ` title="${esc(title)}"` : ''}>${isCpu ? title.split(/\r?\n/).map((line) => line.trim()).filter(Boolean).map(esc).join('<br>') : esc(title)}</span>
                  </div>
                  ${hwSubtitle ? `<div class="tc-canvas-sub">${esc(hwSubtitle)}</div>` : ''}
                </div>
              </div>
              <div class="tc-canvas-head-right">
                <div class="tc-canvas-reading">${esc(reading)}</div>
              </div>
            </div>
    `;

  return `
    <div class="vm-overview-scroll">
      <div class="vm-overview-alt-host mode-fill">
        <div class="tc-shell tm-wide">
          <div class="tc-left-rail">${railHtml}</div>
          <div class="tc-right-card">
            ${canvasHeadHtml}
            <div class="tc-canvas-body">${bodyHtml}</div>
          </div>
        </div>
      </div>
    </div>
  `;
}

// ============================================================================
// Tab 2: 交换机 (Full-width Flat Virtual Switch Management)
// ============================================================================
function renderSwitchesTab() {
  const cards = hostState.switches
    .map(
      (sw) => `
      <div class="winui-expander ${sw.expanded ? 'expanded' : ''}">
        <div class="winui-expander-header" data-host-expand="switch:${sw.id}">
          <div class="host-exp-head">
            <span class="fi" style="font-size:18px;">&#xE774;</span>
            <span class="host-exp-title">${esc(sw.name)}</span>
            <span class="vm-badge"><span class="vm-badge-text">${esc(getSwitchModeLabel(sw))}</span></span>
            <span class="host-exp-muted">${sw.endpoints.length} 台虚拟机接入</span>
          </div>
          <span class="fi winui-expander-chevron">${sw.expanded ? '&#xE70E;' : '&#xE70D;'}</span>
        </div>
        <div class="winui-expander-body">
          <div class="settings-field-panel">
            <div class="field-editor-stack">
              ${labelHtml('连接模式', '桥接：连接物理网卡。NAT：内部共享上网。仅内网：完全隔离内网。')}
              <select class="winui-select" data-host-sw-mode="${sw.id}">
                <option value="Bridge" ${sw.mode === 'Bridge' ? 'selected' : ''}>桥接 (Bridge)</option>
                <option value="NAT" ${sw.mode === 'NAT' ? 'selected' : ''}>NAT</option>
                <option value="Isolated" ${sw.mode === 'Isolated' ? 'selected' : ''}>仅内网 (Isolated)</option>
              </select>
            </div>
            <div class="field-editor-stack">
              ${labelHtml('上游物理网卡 / 共享接口', '由 HyperVSwitchService 识别的物理上联适配器。')}
              <input type="text" class="winui-textbox" value="${esc(sw.upstream)}" readonly />
            </div>
            <div class="field-editor-toggle-row">
              ${labelHtml('允许宿主 OS 共享', '开启后宿主操作系统会通过内部虚拟端口接入该交换机。')}
              <div class="winui-switch ${sw.allowManagementOs ? 'on' : ''}" data-host-sw-mgmt="${sw.id}">
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                <span class="switch-state-label">${sw.allowManagementOs ? '允许' : '禁止'}</span>
              </div>
            </div>
            <div class="field-editor-stack">
              ${labelHtml('已接入端点', '通过 GetFullSwitchNetworkStateAsync 查到的连接虚拟机。')}
              <div class="host-inline-tags">
                ${sw.endpoints.length ? sw.endpoints.map((vm) => `<span class="vm-badge"><span class="vm-badge-text">${esc(vm)}</span></span>`).join('') : '<span class="host-exp-muted">无已连接虚拟机</span>'}
              </div>
            </div>
          </div>
        </div>
      </div>
    `
    )
    .join('');

  return `
    <div class="host-flat-scroll">
      <div class="host-toolbar">
        <button type="button" class="winui-btn primary" id="hostAddSwitchBtn">新建交换机</button>
        <button type="button" class="winui-btn" id="hostRefreshSwitchesBtn">刷新</button>
      </div>
      <div class="host-exp-list">${cards}</div>
    </div>
  `;
}

// ============================================================================
// Tab 2: 环境 (V1.5.2 平台能力自检)
// ============================================================================
function renderEnvTab() {
  const statusRowHtml = (ok, label, { subText = '', actionHtml = '' } = {}) => {
    const iconCls = ok === true ? 'is-ok' : ok === false ? 'is-fail' : 'is-muted';
    const iconGlyph = ok === true ? '&#xEC61;' : ok === false ? '&#xEB90;' : '&#xE946;';
    return `
      <div class="isa-card">
        <div class="host-platform-row">
          <div class="host-platform-left">
            <span class="fi host-platform-status-icon ${iconCls}">${iconGlyph}</span>
            <div class="host-platform-text">
              <div class="host-platform-label">${esc(label)}</div>
              ${subText ? `<div class="host-platform-sub">${esc(subText)}</div>` : ''}
            </div>
          </div>
          ${actionHtml}
        </div>
      </div>
    `;
  };
  const hypervActionHtml = `
    <button type="button" class="winui-btn ${hostState.hyperVEnabled ? '' : 'primary'}" id="hostToggleHyperVBtn" style="flex-shrink:0;">
      ${hostState.hyperVEnabled ? '禁用' : '启用'}
    </button>
  `;
  return `
    <div class="host-flat-scroll">
      <div class="proc-group-stack">
        <div class="host-platform-group">
          <div class="host-platform-group-title">基本要求</div>
          ${statusRowHtml(hostState.cpuVirtOk, 'CPU 虚拟化')}
          ${statusRowHtml(hostState.hyperVEnabled, 'Hyper-V 服务', {
            subText: hostState.hyperVEnabled ? '' : '未启用或服务未运行',
            actionHtml: hypervActionHtml
          })}
        </div>
        <div class="host-platform-group">
          <div class="host-platform-group-title">GPU-PV 要求</div>
          ${statusRowHtml(hostState.gpuPvOk, `系统版本：${HOST_SYS.os}`)}
        </div>
        <div class="host-platform-group">
          <div class="host-platform-group-title">PCIe 直通要求</div>
          ${statusRowHtml(hostState.iommuOk, HOST_SYS.arch === 'ARM64' ? 'SMMU 状态' : 'IOMMU 状态')}
          ${statusRowHtml(hostState.isWindowsServer, '属于 Windows Server 系统')}
        </div>
      </div>
    </div>
  `;
}

// ============================================================================
// Tab 4: 设置 (Left Subnav + Single-Row WinUI Settings Cards)
// ============================================================================
function policySwitchCardHtml(key, title, desc, { span2 = false, disabled = false } = {}) {
  const on = Boolean(hostState.draft[key]);
  return `
    <div class="isa-card uniform-1row ${span2 ? 'span-2' : ''} ${disabled ? 'is-disabled' : ''}">
      <div class="isa-card-row">
        ${labelHtml(title, desc)}
        <div class="winui-switch ${on ? 'on' : ''} ${disabled ? 'disabled' : ''}" data-host-draft-switch="${key}" role="switch" aria-checked="${on}" tabindex="${disabled ? -1 : 0}">
          <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
        </div>
      </div>
    </div>
  `;
}

function renderPolicyTab() {
  const subnav = [
    { key: 'scheduler', label: 'CPU 调度' },
    { key: 'power', label: '电源方案' },
    { key: 'paths', label: '默认目录' },
    { key: 'storage', label: '存储策略' },
    { key: 'mac', label: '网络策略' },
    { key: 'compat', label: '兼容策略' },
    { key: 'preferences', label: '会话偏好' }
  ];

  const activeSub = subnav.some((s) => s.key === hostState.policySub) ? hostState.policySub : 'scheduler';
  const subHtml = subnav
    .map(
      (s) => `
      <button type="button" class="subnav-btn ${activeSub === s.key ? 'selected' : ''}" data-host-policy-sub="${s.key}">
        <span>${esc(s.label)}</span>
      </button>
    `
    )
    .join('');

  let contentHtml = '';
  if (activeSub === 'scheduler') {
    const schedBtns = SCHEDULER_OPTIONS.map(
      (o) => `<button type="button" class="isa-pill-btn ${hostState.draft.scheduler === o.value ? 'active' : ''}" data-host-sched="${o.value}">${esc(o.label)}</button>`
    ).join('');
    const schedGuides = [
      {
        value: 'Classic',
        title: '经典调度器',
        selectedCores: [0, 1, 2, 3],
        isAuto: false,
        body: '由 Hypervisor 调度，可自由分配所有线程，多个虚拟机可能会共用同一个线程。适合个人高性能场景，支持 CPU 组、CPU 配额、权重和预留。'
      },
      {
        value: 'Core',
        title: '核心调度器',
        selectedCores: [0, 2],
        isAuto: false,
        body: '由 Hypervisor 调度，同一个物理核心的所有线程只分给同一台虚拟机。适合多机隔离、服务器场景，支持 CPU 组、CPU 配额、权重和预留。'
      },
      {
        value: 'Root',
        title: '根调度器',
        selectedCores: [],
        isAuto: true,
        body: '由系统内核调度，能识别大小核并动态调节功耗与性能。不支持 CPU 组、CPU 配额、权重和预留，核心绑定通过宿主机进程亲和性实现。'
      }
    ];
    const schedGuideHtml = schedGuides
      .map((g) => {
        const tilesHtml = [0, 1, 2, 3]
          .map(
            (core) => `
            <button type="button" class="cpu-affinity-tile is-demo ${g.selectedCores.includes(core) ? 'selected' : ''}" tabindex="-1">
              <span class="cpu-affinity-num">${core}</span>
            </button>
          `
          )
          .join('');
        return `
        <div class="host-sched-guide-item ${hostState.draft.scheduler === g.value ? 'is-active' : ''}">
          <div class="host-sched-guide-title">${esc(g.title)}</div>
          <div class="host-sched-demo-wrap ${g.isAuto ? 'is-auto' : ''}">
            <div class="host-sched-demo-grid">${tilesHtml}</div>
            ${g.isAuto ? '<span class="host-sched-demo-auto">Auto</span>' : ''}
          </div>
          <p class="host-sched-guide-body">${esc(g.body)}</p>
        </div>
      `;
      })
      .join('');
    contentHtml = `
      <div class="proc-group-stack">
        <div class="isa-grid-2x2">
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${labelHtml('调度器', '经典 / 核心 / 根三种 Hyper-V CPU 调度模式，更改后需重启宿主生效。')}
              <div class="isa-pill-track cols-3" style="width:310px;flex-shrink:0;">${schedBtns}</div>
            </div>
          </div>
          <div class="isa-card span-2">
            <div class="host-sched-guide">${schedGuideHtml}</div>
          </div>
          ${policySwitchCardHtml('numaSpanning', 'NUMA 跨越', '仅对多 NUMA 节点处理器生效。开启后允许虚拟机跨节点分配内存，但会增加内存延迟。', { span2: true })}
        </div>
      </div>
    `;
  } else if (activeSub === 'power') {
    const activePlan = POWER_PLANS.find((p) => p.id === hostState.draft.powerPlanId) || POWER_PLANS[0];
    const comboItems = POWER_PLANS.map(
      (p) => `
        <button type="button" class="winui-combo-item ${p.id === activePlan.id ? 'is-selected' : ''}" data-host-power-plan="${p.id}" role="option" aria-selected="${p.id === activePlan.id}">
          <span>${esc(p.name)}</span>
        </button>
      `
    ).join('');
    contentHtml = `
      <div class="proc-group-stack">
        <div class="isa-grid-2x2">
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${labelHtml('电源方案', '枚举现有 Windows 电源计划并切换。')}
              <div class="winui-combo ${hostState.powerComboOpen ? 'is-open' : ''}" id="hostPowerPlanCombo">
                <button type="button" class="winui-combo-trigger" id="hostPowerPlanTrigger" aria-haspopup="listbox" aria-expanded="${hostState.powerComboOpen}">
                  <span>${esc(activePlan.name)}</span>
                  <span class="fi winui-combo-chevron">&#xE70D;</span>
                </button>
                <div class="winui-combo-flyout" role="listbox" ${hostState.powerComboOpen ? '' : 'hidden'}>
                  ${comboItems}
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    `;
  } else if (activeSub === 'paths') {
    contentHtml = `
      <div class="proc-group-stack">
        <div class="isa-grid-2x2">
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${labelHtml('新建虚拟机默认位置', '新建虚拟机配置文件 (.vmcx) 的默认存放目录。')}
              <div class="host-path-row" style="width:360px;flex-shrink:0;">
                <input type="text" class="winui-textbox" style="height:36px;" value="${esc(hostState.draft.defaultVmPath)}" readonly />
                <button type="button" class="winui-btn" data-host-pick="vm">选择</button>
              </div>
            </div>
          </div>
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${labelHtml('新建虚拟硬盘默认位置', '新建虚拟磁盘文件 (.vhdx) 的默认存放目录。')}
              <div class="host-path-row" style="width:360px;flex-shrink:0;">
                <input type="text" class="winui-textbox" style="height:36px;" value="${esc(hostState.draft.defaultVhdPath)}" readonly />
                <button type="button" class="winui-btn" data-host-pick="vhd">选择</button>
              </div>
            </div>
          </div>
        </div>
      </div>
    `;
  } else if (activeSub === 'storage') {
    contentHtml = `
      <div class="proc-group-stack">
        <div class="isa-grid-2x2">
          ${policySwitchCardHtml('volumeAutoMount', '插入磁盘自动挂载盘符', '控制 Windows 检测到新磁盘或新分区时是否自动分配盘符并挂载。关闭后，新接入的卷不会自动显示盘符，需要手动分配。')}
          ${policySwitchCardHtml('nativeNvme', '主机原生 NVMe', '绕过传统的 SCSI 转换层，用原生 NVMe 驱动访问固态硬盘以提升随机读写性能。仅支持 Build 26100 及以上系统，需重启宿主生效，部分设备可能不兼容。')}
        </div>
      </div>
    `;
  } else if (activeSub === 'mac') {
    const macDirty = isGroupDirty(['macMin', 'macMax']);
    const isDefaultMac =
      hostState.draft.macMin === '00-15-5D-01-00-00' &&
      hostState.draft.macMax === '00-15-5D-01-FF-FF' &&
      hostState.saved.macMin === '00-15-5D-01-00-00' &&
      hostState.saved.macMax === '00-15-5D-01-FF-FF';
    contentHtml = `
      <div class="proc-group-stack">
        <div class="isa-grid-2x2">
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${labelHtml('全局动态 MAC 地址池', '设置虚拟机使用动态 MAC 地址时的分配范围。局域网内有多台 Hyper-V 宿主机时，建议错开号段避免地址冲突。')}
              <div class="host-mac-row" style="flex-shrink:0;">
                <input type="text" class="winui-textbox host-mac-input" style="height:36px;" id="hostMacMinInput" maxlength="17" value="${esc(hostState.draft.macMin)}" />
                <span class="host-mac-arrow">→</span>
                <input type="text" class="winui-textbox host-mac-input" style="height:36px;" id="hostMacMaxInput" maxlength="17" value="${esc(hostState.draft.macMax)}" />
                <button type="button" class="winui-btn" id="hostResetMacBtn" ${isDefaultMac ? 'disabled' : ''}>恢复默认</button>
                <button type="button" class="winui-btn primary" id="hostSaveMacBtn" ${macDirty ? '' : 'disabled'}>保存</button>
              </div>
            </div>
          </div>
        </div>
      </div>
    `;
  } else if (activeSub === 'compat') {
    contentHtml = `
      <div class="proc-group-stack">
        <div class="isa-grid-2x2">
          ${policySwitchCardHtml('pciePolicyUnlocked', '解锁消费级硬件限制', '关闭 RequireSecureDeviceAssignment 和 RequireSupportedDeviceAssignment 两项检查，使消费级硬件或未认证设备也能显卡分区或设备直通。')}
          ${policySwitchCardHtml('openHclFirmware', '从文件加载 OpenHCL 固件', '允许 Hyper-V 从本地文件加载自定义的 OpenHCL 或 IGVM 固件，用于自定义虚拟机引导环境。')}
          ${policySwitchCardHtml('serverProductTypeSpoof', '切换系统为服务器版本', '将系统内核的产品标识切换为 ServerNT，用于解锁原生 PCIe 直通等服务器专属能力，更改后需重启宿主生效。部分没有为服务器系统设计的驱动，可能会在系统引导时蓝屏无法启动，或无法正常工作，需谨慎开启。', { span2: true })}
        </div>
      </div>
    `;
  } else {
    contentHtml = `
      <div class="proc-group-stack">
        <div class="isa-grid-2x2">
          ${policySwitchCardHtml('enhancedSession', '增强会话模式', '主机侧许可：允许虚拟机控制台通过增强会话连接。')}
          ${policySwitchCardHtml('autoOpenConsole', '启动后自动打开控制台', '单台虚拟机从关机状态启动成功后，自动打开连接窗口。')}
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${labelHtml('默认电源按钮动作', '点击运行中虚拟机主电源按钮时的默认动作。')}
              <div class="isa-pill-track" style="width:220px;flex-shrink:0;">
                <button type="button" class="isa-pill-btn ${hostState.draft.defaultPowerAction === 'Shutdown' ? 'active' : ''}" data-host-power-action="Shutdown">关机</button>
                <button type="button" class="isa-pill-btn ${hostState.draft.defaultPowerAction === 'Save' ? 'active' : ''}" data-host-power-action="Save">保存状态</button>
              </div>
            </div>
          </div>
        </div>
      </div>
    `;
  }

  return `
    <div class="vm-settings-area">
      <div class="settings-subnav">${subHtml}</div>
      <div class="vm-settings-right"><div class="settings-host-scroll">${contentHtml}</div></div>
    </div>
  `;
}

function getHostTabs() {
  return HOST_TABS_BASE;
}

function renderBodyByActiveTab() {
  switch (hostState.activeTab) {
    case 'overview':
      return renderResourceTab(true);
    case 'state':
      return renderResourceTab();
    case 'env':
      return renderEnvTab();
    case 'switches':
      return renderSwitchesTab();
    case 'policy':
    default:
      return renderPolicyTab();
  }
}

let onHostRenderCallback = null;

export function getHostOverviewCandidate() {
  return {
    cardStyle: hostState.cardStyle,
    cardContent: hostState.cardContent,
    cpuDetailStyle: hostState.cpuDetailStyle,
    highlightStyle: hostState.highlightStyle || 'elevated-card',
    legendPlacement: hostState.legendPlacement || 'right',
    mockThreads: String(hostState.mockThreads || '12'),
    mockVmCount: String(hostState.mockVmCount || '2')
  };
}

export function setHostOverviewCandidate(payloadOrPlacement) {
  if (typeof payloadOrPlacement === 'string') {
    if (CARD_STYLE_CANDIDATES.includes(payloadOrPlacement)) {
      hostState.cardStyle = payloadOrPlacement;
    } else if (CPU_DETAIL_STYLES.includes(payloadOrPlacement)) {
      hostState.cpuDetailStyle = payloadOrPlacement;
      hostState.activeTab = 'overview';
      hostState.overviewResource = 'cpu';
    }
  } else if (payloadOrPlacement && typeof payloadOrPlacement === 'object') {
    if (CARD_STYLE_CANDIDATES.includes(payloadOrPlacement.cardStyle)) {
      hostState.cardStyle = payloadOrPlacement.cardStyle;
    }
    if (CARD_CONTENT_CANDIDATES.includes(payloadOrPlacement.cardContent)) {
      hostState.cardContent = payloadOrPlacement.cardContent;
    }
    if (CPU_DETAIL_STYLES.includes(payloadOrPlacement.cpuDetailStyle)) {
      hostState.cpuDetailStyle = payloadOrPlacement.cpuDetailStyle;
      hostState.activeTab = 'overview';
      hostState.overviewResource = 'cpu';
    }
    if (payloadOrPlacement.legendPlacement === 'right' || payloadOrPlacement.legendPlacement === 'bottom') {
      hostState.legendPlacement = payloadOrPlacement.legendPlacement;
      hostState.activeTab = 'overview';
      hostState.overviewResource = 'cpu';
    }
    if (HIGHLIGHT_STYLES.includes(payloadOrPlacement.highlightStyle)) {
      hostState.highlightStyle = payloadOrPlacement.highlightStyle;
      hostState.activeTab = 'overview';
      hostState.overviewResource = 'cpu';
      if (!hostState.selectedLedVm && !hostState.hoveredLedVm) {
        const firstVm = getMockRunningVms(hostState.mockVmCount)[0];
        hostState.selectedLedVm = firstVm ? firstVm.name : '__host__';
      }
    }
    if (payloadOrPlacement.mockThreads) {
      hostState.mockThreads = String(payloadOrPlacement.mockThreads);
      hostState.activeTab = 'overview';
      hostState.overviewResource = 'cpu';
    }
    if (payloadOrPlacement.mockVmCount) {
      hostState.mockVmCount = String(payloadOrPlacement.mockVmCount);
      hostState.selectedLedVm = null;
      hostState.hoveredLedVm = null;
      hostState.activeTab = 'overview';
      hostState.overviewResource = 'cpu';
    }
  } else {
    return;
  }

  renderHostPage();
}

let hostNoticeTimer = null;

export function renderHostPage() {
  const page = document.getElementById('pageHost');
  if (!page) return;

  const scrollSelectors = ['.tc-left-rail', '.tc-right-card', '.tc-canvas-body', '.vm-overview-scroll', '.settings-host-scroll', '.host-flat-scroll', '.host-led-legend', '.host-led-vm-scroll', '.host-led-tile-grid'];
  const savedScrolls = scrollSelectors.map((sel) => {
    const el = page.querySelector?.(sel);
    return el ? { sel, top: el.scrollTop, left: el.scrollLeft } : null;
  });


  const tabs = getHostTabs();
  if (!tabs.some((t) => t.key === hostState.activeTab)) {
    hostState.activeTab = tabs[0].key;
  }

  const tabsHtml = tabs
    .map(
      (t) => `
      <button type="button" class="vm-tab-btn ${hostState.activeTab === t.key ? 'selected' : ''}" data-host-tab="${t.key}">
        <span class="fi" style="font-size:15px;">${t.icon}</span>
        <span>${esc(t.label)}</span>
      </button>
    `
    )
    .join('');

  if (hostNoticeTimer) {
    clearTimeout(hostNoticeTimer);
    hostNoticeTimer = null;
  }

  let toastHtml = '';
  let bannerHtml = '';
  if (hostState.notice) {
    const sev = hostState.notice.severity || 'info';
    if (sev === 'success') {
      const expiresAt = hostState.notice._expiresAt ?? (hostState.notice._expiresAt = Date.now() + 2600);
      const remainMs = expiresAt - Date.now();
      if (remainMs <= 0) {
        hostState.notice = null;
      } else {
        toastHtml = `
          <div class="winui-floating-toast" role="status">
            <span class="winui-floating-toast-badge"><span class="fi">&#xE73E;</span></span>
            <span>${esc(hostState.notice.text)}</span>
          </div>
        `;
        if (typeof setTimeout === 'function') {
          hostNoticeTimer = setTimeout(() => {
            if (hostState.notice && hostState.notice.severity === 'success') {
              hostState.notice = null;
              renderHostPage();
            }
          }, remainMs);
        }
      }
    } else {
      const iconMap = { warning: '&#xE7BA;', info: '&#xE946;', error: '&#xE711;' };
      bannerHtml = `
        <div class="winui-floating-banner is-${sev}" role="alert">
          <span class="winui-floating-banner-badge"><span class="fi">${iconMap[sev] || '&#xE946;'}</span></span>
          <span class="winui-floating-banner-text">${esc(hostState.notice.text)}</span>
          <button type="button" class="winui-floating-banner-close" id="hostNoticeCloseBtn" title="关闭">
            <span class="fi" style="font-size:11px;">&#xE711;</span>
          </button>
        </div>
      `;
    }
  }

  page.innerHTML = `
    <div class="vm-pagetabs host-pagetabs">${tabsHtml}</div>
    ${renderBodyByActiveTab()}
    ${bannerHtml}
    ${toastHtml}
  `;

  savedScrolls.forEach((entry) => {
    if (!entry) return;
    const el = page.querySelector?.(entry.sel);
    if (el) {
      el.scrollTop = entry.top;
      el.scrollLeft = entry.left;
    }
  });

  if (onHostRenderCallback) {
    requestAnimationFrame(() => onHostRenderCallback());
  }
  syncHostLedTooltip(page);
}

function getHostLedTooltipEl() {
  if (typeof document === 'undefined' || typeof document.createElement !== 'function' || !document.body) return null;
  let tip = document.getElementById('hostLedTooltip');
  if (!tip) {
    tip = document.createElement('div');
    tip.id = 'hostLedTooltip';
    tip.className = 'host-led-tooltip';
    tip.hidden = true;
    document.body.appendChild(tip);
  }
  return tip;
}

function syncHostLedTooltip(page) {
  const tip = getHostLedTooltipEl();
  if (!tip) return;
  if (hostState.activeTab !== 'overview') {
    tip.hidden = true;
    return;
  }

  let anchorEl = null;
  if (hostState.hoveredMemSeg !== null && hostState.overviewResource === 'mem') {
    anchorEl = page?.querySelector?.(`[data-mem-seg="${CSS?.escape ? CSS.escape(hostState.hoveredMemSeg) : hostState.hoveredMemSeg}"]`);
    const rawMemTip = anchorEl?.getAttribute?.('data-mem-tip');
    if (anchorEl && rawMemTip) {
      if (typeof getComputedStyle === 'function') {
        const accent = getComputedStyle(anchorEl).getPropertyValue('--VmAccentBrush').trim();
        if (accent) tip.style.setProperty('--VmAccentBrush', accent);
      }
      try {
        const parsed = JSON.parse(rawMemTip);
        const rowsHtml = (parsed.rows || [])
          .map(
            (r) => `
              <div class="host-led-tip-row">
                <span class="host-led-tip-left">
                  <span class="host-led-tip-dot" style="background:${r.color};"></span>
                  <span class="host-led-tip-name">${esc(r.label)}</span>
                </span>
                <span class="host-led-tip-pct">${esc(r.value)}</span>
              </div>
            `
          )
          .join('');
        const noteHtml = parsed.note ? `<div class="host-mem-tip-note">${esc(parsed.note)}</div>` : '';
        tip.innerHTML = `<div class="host-led-tip-head">${esc(parsed.title)}</div>${rowsHtml}${noteHtml}`;
      } catch {
        tip.textContent = rawMemTip;
      }
      tip.hidden = false;
      const rect = anchorEl.getBoundingClientRect?.();
      if (rect) {
        const tipRect = tip.getBoundingClientRect();
        const left = Math.max(8, Math.min(window.innerWidth - tipRect.width - 8, rect.left + rect.width / 2 - tipRect.width / 2));
        const top = rect.top - tipRect.height - 6 >= 8 ? rect.top - tipRect.height - 6 : rect.bottom + 6;
        tip.style.left = `${Math.round(left)}px`;
        tip.style.top = `${Math.round(top)}px`;
      }
      return;
    }
  }

  if (hostState.hoveredLedLp === null) {
    tip.hidden = true;
    return;
  }
  const tile = page?.querySelector?.(`[data-led-lp="${hostState.hoveredLedLp}"]`);
  const rawTip = tile?.getAttribute?.('data-led-tip');
  if (!tile || !rawTip) {
    tip.hidden = true;
    return;
  }
  if (typeof getComputedStyle === 'function') {
    const accent = getComputedStyle(tile).getPropertyValue('--VmAccentBrush').trim();
    if (accent) tip.style.setProperty('--VmAccentBrush', accent);
  }
  try {
    const parsed = JSON.parse(rawTip);
    const rowsHtml = (parsed.slices || [])
      .map(
        (s) => `
          <div class="host-led-tip-row">
            <span class="host-led-tip-left">
              <span class="host-led-tip-dot" style="background:${s.color};"></span>
              <span class="host-led-tip-name">${esc(s.label)}</span>
            </span>
            <span class="host-led-tip-pct">${s.pct}%</span>
          </div>
        `
      )
      .join('');
    tip.innerHTML = `<div class="host-led-tip-head">线程 ${parsed.lp}</div>${rowsHtml}`;
  } catch {
    tip.textContent = rawTip;
  }
  tip.hidden = false;
  const rect = tile.getBoundingClientRect?.();
  if (rect) {
    const tipRect = tip.getBoundingClientRect();
    const left = Math.max(8, Math.min(window.innerWidth - tipRect.width - 8, rect.left + rect.width / 2 - tipRect.width / 2));
    const top = rect.top - tipRect.height - 6 >= 8 ? rect.top - tipRect.height - 6 : rect.bottom + 6;
    tip.style.left = `${Math.round(left)}px`;
    tip.style.top = `${Math.round(top)}px`;
  }
}

function showFlyout(anchorBtn) {
  const flyout = document.getElementById('settingInfoFlyout');
  const winEl = document.getElementById('winuiWindow');
  if (!flyout || !winEl) return;
  const desc = anchorBtn.getAttribute('data-info-desc') || '';
  const winRect = winEl.getBoundingClientRect();
  const btnRect = anchorBtn.getBoundingClientRect();
  flyout.textContent = desc;
  flyout.hidden = false;
  const left = Math.max(12, Math.min(winRect.width - 352, btnRect.left - winRect.left - 8));
  const top = btnRect.bottom - winRect.top + 6;
  flyout.style.left = `${left}px`;
  flyout.style.top = `${top}px`;
}

function applyKeys(keys) {
  keys.forEach((k) => {
    hostState.saved[k] = hostState.draft[k];
  });
}

function revertKeys(keys) {
  keys.forEach((k) => {
    hostState.draft[k] = hostState.saved[k];
  });
}

export function initHostPage(options = {}) {
  onHostRenderCallback = options.onRender || null;
  const page = document.getElementById('pageHost');
  if (!page || page.dataset.bound === 'true') return;
  page.dataset.bound = 'true';

  page.addEventListener('mouseover', (e) => {
    const target = e.target.closest?.('[data-host-led-vm]');
    const nextVm = target ? target.getAttribute('data-host-led-vm') : null;
    if (hostState.hoveredLedVm !== nextVm) {
      hostState.hoveredLedVm = nextVm;
      renderHostPage();
    }
    const memTarget = e.target.closest?.('[data-mem-key]');
    const nextMem = memTarget ? memTarget.getAttribute('data-mem-key') : null;
    if (hostState.hoveredMemKey !== nextMem) {
      hostState.hoveredMemKey = nextMem;
      renderHostPage();
    }
    const memSeg = e.target.closest?.('[data-mem-seg]');
    const nextMemSeg = memSeg ? memSeg.getAttribute('data-mem-seg') : null;
    if (hostState.hoveredMemSeg !== nextMemSeg) {
      hostState.hoveredMemSeg = nextMemSeg;
      syncHostLedTooltip(page);
    }
    const lpTile = e.target.closest?.('[data-led-lp]');
    const nextLp = lpTile ? lpTile.getAttribute('data-led-lp') : null;
    if (hostState.hoveredLedLp !== nextLp) {
      hostState.hoveredLedLp = nextLp;
      syncHostLedTooltip(page);
    }
  });

  page.addEventListener('mouseleave', () => {
    let changed = false;
    if (hostState.hoveredLedVm) {
      hostState.hoveredLedVm = null;
      changed = true;
    }
    if (hostState.hoveredMemKey) {
      hostState.hoveredMemKey = null;
      changed = true;
    }
    if (hostState.hoveredMemSeg !== null) {
      hostState.hoveredMemSeg = null;
      syncHostLedTooltip(page);
    }
    if (hostState.hoveredLedLp !== null) {
      hostState.hoveredLedLp = null;
      syncHostLedTooltip(page);
    }
    if (changed) renderHostPage();
  });

  page.addEventListener('click', (e) => {
    const copyBtn = e.target.closest('[data-copy]');
    if (copyBtn) {
      e.stopPropagation();
      const val = copyBtn.getAttribute('data-copy') || '';
      if (val) {
        navigator.clipboard?.writeText(val).catch(() => {});
        hostState.notice = { severity: 'success', text: `已复制到剪贴板：${val}` };
        renderHostPage();
      }
      return;
    }

    const infoBtn = e.target.closest('[data-info-desc]');
    if (infoBtn) {
      e.stopPropagation();
      showFlyout(infoBtn);
      return;
    }

    if (e.target.closest('#hostNoticeCloseBtn')) {
      hostState.notice = null;
      renderHostPage();
      return;
    }

    const ledChip = e.target.closest('[data-host-led-vm]');
    if (ledChip) {
      const vmKey = ledChip.getAttribute('data-host-led-vm');
      hostState.selectedLedVm = hostState.selectedLedVm === vmKey ? null : vmKey;
      renderHostPage();
      return;
    }

    const memChip = e.target.closest('[data-mem-key]');
    if (memChip) {
      const memKey = memChip.getAttribute('data-mem-key');
      hostState.selectedMemKey = hostState.selectedMemKey === memKey ? null : memKey;
      renderHostPage();
      return;
    }

    const tabBtn = e.target.closest('[data-host-tab]');
    if (tabBtn) {
      hostState.activeTab = tabBtn.getAttribute('data-host-tab') || 'state';
      renderHostPage();
      return;
    }

    const resourceButton = e.target.closest('[data-overview-resource]');
    if (resourceButton) {
      hostState.overviewResource = resourceButton.dataset.overviewResource;
      hostState.hoveredLedVm = null;
      hostState.hoveredLedLp = null;
      hostState.hoveredMemKey = null;
      hostState.hoveredMemSeg = null;
      syncHostLedTooltip(page);
      renderHostPage();
      return;
    }

    const railBtn = e.target.closest('[data-host-rail]');
    if (railBtn) {
      const [kind, idxStr] = (railBtn.getAttribute('data-host-rail') || 'cpu:0').split(':');
      hostState.selectedRail = { kind, idx: Number(idxStr) || 0 };
      renderHostPage();
      return;
    }

    const jumpBtn = e.target.closest('[data-host-jump]');
    if (jumpBtn) {
      const [tab, section] = (jumpBtn.getAttribute('data-host-jump') || 'policy:platform').split(':');
      hostState.activeTab = tab === 'switches' ? 'switches' : 'policy';
      if (hostState.activeTab === 'policy' && section) {
        hostState.policySub = section;
      }
      renderHostPage();
      return;
    }

    const policySubBtn = e.target.closest('[data-host-policy-sub]');
    if (policySubBtn) {
      hostState.policySub = policySubBtn.getAttribute('data-host-policy-sub') || 'scheduler';
      renderHostPage();
      return;
    }

    const expHead = e.target.closest('[data-host-expand]');
    if (expHead) {
      const [kind, id] = (expHead.getAttribute('data-host-expand') || '').split(':');
      if (kind === 'switch') {
        const sw = hostState.switches.find((x) => x.id === id);
        if (sw) sw.expanded = !sw.expanded;
      }
      renderHostPage();
      return;
    }

    if (e.target.closest('#hostToggleHyperVBtn')) {
      hostState.hyperVEnabled = !hostState.hyperVEnabled;
      hostState.notice = {
        severity: 'warning',
        text: hostState.hyperVEnabled ? '已请求启用 Hyper-V 功能，需重启宿主完成配置。' : '已请求禁用 Hyper-V 功能，需重启宿主完成配置。'
      };
      renderHostPage();
      return;
    }

    const swMgmt = e.target.closest('[data-host-sw-mgmt]');
    if (swMgmt) {
      const id = swMgmt.getAttribute('data-host-sw-mgmt');
      const sw = hostState.switches.find((x) => x.id === id);
      if (sw) {
        sw.allowManagementOs = !sw.allowManagementOs;
        hostState.notice = { severity: 'success', text: '已应用' };
        renderHostPage();
      }
      return;
    }

    if (e.target.closest('#hostAddSwitchBtn')) {
      const idx = hostState.switches.length + 1;
      hostState.switches.push({
        id: `sw-custom-${idx}`,
        name: `Custom Switch ${idx}`,
        switchKind: 'Private',
        natEngine: 'None',
        subnetPrefix: null,
        mode: 'Isolated',
        allowManagementOs: false,
        upstream: '无宿主机或外网连接',
        ipv4: '---',
        sendRate: '0 Kbps',
        recvRate: '0 Kbps',
        sendPts: [0, 0, 0, 0, 0, 0, 0],
        recvPts: [0, 0, 0, 0, 0, 0, 0],
        endpoints: [],
        expanded: true
      });
      hostState.notice = { severity: 'success', text: `已新建专用交换机 Custom Switch ${idx}` };
      renderHostPage();
      return;
    }

    const schedBtn = e.target.closest('[data-host-sched]');
    if (schedBtn) {
      const next = schedBtn.getAttribute('data-host-sched') || 'Core';
      if (hostState.saved.scheduler !== next) {
        hostState.draft.scheduler = next;
        hostState.saved.scheduler = next;
        hostState.notice = {
          severity: 'warning',
          text: `调度器已切换为「${getSchedulerLabel(next, true)}」，需重启宿主后生效。`
        };
      }
      renderHostPage();
      return;
    }

    if (e.target.closest('#hostPowerPlanTrigger')) {
      e.stopPropagation();
      hostState.powerComboOpen = !hostState.powerComboOpen;
      renderHostPage();
      return;
    }

    const powerPlanBtn = e.target.closest('[data-host-power-plan]');
    if (powerPlanBtn) {
      const next = powerPlanBtn.getAttribute('data-host-power-plan') || 'high';
      hostState.draft.powerPlanId = next;
      hostState.saved.powerPlanId = next;
      hostState.powerComboOpen = false;
      hostState.notice = { severity: 'success', text: '已应用' };
      renderHostPage();
      return;
    }

    if (hostState.powerComboOpen && !e.target.closest('#hostPowerPlanCombo')) {
      hostState.powerComboOpen = false;
      renderHostPage();
    }

    const powerActionBtn = e.target.closest('[data-host-power-action]');
    if (powerActionBtn) {
      const next = powerActionBtn.getAttribute('data-host-power-action') || 'Shutdown';
      hostState.draft.defaultPowerAction = next;
      hostState.saved.defaultPowerAction = next;
      hostState.notice = { severity: 'success', text: '已应用' };
      renderHostPage();
      return;
    }

    const pickBtn = e.target.closest('[data-host-pick]');
    if (pickBtn) {
      const kind = pickBtn.getAttribute('data-host-pick');
      const presets = kind === 'vm' ? PATH_PRESETS.vm : PATH_PRESETS.vhd;
      const key = kind === 'vm' ? 'defaultVmPath' : 'defaultVhdPath';
      const next = presets[(presets.indexOf(hostState.draft[key]) + 1) % presets.length];
      hostState.draft[key] = next;
      hostState.saved[key] = next;
      hostState.notice = { severity: 'success', text: '已应用' };
      renderHostPage();
      return;
    }

    const draftSw = e.target.closest('[data-host-draft-switch]');
    if (draftSw && !draftSw.classList.contains('disabled')) {
      const key = draftSw.getAttribute('data-host-draft-switch');
      if (key && key in hostState.draft) {
        const next = !hostState.draft[key];
        hostState.draft[key] = next;
        hostState.saved[key] = next;
        if (key === 'serverProductTypeSpoof' || key === 'nativeNvme') {
          hostState.notice = {
            severity: 'warning',
            text: `${key === 'nativeNvme' ? '主机原生 NVMe' : 'Server 系统标识'}已更新，需重启宿主后生效。`
          };
        } else {
          hostState.notice = { severity: 'success', text: '已应用' };
        }
        renderHostPage();
      }
      return;
    }

    if (e.target.closest('#hostResetMacBtn')) {
      hostState.draft.macMin = '00-15-5D-01-00-00';
      hostState.draft.macMax = '00-15-5D-01-FF-FF';
      applyKeys(['macMin', 'macMax']);
      hostState.notice = { severity: 'success', text: '已恢复默认 MAC 地址池。' };
      renderHostPage();
      return;
    }

    if (e.target.closest('#hostSaveMacBtn')) {
      const norm = (v) => String(v || '').trim().toUpperCase().replace(/:/g, '-');
      const macRe = /^([0-9A-F]{2}-){5}[0-9A-F]{2}$/;
      const minMac = norm(hostState.draft.macMin);
      const maxMac = norm(hostState.draft.macMax);
      if (!macRe.test(minMac) || !macRe.test(maxMac)) {
        hostState.notice = {
          severity: 'error',
          text: 'MAC 地址格式无效，请输入有效的 12 位十六进制地址（格式 XX-XX-XX-XX-XX-XX）。'
        };
        renderHostPage();
        return;
      }
      if (minMac.replace(/-/g, '') > maxMac.replace(/-/g, '')) {
        hostState.notice = {
          severity: 'error',
          text: 'MAC 地址范围无效：起始地址不能大于结束地址。'
        };
        renderHostPage();
        return;
      }
      hostState.draft.macMin = minMac;
      hostState.draft.macMax = maxMac;
      applyKeys(['macMin', 'macMax']);
      hostState.notice = { severity: 'success', text: '全局动态 MAC 地址池已更新。' };
      renderHostPage();
      return;
    }
  });

  if (typeof document !== 'undefined') {
    document.addEventListener('click', (e) => {
      if (hostState.powerComboOpen && !e.target.closest('#hostPowerPlanCombo')) {
        hostState.powerComboOpen = false;
        renderHostPage();
      }
    });
  }

  page.addEventListener('change', (e) => {
    if (e.target.id === 'hostPowerPlanSelect') {
      hostState.draft.powerPlanId = e.target.value;
      hostState.saved.powerPlanId = e.target.value;
      hostState.notice = { severity: 'success', text: '已应用' };
      renderHostPage();
    } else if (e.target.id === 'hostPowerActionSelect') {
      hostState.draft.defaultPowerAction = e.target.value;
      hostState.saved.defaultPowerAction = e.target.value;
      hostState.notice = { severity: 'success', text: '已应用' };
      renderHostPage();
    } else if (e.target.matches('[data-host-sw-mode]')) {
      const id = e.target.getAttribute('data-host-sw-mode');
      const sw = hostState.switches.find((x) => x.id === id);
      if (sw) {
        sw.mode = e.target.value;
        hostState.notice = { severity: 'success', text: '已应用' };
        renderHostPage();
      }
    }
  });

  page.addEventListener('input', (e) => {
    if (e.target.id === 'hostMacMinInput' || e.target.id === 'hostMacMaxInput') {
      if (e.target.id === 'hostMacMinInput') hostState.draft.macMin = e.target.value;
      else hostState.draft.macMax = e.target.value;
      const dirty = isGroupDirty(['macMin', 'macMax']);
      const isDefaultMac =
        hostState.draft.macMin === '00-15-5D-01-00-00' &&
        hostState.draft.macMax === '00-15-5D-01-FF-FF' &&
        hostState.saved.macMin === '00-15-5D-01-00-00' &&
        hostState.saved.macMax === '00-15-5D-01-FF-FF';
      const saveMacBtn = page.querySelector('#hostSaveMacBtn');
      const resetMacBtn = page.querySelector('#hostResetMacBtn');
      if (saveMacBtn) saveMacBtn.disabled = !dirty;
      if (resetMacBtn) resetMacBtn.disabled = isDefaultMac;
    }
  });

  renderHostPage();
}
