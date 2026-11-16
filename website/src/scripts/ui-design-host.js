// HostPage Prototype: Task Manager-style State tab (1:1 replica of VmDashboardView Overview)
// + clean Core Host capability tabs (交换机 / 设备 / 计算 / 设置) with NO top summary clutter.

const HOST_SYS = {
  os: 'Windows 11 专业工作站版 (26100)',
  arch: 'X64',
  cpu: 'AMD Ryzen 9 9950X 16-Core Processor',
  cpuShort: 'AMD Ryzen 9 9950X',
  cores: 16,
  threads: 32,
  baseClock: '4.30 GHz',
  curClock: '4.18 GHz',
  numaNodes: 1,
  memoryGb: 64,
  usedMemoryGb: 22.4
};

const POWER_PLANS = [
  { id: 'balanced', name: '平衡' },
  { id: 'high', name: '高性能' },
  { id: 'ultimate', name: '卓越性能' },
  { id: 'saver', name: '节能' }
];

const SCHEDULER_OPTIONS = [
  { value: 'Classic', label: 'Classic' },
  { value: 'Core', label: 'Core' },
  { value: 'Root', label: 'Root' }
];

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

const INITIAL_SWITCHES = [
  {
    id: 'sw-ext',
    name: 'External vSwitch',
    mode: 'Bridge',
    allowManagementOs: true,
    upstream: 'Intel(R) Ethernet Controller I226-V',
    ipv4: '192.168.1.100',
    sendRate: '1.2 Mbps',
    recvRate: '4.8 Mbps',
    sendPts: [12, 18, 15, 26, 20, 24, 18],
    recvPts: [22, 34, 28, 46, 38, 42, 36],
    endpoints: ['ubuntu-dev'],
    expanded: true
  },
  {
    id: 'sw-default',
    name: 'Default Switch',
    mode: 'NAT',
    allowManagementOs: true,
    upstream: '内部 NAT (ICS)',
    ipv4: '172.24.112.1',
    sendRate: '0.4 Mbps',
    recvRate: '1.6 Mbps',
    sendPts: [4, 8, 6, 12, 9, 10, 8],
    recvPts: [10, 16, 14, 22, 18, 20, 16],
    endpoints: ['test', 'win11-arm64', 'ubuntu-dev'],
    expanded: false
  },
  {
    id: 'sw-iso',
    name: 'Lab Isolated',
    mode: 'Isolated',
    allowManagementOs: false,
    upstream: '无物理上行网卡',
    ipv4: '---',
    sendRate: '0 Kbps',
    recvRate: '0 Kbps',
    sendPts: [0, 0, 0, 0, 0, 0, 0],
    recvPts: [0, 0, 0, 0, 0, 0, 0],
    endpoints: [],
    expanded: false
  }
];

const INITIAL_GPUS = [
  {
    id: 'gpu-0',
    name: 'NVIDIA GeForce RTX 4090',
    manu: 'NVIDIA',
    vendor: 'ASUS (华硕)',
    vram: '24564 MB',
    driver: '32.0.15.6636',
    instanceId: 'PCI\\VEN_10DE&DEV_2684&SUBSYS_889C1043&REV_A1\\4&2C8B14A&0&0008',
    partitionable: true,
    assignedVms: ['test', 'ubuntu-dev'],
    pct: 14,
    pts3d: [10, 14, 19, 15, 18, 14],
    ptsCopy: [2, 4, 5, 3, 4, 4],
    ptsEnc: [0, 0, 0, 0, 0, 0],
    ptsDec: [4, 6, 9, 5, 7, 6]
  },
  {
    id: 'gpu-1',
    name: 'AMD Radeon(TM) Graphics',
    manu: 'AMD',
    vendor: 'AMD',
    vram: '512 MB',
    driver: '31.0.24002.92',
    instanceId: 'PCI\\VEN_1002&DEV_164E&SUBSYS_88771043&REV_C1\\4&3E19B2&0&0041',
    partitionable: false,
    assignedVms: [],
    pct: 1,
    pts3d: [1, 2, 1, 2, 1, 1],
    ptsCopy: [0, 1, 0, 0, 1, 0],
    ptsEnc: [0, 0, 0, 0, 0, 0],
    ptsDec: [0, 0, 1, 0, 0, 0]
  }
];

const INITIAL_DISKS = [
  {
    number: 0,
    name: 'WD_BLACK SN850X 2000GB',
    sizeText: '1863.02 GB',
    serial: 'WDS200T2X0E-00BCA0',
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
    status: '脱机 · 可直通',
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
    name: '性能组 (P-Core 0-15)',
    processors: '0 - 15',
    members: ['test', 'win11-arm64'],
    cpuCap: 100,
    priority: 5,
    idleReserve: 2,
    weight: 1000,
    expanded: true
  },
  {
    id: '8c12a109-1b2c-4d3e-9a01-99100234ab12',
    name: '后台能效组 (E-Core 16-31)',
    processors: '16 - 31',
    members: ['ubuntu-dev'],
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

const HOST_TABS = [
  { key: 'state', label: '状态', icon: '&#xE9D2;' },
  { key: 'switches', label: '交换机', icon: '&#xE774;' },
  { key: 'devices', label: '设备', icon: '&#xE950;' },
  { key: 'compute', label: '计算', icon: '&#xEA86;' },
  { key: 'settings', label: '设置', icon: '&#xE713;' }
];

const hostState = {
  activeTab: 'state',
  selectedRail: { kind: 'cpu', idx: 0 }, // 'cpu' | 'mem' | 'disk' | 'net' | 'gpu'

  switchesSub: 'list',
  devicesSub: 'gpu',
  computeSub: 'scheduler',
  settingsSub: 'environment',

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

function renderStateTab() {
  const accentMode = isAccentMode();
  const strokeOf = (fallback) => (accentMode ? 'var(--VmAccentBrush)' : fallback);
  const fillOf = (fallback) => (accentMode ? 'var(--VmChartFillBrush)' : fallback);

  const memPct = Math.round((HOST_SYS.usedMemoryGb / HOST_SYS.memoryGb) * 100);
  const rows = [
    {
      kind: 'cpu',
      idx: 0,
      title: '处理器',
      sub: HOST_SYS.cpuShort,
      stat: `18% ${HOST_SYS.cores} 核心`,
      active: true,
      pts: [15, 20, 17, 24, 19, 21, 18],
      color: '#3898EC',
      fill: 'rgba(56, 152, 236, 0.22)',
      tip: HOST_SYS.cpu
    },
    {
      kind: 'mem',
      idx: 0,
      title: '内存',
      sub: '',
      stat: `${memPct}% ${HOST_SYS.usedMemoryGb.toFixed(1)}/${HOST_SYS.memoryGb.toFixed(1)} GB`,
      active: true,
      pts: [32, 33, 34, 35, 34, 35, memPct],
      color: '#9D5CE6',
      fill: 'rgba(157, 92, 230, 0.22)',
      tip: `物理内存 ${HOST_SYS.memoryGb} GB`
    }
  ];

  hostState.disks.forEach((d, idx) => {
    rows.push({
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

  hostState.switches.slice(0, 2).forEach((sw, idx) => {
    const active = sw.mode !== 'Isolated';
    rows.push({
      kind: 'net',
      idx,
      title: idx === 0 ? '网卡' : '交换机',
      sub: sw.name,
      stat: active ? `发 ${sw.sendRate} 收 ${sw.recvRate}` : '未连接',
      active,
      pts: sw.recvPts,
      color: '#46B3E6',
      fill: 'rgba(70, 179, 230, 0.22)',
      tip: `${sw.name} (${sw.upstream})`
    });
  });

  hostState.gpus.forEach((g, idx) => {
    rows.push({
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

  const isSel = (kind, idx) => hostState.selectedRail.kind === kind && hostState.selectedRail.idx === idx;
  const railHtml = rows
    .map(
      (r) => `
      <button type="button" class="tmw-item ${accentMode ? 'accent-mode' : ''} ${isSel(r.kind, r.idx) ? 'selected' : ''}" data-host-rail="${r.kind}:${r.idx}" title="${esc(r.tip)}">
        <div class="tmw-thumb">${renderSparkThumbSvg(r.pts, r.active, strokeOf(r.color), fillOf(r.fill))}</div>
        <div class="tmw-info">
          <div class="tmw-title-row"><span class="tmw-title">${esc(r.title)}</span></div>
          ${r.sub ? `<div class="tmw-sub tc-mid-fit">${esc(r.sub)}</div>` : ''}
          <div class="tmw-stat">${esc(r.stat)}</div>
        </div>
      </button>
    `
    )
    .join('');

  const sel = hostState.selectedRail;
  let icon = '&#xF58E;';
  let rotate = 90;
  let title = HOST_SYS.cpu;
  let reading = '18%';
  let jumpTab = 'compute';
  let jumpSub = 'scheduler';
  let jumpLabel = '配置调度与处理器';
  let bodyHtml = '';

  if (sel.kind === 'cpu') {
    const coreCharts = Array.from({ length: 16 }, (_, i) => {
      const base = 10 + ((i * 7) % 28);
      const pts = [base - 4, base + 5, base - 2, base + 8, base + 2, base + 4, base];
      return createUsageChartHtml(String(i), true, `${base}%`, pts, false, 84);
    }).join('');
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
  } else if (sel.kind === 'mem') {
    icon = '&#xEEA0;';
    rotate = 0;
    title = '内存';
    reading = `${HOST_SYS.usedMemoryGb.toFixed(1)} / ${HOST_SYS.memoryGb.toFixed(1)} GB`;
    jumpTab = 'compute';
    jumpSub = 'scheduler';
    jumpLabel = '配置 NUMA 与电源';
    const kvItems = [
      renderTitleFirstKvItemHtml({ title: '使用中', stateText: `${HOST_SYS.usedMemoryGb.toFixed(1)} GB` }),
      renderTitleFirstKvItemHtml({ title: '可用', stateText: `${(HOST_SYS.memoryGb - HOST_SYS.usedMemoryGb).toFixed(1)} GB` }),
      renderTitleFirstKvItemHtml({ title: '物理总容量', stateText: `${HOST_SYS.memoryGb.toFixed(1)} GB` }),
      renderTitleFirstKvItemHtml({ title: 'NUMA 跨越', stateText: hostState.saved.numaSpanning ? '开启' : '关闭' }),
      renderTitleFirstKvItemHtml({ title: '电源方案', stateText: POWER_PLANS.find((p) => p.id === hostState.saved.powerPlanId)?.name || '高性能' })
    ];
    bodyHtml = `
      ${createUsageChartHtml('内存使用趋势', true, `${memPct}%`, [32, 33, 34, 35, 34, 35, memPct], false, 136)}
      <div class="dt-kv-grid is-title-first is-2col">${kvItems.join('')}</div>
    `;
  } else if (sel.kind === 'disk') {
    const d = hostState.disks[sel.idx] || hostState.disks[0];
    icon = '&#xEDA2;';
    rotate = 0;
    title = d.name;
    reading = d.offline ? '已脱机' : d.sizeText;
    jumpTab = 'devices';
    jumpSub = 'disks';
    jumpLabel = '管理物理磁盘';
    const kvItems = [
      renderTitleFirstKvItemHtml({ title: '状态', stateText: d.status, caution: d.offline }),
      renderTitleFirstKvItemHtml({ title: '容量', stateText: d.sizeText }),
      renderTitleFirstKvItemHtml({ title: '直通资格', stateText: d.canPassthrough ? '支持直通' : '系统盘排除' }),
      renderTitleFirstKvItemHtml({ title: '序列号', stateText: d.serial, copyVal: d.serial }),
      renderTitleFirstKvItemHtml({ title: '设备路径', stateText: d.path, copyVal: d.path, wide: true })
    ];
    bodyHtml = `
      <div class="transfer-charts-grid">
        ${createUsageChartHtml('读取', !d.offline, d.readRate, d.readPts, false, 136)}
        ${createUsageChartHtml('写入', !d.offline, d.writeRate, d.writePts, false, 136)}
      </div>
      <div class="dt-kv-grid is-title-first is-2col">${kvItems.join('')}</div>
    `;
  } else if (sel.kind === 'net') {
    const sw = hostState.switches[sel.idx] || hostState.switches[0];
    icon = '&#xE774;';
    rotate = 0;
    title = sw.name;
    reading = sw.mode;
    jumpTab = 'switches';
    jumpSub = 'list';
    jumpLabel = '管理虚拟交换机';
    const kvItems = [
      renderTitleFirstKvItemHtml({ title: '模式', stateText: sw.mode }),
      renderTitleFirstKvItemHtml({ title: '宿主共享', stateText: sw.allowManagementOs ? '允许' : '禁止' }),
      renderTitleFirstKvItemHtml({ title: 'IPv4 地址', stateText: sw.ipv4, copyVal: sw.ipv4 !== '---' ? sw.ipv4 : '' }),
      renderTitleFirstKvItemHtml({ title: '接入虚拟机', stateText: sw.endpoints.length ? sw.endpoints.join(', ') : '无' }),
      renderTitleFirstKvItemHtml({ title: '上行适配器', stateText: sw.upstream, copyVal: sw.upstream, wide: true })
    ];
    bodyHtml = `
      <div class="transfer-charts-grid">
        ${createUsageChartHtml('发送', sw.mode !== 'Isolated', sw.sendRate, sw.sendPts, false, 136)}
        ${createUsageChartHtml('接收', sw.mode !== 'Isolated', sw.recvRate, sw.recvPts, false, 136)}
      </div>
      <div class="dt-kv-grid is-title-first is-2col">${kvItems.join('')}</div>
    `;
  } else {
    const g = hostState.gpus[sel.idx] || hostState.gpus[0];
    icon = '&#xF211;';
    rotate = 0;
    title = g.name;
    reading = `${g.pct}%`;
    jumpTab = 'devices';
    jumpSub = 'gpu';
    jumpLabel = '查看显卡与分区';
    const kvItems = [
      renderTitleFirstKvItemHtml({ title: '芯片厂商', stateText: g.manu, copyVal: g.manu }),
      renderTitleFirstKvItemHtml({ title: '板卡厂商', stateText: g.vendor, copyVal: g.vendor }),
      renderTitleFirstKvItemHtml({ title: '专用显存', stateText: g.vram, copyVal: g.vram }),
      renderTitleFirstKvItemHtml({ title: '驱动版本', stateText: g.driver, copyVal: g.driver }),
      renderTitleFirstKvItemHtml({ title: 'GPU-PV 分区', stateText: g.partitionable ? `支持 (${g.assignedVms.length} 台 VM)` : '不支持' }),
      renderTitleFirstKvItemHtml({ title: 'IOMMU / DDA', stateText: hostState.isWindowsServer && hostState.iommuOk ? '已就绪' : '需 Windows Server', caution: !hostState.isWindowsServer }),
      renderTitleFirstKvItemHtml({ title: '设备实例路径', stateText: g.instanceId, copyVal: g.instanceId, wide: true })
    ];
    bodyHtml = `
      <div class="gpu-charts-grid">
        ${createUsageChartHtml('3D', true, `${g.pct}%`, g.pts3d, true, 82)}
        ${createUsageChartHtml('数据复制', true, `${g.ptsCopy.at(-1)}%`, g.ptsCopy, true, 82)}
        ${createUsageChartHtml('视频编码', true, `${g.ptsEnc.at(-1)}%`, g.ptsEnc, true, 82)}
        ${createUsageChartHtml('视频解码', true, `${g.ptsDec.at(-1)}%`, g.ptsDec, true, 82)}
      </div>
      <div class="dt-kv-grid is-title-first is-2col">${kvItems.join('')}</div>
    `;
  }

  return `
    <div class="vm-overview-scroll">
      <div class="vm-overview-alt-host mode-fill">
        <div class="tc-shell tm-wide">
          <div class="tc-left-rail">${railHtml}</div>
          <div class="tc-right-card">
            <div class="tc-canvas-head">
              <div class="tc-canvas-identity is-single-line">
                <div class="tc-head-icon-box">
                  <span class="fi" style="${rotate ? `transform:rotate(${rotate}deg);` : ''}">${icon}</span>
                </div>
                <div class="tc-canvas-title-wrap">
                  <div class="tc-canvas-title-row">
                    <span class="tc-canvas-title">${esc(title)}</span>
                  </div>
                </div>
              </div>
              <div class="tc-canvas-head-right">
                <div class="tc-canvas-reading">${esc(reading)}</div>
                <button type="button" class="tc-cfg-icon-btn" data-host-jump="${jumpTab}:${jumpSub}" title="${esc(jumpLabel)}">
                  <span class="fi">&#xE713;</span>
                </button>
              </div>
            </div>
            <div class="tc-canvas-body">${bodyHtml}</div>
          </div>
        </div>
      </div>
    </div>
  `;
}

function renderSwitchesTab() {
  const subnav = [
    { key: 'list', label: '虚拟交换机', icon: '&#xE774;' },
    { key: 'mac', label: 'MAC 地址池', icon: '&#xE839;' }
  ];
  const subHtml = subnav
    .map(
      (s) => `
      <button type="button" class="subnav-btn ${hostState.switchesSub === s.key ? 'selected' : ''}" data-host-sub="switches:${s.key}">
        <span class="fi subnav-icon">${s.icon}</span><span>${esc(s.label)}</span>
      </button>
    `
    )
    .join('');

  let bodyHtml = '';
  if (hostState.switchesSub === 'list') {
    const cards = hostState.switches
      .map(
        (sw) => `
        <div class="winui-expander ${sw.expanded ? 'expanded' : ''}">
          <div class="winui-expander-header" data-host-expand="switch:${sw.id}">
            <div class="host-exp-head">
              <span class="fi" style="font-size:18px;">&#xE774;</span>
              <span class="host-exp-title">${esc(sw.name)}</span>
              <span class="vm-badge"><span class="vm-badge-text">${esc(sw.mode)}</span></span>
              <span class="host-exp-muted">${sw.endpoints.length} 台虚拟机接入</span>
            </div>
            <span class="fi winui-expander-chevron">${sw.expanded ? '&#xE70E;' : '&#xE70D;'}</span>
          </div>
          <div class="winui-expander-body">
            <div class="settings-field-panel">
              <div class="field-editor-stack">
                ${labelHtml('连接模式', 'Bridge：桥接物理网卡；NAT：内部共享上网；Isolated：完全隔离内网。')}
                <select class="winui-select" data-host-sw-mode="${sw.id}">
                  <option value="Bridge" ${sw.mode === 'Bridge' ? 'selected' : ''}>外部桥接 (Bridge)</option>
                  <option value="NAT" ${sw.mode === 'NAT' ? 'selected' : ''}>内部 NAT</option>
                  <option value="Isolated" ${sw.mode === 'Isolated' ? 'selected' : ''}>仅主机隔离 (Isolated)</option>
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

    bodyHtml = `
      <div class="host-toolbar">
        <button type="button" class="winui-btn primary" id="hostAddSwitchBtn">新建交换机</button>
        <button type="button" class="winui-btn" id="hostRefreshSwitchesBtn">刷新</button>
      </div>
      <div class="host-exp-list">${cards}</div>
    `;
  } else {
    bodyHtml = `
      <div class="winui-field-card">
        <div class="settings-section-title">全局动态 MAC 地址池</div>
        <div class="settings-field-panel">
          <div class="field-editor-stack host-span-2">
            ${labelHtml('MAC 地址范围', '上下限通过一次 WMI ModifyServiceSettings 原子提交，格式 XX-XX-XX-XX-XX-XX。')}
            <div class="host-mac-row">
              <input type="text" class="winui-textbox host-mac-input" id="hostMacMinInput" maxlength="17" value="${esc(hostState.draft.macMin)}" />
              <span class="host-mac-arrow">→</span>
              <input type="text" class="winui-textbox host-mac-input" id="hostMacMaxInput" maxlength="17" value="${esc(hostState.draft.macMax)}" />
            </div>
          </div>
        </div>
        ${actionRowHtml('mac', ['macMin', 'macMax'])}
      </div>
    `;
  }

  return `
    <div class="vm-settings-area">
      <div class="settings-subnav">${subHtml}</div>
      <div class="vm-settings-right">
        <div class="settings-host-scroll">${bodyHtml}</div>
      </div>
    </div>
  `;
}

function renderDevicesTab() {
  const subnav = [
    { key: 'gpu', label: '显卡 (GPU-PV)', icon: '&#xF211;' },
    { key: 'pcie', label: 'PCIe 设备 (DDA)', icon: '&#xF0B9;' },
    { key: 'disks', label: '物理磁盘与镜像', icon: '&#xEDA2;' }
  ];
  const subHtml = subnav
    .map(
      (s) => `
      <button type="button" class="subnav-btn ${hostState.devicesSub === s.key ? 'selected' : ''}" data-host-sub="devices:${s.key}">
        <span class="fi subnav-icon">${s.icon}</span><span>${esc(s.label)}</span>
      </button>
    `
    )
    .join('');

  let bodyHtml = '';
  if (hostState.devicesSub === 'gpu') {
    bodyHtml = `
      <div class="host-exp-list">
        ${hostState.gpus
          .map(
            (g) => `
          <div class="winui-expander expanded">
            <div class="winui-expander-header">
              <div class="host-exp-head">
                <span class="fi" style="font-size:18px;">&#xF211;</span>
                <span class="host-exp-title">${esc(g.name)}</span>
                <span class="vm-badge"><span class="vm-badge-text">${g.partitionable ? '支持 GPU-PV' : '不支持分区'}</span></span>
              </div>
            </div>
            <div class="winui-expander-body">
              <div class="settings-field-panel">
                <div class="field-editor-stack"><span class="setting-label-title">显存容量</span><span>${esc(g.vram)}</span></div>
                <div class="field-editor-stack"><span class="setting-label-title">驱动版本</span><span>${esc(g.driver)}</span></div>
                <div class="field-editor-stack host-span-2">
                  <span class="setting-label-title">已分配虚拟机</span>
                  <div class="host-inline-tags">
                    ${g.assignedVms.length ? g.assignedVms.map((v) => `<span class="vm-badge"><span class="vm-badge-text">${esc(v)}</span></span>`).join('') : '<span class="host-exp-muted">尚未分配分区</span>'}
                  </div>
                </div>
              </div>
            </div>
          </div>
        `
          )
          .join('')}
      </div>
    `;
  } else if (hostState.devicesSub === 'pcie') {
    const warnBanner = !hostState.isWindowsServer
      ? `<div class="winui-infobar is-warning" style="margin:0 0 12px 0;"><div class="winui-infobar-main"><span class="fi winui-infobar-icon">&#xE7BA;</span><span>当前宿主为消费级 Windows，原生 DDA 直通需要 Windows Server。</span></div></div>`
      : '';
    bodyHtml = `
      ${warnBanner}
      <div class="host-exp-list">
        ${hostState.pcieDevices
          .map(
            (d) => `
          <div class="winui-expander ${d.expanded ? 'expanded' : ''}">
            <div class="winui-expander-header" data-host-expand="pcie:${d.id}">
              <div class="host-exp-head">
                <span class="fi" style="font-size:18px;">&#xF0B9;</span>
                <span class="host-exp-title">${esc(d.name)}</span>
                <span class="vm-badge"><span class="vm-badge-text">${esc(d.ownerName)}</span></span>
              </div>
              <span class="fi winui-expander-chevron">${d.expanded ? '&#xE70E;' : '&#xE70D;'}</span>
            </div>
            <div class="winui-expander-body">
              <div class="settings-field-panel">
                <div class="field-editor-stack"><span class="setting-label-title">设备类别</span><span>${esc(d.classType)} · ${esc(d.vendor)}</span></div>
                <div class="field-editor-stack"><span class="setting-label-title">位置路径</span><span class="host-mono">${esc(d.path)}</span></div>
                <div class="field-editor-stack host-span-2"><span class="setting-label-title">InstanceId</span><span class="host-mono">${esc(d.instanceId)}</span></div>
              </div>
              <div class="host-actions-row">
                <button type="button" class="winui-btn" data-host-pcie-toggle="${d.id}">
                  ${d.ownerKind === 'host' ? '从宿主卸下 (准备直通)' : '归还给宿主'}
                </button>
              </div>
            </div>
          </div>
        `
          )
          .join('')}
      </div>
    `;
  } else {
    bodyHtml = `
      <div class="host-toolbar">
        <button type="button" class="winui-btn primary" id="hostMountVhdxBtn">挂载 VHDX / ISO</button>
        <button type="button" class="winui-btn" id="hostRefreshDisksBtn">刷新磁盘</button>
      </div>
      <div class="host-exp-list">
        ${hostState.disks
          .map(
            (disk) => `
          <div class="winui-expander expanded">
            <div class="winui-expander-header">
              <div class="host-exp-head">
                <span class="fi" style="font-size:18px;">&#xEDA2;</span>
                <span class="host-exp-title">磁盘 ${disk.number} · ${esc(disk.name)}</span>
                <span class="vm-badge"><span class="vm-badge-text">${esc(disk.sizeText)}</span></span>
                <span class="vm-badge"><span class="vm-badge-text">${esc(disk.status)}</span></span>
              </div>
            </div>
            <div class="winui-expander-body">
              <div class="settings-field-panel">
                <div class="field-editor-toggle-row">
                  ${labelHtml('脱机状态', '磁盘直通 (Pass-through) 给虚拟机前通常需先设为脱机。')}
                  <div class="winui-switch ${disk.offline ? 'on' : ''} ${disk.number === 0 ? 'disabled' : ''}" data-host-disk-offline="${disk.number}">
                    <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                    <span class="switch-state-label">${disk.offline ? '脱机' : '联机'}</span>
                  </div>
                </div>
                <div class="field-editor-toggle-row">
                  ${labelHtml('只读保护', '防止宿主误写入直通盘或只读挂载的虚拟磁盘。')}
                  <div class="winui-switch ${disk.readOnly ? 'on' : ''} ${disk.number === 0 ? 'disabled' : ''}" data-host-disk-ro="${disk.number}">
                    <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                    <span class="switch-state-label">${disk.readOnly ? '只读' : '读写'}</span>
                  </div>
                </div>
              </div>
            </div>
          </div>
        `
          )
          .join('')}
      </div>
    `;
  }

  return `
    <div class="vm-settings-area">
      <div class="settings-subnav">${subHtml}</div>
      <div class="vm-settings-right">
        <div class="settings-host-scroll">${bodyHtml}</div>
      </div>
    </div>
  `;
}

function renderComputeTab() {
  const subnav = [
    { key: 'scheduler', label: '电源与调度', icon: '&#xEA86;' },
    { key: 'cpugroups', label: 'CPU 资源组', icon: '&#xF58E;' },
    { key: 'vmgroups', label: '虚拟机分组', icon: '&#xE8F1;' }
  ];
  const subHtml = subnav
    .map(
      (s) => `
      <button type="button" class="subnav-btn ${hostState.computeSub === s.key ? 'selected' : ''}" data-host-sub="compute:${s.key}">
        <span class="fi subnav-icon">${s.icon}</span><span>${esc(s.label)}</span>
      </button>
    `
    )
    .join('');

  let bodyHtml = '';
  if (hostState.computeSub === 'scheduler') {
    const powerOpts = POWER_PLANS.map(
      (p) => `<option value="${p.id}" ${p.id === hostState.draft.powerPlanId ? 'selected' : ''}>${esc(p.name)}</option>`
    ).join('');
    const schedBtns = SCHEDULER_OPTIONS.map(
      (o) => `<button type="button" class="host-seg-btn ${hostState.draft.scheduler === o.value ? 'active' : ''}" data-host-sched="${o.value}">${esc(o.label)}</button>`
    ).join('');

    bodyHtml = `
      <div class="winui-field-card">
        <div class="settings-section-title">电源与核心调度</div>
        <div class="settings-field-panel">
          <div class="field-editor-stack">
            ${labelHtml('宿主电源方案', '枚举现有 Windows 电源计划并切换。')}
            <select class="winui-select" id="hostPowerPlanSelect">${powerOpts}</select>
          </div>
          <div class="field-editor-stack">
            ${labelHtml('Hyper-V 核心调度器', 'Classic / Core / Root 引导调度模式，更改后需重启宿主生效。')}
            <div class="host-segmented">${schedBtns}</div>
          </div>
          ${toggleFieldHtml('numaSpanning', 'NUMA 跨越', '允许虚拟机跨越多个物理 NUMA 节点分配内存。', { span2: true })}
        </div>
        ${actionRowHtml('compute', ['powerPlanId', 'scheduler', 'numaSpanning'])}
      </div>
    `;
  } else if (hostState.computeSub === 'cpugroups') {
    bodyHtml = `
      <div class="host-exp-list">
        ${hostState.cpuGroups
          .map(
            (g) => `
          <div class="winui-expander ${g.expanded ? 'expanded' : ''}">
            <div class="winui-expander-header" data-host-expand="cpugroup:${g.id}">
              <div class="host-exp-head">
                <span class="fi" style="font-size:18px;">&#xF58E;</span>
                <span class="host-exp-title">${esc(g.name)}</span>
                <span class="vm-badge"><span class="vm-badge-text">核心 ${esc(g.processors)}</span></span>
                <span class="host-exp-muted">${g.members.length} 台虚拟机</span>
              </div>
              <span class="fi winui-expander-chevron">${g.expanded ? '&#xE70E;' : '&#xE70D;'}</span>
            </div>
            <div class="winui-expander-body">
              <div class="settings-field-panel">
                <div class="field-editor-stack">
                  ${labelHtml('组总算力配额 (%)', 'HostCpuGroupService.SetCpuCapAsync (0–65536 映射为百分比)。')}
                  <input type="number" class="winui-textbox" min="1" max="100" value="${g.cpuCap}" />
                </div>
                <div class="field-editor-stack">
                  ${labelHtml('调度优先级 (0–7)', '数值越大优先级越高。')}
                  <input type="number" class="winui-textbox" min="0" max="7" value="${g.priority}" />
                </div>
                <div class="field-editor-stack">
                  ${labelHtml('空闲保留核心数', '组空闲时仍为本组保留、不出借的逻辑处理器数。')}
                  <input type="number" class="winui-textbox" min="0" max="32" value="${g.idleReserve}" />
                </div>
                <div class="field-editor-stack">
                  ${labelHtml('相对权重', 'HCS 组竞争时间片的权重 (1–65535)。')}
                  <input type="number" class="winui-textbox" min="1" max="65535" value="${g.weight}" />
                </div>
                <div class="field-editor-stack host-span-2">
                  <span class="setting-label-title">成员虚拟机</span>
                  <div class="host-inline-tags">
                    ${g.members.map((m) => `<span class="vm-badge"><span class="vm-badge-text">${esc(m)}</span></span>`).join('')}
                  </div>
                </div>
              </div>
            </div>
          </div>
        `
          )
          .join('')}
      </div>
    `;
  } else {
    bodyHtml = `
      <div class="host-toolbar">
        <button type="button" class="winui-btn primary" id="hostAddVmGroupBtn">新建分组</button>
      </div>
      <div class="host-exp-list">
        ${hostState.vmGroups
          .map(
            (vg) => `
          <div class="winui-expander expanded">
            <div class="winui-expander-header">
              <div class="host-exp-head">
                <span class="fi" style="font-size:18px;">&#xE8F1;</span>
                <span class="host-exp-title">${esc(vg.name)}</span>
                <span class="vm-badge"><span class="vm-badge-text">${vg.kind === 'Groups' ? '管理分组' : '虚拟机集合'}</span></span>
              </div>
            </div>
            <div class="winui-expander-body">
              <div class="host-inline-tags">
                ${(vg.kind === 'Groups' ? vg.children : vg.vms).map((item) => `<span class="vm-badge"><span class="vm-badge-text">${esc(item)}</span></span>`).join('')}
              </div>
            </div>
          </div>
        `
          )
          .join('')}
      </div>
    `;
  }

  return `
    <div class="vm-settings-area">
      <div class="settings-subnav">${subHtml}</div>
      <div class="vm-settings-right">
        <div class="settings-host-scroll">${bodyHtml}</div>
      </div>
    </div>
  `;
}

function renderSettingsTab() {
  const subnav = [
    { key: 'environment', label: 'Hyper-V 平台', icon: '&#xE9D9;' },
    { key: 'paths', label: '路径与会话', icon: '&#xE8B7;' },
    { key: 'experimental', label: '高级策略', icon: '&#xEC7A;' },
    { key: 'preferences', label: '客户端偏好', icon: '&#xE771;' }
  ];

  const subHtml = subnav
    .map(
      (s) => `
      <button type="button" class="subnav-btn ${hostState.settingsSub === s.key ? 'selected' : ''}" data-host-sub="settings:${s.key}">
        <span class="fi subnav-icon">${s.icon}</span><span>${esc(s.label)}</span>
      </button>
    `
    )
    .join('');

  let bodyHtml = '';
  if (hostState.settingsSub === 'environment') {
    bodyHtml = `
      <div class="winui-field-card">
        <div class="settings-section-title">Hyper-V 服务与环境诊断</div>
        <div class="settings-field-panel">
          <div class="field-editor-toggle-row host-span-2">
            ${labelHtml('Hyper-V 平台与管理服务', '控制 Hypervisor 引导与 VMMS 管理服务，变更需重启宿主。')}
            <div class="host-inline-tags">
              <span class="vm-badge"><span class="vm-badge-text">${hostState.hyperVEnabled ? '运行中' : '未启用'}</span></span>
              <button type="button" class="winui-btn ${hostState.hyperVEnabled ? '' : 'primary'}" id="hostToggleHyperVBtn">
                ${hostState.hyperVEnabled ? '禁用' : '启用'}
              </button>
            </div>
          </div>
          <div class="field-editor-stack">
            ${labelHtml('CPU 硬件虚拟化', 'Intel VT-x / AMD-V / ARM64 虚拟化扩展。')}
            <span>${hostState.cpuVirtOk ? '已支持并启用' : '未启用'}</span>
          </div>
          <div class="field-editor-stack">
            ${labelHtml('GPU-PV 系统要求', '需要 Windows 10 2004 (Build 19041) 及以上。')}
            <span>${hostState.gpuPvOk ? '符合要求 (Build 26100)' : '版本过低'}</span>
          </div>
          <div class="field-editor-stack">
            ${labelHtml(HOST_SYS.arch === 'ARM64' ? 'SMMU 硬件隔离' : 'IOMMU (VT-d / AMD-Vi)', 'DDA 直通所需硬件隔离支持。')}
            <span>${hostState.iommuOk ? '已开启' : '未检测到'}</span>
          </div>
          <div class="field-editor-stack">
            ${labelHtml('Windows Server 系统', '原生 DDA 直通需要 Server 版本。')}
            <span>${hostState.isWindowsServer ? '是' : '否 (客户端系统)'}</span>
          </div>
        </div>
      </div>
    `;
  } else if (hostState.settingsSub === 'paths') {
    bodyHtml = `
      <div class="winui-field-card">
        <div class="settings-section-title">默认目录与宿主会话</div>
        <div class="settings-field-panel">
          <div class="field-editor-stack host-span-2">
            ${labelHtml('新建虚拟机默认位置', '新建虚拟机配置文件 (.vmcx) 的默认存放目录。')}
            <div class="host-path-row">
              <input type="text" class="winui-textbox" value="${esc(hostState.draft.defaultVmPath)}" readonly />
              <button type="button" class="winui-btn" data-host-pick="vm">选择</button>
            </div>
          </div>
          <div class="field-editor-stack host-span-2">
            ${labelHtml('新建虚拟硬盘默认位置', '新建虚拟磁盘文件 (.vhdx) 的默认存放目录。')}
            <div class="host-path-row">
              <input type="text" class="winui-textbox" value="${esc(hostState.draft.defaultVhdPath)}" readonly />
              <button type="button" class="winui-btn" data-host-pick="vhd">选择</button>
            </div>
          </div>
          ${toggleFieldHtml('enhancedSession', '增强会话模式', '主机侧许可：允许虚拟机控制台通过增强会话连接。')}
          ${toggleFieldHtml('volumeAutoMount', '卷自动挂载盘符', '控制 Mount Manager 是否为新挂载磁盘卷自动分配盘符。')}
        </div>
        ${actionRowHtml('paths', ['defaultVmPath', 'defaultVhdPath', 'enhancedSession', 'volumeAutoMount'])}
      </div>
    `;
  } else if (hostState.settingsSub === 'experimental') {
    bodyHtml = `
      <div class="winui-field-card">
        <div class="settings-section-title">系统策略与实验特性</div>
        <div class="settings-field-panel">
          ${toggleFieldHtml('pciePolicyUnlocked', '解锁 PCIe / GPU 策略', '放宽 Hyper-V 主机对 GPU 分区与 PCIe 设备分配的策略限制。')}
          ${toggleFieldHtml('serverProductTypeSpoof', '切换 Server 系统标识', '将 ProductType 调整为 ServerNT，需重启宿主生效。')}
          ${toggleFieldHtml('nativeNvme', '主机原生 NVMe', 'Windows 11 24H2+ / Server 2025 原生 NVMe 存储栈，需重启宿主。')}
          ${toggleFieldHtml('openHclFirmware', 'OpenHCL / IGVM 固件', '允许虚拟机加载自定义 OpenHCL / IGVM 开发固件。')}
        </div>
        ${actionRowHtml('experimental', ['pciePolicyUnlocked', 'serverProductTypeSpoof', 'nativeNvme', 'openHclFirmware'])}
      </div>
    `;
  } else {
    bodyHtml = `
      <div class="winui-field-card">
        <div class="settings-section-title">客户端偏好</div>
        <div class="settings-field-panel">
          ${toggleFieldHtml('autoOpenConsole', '启动后自动打开控制台', '单台虚拟机从关机状态启动成功后，自动打开连接窗口。')}
          <div class="field-editor-stack">
            ${labelHtml('默认电源按钮动作', '点击运行中虚拟机主电源按钮时的默认动作。')}
            <select class="winui-select" id="hostPowerActionSelect">
              <option value="Shutdown" ${hostState.draft.defaultPowerAction === 'Shutdown' ? 'selected' : ''}>关机</option>
              <option value="Save" ${hostState.draft.defaultPowerAction === 'Save' ? 'selected' : ''}>保存状态</option>
            </select>
          </div>
        </div>
        ${actionRowHtml('preferences', ['autoOpenConsole', 'defaultPowerAction'])}
      </div>
    `;
  }

  return `
    <div class="vm-settings-area">
      <div class="settings-subnav">${subHtml}</div>
      <div class="vm-settings-right">
        <div class="settings-host-scroll">${bodyHtml}</div>
      </div>
    </div>
  `;
}

function renderBodyByActiveTab() {
  switch (hostState.activeTab) {
    case 'state':
      return renderStateTab();
    case 'switches':
      return renderSwitchesTab();
    case 'devices':
      return renderDevicesTab();
    case 'compute':
      return renderComputeTab();
    case 'settings':
    default:
      return renderSettingsTab();
  }
}

let onHostRenderCallback = null;

export function renderHostPage() {
  const page = document.getElementById('pageHost');
  if (!page) return;

  const tabsHtml = HOST_TABS.map(
    (t) => `
      <button type="button" class="vm-tab-btn ${hostState.activeTab === t.key ? 'selected' : ''}" data-host-tab="${t.key}">
        <span class="fi" style="font-size:15px;">${t.icon}</span>
        <span>${esc(t.label)}</span>
      </button>
    `
  ).join('');

  const noticeHtml = hostState.notice
    ? `
      <div class="winui-infobar is-${hostState.notice.severity}">
        <div class="winui-infobar-main">
          <span class="fi winui-infobar-icon">${hostState.notice.severity === 'warning' ? '&#xE7BA;' : '&#xE73E;'}</span>
          <span>${esc(hostState.notice.text)}</span>
        </div>
        <button type="button" class="pane-toggle-btn" id="hostNoticeCloseBtn" style="width:24px;height:24px;margin:0;">
          <span class="fi" style="font-size:10px;">&#xE711;</span>
        </button>
      </div>
    `
    : '<div></div>';

  page.innerHTML = `
    <div class="vm-pagetabs host-pagetabs">${tabsHtml}</div>
    ${noticeHtml}
    ${renderBodyByActiveTab()}
  `;

  if (onHostRenderCallback) {
    requestAnimationFrame(() => onHostRenderCallback());
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

    const tabBtn = e.target.closest('[data-host-tab]');
    if (tabBtn) {
      hostState.activeTab = tabBtn.getAttribute('data-host-tab') || 'state';
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
      const [tab, sub] = (jumpBtn.getAttribute('data-host-jump') || 'settings:environment').split(':');
      hostState.activeTab = tab;
      if (tab === 'switches') hostState.switchesSub = sub;
      else if (tab === 'devices') hostState.devicesSub = sub;
      else if (tab === 'compute') hostState.computeSub = sub;
      else if (tab === 'settings') hostState.settingsSub = sub;
      renderHostPage();
      return;
    }

    const subBtn = e.target.closest('[data-host-sub]');
    if (subBtn) {
      const [group, key] = (subBtn.getAttribute('data-host-sub') || '').split(':');
      if (group === 'switches') hostState.switchesSub = key;
      else if (group === 'devices') hostState.devicesSub = key;
      else if (group === 'compute') hostState.computeSub = key;
      else if (group === 'settings') hostState.settingsSub = key;
      renderHostPage();
      return;
    }

    const expHead = e.target.closest('[data-host-expand]');
    if (expHead) {
      const [kind, id] = (expHead.getAttribute('data-host-expand') || '').split(':');
      if (kind === 'switch') {
        const sw = hostState.switches.find((x) => x.id === id);
        if (sw) sw.expanded = !sw.expanded;
      } else if (kind === 'pcie') {
        const dev = hostState.pcieDevices.find((x) => x.id === id);
        if (dev) dev.expanded = !dev.expanded;
      } else if (kind === 'cpugroup') {
        const cg = hostState.cpuGroups.find((x) => x.id === id);
        if (cg) cg.expanded = !cg.expanded;
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
        renderHostPage();
      }
      return;
    }

    const pcieBtn = e.target.closest('[data-host-pcie-toggle]');
    if (pcieBtn) {
      const id = pcieBtn.getAttribute('data-host-pcie-toggle');
      const dev = hostState.pcieDevices.find((x) => x.id === id);
      if (dev) {
        dev.ownerKind = dev.ownerKind === 'host' ? 'unassigned' : 'host';
        dev.ownerName = dev.ownerKind === 'host' ? '宿主' : '已从宿主卸下（待分配）';
        hostState.notice = { severity: 'success', text: `${dev.name} 状态已更新为：${dev.ownerName}` };
        renderHostPage();
      }
      return;
    }

    const diskOff = e.target.closest('[data-host-disk-offline]');
    if (diskOff && !diskOff.classList.contains('disabled')) {
      const num = Number(diskOff.getAttribute('data-host-disk-offline'));
      const d = hostState.disks.find((x) => x.number === num);
      if (d) {
        d.offline = !d.offline;
        d.status = d.offline ? '脱机 · 可直通' : '联机';
        renderHostPage();
      }
      return;
    }

    const diskRo = e.target.closest('[data-host-disk-ro]');
    if (diskRo && !diskRo.classList.contains('disabled')) {
      const num = Number(diskRo.getAttribute('data-host-disk-ro'));
      const d = hostState.disks.find((x) => x.number === num);
      if (d) {
        d.readOnly = !d.readOnly;
        renderHostPage();
      }
      return;
    }

    if (e.target.closest('#hostAddSwitchBtn')) {
      const idx = hostState.switches.length + 1;
      hostState.switches.push({
        id: `sw-custom-${idx}`,
        name: `Custom Switch ${idx}`,
        mode: 'Isolated',
        allowManagementOs: false,
        upstream: '无物理上行网卡',
        ipv4: '---',
        sendRate: '0 Kbps',
        recvRate: '0 Kbps',
        sendPts: [0, 0, 0, 0, 0, 0, 0],
        recvPts: [0, 0, 0, 0, 0, 0, 0],
        endpoints: [],
        expanded: true
      });
      hostState.notice = { severity: 'success', text: `已新建内部隔离交换机 Custom Switch ${idx}` };
      renderHostPage();
      return;
    }

    if (e.target.closest('#hostMountVhdxBtn')) {
      hostState.notice = { severity: 'info', text: '原型演示：此处将调用 HostDiskService.MountVhdxAsync 弹出文件选择器。' };
      renderHostPage();
      return;
    }

    if (e.target.closest('#hostAddVmGroupBtn')) {
      hostState.vmGroups.push({
        id: `grp-${Date.now()}`,
        name: `新建分组 ${hostState.vmGroups.length + 1}`,
        kind: 'VirtualMachines',
        children: [],
        vms: []
      });
      renderHostPage();
      return;
    }

    const schedBtn = e.target.closest('[data-host-sched]');
    if (schedBtn) {
      hostState.draft.scheduler = schedBtn.getAttribute('data-host-sched') || 'Core';
      renderHostPage();
      return;
    }

    const pickBtn = e.target.closest('[data-host-pick]');
    if (pickBtn) {
      const kind = pickBtn.getAttribute('data-host-pick');
      const presets = kind === 'vm' ? PATH_PRESETS.vm : PATH_PRESETS.vhd;
      const key = kind === 'vm' ? 'defaultVmPath' : 'defaultVhdPath';
      hostState.draft[key] = presets[(presets.indexOf(hostState.draft[key]) + 1) % presets.length];
      renderHostPage();
      return;
    }

    const draftSw = e.target.closest('[data-host-draft-switch]');
    if (draftSw && !draftSw.classList.contains('disabled')) {
      const key = draftSw.getAttribute('data-host-draft-switch');
      if (key && key in hostState.draft) {
        hostState.draft[key] = !hostState.draft[key];
        renderHostPage();
      }
      return;
    }

    const applyBtn = e.target.closest('[data-host-apply]');
    if (applyBtn) {
      const grp = applyBtn.getAttribute('data-host-apply');
      const map = {
        mac: ['macMin', 'macMax'],
        compute: ['powerPlanId', 'scheduler', 'numaSpanning'],
        paths: ['defaultVmPath', 'defaultVhdPath', 'enhancedSession', 'volumeAutoMount'],
        experimental: ['pciePolicyUnlocked', 'serverProductTypeSpoof', 'nativeNvme', 'openHclFirmware'],
        preferences: ['autoOpenConsole', 'defaultPowerAction']
      };
      const keys = map[grp] || [];
      const needReboot =
        (keys.includes('scheduler') && hostState.draft.scheduler !== hostState.saved.scheduler) ||
        (keys.includes('serverProductTypeSpoof') && hostState.draft.serverProductTypeSpoof !== hostState.saved.serverProductTypeSpoof) ||
        (keys.includes('nativeNvme') && hostState.draft.nativeNvme !== hostState.saved.nativeNvme);
      applyKeys(keys);
      hostState.notice = {
        severity: needReboot ? 'warning' : 'success',
        text: needReboot ? '设置已保存，部分系统级修改需重启宿主后生效。' : '宿主设置已应用。'
      };
      renderHostPage();
      return;
    }

    const revertBtn = e.target.closest('[data-host-revert]');
    if (revertBtn) {
      const grp = revertBtn.getAttribute('data-host-revert');
      const map = {
        mac: ['macMin', 'macMax'],
        compute: ['powerPlanId', 'scheduler', 'numaSpanning'],
        paths: ['defaultVmPath', 'defaultVhdPath', 'enhancedSession', 'volumeAutoMount'],
        experimental: ['pciePolicyUnlocked', 'serverProductTypeSpoof', 'nativeNvme', 'openHclFirmware'],
        preferences: ['autoOpenConsole', 'defaultPowerAction']
      };
      revertKeys(map[grp] || []);
      renderHostPage();
    }
  });

  page.addEventListener('change', (e) => {
    if (e.target.id === 'hostPowerPlanSelect') {
      hostState.draft.powerPlanId = e.target.value;
      renderHostPage();
    } else if (e.target.id === 'hostPowerActionSelect') {
      hostState.draft.defaultPowerAction = e.target.value;
      renderHostPage();
    } else if (e.target.matches('[data-host-sw-mode]')) {
      const id = e.target.getAttribute('data-host-sw-mode');
      const sw = hostState.switches.find((x) => x.id === id);
      if (sw) {
        sw.mode = e.target.value;
        renderHostPage();
      }
    }
  });

  page.addEventListener('input', (e) => {
    if (e.target.id === 'hostMacMinInput' || e.target.id === 'hostMacMaxInput') {
      if (e.target.id === 'hostMacMinInput') hostState.draft.macMin = e.target.value;
      else hostState.draft.macMax = e.target.value;
      const dirty = isGroupDirty(['macMin', 'macMax']);
      const applyBtn = page.querySelector('[data-host-apply="mac"]');
      const revertBtn = page.querySelector('[data-host-revert="mac"]');
      if (applyBtn) applyBtn.disabled = !dirty;
      if (revertBtn) revertBtn.disabled = !dirty;
    }
  });

  renderHostPage();
}
