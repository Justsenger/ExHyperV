import {
  OS_TYPES,
  PAGE_TABS,
  MERGED_SUBNAV_GROUPS,
  VM_ITEMS,
  PROCESSOR_SECTIONS,
  MEMORY_SECTIONS,
  INITIAL_STORAGE_ITEMS,
  INITIAL_GPU_ASSIGNMENTS,
  HOST_GPUS,
  INITIAL_GPU_PARTITIONS,
  LINUX_GPU_SCRIPTS,
  INITIAL_BOOT_ENTRIES,
  INITIAL_SERIAL_PORTS,
  INITIAL_PCIE_DEVICES,
  SPACETIME_NODES
} from './ui-design-data.js';
import {
  initComputePlayground,
  renderComputeSection,
  isComputeSectionDirty
} from './ui-design-compute.js';
import { renderGpuWorkbench } from './ui-design-devices.js';
import { buildVirtualGpuEntries, initHostPage, renderHostPage } from './ui-design-host.js';

export function initUiDesignWorkbench() {
  const winEl = document.getElementById('winuiWindow');
  if (!winEl || winEl.dataset.bound === 'true') return;
  winEl.dataset.bound = 'true';
  initHostPage({ onRender: layoutDynamicGrids });

  const state = {
    navTarget: 'vm',
    selectedVmId: VM_ITEMS[0].id,
    searchQuery: '',
    activeTabKey: 'overview',
    deviceSubKey: 'gpu',
    advancedSubKey: 'boot',
    tailOrder: 'spacetime-first',
    advSettings: {
      fullScsi: false,
      numLock: false,
      consoleSupport: true,
      resolution: '默认（自适应）',
      lockOnDisconnect: false,
      hibernation: false,
      turnOffOnRestart: false,
      batteryPassthrough: true
    },
    uiLang: 'zh',
    enShort: false,
    selectedResource: 'cpu', // 'cpu' | 'mem'
    overviewLayout: 'tm-adaptive', // 'tm-adaptive' | 'original'
    stressDataMode: false,
    tcSelected: { kind: 'gpu', idx: 0 },
    selectedDeviceTab: 0,    // 0: 显卡, 1: 存储, 2: 网卡
    selectedGpuIdx: 0,
    selectedDiskIdx: 0,
    selectedNetIdx: 0,
    processorSectionIdx: 2,
    memorySectionIdx: 0,
    affinityMask: new Set([0, 1, 2, 3, 4, 5, 6, 7]),
    storageItems: structuredClone(INITIAL_STORAGE_ITEMS),
    storageAddingMode: false,
    storageForm: {
      driveType: 'HardDisk',
      mediaSource: 'Virtual',
      operation: 'Existing',
      filePath: 'D:\\Hyper-V\\Virtual Hard Disks\\extra_disk.vhdx',
      sizeGb: '128',
      vhdType: 'Dynamic',
      parentPath: '',
      autoLocation: true,
      controllerType: 'SCSI',
      controllerNumber: 0,
      location: 2
    },
    gpuMode: 'list', // 'list' | 'select' | 'deploy'
    gpuVariant: 'expander', // 'expander' | 'tiles' | 'compact' | 'layered'
    gpuEnvExpanded: false,
    selectedHostGpuId: HOST_GPUS[0].id,
    installGpuDrivers: true,
    gpuScenario: 'normal', // 'normal' | 'stale-single' | 'stale-multi' | 'stale-none'
    gpuVmReady: true,
    gpuHostPolicyRelaxed: false,
    gpuSelectedRebindId: '',
    gpuDeployMode: 'add-with-drivers',
    gpuDeployDecision: 'SelectPartition',
    gpuSelectedPartitionId: INITIAL_GPU_PARTITIONS[0].id,
    gpuLogExpanded: false,
    gpuLinuxForm: {
      host: '172.24.118.94',
      port: '22',
      username: 'ubuntu',
      password: '••••••',
      scriptId: LINUX_GPU_SCRIPTS[0].id
    },
    gpuAssignmentsByVm: {
      'vm-test': structuredClone(INITIAL_GPU_ASSIGNMENTS),
      'vm-win11-arm64': structuredClone(INITIAL_GPU_ASSIGNMENTS)
    },
    netVlanMode: 'Access',
    bootEntries: structuredClone(INITIAL_BOOT_ENTRIES),
    serialPortsByVm: {
      'vm-test': structuredClone(INITIAL_SERIAL_PORTS),
      'vm-win11-arm64': structuredClone(INITIAL_SERIAL_PORTS)
    },
    pcieDevicesByVm: {
      'vm-test': [],
      'vm-win11-arm64': structuredClone(INITIAL_PCIE_DEVICES)
    },
    spacetimeSelectedId: 'node-current',
    useThemeAccentForHeader: true,
    accentHex: '#4CC2FF',
    dcpMode: 'isa',
    isaVariant: 'single-row-equal',
    isaMigDesign: 'three-way',
    isaMigTerms: 'friendly',
    isaLegacyName: '老旧系统兼容',
    isaByVm: {},
    computeVariant: 'energy-fader',
    computeThumbStyle: 'slim-bar',
    computeActionMode: 'winui-rect',
    noticeMode: 'banner',
    memComputeLayoutMode: '2-1-3',
    memDynamicOffMode: 'disable',
    memSmartPagingAlert: false,
    singleChartHeight: 136,
    diskCandidate: 'a', // 'a' | 'b' | 'c'
    isoChartMode: 'single', // 'single' | 'dual'
    diffParentMode: 'wide', // 'wide' | 'top-right'
    diskPreviewMode: 'virtual', // 'virtual' | 'fixed' | 'differencing' | 'iso' | 'physical' | 'stale'
    netPreviewMode: 'connected', // 'connected' | 'bridge' | 'disconnected'
    netCandidate: 'a', // 'a' | 'b' | 'c'
    netHeaderMode: 'switch-ip', // 'switch-ip' | 'adapter-switch'
    diskParts: {
      slotInSub: false,
      iops: false,
      errorAlert: false
    },
    computeFlags: {
      jumboSize: false,
      valTopRight: true,
      showSteppers: false,
      showEquivalent: false,
      dragTooltip: true,
      weightPresetMode: false
    },
    computeByVm: {},
    hostLogicalCores: 16
  };

  let latestVirtualHw = null;

  const getVm = () => VM_ITEMS.find((v) => v.id === state.selectedVmId) || VM_ITEMS[0];

  function hexToRgbTuple(hex) {
    const clean = hex.replace('#', '');
    const n = parseInt(clean, 16);
    return `${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}`;
  }

  function applyAccentVariables() {
    const rgb = hexToRgbTuple(state.accentHex);
    winEl.style.setProperty('--VmAccentColor', state.accentHex);
    winEl.style.setProperty('--VmAccentRgb', rgb);
    winEl.style.setProperty('--VmAccentBrush', state.accentHex);
    if (state.useThemeAccentForHeader) {
      winEl.style.setProperty('--VmHeaderCheckedBrush', state.accentHex);
      winEl.style.setProperty('--VmHeaderForegroundBrush', '#000000');
    } else {
      const isLight = winEl.getAttribute('data-theme') === 'light';
      winEl.style.setProperty('--VmHeaderCheckedBrush', isLight ? '#DCDCDC' : '#3A3A3A');
      winEl.style.setProperty('--VmHeaderForegroundBrush', isLight ? '#000000' : '#FFFFFF');
    }
    winEl.style.setProperty('--VmChartFillBrush', `rgba(${rgb}, 0.25)`);
    winEl.style.setProperty('--VmGpuChartFillBrush', `rgba(${rgb}, 0.40)`);
    const colorText = document.getElementById('settingsColorValueText');
    if (colorText) {
      colorText.textContent = state.useThemeAccentForHeader ? state.accentHex : '灰色';
    }
    const accentSwitch = document.getElementById('settingsAccentSwitch');
    if (accentSwitch) {
      accentSwitch.classList.toggle('on', state.useThemeAccentForHeader);
    }
    const accentSwitchLabel = document.getElementById('settingsAccentSwitchLabel');
    if (accentSwitchLabel) {
      accentSwitchLabel.textContent = state.useThemeAccentForHeader ? '开启' : '关闭';
    }
    const dcpAccentBtn = document.getElementById('dcpToggleAccentBtn');
    if (dcpAccentBtn) {
      dcpAccentBtn.classList.toggle('active', state.useThemeAccentForHeader);
      dcpAccentBtn.textContent = state.useThemeAccentForHeader ? '主题色: 开启 (波形2)' : '主题色: 关闭 (波形1)';
    }
  }

  let noticeTimer = null;
  let inlineNoticeTimer = null;

  function showNotice(message) {
    const inSettings = state.activeTabKey && state.activeTabKey !== 'overview';
    const settingsBar = document.getElementById('settingsNoticeBar');
    const settingsTxt = document.getElementById('settingsNoticeText');
    const bar = document.getElementById('dashboardNoticeBar');
    const txt = document.getElementById('dashboardNoticeText');
    const mode = state.noticeMode || 'banner';

    if (noticeTimer) {
      clearTimeout(noticeTimer);
      noticeTimer = null;
    }
    if (inlineNoticeTimer) {
      clearTimeout(inlineNoticeTimer);
      inlineNoticeTimer = null;
    }

    if (inSettings && settingsBar && settingsTxt) {
      if (bar) bar.hidden = true;
      settingsTxt.textContent = message;
      settingsBar.hidden = false;
      noticeTimer = setTimeout(() => {
        settingsBar.hidden = true;
      }, 2600);
      return;
    }

    if (settingsBar) settingsBar.hidden = true;
    if (!bar || !txt) return;
    txt.textContent = message;
    bar.classList.toggle('is-toast', mode !== 'banner');
    bar.hidden = false;
    noticeTimer = setTimeout(() => {
      bar.hidden = true;
    }, 2600);
  }

  // Generate smooth SVG area + stroke path for VmUsageChart when VM is running
  function buildChartSvg(points, isGpu = false) {
    const w = 240;
    const h = 100;
    const step = w / (points.length - 1);
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
        <span class="vm-usage-chart-caption">${caption}</span>
        ${active && points.length > 1 ? buildChartSvg(points, isGpu) : ''}
        ${active ? `<span class="vm-usage-chart-value">${valueText}</span>` : ''}
      </div>
    `;
  }

  function renderSparkThumbSvg(points, active, strokeColor = 'var(--VmAccentBrush)', fillColor = 'var(--VmChartFillBrush)') {
    const w = 52;
    const h = 46;
    if (!active || points.length < 2) {
      return '';
    }
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

  function getCoreSample(idx) {
    const base = 14 + ((idx * 11) % 36);
    const pts = [base - 6, base + 4, base - 2, base + 9, base + 3, base + 12, base];
    return { base, pts };
  }

  const STRESS_GPUS = [
    {
      id: 'stress-gpu-0',
      name: 'NVIDIA GeForce RTX 4090 Laptop GPU (DDA 直通)',
      vendor: 'NVIDIA',
      memoryText: '24564 MB',
      driverVersion: '32.0.15.6636',
      instanceId: 'PCI\\VEN_10DE&DEV_2684&SUBSYS_409010DE&REV_A1\\4&2C8B14A&0&0008'
    },
    {
      id: 'stress-gpu-1',
      name: 'AMD Radeon(TM) RX 7900 XTX Workstation Edition (GPU-P 分区 50%)',
      vendor: 'AMD',
      memoryText: '24576 MB',
      driverVersion: '31.0.24002.92',
      instanceId: 'PCI\\VEN_1002&DEV_744C&SUBSYS_0E3A1002&REV_C8\\6&18C2F1A&0&00000009'
    }
  ];

  const STRESS_DISKS = [
    {
      key: 's-disk-0',
      title: 'Windows11_24H2_Enterprise_SystemBoot_OSDisk_Gen2.vhdx',
      subtitle: '动态扩展 · SCSI 0:0',
      glyph: '\uEC59',
      fileSizeText: '68.4 GB',
      capacityText: '128 GB',
      ratio: 0.534,
      readRate: '2.4 MB/s',
      writeRate: '610 KB/s',
      path: 'D:\\Hyper-V\\VMs\\Prod-Cluster\\Windows11_24H2_Enterprise_SystemBoot_OSDisk_Gen2.vhdx'
    },
    {
      key: 's-disk-1',
      title: 'Production_SQL_Server_Cluster_Node01_TransactionLogs_Volume.vhdx',
      subtitle: '固定大小 · SCSI 0:1',
      glyph: '\uEC59',
      fileSizeText: '184.2 GB',
      capacityText: '512 GB',
      ratio: 0.36,
      readRate: '18.6 MB/s',
      writeRate: '9.4 MB/s',
      path: 'E:\\NVMe_Array_01\\SQL\\Production_SQL_Server_Cluster_Node01_TransactionLogs_Volume.vhdx'
    },
    {
      key: 's-disk-2',
      title: 'NVMe_Scratch_Cache_TempDB_HighIOPS_Passthrough_Disk_03.vhdx',
      subtitle: '物理直通 · SCSI 0:2',
      glyph: '\uEC59',
      fileSizeText: '12.8 GB',
      capacityText: '256 GB',
      ratio: 0.05,
      readRate: '420 KB/s',
      writeRate: '128 KB/s',
      path: '\\\\.\\PhysicalDrive4 (Samsung PM9A3 1.92TB U.3 Enterprise)'
    },
    {
      key: 's-disk-3',
      title: 'Cold_Archive_Backup_Snapshots_Repository_2026_Q4_Mirror.vhdx',
      subtitle: '差分磁盘 · SCSI 0:3',
      glyph: '\uEC59',
      fileSizeText: '410.0 GB',
      capacityText: '1 TB',
      ratio: 0.40,
      readRate: '0 KB/s',
      writeRate: '32 KB/s',
      path: 'Z:\\NAS_Mount\\Archives\\Cold_Archive_Backup_Snapshots_Repository_2026_Q4_Mirror.vhdx'
    }
  ];

  const STRESS_ADAPTERS = [
    {
      key: 's-net-0',
      title: 'Microsoft Hyper-V 网络适配器 #1 — Intel(R) Ethernet Controller (3) I226-V',
      subtitle: '外部高速虚拟交换机 (Default Uplink)',
      mac: '00155D012A0B',
      ipv4: '172.24.118.94',
      ipv6: 'fe80::215:5dff:fe01:2a0b',
      txRate: '128 KB/s',
      rxRate: '4.6 MB/s'
    },
    {
      key: 's-net-1',
      title: 'Microsoft Hyper-V 网络适配器 #2 — Mellanox ConnectX-6 Lx 25GbE SFP28 存储专网',
      subtitle: 'iSCSI / SMB Direct VLAN-104 专用交换机',
      mac: '00155D012A0C',
      ipv4: '10.104.12.88',
      ipv6: 'fe80::215:5dff:fe01:2a0c',
      txRate: '14.2 MB/s',
      rxRate: '22.8 MB/s'
    },
    {
      key: 's-net-2',
      title: 'Microsoft Hyper-V 网络适配器 #3 — Tailscale WireGuard Overlay Tunnel Adapter',
      subtitle: '内网加密隔离交换机 (Internal Switch)',
      mac: '00155D012A0D',
      ipv4: '100.89.14.2',
      ipv6: 'fd7a:115c:a1e0::2',
      txRate: '12 KB/s',
      rxRate: '64 KB/s'
    }
  ];

  function getEffectiveOverviewTarget() {
    const baseVm = getVm();
    const baseGpus = state.gpuAssignmentsByVm[baseVm.id] || [];
    if (!state.stressDataMode) {
      return { vm: baseVm, gpuAssigns: baseGpus };
    }
    return {
      vm: {
        ...baseVm,
        cores: Math.max(12, baseVm.cores),
        memoryGb: 32.0,
        usedMemoryGb: 21.4,
        disks: STRESS_DISKS,
        adapters: STRESS_ADAPTERS
      },
      gpuAssigns: STRESS_GPUS
    };
  }

  // 1. Render Left VM List (VirtualMachinesPage.xaml lines 15-38)
  function renderVmList() {
    const container = document.getElementById('vmListContainer');
    if (!container) return;
    const q = state.searchQuery.trim().toLowerCase();
    const filtered = VM_ITEMS.filter((vm) => !q || vm.name.toLowerCase().includes(q));

    container.innerHTML = filtered
      .map((vm) => {
        const selected = vm.id === state.selectedVmId;
        return `
          <div class="vm-listitem ${selected ? 'selected' : ''}" data-vm-id="${vm.id}">
            <span class="vm-state-ellipse ${vm.running ? 'running' : ''}"></span>
            <span class="fi vm-item-glyph">&#xE7F4;</span>
            <span class="vm-item-name">${vm.name}</span>
          </div>
        `;
      })
      .join('');

    container.querySelectorAll('[data-vm-id]').forEach((el) => {
      el.addEventListener('click', () => {
        state.selectedVmId = el.getAttribute('data-vm-id');
        state.selectedGpuIdx = 0;
        state.selectedDiskIdx = 0;
        state.selectedNetIdx = 0;
        state.gpuMode = 'list';
        state.gpuScenario = 'normal';
        state.gpuSelectedRebindId = '';
        state.ribbonComboOpen = false;
        syncDcpGpuButton();
        renderVmList();
        renderDashboardSummary();
        renderActiveWorkspace();
      });
    });
  }

  // 2. Render Summary Card (VmDashboardView.xaml lines 11-54 & Presentation.cs)
  function renderDashboardSummary() {
    const vm = getVm();
    const osImg = document.getElementById('vmOsImage');
    const sysIconPanel = document.getElementById('vmSystemIconPanel');
    const previewBorder = document.getElementById('vmPreviewBorder');
    if (osImg) osImg.src = vm.osImage;
    if (sysIconPanel) {
      sysIconPanel.style.cursor = 'pointer';
      sysIconPanel.title = '点击切换系统标志';
      if (!sysIconPanel.dataset.osPickerBound) {
        sysIconPanel.dataset.osPickerBound = '1';
        sysIconPanel.addEventListener('click', (e) => {
          e.stopPropagation();
          toggleOsPickerFlyout();
        });
      }
    }
    if (sysIconPanel && previewBorder) {
      sysIconPanel.style.display = vm.running ? 'none' : 'flex';
      previewBorder.hidden = !vm.running;
    }

    const titleEl = document.getElementById('vmTitleText');
    if (titleEl) titleEl.textContent = vm.name;

    const powerWrap = document.getElementById('vmPowerWrap');
    const powerMainBtn = document.getElementById('vmPowerMainBtn');
    const powerDropBtn = document.getElementById('vmPowerDropBtn');
    const powerFlyout = document.getElementById('vmPowerFlyout');
    if (powerWrap && powerMainBtn && powerDropBtn) {
      powerWrap.className = `power-split-wrap ${vm.running ? 'running' : 'stopped'}`;
      powerMainBtn.textContent = vm.running ? '关机' : '启动';
      powerDropBtn.hidden = !vm.running;
      if (!vm.running && powerFlyout) powerFlyout.hidden = true;
    }

    const stateText = document.getElementById('vmStateText');
    const uptimeText = document.getElementById('vmUptimeText');
    const genTextEl = document.getElementById('vmGenText');
    if (stateText) stateText.textContent = vm.running ? '运行中' : '已关机';
    if (uptimeText) uptimeText.textContent = vm.running ? vm.liveUptime : '00:00:00';
    if (genTextEl) {
      genTextEl.textContent = `${vm.gen || 2}代`;
    }

    const genSwitchBtn = document.getElementById('vmGenSwitchBtn');
    if (genSwitchBtn && !genSwitchBtn.dataset.bound) {
      genSwitchBtn.dataset.bound = '1';
      genSwitchBtn.addEventListener('click', () => {
        const curVm = getVm();
        curVm.gen = (curVm.gen || 2) === 2 ? 1 : 2;
        renderDashboardSummary();
      });
    }

    const dcpToggleGenBtn = document.getElementById('dcpToggleGenBtn');
    if (dcpToggleGenBtn && !dcpToggleGenBtn.dataset.bound) {
      dcpToggleGenBtn.dataset.bound = '1';
      dcpToggleGenBtn.addEventListener('click', () => {
        const curVm = getVm();
        curVm.gen = (curVm.gen || 2) === 2 ? 1 : 2;
        renderDashboardSummary();
      });
    }

    const cfgBadge = document.getElementById('vmConfigBadgeText');
    if (cfgBadge) cfgBadge.textContent = vm.configText;

    const genBadge = document.getElementById('vmGenerationBadge');
    const genText = document.getElementById('vmGenerationBadgeText');
    if (genBadge && genText) {
      genBadge.hidden = !vm.generationText;
      genText.textContent = vm.generationText;
    }

    const verBadge = document.getElementById('vmVersionBadge');
    const verText = document.getElementById('vmVersionBadgeText');
    if (verBadge && verText) {
      verBadge.hidden = !vm.versionText;
      verText.textContent = vm.versionText;
    }

    const ipVal = vm.running ? vm.ipWhenRunning : '---';

    if (!state.activeTags) {
      state.activeTags = {
        os: true,
        ip: false,
        spec: false,
        version: false,
        guid: true,
        folder: false
      };
    }
    const t = state.activeTags;

    const stateVerText = document.getElementById('vmStateVersionText');
    if (stateVerText) stateVerText.textContent = vm.versionText || '版本 11.0';

    const badgeRow2 = document.getElementById('vmBadgeRow2');
    if (badgeRow2) {
      const chips = [];
      if (t.os) {
        const osName = vm.osText || 'Windows';
        chips.push(
          `<button class="vm-badge" data-open-os-picker="true" title="点击切换系统标志"><span class="fi vm-badge-icon">&#xE74C;</span><span class="vm-badge-text">${osName}</span><span class="fi vm-os-badge-chevron">&#xE70D;</span></button>`
        );
      }
      if (t.guid !== false) {
        const guidVal = vm.guid || '7A9C4E12-8B3F-4D21-9E6A-0F1B2C3D4E5F';
        chips.push(
          `<button class="vm-badge" data-copy-guid="${guidVal}" title="点击复制虚拟机 GUID"><span class="vm-badge-text vm-guid-text">${guidVal}</span><span class="fi vm-os-badge-chevron">&#xE8C8;</span></button>`
        );
      }
      if (t.spec) {
        chips.push(
          `<div class="vm-badge"><span class="fi vm-badge-icon">&#xE81E;</span><span class="vm-badge-text">${vm.cores} 核 · ${vm.memoryGb} GB</span></div>`
        );
      }
      if (t.ip) {
        chips.push(
          `<button class="vm-badge" data-copy-ip="${ipVal}" title="点击复制完整 IP 地址"><span class="fi vm-badge-icon">&#xE839;</span><span class="vm-badge-text">${ipVal}</span></button>`
        );
      }
      if (t.version) {
        chips.push(
          `<div class="vm-badge"><span class="fi vm-badge-icon">&#xE946;</span><span class="vm-badge-text">版本 11.0</span></div>`
        );
      }
      if (t.folder) {
        chips.push(
          `<button class="vm-badge" data-open-cfg="true" title="D:\\Hyper-V\\Virtual Machines\\${vm.name}.vmcx"><span class="fi fi-fluent vm-badge-icon">&#xF6B0;</span><span class="vm-badge-text">配置目录</span></button>`
        );
      }
      badgeRow2.innerHTML = chips.join('');

      badgeRow2.querySelectorAll('[data-open-os-picker]').forEach((btn) => {
        btn.addEventListener('click', (e) => {
          e.stopPropagation();
          toggleOsPickerFlyout();
        });
      });
      badgeRow2.querySelectorAll('[data-copy-guid]').forEach((btn) => {
        btn.addEventListener('click', () => {
          const val = btn.getAttribute('data-copy-guid') || '';
          if (val) {
            navigator.clipboard?.writeText(val).catch(() => {});
            showNotice(`已复制 GUID：${val}`);
          }
        });
      });
      badgeRow2.querySelectorAll('[data-toggle-fw]').forEach((btn) => {
        btn.addEventListener('click', () => {
          vm.firmware = (vm.firmware || 'UEFI') === 'UEFI' ? 'MBR' : 'UEFI';
          renderDashboardSummary();
        });
      });
      badgeRow2.querySelectorAll('[data-copy-ip]').forEach((btn) => {
        btn.addEventListener('click', () => {
          const val = btn.getAttribute('data-copy-ip') || '';
          if (val && val !== '---') {
            navigator.clipboard?.writeText(val).catch(() => {});
            showNotice(`已复制 IP 地址：${val}`);
          }
        });
      });
      badgeRow2.querySelectorAll('[data-open-cfg]').forEach((btn) => {
        btn.addEventListener('click', () => {
          showNotice(`已在资源管理器定位：D:\\Hyper-V\\Virtual Machines\\${vm.name}.vmcx`);
        });
      });
    }

    const matrixOs = document.getElementById('vmMatrixOs');
    const matrixSpec = document.getElementById('vmMatrixSpec');
    const matrixIp = document.getElementById('vmMatrixIp');
    if (matrixOs) matrixOs.textContent = vm.osText;
    if (matrixSpec) matrixSpec.textContent = `${vm.cores} 核 · ${vm.memoryGb} GB`;
    if (matrixIp) matrixIp.textContent = ipVal;
  }

  function closeOsPickerFlyout() {
    const flyout = document.getElementById('osPickerFlyout');
    if (flyout) flyout.hidden = true;
  }

  function renderOsPickerGrid(query = '') {
    const grid = document.getElementById('osPickerGrid');
    if (!grid) return;
    const vm = getVm();
    const currentOs = (vm.osText || 'Windows').toLowerCase();
    const q = query.trim().toLowerCase();
    const list = q ? OS_TYPES.filter((name) => name.toLowerCase().includes(q)) : OS_TYPES;

    if (list.length === 0) {
      grid.innerHTML = '<div class="os-picker-empty">未找到匹配的系统</div>';
      return;
    }

    grid.innerHTML = list
      .map((name) => {
        const selected = name.toLowerCase() === currentOs ? ' selected' : '';
        return `<button type="button" class="os-picker-item${selected}" data-pick-os="${name}" title="${name}">
          <img class="os-picker-item-img" src="/ui-design/os/${name}.svg" alt="" />
          <span class="os-picker-item-label">${name}</span>
        </button>`;
      })
      .join('');

    grid.querySelectorAll('[data-pick-os]').forEach((btn) => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const picked = btn.getAttribute('data-pick-os');
        if (!picked) return;
        const curVm = getVm();
        curVm.osText = picked;
        curVm.osImage = `/ui-design/os/${picked}.svg`;
        closeOsPickerFlyout();
        renderDashboardSummary();
      });
    });
  }

  function toggleOsPickerFlyout() {
    const flyout = document.getElementById('osPickerFlyout');
    const search = document.getElementById('osPickerSearch');
    if (!flyout) return;
    const willOpen = flyout.hidden;
    if (!willOpen) {
      closeOsPickerFlyout();
      return;
    }
    if (!flyout.dataset.bound) {
      flyout.dataset.bound = '1';
      flyout.addEventListener('click', (e) => e.stopPropagation());
      search?.addEventListener('input', () => renderOsPickerGrid(search.value));
      search?.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') closeOsPickerFlyout();
      });
      document.addEventListener('click', closeOsPickerFlyout);
    }
    if (search) search.value = '';
    renderOsPickerGrid('');
    flyout.hidden = false;
    search?.focus();
  }

  // 3. Render PageTabs (VmDashboardView.Settings.cs lines 24-25 + VmTabs.cs)
  function ensureSelectedTabVisible(bar) {
    const selBtn = bar.querySelector('.vm-tab-btn.selected');
    if (!selBtn || bar.clientWidth <= 0) return;
    const left = selBtn.offsetLeft;
    const right = left + selBtn.offsetWidth;
    if (left < bar.scrollLeft || selBtn.offsetWidth > bar.clientWidth) {
      bar.scrollLeft = left;
    } else if (right > bar.scrollLeft + bar.clientWidth) {
      bar.scrollLeft = right - bar.clientWidth;
    }
  }

  function getLocalizedLabel(item) {
    if (state.uiLang === 'en') {
      if (state.enShort && item.titleEnShort) return item.titleEnShort;
      return item.titleEn || item.title;
    }
    return item.title;
  }

  function renderPageTabs() {
    const bar = document.getElementById('vmPageTabsBar');
    if (!bar) return;
    const prevScroll = bar.scrollLeft;
    const tabs = [...PAGE_TABS];
    if (state.tailOrder === 'advanced-first') {
      const spIdx = tabs.findIndex((t) => t.key === 'spacetime');
      const adIdx = tabs.findIndex((t) => t.key === 'advanced');
      if (spIdx !== -1 && adIdx !== -1 && spIdx < adIdx) {
        [tabs[spIdx], tabs[adIdx]] = [tabs[adIdx], tabs[spIdx]];
      }
    }
    bar.innerHTML = tabs.map((tab) => {
      const selected = tab.key === state.activeTabKey;
      const iconHtml = tab.glyph
        ? `<span class="fi ${tab.fluent ? 'fi-fluent' : ''}" style="font-size:16px;${tab.rotate ? `transform:rotate(${tab.rotate}deg);` : ''}">${tab.glyph}</span>`
        : '';
      return `
        <button class="vm-tab-btn ${selected ? 'selected' : ''}" data-page-tab="${tab.key}">
          ${iconHtml}
          <span>${getLocalizedLabel(tab)}</span>
        </button>
      `;
    }).join('');
    bar.scrollLeft = prevScroll;
    requestAnimationFrame(() => ensureSelectedTabVisible(bar));

    if (!bar.dataset.wheelBound) {
      bar.dataset.wheelBound = 'true';
      bar.addEventListener(
        'wheel',
        (e) => {
          if (bar.scrollWidth <= bar.clientWidth) return;
          if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) {
            e.preventDefault();
            bar.scrollLeft += (e.deltaY / 100) * 80;
          }
        },
        { passive: false }
      );
    }

    bar.querySelectorAll('[data-page-tab]').forEach((btn) => {
      btn.addEventListener('click', () => {
        state.activeTabKey = btn.getAttribute('data-page-tab');
        renderPageTabs();
        renderActiveWorkspace();
      });
    });
  }

  // 4. Render Overview (状态) — Original + 3 Desktop-Native Interaction Modes
  function getOverviewTelemetry(vm, gpuAssigns) {
    state.selectedGpuIdx = Math.min(state.selectedGpuIdx, Math.max(0, gpuAssigns.length - 1));
    state.selectedDiskIdx = Math.min(state.selectedDiskIdx, Math.max(0, vm.disks.length - 1));
    state.selectedNetIdx = Math.min(state.selectedNetIdx, Math.max(0, vm.adapters.length - 1));
    const activeGpu = gpuAssigns[state.selectedGpuIdx] || gpuAssigns[0] || null;
    const activeDisk = vm.disks[state.selectedDiskIdx] || vm.disks[0];
    const activeNet = vm.adapters[state.selectedNetIdx] || vm.adapters[0];
    const memPct = vm.running ? Math.round((vm.usedMemoryGb / vm.memoryGb) * 100) : 0;
    const usedMemText = vm.running ? vm.usedMemoryGb.toFixed(1) : '0.0';
    const totalMemText = vm.memoryGb.toFixed(1);
    const availGb = vm.running ? Math.max(0, vm.memoryGb - vm.usedMemoryGb).toFixed(1) : vm.memoryGb.toFixed(1);
    const coreSamples = Array.from({ length: vm.cores }, (_, i) => getCoreSample(i));
    return { activeGpu, activeDisk, activeNet, memPct, usedMemText, totalMemText, availGb, coreSamples };
  }

  function renderKvGridHtml(items, gridClass = '') {
    const cls = gridClass ? `dt-kv-grid ${gridClass}` : 'dt-kv-grid';
    return `
      <div class="${cls}">
        ${items
          .map(
            (it) => `
          <div class="dt-kv-item">
            <span class="dt-kv-label">${it.label}</span>
            ${
              it.copyVal
                ? `<button class="dt-copy-link" data-copy="${it.copyVal}" title="点击复制：${it.copyVal}"><span>${it.value}</span><span class="fi" style="font-size:11px;opacity:0.55;">&#xE8C8;</span></button>`
                : `<span class="dt-kv-val ${it.mono ? 'mono' : ''}" title="${it.title || it.value}">${it.value}</span>`
            }
          </div>
        `
          )
          .join('')}
      </div>
    `;
  }

  const CPU_TOPOLOGY_PRESETS = {
    single: 'AMD Ryzen 9 9950X 16-Core Processor',
    mixed: 'AMD EPYC 9654 96-Core Processor × 1\nIntel(R) Xeon(R) Gold 6430 × 1'
  };

  function escapeHtmlText(text = '') {
    return String(text)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function formatCpuTitleHtml(rawModel = '') {
    const lines = String(rawModel)
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter(Boolean);
    if (!lines.length) return '处理器';
    return lines.map((line) => escapeHtmlText(line)).join('<br>');
  }

  function getProcessorField(fieldId) {
    for (const sec of PROCESSOR_SECTIONS) {
      const hit = sec.fields?.find((f) => f.id === fieldId);
      if (hit) return hit;
    }
    return null;
  }

  function getCpuSummaryMeta() {
    const affinitySec = PROCESSOR_SECTIONS.find((s) => s.kind === 'affinity');
    const hostCpuModel = affinitySec?.cpuModel || 'AMD Ryzen 9 9950X 16-Core Processor';
    const nestedOn = Boolean(getProcessorField('ExposeVirtualizationExtensions')?.value);
    const smtVal = getProcessorField('SmtMode')?.value || 'Inherit';
    const htText = smtVal === 'MultiThread' ? '开启' : smtVal === 'SingleThread' ? '关闭' : '自动';
    const htTitle =
      smtVal === 'MultiThread'
        ? '同时多线程 (SMT)：多线程'
        : smtVal === 'SingleThread'
          ? '同时多线程 (SMT)：单线程'
          : '同时多线程 (SMT)：继承宿主（自动）';
    return {
      hostCpuModel,
      nestedText: nestedOn ? '开启' : '关闭',
      htText,
      htTitle
    };
  }

  function buildCpuCanvasHtml(vm, t, chartHeight = 84) {
    const meta = getCpuSummaryMeta();
    const coreCards = t.coreSamples
      .map((s, i) => createUsageChartHtml(`${i}`, vm.running, `${s.base}%`, s.pts, false, chartHeight))
      .join('');
    return `
      <div class="dt-core-grid">${coreCards}</div>
      <div class="dt-kv-grid is-title-first">
        <div class="dt-kv-item">
          <span class="dt-kv-title">嵌套虚拟化</span>
          <span class="dt-kv-state">${meta.nestedText}</span>
        </div>
        <div class="dt-kv-item" title="${meta.htTitle}">
          <span class="dt-kv-title">超线程</span>
          <span class="dt-kv-state">${meta.htText}</span>
        </div>
      </div>
    `;
  }

  function getMemoryField(fieldId) {
    for (const sec of MEMORY_SECTIONS) {
      const hit = sec.fields?.find((f) => f.id === fieldId);
      if (hit) return hit;
    }
    return null;
  }

  function formatMibCapacity(mib) {
    const n = Number(mib) || 0;
    if (n >= 1024) {
      const gb = n / 1024;
      return `${Number.isInteger(gb) ? gb : gb.toFixed(1)} GB`;
    }
    return `${Math.round(n)} MB`;
  }

  function getMemorySummaryMeta(vm, t) {
    const dynamicEnabled = Boolean(getMemoryField('DynamicMemoryEnabled')?.value);
    const minMib = Number(getMemoryField('Minimum')?.value ?? 512);
    const maxMib = Number(getMemoryField('Maximum')?.value ?? 16384);
    const demandMib = vm.running ? Math.round((vm.usedMemoryGb ?? 1.4) * 1024) : 0;
    const smartPagingActive = Boolean(vm.running && dynamicEnabled && state.memSmartPagingAlert);

    return {
      dynamicEnabled,
      rangeText: `${formatMibCapacity(minMib)} - ${formatMibCapacity(maxMib)}`,
      pressureText: vm.running ? `${t.memPct}%` : '---',
      demandText: vm.running ? formatMibCapacity(demandMib) : '---',
      smartPagingActive,
      smartPagingText: smartPagingActive ? '256 MB · 使用中' : ''
    };
  }

  function buildMemCanvasHtml(vm, t, chartHeight = 184) {
    const meta = getMemorySummaryMeta(vm, t);
    const chart = createUsageChartHtml(
      '内存使用趋势',
      vm.running,
      `${t.memPct}%`,
      [58, 60, 62, 65, 67, 69, t.memPct],
      false,
      chartHeight
    );
    const itemDefs = [
      {
        title: '动态内存',
        value: meta.dynamicEnabled ? '开启' : '关闭',
        tooltip: '动态内存开关 (DynamicMemoryEnabled)'
      }
    ];
    if (meta.dynamicEnabled) {
      itemDefs.push(
        {
          title: '内存范围',
          value: meta.rangeText,
          tooltip: '动态内存上下限 (Minimum ~ Maximum)'
        },
        {
          title: '内存压力',
          value: meta.pressureText,
          tooltip: '内存压力百分比 (PressurePercent / CurrentPressure)'
        },
        {
          title: '内存需求',
          value: meta.demandText,
          tooltip: '当前内存需求估算 (DemandMiB)'
        }
      );
      if (meta.smartPagingActive) {
        itemDefs.push({
          title: '智能分页',
          value: meta.smartPagingText,
          tooltip: '正在使用磁盘智能分页文件 (SmartPagingMiB > 0)',
          caution: true
        });
      }
    }
    const kvItems = itemDefs.map(
      (item) => `
        <div class="dt-kv-item" title="${item.tooltip}">
          <span class="dt-kv-title">${item.title}</span>
          <span class="dt-kv-state${item.caution ? ' is-caution' : ''}">${item.value}</span>
        </div>
      `
    );
    return `
      ${chart}
      <div class="dt-kv-grid is-title-first">${kvItems.join('')}</div>
    `;
  }

  function resolveLiveGpuTelemetry(gpu, idx = 0) {
    const hwGpu = latestVirtualHw?.gpus?.[idx] || latestVirtualHw?.gpus?.[0];
    if (hwGpu && gpu && (!gpu.name || gpu.name === hwGpu.name || idx === 0)) {
      return {
        pct: typeof hwGpu.usagePct === 'number' ? hwGpu.usagePct : 18,
        pts3d: Array.isArray(hwGpu.pts3d) && hwGpu.pts3d.length > 1 ? hwGpu.pts3d : [10, 14, 22, 16, 19, 18],
        ptsCopy: Array.isArray(hwGpu.ptsCopy) && hwGpu.ptsCopy.length > 1 ? hwGpu.ptsCopy : [2, 3, 5, 4, 4, 4],
        ptsEnc: Array.isArray(hwGpu.ptsEnc) && hwGpu.ptsEnc.length > 1 ? hwGpu.ptsEnc : [0, 0, 0, 0, 0, 0],
        ptsDec: Array.isArray(hwGpu.ptsDec) && hwGpu.ptsDec.length > 1 ? hwGpu.ptsDec : [3, 5, 8, 6, 7, 6]
      };
    }
    return {
      pct: idx === 0 ? 18 : 6,
      pts3d: [10, 14, 22, 16, 19, 18],
      ptsCopy: [2, 3, 5, 4, 4, 4],
      ptsEnc: [0, 0, 0, 0, 0, 0],
      ptsDec: [3, 5, 8, 6, 7, 6]
    };
  }

  function buildGpuCanvasHtml(gpu, vm, chartHeight = 82, gpuIdx = 0) {
    if (!gpu) {
      return `<div style="opacity:0.65;font-size:13px;padding:18px 4px;">未分配显卡</div>`;
    }
    const active = Boolean(vm && vm.running);
    const live = resolveLiveGpuTelemetry(gpu, gpuIdx);
    const manuText =
      gpu.manu ||
      (gpu.name && gpu.name.includes('AMD')
        ? 'AMD'
        : gpu.name && gpu.name.includes('Intel')
          ? 'Intel'
          : 'NVIDIA');
    const vendorText = gpu.vendor || manuText;
    const vramText = (gpu.memoryText || '未知').replace(/,/g, '');
    const drvText = gpu.driverVersion || '---';
    const instText = gpu.instanceId || '---';
    const chartsHtml = `
      <div class="gpu-charts-grid">
        ${createUsageChartHtml('3D', active, `${live.pct}%`, live.pts3d, true, chartHeight)}
        ${createUsageChartHtml('数据复制', active, `${live.ptsCopy.at(-1) ?? 0}%`, live.ptsCopy, true, chartHeight)}
        ${createUsageChartHtml('视频编码', active, `${live.ptsEnc.at(-1) ?? 0}%`, live.ptsEnc, true, chartHeight)}
        ${createUsageChartHtml('视频解码', active, `${live.ptsDec.at(-1) ?? 0}%`, live.ptsDec, true, chartHeight)}
      </div>
    `;
    const kvItems = [
      renderTitleFirstKvItemHtml({
        title: '芯片厂商',
        stateText: manuText,
        copyVal: manuText && manuText !== '---' ? manuText : ''
      }),
      renderTitleFirstKvItemHtml({
        title: '板卡厂商',
        stateText: vendorText,
        copyVal: vendorText && vendorText !== '---' ? vendorText : ''
      }),
      renderTitleFirstKvItemHtml({
        title: '专用显存',
        stateText: vramText,
        copyVal: vramText && vramText !== '---' ? vramText : ''
      }),
      renderTitleFirstKvItemHtml({
        title: '驱动版本',
        stateText: drvText,
        copyVal: drvText !== '---' ? drvText : ''
      }),
      renderTitleFirstKvItemHtml({
        title: '设备实例路径',
        stateText: instText,
        copyVal: instText !== '---' ? instText : '',
        wide: true
      })
    ];
    return `
      ${chartsHtml}
      <div class="dt-kv-grid is-title-first is-2col">${kvItems.join('')}</div>
    `;
  }

  function resolveDiskViewModel(disk, idx, vm) {
    const slot = disk.controllerLoc || `SCSI 0:${idx}`;
    const mode = idx === 0 ? state.diskPreviewMode : 'virtual';

    if (mode === 'physical-cd') {
      const cdModel = 'ASUS BW-16D1HT Blu-ray Drive';
      const cdPath = '\\\\.\\CDROM0';
      const cdSerial = 'ASUS-BW16D1HT-882A';
      const readRate = '8.2 MB/s';
      const writeRate = '0 KB/s';
      return {
        isPhysical: true,
        isIso: true,
        isDifferencing: false,
        isStale: false,
        icon: '&#xE958;',
        slot,
        diskNumber: 0,
        serialNumber: cdSerial,
        sourceState: '物理光驱',
        rightTitle: '序列号',
        rightState: cdSerial,
        rightCopy: cdSerial,
        title: cdModel,
        fileSizeText: '4.7 GB',
        capacityText: '4.7 GB',
        pctText: '100%',
        headerReading: '4.7 GB',
        railStat: vm.running ? `读 ${readRate} 写 ${writeRate}` : '',
        railTip: `${cdModel}\n${cdPath}`,
        readRate,
        writeRate,
        readIops: 120,
        writeIops: 0,
        readPts: [6, 14, 18, 12, 16, 14],
        writePts: [0, 0, 0, 0, 0, 0],
        parentPath: '',
        pathTitle: '路径',
        pathText: cdPath,
        pathCopy: cdPath
      };
    }

    if (mode === 'physical' || mode === 'stale') {
      const p = disk.physicalInfo || {
        model: 'WDC SN850X 1000GB',
        diskNumber: 2,
        serialNumber: 'WD-WCC6Y4K8921A',
        capacityText: '1.0 TB',
        readRate: '24.6 MB/s',
        writeRate: '8.4 MB/s',
        readIops: 410,
        writeIops: 96
      };
      const isStale = mode === 'stale';
      const physPath = `\\\\.\\PhysicalDrive${p.diskNumber}`;
      return {
        isPhysical: true,
        isIso: false,
        isDifferencing: false,
        isStale,
        icon: '&#xEDA2;',
        slot,
        diskNumber: p.diskNumber,
        serialNumber: p.serialNumber,
        sourceState: isStale ? '物理磁盘（脱机）' : '物理磁盘',
        rightTitle: '序列号',
        rightState: p.serialNumber,
        rightCopy: p.serialNumber,
        title: isStale ? `PhysicalDrive${p.diskNumber}` : p.model,
        fileSizeText: p.capacityText,
        capacityText: p.capacityText,
        pctText: '100%',
        headerReading: isStale ? '已脱机' : p.capacityText,
        railStat: isStale ? '已脱机' : vm.running ? `读 ${p.readRate} 写 ${p.writeRate}` : '',
        railTip: `${p.model}\n${physPath}`,
        readRate: p.readRate,
        writeRate: p.writeRate,
        readIops: p.readIops,
        writeIops: p.writeIops,
        readPts: [14, 28, 22, 36, 26, 30],
        writePts: [8, 14, 10, 18, 12, 16],
        diskTypeState: isStale ? '物理直通（脱机）' : '物理直通',
        parentPath: '',
        pathTitle: '路径',
        pathText: physPath,
        pathCopy: isStale ? '' : physPath
      };
    }

    if (mode === 'iso') {
      const isoName = 'Win11_24H2_Chinese_Simplified_x64.iso';
      const isoPath = 'E:\\ISO\\Win11_24H2_Chinese_Simplified_x64.iso';
      const isoSize = '5.4 GB';
      const readRate = '12.4 MB/s';
      const writeRate = '0 KB/s';
      return {
        isPhysical: false,
        isIso: true,
        isDifferencing: false,
        isStale: false,
        icon: '&#xE958;',
        slot,
        sourceState: '虚拟光驱',
        rightTitle: '',
        rightState: '',
        rightCopy: '',
        title: isoName,
        fileSizeText: isoSize,
        capacityText: isoSize,
        pctText: '只读',
        headerReading: isoSize,
        railStat: vm.running ? `读 ${readRate} 写 ${writeRate}` : '',
        railTip: `${isoName}\n${isoPath}`,
        readRate,
        writeRate,
        readIops: 280,
        writeIops: 0,
        readPts: [10, 24, 32, 18, 26, 22],
        writePts: [0, 0, 0, 0, 0, 0],
        diskTypeState: '',
        parentPath: '',
        pathTitle: '路径',
        pathText: isoPath,
        pathCopy: isoPath
      };
    }

    if (mode === 'differencing') {
      const diffName = 'win11_dev_diff.vhdx';
      const diffPath = 'D:\\Hyper-V\\VMs\\win11_dev_diff.vhdx';
      const parentPath = 'D:\\Hyper-V\\Base\\Win11_24H2_Base.vhdx';
      const fileSizeText = '4.2 GB';
      const capacityText = '128 GB';
      const pctText = '3.3%';
      const readRate = '3.6 MB/s';
      const writeRate = '860 KB/s';
      return {
        isPhysical: false,
        isIso: false,
        isDifferencing: true,
        isStale: false,
        icon: '&#xEDA2;',
        slot,
        sourceState: '虚拟磁盘',
        rightTitle: '类型',
        rightState: '差分',
        rightCopy: '',
        title: diffName,
        fileSizeText,
        capacityText,
        pctText,
        headerReading: `${fileSizeText} / ${capacityText}`,
        railStat: vm.running ? `读 ${readRate} 写 ${writeRate}` : '',
        railTip: `${diffName}\n${diffPath}`,
        readRate,
        writeRate,
        readIops: 186,
        writeIops: 64,
        readPts: [12, 22, 16, 28, 19, 21],
        writePts: [6, 12, 9, 15, 11, 10],
        diskFormatState: 'VHDX',
        diskTypeState: '差分',
        parentPath,
        pathTitle: '路径',
        pathText: diffPath,
        pathCopy: diffPath
      };
    }

    const forceFixed = mode === 'fixed';
    const rawType = forceFixed ? '固定大小' : disk.vhdFormat || disk.subtitle || '动态扩展';
    const isFixed = forceFixed || rawType.includes('固定');
    const isDiff = rawType.includes('差分');
    const diskTypeState = isFixed ? '固定大小' : isDiff ? '差分' : '动态扩展';
    const ext = (disk.path || '').toLowerCase().endsWith('.vhd') ? 'VHD' : 'VHDX';
    const fileSizeText = isFixed ? disk.capacityText : disk.fileSizeText;
    const capacityText = disk.capacityText;
    const ratio = isFixed ? 1 : disk.ratio || 0;
    const pctNum = ratio * 100;
    const pctText = `${pctNum < 1 && pctNum > 0 ? pctNum.toFixed(1) : Math.round(pctNum)}%`;
    const readRate = disk.readRate || '1.2 MB/s';
    const writeRate = disk.writeRate || '420 KB/s';

    return {
      isPhysical: false,
      isIso: false,
      isDifferencing: isDiff,
      isStale: false,
      icon: '&#xEDA2;',
      slot,
      sourceState: '虚拟磁盘',
      rightTitle: '类型',
      rightState: diskTypeState,
      rightCopy: '',
      title: disk.title,
      fileSizeText,
      capacityText,
      pctText,
      headerReading: `${fileSizeText} / ${capacityText}`,
      railStat: vm.running ? `读 ${readRate} 写 ${writeRate}` : '',
      railTip: `${disk.title}\n${disk.path}`,
      readRate,
      writeRate,
      readIops: disk.readIops ?? 142,
      writeIops: disk.writeIops ?? 38,
      readPts: [8, 18, 12, 24, 15, 14],
      writePts: [4, 9, 6, 12, 8, 7],
      diskFormatState: ext,
      diskTypeState,
      parentPath: '',
      pathTitle: '路径',
      pathText: disk.path,
      pathCopy: disk.path
    };
  }

  function renderTitleFirstKvItemHtml({ title, stateText, caution = false, wide = false, copyVal = '' }) {
    const itemCls = wide ? 'dt-kv-item is-wide' : 'dt-kv-item';
    const stateCls = `${caution ? 'dt-kv-state is-caution' : 'dt-kv-state'}${wide ? ' tc-mid-fit' : ''}`;
    const copyBtn = copyVal
      ? `<button class="dt-kv-head-copy" data-copy="${copyVal}" title="点击复制：${copyVal}" type="button"><span class="fi" style="font-size:11.5px;">&#xE8C8;</span></button>`
      : '';
    return `
      <div class="${itemCls}">
        <div class="dt-kv-head">
          <span class="dt-kv-title">${title}</span>
          ${copyBtn}
        </div>
        <span class="${stateCls}" title="${stateText}">${stateText}</span>
      </div>
    `;
  }

  function buildDiskCanvasHtml(disk, idx, vm, chartHeight = 136) {
    const info = resolveDiskViewModel(disk, idx, vm);
    const active = vm.running && !info.isStale;
    const diffTopRight = info.isDifferencing && state.diffParentMode === 'top-right';
    const parentFileName = info.parentPath ? info.parentPath.split('\\').pop() : '';

    const kvItems = [
      renderTitleFirstKvItemHtml({
        title: '来源',
        stateText: info.sourceState,
        caution: info.isStale
      })
    ];

    if (diffTopRight) {
      kvItems.push(
        renderTitleFirstKvItemHtml({
          title: '父盘（差分）',
          stateText: parentFileName || info.parentPath,
          copyVal: info.parentPath
        })
      );
    } else if (info.rightTitle) {
      kvItems.push(
        renderTitleFirstKvItemHtml({
          title: info.rightTitle,
          stateText: info.rightState,
          copyVal: info.rightCopy
        })
      );
    }

    if (info.isDifferencing && info.parentPath && !diffTopRight) {
      kvItems.push(
        renderTitleFirstKvItemHtml({
          title: '父盘路径',
          stateText: info.parentPath,
          copyVal: info.parentPath,
          wide: true
        })
      );
    }

    // 路径始终独占最底下一整行 (wide: true)
    kvItems.push(
      renderTitleFirstKvItemHtml({
        title: info.pathTitle,
        stateText: info.pathText,
        copyVal: info.pathCopy,
        wide: true
      })
    );

    const chartsHtml =
      info.isIso && state.isoChartMode === 'single'
        ? createUsageChartHtml('读取', active, info.readRate, info.readPts, false, chartHeight)
        : `
          <div class="transfer-charts-grid">
            ${createUsageChartHtml('读取', active, info.readRate, info.readPts, false, chartHeight)}
            ${createUsageChartHtml('写入', active && !info.isIso, info.writeRate, info.writePts, false, chartHeight)}
          </div>
        `;

    return `
      ${chartsHtml}
      <div class="dt-kv-grid is-title-first is-2col">${kvItems.join('')}</div>
    `;
  }

  function formatMacAddress(raw = '') {
    const clean = String(raw).replace(/[^0-9a-fA-F]/g, '').toUpperCase();
    if (clean.length === 12) {
      return clean.match(/.{1,2}/g).join(':');
    }
    return raw || '00:15:5D:01:2A:0B';
  }

  function resolveNetViewModel(net, vm = getVm()) {
    const mode = state.netPreviewMode || 'connected';
    const isRunning = Boolean(vm && vm.running);
    let isConnected = true;
    let switchName = (net && net.subtitle) || 'Default Switch';
    let switchType = 'NAT';
    let macAddress = formatMacAddress((net && net.mac) || '00:15:5D:01:2A:0B');
    let macMode = '动态';
    let ipv4List = isRunning ? [(net && net.ipv4) || vm.ipWhenRunning || '172.24.118.94'] : [];
    let ipv6List = isRunning
      ? ['fd00:172:24::94', (net && net.ipv6) || vm.ipv6WhenRunning || 'fe80::215:5dff:fe01:2a0b']
      : [];
    let vlanText = '未启用';
    let sendRate = isRunning ? (net && net.txRate) || '48 KB/s' : '0 KB/s';
    let recvRate = isRunning ? (net && net.rxRate) || '1.2 MB/s' : '0 KB/s';
    let sendPts = [10, 14, 12, 18, 15, 13];
    let recvPts = [16, 28, 22, 38, 30, 26];

    if (mode === 'bridge') {
      switchName = 'External-LAN';
      switchType = '外部桥接';
      macAddress = '00:15:5D:01:2A:0B';
      macMode = '静态';
      ipv4List = isRunning ? ['192.168.1.128'] : [];
      ipv6List = isRunning
        ? ['240e:3b0:1280:1::128', '240e:3b0:1280:1:8c4a:91ff:fe2b:41a0', 'fe80::215:5dff:fe01:2a0b']
        : [];
      vlanText = 'VLAN 20';
      sendRate = isRunning ? '860 KB/s' : '0 KB/s';
      recvRate = isRunning ? '14.5 MB/s' : '0 KB/s';
      sendPts = [18, 26, 22, 34, 28, 24];
      recvPts = [28, 46, 38, 62, 52, 48];
    } else if (mode === 'disconnected') {
      isConnected = false;
      switchName = '';
      switchType = '—';
      macAddress = '00:15:5D:01:2A:0B';
      macMode = '动态';
      ipv4List = [];
      ipv6List = [];
      vlanText = '未启用';
      sendRate = '0 KB/s';
      recvRate = '0 KB/s';
      sendPts = [6, 6, 6, 6, 6, 6];
      recvPts = [6, 6, 6, 6, 6, 6];
    }

    const activeChart = isRunning && isConnected;
    const railSub = isConnected ? switchName : '未连接';
    const railStat = activeChart ? `发 ${sendRate} 收 ${recvRate}` : '';
    const headerMode = state.netHeaderMode || 'switch-ip';
    const headerTitle =
      headerMode === 'adapter-switch' ? '网络适配器' : isConnected ? switchName : '未连接';
    const headerReading =
      headerMode === 'adapter-switch'
        ? isConnected
          ? switchName
          : '未连接'
        : '';

    return {
      isConnected,
      switchName,
      switchType,
      macAddress,
      macMode,
      ipv4List,
      ipv6List,
      vlanText,
      sendRate,
      recvRate,
      sendPts,
      recvPts,
      activeChart,
      railSub,
      railStat,
      headerTitle,
      headerReading
    };
  }

  function renderNetIpKvItemHtml({ title, ipList = [], wide = false }) {
    const cls = wide ? 'dt-kv-item is-wide' : 'dt-kv-item';
    if (!ipList || ipList.length === 0) {
      return renderTitleFirstKvItemHtml({
        title,
        stateText: '未获取',
        wide
      });
    }
    if (ipList.length === 1) {
      const onlyIp = ipList[0];
      return renderTitleFirstKvItemHtml({
        title,
        stateText: onlyIp,
        copyVal: onlyIp,
        wide
      });
    }
    const rowsHtml = ipList
      .map(
        (ip) => `
        <div class="dt-kv-multi-row">
          <span class="dt-kv-multi-val">${escapeHtmlText(ip)}</span>
          <button class="dt-kv-head-copy" type="button" data-copy="${escapeHtmlText(ip)}" title="复制 ${escapeHtmlText(ip)}">
            <span class="fi" style="font-size:11px;">&#xE8C8;</span>
          </button>
        </div>
      `
      )
      .join('');
    return `
      <div class="${cls}">
        <div class="dt-kv-head">
          <span class="dt-kv-title">${escapeHtmlText(title)}</span>
        </div>
        <div class="dt-kv-multi-list">${rowsHtml}</div>
      </div>
    `;
  }

  function buildNetCanvasHtml(net, vm, chartHeight = 136) {
    const info = resolveNetViewModel(net, vm);
    const effectiveHeight = state.singleChartHeight || chartHeight || 136;
    const cand = state.netCandidate || 'a';
    const kvItems = [];

    if ( cand === 'a' ) {
      // 方案 A（推荐 · 2上1下同磁盘节奏）：顶部标题已是 Default Switch / 未连接
      // 左上 IPv4 地址 📋 | 右上 MAC 地址 📋 | 下方通栏 IPv6 地址（支持多条各自带 📋）
      kvItems.push(
        renderNetIpKvItemHtml({
          title: 'IPv4 地址',
          ipList: info.ipv4List,
          wide: false
        }),
        renderTitleFirstKvItemHtml({
          title: 'MAC 地址',
          stateText: info.macAddress,
          copyVal: info.macAddress
        }),
        renderNetIpKvItemHtml({
          title: 'IPv6 地址',
          ipList: info.ipv6List,
          wide: true
        })
      );
    } else if (cand === 'b') {
      // 方案 B：左上 虚拟交换机（未连接时为 连接状态：未连接）| 右上 MAC 地址 📋 + 下方 IPv4 通栏 + IPv6 通栏
      kvItems.push(
        renderTitleFirstKvItemHtml({
          title: info.isConnected ? '虚拟交换机' : '连接状态',
          stateText: info.isConnected ? info.switchName : '未连接'
        }),
        renderTitleFirstKvItemHtml({
          title: 'MAC 地址',
          stateText: info.macAddress,
          copyVal: info.macAddress
        }),
        renderNetIpKvItemHtml({
          title: 'IPv4 地址',
          ipList: info.ipv4List,
          wide: true
        }),
        renderNetIpKvItemHtml({
          title: 'IPv6 地址',
          ipList: info.ipv6List,
          wide: true
        })
      );
    } else {
      // 方案 C（带 VLAN 与动态/静态 MAC）：左上 网络类型/状态 | 右上 VLAN + 左中 IPv4 | 右中 MAC + 下方 IPv6 通栏
      kvItems.push(
        renderTitleFirstKvItemHtml({
          title: info.isConnected ? '网络模式' : '连接状态',
          stateText: info.isConnected ? info.switchType : '未连接'
        }),
        renderTitleFirstKvItemHtml({
          title: 'VLAN',
          stateText: info.vlanText
        }),
        renderNetIpKvItemHtml({
          title: 'IPv4 地址',
          ipList: info.ipv4List,
          wide: false
        }),
        renderTitleFirstKvItemHtml({
          title: 'MAC 地址',
          stateText: `${info.macAddress} (${info.macMode})`,
          copyVal: info.macAddress
        }),
        renderNetIpKvItemHtml({
          title: 'IPv6 地址',
          ipList: info.ipv6List,
          wide: true
        })
      );
    }

    return `
      <div class="transfer-charts-grid">
        ${createUsageChartHtml('发送', info.activeChart, info.sendRate, info.sendPts, false, effectiveHeight)}
        ${createUsageChartHtml('接收', info.activeChart, info.recvRate, info.recvPts, false, effectiveHeight)}
      </div>
      <div class="dt-kv-grid is-title-first is-2col">${kvItems.join('')}</div>
    `;
  }

  function clampTcSelection(sel, vm, gpuAssigns) {
    if (sel.kind === 'gpu' && (!gpuAssigns.length || sel.idx >= gpuAssigns.length)) {
      return { kind: gpuAssigns.length ? 'gpu' : 'cpu', idx: 0 };
    }
    if (sel.kind === 'disk' && sel.idx >= vm.disks.length) {
      return { kind: 'disk', idx: Math.max(0, vm.disks.length - 1) };
    }
    if (sel.kind === 'net' && sel.idx >= vm.adapters.length) {
      return { kind: 'net', idx: Math.max(0, vm.adapters.length - 1) };
    }
    return sel;
  }

  function renderTcDetailCardHtml(sel, vm, gpuAssigns, t) {
    let icon = '&#xF58E;';
    let rotate = 90;
    let title = getCpuSummaryMeta().hostCpuModel;
    let pill = '';
    let sub = '';
    let reading = vm.running ? '24.8%' : '0%';
    let cfgTab = 'processor';
    let cfgDev = '';
    let cfgLabel = '处理器';
    let bodyHtml = buildCpuCanvasHtml(vm, t, 84);

    const chartH = state.singleChartHeight || 136;

    if (sel.kind === 'mem') {
      icon = '&#xEEA0;';
      rotate = 0;
      title = '内存';
      pill = '';
      sub = '';
      reading = `${t.usedMemText} / ${t.totalMemText} GB`;
      cfgTab = 'memory';
      cfgLabel = '内存';
      bodyHtml = buildMemCanvasHtml(vm, t, chartH);
    } else if (sel.kind === 'gpu') {
      const gIdx = gpuAssigns[sel.idx] ? sel.idx : 0;
      const g = gpuAssigns[gIdx];
      const live = resolveLiveGpuTelemetry(g, gIdx);
      icon = '&#xF211;';
      rotate = 0;
      title = g ? g.name : '未分配显卡';
      pill = '';
      sub = '';
      reading = vm.running && g ? `${live.pct}%` : g ? '0%' : '';
      cfgTab = 'devices';
      cfgDev = 'gpu';
      cfgLabel = '显卡';
      bodyHtml = buildGpuCanvasHtml(g, vm, 82, gIdx);
    } else if (sel.kind === 'disk') {
      const diskIdx = vm.disks[sel.idx] ? sel.idx : 0;
      const d = vm.disks[diskIdx] || vm.disks[0];
      const dInfo = resolveDiskViewModel(d, diskIdx, vm);
      icon = dInfo.icon || '&#xEDA2;';
      rotate = 0;
      title = dInfo.title;
      pill = '';
      sub = '';
      reading = dInfo.headerReading;
      cfgTab = 'devices';
      cfgDev = 'storage';
      cfgLabel = '存储';
      bodyHtml = buildDiskCanvasHtml(d, diskIdx, vm, chartH);
    } else if (sel.kind === 'net') {
      const n = vm.adapters[sel.idx] || vm.adapters[0];
      const netInfo = resolveNetViewModel(n, vm);
      icon = '&#xE774;';
      rotate = 0;
      title = netInfo.headerTitle;
      pill = '';
      sub = '';
      reading = netInfo.headerReading;
      cfgTab = 'devices';
      cfgDev = 'network';
      cfgLabel = '网卡';
      bodyHtml = buildNetCanvasHtml(n, vm, chartH);
    }

    const headIconBoxHtml = `<div class="tc-head-icon-box">
      <span class="fi" style="${rotate ? `transform:rotate(${rotate}deg);` : ''}">${icon}</span>
    </div>`;
    const identitySingleLine = !sub;
    const isCpu = sel.kind === 'cpu';
    const titleHtml = isCpu ? formatCpuTitleHtml(title) : escapeHtmlText(title);
    const titleTooltip = isCpu ? ` title="${escapeHtmlText(String(title).replace(/\r?\n/g, ' + '))}"` : '';

    return `
      <div class="tc-right-card">
        <div class="tc-canvas-head">
          <div class="tc-canvas-identity ${identitySingleLine ? 'is-single-line' : ''}">
            ${headIconBoxHtml}
            <div class="tc-canvas-title-wrap">
              <div class="tc-canvas-title-row">
                <span class="tc-canvas-title ${isCpu ? 'is-model-title' : ''}"${titleTooltip}>${titleHtml}</span>
                ${pill ? `<span class="tc-kind-pill">${pill}</span>` : ''}
              </div>
              ${sub ? `<div class="tc-canvas-sub">${sub}</div>` : ''}
            </div>
          </div>
          <div class="tc-canvas-head-right">
            <div class="tc-canvas-reading">${reading}</div>
            <button class="tc-cfg-icon-btn" data-jump-tab="${cfgTab}" data-jump-dev="${cfgDev}" title="配置${cfgLabel}" aria-label="配置${cfgLabel}">
              <span class="fi">&#xE713;</span>
            </button>
          </div>
        </div>
        <div class="tc-canvas-body">${bodyHtml}</div>
      </div>
    `;
  }

  // Win11 实时波形双栏：开启主题色时为波形 2（主题强调色），关闭主题色时自动切为波形 1（Win11 原生分类配色）
  function renderOverviewTmRail(altHost, vm, gpuAssigns, accentMode = false) {
    const t = getOverviewTelemetry(vm, gpuAssigns);
    state.tcSelected = clampTcSelection(state.tcSelected, vm, gpuAssigns);
    const isSel = (kind, idx = 0) => state.tcSelected.kind === kind && state.tcSelected.idx === idx;
    const strokeOf = (fallback) => (accentMode ? 'var(--VmAccentBrush)' : fallback);
    const fillOf = (fallback) => (accentMode ? 'var(--VmChartFillBrush)' : fallback);

    const rows = [
      {
        kind: 'cpu',
        idx: 0,
        title: '处理器',
        sub: '',
        stat: `${vm.running ? '24.8%' : '0%'} ${vm.cores} 核心`,
        pts: [18, 22, 20, 29, 26, 25, 25],
        color: '#3898EC',
        fill: 'rgba(56, 152, 236, 0.22)',
        tip: `处理器 (${vm.cores} 核)`
      },
      {
        kind: 'mem',
        idx: 0,
        title: '内存',
        sub: '',
        stat: `${t.memPct}% ${t.usedMemText}/${t.totalMemText} GB`,
        pts: [58, 60, 62, 65, 67, 69, t.memPct],
        color: '#9D5CE6',
        fill: 'rgba(157, 92, 230, 0.22)',
        tip: `内存 ${t.totalMemText} GB`
      }
    ];

    vm.disks.forEach((d, idx) => {
      const dInfo = resolveDiskViewModel(d, idx, vm);
      rows.push({
        kind: 'disk',
        idx,
        title: vm.disks.length > 1 ? `磁盘 ${idx}` : '磁盘',
        sub: dInfo.title,
        stat: dInfo.railStat,
        pts: dInfo.readPts,
        color: dInfo.isStale ? '#D8A334' : '#4BA63A',
        fill: dInfo.isStale ? 'rgba(216, 163, 52, 0.22)' : 'rgba(75, 166, 58, 0.22)',
        tip: dInfo.railTip
      });
    });

    vm.adapters.forEach((n, idx) => {
      const nInfo = resolveNetViewModel(n, vm);
      rows.push({
        kind: 'net',
        idx,
        title: vm.adapters.length > 1 ? `网卡 ${idx}` : '网卡',
        sub: nInfo.railSub,
        stat: nInfo.railStat,
        pts: nInfo.recvPts,
        color: '#D946EF',
        fill: 'rgba(217, 70, 239, 0.22)',
        tip: `${n.title} — ${nInfo.railSub}`
      });
    });

    gpuAssigns.forEach((g, idx) => {
      const live = resolveLiveGpuTelemetry(g, idx);
      rows.push({
        kind: 'gpu',
        idx,
        title: gpuAssigns.length > 1 ? `GPU ${idx}` : 'GPU',
        sub: g.name,
        stat: vm.running ? `${live.pct}%` : '0%',
        pts: live.pts3d,
        color: '#8B5CF6',
        fill: 'rgba(139, 92, 246, 0.24)',
        tip: g.name
      });
    });

    const railHtml = rows
      .map(
        (r) => `
        <button class="tmw-item ${accentMode ? 'accent-mode' : ''} ${isSel(r.kind, r.idx) ? 'selected' : ''}" data-tc-select="${r.kind}:${r.idx}" title="${r.tip}">
          <div class="tmw-thumb">${renderSparkThumbSvg(r.pts, vm.running && (r.kind !== 'net' || resolveNetViewModel(null, vm).isConnected), strokeOf(r.color), fillOf(r.fill))}</div>
          <div class="tmw-info">
            <div class="tmw-title-row">
              <span class="tmw-title">${r.title}</span>
            </div>
            ${r.sub ? `<div class="tmw-sub tc-mid-fit">${r.sub}</div>` : ''}
            <div class="tmw-stat">${r.stat}</div>
          </div>
        </button>
      `
      )
      .join('');

    altHost.innerHTML = `
      <div class="tc-shell tm-wide">
        <div class="tc-left-rail">${railHtml}</div>
        ${renderTcDetailCardHtml(state.tcSelected, vm, gpuAssigns, t)}
      </div>
    `;
  }

  function bindDesktopAltEvents(altHost) {
    altHost.querySelectorAll('[data-copy]').forEach((btn) => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const val = btn.getAttribute('data-copy');
        if (!val) return;
        navigator.clipboard?.writeText(val).catch(() => {});
        showNotice(`已复制到剪贴板：${val}`);
      });
    });
    altHost.querySelectorAll('[data-tc-select]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const [kind, idxStr] = (btn.getAttribute('data-tc-select') || 'cpu:0').split(':');
        const idx = Number(idxStr) || 0;
        state.tcSelected = { kind, idx };
        if (kind === 'gpu') state.selectedGpuIdx = idx;
        if (kind === 'disk') state.selectedDiskIdx = idx;
        if (kind === 'net') state.selectedNetIdx = idx;
        renderOverview();
      });
    });
    altHost.querySelectorAll('[data-jump-tab]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const tab = btn.getAttribute('data-jump-tab');
        const dev = btn.getAttribute('data-jump-dev');
        if (!tab) return;
        state.activeTabKey = tab;
        if (tab === 'devices' && dev) {
          state.deviceSubKey = dev;
        }
        renderPageTabs();
        renderActiveWorkspace();
      });
    });
  }

  function renderOverview() {
    const { vm, gpuAssigns } = getEffectiveOverviewTarget();
    const origEl = document.getElementById('vmOverviewOriginal');
    const altHost = document.getElementById('vmOverviewAltHost');

    if (origEl && altHost) {
      const isOrig = state.overviewLayout === 'original';
      origEl.hidden = !isOrig;
      altHost.hidden = isOrig;
      altHost.classList.toggle('mode-fill', !isOrig);
      if (!isOrig) {
        renderOverviewTmRail(altHost, vm, gpuAssigns, state.useThemeAccentForHeader);
        bindDesktopAltEvents(altHost);
        layoutDynamicGrids();
        requestAnimationFrame(layoutDynamicGrids);
        return;
      }
    }

    const cpuCard = document.getElementById('metricCpuCard');
    const memCard = document.getElementById('metricMemCard');
    const cpuVal = document.getElementById('cpuReadingVal');
    const memVal = document.getElementById('memReadingVal');
    const resourceDetailsBox = document.getElementById('resourceDetailsBox');
    const coreCharts = document.getElementById('overviewCoreCharts');
    const memBox = document.getElementById('overviewMemoryBox');

    if (cpuCard && memCard) {
      cpuCard.classList.toggle('selected', state.selectedResource === 'cpu');
      memCard.classList.toggle('selected', state.selectedResource === 'mem');
    }

    if (cpuVal) {
      cpuVal.innerHTML = vm.running ? `24.8<span class="unit"> %</span>` : `0.0<span class="unit"> %</span>`;
    }
    if (memVal) {
      memVal.innerHTML = vm.running
        ? `${vm.usedMemoryGb.toFixed(1)} / ${vm.memoryGb.toFixed(1)}<span class="unit"> GB</span>`
        : `0.0 / 0.0<span class="unit"> GB</span>`;
    }

    if (coreCharts && memBox) {
      const showCpu = state.selectedResource === 'cpu';
      coreCharts.hidden = !showCpu;
      memBox.hidden = showCpu;

      if (showCpu) {
        const cards = [];
        for (let i = 0; i < vm.cores; i++) {
          const { base, pts } = getCoreSample(i);
          cards.push(createUsageChartHtml(`${i}`, vm.running, `${base}%`, pts, false, 104));
        }
        coreCharts.innerHTML = cards.join('');
      } else {
        // VmDashboardView.xaml.cs line 16: new("内存使用趋势", VmUsageChartKind.Memory)
        const memChartHost = document.getElementById('overviewMemoryChartHost');
        const pct = Math.round((vm.usedMemoryGb / vm.memoryGb) * 100);
        if (memChartHost) {
          memChartHost.innerHTML = createUsageChartHtml(
            '内存使用趋势',
            vm.running,
            `${pct}%`,
            [58, 60, 62, 65, 67, 69, pct],
            false,
            214
          );
        }
      }
    }
    layoutDynamicGrids();

    // DeviceTabs (VmDashboardView.Settings.cs lines 26-27: SetGlyphs("\uF211", "\uEDA2", "\uE774"))
    const devTabsBar = document.getElementById('deviceTabsBar');
    if (devTabsBar) {
      const devTabs = [
        { glyph: '&#xF211;', label: `显卡 ${gpuAssigns.length}` },
        { glyph: '&#xEDA2;', label: `存储 ${vm.disks.length}` },
        { glyph: '&#xE774;', label: `网卡 ${vm.adapters.length}` }
      ];
      devTabsBar.innerHTML = devTabs
        .map(
          (dt, idx) => `
          <button class="vm-tab-btn ${state.selectedDeviceTab === idx ? 'selected' : ''}" data-dev-tab="${idx}">
            <span class="fi" style="font-size:16px;">${dt.glyph}</span>
            <span>${dt.label}</span>
          </button>
        `
        )
        .join('');
      devTabsBar.querySelectorAll('[data-dev-tab]').forEach((btn) => {
        btn.addEventListener('click', () => {
          state.selectedDeviceTab = Number(btn.getAttribute('data-dev-tab'));
          renderOverview();
        });
      });
    }

    const gpuPanel = document.getElementById('devPanelGpu');
    const storagePanel = document.getElementById('devPanelStorage');
    const netPanel = document.getElementById('devPanelNetwork');
    if (gpuPanel && storagePanel && netPanel) {
      gpuPanel.hidden = state.selectedDeviceTab !== 0;
      storagePanel.hidden = state.selectedDeviceTab !== 1;
      netPanel.hidden = state.selectedDeviceTab !== 2;

      // Panel 0: GPU (VmDashboardView.Presentation.cs lines 12, 85-93)
      if (gpuAssigns.length === 0) {
        gpuPanel.innerHTML = `<div style="opacity:0.6;font-size:13px;margin:0 4px;">未分配显卡</div>`;
      } else {
        const selectors = gpuAssigns
          .map(
            (g, idx) => `
            <button class="vm-metric-card ${idx === 0 ? 'selected' : ''}">
              <span class="fi vm-metric-icon">&#xF211;</span>
              <div class="vm-metric-text-stack">
                <div class="vm-metric-title">${g.name}</div>
              </div>
              <div class="vm-metric-reading"></div>
            </button>
          `
          )
          .join('');
        const bodyHtml = !vm.running
          ? `<div style="opacity:0.6;font-size:13px;margin:0 4px;">虚拟机未启动</div>`
          : `
            <div class="gpu-charts-grid">
              ${createUsageChartHtml('3D', true, '18%', [10, 14, 22, 16, 19, 18], true, 148)}
              ${createUsageChartHtml('数据复制', true, '4%', [2, 3, 5, 4, 4, 4], true, 148)}
              ${createUsageChartHtml('视频编码', true, '0%', [0, 0, 0, 0, 0, 0], true, 148)}
              ${createUsageChartHtml('视频解码', true, '6%', [3, 5, 8, 6, 7, 6], true, 148)}
            </div>
          `;
        gpuPanel.innerHTML = `
          <div class="metric-selectors-grid">${selectors}</div>
          ${bodyHtml}
        `;
      }

      // Panel 1: Storage (VmDashboardView.Devices.cs lines 90-133, 149-175)
      const diskCards = vm.disks
        .map((d, idx) => {
          const sel = idx === state.selectedDiskIdx;
          return `
            <button class="vm-metric-card ${sel ? 'selected' : ''}" data-disk-idx="${idx}">
              <span class="fi vm-metric-icon">${d.glyph}</span>
              <div class="vm-metric-text-stack">
                <div class="vm-metric-title">${d.title}</div>
                <div class="vm-metric-subline">
                  <span class="vm-metric-subtitle">${d.subtitle}</span>
                  <span class="vm-metric-secreading">${d.fileSizeText} / ${d.capacityText}</span>
                </div>
              </div>
              <div class="vm-metric-reading"></div>
            </button>
          `;
        })
        .join('');
      const activeDisk = vm.disks[state.selectedDiskIdx] || vm.disks[0];
      const [fileNum, fileUnit] = activeDisk.fileSizeText.split(' ');
      const [capNum, capUnit] = activeDisk.capacityText.split(' ');
      storagePanel.innerHTML = `
        <div class="metric-selectors-grid">${diskCards}</div>
        <div style="display:flex;flex-direction:column;gap:12px;">
          <div>
            <div class="storage-capacity-line">
              <div><span class="cap-label">文件占用</span><span class="cap-num">${fileNum}</span> <span>${fileUnit}</span></div>
              <div><span class="cap-label">虚拟容量</span><span class="cap-num">${capNum}</span> <span>${capUnit}</span></div>
            </div>
            <div class="storage-capacity-meter">
              <div class="storage-capacity-fill" style="width:${(activeDisk.ratio * 100).toFixed(1)}%;"></div>
            </div>
          </div>
          <div class="transfer-charts-grid">
            ${createUsageChartHtml('读取', vm.running, '1.2 MB/s', [8, 18, 12, 24, 15, 14], false, 132)}
            ${createUsageChartHtml('写入', vm.running, '420 KB/s', [4, 9, 6, 12, 8, 7], false, 132)}
          </div>
        </div>
      `;
      storagePanel.querySelectorAll('[data-disk-idx]').forEach((btn) => {
        btn.addEventListener('click', () => {
          state.selectedDiskIdx = Number(btn.getAttribute('data-disk-idx'));
          renderOverview();
        });
      });

      // Panel 2: Network (VmDashboardView.Devices.cs lines 135-146, 207-277)
      const netCards = vm.adapters
        .map((n, idx) => {
          const sel = idx === state.selectedNetIdx;
          const secReading = vm.running ? '↑ 48 KB/s  ↓ 1.2 MB/s' : '';
          return `
            <button class="vm-metric-card ${sel ? 'selected' : ''}" data-net-idx="${idx}">
              <span class="fi vm-metric-icon">&#xE839;</span>
              <div class="vm-metric-text-stack">
                <div class="vm-metric-title">${n.title}</div>
                <div class="vm-metric-subline">
                  <span class="vm-metric-subtitle">${n.subtitle}</span>
                  <span class="vm-metric-secreading">${secReading}</span>
                </div>
              </div>
              <div class="vm-metric-reading"></div>
            </button>
          `;
        })
        .join('');
      const activeNet = vm.adapters[state.selectedNetIdx] || vm.adapters[0];
      const ipv4Html = vm.running
        ? `<button class="network-copy-btn" data-copy="${vm.ipWhenRunning}"><span>${vm.ipWhenRunning}</span><span class="fi" style="font-size:12px;opacity:0.5;">&#xE8C8;</span></button>`
        : `<span style="font-size:12px;opacity:0.8;">未获取</span>`;
      const ipv6Html = vm.running
        ? `<button class="network-copy-btn" data-copy="${vm.ipv6WhenRunning}"><span>${vm.ipv6WhenRunning}</span><span class="fi" style="font-size:12px;opacity:0.5;">&#xE8C8;</span></button>`
        : `<span style="font-size:12px;opacity:0.8;">未获取</span>`;

      netPanel.innerHTML = `
        <div class="metric-selectors-grid">${netCards}</div>
        <div class="network-addresses-stack">
          <div class="network-addresses-pair">
            <div class="network-addr-group">
              <span class="network-addr-title">MAC</span>
              <button class="network-copy-btn" data-copy="${activeNet.mac}">
                <span>${activeNet.mac}</span>
                <span class="fi" style="font-size:12px;opacity:0.5;">&#xE8C8;</span>
              </button>
            </div>
            <div class="network-addr-group">
              <span class="network-addr-title">IPv4</span>
              ${ipv4Html}
            </div>
          </div>
          <div class="network-addr-group">
            <span class="network-addr-title">IPv6</span>
            ${ipv6Html}
          </div>
        </div>
        <div class="transfer-charts-grid">
          ${createUsageChartHtml('发送', vm.running, '48 KB/s', [5, 8, 6, 11, 7, 6], false, 132)}
          ${createUsageChartHtml('接收', vm.running, '1.2 MB/s', [14, 28, 19, 34, 22, 20], false, 132)}
        </div>
      `;
      netPanel.querySelectorAll('[data-copy]').forEach((btn) => {
        btn.addEventListener('click', () => {
          const val = btn.getAttribute('data-copy');
          navigator.clipboard?.writeText(val).catch(() => {});
          showNotice(`已复制到剪贴板：${val}`);
        });
      });
    }
    requestAnimationFrame(layoutDynamicGrids);
  }

  // Offscreen canvas for VmMiddleText.Fit() (VmMetricCard.cs lines 76-99)
  const measureCanvas = document.createElement('canvas');
  const measureCtx = measureCanvas.getContext('2d');
  function measureTextPx(str, fontSize = 14) {
    if (!measureCtx) return str.length * fontSize;
    measureCtx.font = `${fontSize <= 12 ? 500 : 400} ${fontSize}px 'Segoe UI Variable Text', 'Segoe UI', 'Microsoft YaHei UI', sans-serif`;
    return measureCtx.measureText(str).width;
  }

  function fitMiddleText(el, fontSize = 14) {
    const fullText = el.getAttribute('data-full-text') ?? el.textContent.trim();
    if (!el.hasAttribute('data-full-text')) el.setAttribute('data-full-text', fullText);
    const availW = el.clientWidth;
    if (availW <= 4) {
      el.textContent = '';
      return;
    }
    if (measureTextPx(fullText, fontSize) <= availW) {
      el.textContent = fullText;
      return;
    }
    const chars = Array.from(fullText);
    function shorten(count) {
      const left = Math.floor((count + 1) / 2);
      const right = Math.floor(count / 2);
      return (
        (left === 0 ? '' : chars.slice(0, left).join('')) +
        '…' +
        (right === 0 ? '' : chars.slice(chars.length - right).join(''))
      );
    }
    let lo = 0;
    let hi = Math.max(0, chars.length - 1);
    while (lo < hi) {
      const mid = Math.floor((lo + hi + 1) / 2);
      if (measureTextPx(shorten(mid), fontSize) <= availW) lo = mid;
      else hi = mid - 1;
    }
    const candidate = shorten(lo);
    el.textContent = measureTextPx(candidate, fontSize) <= availW ? candidate : '';
  }

  let lastAutoNavMode = 'compact';

  // Exact C# Responsive Layout Engine (NavigationView Auto Mode, LayoutCoreCharts, LayoutDeviceSelectors, SettingsFieldPanel, ArrangeProcessors, VmMiddleText)
  function layoutDynamicGrids() {
    // 0. MainWindow.xaml lines 20-21: NavigationView PaneDisplayMode="Auto", default closed (compact)
    const navView = document.getElementById('winuiNavView');
    if (navView && winEl) {
      const winW = winEl.clientWidth || 1080;
      const targetMode = winW < 820 ? 'minimal' : 'compact';
      if (targetMode !== lastAutoNavMode) {
        lastAutoNavMode = targetMode;
        navView.classList.toggle('minimal', targetMode === 'minimal');
        navView.classList.toggle('compact', targetMode === 'compact');
        if (targetMode !== 'minimal') {
          navView.classList.remove('minimal-open');
          const backdrop = document.getElementById('navDrawerBackdrop');
          if (backdrop) backdrop.hidden = true;
        }
      }
      const vmPageGrid = document.getElementById('pageVirtualMachines');
      if (vmPageGrid) {
        vmPageGrid.classList.toggle('vm-compact-list', winW < 760);
      }
    }

    // Overview Dual-Column Narrow Adaptation
    const altHost = document.getElementById('vmOverviewAltHost');
    if (altHost) {
      const tcShell = altHost.querySelector('.tc-shell.tm-wide');
      if (tcShell) {
        const w = altHost.clientWidth || 0;
        tcShell.classList.toggle('tc-stacked', w > 0 && w < 560);
        tcShell.classList.toggle('tc-compact-rail', w >= 560 && w < 680);
      }
    }

    // Settings Narrow Adaptation
    const settingsArea = document.getElementById('vmSettingsArea');
    if (settingsArea && !settingsArea.classList.contains('no-subnav')) {
      const sw = settingsArea.clientWidth || 0;
      settingsArea.classList.toggle('settings-compact-subnav', sw > 0 && sw < 500);
    }

    // 1. VmDashboardView.Presentation.cs lines 34-55: LayoutCoreCharts()
    const resourceDetailsBox = document.getElementById('resourceDetailsBox');
    const coreCharts = document.getElementById('overviewCoreCharts');
    if (state.overviewLayout === 'original' && resourceDetailsBox && coreCharts) {
      const { vm } = getEffectiveOverviewTarget();
      const count = Math.max(1, vm.cores);
      const gap = 6;
      const minimumChartWidth = 104;
      const maximumColumns = 8;
      const actualW = resourceDetailsBox.clientWidth || 542;
      const limit = Math.max(
        1,
        Math.min(Math.min(maximumColumns, count), Math.floor((actualW + gap) / (minimumChartWidth + gap)))
      );
      const rows = Math.ceil(count / limit);
      const columns = Math.ceil(count / rows);
      const resourceHeight = rows * 104 + (rows - 1) * gap;
      coreCharts.style.gridTemplateColumns = `repeat(${columns}, minmax(0, 1fr))`;
      resourceDetailsBox.style.height = `${resourceHeight}px`;
      const memChartEl = document.querySelector('#overviewMemoryChartHost .vm-usage-chart');
      if (memChartEl) {
        memChartEl.style.height = `${resourceHeight}px`;
      }
    }

    // 2. VmDashboardView.Devices.cs lines 38-48: LayoutDeviceSelectors()
    document.querySelectorAll('.device-selector-grid').forEach((grid) => {
      const w = grid.clientWidth;
      if (w > 0) {
        const cols = w < 560 ? 1 : 2;
        grid.style.gridTemplateColumns = `repeat(${cols}, minmax(0, 1fr))`;
      }
    });

    // 3. SettingsFieldPanel.cs lines 9-12: Columns(double width) => width >= 210 * 2 + 16 ? 2 : 1
    document.querySelectorAll('.settings-field-panel').forEach((panel) => {
      const w = panel.clientWidth;
      if (w > 0) {
        const cols = w >= 436 ? 2 : 1;
        panel.style.gridTemplateColumns = `repeat(${cols}, minmax(0, 1fr))`;
      }
    });

    // 4. VmCpuAffinitySettingsView.cs lines 170-194: ArrangeProcessors()
    document.querySelectorAll('.affinity-cores-grid').forEach((grid) => {
      const w = grid.clientWidth;
      const count = grid.children.length || 16;
      if (w > 0 && count > 0) {
        let bestCols = Math.min(8, count);
        let bestScore = Infinity;
        for (let candidate = 4; candidate <= Math.min(10, count); candidate++) {
          const r = Math.ceil(count / candidate);
          const rem = (candidate - (count % candidate)) % candidate;
          const score = (2 * rem) / candidate + Math.abs(candidate / r - 1.5);
          if (score < bestScore) {
            bestScore = score;
            bestCols = candidate;
          }
        }
        const cols = Math.min(bestCols, Math.max(1, Math.floor((w + 8) / 64)));
        grid.style.gridTemplateColumns = `repeat(${cols}, minmax(56px, 1fr))`;
      }
    });

    // 5. VmMetricCard.cs lines 76-99: VmMiddleText.Fit()
    document.querySelectorAll('.vm-metric-title').forEach((el) => fitMiddleText(el));
    document.querySelectorAll('.tc-mid-fit').forEach((el) => fitMiddleText(el, 11.5));
  }

  // Helper: SettingLabel (VmSettingsView.cs lines 262-275: title on left, \uE946 32x32 info button on right)
  function renderSettingLabelHtml(title, desc) {
    const infoBtn = desc
      ? `<button class="setting-info-btn" data-info-desc="${desc.replace(/"/g, '&quot;')}" title="${desc.replace(/"/g, '&quot;')}"><span class="fi" style="font-size:15px;">&#xE946;</span></button>`
      : '';
    return `
      <div class="setting-label-grid">
        <span class="setting-label-title">${title}</span>
        ${infoBtn}
      </div>
    `;
  }

  // 5. Render Settings Area (处理器 / 内存 / 设备[显卡·存储·网卡·PCIe] / 检查点 / 高级[引导·安全·控制台·电源])
  function renderSubnavItems(subnav, items, activeId, onSelect) {
    subnav.innerHTML = items
      .map((item, idx) => {
        const id = item.key ?? String(idx);
        const selected = id === String(activeId);
        const dividerHtml = item.dividerBefore ? '<div class="subnav-divider" role="separator"></div>' : '';
        const iconHtml = item.glyph
          ? `<span class="fi ${item.fluent ? 'fi-fluent' : ''} subnav-icon">${item.glyph}</span>`
          : '';
        return `${dividerHtml}<button class="subnav-btn ${selected ? 'selected' : ''}" data-subnav-id="${id}">${iconHtml}<span>${getLocalizedLabel(item)}</span></button>`;
      })
      .join('');
    subnav.querySelectorAll('[data-subnav-id]').forEach((btn) => {
      btn.addEventListener('click', () => {
        onSelect(btn.getAttribute('data-subnav-id'));
      });
    });
  }

  function bindSettingsHostControls(host) {
    host.querySelectorAll('.winui-switch').forEach((sw) => {
      if (sw.dataset.boundSwitch || sw.dataset.customSwitch === 'true') return;
      sw.dataset.boundSwitch = 'true';
      sw.addEventListener('click', () => {
        if (sw.classList.contains('disabled')) return;
        sw.classList.toggle('on');
        const settingKey = sw.getAttribute('data-setting-key');
        if (settingKey && settingKey in state.advSettings) {
          state.advSettings[settingKey] = sw.classList.contains('on');
        }
        const procFieldId = sw.getAttribute('data-proc-field-id');
        if (procFieldId) {
          const procField = getProcessorField(procFieldId);
          if (procField) procField.value = sw.classList.contains('on');
        }
        const lbl = sw.querySelector('.switch-state-label');
        if (lbl) {
          const onTxt = sw.getAttribute('data-on-text') || '启用';
          const offTxt = sw.getAttribute('data-off-text') || '关闭';
          lbl.textContent = sw.classList.contains('on') ? onTxt : offTxt;
        }
      });
    });

    host.querySelectorAll('[data-copy]').forEach((btn) => {
      if (btn.dataset.copyBound) return;
      btn.dataset.copyBound = 'true';
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const val = btn.getAttribute('data-copy') || '';
        if (!val) return;
        navigator.clipboard?.writeText(val).catch(() => {});
        showNotice(`已复制到剪贴板：${val}`);
      });
    });

    host.querySelectorAll('[data-info-desc]').forEach((btn) => {
      if (btn.dataset.infoBound) return;
      btn.dataset.infoBound = 'true';
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const flyout = document.getElementById('settingInfoFlyout');
        if (!flyout) return;
        const desc = btn.getAttribute('data-info-desc') || '';
        const winRect = winEl.getBoundingClientRect();
        const btnRect = btn.getBoundingClientRect();
        flyout.textContent = desc;
        flyout.hidden = false;
        const left = Math.max(12, Math.min(winRect.width - 352, btnRect.left - winRect.left - 8));
        const top = btnRect.bottom - winRect.top + 6;
        flyout.style.left = `${left}px`;
        flyout.style.top = `${top}px`;
      });
    });
  }

  function renderSettingsArea() {
    const settingsArea = document.getElementById('vmSettingsArea');
    const subnav = document.getElementById('vmSettingsSubnav');
    const host = document.getElementById('vmSettingsHost');
    if (!settingsArea || !subnav || !host) return;

    const key = state.activeTabKey;
    const hasSubnav = key === 'processor' || key === 'memory' || key === 'devices' || key === 'advanced';
    settingsArea.classList.toggle('no-subnav', !hasSubnav);
    subnav.hidden = !hasSubnav;

    const viewRenderers = {
      gpu: renderGpuTab,
      storage: renderStorageTab,
      network: renderNetworkTab,
      serial: renderSerialTab,
      pcie: renderPcieTab,
      boot: renderBootTab,
      security: renderSecurityTab,
      console: renderConsoleTab,
      power: renderPowerTab,
      'storage-policy': renderStoragePolicyTab
    };

    if (key === 'processor') {
      renderSubnavItems(subnav, PROCESSOR_SECTIONS, state.processorSectionIdx, (id) => {
        state.processorSectionIdx = Number(id);
        host.scrollTop = 0;
        renderSettingsArea();
      });
      if (isComputeSectionDirty(state, getVm())) {
        const compBtn = subnav.querySelector('[data-subnav-id="0"]');
        if (compBtn && !compBtn.querySelector('.subnav-dirty-dot')) {
          const dot = document.createElement('span');
          dot.className = 'subnav-dirty-dot';
          compBtn.appendChild(dot);
        }
      }
      renderProcessorSection(host, PROCESSOR_SECTIONS[state.processorSectionIdx]);
    } else if (key === 'memory') {
      renderSubnavItems(subnav, MEMORY_SECTIONS, state.memorySectionIdx, (id) => {
        state.memorySectionIdx = Number(id);
        host.scrollTop = 0;
        renderSettingsArea();
      });
      renderMemorySection(host, MEMORY_SECTIONS[state.memorySectionIdx]);
    } else if (key === 'spacetime') {
      renderSpacetimeTab(host);
    } else if (key === 'devices' || key === 'advanced') {
      const items = MERGED_SUBNAV_GROUPS[key] || [];
      const subProp = key === 'devices' ? 'deviceSubKey' : 'advancedSubKey';
      const activeSubKey = items.some((it) => it.key === state[subProp]) ? state[subProp] : items[0]?.key;
      state[subProp] = activeSubKey;
      renderSubnavItems(subnav, items, activeSubKey, (id) => {
        state[subProp] = id;
        host.scrollTop = 0;
        renderActiveWorkspace();
      });
      const renderFn = viewRenderers[activeSubKey];
      if (renderFn) renderFn(host);
    }

    bindSettingsHostControls(host);
  }

  // Tab 1: 处理器 (VmProcessorSettingsView.cs + VmCpuAffinitySettingsView.cs)
  function renderProcessorSection(host, sec) {
    if (sec === PROCESSOR_SECTIONS[0]) {
      const vm = getVm();
      renderComputeSection(host, {
        state,
        vm,
        processorFields: sec.fields,
        onApplied: (saved) => {
          vm.configText = String(vm.configText || '').replace(/^\d+\s*核/, `${saved.Count} 核`);
          renderVmList();
          renderDashboardSummary();
        },
        showNotice,
        onLayoutNeeded: layoutDynamicGrids
      });
      return;
    }

    // Section 2: 核心绑定 (VmCpuAffinitySettingsView.cs lines 16-295)
    if (sec.kind === 'affinity') {
      if (!state.savedAffinityMask) state.savedAffinityMask = new Set(state.affinityMask);
      const totalCores = Math.max(1, state.hostLogicalCores || 16);
      const splitIdx = Math.ceil(totalCores / 2);
      const isAffinityDirty = () => {
        if (state.affinityMask.size !== state.savedAffinityMask.size) return true;
        for (const id of state.affinityMask) {
          if (!state.savedAffinityMask.has(id)) return true;
        }
        return false;
      };
      const formatAffinityStatus = () => {
        const count = state.affinityMask.size;
        return count === 0 ? '自由调度' : `已绑定 ${count} / ${totalCores}`;
      };

      const coreMeta = Array.from({ length: totalCores }, (_, i) => {
        // Hybrid P/E demo when cores carry distinct types (matches WinUI3 CpuCoreType check)
        const type = totalCores >= 8 ? (i < splitIdx ? 'perf' : 'eff') : 'perf';
        return { index: i, type };
      });
      const hasHybridCores =
        coreMeta.some((c) => c.type === 'perf') && coreMeta.some((c) => c.type === 'eff');

      const PERF_BOLT_SVG = `<svg viewBox="0 0 16 16" width="13" height="13" fill="currentColor" stroke="rgba(0,0,0,0.35)" stroke-width="1" stroke-linejoin="round" aria-hidden="true"><path d="M9.5 1.5L3.8 8.8h3.7L6.5 14.5l5.7-7.3H8.5l1-5.7z"/></svg>`;
      const EFF_LEAF_SVG = `<svg viewBox="0 0 24 24" width="13" height="13" fill="currentColor" stroke="rgba(0,0,0,0.35)" stroke-width="1" stroke-linejoin="round" aria-hidden="true"><path d="M17 8C8 10 5.9 16.17 3.82 21.34l1.89.66.95-2.3c.48.17.98.3 1.34.3C19 20 22 3 22 3c-1 2-8 2.25-13 3.25S2 11.5 2 13.5s1.75 3.75 1.75 3.75C7 8 17 8 17 8z"/></svg>`;

      const tiles = coreMeta.map(({ index: i, type }) => {
        const checked = state.affinityMask.has(i);
        const badgeIcon = type === 'perf' ? PERF_BOLT_SVG : type === 'eff' ? EFF_LEAF_SVG : '';
        const badgeClass = type === 'perf' ? 'perf' : type === 'eff' ? 'eff' : '';
        return `
          <div class="cpu-affinity-tile ${checked ? 'selected' : ''}" data-core-idx="${i}">
            ${badgeIcon ? `<span class="cpu-affinity-badge ${badgeClass}">${badgeIcon}</span>` : ''}
            <span class="cpu-affinity-num">${i}</span>
          </div>
        `;
      });
      const hybridQuickHtml = hasHybridCores
        ? `
          <button class="cpu-affinity-quick-btn" data-affinity-quick="perf" type="button">仅 P 核</button>
          <button class="cpu-affinity-quick-btn" data-affinity-quick="eff" type="button">仅 E 核</button>
        `
        : '';
      const dirty = isAffinityDirty();
      host.innerHTML = `
        <div class="cpu-affinity-view">
          <div class="cpu-affinity-heading">
            <div class="cpu-affinity-status-group">
              <span class="cpu-affinity-model" id="affinityStatusText">${formatAffinityStatus()}</span>
              <button class="setting-info-btn" data-info-desc="${sec.desc}" title="${sec.desc}">
                <span class="fi" style="font-size:15px;">&#xE946;</span>
              </button>
            </div>
            <div class="cpu-affinity-quick-group">
              <button class="cpu-affinity-quick-btn" data-affinity-quick="all" type="button">全选</button>
              <button class="cpu-affinity-quick-btn" data-affinity-quick="clear" type="button">清空</button>
              ${hybridQuickHtml}
            </div>
          </div>
          <div class="cpu-affinity-grid" id="affinityGrid">${tiles.join('')}</div>
          <div class="compute-rect-actions">
            <button class="winui-btn-secondary" id="affinityRevertBtn" ${dirty ? '' : 'disabled'} type="button">撤销</button>
            <button class="winui-btn-accent" id="affinityApplyBtn" ${dirty ? '' : 'disabled'} type="button">应用</button>
          </div>
        </div>
      `;

      const syncAffinityDom = () => {
        host.querySelectorAll('[data-core-idx]').forEach((el) => {
          const idx = Number(el.getAttribute('data-core-idx'));
          el.classList.toggle('selected', state.affinityMask.has(idx));
        });
        const isPerfOnly =
          state.affinityMask.size === 8 && [0, 1, 2, 3, 4, 5, 6, 7].every((i) => state.affinityMask.has(i));
        const isEffOnly =
          state.affinityMask.size === 8 && [8, 9, 10, 11, 12, 13, 14, 15].every((i) => state.affinityMask.has(i));
        host.querySelector('[data-affinity-quick="perf"]')?.classList.toggle('active', isPerfOnly);
        host.querySelector('[data-affinity-quick="eff"]')?.classList.toggle('active', isEffOnly);
        const statusEl = host.querySelector('#affinityStatusText');
        if (statusEl) statusEl.textContent = formatAffinityStatus();
        const isDirty = isAffinityDirty();
        const revBtn = host.querySelector('#affinityRevertBtn');
        const appBtn = host.querySelector('#affinityApplyBtn');
        if (revBtn) revBtn.disabled = !isDirty;
        if (appBtn) appBtn.disabled = !isDirty;
      };

      const toggleTile = (idx) => {
        if (state.affinityMask.has(idx)) state.affinityMask.delete(idx);
        else state.affinityMask.add(idx);
        syncAffinityDom();
      };

      // WinUI 3 drag-to-toggle per entered tile (BeginSelection / MoveSelection / EndSelection)
      const gridEl = host.querySelector('#affinityGrid');
      if (gridEl) {
        let dragging = false;
        let lastToggledIdx = null;
        gridEl.addEventListener('pointerdown', (e) => {
          if (e.button !== 0) return;
          const tile = e.target.closest('[data-core-idx]');
          if (!tile) return;
          e.preventDefault();
          dragging = true;
          const idx = Number(tile.getAttribute('data-core-idx'));
          lastToggledIdx = idx;
          toggleTile(idx);
          gridEl.setPointerCapture(e.pointerId);
        });
        gridEl.addEventListener('pointermove', (e) => {
          if (!dragging) return;
          const hit = document.elementFromPoint(e.clientX, e.clientY)?.closest('[data-core-idx]');
          if (!hit || !gridEl.contains(hit)) return;
          const idx = Number(hit.getAttribute('data-core-idx'));
          if (idx !== lastToggledIdx) {
            lastToggledIdx = idx;
            toggleTile(idx);
          }
        });
        const endDrag = (e) => {
          if (!dragging) return;
          dragging = false;
          lastToggledIdx = null;
          try {
            gridEl.releasePointerCapture(e.pointerId);
          } catch {}
        };
        gridEl.addEventListener('pointerup', endDrag);
        gridEl.addEventListener('pointercancel', endDrag);
      }

      host.querySelectorAll('[data-affinity-quick]').forEach((btn) => {
        btn.addEventListener('click', () => {
          const mode = btn.getAttribute('data-affinity-quick');
          if (mode === 'all') state.affinityMask = new Set(Array.from({ length: totalCores }, (_, i) => i));
          else if (mode === 'clear') state.affinityMask = new Set();
          else if (mode === 'perf') state.affinityMask = new Set(Array.from({ length: splitIdx }, (_, i) => i));
          else if (mode === 'eff') state.affinityMask = new Set(Array.from({ length: Math.max(0, totalCores - splitIdx) }, (_, i) => splitIdx + i));
          syncAffinityDom();
        });
      });

      host.querySelector('#affinityRevertBtn')?.addEventListener('click', () => {
        state.affinityMask = new Set(state.savedAffinityMask);
        syncAffinityDom();
      });
      host.querySelector('#affinityApplyBtn')?.addEventListener('click', () => {
        state.savedAffinityMask = new Set(state.affinityMask);
        syncAffinityDom();
        showNotice(state.uiLang === 'en' ? 'Applied' : '已应用');
      });
      syncAffinityDom();
      return;
    }

    // Section 3: 指令集能力 (Candidates A / B / C / Original)
    if (sec === PROCESSOR_SECTIONS[2] && state.isaVariant !== 'original-5cards') {
      const vm = getVm();
      const hostCpuModel =
        PROCESSOR_SECTIONS.find((s) => s.kind === 'affinity')?.cpuModel || 'AMD Ryzen 9 9950X 16-Core Processor';
      if (!state.isaByVm[vm.id]) {
        const initObj = {
          nested: true,
          legacyOs: false,
          migrationEnabled: false,
          migrationMode: 'MinimumFeatureSet',
          cpuBrand: ''
        };
        state.isaByVm[vm.id] = {
          saved: { ...initObj },
          draft: { ...initObj }
        };
      }
      const store = state.isaByVm[vm.id];
      const d = store.draft;
      const s = store.saved;
      const dirty =
        d.nested !== s.nested ||
        d.legacyOs !== s.legacyOs ||
        d.migrationEnabled !== s.migrationEnabled ||
        d.migrationMode !== s.migrationMode ||
        d.cpuBrand !== s.cpuBrand;
      const disabled = Boolean(vm.running);
      const variant = state.isaVariant || 'grid-2x2';
      const migDesign = state.isaMigDesign || 'three-way';
      const migTerms = state.isaMigTerms || 'friendly';
      const legacyTitle = state.isaLegacyName || '老旧系统兼容';

      const termMap = {
        friendly: {
          cardTitle: '跨机迁移兼容',
          subCardTitle: '迁移兼容范围',
          minLabel: '通用模式',
          clusterLabel: '集群模式',
          offHint: '完整性能',
          minHint: '兼容任意同厂主机',
          clusterHint: '兼容集群内节点'
        },
        scene: {
          cardTitle: '跨机迁移兼容',
          subCardTitle: '可迁移目标范围',
          minLabel: '任意同厂主机',
          clusterLabel: '集群内节点',
          offHint: '仅限同款 CPU',
          minHint: '跨代同厂可迁移',
          clusterHint: '集群节点可迁移'
        },
        concise: {
          cardTitle: '迁移兼容',
          subCardTitle: '兼容指令集',
          minLabel: '最小特征集',
          clusterLabel: '集群公共集',
          offHint: '宿主全部指令',
          minHint: '固定最小特征集',
          clusterHint: '集群公共特征集'
        }
      };
      const t = termMap[migTerms] || termMap.friendly;

      const nestedLabel = renderSettingLabelHtml(
        '嵌套虚拟化',
        '允许在虚拟机内继续运行虚拟机或容器。'
      );
      const legacyLabel = renderSettingLabelHtml(
        legacyTitle,
        '屏蔽部分新指令，兼容 Windows 7 等老旧系统。'
      );
      const migLabel = renderSettingLabelHtml(
        t.cardTitle,
        `${t.minLabel}：只保留基础指令，可在不同型号的 CPU 间迁移。\n${t.clusterLabel}：保留集群各节点共有的指令，兼顾迁移与性能。`
      );
      const migModeLabel = renderSettingLabelHtml(
        t.subCardTitle,
        `${t.minLabel}：只保留基础指令，可在不同型号的 CPU 间迁移；${t.clusterLabel}：保留集群各节点共有的指令。`
      );
      const brandLabel = renderSettingLabelHtml(
        '自定义 CPU 名称',
        '修改虚拟机内显示的处理器名称，留空使用宿主型号。'
      );

      const renderTwoWayPill = (isSubDisabled, inlineWidth = false) => `
        <div class="isa-pill-track ${inlineWidth ? 'inline-width' : ''}">
          <button type="button" class="isa-pill-btn ${d.migrationEnabled && d.migrationMode === 'MinimumFeatureSet' ? 'active' : !d.migrationEnabled && d.migrationMode === 'MinimumFeatureSet' ? 'active-neutral' : ''}" data-isa-mode="MinimumFeatureSet" ${disabled || isSubDisabled ? 'disabled' : ''}>${t.minLabel}</button>
          <button type="button" class="isa-pill-btn ${d.migrationEnabled && d.migrationMode === 'CommonClusterFeatureSet' ? 'active' : !d.migrationEnabled && d.migrationMode === 'CommonClusterFeatureSet' ? 'active-neutral' : ''}" data-isa-mode="CommonClusterFeatureSet" ${disabled || isSubDisabled ? 'disabled' : ''}>${t.clusterLabel}</button>
        </div>
      `;

      const renderMergedMigrationCard = (span2 = false) => {
        if (migDesign === 'three-way') {
          return `
            <div class="isa-card ${span2 ? 'span-2' : ''} ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${migLabel}
              </div>
              <div class="isa-sub-full">
                <div class="isa-pill-track cols-3">
                  <button type="button" class="isa-pill-btn ${!d.migrationEnabled ? 'active' : ''}" data-isa-three="off" ${disabled ? 'disabled' : ''}>关闭</button>
                  <button type="button" class="isa-pill-btn ${d.migrationEnabled && d.migrationMode === 'MinimumFeatureSet' ? 'active' : ''}" data-isa-three="MinimumFeatureSet" ${disabled ? 'disabled' : ''}>${t.minLabel}</button>
                  <button type="button" class="isa-pill-btn ${d.migrationEnabled && d.migrationMode === 'CommonClusterFeatureSet' ? 'active' : ''}" data-isa-three="CommonClusterFeatureSet" ${disabled ? 'disabled' : ''}>${t.clusterLabel}</button>
                </div>
              </div>
            </div>
          `;
        }
        if (migDesign === 'switch-reveal') {
          return `
            <div class="isa-card ${span2 ? 'span-2' : ''} ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${migLabel}
                <div class="winui-switch ${d.migrationEnabled ? 'on' : ''}" data-bound-switch="true" data-isa-toggle="migrationEnabled">
                  <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                </div>
              </div>
              <div class="isa-sub-full">
                ${
                  d.migrationEnabled
                    ? renderTwoWayPill(false)
                    : `<div class="isa-off-banner">未开启，使用宿主完整指令集</div>`
                }
              </div>
            </div>
          `;
        }
        // 'switch-pill'
        return `
          <div class="isa-card ${span2 ? 'span-2' : ''} ${disabled ? 'is-disabled' : ''}">
            <div class="isa-card-row">
              ${migLabel}
              <div class="winui-switch ${d.migrationEnabled ? 'on' : ''}" data-bound-switch="true" data-isa-toggle="migrationEnabled">
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
              </div>
            </div>
            <div class="isa-sub-full ${!d.migrationEnabled ? 'is-disabled' : ''}">
              ${renderTwoWayPill(!d.migrationEnabled)}
            </div>
          </div>
        `;
      };

      const brandPlaceholder = '';

      const renderThreeWayPillHtml = (inlineWidth = false) => `
        <div class="isa-pill-track cols-3" ${inlineWidth ? 'style="width:310px;flex-shrink:0;"' : ''}>
          <button type="button" class="isa-pill-btn ${!d.migrationEnabled ? 'active' : ''}" data-isa-three="off" ${disabled ? 'disabled' : ''}>关闭</button>
          <button type="button" class="isa-pill-btn ${d.migrationEnabled && d.migrationMode === 'MinimumFeatureSet' ? 'active' : ''}" data-isa-three="MinimumFeatureSet" ${disabled ? 'disabled' : ''}>${t.minLabel}</button>
          <button type="button" class="isa-pill-btn ${d.migrationEnabled && d.migrationMode === 'CommonClusterFeatureSet' ? 'active' : ''}" data-isa-three="CommonClusterFeatureSet" ${disabled ? 'disabled' : ''}>${t.clusterLabel}</button>
        </div>
      `;

      const renderBoolPillHtml = (key, val) => `
        <div class="isa-pill-track">
          <button type="button" class="isa-pill-btn ${!val ? 'active' : ''}" data-isa-bool-key="${key}" data-isa-bool-val="false" ${disabled ? 'disabled' : ''}>关闭</button>
          <button type="button" class="isa-pill-btn ${val ? 'active' : ''}" data-isa-bool-key="${key}" data-isa-bool-val="true" ${disabled ? 'disabled' : ''}>开启</button>
        </div>
      `;

      let gridBodyHtml = '';
      if (variant === 'single-row-equal') {
        // 破法 2：全部单行 68px 等高（上二半宽 + 下二通栏单行）
        gridBodyHtml = `
          <div class="isa-grid-2x2">
            <div class="isa-card uniform-1row ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${nestedLabel}
                <div class="winui-switch ${d.nested ? 'on' : ''}" data-bound-switch="true" data-isa-toggle="nested">
                  <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                </div>
              </div>
            </div>
            <div class="isa-card uniform-1row ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${legacyLabel}
                <div class="winui-switch ${d.legacyOs ? 'on' : ''}" data-bound-switch="true" data-isa-toggle="legacyOs">
                  <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                </div>
              </div>
            </div>
            <div class="isa-card uniform-1row span-2 ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${migLabel}
                ${renderThreeWayPillHtml(true)}
              </div>
            </div>
            <div class="isa-card uniform-1row span-2 ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${brandLabel}
                <input type="text" class="winui-textbox" id="isaBrandInput" style="width:310px;height:38px;flex-shrink:0;" value="${d.cpuBrand}" placeholder="${brandPlaceholder}" ${disabled ? 'disabled' : ''} />
              </div>
            </div>
          </div>
        `;
      } else if (variant === 'grid-switch-equal') {
        // 破法 3：2×2 四宫格保留开关，强制 114px 等高（标题在上，开关居右下与底栏对齐）
        gridBodyHtml = `
          <div class="isa-grid-2x2">
            <div class="isa-card uniform-2row ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${nestedLabel}
              </div>
              <div class="isa-card-row" style="min-height:38px;">
                <span style="font-size:13px;color:var(--TextFillColorSecondaryBrush);">${d.nested ? '已开启' : '已关闭'}</span>
                <div class="winui-switch ${d.nested ? 'on' : ''}" data-bound-switch="true" data-isa-toggle="nested">
                  <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                </div>
              </div>
            </div>
            <div class="isa-card uniform-2row ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${legacyLabel}
              </div>
              <div class="isa-card-row" style="min-height:38px;">
                <span style="font-size:13px;color:var(--TextFillColorSecondaryBrush);">${d.legacyOs ? '已开启' : '已关闭'}</span>
                <div class="winui-switch ${d.legacyOs ? 'on' : ''}" data-bound-switch="true" data-isa-toggle="legacyOs">
                  <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                </div>
              </div>
            </div>
            <div class="isa-card uniform-2row ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${migLabel}
              </div>
              <div class="isa-sub-full">
                ${renderThreeWayPillHtml(false)}
              </div>
            </div>
            <div class="isa-card uniform-2row ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${brandLabel}
              </div>
              <input type="text" class="winui-textbox" id="isaBrandInput" style="width:100%;height:38px;" value="${d.cpuBrand}" placeholder="${brandPlaceholder}" ${disabled ? 'disabled' : ''} />
            </div>
          </div>
        `;
      } else {
        // 破法 1（默认 grid-pill-equal）：2×2 四宫格统一底栏（全部 114px 等高，顶排也用 [关闭 | 开启] 胶囊）
        gridBodyHtml = `
          <div class="isa-grid-2x2">
            <div class="isa-card uniform-2row ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${nestedLabel}
              </div>
              <div class="isa-sub-full">
                ${renderBoolPillHtml('nested', d.nested)}
              </div>
            </div>
            <div class="isa-card uniform-2row ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${legacyLabel}
              </div>
              <div class="isa-sub-full">
                ${renderBoolPillHtml('legacyOs', d.legacyOs)}
              </div>
            </div>
            <div class="isa-card uniform-2row ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${migLabel}
              </div>
              <div class="isa-sub-full">
                ${renderThreeWayPillHtml(false)}
              </div>
            </div>
            <div class="isa-card uniform-2row ${disabled ? 'is-disabled' : ''}">
              <div class="isa-card-row">
                ${brandLabel}
              </div>
              <input type="text" class="winui-textbox" id="isaBrandInput" style="width:100%;height:38px;" value="${d.cpuBrand}" placeholder="${brandPlaceholder}" ${disabled ? 'disabled' : ''} />
            </div>
          </div>
        `;
      }

      host.innerHTML = `
        <div class="proc-group-stack">
          ${gridBodyHtml}
          <div class="compute-rect-actions">
            <button class="winui-btn-secondary" id="isaRevertBtn" ${dirty && !disabled ? '' : 'disabled'} type="button">撤销</button>
            <button class="winui-btn-accent" id="isaApplyBtn" ${dirty && !disabled ? '' : 'disabled'} type="button">应用</button>
          </div>
        </div>
      `;

      host.querySelectorAll('[data-isa-toggle]').forEach((sw) => {
        sw.addEventListener('click', () => {
          if (disabled) return;
          const key = sw.getAttribute('data-isa-toggle');
          d[key] = !d[key];
          renderProcessorSection(host, sec);
          bindSettingsHostControls(host);
        });
      });

      host.querySelectorAll('[data-isa-bool-key]').forEach((btn) => {
        btn.addEventListener('click', () => {
          if (disabled) return;
          const key = btn.getAttribute('data-isa-bool-key');
          d[key] = btn.getAttribute('data-isa-bool-val') === 'true';
          renderProcessorSection(host, sec);
          bindSettingsHostControls(host);
        });
      });

      host.querySelectorAll('[data-isa-three]').forEach((btn) => {
        btn.addEventListener('click', () => {
          if (disabled) return;
          const val = btn.getAttribute('data-isa-three');
          if (val === 'off') {
            d.migrationEnabled = false;
          } else {
            d.migrationEnabled = true;
            d.migrationMode = val;
          }
          renderProcessorSection(host, sec);
          bindSettingsHostControls(host);
        });
      });

      host.querySelectorAll('[data-isa-mode]').forEach((btn) => {
        btn.addEventListener('click', () => {
          if (disabled || !d.migrationEnabled) return;
          d.migrationMode = btn.getAttribute('data-isa-mode');
          renderProcessorSection(host, sec);
          bindSettingsHostControls(host);
        });
      });

      const brandInp = host.querySelector('#isaBrandInput');
      brandInp?.addEventListener('input', () => {
        d.cpuBrand = brandInp.value;
        const nextDirty =
          d.nested !== s.nested ||
          d.legacyOs !== s.legacyOs ||
          d.migrationEnabled !== s.migrationEnabled ||
          d.migrationMode !== s.migrationMode ||
          d.cpuBrand !== s.cpuBrand;
        const revBtn = host.querySelector('#isaRevertBtn');
        const appBtn = host.querySelector('#isaApplyBtn');
        if (revBtn) revBtn.disabled = !nextDirty || disabled;
        if (appBtn) appBtn.disabled = !nextDirty || disabled;
      });

      host.querySelector('#isaRevertBtn')?.addEventListener('click', () => {
        store.draft = { ...store.saved };
        renderProcessorSection(host, sec);
        bindSettingsHostControls(host);
      });

      host.querySelector('#isaApplyBtn')?.addEventListener('click', () => {
        store.saved = { ...store.draft };
        renderProcessorSection(host, sec);
        bindSettingsHostControls(host);
        showNotice('已应用');
      });
      return;
    }

    // Section 5: 处理器拓扑 — 意图驱动拓扑划分交互 vs 原始 WMI 除数输入
    if (sec.titleEnShort === 'Topology') {
      sec.fields.forEach((f) => {
        if (f.savedValue === undefined) f.savedValue = f.value;
      });
      if (!state.topologyUiMode) state.topologyUiMode = 'intent';

      const countField = getProcessorField('Count');
      const vcpu = Math.max(1, parseInt(countField?.value || '12', 10) || 12);

      // Pure Planner: Convert between High-level Intent & Low-level WMI fields
      const readIntentFromFields = () => {
        const smt = getProcessorField('SmtMode')?.value || 'Inherit';
        const maxPerNuma = Math.max(1, parseInt(getProcessorField('MaxProcessorsPerNumaNode')?.value || String(vcpu), 10) || vcpu);
        const maxNumaPerSocket = Math.max(1, parseInt(getProcessorField('MaxNumaNodesPerSocket')?.value || '1', 10) || 1);
        const maxPerL3Raw = parseInt(getProcessorField('MaxProcessorCountPerL3')?.value || '0', 10) || 0;
        const maxClusterRaw = parseInt(getProcessorField('MaxClusterCountPerSocket')?.value || '0', 10) || 0;
        const l3Dist = getProcessorField('L3DistributionPolicy')?.value || 'SmallToLarge';
        const l3Ways = parseInt(getProcessorField('L3CacheWays')?.value || '0', 10) || 0;

        const numaCount = Math.max(1, Math.min(vcpu, Math.ceil(vcpu / maxPerNuma)));
        const socketCount = Math.max(1, Math.min(numaCount, Math.ceil(numaCount / maxNumaPerSocket)));
        const vpPerNode = Math.ceil(vcpu / numaCount);
        const l3PerNode = maxPerL3Raw > 0 ? Math.max(1, Math.ceil(vpPerNode / maxPerL3Raw)) : 0; // 0 = 自动

        return {
          smt,
          socketCount,
          numaCount,
          l3PerNode,
          clustersPerSocket: maxClusterRaw, // 0 = 自动, 1..8
          l3Dist,
          l3Ways,
          // Derived WMI values:
          wmiMaxPerNuma: maxPerNuma,
          wmiMaxNumaPerSocket: maxNumaPerSocket,
          wmiMaxPerL3: maxPerL3Raw,
          wmiMaxCluster: maxClusterRaw
        };
      };

      // Compute exact Hyper-V achievable discrete counts for NUMA & Sockets
      const getAchievableCounts = (total) => {
        const set = new Set();
        for (let k = 1; k <= total; k++) {
          set.add(Math.ceil(total / k));
        }
        return Array.from(set).sort((a, b) => a - b);
      };
      const validNumaSteps = getAchievableCounts(vcpu);
      const snapToSteps = (val, steps) =>
        steps.reduce((best, s) => (Math.abs(s - val) < Math.abs(best - val) ? s : best), steps[0]);

      const writeIntentToFields = (intent) => {
        const validNuma = snapToSteps(Math.max(1, Math.min(vcpu, intent.numaCount)), validNumaSteps);
        const validSocketSteps = getAchievableCounts(validNuma);
        const validSockets = snapToSteps(Math.max(1, Math.min(validNuma, intent.socketCount)), validSocketSteps);
        // Find exact MaxProcessorsPerNumaNode that yields validNuma (prefer largest/evenly balanced)
        let maxPerNuma = Math.ceil(vcpu / validNuma);
        while (maxPerNuma > 1 && Math.ceil(vcpu / maxPerNuma) !== validNuma) maxPerNuma--;
        let maxNumaPerSocket = Math.ceil(validNuma / validSockets);
        while (maxNumaPerSocket > 1 && Math.ceil(validNuma / maxNumaPerSocket) !== validSockets) maxNumaPerSocket--;
        const vpPerNode = maxPerNuma;
        const maxPerL3 = intent.l3PerNode > 0 ? Math.max(1, Math.ceil(vpPerNode / intent.l3PerNode)) : '';
        const maxCluster = intent.clustersPerSocket > 0 ? String(Math.min(8, Math.max(1, intent.clustersPerSocket))) : '';

        const setF = (id, val) => {
          const f = getProcessorField(id);
          if (f) f.value = String(val);
        };
        setF('SmtMode', intent.smt);
        setF('MaxProcessorsPerNumaNode', maxPerNuma);
        setF('MaxNumaNodesPerSocket', maxNumaPerSocket);
        setF('MaxProcessorCountPerL3', maxPerL3);
        setF('MaxClusterCountPerSocket', maxCluster);
        setF('L3DistributionPolicy', intent.l3Dist);
        setF('L3CacheWays', intent.l3Ways);
      };

      const computeL3BucketsPreview = (vpInNode, maxPerL3, policy) => {
        if (!maxPerL3 || maxPerL3 <= 0) return [`${vpInNode}`];
        const count = Math.ceil(vpInNode / maxPerL3);
        if (count <= 1) return [`${vpInNode}`];
        if (policy === 'EvenSmallToLarge' || policy === 'EvenLargeToSmall') {
          const base = Math.floor(vpInNode / count);
          const rem = vpInNode % count;
          const arr = Array.from({ length: count }, (_, i) => (i < rem ? base + 1 : base));
          return policy === 'EvenSmallToLarge' ? arr.reverse() : arr;
        }
        const full = count - 1;
        const rem = vpInNode - full * maxPerL3;
        const arr = [...Array(full).fill(maxPerL3), rem];
        return policy === 'SmallToLarge' ? arr.slice().reverse() : arr;
      };

      const cur = readIntentFromFields();
      const vpPerNode = Math.ceil(vcpu / cur.numaCount);
      const totalL3 = cur.l3PerNode > 0 ? cur.numaCount * cur.l3PerNode : cur.numaCount;
      const hasUnevenL3 = cur.wmiMaxPerL3 > 0 && vpPerNode % cur.wmiMaxPerL3 !== 0 && Math.ceil(vpPerNode / cur.wmiMaxPerL3) > 1;
      const bucketArr = computeL3BucketsPreview(vpPerNode, cur.wmiMaxPerL3, cur.l3Dist);
      const coreDesc =
        cur.smt === 'SingleThread'
          ? `${vcpu} 核 ${vcpu} 线程`
          : cur.smt === 'MultiThread'
          ? `${Math.max(1, Math.ceil(vcpu / 2))} 核 ${vcpu} 线程`
          : `${vcpu} vCPU（自动超线程）`;

      const isSecDirty = () => sec.fields.some((f) => f.value !== f.savedValue);

      const syncRightTopoPanel = (info) => {
        document.querySelectorAll('#dcpTopoModeList [data-topo-uimode]').forEach((b) => {
          b.classList.toggle('active', b.getAttribute('data-topo-uimode') === state.topologyUiMode);
        });
        const setEl = (id, text) => {
          const el = document.getElementById(id);
          if (el) el.textContent = text;
        };
        setEl('dcpWmiNuma', String(info.wmiMaxPerNuma));
        setEl('dcpWmiSocket', String(info.wmiMaxNumaPerSocket));
        setEl('dcpWmiL3', info.wmiMaxPerL3 > 0 ? String(info.wmiMaxPerL3) : '自动');
        setEl('dcpWmiCluster', info.wmiMaxCluster > 0 ? String(info.wmiMaxCluster) : '自动');
      };

      syncRightTopoPanel(cur);

      if (state.topologyUiMode === 'intent') {
        const smtLabel = renderSettingLabelHtml(
          '超线程',
          '自动：跟随宿主每核心线程数。\n关闭：每个虚拟核心 1 个线程。\n开启：每 2 个逻辑处理器组成 1 个双线程物理核心。'
        );
        const l3CountLabel = renderSettingLabelHtml(
          '每节点 L3 域',
          '设置每个 NUMA 节点内拆分的虚拟 L3 缓存域数量（自动表示整节点共享 1 块 L3）。'
        );
        const clusterLabel = renderSettingLabelHtml(
          '每插槽簇数',
          '设置每个虚拟 CPU 插槽内划分的处理器簇数量（1～8，自动由 Hyper-V 默认处理）。'
        );
        const l3DistLabel = renderSettingLabelHtml(
          'L3 余数分配',
          `当节点内 ${vpPerNode} 个核心无法被 ${cur.l3PerNode || 1} 组 L3 整除时，决定各缓存域的核心排布（当前：${bucketArr.join(' + ')}）。`
        );
        const l3WaysLabel = renderSettingLabelHtml(
          'L3 缓存路数',
          '设置传入虚拟化引擎的 L3 缓存相联路数参数（0 为默认）。'
        );

        const smtPills = [
          ['Inherit', '自动'],
          ['SingleThread', '关闭'],
          ['MultiThread', '开启']
        ]
          .map(
            ([val, text]) =>
              `<button type="button" class="isa-pill-btn ${cur.smt === val ? 'active' : ''}" data-topo-smt="${val}">${text}</button>`
          )
          .join('');

        const distPills = [
          ['SmallToLarge', '由小到大'],
          ['LargeToSmall', '由大到小'],
          ['EvenSmallToLarge', '均匀小到大'],
          ['EvenLargeToSmall', '均匀大到小']
        ]
          .map(
            ([val, text]) =>
              `<button type="button" class="isa-pill-btn ${cur.l3Dist === val ? 'active' : ''}" data-topo-dist="${val}" ${cur.l3PerNode <= 1 ? 'disabled' : ''}>${text}</button>`
          )
          .join('');

        const renderCoreTilesHtml = (startCpu, count) => {
          if (count > 24) {
            return `<span class="topo-core-chip">${count} 个逻辑处理器（CPU ${startCpu}–${startCpu + count - 1}）</span>`;
          }
          if (cur.smt === 'MultiThread') {
            const chips = [];
            for (let i = 0; i < count; i += 2) {
              const t0 = startCpu + i;
              const t1 = i + 1 < count ? startCpu + i + 1 : null;
              chips.push(
                `<span class="topo-core-chip is-smt">核心 ${Math.floor(t0 / 2)}<span class="topo-thread-tag">${t0}</span>${
                  t1 !== null ? `<span class="topo-thread-tag">${t1}</span>` : ''
                }</span>`
              );
            }
            return chips.join('');
          }
          if (cur.smt === 'SingleThread') {
            return Array.from({ length: count }, (_, i) => `<span class="topo-core-chip">核心 ${startCpu + i}</span>`).join('');
          }
          return Array.from({ length: count }, (_, i) => `<span class="topo-core-chip">CPU ${startCpu + i}</span>`).join('');
        };

        const renderSocketsDiagramHtml = () => {
          const numaSizes = [];
          let remVcpu = vcpu;
          for (let n = 0; n < cur.numaCount; n++) {
            const take = Math.min(cur.wmiMaxPerNuma, remVcpu);
            if (take > 0) {
              numaSizes.push(take);
              remVcpu -= take;
            }
          }
          const actualNumaCount = numaSizes.length;
          let nextCpuId = 0;
          let numaOffset = 0;
          return Array.from({ length: cur.socketCount }, (_, sIdx) => {
            const remainingSockets = cur.socketCount - sIdx;
            const remainingNumas = actualNumaCount - numaOffset;
            if (remainingNumas <= 0) return '';
            const takeNumas = Math.min(cur.wmiMaxNumaPerSocket, Math.max(1, remainingNumas - (remainingSockets - 1)));
            const renderNodeContent = (nVcpus) => {
              const l3Buckets = cur.l3PerNode > 1 ? computeL3BucketsPreview(nVcpus, cur.wmiMaxPerL3, cur.l3Dist) : null;
              if (!l3Buckets) {
                const startId = nextCpuId;
                nextCpuId += nVcpus;
                return `<div class="topo-cores-wrap">${renderCoreTilesHtml(startId, nVcpus)}</div>`;
              }
              return `<div class="topo-l3-grid">${l3Buckets
                .map((bSize, lIdx) => {
                  const startId = nextCpuId;
                  nextCpuId += bSize;
                  return `
                    <div class="topo-l3-box">
                      <div class="topo-box-hdr">
                        <span>L3 ${lIdx}</span>
                        <span class="topo-box-sub">${bSize} 核</span>
                      </div>
                      <div class="topo-cores-wrap">${renderCoreTilesHtml(startId, bSize)}</div>
                    </div>
                  `;
                })
                .join('')}</div>`;
            };

            // Flatten: if this socket has only 1 NUMA, don't wrap in an extra NUMA box
            if (takeNumas === 1) {
              const nIdx = numaOffset++;
              const nVcpus = numaSizes[nIdx] || 1;
              const clusterBadge = cur.clustersPerSocket > 0 ? `，${cur.clustersPerSocket} 簇` : '';
              return `
                <div class="topo-socket-box">
                  <div class="topo-box-hdr">
                    <span>插槽 ${sIdx}</span>
                    <span class="topo-box-sub" style="color:var(--VmHeaderCheckedBrush);">NUMA ${nIdx}${clusterBadge}</span>
                  </div>
                  ${renderNodeContent(nVcpus)}
                </div>
              `;
            }

            const socketNumasHtml = Array.from({ length: takeNumas }, () => {
              const nIdx = numaOffset++;
              const nVcpus = numaSizes[nIdx] || 1;
              return `
                <div class="topo-numa-box">
                  <div class="topo-box-hdr">
                    <span style="color:var(--VmHeaderCheckedBrush);">NUMA ${nIdx}</span>
                    <span class="topo-box-sub">${nVcpus} 核</span>
                  </div>
                  ${renderNodeContent(nVcpus)}
                </div>
              `;
            }).join('');

            const clusterBadge = cur.clustersPerSocket > 0 ? `，${cur.clustersPerSocket} 簇` : '';
            return `
              <div class="topo-socket-box">
                <div class="topo-box-hdr">
                  <span>插槽 ${sIdx}</span>
                  <span class="topo-box-sub">${takeNumas} NUMA${clusterBadge}</span>
                </div>
                <div class="topo-numa-grid">${socketNumasHtml}</div>
              </div>
            `;
          }).join('');
        };

        const getStageClass = () =>
          `topo-diagram-stage ${cur.socketCount === 1 ? 'is-single' : cur.socketCount === 2 ? 'is-two' : ''}`;

        const renderTopoSliderHtml = (kind, val, minVal, maxVal) => {
          const span = Math.max(1, maxVal - minVal);
          const ratio = maxVal <= minVal ? 0 : Math.max(0, Math.min(1, (val - minVal) / span));
          return `
            <div
              class="winui-energy-slider mode-grip thumb-slim-bar ${maxVal <= minVal ? 'is-disabled' : ''}"
              data-topo-slider="${kind}"
              style="--energy-ratio:${ratio.toFixed(4)};"
              role="slider"
              aria-valuemin="${minVal}"
              aria-valuemax="${maxVal}"
              aria-valuenow="${val}"
              tabindex="${maxVal <= minVal ? '-1' : '0'}"
            >
              <div class="winui-energy-track">
                <div class="winui-energy-fill">
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                </div>
                <div class="winui-energy-cells-overlay"></div>
              </div>
              <div class="winui-energy-thumb">
                <span class="thumb-grip"></span>
              </div>
            </div>
          `;
        };

        const advParts = [];
        if (cur.l3PerNode > 1) advParts.push(`${cur.l3PerNode} 组 L3`);
        if (cur.clustersPerSocket > 0) advParts.push(`${cur.clustersPerSocket} 簇`);
        if (cur.l3Ways > 0) advParts.push(`${cur.l3Ways} 路缓存`);
        const advSummary = advParts.length > 0 ? advParts.join('，') : '默认（无需调整）';

        host.innerHTML = `
          <div class="proc-group-stack" style="gap:8px;">
            <div class="isa-grid-2x2" style="gap:6px;">
              <div class="isa-card uniform-1row span-2">
                <div class="isa-card-row">
                  ${smtLabel}
                  <div class="isa-pill-track cols-3" style="width:310px;flex-shrink:0;">
                    ${smtPills}
                  </div>
                </div>
              </div>

              <div class="compute-energy-card is-top-right topo-slider-card">
                <div class="energy-card-head">
                  <span class="setting-label-title">插槽</span>
                  <div class="energy-val-inline">
                    <span class="energy-num-input compact-num" id="topoSocketValText" style="border:none;background:transparent;padding:0;">${cur.socketCount}</span>
                    <span class="energy-num-unit">路</span>
                  </div>
                </div>
                ${renderTopoSliderHtml('socket', cur.socketCount, 1, vcpu)}
              </div>

              <div class="compute-energy-card is-top-right topo-slider-card">
                <div class="energy-card-head">
                  <span class="setting-label-title">NUMA 节点</span>
                  <div class="energy-val-inline">
                    <span class="energy-num-input compact-num" id="topoNumaValText" style="border:none;background:transparent;padding:0;">${cur.numaCount}</span>
                    <span class="energy-num-unit">个</span>
                  </div>
                </div>
                ${renderTopoSliderHtml('numa', cur.numaCount, 1, vcpu)}
              </div>

              <div class="compute-energy-card is-top-right topo-slider-card">
                <div class="energy-card-head">
                  ${l3CountLabel}
                  <div class="energy-val-inline">
                    <span class="energy-num-input compact-num" id="topoL3ValText" style="border:none;background:transparent;padding:0;width:auto;">${cur.l3PerNode > 1 ? cur.l3PerNode : '自动'}</span>
                    <span class="energy-num-unit" id="topoL3UnitText">${cur.l3PerNode > 1 ? '组' : ''}</span>
                  </div>
                </div>
                ${renderTopoSliderHtml('l3', cur.l3PerNode > 1 ? cur.l3PerNode : 1, 1, Math.max(1, vpPerNode))}
              </div>

              <div class="compute-energy-card is-top-right topo-slider-card">
                <div class="energy-card-head">
                  ${clusterLabel}
                  <div class="energy-val-inline">
                    <span class="energy-num-input compact-num" id="topoClusterValText" style="border:none;background:transparent;padding:0;width:auto;">${cur.clustersPerSocket > 0 ? cur.clustersPerSocket : '自动'}</span>
                    <span class="energy-num-unit" id="topoClusterUnitText">${cur.clustersPerSocket > 0 ? '簇' : ''}</span>
                  </div>
                </div>
                ${renderTopoSliderHtml('cluster', cur.clustersPerSocket, 0, 8)}
              </div>

              <div class="isa-card uniform-1row span-2 ${cur.l3PerNode <= 1 ? 'is-disabled' : ''}" id="topoL3DistCard">
                <div class="isa-card-row">
                  ${l3DistLabel}
                  <div class="isa-pill-track cols-4" style="width:388px;flex-shrink:0;">
                    ${distPills}
                  </div>
                </div>
              </div>

              <div class="compute-energy-card is-top-right span-2 topo-slider-card">
                <div class="energy-card-head">
                  ${l3WaysLabel}
                  <div class="energy-val-inline">
                    <span class="energy-num-input compact-num" id="topoWaysValText" style="border:none;background:transparent;padding:0;width:auto;">${cur.l3Ways > 0 ? cur.l3Ways : '默认'}</span>
                    <span class="energy-num-unit" id="topoWaysUnitText">${cur.l3Ways > 0 ? '路' : ''}</span>
                  </div>
                </div>
                ${renderTopoSliderHtml('ways', Math.min(64, cur.l3Ways), 0, 64)}
              </div>
            </div>

            <div class="topo-canvas-card" style="gap:8px;padding:10px 12px;">
              <div class="topo-box-hdr">
                <span>预览</span>
                <span class="topo-box-sub">共 ${vcpu} 逻辑处理器</span>
              </div>
              <div class="${getStageClass()}" id="topoDiagramStage">
                ${renderSocketsDiagramHtml()}
              </div>
            </div>

            <div class="compute-rect-actions">
              <button class="winui-btn-secondary" id="procGroupRevertBtn" ${isSecDirty() ? '' : 'disabled'} type="button">撤销</button>
              <button class="winui-btn-accent" id="procGroupApplyBtn" ${isSecDirty() ? '' : 'disabled'} type="button">应用</button>
            </div>
          </div>
        `;

        host.querySelector('#topoAdvToggleBtn')?.addEventListener('click', () => {
          state.topoAdvancedOpen = !state.topoAdvancedOpen;
          renderProcessorSection(host, sec);
          bindSettingsHostControls(host);
        });

        host.querySelectorAll('[data-topo-slider]').forEach((sliderEl) => {
          const kind = sliderEl.getAttribute('data-topo-slider');
          const minVal = Number(sliderEl.getAttribute('aria-valuemin') || '0');
          const maxVal = Number(sliderEl.getAttribute('aria-valuemax') || '1');
          if (maxVal <= minVal) return;

          const applyTopoVal = (nextVal) => {
            const curVpPerNode = Math.ceil(vcpu / cur.numaCount);
            if (kind === 'socket') {
              const clamped = Math.max(1, Math.min(vcpu, nextVal));
              cur.socketCount = snapToSteps(clamped, validNumaSteps);
              if (cur.numaCount < cur.socketCount) cur.numaCount = cur.socketCount;
              const validForSock = getAchievableCounts(cur.numaCount);
              if (!validForSock.includes(cur.socketCount)) {
                cur.numaCount = snapToSteps(cur.numaCount, validNumaSteps.filter((n) => getAchievableCounts(n).includes(cur.socketCount)));
              }
            } else if (kind === 'numa') {
              const clamped = Math.max(1, Math.min(vcpu, nextVal));
              cur.numaCount = snapToSteps(clamped, validNumaSteps);
              if (cur.socketCount > cur.numaCount) cur.socketCount = cur.numaCount;
            } else if (kind === 'l3') {
              const l3Steps = getAchievableCounts(curVpPerNode);
              const snappedL3 = snapToSteps(Math.max(1, Math.min(curVpPerNode, nextVal)), l3Steps);
              cur.l3PerNode = snappedL3 <= 1 ? 0 : snappedL3;
            } else if (kind === 'cluster') {
              cur.clustersPerSocket = Math.max(0, Math.min(8, nextVal));
            } else if (kind === 'ways') {
              cur.l3Ways = Math.max(0, Math.min(64, nextVal));
            }

            writeIntentToFields(cur);
            const updated = readIntentFromFields();
            cur.socketCount = updated.socketCount;
            cur.numaCount = updated.numaCount;
            cur.l3PerNode = updated.l3PerNode;
            cur.clustersPerSocket = updated.clustersPerSocket;
            cur.l3Ways = updated.l3Ways;
            cur.wmiMaxPerNuma = updated.wmiMaxPerNuma;
            cur.wmiMaxNumaPerSocket = updated.wmiMaxNumaPerSocket;
            cur.wmiMaxPerL3 = updated.wmiMaxPerL3;
            cur.wmiMaxCluster = updated.wmiMaxCluster;
            syncRightTopoPanel(updated);

            const updateSliderUi = (k, val, lo, hi) => {
              const el = host.querySelector(`[data-topo-slider="${k}"]`);
              if (!el) return;
              const span = Math.max(1, hi - lo);
              el.style.setProperty('--energy-ratio', Math.max(0, Math.min(1, (val - lo) / span)).toFixed(4));
              el.setAttribute('aria-valuenow', String(val));
              el.setAttribute('aria-valuemax', String(hi));
            };

            const nextVpPerNode = Math.ceil(vcpu / cur.numaCount);
            updateSliderUi('socket', cur.socketCount, 1, vcpu);
            updateSliderUi('numa', cur.numaCount, 1, vcpu);
            updateSliderUi('l3', cur.l3PerNode > 1 ? cur.l3PerNode : 1, 1, Math.max(1, nextVpPerNode));
            updateSliderUi('cluster', cur.clustersPerSocket, 0, 8);
            updateSliderUi('ways', Math.min(64, cur.l3Ways), 0, 64);

            const setT = (id, val) => {
              const el = host.querySelector(`#${id}`);
              if (el) el.textContent = val;
            };
            setT('topoSocketValText', String(cur.socketCount));
            setT('topoNumaValText', String(cur.numaCount));
            setT('topoL3ValText', cur.l3PerNode > 1 ? String(cur.l3PerNode) : '自动');
            setT('topoL3UnitText', cur.l3PerNode > 1 ? '组' : '');
            setT('topoClusterValText', cur.clustersPerSocket > 0 ? String(cur.clustersPerSocket) : '自动');
            setT('topoClusterUnitText', cur.clustersPerSocket > 0 ? '簇' : '');
            setT('topoWaysValText', cur.l3Ways > 0 ? String(cur.l3Ways) : '默认');
            setT('topoWaysUnitText', cur.l3Ways > 0 ? '路' : '');

            const distCard = host.querySelector('#topoL3DistCard');
            if (distCard) {
              distCard.classList.toggle('is-disabled', cur.l3PerNode <= 1);
              distCard.querySelectorAll('[data-topo-dist]').forEach((b) => {
                b.disabled = cur.l3PerNode <= 1;
              });
            }

            const parts = [];
            if (cur.l3PerNode > 1) parts.push(`${cur.l3PerNode} 组 L3`);
            if (cur.clustersPerSocket > 0) parts.push(`${cur.clustersPerSocket} 簇`);
            if (cur.l3Ways > 0) parts.push(`${cur.l3Ways} 路缓存`);
            setT('topoAdvSummaryText', parts.length > 0 ? parts.join('，') : '默认（无需调整）');

            const stage = host.querySelector('#topoDiagramStage');
            if (stage) {
              stage.className = getStageClass();
              stage.innerHTML = renderSocketsDiagramHtml();
            }
            const dirty = isSecDirty();
            const rev = host.querySelector('#procGroupRevertBtn');
            const app = host.querySelector('#procGroupApplyBtn');
            if (rev) rev.disabled = !dirty;
            if (app) app.disabled = !dirty;
          };

          const updateFromClientX = (clientX) => {
            const rect = sliderEl.getBoundingClientRect();
            const ratio = Math.max(0, Math.min(1, (clientX - rect.left) / Math.max(1, rect.width)));
            const liveMax = Number(sliderEl.getAttribute('aria-valuemax') || maxVal);
            applyTopoVal(Math.round(minVal + ratio * (liveMax - minVal)));
          };

          sliderEl.addEventListener('pointerdown', (e) => {
            e.preventDefault();
            sliderEl.classList.add('is-dragging');
            sliderEl.setPointerCapture(e.pointerId);
            updateFromClientX(e.clientX);
          });
          sliderEl.addEventListener('pointermove', (e) => {
            if (!sliderEl.classList.contains('is-dragging')) return;
            updateFromClientX(e.clientX);
          });
          const endDrag = (e) => {
            if (!sliderEl.classList.contains('is-dragging')) return;
            sliderEl.classList.remove('is-dragging');
            if (sliderEl.hasPointerCapture?.(e.pointerId)) {
              sliderEl.releasePointerCapture(e.pointerId);
            }
          };
          sliderEl.addEventListener('pointerup', endDrag);
          sliderEl.addEventListener('pointercancel', endDrag);
        });

        host.querySelectorAll('[data-topo-smt]').forEach((btn) => {
          btn.addEventListener('click', () => {
            cur.smt = btn.getAttribute('data-topo-smt');
            writeIntentToFields(cur);
            renderProcessorSection(host, sec);
            bindSettingsHostControls(host);
          });
        });

        host.querySelectorAll('[data-topo-dist]').forEach((btn) => {
          btn.addEventListener('click', () => {
            if (cur.l3PerNode <= 1) return;
            cur.l3Dist = btn.getAttribute('data-topo-dist');
            writeIntentToFields(cur);
            renderProcessorSection(host, sec);
            bindSettingsHostControls(host);
          });
        });

        host.querySelectorAll('[data-topo-step]').forEach((btn) => {
          btn.addEventListener('click', () => {
            const kind = btn.getAttribute('data-topo-step');
            const dir = Number(btn.getAttribute('data-dir'));
            if (kind === 'socket') {
              cur.socketCount = Math.max(1, Math.min(vcpu, cur.socketCount + dir));
              if (cur.numaCount < cur.socketCount) cur.numaCount = cur.socketCount;
            } else if (kind === 'numa') {
              cur.numaCount = Math.max(1, Math.min(vcpu, cur.numaCount + dir));
              if (cur.socketCount > cur.numaCount) cur.socketCount = cur.numaCount;
            } else if (kind === 'l3') {
              const nextL3 = cur.l3PerNode === 0 && dir > 0 ? 2 : cur.l3PerNode === 2 && dir < 0 ? 0 : cur.l3PerNode + dir;
              cur.l3PerNode = Math.max(0, Math.min(vpPerNode, nextL3));
            } else if (kind === 'cluster') {
              cur.clustersPerSocket = Math.max(0, Math.min(8, cur.clustersPerSocket + dir));
            } else if (kind === 'ways') {
              cur.l3Ways = Math.max(0, Math.min(1024, cur.l3Ways + dir));
            }
            writeIntentToFields(cur);
            renderProcessorSection(host, sec);
            bindSettingsHostControls(host);
          });
        });

        host.querySelector('#topoWaysInput')?.addEventListener('change', (e) => {
          cur.l3Ways = Math.max(0, Math.min(1024, parseInt(e.target.value || '0', 10) || 0));
          writeIntentToFields(cur);
          renderProcessorSection(host, sec);
          bindSettingsHostControls(host);
        });

        host.querySelector('#procGroupRevertBtn')?.addEventListener('click', () => {
          sec.fields.forEach((f) => {
            f.value = f.savedValue;
          });
          renderProcessorSection(host, sec);
          bindSettingsHostControls(host);
        });

        host.querySelector('#procGroupApplyBtn')?.addEventListener('click', () => {
          sec.fields.forEach((f) => {
            f.savedValue = f.value;
          });
          renderProcessorSection(host, sec);
          bindSettingsHostControls(host);
          showNotice('已应用');
        });
        return;
      }
    }

    // Section: 频率调节 — 显式区分「自动调频」vs「指定目标频率」
    if (sec.titleEnShort === 'Freq') {
      sec.fields.forEach((f) => {
        if (f.savedValue === undefined) f.savedValue = f.value;
      });
      const isSecDirty = () => sec.fields.some((f) => f.value !== f.savedValue);
      const getF = (id) => sec.fields.find((f) => f.id === id);
      const minF = getF('PerfCpuFreqMinMhz');
      const capF = getF('PerfCpuFreqCapMhz');
      const desF = getF('PerfCpuFreqDesiredMhz');
      const eppF = getF('PerfCpuEnergyPerformancePreference');
      const winF = getF('PerfCpuAutonomousActivityWindow');
      const ignF = getF('PerfCpuIgnoreHostMaxFrequency');
      const isManualTarget = (parseInt(desF?.value || '0', 10) || 0) > 0;

      const FREQ_MIN_MHZ = 400;
      const FREQ_MAX_MHZ = 10000;
      const FREQ_STEP_MHZ = 100;
      const FREQ_STEPS = (FREQ_MAX_MHZ - FREQ_MIN_MHZ) / FREQ_STEP_MHZ; // 96 -> 97 values (400..10000)

      const freqToPos = (mhz, allowAuto) => {
        const n = parseInt(mhz || '0', 10) || 0;
        if (allowAuto) {
          if (n <= 0) return 0;
          const clamped = Math.max(FREQ_MIN_MHZ, Math.min(FREQ_MAX_MHZ, n));
          return 1 + Math.round((clamped - FREQ_MIN_MHZ) / FREQ_STEP_MHZ);
        }
        const clamped = Math.max(FREQ_MIN_MHZ, Math.min(FREQ_MAX_MHZ, n || FREQ_MIN_MHZ));
        return Math.round((clamped - FREQ_MIN_MHZ) / FREQ_STEP_MHZ);
      };

      const posToFreq = (pos, allowAuto) => {
        if (allowAuto) {
          if (pos <= 0) return 0;
          return FREQ_MIN_MHZ + (pos - 1) * FREQ_STEP_MHZ;
        }
        return FREQ_MIN_MHZ + pos * FREQ_STEP_MHZ;
      };

      const calcFreqInputWidthCh = (str) => {
        const visualLen = [...String(str || '自动')].reduce(
          (acc, ch) => acc + (/[\u4e00-\u9fff]/.test(ch) ? 2 : 1),
          0
        );
        return `${Math.max(2.6, visualLen + 0.6)}ch`;
      };

      const renderFreqSliderCard = (f, allowAuto = true) => {
        const maxPos = allowAuto ? FREQ_STEPS + 1 : FREQ_STEPS;
        const val = parseInt(f.value || '0', 10) || 0;
        const pos = freqToPos(val, allowAuto);
        const ratio = (pos / Math.max(1, maxPos)).toFixed(4);
        const dispVal = allowAuto && val <= 0 ? '' : String(val);
        const tipText = allowAuto && val <= 0 ? '自动（不限制）' : `${val} MHz`;
        return `
          <div class="energy-card compact-head-mode" data-freq-slider-card="${f.id}">
            <div class="energy-card-head">
              ${renderSettingLabelHtml(f.title, f.desc)}
              <div class="energy-val-inline">
                <input
                  type="text"
                  class="energy-num-input compact-num"
                  data-freq-slider-input="${f.id}"
                  data-allow-auto="${allowAuto ? '1' : '0'}"
                  value="${dispVal}"
                  placeholder="自动"
                  style="width:${calcFreqInputWidthCh(dispVal || '自动')};max-width:none;"
                />
                <span class="energy-num-unit" data-freq-unit="${f.id}" style="${allowAuto && val <= 0 ? 'display:none;' : ''}">MHz</span>
              </div>
            </div>
            <div
              class="winui-energy-slider mode-fader thumb-slim-bar"
              data-freq-slider="${f.id}"
              data-allow-auto="${allowAuto ? '1' : '0'}"
              style="--energy-ratio:${ratio};"
              role="slider"
              aria-valuemin="0"
              aria-valuemax="${maxPos}"
              aria-valuenow="${pos}"
              tabindex="0"
            >
              <div class="winui-energy-tooltip" data-freq-tooltip="${f.id}">${tipText}</div>
              <div class="winui-energy-track">
                <div class="winui-energy-fill">
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                </div>
                <div class="winui-energy-cells-overlay"></div>
              </div>
              <div class="winui-energy-thumb">
                <span class="thumb-grip"></span>
              </div>
            </div>
          </div>
        `;
      };

      const renderStepperCard = (f, span2 = false) => `
        <div class="isa-card uniform-1row ${span2 ? 'span-2' : ''}" data-freq-stepper="${f.id}">
          <div class="isa-card-row">
            ${renderSettingLabelHtml(f.title, f.desc)}
            <div class="isa-num-box">
              <button type="button" class="isa-num-step" data-step-dir="-1" title="减小">
                <span class="fi" style="font-size:12px;">&#xE738;</span>
              </button>
              <input type="text" class="isa-num-input" value="${f.value}" placeholder="${f.placeholder || ''}" />
              <button type="button" class="isa-num-step" data-step-dir="1" title="增大">
                <span class="fi" style="font-size:12px;">&#xE710;</span>
              </button>
            </div>
          </div>
        </div>
      `;

      const modeLabel = renderSettingLabelHtml(
        '调频方式',
        '智能调频：随负载高低自动升降频（推荐）。\n固定频率：尽量稳定跑在指定的目标频率上。'
      );

      const eppVal = String(eppF.value ?? '');
      const eppPos = eppVal === '' ? 0 : 256 - Math.max(0, Math.min(255, parseInt(eppVal, 10) || 0));
      const formatEppLabel = (pos) => {
        if (pos <= 0) return '自动';
        if (pos === 1) return '节能优先';
        if (pos <= 85) return '偏节能';
        if (pos <= 171) return '均衡';
        if (pos < 256) return '偏性能';
        return '性能优先';
      };
      const eppRatio = (eppPos / 256).toFixed(4);

      host.innerHTML = `
        <div class="proc-group-stack">
          <div class="isa-grid-2x2">
            ${renderFreqSliderCard(minF, true)}
            ${renderFreqSliderCard(capF, true)}

            <div class="isa-card uniform-1row span-2">
              <div class="isa-card-row">
                ${modeLabel}
                <div class="isa-pill-track" style="width:310px;flex-shrink:0;">
                  <button type="button" class="isa-pill-btn ${!isManualTarget ? 'active' : ''}" data-freq-mode="auto">智能调频</button>
                  <button type="button" class="isa-pill-btn ${isManualTarget ? 'active' : ''}" data-freq-mode="manual">固定频率</button>
                </div>
              </div>
            </div>

            ${
              isManualTarget
                ? renderFreqSliderCard(desF, false)
                : `<div class="isa-card uniform-1row span-2">
                    <div class="isa-card-row">
                      ${renderSettingLabelHtml(eppF.title, eppF.desc)}
                      <div
                        class="winui-energy-slider mode-fader thumb-slim-bar"
                        id="freqEppSlider"
                        style="--energy-ratio:${eppRatio};width:310px;flex-shrink:0;"
                        role="slider"
                        aria-valuemin="0"
                        aria-valuemax="256"
                        aria-valuenow="${eppPos}"
                        tabindex="0"
                      >
                        <div class="winui-energy-tooltip" id="freqEppTooltip">${formatEppLabel(eppPos)}</div>
                        <div class="winui-energy-track">
                          <div class="winui-energy-fill">
                            <span class="energy-bubble"></span>
                            <span class="energy-bubble"></span>
                            <span class="energy-bubble"></span>
                            <span class="energy-bubble"></span>
                            <span class="energy-bubble"></span>
                            <span class="energy-bubble"></span>
                            <span class="energy-bubble"></span>
                          </div>
                          <div class="winui-energy-cells-overlay"></div>
                        </div>
                        <div class="winui-energy-thumb">
                          <span class="thumb-grip"></span>
                        </div>
                      </div>
                    </div>
                  </div>
                  ${renderStepperCard(winF)}`
            }

            <div class="isa-card uniform-1row">
              <div class="isa-card-row">
                ${renderSettingLabelHtml(ignF.title, ignF.desc)}
                <div class="winui-switch ${ignF.value ? 'on' : ''}" data-proc-field-id="${ignF.id}">
                  <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                </div>
              </div>
            </div>
          </div>

          <div class="compute-rect-actions">
            <button class="winui-btn-secondary" id="procGroupRevertBtn" ${isSecDirty() ? '' : 'disabled'} type="button">撤销</button>
            <button class="winui-btn-accent" id="procGroupApplyBtn" ${isSecDirty() ? '' : 'disabled'} type="button">应用</button>
          </div>
        </div>
      `;

      const syncDirtyBtns = () => {
        const dirty = isSecDirty();
        const rev = host.querySelector('#procGroupRevertBtn');
        const app = host.querySelector('#procGroupApplyBtn');
        if (rev) rev.disabled = !dirty;
        if (app) app.disabled = !dirty;
      };

      host.querySelectorAll('[data-freq-slider]').forEach((sliderEl) => {
        const fid = sliderEl.getAttribute('data-freq-slider');
        const allowAuto = sliderEl.getAttribute('data-allow-auto') === '1';
        const fieldObj = getF(fid);
        if (!fieldObj) return;
        const maxPos = allowAuto ? FREQ_STEPS + 1 : FREQ_STEPS;
        const thumbEl = sliderEl.querySelector('.winui-energy-thumb');
        let dragging = false;
        let grabOffsetPx = 0;

        const applyFreqMhz = (mhz, fromInput = false) => {
          const clean = Math.max(0, Math.min(20000, parseInt(mhz, 10) || 0));
          const finalVal = allowAuto ? clean : Math.max(FREQ_MIN_MHZ, clean);
          fieldObj.value = String(finalVal);
          const pos = freqToPos(finalVal, allowAuto);
          const ratio = (pos / Math.max(1, maxPos)).toFixed(4);
          sliderEl.style.setProperty('--energy-ratio', ratio);
          sliderEl.setAttribute('aria-valuenow', String(pos));

          const tip = host.querySelector(`[data-freq-tooltip="${fid}"]`);
          if (tip) tip.textContent = allowAuto && finalVal <= 0 ? '自动（不限制）' : `${finalVal} MHz`;

          const unitEl = host.querySelector(`[data-freq-unit="${fid}"]`);
          if (unitEl) unitEl.style.display = allowAuto && finalVal <= 0 ? 'none' : '';

          if (!fromInput) {
            const inp = host.querySelector(`[data-freq-slider-input="${fid}"]`);
            if (inp) {
              const disp = allowAuto && finalVal <= 0 ? '' : String(finalVal);
              inp.value = disp;
              inp.style.width = calcFreqInputWidthCh(disp || '自动');
            }
          }
          syncDirtyBtns();
        };

        const updateFromX = (clientX) => {
          const rect = sliderEl.getBoundingClientRect();
          const thumbW = thumbEl ? thumbEl.offsetWidth : 24;
          const usableW = Math.max(1, rect.width - thumbW);
          const x = clientX - rect.left - grabOffsetPx;
          const r = Math.max(0, Math.min(1, x / usableW));
          const pos = Math.round(r * maxPos);
          applyFreqMhz(posToFreq(pos, allowAuto));
        };

        sliderEl.addEventListener('pointerdown', (e) => {
          if (e.button !== 0) return;
          e.preventDefault();
          dragging = true;
          sliderEl.classList.add('is-dragging');
          const thumbRect = thumbEl?.getBoundingClientRect();
          if (thumbRect && e.clientX >= thumbRect.left && e.clientX <= thumbRect.right) {
            grabOffsetPx = e.clientX - thumbRect.left;
          } else {
            grabOffsetPx = (thumbEl ? thumbEl.offsetWidth : 24) / 2;
            updateFromX(e.clientX);
          }
          sliderEl.setPointerCapture(e.pointerId);
        });
        sliderEl.addEventListener('pointermove', (e) => {
          if (!dragging) return;
          updateFromX(e.clientX);
        });
        const endDrag = (e) => {
          if (!dragging) return;
          dragging = false;
          sliderEl.classList.remove('is-dragging');
          try {
            sliderEl.releasePointerCapture(e.pointerId);
          } catch {}
        };
        sliderEl.addEventListener('pointerup', endDrag);
        sliderEl.addEventListener('pointercancel', endDrag);

        const inp = host.querySelector(`[data-freq-slider-input="${fid}"]`);
        if (inp) {
          inp.addEventListener('input', () => {
            const raw = inp.value.replace(/[^\d]/g, '');
            inp.value = raw;
            inp.style.width = calcFreqInputWidthCh(raw || '自动');
            if (raw === '' && allowAuto) {
              applyFreqMhz(0, true);
              return;
            }
            const parsed = parseInt(raw, 10);
            if (!Number.isNaN(parsed)) {
              applyFreqMhz(parsed, true);
            }
          });
          inp.addEventListener('blur', () => {
            const val = parseInt(inp.value || '0', 10) || 0;
            applyFreqMhz(allowAuto ? val : Math.max(FREQ_MIN_MHZ, val), false);
          });
        }
      });

      const eppSlider = host.querySelector('#freqEppSlider');
      if (eppSlider) {
        const thumbEl = eppSlider.querySelector('.winui-energy-thumb');
        let dragging = false;
        let grabOffsetPx = 0;
        const applyEppPos = (pos) => {
          const clamped = Math.max(0, Math.min(256, pos));
          eppF.value = clamped <= 0 ? '' : String(256 - clamped);
          const ratio = (clamped / 256).toFixed(4);
          eppSlider.style.setProperty('--energy-ratio', ratio);
          eppSlider.setAttribute('aria-valuenow', String(clamped));
          const tip = host.querySelector('#freqEppTooltip');
          if (tip) tip.textContent = formatEppLabel(clamped);
          syncDirtyBtns();
        };
        const updateFromX = (clientX) => {
          const rect = eppSlider.getBoundingClientRect();
          const thumbW = thumbEl ? thumbEl.offsetWidth : 24;
          const usableW = Math.max(1, rect.width - thumbW);
          const x = clientX - rect.left - grabOffsetPx;
          const r = Math.max(0, Math.min(1, x / usableW));
          applyEppPos(Math.round(r * 256));
        };
        eppSlider.addEventListener('pointerdown', (e) => {
          if (e.button !== 0) return;
          e.preventDefault();
          dragging = true;
          eppSlider.classList.add('is-dragging');
          const thumbRect = thumbEl?.getBoundingClientRect();
          if (thumbRect && e.clientX >= thumbRect.left && e.clientX <= thumbRect.right) {
            grabOffsetPx = e.clientX - thumbRect.left;
          } else {
            grabOffsetPx = (thumbEl ? thumbEl.offsetWidth : 24) / 2;
            updateFromX(e.clientX);
          }
          eppSlider.setPointerCapture(e.pointerId);
        });
        eppSlider.addEventListener('pointermove', (e) => {
          if (!dragging) return;
          updateFromX(e.clientX);
        });
        const endDrag = (e) => {
          if (!dragging) return;
          dragging = false;
          eppSlider.classList.remove('is-dragging');
          try {
            eppSlider.releasePointerCapture(e.pointerId);
          } catch {}
        };
        eppSlider.addEventListener('pointerup', endDrag);
        eppSlider.addEventListener('pointercancel', endDrag);
      }

      host.querySelectorAll('[data-freq-mode]').forEach((btn) => {
        btn.addEventListener('click', () => {
          const mode = btn.getAttribute('data-freq-mode');
          if (mode === 'auto') {
            if ((parseInt(desF.value || '0', 10) || 0) > 0) {
              state.lastDesiredFreqMhz = desF.value;
            }
            desF.value = '';
          } else {
            const minValNum = parseInt(minF.value || '0', 10) || 0;
            desF.value = state.lastDesiredFreqMhz || (minValNum > 0 ? String(minValNum) : '3500');
          }
          renderProcessorSection(host, sec);
          bindSettingsHostControls(host);
        });
      });

      host.querySelectorAll('[data-freq-stepper]').forEach((card) => {
        const field = getF(card.getAttribute('data-freq-stepper'));
        const input = card.querySelector('.isa-num-input');
        input?.addEventListener('input', () => {
          field.value = input.value;
          syncDirtyBtns();
        });
        card.querySelectorAll('[data-step-dir]').forEach((btn) => {
          btn.addEventListener('click', () => {
            const dir = Number(btn.getAttribute('data-step-dir'));
            const curVal = parseInt(input.value || '0', 10) || 0;
            const step = field.id === 'PerfCpuEnergyPerformancePreference' ? 5 : field.id === 'PerfCpuAutonomousActivityWindow' ? 10 : 100;
            const minV = field.id === 'PerfCpuFreqDesiredMhz' ? 100 : (field.min ?? 0);
            const next = Math.max(minV, Math.min(field.max ?? 99999, curVal + dir * step));
            input.value = String(next);
            field.value = String(next);
            syncDirtyBtns();
          });
        });
      });

      host.querySelectorAll('[data-proc-field-id]').forEach((sw) => {
        sw.addEventListener('click', () => {
          setTimeout(syncDirtyBtns, 0);
        });
      });

      host.querySelector('#procGroupRevertBtn')?.addEventListener('click', () => {
        sec.fields.forEach((f) => {
          f.value = f.savedValue;
        });
        renderProcessorSection(host, sec);
        bindSettingsHostControls(host);
      });

      host.querySelector('#procGroupApplyBtn')?.addEventListener('click', () => {
        sec.fields.forEach((f) => {
          f.savedValue = f.value;
        });
        renderProcessorSection(host, sec);
        bindSettingsHostControls(host);
        showNotice('已应用');
      });
      return;
    }

    // Standard processor field sections (Sections 4-9): Unified 68px single-row cards (破法 2)
    sec.fields.forEach((f) => {
      if (f.savedValue === undefined) f.savedValue = f.value;
    });
    const isSecDirty = () => sec.fields.some((f) => f.value !== f.savedValue);
    const syncSecActionBtns = () => {
      const dirty = isSecDirty();
      const rev = host.querySelector('#procGroupRevertBtn');
      const app = host.querySelector('#procGroupApplyBtn');
      if (rev) rev.disabled = !dirty;
      if (app) app.disabled = !dirty;
    };

    const nonFullCount = sec.fields.filter((f) => f.kind !== 'choice').length;
    let halfIndex = 0;

    const buildTopologyPreviewHtml = () => {
      if (sec.titleEnShort !== 'Topology') return '';
      const countField = getProcessorField('Count');
      const vcpu = Math.max(1, parseInt(countField?.value || '8', 10) || 8);
      const smtVal = getProcessorField('SmtMode')?.value || 'Inherit';
      const maxPerNuma = Math.max(1, parseInt(getProcessorField('MaxProcessorsPerNumaNode')?.value || '16', 10) || 16);
      const maxNumaPerSocket = Math.max(1, parseInt(getProcessorField('MaxNumaNodesPerSocket')?.value || '1', 10) || 1);
      const maxPerL3Raw = parseInt(getProcessorField('MaxProcessorCountPerL3')?.value || '0', 10) || 0;

      const numaCount = Math.ceil(vcpu / maxPerNuma);
      const socketCount = Math.ceil(numaCount / maxNumaPerSocket);
      const vpPerNode = Math.ceil(vcpu / numaCount);
      const l3PerNode = maxPerL3Raw > 0 ? Math.ceil(vpPerNode / maxPerL3Raw) : 1;
      const totalL3 = numaCount * l3PerNode;
      const coreDesc =
        smtVal === 'SingleThread'
          ? `${vcpu} 核 ${vcpu} 线程`
          : smtVal === 'MultiThread'
          ? `${Math.max(1, Math.ceil(vcpu / 2))} 核 ${vcpu} 线程`
          : `${vcpu} vCPU（自动超线程）`;

      const maxClusterRaw = parseInt(getProcessorField('MaxClusterCountPerSocket')?.value || '0', 10) || 0;
      const setEl = (id, text) => {
        const el = document.getElementById(id);
        if (el) el.textContent = text;
      };
      setEl('dcpWmiNuma', String(maxPerNuma));
      setEl('dcpWmiSocket', String(maxNumaPerSocket));
      setEl('dcpWmiL3', maxPerL3Raw > 0 ? String(maxPerL3Raw) : '自动');
      setEl('dcpWmiCluster', maxClusterRaw > 0 ? String(maxClusterRaw) : '自动');

      return `
        <div class="topo-preview-bar" id="topoPreviewBar">
          <span class="topo-preview-title">
            <span class="fi" style="font-size:14px;">&#xE950;</span>
            虚拟机拓扑预览
          </span>
          <div class="topo-preview-chips">
            <span class="topo-chip"><b>${socketCount}</b> 路插槽</span>
            <span class="topo-chip"><b>${numaCount}</b> 个 NUMA（每节点 <b>${vpPerNode}</b> 核）</span>
            <span class="topo-chip"><b>${totalL3}</b> 个 L3</span>
            <span class="topo-chip">${coreDesc}</span>
          </div>
        </div>
      `;
    };

    const syncTopologyPreview = () => {
      const bar = host.querySelector('#topoPreviewBar');
      if (!bar) return;
      const temp = document.createElement('div');
      temp.innerHTML = buildTopologyPreviewHtml();
      const next = temp.firstElementChild;
      if (next) bar.replaceWith(next);
    };

    const getFieldDisabledState = (f) => {
      if (f.disabled) return { disabled: true, tip: f.disabledTip || '' };
      if (sec.titleEnShort === 'PerfMon') {
        const pmuOn = Boolean(getProcessorField('EnablePerfmonPmu')?.value);
        if (!pmuOn && (f.id === 'EnablePerfmonPebs' || f.id === 'EnablePerfmonIpt')) {
          return { disabled: true, tip: '需先开启性能监控单元（PMU）' };
        }
      }
      if (sec.titleEnShort === 'Freq') {
        const desired = parseInt(getProcessorField('PerfCpuFreqDesiredMhz')?.value || '0', 10) || 0;
        if (desired > 0 && (f.id === 'PerfCpuEnergyPerformancePreference' || f.id === 'PerfCpuAutonomousActivityWindow')) {
          return { disabled: true, tip: '仅在期望频率为空或 0（自主调频）时生效' };
        }
      }
      if (sec.titleEnShort === 'Isolation') {
        const isoOn = Boolean(getProcessorField('HardwareIsolationExtensionsEnabled')?.value);
        if (!isoOn && f.id === 'MaxHwIsolatedGuests') {
          return { disabled: true, tip: '需先开启硬件隔离扩展' };
        }
      }
      return { disabled: false, tip: '' };
    };

    const cardsHtml = sec.fields
      .map((f, idx) => {
        const { disabled: isDis, tip: disTip } = getFieldDisabledState(f);
        const effectiveDesc =
          isDis && disTip ? `${f.desc ? `${f.desc} ` : ''}（当前不可用：${disTip}）` : f.desc;
        const labelHtml = renderSettingLabelHtml(f.title, effectiveDesc);
        const disClass = isDis ? 'is-arch-disabled' : '';
        const disTitle = isDis && disTip ? `title="${disTip}"` : '';
        if (f.kind === 'choice') {
          const colClass = f.options.length === 4 ? 'cols-4' : f.options.length === 3 ? 'cols-3' : '';
          const trackWidth = f.options.length === 4 ? 388 : 310;
          const pills = f.options
            .map(
              ([val, text]) =>
                `<button type="button" class="isa-pill-btn ${val === f.value ? 'active' : ''}" data-proc-pill-id="${f.id}" data-proc-pill-val="${val}" ${isDis ? 'disabled' : ''}>${text}</button>`
            )
            .join('');
          return `
            <div class="isa-card uniform-1row span-2 ${disClass}" ${disTitle}>
              <div class="isa-card-row">
                ${labelHtml}
                <div class="isa-pill-track ${colClass}" style="width:${trackWidth}px;flex-shrink:0;">
                  ${pills}
                </div>
              </div>
            </div>
          `;
        }
        halfIndex += 1;
        const spanClass = '';
        if (f.kind === 'toggle') {
          return `
            <div class="isa-card uniform-1row ${spanClass} ${disClass}" ${disTitle}>
              <div class="isa-card-row">
                ${labelHtml}
                <div class="winui-switch ${f.value ? 'on' : ''}" data-proc-field-id="${f.id}">
                  <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                </div>
              </div>
            </div>
          `;
        }
        if (f.kind === 'string') {
          return `
            <div class="isa-card uniform-1row span-2 ${disClass}" ${disTitle}>
              <div class="isa-card-row">
                ${labelHtml}
                <input type="text" class="winui-textbox" style="width:310px;height:38px;flex-shrink:0;" value="${f.value}" ${isDis ? 'disabled' : ''} />
              </div>
            </div>
          `;
        }
        if (f.id === 'PhysicalAddressWidth') {
          const formatAddrSize = (b) => {
            if (b <= 0) return '';
            if (b < 30 || b > 56) return '无效位宽';
            if (b < 40) return `${2 ** (b - 30)} GB`;
            if (b < 50) return `${2 ** (b - 40)} TB`;
            return `${2 ** (b - 50)} PB`;
          };
          const addrVal = parseInt(f.value || '0', 10) || 0;
          const addrPos = addrVal <= 0 ? 0 : Math.max(1, Math.min(17, addrVal - 35));
          const addrRatio = (addrPos / 17).toFixed(4);
          const sizeStr = formatAddrSize(addrVal);
          const tipText = addrVal <= 0 ? '自动' : `${addrVal} 位（${sizeStr}）`;
          return `
            <div class="isa-card uniform-1row span-2 ${disClass}" data-addr-slider-card ${disTitle}>
              <div class="isa-card-row">
                ${labelHtml}
                <div
                  class="winui-energy-slider mode-fader thumb-slim-bar ${isDis ? 'is-disabled' : ''}"
                  id="procAddrWidthSlider"
                  style="--energy-ratio:${addrRatio};width:310px;flex-shrink:0;"
                  role="slider"
                  aria-valuemin="0"
                  aria-valuemax="17"
                  aria-valuenow="${addrPos}"
                  tabindex="${isDis ? '-1' : '0'}"
                >
                  <div class="winui-energy-tooltip" id="procAddrWidthTooltip">${tipText}</div>
                  <div class="winui-energy-track">
                    <div class="winui-energy-fill">
                      <span class="energy-bubble"></span>
                      <span class="energy-bubble"></span>
                      <span class="energy-bubble"></span>
                      <span class="energy-bubble"></span>
                      <span class="energy-bubble"></span>
                      <span class="energy-bubble"></span>
                      <span class="energy-bubble"></span>
                    </div>
                    <div class="winui-energy-cells-overlay"></div>
                  </div>
                  <div class="winui-energy-thumb">
                    <span class="thumb-grip"></span>
                  </div>
                </div>
              </div>
            </div>
          `;
        }
        return `
          <div class="isa-card uniform-1row ${spanClass} ${disClass}" data-stepper-idx="${idx}" ${disTitle}>
            <div class="isa-card-row">
              ${labelHtml}
              <div class="isa-num-box">
                <button type="button" class="isa-num-step" data-step-dir="-1" title="减小" ${isDis ? 'disabled' : ''}>
                  <span class="fi" style="font-size:12px;">&#xE738;</span>
                </button>
                <input type="text" class="isa-num-input" value="${f.value}" placeholder="${f.placeholder || ''}" ${isDis ? 'disabled' : ''} />
                <button type="button" class="isa-num-step" data-step-dir="1" title="增大" ${isDis ? 'disabled' : ''}>
                  <span class="fi" style="font-size:12px;">&#xE710;</span>
                </button>
              </div>
            </div>
          </div>
        `;
      })
      .join('');

    host.innerHTML = `
      <div class="proc-group-stack">
        ${buildTopologyPreviewHtml()}
        <div class="isa-grid-2x2">${cardsHtml}</div>
        <div class="compute-rect-actions">
          <button class="winui-btn-secondary" id="procGroupRevertBtn" ${isSecDirty() ? '' : 'disabled'} type="button">撤销</button>
          <button class="winui-btn-accent" id="procGroupApplyBtn" ${isSecDirty() ? '' : 'disabled'} type="button">应用</button>
        </div>
      </div>
    `;

    host.querySelectorAll('[data-topo-uimode]').forEach((btn) => {
      btn.addEventListener('click', () => {
        state.topologyUiMode = btn.getAttribute('data-topo-uimode');
        renderProcessorSection(host, sec);
        bindSettingsHostControls(host);
      });
    });

    host.querySelectorAll('[data-proc-pill-id]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const procField = getProcessorField(btn.getAttribute('data-proc-pill-id'));
        if (procField) {
          procField.value = btn.getAttribute('data-proc-pill-val');
          renderProcessorSection(host, sec);
          bindSettingsHostControls(host);
        }
      });
    });

    const addrSlider = host.querySelector('#procAddrWidthSlider');
    if (addrSlider) {
      const addrField = getProcessorField('PhysicalAddressWidth');
      const thumbEl = addrSlider.querySelector('.winui-energy-thumb');
      const addrTip = host.querySelector('#procAddrWidthTooltip');
      let dragging = false;
      let grabOffsetPx = 0;

      const formatAddrSize = (b) => {
        if (b <= 0) return '';
        if (b < 30 || b > 56) return '无效位宽';
        if (b < 40) return `${2 ** (b - 30)} GB`;
        if (b < 50) return `${2 ** (b - 40)} TB`;
        return `${2 ** (b - 50)} PB`;
      };

      const applyAddrBits = (bits) => {
        const clean = bits <= 0 ? 0 : Math.max(36, Math.min(52, parseInt(bits, 10) || 0));
        if (addrField) addrField.value = clean <= 0 ? '' : String(clean);
        const pos = clean <= 0 ? 0 : Math.max(1, Math.min(17, clean - 35));
        addrSlider.style.setProperty('--energy-ratio', (pos / 17).toFixed(4));
        addrSlider.setAttribute('aria-valuenow', String(pos));

        const sizeStr = formatAddrSize(clean);
        if (addrTip) addrTip.textContent = clean <= 0 ? '自动' : `${clean} 位（${sizeStr}）`;
        syncSecActionBtns();
      };

      const updateFromX = (clientX) => {
        const rect = addrSlider.getBoundingClientRect();
        const thumbW = thumbEl ? thumbEl.offsetWidth : 24;
        const usableW = Math.max(1, rect.width - thumbW);
        const x = clientX - rect.left - grabOffsetPx;
        const r = Math.max(0, Math.min(1, x / usableW));
        const pos = Math.round(r * 17);
        applyAddrBits(pos <= 0 ? 0 : 35 + pos);
      };

      addrSlider.addEventListener('pointerdown', (e) => {
        if (e.button !== 0 || addrSlider.classList.contains('is-disabled')) return;
        e.preventDefault();
        dragging = true;
        addrSlider.classList.add('is-dragging');
        const thumbRect = thumbEl?.getBoundingClientRect();
        if (thumbRect && e.clientX >= thumbRect.left && e.clientX <= thumbRect.right) {
          grabOffsetPx = e.clientX - thumbRect.left;
        } else {
          grabOffsetPx = (thumbEl ? thumbEl.offsetWidth : 24) / 2;
          updateFromX(e.clientX);
        }
        addrSlider.setPointerCapture(e.pointerId);
      });
      addrSlider.addEventListener('pointermove', (e) => {
        if (!dragging) return;
        updateFromX(e.clientX);
      });
      const endDrag = (e) => {
        if (!dragging) return;
        dragging = false;
        addrSlider.classList.remove('is-dragging');
        try {
          addrSlider.releasePointerCapture(e.pointerId);
        } catch {}
      };
      addrSlider.addEventListener('pointerup', endDrag);
      addrSlider.addEventListener('pointercancel', endDrag);
    }

    host.querySelectorAll('[data-stepper-idx]').forEach((card) => {
      const idx = Number(card.getAttribute('data-stepper-idx'));
      const field = sec.fields[idx];
      const input = card.querySelector('.isa-num-input');
      input?.addEventListener('input', () => {
        field.value = input.value;
        syncSecActionBtns();
        syncTopologyPreview();
        if (field.id === 'PerfCpuFreqDesiredMhz') {
          const isLocked = (parseInt(field.value || '0', 10) || 0) > 0;
          ['PerfCpuEnergyPerformancePreference', 'PerfCpuAutonomousActivityWindow'].forEach((depId) => {
            const depInput = host.querySelector(`[data-stepper-idx] input`) && sec.fields.findIndex((sf) => sf.id === depId);
            if (depInput >= 0) {
              const depCard = host.querySelector(`[data-stepper-idx="${depInput}"]`);
              if (depCard) {
                depCard.classList.toggle('is-arch-disabled', isLocked);
                depCard.title = isLocked ? '仅在期望频率为空或 0（自主调频）时生效' : '';
                depCard.querySelectorAll('input, button[data-step-dir]').forEach((el) => {
                  el.disabled = isLocked;
                });
              }
            }
          });
        }
      });
      card.querySelectorAll('[data-step-dir]').forEach((btn) => {
        btn.addEventListener('click', () => {
          const dir = Number(btn.getAttribute('data-step-dir'));
          const cur = parseInt(input.value || '0', 10) || 0;
          if (field.id === 'PhysicalAddressWidth' && dir < 0 && cur > 0 && cur <= (field.min ?? 36)) {
            input.value = '';
            field.value = '';
            updateAddrSpaceHint();
            syncSecActionBtns();
            return;
          }
          const step = field.max >= 1000 ? 100 : 1;
          const next = Math.max(field.min ?? 0, Math.min(field.max ?? 99999, cur + dir * step));
          input.value = String(next);
          field.value = String(next);
          updateAddrSpaceHint();
          if (field.id === 'PerfCpuFreqDesiredMhz') {
            renderProcessorSection(host, sec);
            bindSettingsHostControls(host);
            return;
          }
          syncSecActionBtns();
          syncTopologyPreview();
        });
      });
    });

    host.querySelectorAll('[data-proc-field-id]').forEach((sw) => {
      sw.addEventListener('click', () => {
        const fid = sw.getAttribute('data-proc-field-id');
        setTimeout(() => {
          if (fid === 'EnablePerfmonPmu' || fid === 'HardwareIsolationExtensionsEnabled') {
            renderProcessorSection(host, sec);
            bindSettingsHostControls(host);
          } else {
            syncSecActionBtns();
          }
        }, 0);
      });
    });

    host.querySelector('#procGroupRevertBtn')?.addEventListener('click', () => {
      sec.fields.forEach((f) => {
        f.value = f.savedValue;
      });
      renderProcessorSection(host, sec);
      bindSettingsHostControls(host);
    });
    host.querySelector('#procGroupApplyBtn')?.addEventListener('click', () => {
      sec.fields.forEach((f) => {
        f.savedValue = f.value;
      });
      syncSecActionBtns();
      showNotice('已应用');
    });
  }

  function syncDcpMemoryAssembler() {
    const cpuModel = PROCESSOR_SECTIONS.find((s) => s.kind === 'affinity')?.cpuModel || '';
    const isMixedCpu = cpuModel === CPU_TOPOLOGY_PRESETS.mixed;
    document.querySelectorAll('#dcpCpuTopologyOptions [data-cpu-topology]').forEach((b) => {
      const isMixed = b.getAttribute('data-cpu-topology') === 'mixed';
      b.classList.toggle('active', isMixed === isMixedCpu);
    });
    const running = Boolean(getVm()?.running);
    document.querySelectorAll('#dcpMemPowerOptions [data-mem-power]').forEach((b) => {
      const isOn = b.getAttribute('data-mem-power') === 'on';
      b.classList.toggle('active', isOn === running);
    });
    const enabled = Boolean(getMemoryField('DynamicMemoryEnabled')?.value);
    document.querySelectorAll('#dcpMemDynamicOptions [data-mem-dynamic]').forEach((b) => {
      const isOn = b.getAttribute('data-mem-dynamic') === 'on';
      b.classList.toggle('active', isOn === enabled);
    });
    const smartPagingOn = Boolean(state.memSmartPagingAlert);
    document.querySelectorAll('#dcpMemSmartPagingOptions [data-mem-smartpaging]').forEach((b) => {
      const isOn = b.getAttribute('data-mem-smartpaging') === 'on';
      b.classList.toggle('active', isOn === smartPagingOn);
    });
    document.querySelectorAll('#dcpChartHeightOptions [data-chart-height]').forEach((b) => {
      b.classList.toggle('active', Number(b.getAttribute('data-chart-height')) === (state.singleChartHeight || 136));
    });
    document.querySelectorAll('#dcpDiskCandidateOptions [data-disk-candidate]').forEach((b) => {
      b.classList.toggle('active', b.getAttribute('data-disk-candidate') === (state.diskCandidate || 'a'));
    });
    document.querySelectorAll('#dcpIsoChartOptions [data-iso-chart]').forEach((b) => {
      b.classList.toggle('active', b.getAttribute('data-iso-chart') === (state.isoChartMode || 'single'));
    });
    document.querySelectorAll('#dcpDiffParentOptions [data-diff-parent]').forEach((b) => {
      b.classList.toggle('active', b.getAttribute('data-diff-parent') === (state.diffParentMode || 'wide'));
    });
    document.querySelectorAll('#dcpDiskModeOptions [data-disk-mode]').forEach((b) => {
      b.classList.toggle('active', b.getAttribute('data-disk-mode') === state.diskPreviewMode);
    });
    document.querySelectorAll('#dcpDiskPartOptions [data-disk-part]').forEach((b) => {
      const key = b.getAttribute('data-disk-part');
      b.classList.toggle('active', Boolean(key && state.diskParts?.[key]));
    });
  }

  // Tab 2: 内存 (VmMemorySettingsView.cs lines 22-101)
  const MEM_SLIDER_META = {
    Startup: { min: 32, max: 65536, step: 128, unit: 'MiB', fmt: (v) => `${v} MiB（${(v / 1024).toFixed(v % 1024 === 0 ? 0 : 1)} GB）` },
    Weight: {
      min: 0,
      max: 10000,
      step: 100,
      unit: '',
      fmt: (v) => {
        if (v === 5000) return '紧缺时均分内存（标准）';
        const mult = v / 5000;
        const multStr = mult < 1 ? mult.toFixed(2).replace(/\.?0+$/, '') : mult.toFixed(1).replace(/\.0$/, '');
        return v < 5000 ? `紧缺时少分内存（${multStr}x）` : `紧缺时多抢内存（${multStr}x）`;
      }
    },
    Minimum: { min: 32, max: 65536, step: 128, unit: 'MiB', fmt: (v) => `${v} MiB（${(v / 1024).toFixed(v % 1024 === 0 ? 0 : 1)} GB）` },
    Maximum: { min: 32, max: 131072, step: 256, unit: 'MiB', fmt: (v) => `${v} MiB（${(v / 1024).toFixed(v % 1024 === 0 ? 0 : 1)} GB）` },
    Buffer: { min: 5, max: 2000, step: 5, unit: '%', fmt: (v) => `${v}%` },
    LowSizeMiB: { min: 128, max: 3584, step: 128, unit: 'MiB', fmt: (v) => `${v} MiB（${(v / 1024).toFixed(v % 1024 === 0 ? 0 : 2).replace(/\.?0+$/, '')} GB）` },
    HighSizeMiB: { min: 512, max: 262656, step: 512, unit: 'MiB', fmt: (v) => `${v} MiB（${(v / 1024).toFixed(v % 1024 === 0 ? 0 : 1)} GB）` },
    HighBaseMiB: { min: 4096, max: 262144, step: 1024, unit: 'MiB', fmt: (v) => `${v} MiB（${(v / 1024).toFixed(v % 1024 === 0 ? 0 : 1)} GB）` },
    MaxMemoryBlocksPerNumaNode: { min: 32, max: 262144, step: 1024, unit: 'MiB', fmt: (v) => `${v} MiB（${(v / 1024).toFixed(v % 1024 === 0 ? 0 : 1)} GB）` },
    SgxSize: { min: 2, max: 4096, step: 2, unit: 'MiB', fmt: (v) => v >= 1024 ? `${v} MiB（${(v / 1024).toFixed(v % 1024 === 0 ? 0 : 1)} GB）` : `${v} MiB` }
  };

  function renderMemorySection(host, sec) {
    sec.fields.forEach((f) => {
      if (f.savedValue === undefined) f.savedValue = f.value;
    });
    const isSecDirty = () => sec.fields.some((f) => f.value !== f.savedValue);
    const syncSecActionBtns = () => {
      const dirty = isSecDirty();
      const rev = host.querySelector('#memGroupRevertBtn');
      const app = host.querySelector('#memGroupApplyBtn');
      if (rev) rev.disabled = !dirty;
      if (app) app.disabled = !dirty;
    };

    const isMemoryCompute = sec.titleEnShort === 'Memory';
    const dynEnabled = Boolean(getMemoryField('DynamicMemoryEnabled')?.value);
    const DYN_DEP_IDS = new Set(['Minimum', 'Maximum', 'Buffer']);
    const layoutMode = state.memComputeLayoutMode || '2-1-3';
    const dynOffMode = state.memDynamicOffMode || 'disable';

    const renderMemCard = (f, idx, forceNoSpan = false) => {
      const labelHtml = renderSettingLabelHtml(f.title, f.desc);
      const isTrackingDisabled = sec.titleEnShort === 'Extensions' && f.id === 'MemoryAccessTrackingPolicy' && getMemoryField('MemoryAccessTrackingState')?.value === '0';
      const isSgxDisabled = sec.titleEnShort === 'Security' && ['SgxLaunchControlMode', 'SgxSize', 'SgxLaunchControlDefault'].includes(f.id) && !getMemoryField('SgxEnabled')?.value;
      const isPhysicalBackingDisabled = sec.titleEnShort === 'Mapping'
        && ['EnableColdHint', 'EnableHotHint', 'EnableEpf', 'EnablePrivateCompressionStore'].includes(f.id)
        && getMemoryField('BackingType')?.value === '0';
      const isDynDisabled = (isMemoryCompute && DYN_DEP_IDS.has(f.id) && !dynEnabled) || isTrackingDisabled || isSgxDisabled || isPhysicalBackingDisabled;
      const disClass = isDynDisabled ? 'is-disabled' : '';
      const disTitle = isTrackingDisabled
        ? 'title="需先启用访问跟踪"'
        : isSgxDisabled
          ? 'title="需先启用 SGX"'
          : isPhysicalBackingDisabled
            ? 'title="物理后备模式下不支持此特性，需切换为虚拟后备"'
            : isDynDisabled
              ? 'title="需先开启动态内存"'
              : '';
      if (f.kind === 'choice') {
        const colClass = f.options.length >= 4 ? 'cols-4' : 'cols-3';
        const maxVisualPx = Math.max(
          ...f.options.map(([, text]) =>
            [...text].reduce((sum, ch) => sum + (/[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef]/.test(ch) ? 14 : 8), 0)
          )
        );
        const trackWidth = f.trackWidth || (f.options.length >= 4
          ? Math.max(388, (maxVisualPx + 22) * f.options.length)
          : Math.max(340, (maxVisualPx + 22) * 3));
        const pills = f.options
          .map(([val, text]) => {
            const optDis = isDynDisabled || f.disabledOptions?.includes(val);
            const optTitle = isTrackingDisabled ? '需先启用访问跟踪' : isSgxDisabled ? '需先启用 SGX' : f.disabledOptions?.includes(val) ? '该选项暂不可手动选择' : '';
            return `<button type="button" class="isa-pill-btn ${val === f.value ? 'active' : ''}" data-mem-pill-id="${f.id}" data-mem-pill-val="${val}" ${optDis ? `disabled title="${optTitle}"` : ''}>${text}</button>`;
          })
          .join('');
        return `
          <div class="isa-card uniform-1row ${f.noSpan2 ? '' : 'span-2'} ${disClass}" ${disTitle}>
            <div class="isa-card-row">
              ${labelHtml}
              <div class="isa-pill-track ${colClass}" style="width:${trackWidth}px;flex-shrink:0;">
                ${pills}
              </div>
            </div>
          </div>
        `;
      }
      if (f.kind === 'string') {
        return `
          <div class="isa-card uniform-1row ${f.span2 && !forceNoSpan ? 'span-2' : ''} ${disClass}" ${disTitle}>
            <div class="isa-card-row">
              ${labelHtml}
              <input type="text" class="winui-textbox" data-mem-string-id="${f.id}" style="width:310px;height:36px;flex-shrink:0;" value="${f.value || ''}" placeholder="${f.placeholder || ''}" ${isDynDisabled ? 'disabled' : ''} />
            </div>
          </div>
        `;
      }
      if (f.kind === 'toggle') {
        const spanClass = (isMemoryCompute || f.span2) && !forceNoSpan ? 'span-2' : '';
        return `
          <div class="isa-card uniform-1row ${spanClass} ${disClass}" ${disTitle}>
            <div class="isa-card-row">
              ${labelHtml}
              <div class="winui-switch ${f.value ? 'on' : ''}" data-mem-field-id="${f.id}" ${isDynDisabled ? 'data-mem-disabled="1"' : ''}>
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
              </div>
            </div>
          </div>
        `;
      }
      const meta = MEM_SLIDER_META[f.id];
      if (meta) {
        const numVal = Math.max(meta.min, Math.min(meta.max, parseInt(f.value || '0', 10) || meta.min));
        const ratio = Math.max(0, Math.min(1, (numVal - meta.min) / Math.max(1, meta.max - meta.min)));
        const isDirty = f.value !== f.savedValue;
        const unitStr = f.unit ?? meta.unit ?? '';
        const spanClass = f.span2 && !forceNoSpan ? 'span-2' : '';
        return `
          <div class="energy-card compact-head-mode ${spanClass} ${isDirty ? 'is-dirty' : ''} ${disClass}" data-mem-slider-card="${f.id}" ${disTitle}>
            <div class="energy-card-head">
              ${labelHtml}
              <div class="energy-val-group">
                <div class="energy-val-inline">
                  <input
                    type="text"
                    class="energy-num-input compact-num"
                    data-mem-slider-input="${f.id}"
                    value="${numVal}"
                    style="width:${Math.max(1.8, String(numVal).length + 0.5)}ch;"
                    ${isDynDisabled ? 'disabled' : ''}
                  />
                  ${unitStr ? `<span class="energy-num-unit">${unitStr}</span>` : ''}
                </div>
              </div>
            </div>
            <div
              class="winui-energy-slider mode-fader thumb-slim-bar ${isDynDisabled ? 'is-disabled' : ''}"
              data-mem-slider="${f.id}"
              style="--energy-ratio:${ratio.toFixed(4)};"
              role="slider"
              aria-valuemin="${meta.min}"
              aria-valuemax="${meta.max}"
              aria-valuenow="${numVal}"
              tabindex="${isDynDisabled ? '-1' : '0'}"
            >
              <div class="winui-energy-tooltip" data-mem-tooltip="${f.id}">${meta.fmt(numVal)}</div>
              <div class="winui-energy-track">
                <div class="winui-energy-fill">
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                  <span class="energy-bubble"></span>
                </div>
                <div class="winui-energy-cells-overlay"></div>
              </div>
              <div class="winui-energy-thumb">
                <span class="thumb-grip"></span>
              </div>
            </div>
          </div>
        `;
      }
      return `
        <div class="isa-card uniform-1row ${disClass}" data-mem-stepper-idx="${idx}" ${disTitle}>
          <div class="isa-card-row">
            ${labelHtml}
            <div class="isa-num-box">
              <button type="button" class="isa-num-step" data-step-dir="-1" title="减小" ${isDynDisabled ? 'disabled' : ''}>
                <span class="fi" style="font-size:12px;">&#xE738;</span>
              </button>
              <input type="text" class="isa-num-input" value="${f.value}" ${isDynDisabled ? 'disabled' : ''} />
              <button type="button" class="isa-num-step" data-step-dir="1" title="增大" ${isDynDisabled ? 'disabled' : ''}>
                <span class="fi" style="font-size:12px;">&#xE710;</span>
              </button>
            </div>
          </div>
        </div>
      `;
    };

    let bodyGridHtml = '';
    if (isMemoryCompute && layoutMode === '2-1-3') {
      const byId = Object.fromEntries(sec.fields.map((f, i) => [f.id, { f, i }]));
      const order = dynEnabled || dynOffMode !== 'hide'
        ? ['Startup', 'Weight', 'DynamicMemoryEnabled', 'Minimum', 'Maximum', 'Buffer']
        : ['Startup', 'Weight', 'DynamicMemoryEnabled'];
      bodyGridHtml = `<div class="isa-grid-2x2">${order
        .filter((id) => byId[id])
        .map((id) => renderMemCard(byId[id].f, byId[id].i))
        .join('')}</div>`;
    } else if (isMemoryCompute && layoutMode === '2x3') {
      const byId = Object.fromEntries(sec.fields.map((f, i) => [f.id, { f, i }]));
      const order = dynEnabled || dynOffMode !== 'hide'
        ? ['Startup', 'DynamicMemoryEnabled', 'Minimum', 'Maximum', 'Buffer', 'Weight']
        : ['Startup', 'DynamicMemoryEnabled', 'Weight'];
      bodyGridHtml = `<div class="isa-grid-2x2">${order
        .filter((id) => byId[id])
        .map((id) => renderMemCard(byId[id].f, byId[id].i, true))
        .join('')}</div>`;
    } else {
      bodyGridHtml = `<div class="isa-grid-2x2">${sec.fields.map((f, idx) => renderMemCard(f, idx)).join('')}</div>`;
    }

    host.innerHTML = `
      <div class="proc-group-stack">
        ${bodyGridHtml}
        <div class="compute-rect-actions">
          <button class="winui-btn-secondary" id="memGroupRevertBtn" ${isSecDirty() ? '' : 'disabled'} type="button">撤销</button>
          <button class="winui-btn-accent" id="memGroupApplyBtn" ${isSecDirty() ? '' : 'disabled'} type="button">应用</button>
        </div>
      </div>
    `;

    host.querySelectorAll('[data-mem-pill-id]').forEach((btn) => {
      btn.addEventListener('click', () => {
        if (btn.disabled) return;
        const fid = btn.getAttribute('data-mem-pill-id');
        const field = getMemoryField(fid);
        if (!field) return;
        field.value = btn.getAttribute('data-mem-pill-val');
        if (fid === 'BackingType') {
          if (field.value === '0') {
            ['EnableColdHint', 'EnableHotHint', 'EnableEpf', 'EnablePrivateCompressionStore'].forEach((id) => {
              const dep = getMemoryField(id);
              if (dep) dep.value = false;
            });
          } else if (field.value === '1') {
            const pageField = getMemoryField('BackingPageSize');
            if (pageField && pageField.value === '2') {
              pageField.value = '1';
              showNotice('虚拟后备不支持 1 GB 巨页，已同步切换为 2 MB 大页');
            }
          }
          renderMemorySection(host, sec);
          bindSettingsHostControls(host);
          return;
        }
        if (fid === 'BackingPageSize') {
          if (field.value === '2') {
            const backingField = getMemoryField('BackingType');
            if (backingField && backingField.value !== '0') {
              backingField.value = '0';
              ['EnableColdHint', 'EnableHotHint', 'EnableEpf', 'EnablePrivateCompressionStore'].forEach((id) => {
                const dep = getMemoryField(id);
                if (dep) dep.value = false;
              });
              showNotice('1 GB 巨页需使用物理后备，已同步切换内存后备方式');
            }
          }
        }
        if (fid === 'MemoryAccessTrackingState') {
          renderMemorySection(host, sec);
          bindSettingsHostControls(host);
          return;
        }
        const track = btn.closest('.isa-pill-track');
        track?.querySelectorAll('.isa-pill-btn').forEach((p) => p.classList.toggle('active', p === btn));
        syncSecActionBtns();
      });
    });

    host.querySelectorAll('[data-mem-field-id]').forEach((sw) => {
      sw.addEventListener('click', () => {
        if (sw.getAttribute('data-mem-disabled') === '1') return;
        const fid = sw.getAttribute('data-mem-field-id');
        const field = getMemoryField(fid);
        if (!field) return;
        field.value = !Boolean(field.value);
        if (fid === 'SgxEnabled' && field.value) {
          const sgxSize = getMemoryField('SgxSize');
          if (sgxSize && (parseInt(sgxSize.value || '0', 10) || 0) < 2) {
            sgxSize.value = '2';
          }
        }
        if (fid === 'DynamicMemoryEnabled' || fid === 'SgxEnabled') {
          renderMemorySection(host, sec);
          bindSettingsHostControls(host);
          syncDcpMemoryAssembler();
          return;
        }
        sw.classList.toggle('on', field.value);
        syncSecActionBtns();
        syncDcpMemoryAssembler();
      });
    });

    host.querySelectorAll('[data-mem-string-id]').forEach((inp) => {
      const fid = inp.getAttribute('data-mem-string-id');
      inp.addEventListener('input', () => {
        const field = getMemoryField(fid);
        if (!field) return;
        field.value = inp.value;
        syncSecActionBtns();
      });
    });

    host.querySelectorAll('[data-mem-stepper-idx]').forEach((card) => {
      const idx = Number(card.getAttribute('data-mem-stepper-idx'));
      const field = sec.fields[idx];
      const input = card.querySelector('.isa-num-input');
      if (!field || !input) return;
      input.addEventListener('input', () => {
        field.value = input.value;
        syncSecActionBtns();
        syncDcpMemoryAssembler();
      });
      card.querySelectorAll('[data-step-dir]').forEach((btn) => {
        btn.addEventListener('click', () => {
          const dir = Number(btn.getAttribute('data-step-dir'));
          const cur = parseInt(input.value || '0', 10) || 0;
          const next = Math.max(field.min ?? 0, cur + dir * (field.step ?? 1));
          input.value = String(next);
          field.value = String(next);
          syncSecActionBtns();
          syncDcpMemoryAssembler();
        });
      });
    });

    const updateMemSliderVisual = (fid, rawVal, syncInput = true) => {
      const field = getMemoryField(fid);
      const meta = MEM_SLIDER_META[fid];
      if (!field || !meta) return;
      const clamped = Math.max(meta.min, Math.min(meta.max, parseInt(rawVal || '0', 10) || meta.min));
      field.value = String(clamped);
      const ratio = Math.max(0, Math.min(1, (clamped - meta.min) / Math.max(1, meta.max - meta.min)));
      const sliderEl = host.querySelector(`[data-mem-slider="${fid}"]`);
      if (sliderEl) {
        sliderEl.style.setProperty('--energy-ratio', ratio.toFixed(4));
        sliderEl.setAttribute('aria-valuenow', String(clamped));
      }
      const tipEl = host.querySelector(`[data-mem-tooltip="${fid}"]`);
      if (tipEl) tipEl.textContent = meta.fmt(clamped);
      if (syncInput) {
        const inpEl = host.querySelector(`[data-mem-slider-input="${fid}"]`);
        if (inpEl) {
          inpEl.value = String(clamped);
          inpEl.style.width = `${Math.max(1.8, String(clamped).length + 0.5)}ch`;
        }
      }
      const cardEl = host.querySelector(`[data-mem-slider-card="${fid}"]`);
      if (cardEl) cardEl.classList.toggle('is-dirty', field.value !== field.savedValue);
      syncSecActionBtns();
      syncDcpMemoryAssembler();
    };

    host.querySelectorAll('[data-mem-slider]').forEach((sliderEl) => {
      const fid = sliderEl.getAttribute('data-mem-slider');
      const meta = MEM_SLIDER_META[fid];
      if (!meta) return;
      const thumbEl = sliderEl.querySelector('.winui-energy-thumb');
      let dragging = false;
      let grabOffsetPx = 0;

      const updateFromClientX = (clientX) => {
        const rect = sliderEl.getBoundingClientRect();
        const thumbW = thumbEl ? thumbEl.offsetWidth : 24;
        const usableW = Math.max(1, rect.width - thumbW);
        const x = clientX - rect.left - grabOffsetPx;
        const r = Math.max(0, Math.min(1, x / usableW));
        const raw = meta.min + r * (meta.max - meta.min);
        const snapped = Math.round(raw / meta.step) * meta.step;
        updateMemSliderVisual(fid, Math.max(meta.min, Math.min(meta.max, snapped)), true);
      };

      sliderEl.addEventListener('pointerdown', (e) => {
        if (e.button !== 0 || sliderEl.classList.contains('is-disabled')) return;
        e.preventDefault();
        dragging = true;
        sliderEl.classList.add('is-dragging');
        const thumbRect = thumbEl?.getBoundingClientRect();
        if (thumbRect && e.clientX >= thumbRect.left && e.clientX <= thumbRect.right) {
          grabOffsetPx = e.clientX - thumbRect.left;
        } else {
          grabOffsetPx = (thumbEl ? thumbEl.offsetWidth : 24) / 2;
          updateFromClientX(e.clientX);
        }
        sliderEl.setPointerCapture(e.pointerId);
      });
      sliderEl.addEventListener('pointermove', (e) => {
        if (!dragging) return;
        updateFromClientX(e.clientX);
      });
      const endDrag = (e) => {
        if (!dragging) return;
        dragging = false;
        sliderEl.classList.remove('is-dragging');
        try {
          sliderEl.releasePointerCapture(e.pointerId);
        } catch {}
      };
      sliderEl.addEventListener('pointerup', endDrag);
      sliderEl.addEventListener('pointercancel', endDrag);
    });

    host.querySelectorAll('[data-mem-slider-input]').forEach((inp) => {
      const fid = inp.getAttribute('data-mem-slider-input');
      inp.addEventListener('input', () => {
        inp.style.width = `${Math.max(1.8, (inp.value || '0').length + 0.5)}ch`;
        const parsed = parseInt(inp.value.trim(), 10);
        if (!Number.isNaN(parsed)) {
          updateMemSliderVisual(fid, parsed, false);
        }
      });
      inp.addEventListener('change', () => {
        const parsed = parseInt(inp.value.trim(), 10);
        updateMemSliderVisual(fid, Number.isNaN(parsed) ? MEM_SLIDER_META[fid]?.min : parsed, true);
      });
    });

    host.querySelector('#memGroupRevertBtn')?.addEventListener('click', () => {
      sec.fields.forEach((f) => {
        f.value = f.savedValue;
      });
      renderMemorySection(host, sec);
      bindSettingsHostControls(host);
      syncDcpMemoryAssembler();
    });
    host.querySelector('#memGroupApplyBtn')?.addEventListener('click', () => {
      sec.fields.forEach((f) => {
        f.savedValue = f.value;
      });
      host.querySelectorAll('.energy-card.is-dirty').forEach((el) => el.classList.remove('is-dirty'));
      syncSecActionBtns();
      syncDcpMemoryAssembler();
      showNotice('已应用');
    });
  }

  // Tab 3: 存储 (VmStorageSettingsView.cs + VmStorageSettingsView.Add.cs + Screenshot 4)
  function renderStorageTab(host) {
    const vm = getVm();
    if (state.storageAddingMode) {
      const f = state.storageForm;
      const isDisk = f.driveType === 'HardDisk';
      const isVirtual = f.mediaSource === 'Virtual';
      const isNew = isVirtual && f.operation === 'New';
      host.innerHTML = `
        <div style="padding:12px 24px 24px 24px;display:flex;flex-direction:column;gap:12px;">
          <div class="settings-section-title">存储设备</div>
          <div class="winui-card" style="margin-bottom:0;">
            <div class="winui-card-row">
              ${renderSettingLabelHtml('驱动器类型', null)}
              <select class="winui-select" id="stFormDriveType" style="width:240px;">
                <option value="HardDisk" ${isDisk ? 'selected' : ''}>硬盘驱动器</option>
                <option value="DvdDrive" ${!isDisk ? 'selected' : ''}>光驱</option>
              </select>
            </div>
            <div class="winui-card-row">
              ${renderSettingLabelHtml('介质来源', null)}
              <select class="winui-select" id="stFormMediaSource" style="width:240px;">
                <option value="Virtual" ${isVirtual ? 'selected' : ''}>${isDisk ? '虚拟磁盘文件' : '光盘映像文件'}</option>
                <option value="Physical" ${!isVirtual ? 'selected' : ''}>${isDisk ? '物理磁盘' : '空光驱'}</option>
              </select>
            </div>
            ${
              isDisk && isVirtual
                ? `
              <div class="winui-card-row">
                ${renderSettingLabelHtml('操作方式', null)}
                <select class="winui-select" id="stFormOperation" style="width:240px;">
                  <option value="Existing" ${!isNew ? 'selected' : ''}>选择现有文件</option>
                  <option value="New" ${isNew ? 'selected' : ''}>创建新的虚拟硬盘</option>
                </select>
              </div>
            `
                : ''
            }
            ${
              isVirtual
                ? `
              <div class="winui-card-row">
                ${renderSettingLabelHtml('文件路径', null)}
                <div style="display:flex;gap:8px;align-items:center;">
                  <input type="text" class="winui-textbox" id="stFormFilePath" style="width:280px;" value="${f.filePath}" placeholder="${isNew ? '新虚拟硬盘保存路径 (.vhdx/.vhd)' : '选择现有虚拟硬盘文件'}" />
                  <button class="winui-btn">浏览</button>
                </div>
              </div>
            `
                : ''
            }
            ${
              isDisk && isNew
                ? `
              <div class="winui-card-row">
                ${renderSettingLabelHtml('容量 (GB)', null)}
                <input type="number" class="winui-textbox" id="stFormSizeGb" style="width:240px;" value="${f.sizeGb}" />
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('虚拟磁盘类型', null)}
                <select class="winui-select" id="stFormVhdType" style="width:240px;">
                  <option value="Dynamic" ${f.vhdType === 'Dynamic' ? 'selected' : ''}>动态扩展</option>
                  <option value="Fixed" ${f.vhdType === 'Fixed' ? 'selected' : ''}>固定大小</option>
                  <option value="Differencing" ${f.vhdType === 'Differencing' ? 'selected' : ''}>差异磁盘</option>
                </select>
              </div>
            `
                : ''
            }
          </div>

          <div class="settings-section-title" style="margin-top:8px;">插槽分配</div>
          <div class="winui-card" style="margin-bottom:0;">
            <div class="winui-card-row">
              ${renderSettingLabelHtml('自动分配插槽', null)}
              <div class="winui-switch ${f.autoLocation ? 'on' : ''}" id="stFormAutoSwitch">
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
              </div>
            </div>
            ${
              !f.autoLocation
                ? `
              <div class="winui-card-row">
                ${renderSettingLabelHtml('控制器类型', null)}
                <select class="winui-select" style="width:240px;"><option>SCSI</option><option>IDE</option></select>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('控制器编号', null)}
                <select class="winui-select" style="width:240px;"><option>0</option></select>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('插槽位置', null)}
                <select class="winui-select" style="width:240px;"><option>2</option><option>3</option></select>
              </div>
            `
                : ''
            }
          </div>

          <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:4px;">
            <button class="winui-btn" id="stFormCancelBtn">取消</button>
            <button class="winui-btn accent" id="stFormConfirmBtn">添加</button>
          </div>
        </div>
      `;

      host.querySelector('#stFormDriveType')?.addEventListener('change', (e) => {
        state.storageForm.driveType = e.target.value;
        renderStorageTab(host);
      });
      host.querySelector('#stFormMediaSource')?.addEventListener('change', (e) => {
        state.storageForm.mediaSource = e.target.value;
        renderStorageTab(host);
      });
      host.querySelector('#stFormOperation')?.addEventListener('change', (e) => {
        state.storageForm.operation = e.target.value;
        renderStorageTab(host);
      });
      host.querySelector('#stFormAutoSwitch')?.addEventListener('click', () => {
        state.storageForm.autoLocation = !state.storageForm.autoLocation;
        renderStorageTab(host);
      });
      host.querySelector('#stFormCancelBtn')?.addEventListener('click', () => {
        state.storageAddingMode = false;
        renderStorageTab(host);
      });
      host.querySelector('#stFormConfirmBtn')?.addEventListener('click', () => {
        const pathVal = host.querySelector('#stFormFilePath')?.value || 'D:\\Hyper-V\\new_disk.vhdx';
        const name = pathVal.split('\\').pop() || 'new_disk.vhdx';
        state.storageItems.push({
          id: `st-${Date.now()}`,
          driveType: state.storageForm.driveType,
          diskType: 'Virtual',
          displayName: name,
          sizeText: `${state.storageForm.sizeGb}.0 GB`,
          controllerType: 'SCSI',
          controllerNumber: 0,
          controllerLocation: state.storageItems.length,
          path: pathVal,
          hasIsoMedia: false,
          isPassthroughStale: false,
          expanded: true
        });
        state.storageAddingMode = false;
        renderStorageTab(host);
      });
      return;
    }

    // Normal storage list view — 1:1 match to Screenshot 4 (media_1794659693841_045a2033.png)
    const itemsHtml = state.storageItems
      .map((item, idx) => {
        const isDvd = item.driveType === 'DvdDrive';
        const glyph = isDvd ? '&#xE958;' : '&#xEC59;';
        const sourceText =
          item.diskType === 'Physical' ? '物理磁盘直通' : isDvd && !item.path ? '空光驱' : '虚拟介质文件';
        const showCompact = !isDvd && item.diskType === 'Virtual' && !vm.running;

        return `
          <div class="winui-expander ${item.expanded ? 'expanded' : ''}">
            <div class="winui-expander-header" data-st-toggle="${idx}">
              <div style="display:grid;grid-template-columns:auto 1fr auto;column-gap:16px;align-items:center;">
                <span class="fi" style="font-size:26px;">${glyph}</span>
                <div style="display:flex;flex-direction:column;gap:4px;">
                  <div style="font-size:15px;">${item.displayName}</div>
                  ${item.sizeText ? `<div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);">${item.sizeText}</div>` : ''}
                </div>
                <div style="display:flex;align-items:center;gap:4px;" data-stop-prop>
                  ${
                    item.path
                      ? `<button class="round-icon-btn" title="在资源管理器中定位" data-st-action="locate"><span class="fi fi-fluent" style="font-size:16px;">&#xF6B0;</span></button>`
                      : ''
                  }
                  ${
                    showCompact
                      ? `<button class="round-icon-btn" title="压缩虚拟磁盘" data-st-action="compact"><span class="fi fi-fluent" style="font-size:16px;">&#xF202;</span></button>`
                      : ''
                  }
                  <button class="round-icon-btn" title="${isDvd ? '更换 ISO' : '更换虚拟磁盘'}" data-st-action="edit">
                    <span class="fi fi-fluent" style="font-size:16px;">&#xF3DE;</span>
                  </button>
                  ${
                    item.hasIsoMedia
                      ? `<button class="round-icon-btn" title="弹出 ISO" data-st-action="eject"><span class="fi fi-fluent" style="font-size:16px;">&#xE0BE;</span></button>`
                      : ''
                  }
                  <button class="round-icon-btn" title="移除挂接（保留磁盘文件）" data-st-remove="${idx}">
                    <span class="fi fi-fluent" style="font-size:16px;">&#xF34D;</span>
                  </button>
                </div>
              </div>
              <span class="fi winui-expander-chevron">${item.expanded ? '&#xE70E;' : '&#xE70D;'}</span>
            </div>
            ${
              item.expanded
                ? `
              <div class="winui-expander-body">
                <div style="display:flex;flex-direction:column;gap:10px;">
                  <div style="display:grid;grid-template-columns:64px 1fr;column-gap:16px;">
                    <span style="color:var(--TextFillColorSecondaryBrush);">类型</span>
                    <span>${sourceText}</span>
                  </div>
                  <div style="display:grid;grid-template-columns:64px 1fr;column-gap:16px;">
                    <span style="color:var(--TextFillColorSecondaryBrush);">插槽</span>
                    <span>${item.controllerType} 控制器 ${item.controllerNumber} · 位置 ${item.controllerLocation}</span>
                  </div>
                  <div style="display:grid;grid-template-columns:64px 1fr;column-gap:16px;">
                    <span style="color:var(--TextFillColorSecondaryBrush);">路径</span>
                    <span style="word-break:break-all;">${item.path || '无'}</span>
                  </div>
                </div>
              </div>
            `
                : ''
            }
          </div>
        `;
      })
      .join('');

    host.innerHTML = `
      <div style="padding:12px 24px 24px 24px;display:flex;flex-direction:column;gap:12px;">
        <div style="display:flex;justify-content:flex-end;gap:8px;">
          <button class="winui-btn" id="storageRefreshBtn">刷新</button>
          <button class="winui-btn accent" id="storageAddBtn">添加存储</button>
        </div>
        <div style="display:flex;flex-direction:column;gap:12px;">
          ${itemsHtml || `<div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);">当前虚拟机没有挂载任何存储设备</div>`}
        </div>
      </div>
    `;

    host.querySelectorAll('[data-stop-prop]').forEach((el) => {
      el.addEventListener('click', (e) => e.stopPropagation());
    });
    host.querySelectorAll('[data-st-toggle]').forEach((hdr) => {
      hdr.addEventListener('click', () => {
        const idx = Number(hdr.getAttribute('data-st-toggle'));
        state.storageItems[idx].expanded = !state.storageItems[idx].expanded;
        renderStorageTab(host);
      });
    });
    host.querySelectorAll('[data-st-remove]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const idx = Number(btn.getAttribute('data-st-remove'));
        state.storageItems.splice(idx, 1);
        renderStorageTab(host);
      });
    });
    host.querySelector('#storageAddBtn')?.addEventListener('click', () => {
      state.storageAddingMode = true;
      renderStorageTab(host);
    });
  }

  // Tab 4: 显卡 (GPU-PV Layout Candidates)
  function renderGpuTab(host) {
    renderGpuWorkbench(host, {
      state,
      vm: getVm(),
      HOST_GPUS,
      INITIAL_GPU_PARTITIONS,
      LINUX_GPU_SCRIPTS,
      INITIAL_GPU_ASSIGNMENTS,
      renderTitleFirstKvItemHtml,
      renderSettingLabelHtml,
      renderDashboardSummary,
      renderVmList,
      showNotice,
      onLayoutNeeded: layoutDynamicGrids,
      bindHostControls: bindSettingsHostControls,
      notifyParentGpuState
    });
  }

  // Tab 5: 网络 (VmNetworkSettingsView.cs lines 19-210)
  function renderNetworkTab(host) {
    const vm = getVm();
    const adapter = vm.adapters[0];
    const vlanMode = state.netVlanMode;
    let vlanExtraRows = '';
    if (vlanMode === 'Access') {
      vlanExtraRows = `
        <div class="winui-card-row">
          ${renderSettingLabelHtml('VLAN ID', '0 表示不标记')}
          <input type="number" class="winui-textbox" style="width:180px;" value="0" />
        </div>
      `;
    } else if (vlanMode === 'Trunk') {
      vlanExtraRows = `
        <div class="winui-card-row">
          ${renderSettingLabelHtml('Native VLAN ID', null)}
          <input type="number" class="winui-textbox" style="width:180px;" value="1" />
        </div>
        <div class="winui-card-row">
          ${renderSettingLabelHtml('允许的 VLAN', '如 1-100,200')}
          <input type="text" class="winui-textbox" style="width:220px;" value="1-100" />
        </div>
      `;
    } else {
      vlanExtraRows = `
        <div class="winui-card-row">
          ${renderSettingLabelHtml('PVLAN 角色', null)}
          <select class="winui-select" style="width:180px;"><option>隔离</option><option>团体</option><option>混杂</option></select>
        </div>
        <div class="winui-card-row">
          ${renderSettingLabelHtml('主 VLAN ID', null)}
          <input type="number" class="winui-textbox" style="width:180px;" value="10" />
        </div>
        <div class="winui-card-row">
          ${renderSettingLabelHtml('辅助 VLAN ID', '混杂模式可填辅助列表，如 20-30')}
          <input type="text" class="winui-textbox" style="width:220px;" value="20" />
        </div>
      `;
    }

    host.innerHTML = `
      <div style="padding:8px 24px 24px 24px;display:flex;flex-direction:column;gap:16px;">
        <div style="display:flex;gap:8px;">
          <button class="winui-btn">添加网卡</button>
          <button class="winui-btn">刷新</button>
        </div>

        <div class="winui-expander expanded">
          <div class="winui-expander-header">
            <span style="font-size:15px;">${adapter.title}</span>
            <span class="fi winui-expander-chevron">&#xE70E;</span>
          </div>
          <div class="winui-expander-body">
            <div class="settings-section-title">基本连接</div>
            <div class="winui-card">
              <div class="winui-card-row">
                ${renderSettingLabelHtml('连接', null)}
                <div class="winui-switch on" data-on-text="已连接" data-off-text="已断开">
                  <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                  <span class="switch-state-label">已连接</span>
                </div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('虚拟交换机', null)}
                <div style="display:flex;gap:8px;">
                  <select class="winui-select" style="width:200px;">
                    <option selected>Default Switch</option>
                    <option>External vSwitch</option>
                  </select>
                  <button class="winui-btn">应用</button>
                </div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('MAC 地址', vm.running ? '关闭虚拟机后可修改' : '留空恢复自动分配')}
                <div style="display:flex;gap:8px;">
                  <input type="text" class="winui-textbox" style="width:180px;" value="${adapter.mac}" ${vm.running ? 'disabled' : ''} />
                  <button class="winui-btn" ${vm.running ? 'disabled' : ''}>应用</button>
                  <button class="winui-btn">复制</button>
                </div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('IP 地址', null)}
                <div>${vm.running ? `<button class="winui-btn">${vm.ipWhenRunning}  复制</button>` : `<span style="opacity:0.6;">暂无 IP 地址</span>`}</div>
              </div>
            </div>

            <div class="winui-card">
              <div class="winui-card-row">
                ${renderSettingLabelHtml('VLAN 隔离', null)}
                <select class="winui-select" id="netVlanModeSelect" style="width:180px;">
                  <option value="Access" ${vlanMode === 'Access' ? 'selected' : ''}>Access</option>
                  <option value="Trunk" ${vlanMode === 'Trunk' ? 'selected' : ''}>Trunk</option>
                  <option value="PrivateVlan" ${vlanMode === 'PrivateVlan' ? 'selected' : ''}>Private VLAN</option>
                </select>
              </div>
              ${vlanExtraRows}
              <div class="winui-card-row">
                <span></span>
                <button class="winui-btn">应用</button>
              </div>
            </div>

            <div class="winui-card">
              <div class="winui-card-row">
                ${renderSettingLabelHtml('最低带宽', 'Mbit/s，0 表示不保留')}
                <input type="text" class="winui-textbox" style="width:160px;" value="0" />
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('最高带宽', 'Mbit/s，0 表示不限速')}
                <input type="text" class="winui-textbox" style="width:160px;" value="0" />
              </div>
              <div class="winui-card-row">
                <span></span>
                <button class="winui-btn">应用</button>
              </div>
            </div>

            <div class="winui-card">
              <div class="winui-card-row">
                ${renderSettingLabelHtml('MAC 地址欺骗', null)}
                <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('DHCP 防护', null)}
                <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('路由器通告防护', null)}
                <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('允许网卡组队', null)}
                <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('端口镜像', null)}
                <select class="winui-select" style="width:140px;"><option>禁用</option><option>源端口</option><option>目标端口</option></select>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('风暴限制', '0 表示不限')}
                <input type="number" class="winui-textbox" style="width:140px;" value="0" />
              </div>
              <div class="winui-card-row">
                <span></span>
                <button class="winui-btn">应用</button>
              </div>
            </div>

            <div class="winui-card">
              <div class="winui-card-row">
                ${renderSettingLabelHtml('虚拟机队列 VMQ', null)}
                <div class="winui-switch on"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('SR-IOV', '需要宿主硬件与交换机支持')}
                <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                ${renderSettingLabelHtml('IPsec 任务卸载', null)}
                <div class="winui-switch on"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
              </div>
              <div class="winui-card-row">
                <span></span>
                <button class="winui-btn">应用</button>
              </div>
            </div>

            <button class="winui-btn">移除网卡</button>
          </div>
        </div>
      </div>
    `;

    host.querySelector('#netVlanModeSelect')?.addEventListener('change', (e) => {
      state.netVlanMode = e.target.value;
      renderSettingsArea();
    });
  }

  // Tab 6: 引导 (VmBootSettingsView.cs lines 16-64)
  function renderBootTab(host) {
    const itemsHtml = state.bootEntries
      .map(
        (b, idx) => `
        <div class="boot-order-item" data-boot-idx="${idx}" title="点击可将此引导项上移">
          <span class="fi" style="font-size:20px;">${b.glyph}</span>
          <div style="display:grid;grid-template-columns:auto 1fr;column-gap:16px;align-items:center;">
            <span style="font-size:15px;font-weight:500;">${b.name}</span>
            <span style="font-size:14px;opacity:0.5;">${b.description}</span>
          </div>
          <span class="fi" style="font-size:18px;color:var(--TextFillColorSecondaryBrush);">&#xE700;</span>
        </div>
      `
      )
      .join('');

    host.innerHTML = `<div style="padding:4px 24px 24px 24px;">${itemsHtml}</div>`;
    host.querySelectorAll('[data-boot-idx]').forEach((item) => {
      item.addEventListener('click', () => {
        const idx = Number(item.getAttribute('data-boot-idx'));
        if (idx > 0) {
          const tmp = state.bootEntries[idx - 1];
          state.bootEntries[idx - 1] = state.bootEntries[idx];
          state.bootEntries[idx] = tmp;
          renderBootTab(host);
        }
      });
    });
  }

  // Tab 7: 安全 (VmSecuritySettingsView.cs lines 17-38 — uses VmSettingsView.Row with \uE946 info button!)
  function renderSecurityTab(host) {
    host.innerHTML = `
      <div style="padding:4px 24px 24px 24px;">
        <div class="settings-section-title">安全启动</div>
        <div class="winui-card">
          <div class="winui-card-row">
            ${renderSettingLabelHtml('启用安全启动', '帮助防止启动时运行未经授权的代码')}
            <div class="winui-switch on"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
          <div class="winui-card-row">
            ${renderSettingLabelHtml('安全模板', null)}
            <select class="winui-select" style="width:260px;">
              <option selected>Microsoft Windows</option>
              <option>Microsoft UEFI 证书颁发机构</option>
              <option>开源屏蔽虚拟机</option>
            </select>
          </div>
        </div>

        <div class="settings-section-title">加密支持</div>
        <div class="winui-card">
          <div class="winui-card-row">
            ${renderSettingLabelHtml('启用 TPM', '可为虚拟机提供加密服务的受信任的平台模块')}
            <div class="winui-switch on"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
          <div class="winui-card-row">
            ${renderSettingLabelHtml('加密状态和虚拟机迁移流量', '加密虚拟机的保存状态与实时迁移流量')}
            <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
        </div>

        <div class="settings-section-title">安全策略</div>
        <div class="winui-card">
          <div class="winui-card-row">
            ${renderSettingLabelHtml('启用防护', '将虚拟机标记为受防护，会强制启用 TPM 与加密')}
            <div class="winui-switch"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
        </div>
      </div>
    `;
  }

  // 控制台 (split from legacy VmAdvancedSettingsView: VmConsoleSupportService + VmDisplaySettingsService + Keyboard)
  function renderConsoleTab(host) {
    const resOptions = ['默认（自适应）', '3840 x 2160', '2560 x 1440', '1920 x 1080']
      .map((r) => `<option ${state.advSettings.resolution === r ? 'selected' : ''}>${r}</option>`)
      .join('');
    host.innerHTML = `
      <div style="padding:4px 24px 24px 24px;">
        <div class="settings-section-title">控制台与显示</div>
        <div class="winui-card">
          <div class="winui-card-row">
            ${renderSettingLabelHtml('控制台支持', '仅第 2 代虚拟机有效。关闭后无法经控制台访问，适合串流/RDP 直连。')}
            <div class="winui-switch ${state.advSettings.consoleSupport ? 'on' : ''}" data-setting-key="consoleSupport"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
          <div class="winui-card-row">
            ${renderSettingLabelHtml('基本会话默认分辨率', '默认、预设或手动输入，宽高不可为奇数。')}
            <div style="display:flex;gap:8px;">
              <select class="winui-select" id="consoleResSelect" style="width:180px;">${resOptions}</select>
              <button class="winui-btn accent" id="consoleResApplyBtn">应用</button>
            </div>
          </div>
          <div class="winui-card-row">
            ${renderSettingLabelHtml('断开时锁定', '断开虚拟机控制台时锁定虚拟机系统')}
            <div class="winui-switch ${state.advSettings.lockOnDisconnect ? 'on' : ''}" data-setting-key="lockOnDisconnect"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
        </div>

        <div class="settings-section-title">输入行为</div>
        <div class="winui-card" style="margin-bottom:0;">
          <div class="winui-card-row">
            ${renderSettingLabelHtml('开机时启用数字小键盘', null)}
            <div class="winui-switch ${state.advSettings.numLock ? 'on' : ''}" data-setting-key="numLock"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
        </div>
      </div>
    `;
    host.querySelector('#consoleResApplyBtn')?.addEventListener('click', () => {
      const sel = host.querySelector('#consoleResSelect');
      if (sel) state.advSettings.resolution = sel.value;
    });
  }

  // 电源 (split from legacy VmAdvancedSettingsView: VmPowerService)
  function renderPowerTab(host) {
    host.innerHTML = `
      <div style="padding:4px 24px 24px 24px;">
        <div class="settings-section-title">电源行为与电池</div>
        <div class="winui-card" style="margin-bottom:0;">
          <div class="winui-card-row">
            ${renderSettingLabelHtml('虚拟机休眠（S4）', '允许虚拟机使用 S4 休眠和快速启动')}
            <div class="winui-switch ${state.advSettings.hibernation ? 'on' : ''}" data-setting-key="hibernation"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
          <div class="winui-card-row">
            ${renderSettingLabelHtml('重启时改为关机', '虚拟机系统请求重启时，关闭虚拟机而不是重新启动')}
            <div class="winui-switch ${state.advSettings.turnOffOnRestart ? 'on' : ''}" data-setting-key="turnOffOnRestart"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
          <div class="winui-card-row">
            ${renderSettingLabelHtml('电池状态透传', '允许虚拟机读取主机的电池状态和剩余电量')}
            <div class="winui-switch ${state.advSettings.batteryPassthrough ? 'on' : ''}" data-setting-key="batteryPassthrough"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
        </div>
      </div>
    `;
  }

  // 串口 (VmSerialPortSettingsView.cs)
  function renderSerialTab(host) {
    const vm = getVm();
    const canModify = !vm.running;
    const ports = state.serialPortsByVm[vm.id] || (state.serialPortsByVm[vm.id] = structuredClone(INITIAL_SERIAL_PORTS));
    const lockReason = '关闭虚拟机后可修改';

    const normalizePipe = (raw) => {
      const trimmed = String(raw || '').trim();
      if (!trimmed) return '';
      if (trimmed.startsWith('\\\\')) return trimmed;
      return `\\\\.\\pipe\\${trimmed.replace(/^\\+/, '')}`;
    };

    const sanitizePipeSegment = (name) =>
      String(name || 'vm')
        .trim()
        .replace(/[^A-Za-z0-9._-]+/g, '-')
        .replace(/^-+|-+$/g, '') || 'vm';

    const buildDefaultPipe = (port) =>
      `\\\\.\\pipe\\${sanitizePipeSegment(vm.name)}-com${port.portNumber}`;

    const portsHtml = ports.map((port, idx) => {
      const isEnabled = Boolean(port.pipePath && port.pipePath.trim());
      const pipeEditable = canModify && isEnabled;
      const debugEditable = canModify && (isEnabled || Boolean(port.debuggerMode));
      const enableDesc = canModify
        ? '开启后将此串口连接到宿主机命名管道；关闭则断开连接并清空管道配置。'
        : lockReason;
      const pipeDesc = !canModify
        ? lockReason
        : isEnabled
          ? '输入短名称会自动补全为 \\\\.\\pipe\\名称，供 PuTTY / WinDbg 连接。'
          : '请先开启上方开关以启用命名管道连接。';
      const debugDesc = !canModify
        ? lockReason
        : isEnabled
          ? '为 Windows 内核调试器（WinDbg）优化传输；普通串口控制台无需开启。'
          : '需先启用命名管道后才可开启内核调试模式。';
      return `
        <div>
          <div class="settings-section-title">${port.name}</div>
          <div class="winui-card" style="margin-bottom:0;" data-serial-idx="${idx}">
            <div class="winui-card-row">
              ${renderSettingLabelHtml('命名管道连接', enableDesc)}
              <div class="winui-switch serial-enable-switch ${isEnabled ? 'on' : ''} ${canModify ? '' : 'disabled'}" data-serial-idx="${idx}" data-bound-switch="true">
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
              </div>
            </div>
            <div class="winui-card-row">
              ${renderSettingLabelHtml('命名管道路径', pipeDesc)}
              <div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;">
                <input type="text" class="winui-textbox serial-pipe-input" data-serial-idx="${idx}" value="${escapeHtmlText(port.pipePath || '')}" placeholder="管道名或 \\\\.\\pipe\\名称" style="width:240px;" ${pipeEditable ? '' : 'disabled'} />
                <button type="button" class="winui-btn accent serial-pipe-apply" data-serial-idx="${idx}" ${pipeEditable ? '' : 'disabled'}>应用</button>
                <button type="button" class="winui-btn serial-pipe-copy" data-serial-idx="${idx}" ${isEnabled ? '' : 'disabled'}>复制</button>
              </div>
            </div>
            <div class="winui-card-row">
              ${renderSettingLabelHtml('内核调试模式', debugDesc)}
              <div class="winui-switch serial-debug-switch ${port.debuggerMode ? 'on' : ''} ${debugEditable ? '' : 'disabled'}" data-serial-idx="${idx}" data-bound-switch="true">
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
              </div>
            </div>
          </div>
        </div>
      `;
    }).join('');

    host.innerHTML = `
      <div style="padding:4px 24px 24px 24px;display:flex;flex-direction:column;gap:4px;">
        ${portsHtml}
      </div>
    `;

    host.querySelectorAll('.serial-enable-switch').forEach((sw) => {
      sw.addEventListener('click', () => {
        if (!canModify) return;
        const idx = Number(sw.dataset.serialIdx);
        const port = ports[idx];
        const isEnabled = Boolean(port.pipePath && port.pipePath.trim());
        port.pipePath = isEnabled ? '' : buildDefaultPipe(port);
        renderSerialTab(host);
      });
    });

    host.querySelectorAll('.serial-pipe-apply').forEach((btn) => {
      btn.addEventListener('click', () => {
        if (!canModify) return;
        const idx = Number(btn.dataset.serialIdx);
        const port = ports[idx];
        if (!port.pipePath || !port.pipePath.trim()) return;
        const input = host.querySelector(`.serial-pipe-input[data-serial-idx="${idx}"]`);
        const normalized = normalizePipe(input?.value);
        if (!normalized) return;
        port.pipePath = normalized;
        renderSerialTab(host);
      });
    });

    host.querySelectorAll('.serial-pipe-copy').forEach((btn) => {
      btn.addEventListener('click', () => {
        const idx = Number(btn.dataset.serialIdx);
        const port = ports[idx];
        if (!port.pipePath) return;
        navigator.clipboard?.writeText(port.pipePath).catch(() => {});
      });
    });

    host.querySelectorAll('.serial-debug-switch').forEach((sw) => {
      sw.addEventListener('click', () => {
        if (!canModify) return;
        const idx = Number(sw.dataset.serialIdx);
        const port = ports[idx];
        const isEnabled = Boolean(port.pipePath && port.pipePath.trim());
        if (!isEnabled && !port.debuggerMode) return;
        port.debuggerMode = !port.debuggerMode;
        renderSerialTab(host);
      });
    });

    bindSettingsHostControls(host);
  }

  // 存储策略 (split from legacy VmAdvancedSettingsView: VmStorageService)
  function renderStoragePolicyTab(host) {
    host.innerHTML = `
      <div style="padding:4px 24px 24px 24px;">
        <div class="settings-section-title">存储与控制器策略</div>
        <div class="winui-card" style="margin-bottom:0;">
          <div class="winui-card-row">
            ${renderSettingLabelHtml('完整 SCSI 命令集', '允许虚拟机向挂载的物理磁盘发送未经筛选的 SCSI 命令')}
            <div class="winui-switch ${state.advSettings.fullScsi ? 'on' : ''}" data-setting-key="fullScsi"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
        </div>
      </div>
    `;
  }

  // Tab 9: PCIe (VmPcieSettingsView.cs lines 104-186 — uses 2-line title + subtitle and OnContent="启用" OffContent="关闭")
  function renderPcieTab(host) {
    const vm = getVm();
    const devices = state.pcieDevicesByVm[vm.id] || [];
    const devHtml =
      devices.length === 0
        ? `<div style="font-size:12px;color:var(--TextFillColorSecondaryBrush);">没有已分配的 PCIe 设备</div>`
        : devices
            .map(
              (d) => `
          <div class="winui-expander expanded">
            <div class="winui-expander-header">
              <div style="display:grid;grid-template-columns:auto 1fr auto;column-gap:16px;align-items:center;">
                <span class="fi" style="font-size:20px;">${d.glyph}</span>
                <span style="font-size:15px;">${d.name}</span>
                <select class="winui-select" style="width:140px;"><option selected>半虚拟化</option><option>仿真</option></select>
              </div>
              <span class="fi winui-expander-chevron">&#xE70E;</span>
            </div>
            <div class="winui-expander-body">
              <div style="display:flex;flex-direction:column;gap:10px;">
                <div style="display:grid;grid-template-columns:80px 1fr;column-gap:16px;"><span style="color:var(--TextFillColorSecondaryBrush);">类别</span><span>${d.classType}</span></div>
                <div style="display:grid;grid-template-columns:80px 1fr;column-gap:16px;"><span style="color:var(--TextFillColorSecondaryBrush);">实例 ID</span><span>${d.instanceId}</span></div>
                <div style="display:grid;grid-template-columns:80px 1fr;column-gap:16px;"><span style="color:var(--TextFillColorSecondaryBrush);">路径</span><span>${d.path}</span></div>
                <div style="display:grid;grid-template-columns:80px 1fr;column-gap:16px;"><span style="color:var(--TextFillColorSecondaryBrush);">制造商</span><span>${d.vendor}</span></div>
              </div>
            </div>
          </div>
        `
            )
            .join('');

    host.innerHTML = `
      <div style="padding:4px 24px 24px 24px;display:flex;flex-direction:column;gap:18px;">
        <div>
          <div class="settings-section-title">PCIe 呈现方式</div>
          <div class="winui-card" style="margin-bottom:0;">
            <div class="winui-card-row">
              <div>
                <div>PCIe 仿真</div>
                <div class="winui-row-desc">让设备以标准 PCIe 设备的方式出现在虚拟机中</div>
              </div>
              <div class="winui-switch on" data-on-text="启用" data-off-text="关闭">
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                <span class="switch-state-label">启用</span>
              </div>
            </div>
            <div class="winui-card-row">
              <div>
                <div>PCIe 拓扑</div>
                <div class="winui-row-desc">呈现设备的 PCIe 拓扑结构</div>
              </div>
              <select class="winui-select" style="width:140px;"><option selected>默认</option><option>仿照主机</option></select>
            </div>
          </div>
        </div>

        <div>
          <div class="settings-section-title">启动</div>
          <div class="winui-card" style="margin-bottom:0;">
            <div class="winui-card-row">
              <div>
                <div>启动阶段 vPCI</div>
                <div class="winui-row-desc">在 UEFI 中加载 vPCI 驱动，以识别受支持的设备（NVMe、AziHsm）</div>
              </div>
              <div class="winui-switch" data-on-text="启用" data-off-text="关闭">
                <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                <span class="switch-state-label">关闭</span>
              </div>
            </div>
          </div>
        </div>

        <div>
          <div class="settings-section-title">已分配的 PCIe 设备</div>
          ${devHtml}
        </div>
      </div>
    `;
  }

  // Bonus View: 时空 (VmSpacetimeSettingsView.cs lines 48-366)
  function renderSpacetimeTab(host) {
    const sel = SPACETIME_NODES.find((n) => n.id === state.spacetimeSelectedId) || SPACETIME_NODES[3];
    const isCurrent = sel.type === 'Current';

    const linesSvg = SPACETIME_NODES.filter((n) => n.parentId)
      .map((n) => {
        const p = SPACETIME_NODES.find((x) => x.id === n.parentId);
        if (!p) return '';
        const x1 = p.x + 140;
        const y1 = p.y + 40;
        const x2 = n.x;
        const y2 = n.y + 40;
        if (n.wormhole) {
          return `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="Gold" stroke-width="4" />`;
        }
        return `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="rgba(160,160,160,0.6)" stroke-width="2" stroke-dasharray="4,3" />`;
      })
      .join('');

    const nodesHtml = SPACETIME_NODES.map((n) => {
      const active = n.id === sel.id;
      const borderCol = active ? 'var(--VmAccentBrush)' : n.wormhole ? 'Gold' : 'var(--CardStrokeColorDefaultBrush)';
      return `
        <div data-st-node="${n.id}" style="position:absolute;left:${n.x}px;top:${n.y}px;width:140px;height:80px;border-radius:8px;border:2px solid ${borderCol};background:#1A2230;overflow:hidden;cursor:pointer;display:flex;align-items:flex-end;">
          <div style="width:100%;padding:4px 6px;background:rgba(0,0,0,0.65);font-size:11px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${n.name}</div>
        </div>
      `;
    }).join('');

    host.innerHTML = `
      <div style="padding:4px 12px 12px 12px;height:100%;display:grid;grid-template-rows:auto 1fr;row-gap:12px;">
        <div style="display:flex;justify-content:space-between;align-items:center;">
          <div style="display:flex;align-items:center;gap:10px;">
            <span>检查点</span>
            <div class="winui-switch on" data-on-text="启用" data-off-text="关闭">
              <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
              <span class="switch-state-label">启用</span>
            </div>
          </div>
          <button class="winui-btn">导出拓扑图</button>
        </div>

        <div style="display:grid;grid-template-columns:1fr 220px;column-gap:12px;min-height:420px;">
          <div style="position:relative;border-radius:8px;border:1px solid var(--CardStrokeColorDefaultBrush);background:var(--ControlFillColorDefaultBrush);overflow:auto;">
            <svg width="900" height="420" style="position:absolute;inset:0;pointer-events:none;">${linesSvg}</svg>
            ${nodesHtml}
          </div>

          <div style="border-radius:8px;border:1px solid var(--CardStrokeColorDefaultBrush);background:var(--CardBackgroundFillColorDefaultBrush);padding:12px;display:flex;flex-direction:column;gap:8px;">
            <div style="font-size:13px;font-weight:600;">时空详情</div>
            <div style="padding:8px;border-radius:4px;background:var(--ControlFillColorTertiaryBrush);">
              <div style="font-weight:600;">${sel.name}</div>
              <div style="font-size:11px;opacity:0.6;margin-top:2px;">${sel.date}</div>
            </div>
            <div style="font-size:13px;font-weight:600;margin-top:4px;">操作</div>
            ${
              isCurrent
                ? `
              <label style="font-size:12px;display:flex;gap:6px;align-items:center;"><input type="radio" name="stMode" checked /> 连续时空</label>
              <div style="font-size:11px;opacity:0.6;margin-left:20px;">不中断当前运行</div>
              <label style="font-size:12px;display:flex;gap:6px;align-items:center;"><input type="radio" name="stMode" /> 静止时空</label>
              <div style="font-size:11px;opacity:0.6;margin-left:20px;">短暂冻结以保持一致</div>
              <button class="winui-btn accent" style="width:100%;margin-top:6px;">创建时空</button>
            `
                : `
              <button class="winui-btn" style="width:100%;justify-content:flex-start;"><span class="fi">&#xE8AB;</span><span>穿梭</span></button>
              <button class="winui-btn" style="width:100%;justify-content:flex-start;"><span class="fi">&#xE71B;</span><span>${sel.wormhole ? '关闭虫洞' : '开启虫洞'}</span></button>
              <button class="winui-btn" style="width:100%;justify-content:flex-start;"><span class="fi">&#xE97A;</span><span>时空收束</span></button>
              <button class="winui-btn danger" style="width:100%;justify-content:flex-start;"><span class="fi">&#xE74D;</span><span>删除</span></button>
            `
            }
          </div>
        </div>
      </div>
    `;

    host.querySelectorAll('[data-st-node]').forEach((el) => {
      el.addEventListener('click', () => {
        state.spacetimeSelectedId = el.getAttribute('data-st-node');
        renderSpacetimeTab(host);
      });
    });
    bindSettingsHostControls(host);
  }

  // Switch between OverviewScroll and SettingsArea, and sync bottom workbench bar
  function renderActiveWorkspace() {
    const overviewScroll = document.getElementById('vmOverviewScroll');
    const settingsArea = document.getElementById('vmSettingsArea');
    const isOverview = state.activeTabKey === 'overview';
    if (overviewScroll && settingsArea) {
      overviewScroll.hidden = !isOverview;
      settingsArea.hidden = isOverview;
    }
    if (isOverview) {
      if (state.dcpMode !== 'overview') setDcpMode('overview', false);
      renderOverview();
    } else {
      if (state.activeTabKey === 'processor' && state.dcpMode !== 'compute') {
        setDcpMode('compute', false);
      }
      renderSettingsArea();
    }
    requestAnimationFrame(layoutDynamicGrids);

    const tabObj = PAGE_TABS.find((t) => t.key === state.activeTabKey);
    const srcTag = document.getElementById('wbSourceFileTag');
    if (srcTag) {
      if (state.navTarget === 'host') srcTag.textContent = 'UI/Pages/HostPage.xaml';
      else if (state.navTarget === 'settings') srcTag.textContent = 'UI/Pages/SettingsPage.xaml';
      else if (state.activeTabKey === 'devices' || state.activeTabKey === 'advanced') {
        const subKey = state.activeTabKey === 'devices' ? state.deviceSubKey : state.advancedSubKey;
        const subObj = (MERGED_SUBNAV_GROUPS[state.activeTabKey] || []).find((it) => it.key === subKey);
        srcTag.textContent = `UI/Pages/VirtualMachines/${subObj?.file || tabObj?.file || ''}`;
      } else if (tabObj) srcTag.textContent = `UI/Pages/VirtualMachines/${tabObj.file}`;
    }

    document.querySelectorAll('[data-wb-jump]').forEach((chip) => {
      const target = chip.getAttribute('data-wb-jump');
      const active =
        (state.navTarget === 'host' && target === 'nav:host') ||
        (state.navTarget === 'settings' && target === 'nav:settings') ||
        (state.navTarget === 'vm' && target === state.activeTabKey);
      chip.classList.toggle('active', active);
    });

    notifyParentGpuState();
  }

  function switchNavigationTarget(target) {
    state.navTarget = target;
    document.querySelectorAll('[data-nav-target]').forEach((item) => {
      item.classList.toggle('selected', item.getAttribute('data-nav-target') === target);
    });
    const minimalTitle = document.getElementById('minimalNavTitle');
    if (minimalTitle) {
      minimalTitle.textContent = target === 'host' ? '宿主' : target === 'settings' ? '应用设置' : '虚拟机';
    }
    const navView = document.getElementById('winuiNavView');
    const backdrop = document.getElementById('navDrawerBackdrop');
    if (navView) navView.classList.remove('minimal-open');
    if (backdrop) backdrop.hidden = true;
    const pageVm = document.getElementById('pageVirtualMachines');
    const pageHost = document.getElementById('pageHost');
    const pageSettings = document.getElementById('pageSettings');
    if (pageVm) pageVm.style.display = target === 'vm' ? 'grid' : 'none';
    if (pageHost) {
      pageHost.hidden = target !== 'host';
      pageHost.style.display = target === 'host' ? 'grid' : 'none';
    }
    if (pageSettings) pageSettings.hidden = target !== 'settings';
    if (target === 'host') renderHostPage();
    renderActiveWorkspace();
  }

  function toggleVmPower() {
    const vm = getVm();
    vm.running = !vm.running;
    syncDcpMemoryAssembler();
    renderVmList();
    renderDashboardSummary();
    renderActiveWorkspace();
  }

  // Bind Top-level UI Events
  document.getElementById('paneToggleBtn')?.addEventListener('click', () => {
    const navView = document.getElementById('winuiNavView');
    const backdrop = document.getElementById('navDrawerBackdrop');
    if (!navView) return;
    if (navView.classList.contains('minimal')) {
      const open = navView.classList.toggle('minimal-open');
      if (backdrop) backdrop.hidden = !open;
    } else {
      navView.classList.toggle('compact');
      layoutDynamicGrids();
      setTimeout(layoutDynamicGrids, 180);
      const bar = document.getElementById('vmPageTabsBar');
      if (bar) requestAnimationFrame(() => ensureSelectedTabVisible(bar));
    }
  });

  document.getElementById('navDrawerBackdrop')?.addEventListener('click', () => {
    const navView = document.getElementById('winuiNavView');
    const backdrop = document.getElementById('navDrawerBackdrop');
    if (navView) navView.classList.remove('minimal-open');
    if (backdrop) backdrop.hidden = true;
  });

  document.querySelectorAll('[data-nav-target]').forEach((item) => {
    item.addEventListener('click', () => {
      switchNavigationTarget(item.getAttribute('data-nav-target'));
    });
  });

  document.getElementById('vmSearchBox')?.addEventListener('input', (e) => {
    state.searchQuery = e.target.value;
    renderVmList();
  });

  document.getElementById('vmPowerMainBtn')?.addEventListener('click', toggleVmPower);
  document.getElementById('vmPowerDropBtn')?.addEventListener('click', (e) => {
    e.stopPropagation();
    const flyout = document.getElementById('vmPowerFlyout');
    if (flyout) flyout.hidden = !flyout.hidden;
  });

  document.querySelectorAll('[data-power-action]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const act = btn.getAttribute('data-power-action');
      const flyout = document.getElementById('vmPowerFlyout');
      if (flyout) flyout.hidden = true;
      if (act === 'turnoff' || act === 'save') {
        getVm().running = false;
        renderVmList();
        renderDashboardSummary();
        renderActiveWorkspace();
      }
    });
  });

  document.getElementById('metricCpuCard')?.addEventListener('click', () => {
    state.selectedResource = 'cpu';
    renderOverview();
  });
  document.getElementById('metricMemCard')?.addEventListener('click', () => {
    state.selectedResource = 'mem';
    renderOverview();
  });

  document.getElementById('dashboardNoticeCloseBtn')?.addEventListener('click', () => {
    const bar = document.getElementById('dashboardNoticeBar');
    if (bar) bar.hidden = true;
  });

  document.getElementById('settingsNoticeCloseBtn')?.addEventListener('click', () => {
    const bar = document.getElementById('settingsNoticeBar');
    if (bar) bar.hidden = true;
  });

  // SettingsPage.xaml appearance bindings
  document.getElementById('settingsThemePicker')?.addEventListener('change', (e) => {
    const val = e.target.value;
    winEl.setAttribute('data-theme', val === 'light' ? 'light' : 'dark');
    applyAccentVariables();
    if (state.activeTabKey === 'overview') renderOverview();
  });

  document.getElementById('settingsAccentSwitch')?.addEventListener('click', (e) => {
    const sw = e.currentTarget;
    sw.classList.toggle('on');
    state.useThemeAccentForHeader = sw.classList.contains('on');
    applyAccentVariables();
    if (state.activeTabKey === 'overview') renderOverview();
  });

  document.querySelectorAll('[data-swatch-hex]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.accentHex = btn.getAttribute('data-swatch-hex');
      document.querySelectorAll('[data-swatch-hex] .swatch-check').forEach((chk) => {
        chk.style.visibility = 'hidden';
      });
      const check = btn.querySelector('.swatch-check');
      if (check) check.style.visibility = 'visible';
      applyAccentVariables();
      if (state.activeTabKey === 'overview') renderOverview();
    });
  });

  document.getElementById('settingsResetColorBtn')?.addEventListener('click', () => {
    state.accentHex = '#4CC2FF';
    applyAccentVariables();
    if (state.activeTabKey === 'overview') renderOverview();
  });

  // Global click handler for SettingInfoButton Flyout & PowerFlyout dismissal
  winEl.addEventListener('click', (e) => {
    const infoBtn = e.target.closest('[data-info-desc]');
    const flyout = document.getElementById('settingInfoFlyout');
    if (infoBtn && flyout) {
      e.stopPropagation();
      const desc = infoBtn.getAttribute('data-info-desc') || '';
      const winRect = winEl.getBoundingClientRect();
      const btnRect = infoBtn.getBoundingClientRect();
      flyout.textContent = desc;
      flyout.hidden = false;
      const left = Math.max(12, Math.min(winRect.width - 352, btnRect.left - winRect.left - 8));
      const top = btnRect.bottom - winRect.top + 6;
      flyout.style.left = `${left}px`;
      flyout.style.top = `${top}px`;
      return;
    }
    if (flyout && !flyout.hidden && !e.target.closest('#settingInfoFlyout')) {
      flyout.hidden = true;
    }
    const powerFlyout = document.getElementById('vmPowerFlyout');
    if (powerFlyout && !powerFlyout.hidden && !e.target.closest('#vmPowerWrap')) {
      powerFlyout.hidden = true;
    }
  });

  // Window Dragging, Maximize/Restore, and Edge/Corner Resizing
  let winOffsetX = 0;
  let winOffsetY = 0;
  let isDraggingWin = false;
  let dragStartX = 0;
  let dragStartY = 0;

  const titleBar = document.getElementById('winuiTitleBar');
  const maxBtn = document.getElementById('winMaximizeBtn');
  const maxIcon = document.getElementById('winMaximizeIcon');

  function toggleMaximize() {
    const isMax = winEl.classList.toggle('maximized');
    if (maxIcon) maxIcon.innerHTML = isMax ? '&#xE923;' : '&#xE922;';
    document.body.style.overflow = isMax ? 'hidden' : '';
    requestAnimationFrame(() => {
      layoutDynamicGrids();
      const bar = document.getElementById('vmPageTabsBar');
      if (bar) ensureSelectedTabVisible(bar);
    });
  }

  maxBtn?.addEventListener('click', toggleMaximize);
  titleBar?.addEventListener('dblclick', (e) => {
    if (e.target.closest('.caption-buttons')) return;
    toggleMaximize();
  });

  titleBar?.addEventListener('pointerdown', (e) => {
    if (e.button !== 0 || e.target.closest('.caption-buttons') || winEl.classList.contains('maximized')) return;
    isDraggingWin = true;
    dragStartX = e.clientX - winOffsetX;
    dragStartY = e.clientY - winOffsetY;
    titleBar.setPointerCapture(e.pointerId);
  });

  titleBar?.addEventListener('pointermove', (e) => {
    if (!isDraggingWin) return;
    winOffsetX = e.clientX - dragStartX;
    winOffsetY = e.clientY - dragStartY;
    winEl.style.transform = `translate3d(${winOffsetX}px, ${winOffsetY}px, 0)`;
  });

  titleBar?.addEventListener('pointerup', (e) => {
    if (!isDraggingWin) return;
    isDraggingWin = false;
    titleBar.releasePointerCapture(e.pointerId);
  });

  // 8-Way Window Resizing (Top / Bottom / Left / Right / 4 Corners)
  // dirX: -1 (left), 0 (none), 1 (right)
  // dirY: -1 (top),  0 (none), 1 (bottom)
  function bindResize(handleId, dirX, dirY) {
    const handle = document.getElementById(handleId);
    if (!handle) return;
    let resizing = false;
    let startX = 0;
    let startY = 0;
    let startW = 0;
    let startH = 0;
    let startOffX = 0;
    let startOffY = 0;

    handle.addEventListener('pointerdown', (e) => {
      if (e.button !== 0 || winEl.classList.contains('maximized')) return;
      e.preventDefault();
      e.stopPropagation();
      resizing = true;
      startX = e.clientX;
      startY = e.clientY;
      const rect = winEl.getBoundingClientRect();
      startW = rect.width;
      startH = rect.height;
      startOffX = winOffsetX;
      startOffY = winOffsetY;
      handle.setPointerCapture(e.pointerId);
    });

    handle.addEventListener('pointermove', (e) => {
      if (!resizing) return;
      if (dirX !== 0) {
        const rawW = startW + dirX * (e.clientX - startX);
        const nextW = Math.max(520, Math.min(2400, rawW));
        const deltaW = nextW - startW;
        winEl.style.maxWidth = 'none';
        winEl.style.width = `${nextW}px`;
        // Parent is horizontally centered (align-items: center), so shift by half deltaW to anchor opposite edge
        winOffsetX = startOffX + (dirX * deltaW) / 2;
      }
      if (dirY !== 0) {
        const rawH = startH + dirY * (e.clientY - startY);
        const nextH = Math.max(400, Math.min(2000, rawH));
        const deltaH = nextH - startH;
        winEl.style.height = `${nextH}px`;
        // Parent is top-aligned in column flex, so resizing from top shifts Y by -deltaH to anchor bottom edge
        if (dirY === -1) {
          winOffsetY = startOffY - deltaH;
        }
      }
      winEl.style.transform = `translate3d(${winOffsetX}px, ${winOffsetY}px, 0)`;
      layoutDynamicGrids();
      const bar = document.getElementById('vmPageTabsBar');
      if (bar) ensureSelectedTabVisible(bar);
    });

    handle.addEventListener('pointerup', (e) => {
      if (!resizing) return;
      resizing = false;
      handle.releasePointerCapture(e.pointerId);
    });
  }

  bindResize('winResizeT', 0, -1);
  bindResize('winResizeB', 0, 1);
  bindResize('winResizeL', -1, 0);
  bindResize('winResizeR', 1, 0);
  bindResize('winResizeTL', -1, -1);
  bindResize('winResizeTR', 1, -1);
  bindResize('winResizeBL', -1, 1);
  bindResize('winResizeBR', 1, 1);

  // Right-side Design Candidates Panel bindings
  const summaryCard = document.getElementById('vmSummaryCard');
  const dashboardBorder = document.getElementById('vmDashboardBorder');
  document.querySelectorAll('[data-summary-candidate]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const variant = btn.getAttribute('data-summary-candidate') || 'mid-3row';
      if (summaryCard) {
        summaryCard.setAttribute('data-summary-variant', variant);
      }
      if (dashboardBorder) {
        dashboardBorder.classList.toggle('merged-tabs-mode', variant === 'merged-tabs');
      }
      requestAnimationFrame(() => {
        layoutDynamicGrids();
        const bar = document.getElementById('vmPageTabsBar');
        if (bar) ensureSelectedTabVisible(bar);
      });
    });
  });

  document.querySelectorAll('[data-tag-toggle]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const key = btn.getAttribute('data-tag-toggle');
      if (!key) return;
      if (!state.activeTags) {
        state.activeTags = {
          os: true,
          firmware: true,
          gpu: true,
          gpuHideEmpty: false,
          ip: true,
          spec: false,
          version: false,
          folder: false
        };
      }
      state.activeTags[key] = !state.activeTags[key];
      btn.classList.toggle('active', !!state.activeTags[key]);
      renderDashboardSummary();
    });
  });

  document.getElementById('dcpTogglePowerBtn')?.addEventListener('click', toggleVmPower);

  function syncDcpLangButtons() {
    const langBtn = document.getElementById('dcpToggleLangBtn');
    const shortBtn = document.getElementById('dcpToggleEnShortBtn');
    if (langBtn) {
      langBtn.classList.toggle('active', state.uiLang === 'en');
      langBtn.textContent = state.uiLang === 'en' ? '当前: EN' : '中 / EN';
    }
    if (shortBtn) {
      shortBtn.classList.toggle('active', state.uiLang === 'en' && state.enShort);
      shortBtn.textContent = state.enShort ? 'EN: 短词' : 'EN: 标准';
    }
  }

  document.getElementById('dcpToggleLangBtn')?.addEventListener('click', () => {
    state.uiLang = state.uiLang === 'zh' ? 'en' : 'zh';
    syncDcpLangButtons();
    renderPageTabs();
    renderActiveWorkspace();
  });

  document.getElementById('dcpToggleEnShortBtn')?.addEventListener('click', () => {
    if (state.uiLang !== 'en') {
      state.uiLang = 'en';
      state.enShort = true;
    } else {
      state.enShort = !state.enShort;
    }
    syncDcpLangButtons();
    renderPageTabs();
    renderActiveWorkspace();
  });
  function syncDcpGpuButton() {
    const gpuBtn = document.getElementById('dcpToggleGpuBtn');
    if (!gpuBtn) return;
    const vm = getVm();
    const hasGpu = (state.gpuAssignmentsByVm[vm.id] || []).length > 0;
    gpuBtn.classList.toggle('active', hasGpu);
    gpuBtn.textContent = hasGpu ? '显卡: 已分配' : '显卡: 未分配';
  }

  document.getElementById('dcpToggleGpuBtn')?.addEventListener('click', () => {
    const vm = getVm();
    const list = state.gpuAssignmentsByVm[vm.id] || [];
    if (list.length > 0) {
      state.gpuAssignmentsByVm[vm.id] = [];
    } else {
      state.gpuAssignmentsByVm[vm.id] = structuredClone(INITIAL_GPU_ASSIGNMENTS);
    }
    syncDcpGpuButton();
    renderDashboardSummary();
    renderActiveWorkspace();
  });

  document.getElementById('dcpToggleStressBtn')?.addEventListener('click', (e) => {
    state.stressDataMode = !state.stressDataMode;
    state.selectedGpuIdx = 0;
    state.selectedDiskIdx = 0;
    state.selectedNetIdx = 0;
    const btn = e.currentTarget;
    btn.classList.toggle('active', state.stressDataMode);
    btn.textContent = state.stressDataMode ? '模拟: 多设备超长名' : '模拟: 常规设备';
    if (state.navTarget !== 'vm') switchNavigationTarget('vm');
    if (state.activeTabKey !== 'overview') {
      state.activeTabKey = 'overview';
      renderPageTabs();
    }
    renderActiveWorkspace();
  });

  document.getElementById('dcpToggleAccentBtn')?.addEventListener('click', () => {
    state.useThemeAccentForHeader = !state.useThemeAccentForHeader;
    applyAccentVariables();
    if (state.navTarget !== 'vm') switchNavigationTarget('vm');
    if (state.activeTabKey !== 'overview') {
      state.activeTabKey = 'overview';
      renderPageTabs();
    }
    renderActiveWorkspace();
  });

  document.querySelectorAll('[data-overview-layout]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.overviewLayout = btn.getAttribute('data-overview-layout') || 'tm-adaptive';
      document.querySelectorAll('[data-overview-layout]').forEach((chip) => {
        chip.classList.toggle('active', chip === btn);
      });
      if (state.navTarget !== 'vm') {
        switchNavigationTarget('vm');
      }
      if (state.activeTabKey !== 'overview') {
        state.activeTabKey = 'overview';
        renderPageTabs();
      }
      renderActiveWorkspace();
    });
  });

  function focusOverviewDevice(kind = 'mem', idx = 0) {
    syncDcpMemoryAssembler();
    if (state.navTarget !== 'vm') switchNavigationTarget('vm');
    state.activeTabKey = 'overview';
    state.tcSelected = { kind, idx };
    renderPageTabs();
    renderActiveWorkspace();
  }

  document.querySelectorAll('#dcpCpuTopologyOptions [data-cpu-topology]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const mode = btn.getAttribute('data-cpu-topology') === 'mixed' ? 'mixed' : 'single';
      const nextModel = CPU_TOPOLOGY_PRESETS[mode];
      const affinitySec = PROCESSOR_SECTIONS.find((s) => s.kind === 'affinity');
      if (affinitySec) {
        affinitySec.cpuModel = nextModel;
      }
      const hostModelEl = document.getElementById('hostCpuModelText');
      if (hostModelEl) {
        hostModelEl.textContent = nextModel.replace(/\r?\n/g, ' + ');
      }
      focusOverviewDevice('cpu');
    });
  });

  document.querySelectorAll('#dcpMemPowerOptions [data-mem-power]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const vm = getVm();
      const nextRunning = btn.getAttribute('data-mem-power') === 'on';
      if (vm && Boolean(vm.running) !== nextRunning) {
        vm.running = nextRunning;
      }
      renderVmList();
      renderDashboardSummary();
      const curKind = state.tcSelected?.kind || 'mem';
      const curIdx = state.tcSelected?.idx || 0;
      focusOverviewDevice(curKind, curIdx);
    });
  });

  document.querySelectorAll('#dcpMemDynamicOptions [data-mem-dynamic]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const nextOn = btn.getAttribute('data-mem-dynamic') === 'on';
      const dynField = getMemoryField('DynamicMemoryEnabled');
      if (dynField) dynField.value = nextOn;
      focusOverviewDevice('mem');
    });
  });

  document.querySelectorAll('#dcpMemSmartPagingOptions [data-mem-smartpaging]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.memSmartPagingAlert = btn.getAttribute('data-mem-smartpaging') === 'on';
      focusOverviewDevice('mem');
    });
  });

  document.querySelectorAll('#dcpChartHeightOptions [data-chart-height]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.singleChartHeight = Number(btn.getAttribute('data-chart-height')) || 136;
      const curKind = state.tcSelected?.kind || 'disk';
      const curIdx = state.tcSelected?.idx || 0;
      focusOverviewDevice(curKind, curIdx);
    });
  });

  document.querySelectorAll('#dcpDiskCandidateOptions [data-disk-candidate]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.diskCandidate = btn.getAttribute('data-disk-candidate') || 'a';
      const curIdx = state.tcSelected?.kind === 'disk' ? state.tcSelected.idx : 0;
      focusOverviewDevice('disk', curIdx);
    });
  });

  document.querySelectorAll('#dcpIsoChartOptions [data-iso-chart]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.isoChartMode = btn.getAttribute('data-iso-chart') === 'dual' ? 'dual' : 'single';
      state.diskPreviewMode = 'iso';
      focusOverviewDevice('disk', 0);
    });
  });

  document.querySelectorAll('#dcpDiffParentOptions [data-diff-parent]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.diffParentMode = btn.getAttribute('data-diff-parent') === 'top-right' ? 'top-right' : 'wide';
      state.diskPreviewMode = 'differencing';
      focusOverviewDevice('disk', 0);
    });
  });

  document.querySelectorAll('#dcpDiskModeOptions [data-disk-mode]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.diskPreviewMode = btn.getAttribute('data-disk-mode') || 'virtual';
      focusOverviewDevice('disk', 0);
    });
  });

  document.querySelectorAll('#dcpDiskPartOptions [data-disk-part]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const key = btn.getAttribute('data-disk-part');
      if (!key || !state.diskParts) return;
      state.diskParts[key] = !state.diskParts[key];
      const curIdx = state.tcSelected?.kind === 'disk' ? state.tcSelected.idx : 0;
      focusOverviewDevice('disk', curIdx);
    });
  });

  // Live visual icon pickers & tail tab order switcher in right-side panel
  document.querySelectorAll('#dcpCheckpointIcons [data-cp-glyph]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const cpTab = PAGE_TABS.find((t) => t.key === 'spacetime');
      if (cpTab) {
        cpTab.glyph = btn.getAttribute('data-cp-glyph') || cpTab.glyph;
        cpTab.fluent = btn.getAttribute('data-cp-fluent') === 'true';
      }
      document.querySelectorAll('#dcpCheckpointIcons .dcp-icon-btn').forEach((b) => {
        b.classList.toggle('active', b === btn);
      });
      renderPageTabs();
    });
  });

  document.querySelectorAll('#dcpAdvNameOptions [data-adv-name]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const advTab = PAGE_TABS.find((t) => t.key === 'advanced');
      if (advTab) {
        advTab.title = btn.getAttribute('data-adv-name') || advTab.title;
        advTab.titleEn = btn.getAttribute('data-adv-en') || advTab.titleEn;
        advTab.titleEnShort = btn.getAttribute('data-adv-short') || advTab.titleEnShort;
      }
      document.querySelectorAll('#dcpAdvNameOptions [data-adv-name]').forEach((b) => {
        b.classList.toggle('active', b === btn);
      });
      renderPageTabs();
    });
  });

  document.querySelectorAll('#dcpAdvancedIcons [data-adv-glyph]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const advTab = PAGE_TABS.find((t) => t.key === 'advanced');
      if (advTab) {
        advTab.glyph = btn.getAttribute('data-adv-glyph') || advTab.glyph;
        advTab.fluent = btn.getAttribute('data-adv-fluent') === 'true';
      }
      document.querySelectorAll('#dcpAdvancedIcons .dcp-icon-btn').forEach((b) => {
        b.classList.toggle('active', b === btn);
      });
      renderPageTabs();
    });
  });

  document.getElementById('dcpToggleTailOrderBtn')?.addEventListener('click', (e) => {
    state.tailOrder = state.tailOrder === 'spacetime-first' ? 'advanced-first' : 'spacetime-first';
    e.currentTarget.textContent =
      state.tailOrder === 'spacetime-first' ? '顶栏顺序: 检查点 · 高级' : '顶栏顺序: 高级 · 检查点';
    renderPageTabs();
  });

  // Allow dragging the right-side Design Candidates Panel by its header
  const dcpPanel = document.getElementById('designCandidatesPanel');
  const dcpHeader = document.getElementById('dcpHeader');
  if (dcpPanel && dcpHeader) {
    let dcpDragging = false;
    let dcpOffX = 0;
    let dcpOffY = 0;
    let dcpStartX = 0;
    let dcpStartY = 0;
    dcpHeader.addEventListener('pointerdown', (e) => {
      if (e.button !== 0) return;
      dcpDragging = true;
      dcpStartX = e.clientX - dcpOffX;
      dcpStartY = e.clientY - dcpOffY;
      dcpHeader.setPointerCapture(e.pointerId);
    });
    dcpHeader.addEventListener('pointermove', (e) => {
      if (!dcpDragging) return;
      dcpOffX = e.clientX - dcpStartX;
      dcpOffY = e.clientY - dcpStartY;
      dcpPanel.style.transform = `translate3d(${dcpOffX}px, ${dcpOffY}px, 0)`;
    });
    dcpHeader.addEventListener('pointerup', (e) => {
      if (!dcpDragging) return;
      dcpDragging = false;
      dcpHeader.releasePointerCapture(e.pointerId);
    });
  }

  if (typeof ResizeObserver !== 'undefined') {
    const ro = new ResizeObserver(() => {
      layoutDynamicGrids();
    });
    ro.observe(winEl);
    const aHost = document.getElementById('vmOverviewAltHost');
    if (aHost) ro.observe(aHost);
    const rBox = document.getElementById('resourceDetailsBox');
    if (rBox) ro.observe(rBox);
    const sHost = document.getElementById('vmSettingsHost');
    if (sHost) ro.observe(sHost);
  }

  function setDcpMode(mode, switchAppTab = false) {
    state.dcpMode = mode === 'overview' ? 'overview' : 'isa';
    const isIsa = state.dcpMode === 'isa';
    const ovSec = document.getElementById('dcpOverviewSection');
    const isaSec = document.getElementById('dcpIsaSection');
    const titleEl = document.getElementById('dcpTitle');
    if (ovSec) ovSec.hidden = isIsa;
    if (isaSec) isaSec.hidden = !isIsa;
    if (titleEl) titleEl.textContent = isIsa ? '处理器，指令集能力' : '概览卡片预览';
    document.querySelectorAll('#dcpModeTabs [data-dcp-mode]').forEach((btn) => {
      btn.classList.toggle('active', btn.getAttribute('data-dcp-mode') === state.dcpMode);
    });
    if (switchAppTab) {
      if (state.navTarget !== 'vm') switchNavigationTarget('vm');
      state.activeTabKey = isIsa ? 'processor' : 'overview';
      if (isIsa) state.processorSectionIdx = 2;
      renderPageTabs();
      renderActiveWorkspace();
    }
  }

  document.querySelectorAll('#dcpModeTabs [data-dcp-mode]').forEach((btn) => {
    btn.addEventListener('click', () => {
      setDcpMode(btn.getAttribute('data-dcp-mode'), true);
    });
  });

  const focusIsaTab = () => {
    if (state.navTarget !== 'vm') switchNavigationTarget('vm');
    state.activeTabKey = 'processor';
    state.processorSectionIdx = 2;
    setDcpMode('isa', false);
    renderPageTabs();
    renderActiveWorkspace();
  };

  document.querySelectorAll('#dcpIsaVariants [data-isa-variant]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.isaVariant = btn.getAttribute('data-isa-variant') || 'grid-2x2';
      document.querySelectorAll('#dcpIsaVariants [data-isa-variant]').forEach((b) => {
        b.classList.toggle('active', b === btn);
      });
      focusIsaTab();
    });
  });

  document.querySelectorAll('#dcpIsaMigDesigns [data-isa-mig-design]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.isaMigDesign = btn.getAttribute('data-isa-mig-design') || 'three-way';
      document.querySelectorAll('#dcpIsaMigDesigns [data-isa-mig-design]').forEach((b) => {
        b.classList.toggle('active', b === btn);
      });
      focusIsaTab();
    });
  });

  document.querySelectorAll('#dcpIsaMigTerms [data-isa-mig-terms]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.isaMigTerms = btn.getAttribute('data-isa-mig-terms') || 'friendly';
      document.querySelectorAll('#dcpIsaMigTerms [data-isa-mig-terms]').forEach((b) => {
        b.classList.toggle('active', b === btn);
      });
      focusIsaTab();
    });
  });

  document.querySelectorAll('#dcpIsaLegacyNames [data-isa-legacy-name]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.isaLegacyName = btn.getAttribute('data-isa-legacy-name') || '老旧系统兼容';
      document.querySelectorAll('#dcpIsaLegacyNames [data-isa-legacy-name]').forEach((b) => {
        b.classList.toggle('active', b === btn);
      });
      focusIsaTab();
    });
  });

  document.querySelectorAll('#dcpIsaPowerSim [data-isa-power]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const vm = getVm();
      const nextRunning = btn.getAttribute('data-isa-power') === 'on';
      if (vm) vm.running = nextRunning;
      document.querySelectorAll('#dcpIsaPowerSim [data-isa-power]').forEach((b) => {
        b.classList.toggle('active', b === btn);
      });
      renderVmList();
      renderDashboardSummary();
      focusIsaTab();
    });
  });

  initComputePlayground({
    state,
    switchToCompute: () => {
      if (state.navTarget !== 'vm') switchNavigationTarget('vm');
      state.activeTabKey = 'processor';
      state.processorSectionIdx = 0;
      renderPageTabs();
    },
    onVariantOrFlagChange: () => renderActiveWorkspace()
  });

  document.querySelectorAll('#dcpTopoModeList [data-topo-uimode]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.topologyUiMode = btn.getAttribute('data-topo-uimode') || 'intent';
      document.querySelectorAll('#dcpTopoModeList [data-topo-uimode]').forEach((b) => {
        b.classList.toggle('active', b.getAttribute('data-topo-uimode') === state.topologyUiMode);
      });
      if (state.navTarget !== 'vm') switchNavigationTarget('vm');
      state.activeTabKey = 'processor';
      state.processorSectionIdx = 3;
      renderPageTabs();
      renderActiveWorkspace();
    });
  });

  document.querySelectorAll('#dcpMemLayoutList [data-mem-layout]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.memComputeLayoutMode = btn.getAttribute('data-mem-layout') || '2-1-3';
      document.querySelectorAll('#dcpMemLayoutList [data-mem-layout]').forEach((b) => {
        b.classList.toggle('active', b.getAttribute('data-mem-layout') === state.memComputeLayoutMode);
      });
      if (state.navTarget !== 'vm') switchNavigationTarget('vm');
      state.activeTabKey = 'memory';
      state.memorySectionIdx = 0;
      renderPageTabs();
      renderActiveWorkspace();
    });
  });

  document.querySelectorAll('#dcpMemDynOffList [data-mem-dynoff]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.memDynamicOffMode = btn.getAttribute('data-mem-dynoff') || 'disable';
      document.querySelectorAll('#dcpMemDynOffList [data-mem-dynoff]').forEach((b) => {
        b.classList.toggle('active', b.getAttribute('data-mem-dynoff') === state.memDynamicOffMode);
      });
      if (state.navTarget !== 'vm') switchNavigationTarget('vm');
      state.activeTabKey = 'memory';
      state.memorySectionIdx = 0;
      renderPageTabs();
      renderActiveWorkspace();
    });
  });

  document.querySelectorAll('#dcpNoticeModes [data-notice-mode]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.noticeMode = btn.getAttribute('data-notice-mode') || 'toast';
      document.querySelectorAll('#dcpNoticeModes [data-notice-mode]').forEach((b) => {
        b.classList.toggle('active', b.getAttribute('data-notice-mode') === state.noticeMode);
      });
      const bar = document.getElementById('dashboardNoticeBar');
      if (bar) bar.hidden = true;
      const inlineEl = document.getElementById('computeInlineNotice');
      if (inlineEl) inlineEl.classList.remove('is-visible');
      showNotice(state.uiLang === 'en' ? 'Compute settings applied.' : '已应用「计算资源」设置');
    });
  });

  document.querySelectorAll('#dcpNetPreviewModes [data-net-preview]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.netPreviewMode = btn.getAttribute('data-net-preview') || 'connected';
      document.querySelectorAll('#dcpNetPreviewModes [data-net-preview]').forEach((b) => {
        b.classList.toggle('active', b.getAttribute('data-net-preview') === state.netPreviewMode);
      });
      state.tcSelected = { kind: 'net', idx: 0 };
      renderOverview();
    });
  });

  document.querySelectorAll('#dcpNetCandidates [data-net-candidate]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.netCandidate = btn.getAttribute('data-net-candidate') || 'a';
      document.querySelectorAll('#dcpNetCandidates [data-net-candidate]').forEach((b) => {
        b.classList.toggle('active', b.getAttribute('data-net-candidate') === state.netCandidate);
      });
      state.tcSelected = { kind: 'net', idx: 0 };
      renderOverview();
    });
  });

  document.querySelectorAll('#dcpNetHeaderModes [data-net-header]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.netHeaderMode = btn.getAttribute('data-net-header') || 'switch-ip';
      document.querySelectorAll('#dcpNetHeaderModes [data-net-header]').forEach((b) => {
        b.classList.toggle('active', b.getAttribute('data-net-header') === state.netHeaderMode);
      });
      state.tcSelected = { kind: 'net', idx: 0 };
      renderOverview();
    });
  });

  function notifyParentGpuState() {
    if (window.parent === window) return;
    const vm = getVm();
    const assigns = state.gpuAssignmentsByVm[vm.id] || [];
    let status = 'single';
    if (state.gpuScenario === 'stale') status = 'stale';
    else if (!state.gpuVmReady) status = 'mmio-pending';
    else if (assigns.length === 0) status = 'empty';
    else if (assigns.length >= 2) status = 'dual';
    try {
      window.parent.postMessage(
        {
          protocol: 'win11-app/v1',
          type: 'w11:app-event',
          channel: 'exhyperv:gpu-sync',
          payload: {
            activeTabKey: state.activeTabKey,
            deviceSubKey: state.deviceSubKey,
            variant: state.gpuVariant || 'expander',
            status,
            mode: state.gpuMode || 'list'
          }
        },
        window.location.origin
      );
    } catch {
      // ignore
    }
  }

  window.addEventListener('message', (event) => {
    if (event.origin !== window.location.origin) return;
    const raw = event.data;
    if (!raw || typeof raw !== 'object') return;
    if (
      (raw.protocol === 'win11-app/v1' && raw.type === 'w11:init') ||
      raw.type === 'exhyperv-gpu-request-sync'
    ) {
      notifyParentGpuState();
      return;
    }

    let data = null;
    if (
      raw.protocol === 'win11-app/v1' &&
      raw.type === 'w11:app-message' &&
      raw.channel === 'exhyperv:gpu-candidate' &&
      raw.payload &&
      typeof raw.payload === 'object'
    ) {
      data = raw.payload;
    } else if (raw.type === 'exhyperv-gpu-candidate') {
      data = raw;
    }
    if (!data) return;

    const vm = getVm();
    if (state.navTarget !== 'vm') switchNavigationTarget('vm');
    state.activeTabKey = 'devices';
    state.deviceSubKey = 'gpu';

    if (data.variant) {
      state.gpuVariant = data.variant;
      if (!data.mode) state.gpuMode = 'list';
    }
    if (data.status) {
      const s = data.status;
      if (s === 'empty') {
        state.gpuAssignmentsByVm[vm.id] = [];
        state.gpuScenario = 'normal';
      } else if (s === 'dual') {
        const g1 = structuredClone(INITIAL_GPU_ASSIGNMENTS[0]);
        const h2 = HOST_GPUS[1] || HOST_GPUS[0];
        if (g1 && h2) {
          const g2 = {
            id: 'gpu-2',
            name: h2.name,
            manu: h2.manu,
            vendor: h2.vendor,
            memoryText: h2.memoryText,
            driverVersion: h2.driverVersion,
            instanceId: h2.instanceId,
            partitionPath: h2.partitionPath,
            expanded: true
          };
          state.gpuAssignmentsByVm[vm.id] = h2 === HOST_GPUS[0] && !HOST_GPUS[1] ? [g1] : [g1, g2];
        } else {
          state.gpuAssignmentsByVm[vm.id] = g1 ? [g1] : [];
        }
        state.gpuScenario = 'normal';
        state.gpuVmReady = true;
      } else if (s === 'stale') {
        if ((state.gpuAssignmentsByVm[vm.id] || []).length === 0) {
          state.gpuAssignmentsByVm[vm.id] = structuredClone(INITIAL_GPU_ASSIGNMENTS);
        }
        state.gpuScenario = 'stale';
      } else if (s === 'mmio-pending') {
        if ((state.gpuAssignmentsByVm[vm.id] || []).length === 0) {
          state.gpuAssignmentsByVm[vm.id] = structuredClone(INITIAL_GPU_ASSIGNMENTS);
        }
        state.gpuScenario = 'normal';
        state.gpuVmReady = false;
      } else {
        state.gpuAssignmentsByVm[vm.id] = structuredClone(INITIAL_GPU_ASSIGNMENTS);
        state.gpuScenario = 'normal';
        state.gpuVmReady = true;
      }
      if (!data.mode) state.gpuMode = 'list';
    }
    if (data.mode) {
      state.gpuMode = data.mode;
      if (data.mode === 'deploy') {
        if ((state.gpuAssignmentsByVm[vm.id] || []).length === 0) {
          state.gpuAssignmentsByVm[vm.id] = structuredClone(INITIAL_GPU_ASSIGNMENTS);
        }
        state.gpuDeployMode = 'add-with-drivers';
        state.gpuDeployDecision = vm.running ? 'PowerOff' : 'SelectPartition';
      }
    }

    renderPageTabs();
    renderDashboardSummary();
    renderActiveWorkspace();
  });

  let vmInitialRendered = false;
  let lastSyncedHwSig = '';
  let lastSyncedDefaultGpuInstance = INITIAL_GPU_ASSIGNMENTS[0]?.instanceId || '';

  function syncVirtualHardwareToVm(hw) {
    if (!hw || typeof hw !== 'object') return;
    latestVirtualHw = hw;

    const cpuName = hw.cpu?.displayName?.value || '';
    const logicalCores = Math.max(1, Number(hw.cpu?.logicalCores) || state.hostLogicalCores || 16);
    const mappedGpus = buildVirtualGpuEntries(hw);
    const gpuSig = mappedGpus.map((g) => `${g.name}@${g.driverVersion}@${g.instanceId}`).join(';');
    const hwSig = `${cpuName}|${logicalCores}|${gpuSig}`;
    const structuralChanged = hwSig !== lastSyncedHwSig;

    if (cpuName) {
      const prevSingle = CPU_TOPOLOGY_PRESETS.single;
      CPU_TOPOLOGY_PRESETS.single = cpuName;
      const affinitySec = PROCESSOR_SECTIONS.find((s) => s.kind === 'affinity');
      if (affinitySec && affinitySec.cpuModel === prevSingle) {
        affinitySec.cpuModel = cpuName;
      }
      const brandField = getProcessorField('CpuBrandString');
      if (brandField && brandField.value === prevSingle) {
        brandField.value = cpuName;
      }
      const hostModelEl = document.getElementById('hostCpuModelText');
      if (hostModelEl && hostModelEl.textContent?.trim() === prevSingle) {
        hostModelEl.innerHTML = formatCpuTitleHtml(cpuName);
      }
    }

    if (structuralChanged) {
      const isFirstHwSync = !lastSyncedHwSig;
      lastSyncedHwSig = hwSig;

      state.hostLogicalCores = logicalCores;
      const clampedMask = new Set([...state.affinityMask].filter((idx) => idx >= 0 && idx < logicalCores));
      if (clampedMask.size === 0 && state.affinityMask.size > 0) {
        for (let i = 0; i < Math.min(logicalCores, Math.ceil(logicalCores / 2)); i++) clampedMask.add(i);
      }
      state.affinityMask = clampedMask;
      if (state.savedAffinityMask) {
        state.savedAffinityMask = new Set([...state.savedAffinityMask].filter((idx) => idx >= 0 && idx < logicalCores));
      }

      const countField = getProcessorField('Count');
      if (countField) {
        countField.max = Math.max(1, logicalCores);
        const curCount = Number(countField.value);
        if (Number.isFinite(curCount) && curCount > logicalCores) {
          countField.value = String(logicalCores);
        }
      }

      if (mappedGpus.length > 0) {
        HOST_GPUS.splice(0, HOST_GPUS.length, ...mappedGpus);
        const validSelected = HOST_GPUS.find((g) => g.id === state.selectedHostGpuId && g.partitionable);
        state.selectedHostGpuId = (validSelected || HOST_GPUS.find((g) => g.partitionable) || HOST_GPUS[0]).id;

        const primaryGpu = HOST_GPUS.find((g) => g.partitionable) || HOST_GPUS[0];
        const nextAssign = {
          id: 'gpu-1',
          name: primaryGpu.name,
          manu: primaryGpu.manu,
          vendor: primaryGpu.vendor,
          memoryText: primaryGpu.memoryText,
          driverVersion: primaryGpu.driverVersion,
          instanceId: primaryGpu.instanceId,
          partitionPath: primaryGpu.partitionPath,
          expanded: true
        };
        if (primaryGpu.partitionable) {
          INITIAL_GPU_ASSIGNMENTS.splice(0, INITIAL_GPU_ASSIGNMENTS.length, nextAssign);
        } else {
          INITIAL_GPU_ASSIGNMENTS.splice(0, INITIAL_GPU_ASSIGNMENTS.length);
        }

        if (STRESS_GPUS[0]) {
          STRESS_GPUS[0].name = `${primaryGpu.name} (DDA 直通)`;
          STRESS_GPUS[0].vendor = primaryGpu.manu;
          STRESS_GPUS[0].memoryText = primaryGpu.memoryText;
          STRESS_GPUS[0].driverVersion = primaryGpu.driverVersion;
          STRESS_GPUS[0].instanceId = primaryGpu.instanceId;
        }

        const pcieTemplate = {
          id: 'pcie-1',
          name: `${primaryGpu.name} (vPCI)`,
          classType: 'Display',
          glyph: '\uF211',
          guestMode: '0',
          instanceId: primaryGpu.instanceId,
          path: '浏览器隐藏物理 PCIe 拓扑',
          vendor: primaryGpu.vendor,
          expanded: true
        };
        const prevPcieInst = INITIAL_PCIE_DEVICES[0]?.instanceId;
        INITIAL_PCIE_DEVICES.splice(0, INITIAL_PCIE_DEVICES.length, pcieTemplate);

        VM_ITEMS.forEach((vmItem) => {
          if (isFirstHwSync && vmItem.cores > logicalCores) {
            vmItem.cores = logicalCores;
            vmItem.configText = `${vmItem.cores} 核 / ${vmItem.memoryGb} GB / ${vmItem.disks.map((d) => d.capacityText).join(' + ')}`;
          }
          vmItem.gpuText = primaryGpu.name;
          vmItem.gpus = [
            {
              key: 'gpu-0',
              title: primaryGpu.name,
              vendor: primaryGpu.manu,
              memoryText: primaryGpu.memoryText,
              driverVersion: primaryGpu.driverVersion,
              instanceId: primaryGpu.instanceId
            }
          ];

          const curAssigns = state.gpuAssignmentsByVm[vmItem.id];
          const isUntouchedDefault =
            Array.isArray(curAssigns) &&
            curAssigns.length === 1 &&
            curAssigns[0]?.id === 'gpu-1' &&
            curAssigns[0]?.instanceId === lastSyncedDefaultGpuInstance;
          if (isUntouchedDefault) {
            state.gpuAssignmentsByVm[vmItem.id] = structuredClone(INITIAL_GPU_ASSIGNMENTS);
          }

          const curPcie = state.pcieDevicesByVm[vmItem.id];
          if (
            Array.isArray(curPcie) &&
            curPcie.length === 1 &&
            curPcie[0]?.id === 'pcie-1' &&
            (!prevPcieInst || curPcie[0]?.instanceId === prevPcieInst)
          ) {
            state.pcieDevicesByVm[vmItem.id] = structuredClone(INITIAL_PCIE_DEVICES);
          }
        });

        lastSyncedDefaultGpuInstance = nextAssign.instanceId;
      }

      if (vmInitialRendered) {
        const activeEl = document.activeElement;
        const pageVm = document.getElementById('pageVirtualMachines');
        const editingInput =
          activeEl &&
          pageVm?.contains(activeEl) &&
          ['INPUT', 'SELECT', 'TEXTAREA'].includes(activeEl.tagName);
        if (!editingInput) {
          syncDcpGpuButton();
          renderVmList();
          renderDashboardSummary();
          renderActiveWorkspace();
        }
      }
    } else if (vmInitialRendered && state.navTarget === 'vm' && state.activeTabKey === 'overview' && getVm().running) {
      renderOverview();
    }
  }

  if (window.__WIN11_HARDWARE__) {
    syncVirtualHardwareToVm(window.__WIN11_HARDWARE__);
  }
  window.addEventListener('win11:hardware', (e) => {
    syncVirtualHardwareToVm(e.detail);
  });

  // Initial render
  applyAccentVariables();
  syncDcpGpuButton();
  syncDcpMemoryAssembler();
  setDcpMode('overview', false);
  renderVmList();
  renderDashboardSummary();
  renderPageTabs();
  renderActiveWorkspace();
  vmInitialRendered = true;
}
