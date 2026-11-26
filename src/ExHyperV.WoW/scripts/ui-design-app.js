import {
  OS_TYPES,
  PAGE_TABS,
  MERGED_SUBNAV_GROUPS,
  VM_ITEMS,
  PROCESSOR_SECTIONS,
  MEMORY_SECTIONS,
  INITIAL_STORAGE_ITEMS,
  HOST_PHYSICAL_DISKS,
  HOST_OPTICAL_DRIVES,
  INITIAL_GPU_ASSIGNMENTS,
  HOST_GPUS,
  INITIAL_GPU_PARTITIONS,
  INITIAL_BOOT_ENTRIES,
  INITIAL_SERIAL_PORTS,
  INITIAL_VTPM_BY_VM,
  INITIAL_PCIE_DEVICES,
  HOST_PCIE_DEVICES,
  CHECKPOINT_NODES
} from './ui-design-data.js';
import { createServerAdapter } from './server-adapter.js';
import { renderRemotePage } from './remote-page.js';

export function initUiDesignWorkbench() {
  const winEl = document.getElementById('winuiWindow');
  if (!winEl || winEl.dataset.bound === 'true') return;
  winEl.dataset.bound = 'true';

  const DEFAULT_HOST_OVERVIEW_CANDIDATE = {
    cardStyle: 'compact-2row',
    cardContent: 'ops-summary',
    cpuDetailStyle: 'art-ring',
    highlightStyle: 'elevated-card',
    legendPlacement: 'right',
    mockThreads: '12',
    mockVmCount: '2'
  };

  let computeMod = null;
  let computeModPromise = null;
  let devicesMod = null;
  let devicesModPromise = null;
  let hostMod = null;
  let hostModPromise = null;
  let checkpointMod = null;
  let checkpointModPromise = null;
  let consoleCtrl = null;
  let consoleCtrlPromise = null;
  let settingsRenderToken = 0;

  const DEFAULT_CONSOLE_FEATURES = Object.freeze({
    consoleClipboard: true,
    consoleAudioOutput: true,
    consoleAudioInput: true
  });

  function readCachedVmConsoleFeatures(vmId) {
    const prevAdv = vmId ? state.advSettingsByVm[vmId] : null;
    if (!prevAdv || typeof prevAdv !== 'object') return { ...DEFAULT_CONSOLE_FEATURES };
    return {
      consoleClipboard: prevAdv.consoleClipboard !== false,
      consoleAudioOutput: prevAdv.consoleAudioOutput !== false,
      consoleAudioInput: prevAdv.consoleAudioInput !== false
    };
  }

  function emitConsoleSync(vmId, adv) {
    if (!vmId || !adv) return;
    window.__WOW_APP_CLIENT__?.emitAppEvent?.('exhyperv:console-sync', {
      id: vmId,
      consoleSupport: Boolean(adv.consoleSupport),
      shielding: Boolean(adv.shielding),
      consoleClipboard: adv.consoleClipboard !== false,
      consoleAudioOutput: adv.consoleAudioOutput !== false,
      consoleAudioInput: adv.consoleAudioInput !== false
    });
  }

  function preferredConsoleMode() {
    if (!state.showExtensionEntries && state.defaultConsoleMode === 'External') return 'basic';
    return state.defaultConsoleMode === 'External' ? 'external' : state.defaultConsoleMode === 'RdpEnhanced' ? 'enhanced' : 'basic';
  }
  function loadConsoleCtrl() {
    if (consoleCtrl) return Promise.resolve(consoleCtrl);
    if (!consoleCtrlPromise) {
      consoleCtrlPromise = import('./ui-design-console.js').then((mod) => {
        consoleCtrl = mod.createConsoleController({
          getVm: () => ({ ...getVm(), mode: preferredConsoleMode() }),
          getAllVms: () => activeVms,
          getAdvSettings: (vmItem) => {
            const id = vmItem?.id;
            if (!id || id === state.selectedVmId) return state.advSettings;
            return state.advSettingsByVm[id] || {
              consoleSupport: true,
              shielding: false,
              ...DEFAULT_CONSOLE_FEATURES
            };
          },
          getTheme: () => winEl.getAttribute('data-theme') || 'dark',
          getAccentHex: () => state.accentHex,
          getShowExtensionEntries: () => state.showExtensionEntries,
          getServerConnection: (vm, mode, watchOnly, ceiling) => (serverAdapter.isLive ? serverAdapter.issueConsoleTicket(vm.id, mode, watchOnly, ceiling) : Promise.resolve(null)),
          executeVmPowerAction: async (vmId, action) => {
            if (!serverAdapter.isLive) return false;
            await serverAdapter.executeVmPower(vmId, action);
            return true;
          },
          onVmPowerChanged: () => {
            syncStandaloneHostRailPanel();
            notifyParentHostState();
            renderVmList();
            renderDashboardSummary();
            renderActiveWorkspace();
          }
        });
        return consoleCtrl;
      });
    }
    return consoleCtrlPromise;
  }

  function loadComputeMod() {
    if (computeMod) return Promise.resolve(computeMod);
    if (!computeModPromise) {
      computeModPromise = import('./ui-design-compute.js').then((mod) => {
        computeMod = mod;
        return mod;
      });
    }
    return computeModPromise;
  }

  function loadDevicesMod() {
    if (devicesMod) return Promise.resolve(devicesMod);
    if (!devicesModPromise) {
      devicesModPromise = import('./ui-design-devices.js').then((mod) => {
        devicesMod = mod;
        return mod;
      });
    }
    return devicesModPromise;
  }

  function loadHostMod() {
    if (hostMod) return Promise.resolve(hostMod);
    if (!hostModPromise) {
      hostModPromise = import('./ui-design-host.js').then((mod) => {
        hostMod = mod;
        mod.initHostPage({ onRender: layoutDynamicGrids });
        return mod;
      });
    }
    return hostModPromise;
  }

  function loadCheckpointMod() {
    if (checkpointMod) return Promise.resolve(checkpointMod);
    if (!checkpointModPromise) {
      checkpointModPromise = import('./ui-checkpoint-designs.js').then((mod) => {
        checkpointMod = mod;
        return mod;
      });
    }
    return checkpointModPromise;
  }

  const state = {
    navTarget: 'vm',
    selectedVmId: VM_ITEMS[0].id,
    searchQuery: '',
    activeTabKey: 'overview',
    deviceSubKey: 'gpu',
    advancedSubKey: 'power',
    consoleSubTab: 'basic',
    tailOrder: 'checkpoints-first',
    advSettings: {
      secureBoot: true,
      secureBootTemplate: 'Microsoft Windows',
      tpmEnabled: true,
      encryptState: false,
      shielding: false,
      bindHostTpm: false,
      vbsOptOut: false,
      migratableDisabled: false,
      fullScsi: false,
      numLock: false,
      consoleSupport: true,
      consoleClipboard: true,
      consoleAudioOutput: true,
      consoleAudioInput: true,
      resolution: '默认',
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
    storageItemsByVm: {
      'vm-test': structuredClone(INITIAL_STORAGE_ITEMS)
    },
    storageMode: 'list', // 'list' | 'add' | 'progress'
    storageAddIntent: 'existing', // 'existing' | 'new-disk' | 'new-iso' | 'physical'
    storageInlineEditIdx: null,
    storageForm: {
      driveType: 'HardDisk',
      sourceMode: 'existing',
      mediaSource: 'Virtual',
      operation: 'Existing',
      filePath: 'D:\\Hyper-V\\Virtual Hard Disks\\extra_disk.vhdx',
      existingFilePath: 'D:\\Hyper-V\\Virtual Hard Disks\\extra_disk.vhdx',
      newDiskPath: 'D:\\Hyper-V\\Virtual Hard Disks\\new_disk.vhdx',
      isoPath: '',
      sizeGb: '128',
      vhdType: 'Dynamic',
      sectorFormat: 'Default',
      blockSize: 'Default',
      parentPath: '',
      showAdvancedDiskOptions: false,
      isoSourceDir: 'C:\\Drivers\\Qualcomm_GPU',
      isoVolumeLabel: 'Qualcomm_GPU',
      isoOutputPath: 'D:\\Hyper-V\\ISO\\Qualcomm_GPU.iso',
      isoOutputCustom: false,
      physicalKind: 'disk',
      selectedPhysicalDiskNumber: 1,
      selectedPhysicalOpticalId: 'opt-0',
      autoLocation: true,
      controllerType: 'SCSI',
      controllerNumber: 0,
      location: 3
    },
    gpuMode: 'list', // 'list' | 'select' | 'deploy'
    gpuVariant: 'tiles', // 'expander' | 'tiles' | 'compact' | 'layered'
    gpuAddPlacement: 'list-bottom-row', // 'list-bottom-row' | 'below-right' | 'floating-fab' | 'tabs-right'
    gpuAddBtnStyle: 'secondary-icon-text', // 'accent-icon-text' | 'accent-text' | 'accent-circle' | 'round-icon' | 'secondary-icon-text'
    gpuIconStyle: 'vector', // 'fluent' | 'vector'
    gpuDriverMismatch: true,
    gpuUpdateIcon: 'upgrade-base',
    gpuUpdateColor: 'white', // 'white' | 'accent' | 'red'
    selectedHostGpuId: HOST_GPUS[0].id,
    installGpuDrivers: true,
    gpuDeployMode: 'add-with-drivers',
    gpuDeployDecision: 'SelectPartition',
    gpuSelectedPartitionId: INITIAL_GPU_PARTITIONS[0].id,
    gpuLogExpanded: false,
    gpuAssignmentsByVm: {
      'vm-test': structuredClone(INITIAL_GPU_ASSIGNMENTS),
      'vm-win11-arm64': structuredClone(INITIAL_GPU_ASSIGNMENTS),
      'vm-win7-gen1': []
    },
    netVlanMode: 'Access',
    nicVariant: 'unified', // 'unified' | 'inline-converged' | 'pill-tabs' | 'legacy'
    nicScenario: 'single', // 'single' | 'multi' | 'empty'
    nicMode: 'list', // 'list' | 'add' | 'policy'
    nicHostSwitches: 'one', // 'one' | 'many' | 'none'
    nicEditTargetId: null,
    nicPolicyTab: 'connection',
    nicAdvLabel: '策略',
    nicAdvIcon: 'E71C',
    networkAdaptersByVm: {},
    bootEntries: structuredClone(INITIAL_BOOT_ENTRIES),
    serialPortsByVm: {
      'vm-test': structuredClone(INITIAL_SERIAL_PORTS),
      'vm-win11-arm64': structuredClone(INITIAL_SERIAL_PORTS),
      'vm-win7-gen1': structuredClone(INITIAL_SERIAL_PORTS)
    },
    vtpmByVm: structuredClone(INITIAL_VTPM_BY_VM),
    vtpmMode: 'main',
    vtpmSettingsTab: 'keys',
    vtpmConfirmDisable: false,
    pcieDevicesByVm: {
      'vm-test': [
        {
          id: 'pcie-nic-test',
          hostId: 'host-pcie-nic',
          name: 'Intel(R) Ethernet Controller I226-V',
          classType: 'Net',
          glyph: '\uE774',
          guestMode: '0',
          instanceId: 'PCI\\VEN_8086&DEV_125C&SUBSYS_00008086\\6&3B912A&0&0010',
          path: 'PCIROOT(0)#PCI(0201)#PCI(0000)',
          vendor: 'Intel Corporation',
          expanded: false
        }
      ],
      'vm-win11-arm64': structuredClone(INITIAL_PCIE_DEVICES),
      'vm-win7-gen1': []
    },
    checkpointSelectedId: 'node-current',
    checkpointCandidate: 'c-brand-matte', // 'c-brand-matte' | 'c-iso-chain' | 'a-vm-desktop' | 'a-console-shell'
    checkpointFocusId: 'node-root',
    checkpointPanX: 0,
    checkpointPanY: 0,
    checkpointMenuOpenId: null,
    checkpointCreatePopover: false,
    checkpointActiveBranchIdx: 0,
    checkpointExpandedBranches: {},
    checkpointShowThumbs: true,
    checkpointSearch: '',
    checkpointFilter: 'all', // 'all' | 'active' | 'historical-disk'
    checkpointZoom: 1,
    checkpointNewMode: 'standard', // 'standard' | 'production'
    checkpointNewName: '',
    checkpointConfirmAction: null, // null | 'restore' | 'merge' | 'delete'
    checkpointRenaming: false,
    checkpointNodes: structuredClone(CHECKPOINT_NODES),
    checkpointEnabled: true,
    appSettingsLayout: 'subnav', // 'subnav' | 'centered-expander'
    appSettingsSub: 'appearance',
    appSettingsExpanded: {
      appearance: true,
      autostart: false,
      tray: false,
      behavior: false,
      diagnostics: false
    },
    useThemeAccentForHeader: true,
    accentHex: '#4CC2FF',
    themeMode: 'dark',
    language: 'system',
    autoStartMode: 'off', // 'off' | 'normal' | 'silent'
    closeBehavior: 'exit', // 'exit' | 'tray'
    autoOpenConsole: false,
    showExtensionEntries: (() => {
      try {
        return localStorage.getItem('exhyperv.extensions.showEntries') !== '0';
      } catch {}
      return true;
    })(),
    defaultConsoleMode: (() => {
      try {
        const showExt = localStorage.getItem('exhyperv.extensions.showEntries') !== '0';
        const saved = localStorage.getItem('exhyperv.console.defaultBackend');
        if (saved === 'External' && showExt) return 'External';
        if (saved === 'RdpEnhanced') return 'RdpEnhanced';
      } catch {}
      return 'RdpBasic';
    })(),
    defaultPowerAction: 'Shutdown',
    loggingEnabled: true,
    verboseDiagnostics: false,
    autoCheckUpdate: true,
    aboutExpanded: true,
    updateStatusText: '当前已是最新版本',
    isaVariant: 'single-row-equal',
    isaMigDesign: 'three-way',
    isaMigTerms: 'friendly',
    isaLegacyName: '老旧系统兼容',
    isaByVm: {},
    computeVariant: 'energy-fader',
    computeThumbStyle: 'slim-bar',
    computeActionMode: 'winui-rect',
    noticeMode: 'toast',
    memComputeLayoutMode: '2-1-3',
    memDynamicOffMode: 'disable',
    memSmartPagingAlert: false,
    singleChartHeight: 136,
    diskCandidate: 'a', // 'a' | 'b' | 'c'
    isoChartMode: 'single', // 'single' | 'dual'
    diffParentMode: 'wide', // 'wide' | 'top-right'
    storageAddVariant: 'pivot-2x3', // 'pivot-2x3' | 'pivot-3x2' | 'matrix-card'
    diskCapLayout: 'header-edge-6', // 'header-edge-6' | 'header-edge-8' | 'header-edge' | 'bar-only' | 'bar-pct'
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
    bootEntriesByVm: {},
    checkpointNodesByVm: {},
    checkpointEnabledByVm: {},
    advSettingsByVm: {},
    pcieSettingsByVm: {},
    hostLogicalCores: 12,
    hostCpuModel: '',
    _hydratedVmId: ''
  };

  const EMPTY_LIVE_VM = {
    id: '',
    guid: '',
    name: '暂无虚拟机',
    osImage: '/ui-design/os/Windows.svg',
    osIcon: 'Windows.svg',
    osText: '---',
    running: false,
    stateText: '暂无虚拟机',
    liveUptime: '00:00:00',
    cores: 0,
    memoryGb: 0,
    usedMemoryGb: 0,
    configText: '当前宿主机暂无已注册的 Hyper-V 虚拟机',
    ipWhenRunning: '---',
    gpus: [],
    disks: [],
    adapters: [],
    _empty: true
  };

  let activeVms = structuredClone(VM_ITEMS);
  const demoSnapshots = {
    gpuAssignmentsByVm: structuredClone(state.gpuAssignmentsByVm),
    storageItemsByVm: structuredClone(state.storageItemsByVm),
    networkAdaptersByVm: structuredClone(state.networkAdaptersByVm),
    computeByVm: structuredClone(state.computeByVm),
    isaByVm: structuredClone(state.isaByVm),
    bootEntries: structuredClone(state.bootEntries),
    serialPortsByVm: structuredClone(state.serialPortsByVm),
    vtpmByVm: structuredClone(state.vtpmByVm),
    pcieDevicesByVm: structuredClone(state.pcieDevicesByVm),
    pcieSettingsByVm: structuredClone(state.pcieSettingsByVm),
    checkpointNodes: structuredClone(state.checkpointNodes),
    checkpointSelectedId: state.checkpointSelectedId,
    checkpointEnabled: state.checkpointEnabled,
    advSettings: structuredClone(state.advSettings),
    processorFields: Object.fromEntries(
      PROCESSOR_SECTIONS.flatMap((s) => (s.fields || []).map((f) => [f.id, { value: f.value, savedValue: f.savedValue ?? f.value }]))
    ),
    memoryFields: Object.fromEntries(
      MEMORY_SECTIONS.flatMap((s) => (s.fields || []).map((f) => [f.id, { value: f.value, savedValue: f.savedValue ?? f.value }]))
    )
  };

  const getVm = () => activeVms.find((v) => v.id === state.selectedVmId) || activeVms[0] || EMPTY_LIVE_VM;

  const KNOWN_SB_TEMPLATES = {
    '1734c6e8-3154-4dda-ba5f-a874cc483422': 'Microsoft Windows',
    '272e7447-90a4-4563-a4b9-8e4ab00526ce': 'Microsoft UEFI 证书颁发机构',
    '4292ae2b-ee2c-42b5-a969-dd8f8689f6f3': '受防护的开源 VM'
  };

  function resolveSecureBootTemplateLabel(templateId, templates = []) {
    const clean = String(templateId || '').trim().toLowerCase().replace(/[{}]/g, '');
    if (!clean) return 'Microsoft Windows';
    if (KNOWN_SB_TEMPLATES[clean]) return KNOWN_SB_TEMPLATES[clean];
    const hit = (Array.isArray(templates) ? templates : []).find((t) =>
      String(t.Id ?? t.id ?? '').trim().toLowerCase().replace(/[{}]/g, '') === clean
    );
    const name = String(hit?.Name ?? hit?.name ?? '').toLowerCase();
    if (name.includes('uefi')) return 'Microsoft UEFI 证书颁发机构';
    if (name.includes('shielded') || name.includes('open source') || name.includes('开源')) return '受防护的开源 VM';
    return 'Microsoft Windows';
  }

  function applyVmDetailToState(vmId, detailOverride = null) {
    if (!serverAdapter.isLive || !vmId) return false;
    const detail = detailOverride || serverAdapter.state.vmDetailsByVm?.[vmId];
    const vm = activeVms.find((v) => v.id === vmId);
    const isSelected = vmId === state.selectedVmId;

    if (!detail) {
      if (isSelected && state._hydratedVmId !== vmId) {
        state.bootEntries = [];
        state.checkpointNodes = [];
        state.checkpointSelectedId = '';
        state.storageItems = structuredClone(state.storageItemsByVm[vmId] || []);
        if (!state.serialPortsByVm[vmId]) state.serialPortsByVm[vmId] = [];
        if (!state.networkAdaptersByVm[vmId]) state.networkAdaptersByVm[vmId] = vm?.adapters ? structuredClone(vm.adapters) : [];
        state.affinityMask = new Set();
        state.savedAffinityMask = new Set();
        const vmFeatures = readCachedVmConsoleFeatures(vmId);
        state.advSettings = {
          secureBoot: false,
          secureBootTemplate: 'Microsoft Windows',
          tpmEnabled: false,
          encryptState: false,
          shielding: false,
          bindHostTpm: false,
          vbsOptOut: false,
          migratableDisabled: false,
          fullScsi: false,
          numLock: false,
          consoleSupport: true,
          ...vmFeatures,
          resolution: '默认',
          lockOnDisconnect: false,
          hibernation: false,
          turnOffOnRestart: false,
          batteryPassthrough: false
        };
      }
      return false;
    }

    state.storageItemsByVm[vmId] = structuredClone(detail.storageItems || []);
    if (vm) {
      vm.disks = detail.disks || vm.disks;
      vm.storageItems = detail.storageItems || vm.storageItems;
    }
    state.bootEntriesByVm[vmId] = structuredClone(detail.bootEntries || []);
    state.checkpointNodesByVm[vmId] = structuredClone(detail.checkpointNodes || []);
    state.checkpointEnabledByVm[vmId] = detail.checkpointsEnabled !== false;
    state.serialPortsByVm[vmId] = structuredClone(detail.serialPorts || []);
    if (Array.isArray(detail.networkAdapters)) {
      state.networkAdaptersByVm[vmId] = structuredClone(detail.networkAdapters);
      if (vm) vm.adapters = structuredClone(detail.networkAdapters);
    }

    if (detail.vtpm) {
      const rawHosts = detail.vtpm.AuthorizedHosts ?? detail.vtpm.authorizedHosts;
      const authHosts = Array.isArray(rawHosts)
        ? rawHosts.map((h, i) => ({
            id: (h.IsLocal ?? h.isLocal) ? 'local' : `host-${i}`,
            name: h.Name ?? h.name ?? '',
            thumbprint:
              h.SigningThumbprint ?? h.signingThumbprint ?? h.EncryptionThumbprint ?? h.encryptionThumbprint ?? '',
            isLocal: Boolean(h.IsLocal ?? h.isLocal)
          }))
        : [];
      const guardian = detail.vtpm.LocalGuardianName ?? detail.vtpm.localGuardianName ?? '';
      state.vtpmByVm[vmId] = {
        kpConfigured: Boolean(detail.vtpm.KeyProtectorConfigured ?? detail.vtpm.keyProtectorConfigured),
        hasPreviousKey: Boolean(detail.vtpm.HasPreviousKey ?? detail.vtpm.hasPreviousKey),
        boundHost: guardian ? `当前宿主 (${guardian})` : authHosts[0]?.name || '',
        authorizedHosts: authHosts,
        signingThumbprint: detail.vtpm.LocalSigningThumbprint ?? detail.vtpm.localSigningThumbprint ?? '',
        guestStateFile:
          detail.vtpm.GuestStateFile ?? detail.vtpm.guestStateFile ?? detail.vtpm.GuestStateDescription ?? ''
      };
    }

    state.pcieDevicesByVm[vmId] = structuredClone(detail.pcieDevices || []);
    if (detail.pcie) {
      state.pcieSettingsByVm[vmId] = {
        emulationEnabled: Boolean(detail.pcie.EmulationEnabled ?? detail.pcie.emulationEnabled ?? true),
        topology: String(detail.pcie.Topology ?? detail.pcie.topology ?? '0'),
        bootVpci: Boolean(detail.pcie.BootPciExpress ?? detail.pcie.bootPciExpress ?? false)
      };
    }

    const res = detail.displayResolution;
    const resType = res?.ResolutionType ?? res?.resolutionType;
    const resW = res?.Width ?? res?.width;
    const resH = res?.Height ?? res?.height;
    const resolutionText =
      resType === 3 && resW && resH
        ? `${resW} x ${resH}`
        : '默认';

    const sec = detail.sec || {};
    const vtpm = detail.vtpm || {};
    const consoleFeatures = detail.consoleFeatures || {};
    const vmAdv = {
      secureBoot: Boolean(sec.SecureBootEnabled ?? sec.secureBootEnabled),
      secureBootTemplate: resolveSecureBootTemplateLabel(
        sec.SecureBootTemplateId ?? sec.secureBootTemplateId,
        detail.secureBootTemplates
      ),
      tpmEnabled: Boolean(vtpm.TpmEnabled ?? vtpm.tpmEnabled ?? sec.TpmEnabled ?? sec.tpmEnabled),
      encryptState: Boolean(vtpm.EncryptEnabled ?? vtpm.encryptEnabled ?? sec.EncryptEnabled ?? sec.encryptEnabled),
      shielding: Boolean(vtpm.Shielded ?? vtpm.shielded ?? sec.Shielded ?? sec.shielded),
      bindHostTpm: Boolean(vtpm.BindToHostTpm ?? vtpm.bindToHostTpm),
      vbsOptOut: Boolean(vtpm.VirtualizationBasedSecurityOptOut ?? vtpm.virtualizationBasedSecurityOptOut),
      migratableDisabled: Boolean(
        vtpm.MigrationDisabled ?? vtpm.migrationDisabled ?? (vtpm.MigratablePolicy === 1)
      ),
      fullScsi: false,
      numLock: Boolean(detail.numLock),
      consoleSupport:
        detail.consoleSupportEnabled != null ? Boolean(detail.consoleSupportEnabled) : true,
      consoleClipboard: (consoleFeatures.Clipboard ?? consoleFeatures.clipboard) !== false,
      consoleAudioOutput: (consoleFeatures.AudioOutput ?? consoleFeatures.audioOutput) !== false,
      consoleAudioInput: (consoleFeatures.AudioInput ?? consoleFeatures.audioInput) !== false,
      resolution: resolutionText,
      lockOnDisconnect: false,
      hibernation: false,
      turnOffOnRestart: false,
      batteryPassthrough: Boolean(detail.battery?.Enabled ?? detail.battery?.enabled)
    };
    state.advSettingsByVm[vmId] = vmAdv;

    if (!isSelected) return true;

    const switchedVm = state._hydratedVmId !== vmId;
    state._hydratedVmId = vmId;
    state.storageItems = structuredClone(detail.storageItems || []);
    state.bootEntries = structuredClone(detail.bootEntries || []);
    state.checkpointNodes = structuredClone(detail.checkpointNodes || []);
    state.checkpointEnabled = detail.checkpointsEnabled !== false;
    if (!state.checkpointNodes.some((n) => n.id === state.checkpointSelectedId)) {
      const curNode =
        state.checkpointNodes.find((n) => n.isCurrent || n.type === 'Current') ||
        state.checkpointNodes[state.checkpointNodes.length - 1];
      state.checkpointSelectedId = curNode?.id || '';
    }
    state.advSettings = structuredClone(vmAdv);

    if (detail.procRaw) {
      const pr = detail.procRaw;
      const computeObj = {
        Count: Number(pr.Count ?? vm?.cores ?? 1),
        Reservation: Math.round(Number(pr.Reservation ?? 0) / 1000),
        Limit: Math.min(100, Math.round(Number(pr.Limit ?? 100000) / 1000)),
        RelativeWeight: Number(pr.RelativeWeight ?? 100)
      };
      const curComp = state.computeByVm[vmId];
      const compClean =
        !curComp ||
        switchedVm ||
        (curComp.draft.Count === curComp.saved.Count &&
          curComp.draft.Reservation === curComp.saved.Reservation &&
          curComp.draft.Limit === curComp.saved.Limit &&
          curComp.draft.RelativeWeight === curComp.saved.RelativeWeight);
      if (compClean) {
        state.computeByVm[vmId] = { saved: { ...computeObj }, draft: { ...computeObj } };
      } else {
        state.computeByVm[vmId].saved = { ...computeObj };
      }

      for (const secDef of PROCESSOR_SECTIONS) {
        for (const f of secDef.fields || []) {
          if (!(f.id in pr)) continue;
          const rawVal = pr[f.id];
          let nextVal;
          if (f.kind === 'toggle') {
            nextVal = Boolean(rawVal);
          } else if (f.id === 'Reservation' || f.id === 'Limit') {
            nextVal = rawVal == null ? '' : String(Math.min(100, Math.round(Number(rawVal) / 1000)));
          } else {
            nextVal = rawVal == null ? '' : String(rawVal);
          }
          const wasDirty = !switchedVm && f.savedValue !== undefined && f.value !== f.savedValue;
          f.savedValue = nextVal;
          if (!wasDirty) f.value = nextVal;
        }
      }

      const isaObj = {
        nested: Boolean(pr.ExposeVirtualizationExtensions),
        legacyOs: Boolean(pr.CompatibilityForOlderOperatingSystemsEnabled),
        migrationEnabled: Boolean(pr.CompatibilityForMigrationEnabled),
        migrationMode: String(pr.CompatibilityForMigrationMode || 'MinimumFeatureSet'),
        cpuBrand: String(pr.CpuBrandString || '')
      };
      const curIsa = state.isaByVm[vmId];
      const isaClean =
        !curIsa ||
        switchedVm ||
        (curIsa.draft.nested === curIsa.saved.nested &&
          curIsa.draft.legacyOs === curIsa.saved.legacyOs &&
          curIsa.draft.migrationEnabled === curIsa.saved.migrationEnabled &&
          curIsa.draft.migrationMode === curIsa.saved.migrationMode &&
          curIsa.draft.cpuBrand === curIsa.saved.cpuBrand);
      if (isaClean) {
        state.isaByVm[vmId] = { saved: { ...isaObj }, draft: { ...isaObj } };
      } else {
        state.isaByVm[vmId].saved = { ...isaObj };
      }
    }

    if (detail.memRaw) {
      const mr = detail.memRaw;
      for (const secDef of MEMORY_SECTIONS) {
        for (const f of secDef.fields || []) {
          if (!(f.id in mr)) continue;
          const rawVal = mr[f.id];
          const nextVal = f.kind === 'toggle' ? Boolean(rawVal) : rawVal == null ? '' : String(rawVal);
          const wasDirty = !switchedVm && f.savedValue !== undefined && f.value !== f.savedValue;
          f.savedValue = nextVal;
          if (!wasDirty) f.value = nextVal;
        }
      }
    }

    if (detail.affinityState) {
      const affRaw =
        detail.affinityState.SavedProcessors ??
        detail.affinityState.savedProcessors ??
        detail.affinityState.AppliedProcessors ??
        detail.affinityState.appliedProcessors ??
        [];
      const nextAff = Array.isArray(affRaw) ? affRaw.map((n) => Number(n)).filter(Number.isFinite) : [];
      const affClean =
        switchedVm ||
        !state.savedAffinityMask ||
        (state.affinityMask?.size === state.savedAffinityMask.size &&
          Array.from(state.affinityMask).every((x) => state.savedAffinityMask.has(x)));
      state.savedAffinityMask = new Set(nextAff);
      if (affClean) {
        state.affinityMask = new Set(nextAff);
      }
    }

    return true;
  }

  function updateRemoteNavIndicator(st) {
    const demoBadge = document.getElementById('winuiDemoBadge');
    if (demoBadge) {
      demoBadge.hidden = st.mode === 'live' && st.status === 'connected';
    }
    const dot = document.getElementById('navRemoteDot');
    const item = document.getElementById('navRemoteItem');
    if (!dot) return;
    dot.classList.remove('is-live', 'is-connecting', 'is-error');
    if (st.mode === 'live' && st.status === 'connected') {
      dot.hidden = false;
      dot.classList.add('is-live');
      if (item) item.title = `远程（已连接 ${st.serverUrl}）`;
    } else if (st.status === 'connecting') {
      dot.hidden = false;
      dot.classList.add('is-connecting');
      if (item) item.title = '远程（正在连接宿主机）';
    } else if (st.status === 'error') {
      dot.hidden = false;
      dot.classList.add('is-error');
      if (item) item.title = '远程（连接失败，当前保持演示模式）';
    } else {
      dot.hidden = true;
      if (item) item.title = '远程（当前为演示模式）';
    }
  }

  function syncTelemetryTarget() {
    if (!serverAdapter.isLive) return;
    const vm = getVm();
    if (state.navTarget === 'host') {
      const runningIds = activeVms.filter((v) => v.running && v.id).map((v) => v.id);
      void serverAdapter.syncTelemetrySubscription({ host: true, vmIds: runningIds });
    } else if (state.navTarget === 'vm') {
      const vmIds = vm && vm.id && !vm._empty ? [vm.id] : [];
      void serverAdapter.syncTelemetrySubscription({ host: serverAdapter.hasToolset('host') && vmIds.length === 0, vmIds });
    }
  }

  function applyLiveVmListToUi(vms) {
    activeVms = Array.isArray(vms) ? vms : [];
    if (!activeVms.some((v) => v.id === state.selectedVmId)) {
      state.selectedVmId = activeVms[0]?.id || '';
    }
    activeVms.forEach((vm) => {
      state.gpuAssignmentsByVm[vm.id] = vm.gpus || [];
      const cachedDetail = serverAdapter.state.vmDetailsByVm?.[vm.id];
      if (cachedDetail?.storageItems?.length) {
        vm.disks = cachedDetail.disks || vm.disks;
        vm.storageItems = cachedDetail.storageItems;
        state.storageItemsByVm[vm.id] = structuredClone(cachedDetail.storageItems);
      } else {
        state.storageItemsByVm[vm.id] = vm.storageItems?.length ? vm.storageItems : vm.disks || [];
      }
      if (Array.isArray(cachedDetail?.networkAdapters) && cachedDetail.networkAdapters.length > 0) {
        const mergedNics = cachedDetail.networkAdapters.map((cn, idx) => {
          const liveAdp = (vm.adapters || []).find((a) => a.key === cn.key || a.key === cn.id) || (vm.adapters || [])[idx];
          return liveAdp
            ? {
                ...cn,
                sendRate: liveAdp.sendRate || cn.sendRate,
                recvRate: liveAdp.recvRate || cn.recvRate,
                txRate: liveAdp.txRate || cn.txRate,
                rxRate: liveAdp.rxRate || cn.rxRate,
                sendPts: liveAdp.sendPts || cn.sendPts,
                recvPts: liveAdp.recvPts || cn.recvPts,
                ips: liveAdp.ips?.length ? liveAdp.ips : cn.ips,
                ipv4: liveAdp.ipv4 || cn.ipv4,
                ipv6: liveAdp.ipv6 || cn.ipv6
              }
            : cn;
        });
        vm.adapters = mergedNics;
        state.networkAdaptersByVm[vm.id] = structuredClone(mergedNics);
      } else {
        state.networkAdaptersByVm[vm.id] = vm.adapters || [];
      }
    });
    if (serverAdapter.isLive) {
      hostMod?.applyLiveHostTelemetry?.({
        liveVms: activeVms,
        sys: {
          runningVmCount: activeVms.filter((v) => v.running).length,
          totalVmCount: activeVms.length,
          vmAllocatedRamGb: Number(activeVms.reduce((sum, v) => sum + (v.running ? v.memoryGb || 0 : 0), 0).toFixed(1)),
          vmAllocatedVcpu: activeVms.reduce((sum, v) => sum + (v.running ? v.cores || 0 : 0), 0)
        }
      });
    }
    if (serverAdapter.isLive && state.selectedVmId) {
      applyVmDetailToState(state.selectedVmId);
      const curVm = getVm();
      if (curVm && !curVm._empty) {
        void serverAdapter.loadVmDetails(curVm.id);
      }
    }
    renderVmList();
    renderDashboardSummary();
    if (state.navTarget === 'vm') {
      renderActiveWorkspace();
    }
    syncTelemetryTarget();
  }

  const serverAdapter = createServerAdapter({
    onStatusChange: (st) => {
      updateRemoteNavIndicator(st);
      if (state.navTarget === 'remote') {
        const pageRemote = document.getElementById('pageRemote');
        if (pageRemote) renderRemotePage(pageRemote, serverAdapter);
      }
    },
    onVmsUpdated: (vms) => {
      applyLiveVmListToUi(vms);
    },
    onVmDetailsUpdated: (vmId, detail) => {
      applyVmDetailToState(vmId, detail);
      if (vmId === state.selectedVmId) {
        renderVmList();
        renderDashboardSummary();
        if (state.navTarget === 'vm') {
          renderActiveWorkspace();
        }
      }
    },
    onHostUpdated: (snapshot) => {
      if (snapshot?.sys) {
        if (snapshot.sys.threads) state.hostLogicalCores = snapshot.sys.threads;
        if (snapshot.sys.cpu) state.hostCpuModel = snapshot.sys.cpu;
      }
      loadHostMod().then((mod) => {
        mod.applyLiveHostSnapshot(snapshot);
        mod.setHostActionHandler((action, payload) => serverAdapter.executeHostAction(action, payload));
      });
    },
    onHostTelemetryTick: (snapshot) => {
      hostMod?.applyLiveHostTelemetry?.(snapshot);
    },
    onVmTelemetryTick: () => {
      if (state.navTarget === 'vm') {
        renderDashboardSummary();
        if (state.activeTabKey === 'overview') renderOverview();
      }
    },
    onModeSwitched: (nextMode) => {
      if (nextMode === 'demo') {
        activeVms = structuredClone(VM_ITEMS);
        state.selectedVmId = activeVms[0]?.id || 'vm-test';
        state.hostLogicalCores = 12;
        state.hostCpuModel = '';
        state._hydratedVmId = '';
        state.bootEntriesByVm = {};
        state.checkpointNodesByVm = {};
        state.checkpointEnabledByVm = {};
        state.advSettingsByVm = {};
        state.gpuAssignmentsByVm = structuredClone(demoSnapshots.gpuAssignmentsByVm);
        state.storageItemsByVm = structuredClone(demoSnapshots.storageItemsByVm);
        state.networkAdaptersByVm = structuredClone(demoSnapshots.networkAdaptersByVm);
        state.computeByVm = structuredClone(demoSnapshots.computeByVm);
        state.isaByVm = structuredClone(demoSnapshots.isaByVm);
        state.bootEntries = structuredClone(demoSnapshots.bootEntries);
        state.serialPortsByVm = structuredClone(demoSnapshots.serialPortsByVm);
        state.vtpmByVm = structuredClone(demoSnapshots.vtpmByVm);
        state.pcieDevicesByVm = structuredClone(demoSnapshots.pcieDevicesByVm);
        state.pcieSettingsByVm = structuredClone(demoSnapshots.pcieSettingsByVm);
        state.hostPcieDevices = null;
        state.affinityMask = new Set([0, 1, 2, 3, 4, 5, 6, 7]);
        state.savedAffinityMask = new Set(state.affinityMask);
        state.checkpointNodes = structuredClone(demoSnapshots.checkpointNodes);
        state.checkpointSelectedId = demoSnapshots.checkpointSelectedId;
        state.checkpointEnabled = demoSnapshots.checkpointEnabled;
        state.advSettings = structuredClone(demoSnapshots.advSettings);
        for (const secDef of PROCESSOR_SECTIONS) {
          for (const f of secDef.fields || []) {
            const snapF = demoSnapshots.processorFields[f.id];
            if (snapF) {
              f.value = snapF.value;
              f.savedValue = snapF.savedValue;
            }
          }
        }
        for (const secDef of MEMORY_SECTIONS) {
          for (const f of secDef.fields || []) {
            const snapF = demoSnapshots.memoryFields[f.id];
            if (snapF) {
              f.value = snapF.value;
              f.savedValue = snapF.savedValue;
            }
          }
        }
        hostMod?.resetHostToDemo?.();
        renderVmList();
        renderDashboardSummary();
        renderActiveWorkspace();
        showNotice('已切回演示模式', 'info');
      } else {
        syncTelemetryTarget();
        showNotice(`已连接到宿主机 ${serverAdapter.state.serverUrl}`, 'success');
      }
    }
  });

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
      colorText.textContent = state.useThemeAccentForHeader ? state.accentHex : '中性灰';
    }
    const accentSwitch = document.getElementById('settingsAccentSwitch');
    if (accentSwitch) {
      accentSwitch.classList.toggle('on', state.useThemeAccentForHeader);
      accentSwitch.setAttribute('aria-checked', String(state.useThemeAccentForHeader));
    }
    const accentSwitchLabel = document.getElementById('settingsAccentSwitchLabel');
    if (accentSwitchLabel) {
      accentSwitchLabel.textContent = state.useThemeAccentForHeader ? '开启' : '关闭';
    }
    const paletteRow = document.getElementById('settingsPaletteRow');
    if (paletteRow) {
      paletteRow.classList.toggle('is-disabled', !state.useThemeAccentForHeader);
    }
    document.querySelectorAll('[data-swatch-hex]').forEach((btn) => {
      const hex = btn.getAttribute('data-swatch-hex') || '';
      const active = hex.toUpperCase() === state.accentHex.toUpperCase();
      const chk = btn.querySelector('.swatch-check');
      if (chk) chk.style.visibility = active ? 'visible' : 'hidden';
    });
    consoleCtrl?.syncTheme();
  }

  let noticeTimer = null;
  let inlineNoticeTimer = null;
  let lastNoticeFullText = '';
  let noticeCopyResetTimer = null;

  function showNotice(message, severity = 'success') {
    const toast = document.getElementById('vmSuccessToast');
    const toastTxt = document.getElementById('vmSuccessToastText');
    const banner = document.getElementById('vmNoticeBanner');
    const bannerTxt = document.getElementById('vmNoticeBannerText');
    const bannerIcon = document.getElementById('vmNoticeBannerIcon');
    const detailsBtn = document.getElementById('vmNoticeDetailsBtn');
    const mode = state.noticeMode || 'toast';

    if (noticeTimer) {
      clearTimeout(noticeTimer);
      noticeTimer = null;
    }
    if (inlineNoticeTimer) {
      clearTimeout(inlineNoticeTimer);
      inlineNoticeTimer = null;
    }

    const useToast = severity === 'success' && mode !== 'banner';
    if (useToast && toast && toastTxt) {
      if (banner) banner.hidden = true;
      toastTxt.textContent = message;
      toast.hidden = false;
      noticeTimer = setTimeout(() => {
        toast.hidden = true;
      }, 2600);
      return;
    }

    if (toast) toast.hidden = true;
    if (!banner || !bannerTxt) return;
    banner.classList.remove('is-success', 'is-warning', 'is-info', 'is-error');
    banner.classList.add(`is-${severity}`);
    if (bannerIcon) {
      const icons = { warning: '\uF13C', info: '\uE946', success: '\uE73E', error: '\uE711' };
      bannerIcon.textContent = icons[severity] || '\uE711';
    }
    const rawText = String(message || '').trim() || '操作失败';
    lastNoticeFullText = rawText;
    const lines = rawText
      .replace(/\r\n/g, '\n')
      .split('\n')
      .map((s) => s.trim())
      .filter(Boolean);
    const explicitOverflow = lines.length > 2 || rawText.length > 110;
    bannerTxt.textContent = lines.length > 2 ? lines.join(' ') : rawText;
    banner.hidden = false;
    if (detailsBtn) {
      detailsBtn.hidden = !explicitOverflow;
      requestAnimationFrame(() => {
        if (!banner.hidden) {
          detailsBtn.hidden = !(explicitOverflow || bannerTxt.scrollHeight > bannerTxt.clientHeight + 2);
        }
      });
    }
    if (severity === 'success' || severity === 'info') {
      noticeTimer = setTimeout(() => {
        banner.hidden = true;
      }, 2600);
    }
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
    const vm = getVm();
    if (serverAdapter.isLive) {
      const perCore = vm?.telemetry?.perCoreHistories?.[idx];
      const hist =
        Array.isArray(perCore) && perCore.length > 0
          ? perCore
          : Array.isArray(vm?.telemetry?.cpuHistory) && vm.telemetry.cpuHistory.length > 0
            ? vm.telemetry.cpuHistory
            : [0, 0];
      const latest = hist[hist.length - 1] ?? 0;
      return { base: latest, pts: hist.slice(-7) };
    }
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
      subtitle: '动态扩展 (SCSI 0:0)',
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
      subtitle: '固定大小 (SCSI 0:1)',
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
      subtitle: '物理直通 (SCSI 0:2)',
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
      subtitle: '差异磁盘 (SCSI 0:3)',
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
    const filtered = activeVms.filter((vm) => !q || vm.name.toLowerCase().includes(q));

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
        state.storageMode = 'list';
        state.storageInlineEditIdx = null;
        state.nicMode = 'list';
        state.vtpmMode = 'main';
        state.vtpmSettingsTab = 'keys';
        state.vtpmConfirmDisable = false;
        state.gpuScenario = 'normal';
        state.gpuSelectedRebindId = '';
        state.ribbonComboOpen = false;
        syncStandaloneHostRailPanel();
        notifyParentHostState();
        if (serverAdapter.isLive && state.selectedVmId) {
          applyVmDetailToState(state.selectedVmId);
        } else if (state.selectedVmId) {
          Object.assign(state.advSettings, readCachedVmConsoleFeatures(state.selectedVmId));
        }
        renderVmList();
        renderDashboardSummary();
        renderActiveWorkspace();
        syncTelemetryTarget();
        if (serverAdapter.isLive && state.selectedVmId) {
          void serverAdapter.loadVmDetails(state.selectedVmId);
        }
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
      sysIconPanel.style.cursor = vm._empty ? 'default' : 'pointer';
      sysIconPanel.title = vm._empty ? '' : '点击切换系统标志';
      if (!sysIconPanel.dataset.osPickerBound) {
        sysIconPanel.dataset.osPickerBound = '1';
        sysIconPanel.addEventListener('click', (e) => {
          e.stopPropagation();
          if (!getVm()._empty) toggleOsPickerFlyout();
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
      powerMainBtn.disabled = Boolean(vm._empty);
      powerDropBtn.hidden = !vm.running || Boolean(vm._empty);
      if ((!vm.running || vm._empty) && powerFlyout) powerFlyout.hidden = true;
    }
    const connectBtn = document.getElementById('vmConnectBtn');
    if (connectBtn) connectBtn.disabled = Boolean(vm._empty);

    const stateText = document.getElementById('vmStateText');
    const uptimeText = document.getElementById('vmUptimeText');
    const genTextEl = document.getElementById('vmGenText');
    if (stateText) stateText.textContent = serverAdapter.isLive ? (vm.stateText || (vm.running ? '运行中' : '已关机')) : (vm.running ? '运行中' : '已关机');
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
          `<div class="vm-badge"><span class="fi vm-badge-icon">&#xE81E;</span><span class="vm-badge-text">${vm.cores} 核 / ${vm.memoryGb} GB</span></div>`
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
    if (matrixSpec) matrixSpec.textContent = `${vm.cores} 核 / ${vm.memoryGb} GB`;
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
        if (!curVm || curVm._empty) return;
        closeOsPickerFlyout();
        if (serverAdapter.isLive) {
          serverAdapter
            .setVmOsType(curVm.id, picked)
            .then(() => showNotice('系统图标已同步到宿主机元数据'))
            .catch((err) => showNotice(err?.message || '修改系统图标失败', 'error'));
          return;
        }
        curVm.osText = picked;
        curVm.osImage = `/ui-design/os/${picked}.svg`;
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
      const spIdx = tabs.findIndex((t) => t.key === 'checkpoints');
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
    const isLive = serverAdapter.isLive;
    const cpuPctNum = isLive ? Number(vm.telemetry?.cpuPct ?? 0) : 24.8;
    const cpuPctText = vm.running ? `${cpuPctNum.toFixed(1)}%` : '0%';
    const cpuHist = isLive
      ? Array.isArray(vm.telemetry?.cpuHistory) && vm.telemetry.cpuHistory.length >= 2
        ? vm.telemetry.cpuHistory.slice(-7)
        : [0, 0]
      : [18, 22, 20, 29, 26, 25, 25];
    const memPctRaw = vm.running && vm.memoryGb > 0 ? Math.round((vm.usedMemoryGb / vm.memoryGb) * 100) : 0;
    const memPct = isLive ? Math.min(100, Math.max(0, memPctRaw)) : memPctRaw;
    const memHist = isLive
      ? Array.isArray(vm.telemetry?.memHistory) && vm.telemetry.memHistory.length >= 2
        ? vm.telemetry.memHistory.slice(-7)
        : [memPct, memPct]
      : [58, 60, 62, 65, 67, 69, memPct];
    const usedMemText = vm.running ? vm.usedMemoryGb.toFixed(1) : '0.0';
    const totalMemText = vm.memoryGb.toFixed(1);
    const availGb = vm.running ? Math.max(0, vm.memoryGb - vm.usedMemoryGb).toFixed(1) : vm.memoryGb.toFixed(1);
    const coreSamples = Array.from({ length: vm.cores }, (_, i) => getCoreSample(i));
    return {
      activeGpu,
      activeDisk,
      activeNet,
      cpuPctNum,
      cpuPctText,
      cpuHist,
      memPct,
      memHist,
      usedMemText,
      totalMemText,
      availGb,
      coreSamples
    };
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
    single: 'Snapdragon(R) X 12-core X1E80100 @ 3.40 GHz',
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
    const hostCpuModel =
      state.hostCpuModel || affinitySec?.cpuModel || 'Snapdragon(R) X 12-core X1E80100 @ 3.40 GHz';
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
    const demandMib = vm.running
      ? serverAdapter.isLive
        ? Math.round((vm.demandMemoryGb ?? vm.usedMemoryGb ?? 0) * 1024)
        : Math.round((vm.usedMemoryGb ?? 1.4) * 1024)
      : 0;
    const smartPagingActive = serverAdapter.isLive
      ? Boolean(vm.running && vm.smartPagingMiB > 0)
      : Boolean(vm.running && dynamicEnabled && state.memSmartPagingAlert);

    return {
      dynamicEnabled,
      rangeText: `${formatMibCapacity(minMib)} - ${formatMibCapacity(maxMib)}`,
      pressureText: vm.running ? `${t.memPct}%` : '---',
      demandText: vm.running && demandMib > 0 ? formatMibCapacity(demandMib) : '---',
      smartPagingActive,
      smartPagingText: smartPagingActive
        ? serverAdapter.isLive
          ? `${vm.smartPagingMiB} MB（使用中）`
          : '256 MB（使用中）'
        : ''
    };
  }

  function buildMemCanvasHtml(vm, t, chartHeight = 184) {
    const meta = getMemorySummaryMeta(vm, t);
    const memSeries = t.memHist || [t.memPct, t.memPct];
    const chart = createUsageChartHtml(
      '内存使用趋势',
      vm.running,
      `${t.memPct}%`,
      memSeries,
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
    const vm = getVm();
    const liveTel = serverAdapter.isLive ? vm?.telemetry : null;
    if (liveTel && (Array.isArray(liveTel.gpu3dHistory) || Array.isArray(liveTel.gpuHistory))) {
      return {
        pct: liveTel.gpu3dPct || 0,
        pts3d: (liveTel.gpu3dHistory || liveTel.gpuHistory || [0, 0]).slice(-6),
        ptsCopy: (liveTel.gpuCopyHistory || [0, 0]).slice(-6),
        ptsEnc: (liveTel.gpuEncHistory || [0, 0]).slice(-6),
        ptsDec: (liveTel.gpuDecHistory || [0, 0]).slice(-6)
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
          : gpu.name && (gpu.name.includes('Qualcomm') || gpu.name.includes('Adreno'))
            ? 'Qualcomm Incorporated'
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
    if (!disk) {
      return {
        isPhysical: false,
        isIso: false,
        isDifferencing: false,
        isStale: false,
        icon: '&#xEDA2;',
        slot: `SCSI 0:${idx}`,
        sourceState: '虚拟磁盘',
        rightTitle: '类型',
        rightState: '—',
        rightCopy: '',
        title: '暂无磁盘',
        fileSizeText: '---',
        capacityText: '---',
        ratio: 0,
        pctText: '0%',
        headerReading: '---',
        railStat: '',
        railTip: '',
        readRate: '0 B/s',
        writeRate: '0 B/s',
        readIops: 0,
        writeIops: 0,
        readPts: [0, 0],
        writePts: [0, 0],
        diskFormatState: 'VHDX',
        diskTypeState: '—',
        parentPath: '',
        pathTitle: '路径',
        pathText: '未配置',
        pathCopy: ''
      };
    }

    const slot = disk.controllerLoc || `SCSI 0:${idx}`;
    const mode = serverAdapter.isLive ? 'live' : idx === 0 ? state.diskPreviewMode : 'virtual';

    if (mode === 'live') {
      const readRate = vm.running ? (disk.readRate || '0 B/s') : '0 B/s';
      const writeRate = vm.running ? (disk.writeRate || '0 B/s') : '0 B/s';
      const readPts = Array.isArray(disk.readPts) && disk.readPts.length >= 2 ? disk.readPts.slice(-7) : [0, 0];
      const writePts = Array.isArray(disk.writePts) && disk.writePts.length >= 2 ? disk.writePts.slice(-7) : [0, 0];

      if (disk.driveType === 'DvdDrive') {
        const isPhysCd = disk.diskType === 'Physical';
        const hasMedia = isPhysCd || Boolean(disk.hasIsoMedia && disk.path);
        const title = disk.title || (hasMedia ? 'ISO 镜像' : '空光驱');
        const capText = hasMedia && disk.capacityText !== '---' ? disk.capacityText : hasMedia ? '已挂载' : '未挂载';
        return {
          isPhysical: isPhysCd,
          isIso: true,
          isDifferencing: false,
          isStale: false,
          icon: '&#xE958;',
          slot,
          diskNumber: disk.diskNumber,
          serialNumber: disk.serialNumber || '',
          sourceState: isPhysCd ? '物理光驱' : '虚拟光驱',
          rightTitle: isPhysCd ? '序列号' : '',
          rightState: isPhysCd ? (disk.serialNumber || '未取得') : '',
          rightCopy: isPhysCd ? (disk.serialNumber || '') : '',
          title,
          fileSizeText: disk.fileSizeText !== '---' ? disk.fileSizeText : capText,
          capacityText: capText,
          pctText: hasMedia ? '只读' : '空',
          headerReading: capText,
          railStat: vm.running && hasMedia ? `读 ${readRate} 写 ${writeRate}` : '',
          railTip: disk.path ? `${title}\n${disk.path}` : title,
          readRate,
          writeRate: '0 B/s',
          readIops: disk.readIops ?? 0,
          writeIops: 0,
          readPts,
          writePts: [0, 0],
          diskTypeState: '',
          parentPath: '',
          pathTitle: '路径',
          pathText: disk.path || '未挂载镜像',
          pathCopy: disk.path || ''
        };
      }

      if (disk.diskType === 'Physical' || disk.physicalInfo) {
        const p = disk.physicalInfo || {};
        const isStale = Boolean(disk.isPassthroughStale);
        const diskNum = p.diskNumber ?? disk.diskNumber ?? idx;
        const physPath = disk.path || `\\\\.\\PhysicalDrive${diskNum}`;
        const capText = disk.capacityText || p.capacityText || '---';
        const serialNum = p.serialNumber || disk.serialNumber || '未取得';
        return {
          isPhysical: true,
          isIso: false,
          isDifferencing: false,
          isStale,
          icon: '&#xEDA2;',
          slot,
          diskNumber: diskNum,
          serialNumber: serialNum,
          sourceState: isStale ? '物理磁盘（脱机）' : '物理磁盘',
          rightTitle: '序列号',
          rightState: serialNum,
          rightCopy: serialNum === '未取得' ? '' : serialNum,
          title: isStale ? `PhysicalDrive${diskNum}` : (p.model || disk.title || `PhysicalDrive${diskNum}`),
          fileSizeText: capText,
          capacityText: capText,
          pctText: '100%',
          headerReading: isStale ? '已脱机' : capText,
          railStat: isStale ? '已脱机' : vm.running ? `读 ${readRate} 写 ${writeRate}` : '',
          railTip: `${p.model || disk.title}\n${physPath}`,
          readRate,
          writeRate,
          readIops: disk.readIops ?? p.readIops ?? 0,
          writeIops: disk.writeIops ?? p.writeIops ?? 0,
          readPts,
          writePts,
          diskTypeState: isStale ? '物理直通（脱机）' : '物理直通',
          parentPath: '',
          pathTitle: '路径',
          pathText: physPath,
          pathCopy: isStale ? '' : physPath
        };
      }

      const rawType = String(disk.vhdType || disk.vhdFormat || disk.subtitle || 'Dynamic');
      const isFixed = rawType === 'Fixed' || rawType.includes('固定');
      const isDiff = rawType === 'Differencing' || Boolean(disk.parentPath) || rawType.includes('差异') || rawType.includes('差分');
      const diskTypeState = isFixed ? '固定大小' : isDiff ? '差异' : '动态扩展';
      const ext = (disk.path || '').toLowerCase().endsWith('.vhd') ? 'VHD' : 'VHDX';
      const fileSizeText = isFixed ? disk.capacityText : disk.fileSizeText;
      const capacityText = disk.capacityText;
      const ratio = isFixed ? 1 : disk.ratio || 0;
      const pctNum = ratio * 100;
      const pctText = `${pctNum < 1 && pctNum > 0 ? pctNum.toFixed(1) : Math.round(pctNum)}%`;
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
        ratio,
        pctText,
        headerReading: `${fileSizeText} / ${capacityText}`,
        railStat: vm.running ? `读 ${readRate} 写 ${writeRate}` : '',
        railTip: `${disk.title}\n${disk.path}`,
        readRate,
        writeRate,
        readIops: disk.readIops ?? 0,
        writeIops: disk.writeIops ?? 0,
        readPts,
        writePts,
        diskFormatState: ext,
        diskTypeState,
        parentPath: disk.parentPath || '',
        pathTitle: '路径',
        pathText: disk.path || '---',
        pathCopy: disk.path || ''
      };
    }

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
        rightState: '差异',
        rightCopy: '',
        title: diffName,
        fileSizeText,
        capacityText,
        ratio: 4.2 / 128,
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
        diskTypeState: '差异',
        parentPath,
        pathTitle: '路径',
        pathText: diffPath,
        pathCopy: diffPath
      };
    }

    const forceFixed = mode === 'fixed';
    const rawType = forceFixed ? '固定大小' : disk.vhdFormat || disk.subtitle || '动态扩展';
    const isFixed = forceFixed || rawType.includes('固定');
    const isDiff = rawType.includes('差异') || rawType.includes('差分');
    const diskTypeState = isFixed ? '固定大小' : isDiff ? '差异' : '动态扩展';
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
      ratio,
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

  function renderTitleFirstKvItemHtml({ title, stateText, caution = false, wide = false, copyVal = '', revealVal = '' }) {
    const itemCls = wide ? 'dt-kv-item is-wide' : 'dt-kv-item';
    const stateCls = `${caution ? 'dt-kv-state is-caution' : 'dt-kv-state'}${wide ? ' tc-mid-fit' : ''}`;
    const copyBtn = copyVal
      ? `<button class="dt-kv-head-copy" data-copy="${copyVal}" title="点击复制：${copyVal}" type="button"><span class="fi fi-fluent" style="font-size:14px;">&#xF32C;</span></button>`
      : '';
    const revealBtn = revealVal
      ? `<button class="dt-kv-head-copy" data-reveal="${revealVal}" title="打开所在文件夹：${revealVal}" type="button"><span class="fi fi-fluent" style="font-size:14px;">&#xF42F;</span></button>`
      : '';
    const headActions =
      copyBtn || revealBtn ? `<span class="dt-kv-head-actions">${copyBtn}${revealBtn}</span>` : '';
    return `
      <div class="${itemCls}">
        <div class="dt-kv-head">
          <span class="dt-kv-title">${title}</span>
          ${headActions}
        </div>
        <span class="${stateCls}" title="${stateText}">${stateText}</span>
      </div>
    `;
  }

  const DISK_CAP_EDGE_CLASS = {
    'header-edge': '',
    'header-edge-6': 'is-edge-6',
    'header-edge-8': 'is-edge-8'
  };

  function getDiskCapacityMeta(info) {
    if (!info || info.isIso || info.isPhysical) return { show: false };
    const ratioNum = Number.isFinite(info.ratio) ? Math.max(0, Math.min(1, info.ratio)) : 0;
    const ratioPct = ratioNum * 100;
    const fillStyle =
      ratioNum <= 0 ? 'width:0%;' : `width:${ratioPct.toFixed(1)}%;min-width:6px;`;
    const pctLabel = info.pctText || `${ratioPct.toFixed(1)}%`;
    const tooltip = `文件占用 ${info.fileSizeText} / 虚拟容量 ${info.capacityText} (${pctLabel})`;
    return { show: true, ratioPct, fillStyle, pctLabel, tooltip };
  }

  function buildDiskCanvasHtml(disk, idx, vm, chartHeight = 136) {
    const info = resolveDiskViewModel(disk, idx, vm);
    const active = vm.running && !info.isStale;
    const diffTopRight = info.isDifferencing && state.diffParentMode === 'top-right';
    const parentFileName = info.parentPath ? info.parentPath.split('\\').pop() : '';

    const kvItems = [
      renderTitleFirstKvItemHtml({
        title: '介质',
        stateText: info.sourceState,
        caution: info.isStale
      })
    ];

    if (diffTopRight) {
      kvItems.push(
        renderTitleFirstKvItemHtml({
          title: '父盘（差异）',
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
          revealVal: info.parentPath,
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
        revealVal: !info.isPhysical && info.pathCopy ? info.pathCopy : '',
        wide: true
      })
    );

    const cap = getDiskCapacityMeta(info);
    const capMode = state.diskCapLayout || 'header-edge-6';
    let capacityBarHtml = '';
    if (cap.show && !(capMode in DISK_CAP_EDGE_CLASS)) {
      if (capMode === 'bar-only') {
        capacityBarHtml = `
          <div class="storage-capacity-meter is-slim" style="margin:2px 0 0 0;" title="${cap.tooltip}">
            <div class="storage-capacity-fill" style="${cap.fillStyle}"></div>
          </div>
        `;
      } else {
        capacityBarHtml = `
          <div class="storage-cap-inline" title="${cap.tooltip}">
            <div class="storage-capacity-meter is-slim">
              <div class="storage-capacity-fill" style="${cap.fillStyle}"></div>
            </div>
            <span class="storage-cap-pct">${cap.pctLabel}</span>
          </div>
        `;
      }
    }

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
      ${capacityBarHtml}
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
    const isRunning = Boolean(vm && vm.running);
    if (serverAdapter.isLive) {
      if (!net) {
        return {
          isConnected: false,
          switchName: '',
          switchType: '—',
          macAddress: '---',
          macMode: '—',
          ipv4List: [],
          ipv6List: [],
          vlanText: '未启用',
          sendRate: '0 bps',
          recvRate: '0 bps',
          sendPts: [0, 0],
          recvPts: [0, 0],
          activeChart: false,
          railSub: '未连接',
          railStat: '',
          headerTitle: '未连接',
          headerReading: ''
        };
      }
      const isConn = Boolean(net.isConnected);
      const sName = isConn ? net.subtitle || '虚拟交换机' : '';
      const macAddr = net.mac ? formatMacAddress(net.mac) : '---';
      const macMod = net.isStaticMac ? '静态' : '动态';
      const rawIps = Array.isArray(net.ips) ? net.ips.filter(Boolean) : [];
      const ipv4s = isRunning ? rawIps.filter((x) => !x.includes(':')) : [];
      if (isRunning && !ipv4s.length && net.ipv4 && net.ipv4 !== '---') ipv4s.push(net.ipv4);
      const ipv6s = isRunning ? rawIps.filter((x) => x.includes(':')) : [];
      if (isRunning && !ipv6s.length && net.ipv6) ipv6s.push(net.ipv6);
      const sRate = isRunning ? net.sendRate || net.txRate || '0 bps' : '0 bps';
      const rRate = isRunning ? net.recvRate || net.rxRate || '0 bps' : '0 bps';
      const sPts = Array.isArray(net.sendPts) && net.sendPts.length >= 2 ? net.sendPts.slice(-7) : [0, 0];
      const rPts = Array.isArray(net.recvPts) && net.recvPts.length >= 2 ? net.recvPts.slice(-7) : [0, 0];
      const act = isRunning && isConn;
      const hMode = state.netHeaderMode || 'switch-ip';
      const hTitle = hMode === 'adapter-switch' ? net.title || '网络适配器' : isConn ? sName : '未连接';
      const hReading = hMode === 'adapter-switch' ? (isConn ? sName : '未连接') : '';
      return {
        isConnected: isConn,
        switchName: sName,
        switchType: isConn ? net.switchType || '虚拟交换机' : '—',
        macAddress: macAddr,
        macMode: macMod,
        ipv4List: ipv4s,
        ipv6List: ipv6s,
        vlanText: net.vlanText || '未启用',
        sendRate: sRate,
        recvRate: rRate,
        sendPts: sPts,
        recvPts: rPts,
        activeChart: act,
        railSub: isConn ? sName : '未连接',
        railStat: act ? `发 ${sRate} 收 ${rRate}` : '',
        headerTitle: hTitle,
        headerReading: hReading
      };
    }

    const mode = state.netPreviewMode || 'connected';
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
      // 方案 A（推荐，2上1下同磁盘节奏）：顶部标题已是 Default Switch / 未连接
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
    let reading = t.cpuPctText;
    let cfgTab = 'processor';
    let cfgDev = '';
    let cfgLabel = '处理器';
    let bodyHtml = buildCpuCanvasHtml(vm, t, 84);

    const chartH = state.singleChartHeight || 136;

    let headCapEdgeHtml = '';
    let headCapEdgeCls = '';

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
      const cap = getDiskCapacityMeta(dInfo);
      const capMode = state.diskCapLayout || 'header-edge-6';
      if (cap.show && capMode in DISK_CAP_EDGE_CLASS) {
        headCapEdgeCls = DISK_CAP_EDGE_CLASS[capMode];
        headCapEdgeHtml = `
          <div class="tc-head-cap-edge" title="${cap.tooltip}">
            <div class="tc-head-cap-fill" style="${cap.fillStyle}"></div>
          </div>
        `;
      }
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
        <div class="tc-canvas-head${headCapEdgeHtml ? ` has-cap-edge${headCapEdgeCls ? ` ${headCapEdgeCls}` : ''}` : ''}">
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
          ${headCapEdgeHtml}
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
        title: 'CPU',
        sub: '',
        stat: `${t.cpuPctText} ${vm.cores} 核心`,
        pts: t.cpuHist,
        color: '#3898EC',
        fill: 'rgba(56, 152, 236, 0.22)',
        tip: `CPU (${vm.cores} 核)`
      },
      {
        kind: 'mem',
        idx: 0,
        title: '内存',
        sub: '',
        stat: `${t.memPct}% ${t.usedMemText}/${t.totalMemText} GB`,
        pts: t.memHist,
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
          <div class="tmw-thumb">${renderSparkThumbSvg(r.pts, vm.running && (r.kind !== 'net' || resolveNetViewModel(vm.adapters[r.idx] || null, vm).isConnected), strokeOf(r.color), fillOf(r.fill))}</div>
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

  function bindInlinePathActions(scope) {
    if (!scope) return;
    scope.querySelectorAll('[data-copy]').forEach((btn) => {
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
    scope.querySelectorAll('[data-reveal]').forEach((btn) => {
      if (btn.dataset.revealBound) return;
      btn.dataset.revealBound = 'true';
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const val = btn.getAttribute('data-reveal') || '';
        if (!val) return;
        showNotice(`已在资源管理器定位：${val}`);
      });
    });
  }

  function bindDesktopAltEvents(altHost) {
    bindInlinePathActions(altHost);
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

    const origTelemetry = getOverviewTelemetry(vm, gpuAssigns);

    if (cpuVal) {
      cpuVal.innerHTML = vm.running
        ? `${origTelemetry.cpuPctNum.toFixed(1)}<span class="unit"> %</span>`
        : `0.0<span class="unit"> %</span>`;
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
        const pct = origTelemetry.memPct;
        if (memChartHost) {
          memChartHost.innerHTML = createUsageChartHtml(
            '内存使用趋势',
            vm.running,
            `${pct}%`,
            origTelemetry.memHist,
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
        const liveGpu = resolveLiveGpuTelemetry(gpuAssigns[0], 0);
        const bodyHtml = !vm.running
          ? `<div style="opacity:0.6;font-size:13px;margin:0 4px;">虚拟机未启动</div>`
          : `
            <div class="gpu-charts-grid">
              ${createUsageChartHtml('3D', true, `${liveGpu.pct}%`, liveGpu.pts3d, true, 148)}
              ${createUsageChartHtml('数据复制', true, `${liveGpu.ptsCopy.at(-1) ?? 0}%`, liveGpu.ptsCopy, true, 148)}
              ${createUsageChartHtml('视频编码', true, `${liveGpu.ptsEnc.at(-1) ?? 0}%`, liveGpu.ptsEnc, true, 148)}
              ${createUsageChartHtml('视频解码', true, `${liveGpu.ptsDec.at(-1) ?? 0}%`, liveGpu.ptsDec, true, 148)}
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
      if (!activeDisk) {
        storagePanel.innerHTML = `<div style="opacity:0.6;font-size:13px;margin:0 4px;">暂无存储设备</div>`;
      } else {
        const dView = resolveDiskViewModel(activeDisk, state.selectedDiskIdx, vm);
        const [fileNum = dView.fileSizeText, fileUnit = ''] = String(dView.fileSizeText || '---').split(' ');
        const [capNum = dView.capacityText, capUnit = ''] = String(dView.capacityText || '---').split(' ');
        storagePanel.innerHTML = `
          <div class="metric-selectors-grid">${diskCards}</div>
          <div style="display:flex;flex-direction:column;gap:12px;">
            <div>
              <div class="storage-capacity-line">
                <div><span class="cap-label">文件占用</span><span class="cap-num">${fileNum}</span> <span>${fileUnit}</span></div>
                <div><span class="cap-label">虚拟容量</span><span class="cap-num">${capNum}</span> <span>${capUnit}</span></div>
              </div>
              <div class="storage-capacity-meter">
                <div class="storage-capacity-fill" style="width:${((dView.ratio || 0) * 100).toFixed(1)}%;"></div>
              </div>
            </div>
            <div class="transfer-charts-grid">
              ${createUsageChartHtml('读取', vm.running, dView.readRate, dView.readPts, false, 132)}
              ${createUsageChartHtml('写入', vm.running && !dView.isIso, dView.writeRate, dView.writePts, false, 132)}
            </div>
          </div>
        `;
      }
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
          const nv = resolveNetViewModel(n, vm);
          const secReading = nv.railStat;
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
      if (!activeNet) {
        netPanel.innerHTML = `<div style="opacity:0.6;font-size:13px;margin:0 4px;">暂无网络适配器</div>`;
      } else {
        const netInfo = resolveNetViewModel(activeNet, vm);
        const firstIpv4 = netInfo.ipv4List[0] || '';
        const firstIpv6 = netInfo.ipv6List[0] || '';
        const ipv4Html =
          vm.running && firstIpv4
            ? `<button class="network-copy-btn" data-copy="${firstIpv4}"><span>${firstIpv4}</span><span class="fi" style="font-size:12px;opacity:0.5;">&#xE8C8;</span></button>`
            : `<span style="font-size:12px;opacity:0.8;">未获取</span>`;
        const ipv6Html =
          vm.running && firstIpv6
            ? `<button class="network-copy-btn" data-copy="${firstIpv6}"><span>${firstIpv6}</span><span class="fi" style="font-size:12px;opacity:0.5;">&#xE8C8;</span></button>`
            : `<span style="font-size:12px;opacity:0.8;">未获取</span>`;

        netPanel.innerHTML = `
          <div class="metric-selectors-grid">${netCards}</div>
          <div class="network-addresses-stack">
            <div class="network-addresses-pair">
              <div class="network-addr-group">
                <span class="network-addr-title">MAC</span>
                <button class="network-copy-btn" data-copy="${netInfo.macAddress}">
                  <span>${netInfo.macAddress}</span>
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
            ${createUsageChartHtml('发送', netInfo.activeChart, netInfo.sendRate, netInfo.sendPts, false, 132)}
            ${createUsageChartHtml('接收', netInfo.activeChart, netInfo.recvRate, netInfo.recvPts, false, 132)}
          </div>
        `;
      }
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
      ? `<button class="setting-info-btn" type="button" data-info-desc="${desc.replace(/"/g, '&quot;')}" title="${desc.replace(/"/g, '&quot;')}"><span class="fi" style="font-size:15px;">&#xE946;</span></button>`
      : '';
    return `
      <div class="setting-label-grid">
        <span class="setting-label-title">${title}</span>
        ${infoBtn}
      </div>
    `;
  }

  // 5. Render Settings Area (处理器 / 内存 / 设备[显卡、存储、网卡、PCIe] / 快照 / 高级[引导、安全、控制台、电源])
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
        const isOn = sw.classList.contains('on');
        if (sw.hasAttribute('aria-checked')) sw.setAttribute('aria-checked', String(isOn));
        const settingKey = sw.getAttribute('data-setting-key');
        if (settingKey && settingKey in state.advSettings) {
          state.advSettings[settingKey] = isOn;
          const curVm = getVm();
          if (curVm && !curVm._empty) {
            if (!state.advSettingsByVm[curVm.id]) {
              state.advSettingsByVm[curVm.id] = structuredClone(state.advSettings);
            } else {
              state.advSettingsByVm[curVm.id][settingKey] = isOn;
            }
          }
          if (
            settingKey === 'consoleClipboard' ||
            settingKey === 'consoleAudioOutput' ||
            settingKey === 'consoleAudioInput'
          ) {
            const syncedAdv = structuredClone(state.advSettings);
            if (curVm && !curVm._empty) {
              emitConsoleSync(curVm.id, syncedAdv);
            }
            if (serverAdapter.isLive && curVm && !curVm._empty) {
              serverAdapter
                .saveConsoleSetting(curVm.id, 'enhancedFeatures', {
                  clipboard: syncedAdv.consoleClipboard !== false,
                  audioOutput: syncedAdv.consoleAudioOutput !== false,
                  audioInput: syncedAdv.consoleAudioInput !== false
                })
                .then(() => showNotice('已保存至宿主机，将在下次连接或重连增强会话时生效'))
                .catch((err) => {
                  state.advSettings[settingKey] = !isOn;
                  if (state.advSettingsByVm[curVm.id]) {
                    state.advSettingsByVm[curVm.id][settingKey] = !isOn;
                  }
                  emitConsoleSync(curVm.id, state.advSettings);
                  renderActiveWorkspace();
                  showNotice(err?.message || '保存设置失败', 'error');
                });
            } else {
              showNotice('已应用，将在下次连接或重连增强会话时生效');
            }
          } else if (serverAdapter.isLive && curVm && !curVm._empty) {
            let req = null;
            if (settingKey === 'hibernation' || settingKey === 'turnOffOnRestart' || settingKey === 'batteryPassthrough') {
              req = serverAdapter.savePowerSetting(curVm.id, settingKey, isOn);
            } else if (settingKey === 'consoleSupport' || settingKey === 'lockOnDisconnect') {
              req = serverAdapter.saveConsoleSetting(curVm.id, settingKey, isOn);
            } else if (settingKey === 'numLock') {
              req = serverAdapter.saveCompatSetting(curVm.id, settingKey, isOn);
            }
            if (req) {
              req
                .then(() => showNotice('已保存至宿主机'))
                .catch((err) => {
                  state.advSettings[settingKey] = !isOn;
                  renderActiveWorkspace();
                  showNotice(err?.message || '保存设置失败', 'error');
                });
            } else {
              showNotice('已应用');
            }
          } else {
            showNotice('已应用');
          }
        }
        const procFieldId = sw.getAttribute('data-proc-field-id');
        if (procFieldId) {
          const procField = getProcessorField(procFieldId);
          if (procField) procField.value = isOn;
        }
        const lbl = sw.querySelector('.switch-state-label');
        if (lbl) {
          const onTxt = sw.getAttribute('data-on-text') || '启用';
          const offTxt = sw.getAttribute('data-off-text') || '关闭';
          lbl.textContent = isOn ? onTxt : offTxt;
        }
      });
    });

    bindInlinePathActions(host);

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

  function runLazyHostRender(host, token, cachedMod, loader, renderFn, bindAfter = false) {
    if (cachedMod) {
      renderFn(cachedMod);
      if (bindAfter) bindSettingsHostControls(host);
      return;
    }
    host.innerHTML = '';
    loader().then((mod) => {
      if (token !== settingsRenderToken) return;
      renderFn(mod);
      if (bindAfter) bindSettingsHostControls(host);
    });
  }

  function renderSettingsArea() {
    const settingsArea = document.getElementById('vmSettingsArea');
    const subnav = document.getElementById('vmSettingsSubnav');
    const host = document.getElementById('vmSettingsHost');
    if (!settingsArea || !subnav || !host) return;
    const renderToken = ++settingsRenderToken;
    setPageTabsTrailingAction('', null);

    const key = state.activeTabKey;
    const hasSubnav = key === 'processor' || key === 'memory' || key === 'devices' || key === 'advanced';
    settingsArea.classList.toggle('no-subnav', !hasSubnav);
    settingsArea.classList.toggle('is-checkpoints-mode', key === 'checkpoints');
    subnav.hidden = !hasSubnav;

    const viewRenderers = {
      gpu: renderGpuTab,
      storage: renderStorageTab,
      network: renderNetworkTab,
      serial: renderSerialTab,
      vtpm: renderVtpmTab,
      pcie: renderPcieTab,
      power: renderPowerTab,
      security: renderSecurityTab,
      boot: renderBootTab,
      console: renderConsoleTab,
      compat: renderCompatTab
    };

    if (key === 'processor') {
      renderSubnavItems(subnav, PROCESSOR_SECTIONS, state.processorSectionIdx, (id) => {
        state.processorSectionIdx = Number(id);
        host.scrollTop = 0;
        renderSettingsArea();
      });
      if (computeMod?.isComputeSectionDirty(state, getVm())) {
        const compBtn = subnav.querySelector('[data-subnav-id="0"]');
        if (compBtn && !compBtn.querySelector('.subnav-dirty-dot')) {
          const dot = document.createElement('span');
          dot.className = 'subnav-dirty-dot';
          compBtn.appendChild(dot);
        }
      }
      renderProcessorSection(host, PROCESSOR_SECTIONS[state.processorSectionIdx], renderToken);
    } else if (key === 'memory') {
      renderSubnavItems(subnav, MEMORY_SECTIONS, state.memorySectionIdx, (id) => {
        state.memorySectionIdx = Number(id);
        host.scrollTop = 0;
        renderSettingsArea();
      });
      renderMemorySection(host, MEMORY_SECTIONS[state.memorySectionIdx]);
    } else if (key === 'checkpoints') {
      renderCheckpointsTab(host, renderToken);
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
      if (renderFn) renderFn(host, renderToken);
    }

    bindSettingsHostControls(host);
  }

  // Tab 1: 处理器 (VmProcessorSettingsView.cs + VmCpuAffinitySettingsView.cs)
  function renderProcessorSection(host, sec, renderToken = settingsRenderToken) {
    if (sec === PROCESSOR_SECTIONS[0]) {
      const vm = getVm();
      runLazyHostRender(
        host,
        renderToken,
        computeMod,
        loadComputeMod,
        (mod) => {
          mod.renderComputeSection(host, {
            state,
            vm,
            processorFields: sec.fields,
            onApplied: (saved, targetVm, changedKeys) => {
              if (serverAdapter.isLive && vm && !vm._empty) {
                const dirtyProps = Array.isArray(changedKeys) && changedKeys.length > 0
                  ? changedKeys
                  : ['Count', 'Reservation', 'Limit', 'RelativeWeight'];
                return serverAdapter
                  .saveProcessorSettings(vm.id, saved, dirtyProps)
                  .then(() => showNotice('处理器配置已保存至宿主机'))
                  .catch((err) => showNotice(err?.message || '保存处理器配置失败', 'error'));
              }
              vm.configText = String(vm.configText || '').replace(/^\d+\s*核/, `${saved.Count} 核`);
              renderVmList();
              renderDashboardSummary();
            },
            showNotice,
            onLayoutNeeded: layoutDynamicGrids
          });
        },
        true
      );
      return;
    }

    // Section 2: 核心绑定 (VmCpuAffinitySettingsView.cs lines 16-295)
    if (sec.kind === 'affinity') {
      const curVm = getVm();
      if (!state.savedAffinityMask) state.savedAffinityMask = new Set(state.affinityMask);
      const liveAffProcs = serverAdapter.isLive ? serverAdapter.state.liveHostSnapshot?.affinityProcs : null;
      const totalCores =
        Array.isArray(liveAffProcs) && liveAffProcs.length > 0
          ? liveAffProcs.length
          : Math.max(1, state.hostLogicalCores || 12);
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

      const coreMeta =
        Array.isArray(liveAffProcs) && liveAffProcs.length > 0
          ? liveAffProcs.map((p, idx) => ({
              index: Number.isFinite(p.index) ? p.index : idx,
              type: String(p.coreType || '').toLowerCase() === 'efficient' ? 'eff' : 'perf'
            }))
          : Array.from({ length: totalCores }, (_, i) => ({
              index: i,
              type: totalCores >= 8 ? (i < splitIdx ? 'perf' : 'eff') : 'perf'
            }));
      const perfIndices = coreMeta.filter((c) => c.type === 'perf').map((c) => c.index);
      const effIndices = coreMeta.filter((c) => c.type === 'eff').map((c) => c.index);
      const hasHybridCores = perfIndices.length > 0 && effIndices.length > 0;

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
          perfIndices.length > 0 &&
          state.affinityMask.size === perfIndices.length &&
          perfIndices.every((i) => state.affinityMask.has(i));
        const isEffOnly =
          effIndices.length > 0 &&
          state.affinityMask.size === effIndices.length &&
          effIndices.every((i) => state.affinityMask.has(i));
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
          if (mode === 'all') state.affinityMask = new Set(coreMeta.map((c) => c.index));
          else if (mode === 'clear') state.affinityMask = new Set();
          else if (mode === 'perf') state.affinityMask = new Set(perfIndices);
          else if (mode === 'eff') state.affinityMask = new Set(effIndices);
          syncAffinityDom();
        });
      });

      host.querySelector('#affinityRevertBtn')?.addEventListener('click', () => {
        state.affinityMask = new Set(state.savedAffinityMask);
        syncAffinityDom();
      });
      host.querySelector('#affinityApplyBtn')?.addEventListener('click', () => {
        const prevMask = new Set(state.savedAffinityMask);
        const nextArr = Array.from(state.affinityMask).sort((a, b) => a - b);
        state.savedAffinityMask = new Set(state.affinityMask);
        syncAffinityDom();
        if (serverAdapter.isLive && curVm && !curVm._empty) {
          serverAdapter
            .saveCpuAffinity(curVm.id, nextArr)
            .then(() => showNotice('核心绑定已保存至宿主机'))
            .catch((err) => {
              state.savedAffinityMask = prevMask;
              state.affinityMask = new Set(prevMask);
              syncAffinityDom();
              showNotice(err?.message || '保存核心绑定失败', 'error');
            });
          return;
        }
        showNotice(state.uiLang === 'en' ? 'Applied' : '已应用');
      });
      syncAffinityDom();
      return;
    }

    // Section 3: 指令集能力 (Candidates A / B / C / Original)
    if (sec === PROCESSOR_SECTIONS[2] && state.isaVariant !== 'original-5cards') {
      const vm = getVm();
      const hostCpuModel =
        state.hostCpuModel ||
        PROCESSOR_SECTIONS.find((s) => s.kind === 'affinity')?.cpuModel ||
        'Snapdragon(R) X 12-core X1E80100 @ 3.40 GHz';
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
        `${t.minLabel}：只保留基础指令，可在不同型号的 CPU 间迁移。${t.clusterLabel}：保留集群各节点共有的指令。`
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
        const changedProps = [];
        const vals = {};
        if (d.nested !== s.nested) {
          changedProps.push('ExposeVirtualizationExtensions');
          vals.ExposeVirtualizationExtensions = Boolean(d.nested);
        }
        if (d.legacyOs !== s.legacyOs) {
          changedProps.push('CompatibilityForOlderOperatingSystemsEnabled');
          vals.CompatibilityForOlderOperatingSystemsEnabled = Boolean(d.legacyOs);
        }
        if (d.migrationEnabled !== s.migrationEnabled) {
          changedProps.push('CompatibilityForMigrationEnabled');
          vals.CompatibilityForMigrationEnabled = Boolean(d.migrationEnabled);
        }
        if (d.migrationMode !== s.migrationMode) {
          changedProps.push('CompatibilityForMigrationMode');
          vals.CompatibilityForMigrationMode = d.migrationMode || 'MinimumFeatureSet';
        }
        if (d.cpuBrand !== s.cpuBrand) {
          changedProps.push('CpuBrandString');
          vals.CpuBrandString = d.cpuBrand || '';
        }
        if (serverAdapter.isLive && vm && !vm._empty && changedProps.length > 0) {
          serverAdapter
            .saveProcessorSettings(vm.id, vals, changedProps)
            .then(() => {
              store.saved = { ...store.draft };
              renderProcessorSection(host, sec);
              bindSettingsHostControls(host);
              showNotice('指令集配置已保存至宿主机');
            })
            .catch((err) => showNotice(err?.message || '保存指令集配置失败', 'error'));
          return;
        }
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
          const curVm = getVm();
          const changedKeys = [];
          const vals = {};
          sec.fields.forEach((f) => {
            if (f.value !== f.savedValue) {
              changedKeys.push(f.id);
              if (f.kind === 'toggle') vals[f.id] = Boolean(f.value);
              else if (f.kind === 'stepper') vals[f.id] = f.value === '' || f.value == null ? null : Number(f.value);
              else vals[f.id] = f.value === '' ? null : f.value;
            }
          });
          if (serverAdapter.isLive && curVm && !curVm._empty && changedKeys.length > 0) {
            serverAdapter
              .saveProcessorSettings(curVm.id, vals, changedKeys)
              .then(() => {
                sec.fields.forEach((f) => { f.savedValue = f.value; });
                renderProcessorSection(host, sec);
                bindSettingsHostControls(host);
                showNotice('处理器配置已保存至宿主机');
              })
              .catch((err) => showNotice(err?.message || '保存处理器配置失败', 'error'));
            return;
          }
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
        const curVm = getVm();
        const changedKeys = [];
        const vals = {};
        sec.fields.forEach((f) => {
          if (f.value !== f.savedValue) {
            changedKeys.push(f.id);
            if (f.kind === 'toggle') vals[f.id] = Boolean(f.value);
            else if (f.kind === 'stepper') vals[f.id] = f.value === '' || f.value == null ? null : Number(f.value);
            else vals[f.id] = f.value === '' ? null : f.value;
          }
        });
        if (serverAdapter.isLive && curVm && !curVm._empty && changedKeys.length > 0) {
          serverAdapter
            .saveProcessorSettings(curVm.id, vals, changedKeys)
            .then(() => {
              sec.fields.forEach((f) => { f.savedValue = f.value; });
              renderProcessorSection(host, sec);
              bindSettingsHostControls(host);
              showNotice('频率调节已保存至宿主机');
            })
            .catch((err) => showNotice(err?.message || '保存频率设置失败', 'error'));
          return;
        }
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
      const curVm = getVm();
      const changedKeys = [];
      const vals = {};
      sec.fields.forEach((f) => {
        if (f.value !== f.savedValue) {
          changedKeys.push(f.id);
          if (f.kind === 'toggle') vals[f.id] = Boolean(f.value);
          else if (f.kind === 'stepper') vals[f.id] = f.value === '' || f.value == null ? null : Number(f.value);
          else vals[f.id] = f.value === '' ? null : f.value;
        }
      });
      if (serverAdapter.isLive && curVm && !curVm._empty && changedKeys.length > 0) {
        serverAdapter
          .saveProcessorSettings(curVm.id, vals, changedKeys)
          .then(() => {
            sec.fields.forEach((f) => { f.savedValue = f.value; });
            syncSecActionBtns();
            showNotice('处理器配置已保存至宿主机');
          })
          .catch((err) => showNotice(err?.message || '保存处理器配置失败', 'error'));
        return;
      }
      sec.fields.forEach((f) => {
        f.savedValue = f.value;
      });
      syncSecActionBtns();
      showNotice('已应用');
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
          return;
        }
        sw.classList.toggle('on', field.value);
        syncSecActionBtns();
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
      });
      card.querySelectorAll('[data-step-dir]').forEach((btn) => {
        btn.addEventListener('click', () => {
          const dir = Number(btn.getAttribute('data-step-dir'));
          const cur = parseInt(input.value || '0', 10) || 0;
          const next = Math.max(field.min ?? 0, cur + dir * (field.step ?? 1));
          input.value = String(next);
          field.value = String(next);
          syncSecActionBtns();
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
    });
    host.querySelector('#memGroupApplyBtn')?.addEventListener('click', () => {
      const curVm = getVm();
      const changedKeys = [];
      const vals = {};
      sec.fields.forEach((f) => {
        if (f.value !== f.savedValue) {
          changedKeys.push(f.id);
          if (f.kind === 'toggle') vals[f.id] = Boolean(f.value);
          else if (f.kind === 'text') vals[f.id] = f.value === '' || f.value == null ? 0 : Number(f.value);
          else if (f.kind === 'choice') vals[f.id] = /^\d+$/.test(String(f.value)) ? Number(f.value) : f.value;
          else vals[f.id] = String(f.value || '');
        }
      });
      if (serverAdapter.isLive && curVm && !curVm._empty && changedKeys.length > 0) {
        serverAdapter
          .saveMemorySettings(curVm.id, vals, changedKeys)
          .then(() => {
            sec.fields.forEach((f) => { f.savedValue = f.value; });
            host.querySelectorAll('.energy-card.is-dirty').forEach((el) => el.classList.remove('is-dirty'));
            syncSecActionBtns();
            showNotice('内存配置已保存至宿主机');
          })
          .catch((err) => showNotice(err?.message || '保存内存配置失败', 'error'));
        return;
      }
      sec.fields.forEach((f) => {
        f.savedValue = f.value;
      });
      host.querySelectorAll('.energy-card.is-dirty').forEach((el) => el.classList.remove('is-dirty'));
      syncSecActionBtns();
      showNotice('已应用');
    });
  }

  // Tab 3: 存储 (Mirrors GPU-PV Workbench Style & VmStorageSettingsView Capabilities)
  function renderStorageTab(host, renderToken = settingsRenderToken) {
    const isLive = serverAdapter.isLive;
    const liveHost = isLive ? serverAdapter.state.liveHostSnapshot : null;
    runLazyHostRender(host, renderToken, devicesMod, loadDevicesMod, (mod) => {
      mod.renderStorageWorkbench(host, {
        state,
        vm: getVm(),
        isLive,
        HOST_PHYSICAL_DISKS: isLive ? liveHost?.disks || [] : HOST_PHYSICAL_DISKS,
        HOST_OPTICAL_DRIVES: isLive ? liveHost?.opticalDrives || [] : HOST_OPTICAL_DRIVES,
        INITIAL_STORAGE_ITEMS: isLive ? [] : INITIAL_STORAGE_ITEMS,
        renderTitleFirstKvItemHtml,
        renderSettingLabelHtml,
        renderDashboardSummary,
        showNotice,
        onLayoutNeeded: layoutDynamicGrids,
        bindHostControls: bindSettingsHostControls,
        setPageTabsTrailingAction,
        onVmGenerationChange: (gen) => {
          const curVm = getVm();
          if (!curVm) return;
          curVm.generation = gen === 1 ? 1 : 2;
          curVm.gen = curVm.generation;
          renderVmList();
          renderDashboardSummary();
          syncStandaloneHostRailPanel();
          notifyParentHostState();
        }
      });
    });
  }

  // Tab 4: 显卡 (GPU-PV Layout Candidates)
  function setPageTabsTrailingAction(html, onClick) {
    const bar = document.getElementById('vmPageTabsBar');
    if (!bar) return;
    bar.querySelector('#vmPageTabsTrailing')?.remove();
    if (!html) return;
    const slot = document.createElement('div');
    slot.id = 'vmPageTabsTrailing';
    slot.className = 'vm-pagetabs-trailing';
    slot.style.gap = '8px';
    slot.innerHTML = html;
    bar.appendChild(slot);

    if (onClick) {
      const btn = slot.querySelector('button');
      btn?.addEventListener('click', (e) => {
        e.stopPropagation();
        onClick();
      });
    }
  }

  function renderGpuTab(host, renderToken = settingsRenderToken) {
    const isLive = serverAdapter.isLive;
    const liveHost = isLive ? serverAdapter.state.liveHostSnapshot : null;
    runLazyHostRender(host, renderToken, devicesMod, loadDevicesMod, (mod) => {
      mod.renderGpuWorkbench(host, {
        state,
        vm: getVm(),
        isLive,
        HOST_GPUS: isLive ? liveHost?.gpus || [] : HOST_GPUS,
        INITIAL_GPU_PARTITIONS: isLive ? [] : INITIAL_GPU_PARTITIONS,
        INITIAL_GPU_ASSIGNMENTS: isLive ? [] : INITIAL_GPU_ASSIGNMENTS,
        renderTitleFirstKvItemHtml,
        renderSettingLabelHtml,
        renderDashboardSummary,
        renderVmList,
        showNotice,
        onLayoutNeeded: layoutDynamicGrids,
        bindHostControls: bindSettingsHostControls,
        setPageTabsTrailingAction,
        notifyParentGpuState
      });
    });
  }

  // Tab 5: 网络 (VmNetworkSettingsView.cs — 3 Redesign Candidates + Legacy Reference)
  function renderNetworkTab(host, renderToken = settingsRenderToken) {
    const isLive = serverAdapter.isLive;
    const liveHost = isLive ? serverAdapter.state.liveHostSnapshot : null;
    runLazyHostRender(host, renderToken, devicesMod, loadDevicesMod, (mod) => {
      mod.renderNetworkWorkbench(host, {
        state,
        vm: getVm(),
        isLive,
        AVAILABLE_VSWITCHES: isLive ? liveHost?.switches || [] : undefined,
        renderTitleFirstKvItemHtml,
        renderSettingLabelHtml,
        renderDashboardSummary,
        showNotice,
        onLayoutNeeded: layoutDynamicGrids,
        bindHostControls: bindSettingsHostControls,
        setPageTabsTrailingAction,
        notifyParentNicState: () => {
          syncStandaloneHostRailPanel();
          notifyParentHostState();
        }
      });
    });
  }

  // 高级 -> 引导顺序 (纯启动设备排序，单击设为首选 + 按住拖拽微调)
  function renderBootTab(host) {
    const flipReorder = (mutateFn) => {
      const before = new Map();
      host.querySelectorAll('[data-boot-id]').forEach((el) => {
        const r = el.getBoundingClientRect();
        before.set(el.dataset.bootId, { x: r.left, y: r.top });
      });

      mutateFn();
      renderBootTab(host);

      host.querySelectorAll('[data-boot-id]').forEach((el) => {
        const old = before.get(el.dataset.bootId);
        if (!old) return;
        const r = el.getBoundingClientRect();
        const dx = old.x - r.left;
        const dy = old.y - r.top;
        if (Math.abs(dx) > 0.5 || Math.abs(dy) > 0.5) {
          el.style.transition = 'none';
          el.style.transform = `translate(${dx}px, ${dy}px)`;
          el.getBoundingClientRect();
          el.style.transition = 'transform 280ms cubic-bezier(0.2, 0.85, 0.25, 1)';
          el.style.transform = '';
        }
      });
    };

    const vm = getVm();
    const persistBootOrder = (prevEntries) => {
      if (!serverAdapter.isLive || !vm || vm._empty) {
        showNotice('已应用');
        return;
      }
      serverAdapter
        .saveVmBootOrder(vm.id, state.bootEntries)
        .then(() => showNotice('启动顺序已保存'))
        .catch((err) => {
          state.bootEntries = prevEntries;
          renderBootTab(host);
          showNotice(err?.message || '保存启动顺序失败', 'error');
        });
    };

    if (!Array.isArray(state.bootEntries) || state.bootEntries.length === 0) {
      host.innerHTML = `
        <div class="boot-cascade-list">
          <div style="opacity:0.6;font-size:13px;padding:14px 16px;">暂无可配置启动设备</div>
        </div>
      `;
      return;
    }

    const itemsHtml = state.bootEntries
      .map((b, idx) => {
        const isFirst = idx === 0;

        return `
          <div class="boot-cascade-row" data-boot-row="${idx}">
            <div class="boot-order-item${isFirst ? ' is-primary' : ''}" data-boot-id="${b.id}" data-boot-idx="${idx}">
              <span class="fi boot-order-icon">${b.glyph}</span>
              <div class="boot-order-text">
                <span class="boot-order-name">${b.name}</span>
                <span class="boot-order-desc">${b.description}</span>
              </div>
            </div>
          </div>
        `;
      })
      .join('');

    host.innerHTML = `
      <div class="boot-cascade-list">
        <div class="boot-order-group">${itemsHtml}</div>
      </div>
    `;

    host.querySelectorAll('[data-boot-idx]').forEach((card) => {
      const startIdx = Number(card.getAttribute('data-boot-idx'));
      card.addEventListener('pointerdown', (e) => {
        if (e.button !== 0) return;

        const startY = e.clientY;
        let dragging = false;
        let currentIdx = startIdx;
        const rows = Array.from(host.querySelectorAll('[data-boot-row]'));
        const rects = rows.map((r) => r.getBoundingClientRect());
        const stride = rects.length > 1 ? rects[1].top - rects[0].top : rects[0].height + 10;

        const onMove = (ev) => {
          const dy = ev.clientY - startY;
          if (!dragging && Math.abs(dy) < 5) return;

          if (!dragging) {
            dragging = true;
            card.setPointerCapture(e.pointerId);
            card.classList.add('is-dragging');
            card.style.transition = 'none';
          }

          card.style.transform = `translateY(${dy}px) scale(1.012)`;
          const targetIdx = Math.max(0, Math.min(state.bootEntries.length - 1, startIdx + Math.round(dy / stride)));

          if (targetIdx !== currentIdx) {
            currentIdx = targetIdx;
            rows.forEach((row, i) => {
              if (i === startIdx) return;
              let shift = 0;
              if (startIdx < currentIdx && i > startIdx && i <= currentIdx) shift = -stride;
              else if (startIdx > currentIdx && i >= currentIdx && i < startIdx) shift = stride;
              const inner = row.querySelector('[data-boot-id]');
              if (inner) {
                inner.style.transition = 'transform 200ms cubic-bezier(0.2, 0.85, 0.25, 1)';
                inner.style.transform = shift ? `translateY(${shift}px)` : '';
              }
            });
          }
        };

        const onUp = () => {
          window.removeEventListener('pointermove', onMove);
          window.removeEventListener('pointerup', onUp);
          window.removeEventListener('pointercancel', onUp);

          if (!dragging) {
            if (startIdx > 0) {
              const prevEntries = structuredClone(state.bootEntries);
              flipReorder(() => {
                const [moved] = state.bootEntries.splice(startIdx, 1);
                state.bootEntries.unshift(moved);
              });
              persistBootOrder(prevEntries);
            }
            return;
          }

          card.classList.remove('is-dragging');
          if (currentIdx !== startIdx) {
            const prevEntries = structuredClone(state.bootEntries);
            flipReorder(() => {
              const [moved] = state.bootEntries.splice(startIdx, 1);
              state.bootEntries.splice(currentIdx, 0, moved);
            });
            persistBootOrder(prevEntries);
          } else {
            card.style.transition = 'transform 180ms cubic-bezier(0.2, 0.85, 0.25, 1)';
            card.style.transform = '';
            rows.forEach((row, i) => {
              if (i !== startIdx) {
                const inner = row.querySelector('[data-boot-id]');
                if (inner) inner.style.transform = '';
              }
            });
          }
        };

        window.addEventListener('pointermove', onMove);
        window.addEventListener('pointerup', onUp);
        window.addEventListener('pointercancel', onUp);
      });
    });
  }

  // 设备 -> vTPM (虚拟可信平台模块 + 密钥保护器)
  function renderVtpmTab(host, renderToken = settingsRenderToken) {
    runLazyHostRender(host, renderToken, devicesMod, loadDevicesMod, (mod) => {
      mod.renderVtpmWorkbench(host, {
        state,
        vm: getVm(),
        INITIAL_VTPM_BY_VM,
        renderTitleFirstKvItemHtml,
        renderSettingLabelHtml,
        showNotice,
        onLayoutNeeded: layoutDynamicGrids,
        bindHostControls: bindSettingsHostControls,
        setPageTabsTrailingAction
      });
    });
  }

  // 控制台 (VmConsoleSupportService + VmDisplaySettingsService)
  function renderConsoleTab(host) {
    if (host._consoleHostClickHandler) {
      host.removeEventListener('click', host._consoleHostClickHandler);
      host._consoleHostClickHandler = null;
    }

    const vm = getVm();
    const isGen1 = vm?.generation === 1 || vm?.gen === 1;
    const consoleLockedByShield = !isGen1 && Boolean(state.advSettings.shielding);
    const consoleOn = !consoleLockedByShield && Boolean(state.advSettings.consoleSupport);
    const enhancedBlockedReason = isGen1
      ? '第 1 代虚拟机仅支持基本会话，无法配置增强会话资源'
      : consoleLockedByShield
        ? '已在「安全」中开启受防护模式并锁定关闭，需先关闭受防护模式'
        : !consoleOn
          ? '已关闭「启用控制台」，开启控制台后才可使用增强会话'
          : '';
    const enhancedLocked = Boolean(enhancedBlockedReason);
    const consoleSubTab = state.consoleSubTab === 'enhanced' ? 'enhanced' : 'basic';
    const resPresets = [
      '默认',
      '3840 x 2160',
      '2560 x 1440',
      '1920 x 1200',
      '1920 x 1080',
      '1600 x 900',
      '1366 x 768',
      '1280 x 1024',
      '1280 x 720',
      '1024 x 768',
      '800 x 600'
    ];
    const resItems = resPresets
      .map(
        (r) =>
          `<button type="button" class="winui-combo-item ${state.advSettings.resolution === r ? 'is-selected' : ''}" data-console-res-preset="${escapeHtmlText(r)}" role="option" aria-selected="${state.advSettings.resolution === r}"><span>${escapeHtmlText(r)}</span></button>`
      )
      .join('');

    const basicBodyHtml = `
      <div class="isa-grid-2x2">
        <div class="isa-card uniform-1row span-2${consoleLockedByShield ? ' is-disabled' : ''}">
          <div class="isa-card-row">
            ${renderSettingLabelHtml(
              '启用控制台',
              consoleLockedByShield
                ? '已在「安全」中开启受防护模式并锁定关闭，需先关闭受防护模式才能启用'
                : '仅第 2 代虚拟机有效。关闭后会移除虚拟显示与键鼠，无法点击“连接”打开画面，适合纯 RDP、SSH 或显卡直通串流场景'
            )}
            <div class="winui-switch ${consoleOn ? 'on' : ''}${consoleLockedByShield ? ' disabled is-locked' : ''}" data-setting-key="consoleSupport"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
        </div>
        <div class="isa-card uniform-1row span-2">
          <div class="isa-card-row">
            ${renderSettingLabelHtml('断开时锁屏', '关闭控制台连接窗口时，自动锁定虚拟机内的系统')}
            <div class="winui-switch ${state.advSettings.lockOnDisconnect ? 'on' : ''}" data-setting-key="lockOnDisconnect"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
        </div>
        <div class="isa-card uniform-1row span-2">
          <div class="isa-card-row">
            ${renderSettingLabelHtml('默认分辨率', '控制台使用基本会话时的默认窗口分辨率，需在关机状态下修改')}
            <div style="display:flex;align-items:center;gap:8px;flex-shrink:0;">
              <div class="winui-combo" id="consoleResCombo">
                <div class="console-res-field">
                  <input
                    type="text"
                    class="winui-textbox console-res-input"
                    id="consoleResInput"
                    value="${escapeHtmlText(state.advSettings.resolution || '默认')}"
                    placeholder="默认 或 宽 x 高"
                    autocomplete="off"
                  />
                  <button
                    type="button"
                    class="winui-btn console-res-preset-btn"
                    id="consoleResPresetBtn"
                    title="选择常用分辨率"
                    aria-haspopup="listbox"
                    aria-expanded="false"
                  >
                    <span class="fi winui-combo-chevron" style="font-size:11px;">&#xE70D;</span>
                  </button>
                </div>
                <div class="winui-combo-flyout" id="consoleResFlyout" role="listbox" hidden>${resItems}</div>
              </div>
              <button class="winui-btn accent" id="consoleResApplyBtn" type="button">应用</button>
            </div>
          </div>
        </div>
      </div>
    `;

    const enhancedNoticeHtml = enhancedBlockedReason
      ? `
        <div class="winui-floating-banner is-inline is-warning" role="status" style="margin-bottom:10px;">
          <span class="winui-floating-banner-badge is-warning"><span class="fi">&#xF13C;</span></span>
          <div class="winui-floating-banner-text">${escapeHtmlText(enhancedBlockedReason)}</div>
        </div>
      `
      : '';

    const enhancedBodyHtml = `
      ${enhancedNoticeHtml}
      <div class="isa-grid-2x2">
        <div class="isa-card uniform-1row span-2${enhancedLocked ? ' is-disabled' : ''}">
          <div class="isa-card-row">
            ${renderSettingLabelHtml(
              '增强会话剪贴板共享',
              enhancedBlockedReason || '允许在增强会话中与虚拟机同步剪贴板内容，修改将在下次连接或重连增强会话时生效'
            )}
            <div class="winui-switch ${!enhancedLocked && state.advSettings.consoleClipboard !== false ? 'on' : ''}${enhancedLocked ? ' disabled is-locked' : ''}" data-setting-key="consoleClipboard"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
        </div>
        <div class="isa-card uniform-1row span-2${enhancedLocked ? ' is-disabled' : ''}">
          <div class="isa-card-row">
            ${renderSettingLabelHtml(
              '增强会话声音输出',
              enhancedBlockedReason || '允许在增强会话中接收虚拟机播放的系统声音，修改将在下次连接或重连增强会话时生效'
            )}
            <div class="winui-switch ${!enhancedLocked && state.advSettings.consoleAudioOutput !== false ? 'on' : ''}${enhancedLocked ? ' disabled is-locked' : ''}" data-setting-key="consoleAudioOutput"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
        </div>
        <div class="isa-card uniform-1row span-2${enhancedLocked ? ' is-disabled' : ''}">
          <div class="isa-card-row">
            ${renderSettingLabelHtml(
              '增强会话麦克风输入',
              enhancedBlockedReason || '允许在增强会话中向虚拟机传入本机麦克风，工具栏默认仍为静音直到手动开启，修改将在下次连接或重连时生效'
            )}
            <div class="winui-switch ${!enhancedLocked && state.advSettings.consoleAudioInput !== false ? 'on' : ''}${enhancedLocked ? ' disabled is-locked' : ''}" data-setting-key="consoleAudioInput"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
          </div>
        </div>
      </div>
    `;

    host.innerHTML = `
      <div style="padding:4px 24px 24px 24px;">
        <div class="st-pivot-track" role="tablist" style="margin-bottom:12px;">
          <button
            class="st-pivot-btn ${consoleSubTab === 'basic' ? 'active' : ''}"
            data-console-subtab="basic"
            id="consoleSubBasicBtn"
            type="button"
            role="tab"
            aria-selected="${consoleSubTab === 'basic'}"
          >
            <span class="fi" style="font-size:13.5px;">&#xE7F4;</span>
            <span>基本会话</span>
          </button>
          <button
            class="st-pivot-btn ${consoleSubTab === 'enhanced' ? 'active' : ''}"
            data-console-subtab="enhanced"
            id="consoleSubEnhancedBtn"
            type="button"
            role="tab"
            aria-selected="${consoleSubTab === 'enhanced'}"
          >
            <span class="fi" style="font-size:13.5px;">&#xE839;</span>
            <span>增强会话</span>
          </button>
        </div>
        ${consoleSubTab === 'enhanced' ? enhancedBodyHtml : basicBodyHtml}
      </div>
    `;

    host.querySelectorAll('[data-console-subtab]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const nextSub = btn.getAttribute('data-console-subtab') === 'enhanced' ? 'enhanced' : 'basic';
        if (state.consoleSubTab === nextSub) return;
        state.consoleSubTab = nextSub;
        host.scrollTop = 0;
        renderSettingsArea();
      });
    });

    if (consoleSubTab !== 'basic') return;

    const resCombo = host.querySelector('#consoleResCombo');
    const resInput = host.querySelector('#consoleResInput');
    const resPresetBtn = host.querySelector('#consoleResPresetBtn');
    const resFlyout = host.querySelector('#consoleResFlyout');

    const closeResFlyout = () => {
      if (!resFlyout || !resCombo || !resPresetBtn) return;
      resFlyout.hidden = true;
      resCombo.classList.remove('is-open');
      resPresetBtn.setAttribute('aria-expanded', 'false');
    };

    const syncResPresetSelection = (val) => {
      resFlyout?.querySelectorAll('[data-console-res-preset]').forEach((item) => {
        const selected = item.getAttribute('data-console-res-preset') === val;
        item.classList.toggle('is-selected', selected);
        item.setAttribute('aria-selected', String(selected));
      });
    };

    const applyConsoleResolution = () => {
      if (!resInput) return;
      const raw = resInput.value.trim();
      if (!raw) {
        showNotice('请输入有效分辨率，如 1280 x 720。', 'error');
        return;
      }
      if (raw === '默认') {
        if (serverAdapter.isLive && vm && !vm._empty) {
          serverAdapter
            .saveConsoleSetting(vm.id, 'resolution', '默认')
            .then(() => {
              state.advSettings.resolution = '默认';
              resInput.value = '默认';
              syncResPresetSelection('默认');
              closeResFlyout();
              showNotice('控制台分辨率已保存至宿主机');
            })
            .catch((err) => showNotice(err?.message || '保存控制台分辨率失败', 'error'));
          return;
        }
        state.advSettings.resolution = '默认';
        resInput.value = '默认';
        syncResPresetSelection('默认');
        closeResFlyout();
        showNotice('已应用');
        return;
      }
      const parts = raw.split(/[xX×*,\s]+/).filter(Boolean);
      const width = parts.length === 2 && /^\d+$/.test(parts[0]) ? Number(parts[0]) : NaN;
      const height = parts.length === 2 && /^\d+$/.test(parts[1]) ? Number(parts[1]) : NaN;
      if (!Number.isInteger(width) || !Number.isInteger(height)) {
        showNotice('请输入有效分辨率，如 1280 x 720。', 'error');
        return;
      }
      if (width < 200 || width > 7680 || height < 200 || height > 4320) {
        showNotice('分辨率超出有效范围（宽 200 至 7680，高 200 至 4320）。', 'error');
        return;
      }
      const normalized = `${width & ~1} x ${height & ~1}`;
      if (serverAdapter.isLive && vm && !vm._empty) {
        serverAdapter
          .saveConsoleSetting(vm.id, 'resolution', normalized)
          .then(() => {
            state.advSettings.resolution = normalized;
            resInput.value = normalized;
            syncResPresetSelection(normalized);
            closeResFlyout();
            showNotice('控制台分辨率已保存至宿主机');
          })
          .catch((err) => showNotice(err?.message || '保存控制台分辨率失败', 'error'));
        return;
      }
      state.advSettings.resolution = normalized;
      resInput.value = normalized;
      syncResPresetSelection(normalized);
      closeResFlyout();
      showNotice('已应用');
    };

    resPresetBtn?.addEventListener('click', (e) => {
      e.stopPropagation();
      if (!resFlyout || !resCombo || !resPresetBtn) return;
      const nextOpen = resFlyout.hidden;
      if (nextOpen) {
        syncResPresetSelection(resInput?.value.trim() || '');
        const btnRect = resPresetBtn.getBoundingClientRect();
        const hostRect = host.getBoundingClientRect();
        resFlyout.classList.toggle('is-up', hostRect.bottom - btnRect.bottom < 220);
        resFlyout.hidden = false;
        resCombo.classList.add('is-open');
        resPresetBtn.setAttribute('aria-expanded', 'true');
      } else {
        closeResFlyout();
      }
    });

    resFlyout?.querySelectorAll('[data-console-res-preset]').forEach((item) => {
      item.addEventListener('click', (e) => {
        e.stopPropagation();
        const val = item.getAttribute('data-console-res-preset') || '默认';
        if (resInput) {
          resInput.value = val;
          resInput.focus();
        }
        syncResPresetSelection(val);
        closeResFlyout();
      });
    });

    host._consoleHostClickHandler = (e) => {
      if (resFlyout && !resFlyout.hidden && !resCombo?.contains(e.target)) {
        closeResFlyout();
      }
    };
    host.addEventListener('click', host._consoleHostClickHandler);

    resInput?.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        applyConsoleResolution();
      } else if (e.key === 'Escape') {
        closeResFlyout();
      }
    });

    host.querySelector('#consoleResApplyBtn')?.addEventListener('click', applyConsoleResolution);
  }

  // 电源 (VmPowerService)
  function renderPowerTab(host) {
    host.innerHTML = `
      <div style="padding:4px 24px 24px 24px;">
        <div class="isa-grid-2x2">
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('允许休眠', '向虚拟机开放 ACPI 休眠（S4）能力，允许来宾系统启用休眠或 Windows 快速启动')}
              <div class="winui-switch ${state.advSettings.hibernation ? 'on' : ''}" data-setting-key="hibernation"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
            </div>
          </div>
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('重启时关机', '当虚拟机内部请求重启时，改为直接关机而不是重新启动')}
              <div class="winui-switch ${state.advSettings.turnOffOnRestart ? 'on' : ''}" data-setting-key="turnOffOnRestart"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
            </div>
          </div>
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('电池透传', '将宿主电池电量与充电状态同步给虚拟机')}
              <div class="winui-switch ${state.advSettings.batteryPassthrough ? 'on' : ''}" data-setting-key="batteryPassthrough"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
            </div>
          </div>
        </div>
      </div>
    `;
  }

  // 串口 (VmSerialPortSettingsView.cs — 对齐设备工作台风格)
  function renderSerialTab(host, renderToken = settingsRenderToken) {
    runLazyHostRender(host, renderToken, devicesMod, loadDevicesMod, (mod) => {
      mod.renderSerialWorkbench(host, {
        state,
        vm: getVm(),
        INITIAL_SERIAL_PORTS,
        renderTitleFirstKvItemHtml,
        renderSettingLabelHtml,
        showNotice,
        onLayoutNeeded: layoutDynamicGrids,
        bindHostControls: bindSettingsHostControls,
        setPageTabsTrailingAction
      });
    });
  }

  // 高级 -> 安全 (安全启动、状态与迁移加密、受防护模式，全部来自 VmSecurityService)
  function renderSecurityTab(host) {
    const vm = getVm();
    const isGen1 = vm?.generation === 1 || vm?.gen === 1;
    const isRunning = Boolean(vm?.running);
    const adv = state.advSettings;
    const canEditShield = !isGen1 && !isRunning;
    const canEditEnc = canEditShield && !adv.shielding;
    const sbLocked = !canEditEnc;
    const sbValue = !isGen1 && adv.secureBoot ? adv.secureBootTemplate : 'off';
    const encOn = !isGen1 && Boolean(adv.encryptState);
    const shieldOn = !isGen1 && Boolean(adv.shielding);

    const sbChoices = [
      { value: 'off', label: '关闭' },
      { value: 'Microsoft Windows', label: 'Windows' },
      { value: 'Microsoft UEFI 证书颁发机构', label: 'UEFI / CA' },
      { value: '受防护的开源 VM', label: '受防护的开源 VM' }
    ];
    const sbPillsHtml = sbChoices
      .map(
        (c) =>
          `<button type="button" class="isa-pill-btn${sbValue === c.value ? ' active' : ''}" data-security-sb-val="${c.value}" ${sbLocked ? 'disabled' : ''}>${c.label}</button>`
      )
      .join('');

    const sbDesc = isGen1
      ? '仅第 2 代虚拟机支持安全启动'
      : isRunning
        ? '虚拟机运行中，关闭后才可修改'
        : adv.shielding
          ? '已由受防护模式锁定，需先关闭受防护模式才能更改'
          : '决定开机时允许启动哪些系统：Windows 选 Windows，Linux 或第三方签名系统选 UEFI / CA，PE、macOS 或未签名系统选关闭';
    const encDesc = isGen1
      ? '仅第 2 代虚拟机支持状态与迁移加密'
      : isRunning
        ? '虚拟机运行中，关闭后才可修改'
        : adv.shielding
          ? '已由受防护模式锁定开启'
          : '加密保存状态、快照内存文件以及跨宿主实时迁移数据，开启时会自动启用 vTPM 并签发宿主密钥';
    const shieldDesc = isGen1
      ? '仅第 2 代虚拟机支持受防护模式'
      : isRunning
        ? '虚拟机运行中，关闭后才可修改'
        : '企业级隔离模式，会强制锁定安全启动、vTPM 与状态加密，并禁用控制台画面与增强会话，仅能通过 RDP 或 SSH 远程连接，个人单机通常无需开启';

    host.innerHTML = `
      <div style="padding:4px 24px 24px 24px;">
        <div class="settings-section-title" style="margin-top:0;">
          ${renderSettingLabelHtml('安全启动', sbDesc)}
        </div>
        <div class="isa-pill-track cols-4">
          ${sbPillsHtml}
        </div>
        <div class="isa-grid-2x2" style="margin-top:14px;">
          <div class="isa-card uniform-1row${canEditEnc ? '' : ' is-disabled'}">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('状态与迁移加密', encDesc)}
              <div class="winui-switch ${encOn ? 'on' : ''}${canEditEnc ? '' : ' disabled is-locked'}" data-security-key="encryptState" data-custom-switch="true"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
            </div>
          </div>
          <div class="isa-card uniform-1row${canEditShield ? '' : ' is-disabled'}">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('受防护模式', shieldDesc)}
              <div class="winui-switch ${shieldOn ? 'on' : ''}${canEditShield ? '' : ' disabled is-locked'}" data-security-key="shielding" data-custom-switch="true"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
            </div>
          </div>
        </div>
      </div>
    `;

    host.querySelectorAll('[data-security-sb-val]').forEach((btn) => {
      btn.addEventListener('click', async () => {
        if (sbLocked) return;
        const val = btn.getAttribute('data-security-sb-val');
        const prevSb = adv.secureBoot;
        const prevTpl = adv.secureBootTemplate;
        if (val === 'off') {
          adv.secureBoot = false;
        } else {
          adv.secureBoot = true;
          adv.secureBootTemplate = val;
        }
        renderSecurityTab(host);
        bindSettingsHostControls(host);
        if (serverAdapter.isLive && vm && !vm._empty) {
          try {
            if (val === 'off') {
              await serverAdapter.saveSecuritySetting(vm.id, 'secureBoot', false);
            } else {
              if (!prevSb) await serverAdapter.saveSecuritySetting(vm.id, 'secureBoot', true);
              await serverAdapter.saveSecuritySetting(vm.id, 'secureBootTemplate', val);
            }
            showNotice('安全启动设置已保存至宿主机');
          } catch (err) {
            adv.secureBoot = prevSb;
            adv.secureBootTemplate = prevTpl;
            renderSecurityTab(host);
            bindSettingsHostControls(host);
            showNotice(err?.message || '保存安全启动设置失败', 'error');
          }
          return;
        }
        showNotice('已应用');
      });
    });

    host.querySelectorAll('[data-security-key]').forEach((sw) => {
      sw.addEventListener('click', async () => {
        const key = sw.dataset.securityKey;
        if (key === 'encryptState') {
          if (!canEditEnc) return;
          const next = !adv.encryptState;
          if (serverAdapter.isLive && vm && !vm._empty) {
            try {
              await serverAdapter.saveSecuritySetting(vm.id, 'encryptState', next);
              renderSecurityTab(host);
              bindSettingsHostControls(host);
              showNotice('加密设置已保存至宿主机');
            } catch (err) {
              showNotice(err?.message || '保存加密设置失败', 'error');
            }
            return;
          }
          adv.encryptState = next;
          if (adv.encryptState) {
            adv.tpmEnabled = true;
            const mod = devicesMod || (await loadDevicesMod());
            mod.ensureVtpmHostKeyForVm(state, vm, INITIAL_VTPM_BY_VM);
          }
        } else if (key === 'shielding') {
          if (!canEditShield) return;
          const next = !adv.shielding;
          if (serverAdapter.isLive && vm && !vm._empty) {
            try {
              await serverAdapter.saveSecuritySetting(vm.id, 'shielding', next);
              renderSecurityTab(host);
              bindSettingsHostControls(host);
              showNotice('受防护模式已保存至宿主机');
            } catch (err) {
              showNotice(err?.message || '保存受防护模式失败', 'error');
            }
            return;
          }
          adv.shielding = next;
          if (adv.shielding) {
            adv.secureBoot = true;
            adv.tpmEnabled = true;
            const mod = devicesMod || (await loadDevicesMod());
            mod.ensureVtpmHostKeyForVm(state, vm, INITIAL_VTPM_BY_VM);
            adv.encryptState = true;
            adv.consoleSupport = false;
          }
        }
        if (state.navTarget === 'vm' && state.activeTabKey === 'advanced' && state.advancedSubKey === 'security') {
          renderSecurityTab(host);
          bindSettingsHostControls(host);
          showNotice('已应用');
        }
      });
    });
  }

  // 高级 -> 其他 (PCIe 设备引导 + 完整 SCSI 命令集)
  function renderCompatTab(host) {
    const vm = getVm();
    const isGen1 = vm?.generation === 1 || vm?.gen === 1;
    const isRunning = Boolean(vm?.running);
    const adv = state.advSettings;
    const pcieCfg = ((state.pcieSettingsByVm ??= {})[vm.id] ??= { emulationEnabled: true, topology: '0', bootVpci: false });
    const canEditPcieBoot = !isGen1 && !isRunning;
    const bootVpciOn = !isGen1 && Boolean(pcieCfg.bootVpci);

    const bootVpciDesc = isGen1
      ? '仅第 2 代虚拟机支持通过 UEFI 加载 vPCI 驱动引导'
      : isRunning
        ? '虚拟机运行中，关闭后才可修改'
        : '在 UEFI 启动阶段加载 vPCI 驱动，使虚拟机能够直接从直通的 NVMe 等受支持 PCIe 设备引导系统';

    host.innerHTML = `
      <div style="padding:4px 24px 24px 24px;">
        <div class="isa-grid-2x2">
          <div class="isa-card uniform-1row span-2${canEditPcieBoot ? '' : ' is-disabled'}">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('PCIe 设备引导', bootVpciDesc)}
              <div class="winui-switch ${bootVpciOn ? 'on' : ''}${canEditPcieBoot ? '' : ' disabled is-locked'}" data-compat-key="bootVpci" data-custom-switch="true"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
            </div>
          </div>
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('启用完整 SCSI 命令集', '仅对物理直通硬盘生效，关闭命令过滤后可供虚拟机读取硬盘 SMART 信息或使用专业存储管理工具。使用普通 VHDX 虚拟磁盘时无效，平时建议关闭')}
              <div class="winui-switch ${adv.fullScsi ? 'on' : ''}" data-compat-key="fullScsi" data-custom-switch="true"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
            </div>
          </div>
          <div class="isa-card uniform-1row span-2">
            <div class="isa-card-row">
              ${renderSettingLabelHtml('开机数字小键盘', '开机时默认开启 NumLock，避免连接控制台时同步关闭宿主小键盘。第 1 代与第 2 代均支持（关机状态可修改）')}
              <div class="winui-switch ${adv.numLock ? 'on' : ''}" data-setting-key="numLock"><div class="winui-switch-track"><div class="winui-switch-thumb"></div></div></div>
            </div>
          </div>
        </div>
      </div>
    `;

    host.querySelectorAll('[data-compat-key]').forEach((sw) => {
      sw.addEventListener('click', () => {
        const key = sw.dataset.compatKey;
        let nextVal = false;
        if (key === 'bootVpci') {
          if (!canEditPcieBoot) return;
          pcieCfg.bootVpci = !pcieCfg.bootVpci;
          nextVal = pcieCfg.bootVpci;
        } else if (key === 'fullScsi') {
          adv.fullScsi = !adv.fullScsi;
          nextVal = adv.fullScsi;
        }
        renderCompatTab(host);
        bindSettingsHostControls(host);
        if (serverAdapter.isLive && vm && !vm._empty) {
          serverAdapter
            .saveCompatSetting(vm.id, key, nextVal)
            .then(() => showNotice('兼容设置已保存至宿主机'))
            .catch((err) => {
              if (key === 'bootVpci') pcieCfg.bootVpci = !nextVal;
              else if (key === 'fullScsi') adv.fullScsi = !nextVal;
              renderCompatTab(host);
              bindSettingsHostControls(host);
              showNotice(err?.message || '保存兼容设置失败', 'error');
            });
          return;
        }
        showNotice('已应用');
      });
    });
  }

  // Tab 9: PCIe (对齐存储物理磁盘直通交互：单机列表 + 全局设备池)
  function renderPcieTab(host, renderToken = settingsRenderToken) {
    const isLive = serverAdapter.isLive;
    const liveHost = isLive ? serverAdapter.state.liveHostSnapshot : null;
    runLazyHostRender(host, renderToken, devicesMod, loadDevicesMod, (mod) => {
      mod.renderPcieWorkbench(host, {
        state,
        vm: getVm(),
        vmItems: activeVms,
        isLive,
        HOST_PCIE_DEVICES: isLive ? liveHost?.pcieDevices || [] : HOST_PCIE_DEVICES,
        renderTitleFirstKvItemHtml,
        renderSettingLabelHtml,
        showNotice,
        onLayoutNeeded: layoutDynamicGrids,
        bindHostControls: bindSettingsHostControls,
        setPageTabsTrailingAction
      });
    });
  }

  function syncDesignCandidatesPanel() {
    const panel = document.getElementById('designCandidatesPanel');
    const dyn = document.getElementById('dcpDynamicSections');
    const titleEl = document.getElementById('dcpTitle');
    if (!panel || !dyn) return;
    const isEmbedded = window.self !== window.top;
    if (isEmbedded) {
      panel.hidden = true;
      return;
    }
    const isCheckpointTab = state.navTarget === 'vm' && state.activeTabKey === 'checkpoints';
    panel.hidden = !isCheckpointTab;
    if (panel.hidden) {
      dyn.innerHTML = '';
      return;
    }
    panel.querySelectorAll(':scope > .dcp-subheader, :scope > .dcp-list').forEach((el) => {
      const scope = el.getAttribute('data-dcp-scope');
      el.hidden = Boolean(scope && scope !== 'checkpoints');
    });
    if (titleEl) titleEl.textContent = '快照视觉方案';
    dyn.innerHTML = `
      <div class="dcp-footer">
        <button class="dcp-action-btn" type="button" id="dcpCpResetDemo">重置演示快照</button>
      </div>
    `;
    const resetBtn = document.getElementById('dcpCpResetDemo');
    if (resetBtn) {
      resetBtn.addEventListener('click', () => {
        state.checkpointNodes = structuredClone(CHECKPOINT_NODES);
        state.checkpointSelectedId = 'node-current';
        state.checkpointConfirmAction = null;
        state.checkpointRenaming = false;
        state.checkpointSearch = '';
        state.checkpointFilter = 'all';
        state.checkpointZoom = 1;
        const host = document.getElementById('vmSettingsHost');
        if (host && state.activeTabKey === 'checkpoints') renderCheckpointsTab(host);
      });
    }
    syncStandaloneHostRailPanel();
  }

  function renderCheckpointsTab(host, renderToken = settingsRenderToken) {
    runLazyHostRender(
      host,
      renderToken,
      checkpointMod,
      loadCheckpointMod,
      (mod) => mod.renderCheckpointWorkbench(host, { state, initialNodes: CHECKPOINT_NODES }),
      true
    );
  }

  // Switch between OverviewScroll and SettingsArea, and sync bottom workbench bar
  function renderActiveWorkspace() {
    if ((state.activeTabKey !== 'checkpoints' || state.navTarget !== 'vm') && state.checkpointFullscreen) {
      state.checkpointFullscreen = false;
      winEl.classList.remove('is-cp-fullscreen');
    }
    syncDesignCandidatesPanel();
    setPageTabsTrailingAction('', null);
    const overviewScroll = document.getElementById('vmOverviewScroll');
    const settingsArea = document.getElementById('vmSettingsArea');
    const isOverview = state.activeTabKey === 'overview';
    if (overviewScroll && settingsArea) {
      overviewScroll.hidden = !isOverview;
      settingsArea.hidden = isOverview;
    }
    if (isOverview) {
      renderOverview();
    } else {
      renderSettingsArea();
    }
    requestAnimationFrame(layoutDynamicGrids);

    const tabObj = PAGE_TABS.find((t) => t.key === state.activeTabKey);
    const srcTag = document.getElementById('wbSourceFileTag');
    if (srcTag) {
      if (state.navTarget === 'host') srcTag.textContent = 'UI/Pages/HostPage.xaml';
      else if (state.navTarget === 'remote') srcTag.textContent = 'ExHyperV.WoW/RemoteConnection';
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
        (state.navTarget === 'remote' && target === 'nav:remote') ||
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
      minimalTitle.textContent = target === 'host' ? '宿主' : target === 'remote' ? '远程' : target === 'settings' ? '应用设置' : '虚拟机';
    }
    const navView = document.getElementById('winuiNavView');
    const backdrop = document.getElementById('navDrawerBackdrop');
    if (navView) navView.classList.remove('minimal-open');
    if (backdrop) backdrop.hidden = true;
    const pageVm = document.getElementById('pageVirtualMachines');
    const pageHost = document.getElementById('pageHost');
    const pageRemote = document.getElementById('pageRemote');
    const pageSettings = document.getElementById('pageSettings');
    if (pageVm) pageVm.style.display = target === 'vm' ? 'grid' : 'none';
    if (pageHost) {
      pageHost.hidden = target !== 'host';
      pageHost.style.display = target === 'host' ? 'grid' : 'none';
    }
    if (pageRemote) {
      pageRemote.hidden = target !== 'remote';
      if (target === 'remote') renderRemotePage(pageRemote, serverAdapter);
    }
    if (pageSettings) {
      pageSettings.hidden = target !== 'settings';
      if (target === 'settings') renderAppSettingsPage();
    }
    if (target !== 'host') {
      document.getElementById('winuiWindow')?.classList.remove('is-host-topo-fullscreen');
    }
    if (target === 'host') {
      if (hostMod) {
        hostMod.renderHostPage();
      } else {
        loadHostMod().then((mod) => {
          if (state.navTarget === 'host') mod.renderHostPage();
        });
      }
    }
    syncTelemetryTarget();
    renderActiveWorkspace();
  }

  async function executePowerCommand(vm, actionName) {
    if (!vm || vm._empty) return;
    if (serverAdapter.isLive) {
      try {
        await serverAdapter.executeVmPower(vm.id, actionName);
        showNotice(`已提交电源指令：${vm.name}`);
      } catch (err) {
        showNotice(err?.message || '电源操作失败', 'error');
      }
      return;
    }
    if (actionName === 'Start') vm.running = true;
    else if (actionName === 'Stop' || actionName === 'TurnOff' || actionName === 'Save' || actionName === 'Suspend') vm.running = false;
    else vm.running = true;
    syncStandaloneHostRailPanel();
    notifyParentHostState();
    renderVmList();
    renderDashboardSummary();
    renderActiveWorkspace();
  }

  function toggleVmPower() {
    const vm = getVm();
    if (!vm || vm._empty) return;
    const nextAction = !vm.running
      ? 'Start'
      : String(state.defaultPowerAction || 'Shutdown').toLowerCase() === 'save'
        ? 'Save'
        : 'Stop';
    void executePowerCommand(vm, nextAction);
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
  document.getElementById('vmConnectBtn')?.addEventListener('click', async () => {
    const vm = getVm();
    if (!vm || vm._empty) return;
    let liveConn = null;
    if (serverAdapter.isLive) {
      if (!state.advSettingsByVm[vm.id]) {
        await serverAdapter.loadVmDetails(vm.id);
        applyVmDetailToState(vm.id);
      }
      try {
        liveConn = { serverUrl: serverAdapter.client.baseUrl };
      } catch (err) {
        showNotice(err?.message || '申请控制台访问票据失败', 'error');
        return;
      }
    }
    const wowClient = window.__WOW_APP_CLIENT__;
    if (wowClient?.isEmbedded && typeof wowClient.emitAppEvent === 'function') {
      wowClient.emitAppEvent('exhyperv:open-console', {
        id: vm.id,
        name: vm.name,
        running: Boolean(vm.running),
        osIcon: vm.osIcon || 'Windows.svg',
        generation: vm.generation ?? vm.gen ?? 2,
        consoleSupport: Boolean(state.advSettings.consoleSupport),
        shielding: Boolean(state.advSettings.shielding),
        consoleClipboard: state.advSettings.consoleClipboard !== false,
        consoleAudioOutput: state.advSettings.consoleAudioOutput !== false,
        consoleAudioInput: state.advSettings.consoleAudioInput !== false,
        accentHex: state.accentHex,
        serverMode: liveConn ? 'live' : 'demo',
        serverUrl: liveConn?.serverUrl || '',
        mode: preferredConsoleMode(),
        wsTicket: '',
        availableVms: activeVms.map((item) => ({
          id: item.id,
          name: item.name,
          running: Boolean(item.running),
          osIcon: item.osIcon || 'Windows.svg',
          generation: item.generation ?? item.gen ?? 2
        }))
      });
      return;
    }
    loadConsoleCtrl().then((ctrl) => ctrl.open());
  });
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
      const vm = getVm();
      if (!vm || vm._empty) return;
      const mapAct = { restart: 'Restart', pause: 'Suspend', save: 'Save', turnoff: 'TurnOff', shutdown: 'Stop' };
      const resolved = mapAct[act];
      if (resolved) {
        void executePowerCommand(vm, resolved);
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

  document.getElementById('vmNoticeCloseBtn')?.addEventListener('click', () => {
    const banner = document.getElementById('vmNoticeBanner');
    if (banner) banner.hidden = true;
  });

  document.getElementById('vmNoticeDetailsBtn')?.addEventListener('click', () => {
    const modal = document.getElementById('vmNoticeDetailsModal');
    const bodyEl = document.getElementById('vmNoticeDetailsText');
    const copyBtn = document.getElementById('vmNoticeCopyAllBtn');
    if (!modal || !bodyEl || !lastNoticeFullText) return;
    bodyEl.textContent = lastNoticeFullText;
    if (copyBtn) copyBtn.textContent = '全部复制';
    modal.hidden = false;
  });

  const closeNoticeModal = () => {
    const modal = document.getElementById('vmNoticeDetailsModal');
    if (modal) modal.hidden = true;
  };
  document.getElementById('vmNoticeModalCloseBtn')?.addEventListener('click', closeNoticeModal);
  document.getElementById('vmNoticeDetailsModal')?.addEventListener('click', (e) => {
    if (e.target.id === 'vmNoticeDetailsModal') closeNoticeModal();
  });
  document.getElementById('vmNoticeCopyAllBtn')?.addEventListener('click', async () => {
    if (!lastNoticeFullText) return;
    try {
      await navigator.clipboard?.writeText(lastNoticeFullText);
    } catch {}
    const copyBtn = document.getElementById('vmNoticeCopyAllBtn');
    if (copyBtn) {
      copyBtn.textContent = '已复制';
      if (noticeCopyResetTimer) clearTimeout(noticeCopyResetTimer);
      noticeCopyResetTimer = setTimeout(() => {
        copyBtn.textContent = '全部复制';
      }, 1600);
    }
  });

  // SettingsPage.xaml top-tabs + single-column cards
  const APP_SETTINGS_SUBNAV = [
    { key: 'appearance', title: '外观', glyph: '&#xE790;' },
    { key: 'startup', title: '系统', glyph: '&#xE770;' },
    { key: 'behavior', title: '偏好', glyph: '&#xE9E9;' },
    { key: 'extensions', title: '扩展', glyph: '&#xE74C;' },
    { key: 'diagnostics', title: '日志', glyph: '&#xE9D9;' },
    { key: 'about', title: '关于', glyph: '&#xE946;' }
  ];

  const APP_SETTINGS_SWATCHES = [
    ['蓝色', '#4CC2FF'],
    ['紫色', '#B69CFF'],
    ['粉色', '#F39BC1'],
    ['橙色', '#FFB36B'],
    ['黄色', '#F2D66D'],
    ['绿色', '#7BDCB5'],
    ['青色', '#64D8E4']
  ];

  function applyThemeFromMode(mode) {
    state.themeMode = mode;
    const prefersLight = typeof window !== 'undefined' && window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches;
    const resolved = mode === 'light' ? 'light' : mode === 'dark' ? 'dark' : prefersLight ? 'light' : 'dark';
    winEl.setAttribute('data-theme', resolved);
    applyAccentVariables();
    if (state.navTarget === 'settings') renderAppSettingsPage();
    if (state.activeTabKey === 'overview') renderOverview();
  }

  function buildAppSettingsControlsHtml() {
    const langLabels = { system: '跟随系统', 'zh-CN': '简体中文', en: 'English' };
    const langOptionsHtml = Object.entries(langLabels)
      .map(([val, label]) => {
        const sel = state.language === val;
        return `<button type="button" class="winui-combo-item ${sel ? 'is-selected' : ''}" data-lang-val="${val}" role="option" aria-selected="${sel}"><span>${label}</span></button>`;
      })
      .join('');

    const isPreset = APP_SETTINGS_SWATCHES.some(([, hex]) => hex.toUpperCase() === state.accentHex.toUpperCase());
    const swatchesHtml =
      APP_SETTINGS_SWATCHES.map(([name, hex]) => {
        const active = hex.toUpperCase() === state.accentHex.toUpperCase();
        return `
        <button type="button" class="app-settings-swatch-btn ${active ? 'is-selected' : ''}" data-swatch-hex="${hex}" title="${name}" aria-label="${name}" style="background:${hex};">
          <span class="fi swatch-check" style="font-size:14px;color:#000;visibility:${active ? 'visible' : 'hidden'};">&#xE73E;</span>
        </button>
      `;
      }).join('') +
      `<button type="button" class="app-settings-swatch-btn ${isPreset ? 'app-settings-swatch-custom' : 'is-selected'}" id="settingsCustomColorBtn" title="自定义颜色" aria-label="自定义颜色" ${isPreset ? '' : `style="background:${state.accentHex};"`}>
        <span class="fi" style="font-size:14px;color:${isPreset ? '#fff' : '#000'};text-shadow:${isPreset ? '0 1px 2px rgba(0,0,0,0.45)' : 'none'};">&#xE790;</span>
      </button>`;

    const themeLabels = { default: '跟随系统', light: '浅色', dark: '深色' };
    const themeOptionsHtml = Object.entries(themeLabels)
      .map(([val, label]) => {
        const sel = state.themeMode === val;
        return `<button type="button" class="winui-combo-item ${sel ? 'is-selected' : ''}" data-theme-val="${val}" role="option" aria-selected="${sel}"><span>${label}</span></button>`;
      })
      .join('');

    const themeCtrl = `
      <div class="winui-combo" id="settingsThemeCombo">
        <button type="button" class="winui-combo-trigger" id="settingsThemeTrigger" aria-haspopup="listbox" aria-expanded="false">
          <span id="settingsThemeText">${themeLabels[state.themeMode] || '深色'}</span>
          <span class="fi winui-combo-chevron">&#xE70D;</span>
        </button>
        <div class="winui-combo-flyout" id="settingsThemeFlyout" role="listbox" hidden>${themeOptionsHtml}</div>
      </div>`;

    const langCtrl = `
      <div class="winui-combo" id="settingsLangCombo">
        <button type="button" class="winui-combo-trigger" id="settingsLangTrigger" aria-haspopup="listbox" aria-expanded="false">
          <span id="settingsLangText">${langLabels[state.language] || '跟随系统'}</span>
          <span class="fi winui-combo-chevron">&#xE70D;</span>
        </button>
        <div class="winui-combo-flyout" id="settingsLangFlyout" role="listbox" hidden>${langOptionsHtml}</div>
      </div>`;

    const accentToggleCtrl = `
      <div class="winui-switch ${state.useThemeAccentForHeader ? 'on' : ''}" id="settingsAccentSwitch" role="switch" aria-checked="${state.useThemeAccentForHeader}" tabindex="0">
        <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
      </div>`;

    const paletteInner = `
      <div class="app-settings-swatches">${swatchesHtml}</div>
      <div class="app-settings-row-ctrl">
        <button type="button" class="app-settings-accent-chip" id="settingsAccentChip" title="点击复制颜色代码">
          <span class="app-settings-accent-dot" id="settingsAccentChipDot" style="background:${state.useThemeAccentForHeader ? state.accentHex : 'var(--TextFillColorTertiaryBrush)'};"></span>
          <span id="settingsColorValueText">${state.useThemeAccentForHeader ? state.accentHex : '中性灰'}</span>
        </button>
      </div>`;

    const switchHtml = (id, on, disabled = false) => `
      <div class="winui-switch ${on ? 'on' : ''} ${disabled ? 'disabled' : ''}" id="${id}" role="switch" aria-checked="${on}" tabindex="${disabled ? -1 : 0}">
        <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
      </div>`;

    const autoStartCtrl = `
      <div class="isa-pill-track cols-3" id="settingsAutoStartSeg" style="width:264px;flex-shrink:0;" role="radiogroup" aria-label="开机自启动">
        <button type="button" class="isa-pill-btn ${state.autoStartMode === 'off' ? 'active' : ''}" data-autostart-val="off">关闭</button>
        <button type="button" class="isa-pill-btn ${state.autoStartMode === 'normal' ? 'active' : ''}" data-autostart-val="normal">正常启动</button>
        <button type="button" class="isa-pill-btn ${state.autoStartMode === 'silent' ? 'active' : ''}" data-autostart-val="silent">静默启动</button>
      </div>`;

    const closeBehaviorCtrl = `
      <div class="isa-pill-track" id="settingsCloseBehaviorSeg" style="width:220px;flex-shrink:0;" role="radiogroup" aria-label="关闭主窗口时">
        <button type="button" class="isa-pill-btn ${state.closeBehavior === 'exit' ? 'active' : ''}" data-close-val="exit">退出应用</button>
        <button type="button" class="isa-pill-btn ${state.closeBehavior === 'tray' ? 'active' : ''}" data-close-val="tray">最小化到托盘</button>
      </div>`;

    const defaultConsoleCtrl = `
      <div class="isa-pill-track" id="settingsDefaultConsoleSeg" style="min-width:196px;flex-shrink:0;" role="radiogroup" aria-label="默认控制台会话">
        <button type="button" class="isa-pill-btn ${state.defaultConsoleMode === 'RdpBasic' ? 'active' : ''}" data-console-val="RdpBasic">基本会话</button>
        <button type="button" class="isa-pill-btn ${state.defaultConsoleMode === 'RdpEnhanced' ? 'active' : ''}" data-console-val="RdpEnhanced">增强会话</button>
        ${(state.showExtensionEntries || state.defaultConsoleMode === 'External') ? `<button type="button" class="isa-pill-btn ${state.defaultConsoleMode === 'External' ? 'active' : ''}" data-console-val="External">超强会话</button>` : ''}
      </div>`;

    const defaultPowerCtrl = `
      <div class="isa-pill-track" id="settingsDefaultPowerSeg" style="width:196px;flex-shrink:0;" role="radiogroup" aria-label="运行中电源主按钮默认操作">
        <button type="button" class="isa-pill-btn ${state.defaultPowerAction === 'Shutdown' ? 'active' : ''}" data-power-val="Shutdown">正常关机</button>
        <button type="button" class="isa-pill-btn ${state.defaultPowerAction === 'Save' ? 'active' : ''}" data-power-val="Save">保存状态</button>
      </div>`;

    const diagBtnsCtrl = `
      <div class="app-settings-row-ctrl">
        <button type="button" class="winui-btn" id="settingsExportLogsBtn">导出</button>
        <button type="button" class="winui-btn" id="settingsOpenLogsBtn">打开目录</button>
        <button type="button" class="winui-btn" id="settingsClearLogsBtn">清空</button>
      </div>`;

    const aboutHtml = `
      <div class="app-settings-about-showcase">
        <img class="app-settings-about-logo" src="/ui-design/app-icon.webp" alt="ExHyperV" draggable="false" />
        <div class="app-settings-about-title-row">
          <span class="app-settings-about-name">ExHyperV</span>
          <span class="app-settings-about-ver">2.0.0-Alpha</span>
        </div>
        <div class="app-settings-about-desc">为 Windows 上的 Hyper-V，带来现代化桌面体验。</div>
        <div class="app-settings-about-actions">
          <button type="button" class="winui-btn accent app-settings-about-btn" id="settingsCheckUpdateBtn">
            <span class="fi" style="font-size:13px;">&#xE72C;</span>
            <span>检查更新</span>
          </button>
          <a class="winui-btn app-settings-about-btn" href="https://exhyperv.com" target="_blank" rel="noopener noreferrer">
            <span class="fi" style="font-size:13px;">&#xE8A7;</span>
            <span>官网文档</span>
          </a>
          <a class="winui-btn app-settings-about-btn" href="https://github.com/Justsenger/ExHyperV/issues" target="_blank" rel="noopener noreferrer">
            <span class="fi" style="font-size:13px;">&#xE71B;</span>
            <span>问题反馈</span>
          </a>
          <a class="winui-btn app-settings-about-btn" href="https://github.com/Justsenger/ExHyperV" target="_blank" rel="noopener noreferrer">
            <span class="fi" style="font-size:13px;">&#xE943;</span>
            <span>GitHub</span>
          </a>
          <a class="winui-btn app-settings-about-btn" href="https://afdian.com/a/saniye" target="_blank" rel="noopener noreferrer">
            <span class="fi" style="font-size:13px;color:#ff4a4a;">&#xEB52;</span>
            <span>赞助支持</span>
          </a>
        </div>
        <div class="app-settings-about-autoupdate">
          <span>启动时自动检查更新</span>
          ${switchHtml('settingsAutoUpdateSwitch', state.autoCheckUpdate)}
        </div>
      </div>`;

    return { themeCtrl, langCtrl, accentToggleCtrl, paletteInner, switchHtml, autoStartCtrl, closeBehaviorCtrl, defaultConsoleCtrl, defaultPowerCtrl, diagBtnsCtrl, aboutHtml };
  }

  function renderAppSettingsPage() {
    const root = document.getElementById('pageSettings');
    if (!root) return;

    const prevScroll = root.querySelector('.app-settings-body')?.scrollTop || 0;
    const c = buildAppSettingsControlsHtml();

    const rowCard = (title, desc, ctrl, extraClass = '', idAttr = '') => `
      <div class="isa-card uniform-1row ${extraClass}" ${idAttr ? `id="${idAttr}"` : ''}>
        <div class="isa-card-row">
          ${renderSettingLabelHtml(title, desc)}
          ${ctrl}
        </div>
      </div>`;

    const navItemsHtml = APP_SETTINGS_SUBNAV.map(
      (s) => `
        <button type="button" class="subnav-btn ${state.appSettingsSub === s.key ? 'selected' : ''}" data-app-sub="${s.key}">
          <span class="fi subnav-icon">${s.glyph}</span>
          <span>${s.title}</span>
        </button>`
    ).join('');

    let bodyHtml = '';
    if (state.appSettingsSub === 'appearance') {
      bodyHtml = `
        ${rowCard('显示语言', '更改界面显示语言，切换后立即生效', c.langCtrl)}
        ${rowCard('应用主题', '选择界面的深色或浅色外观，或跟随系统设置', c.themeCtrl)}
        <div class="isa-card">
          <div class="isa-card-row">
            ${renderSettingLabelHtml('主题色', '开启后展开的监控卡片与重点高亮使用主题色，关闭后使用中性灰')}
            ${c.accentToggleCtrl}
          </div>
          <div class="isa-card-row app-settings-palette-inline ${state.useThemeAccentForHeader ? '' : 'is-disabled'}" id="settingsPaletteRow">
            ${c.paletteInner}
          </div>
        </div>`;
    } else if (state.appSettingsSub === 'startup') {
      bodyHtml = `
        ${rowCard('开机自启动', '登录 Windows 时自动启动 ExHyperV，可选正常显示窗口或静默最小化到托盘', c.autoStartCtrl)}
        ${rowCard('关闭主窗口时', '选择点击窗口关闭按钮后是直接退出应用，还是最小化到系统托盘后台运行', c.closeBehaviorCtrl)}`;
    } else if (state.appSettingsSub === 'behavior') {
      bodyHtml = `
        ${rowCard('启动虚拟机后自动打开控制台', '在列表或总览中启动虚拟机并成功后，自动打开控制台连接窗口', c.switchHtml('settingsAutoConsoleSwitch', state.autoOpenConsole))}
        ${rowCard('默认控制台会话', '点击连接或自动打开控制台时默认使用的会话模式', c.defaultConsoleCtrl)}`;
    } else if (state.appSettingsSub === 'extensions') {
      bodyHtml = `
        ${rowCard('显示扩展功能入口', '在控制台与设置等界面中显示超强会话等扩展功能入口，关闭后将隐藏对应入口，可随时再次开启', c.switchHtml('settingsShowExtensionsSwitch', state.showExtensionEntries))}
        ${rowCard('外部扩展状态', '以只读方式查看宿主机组件、授权及目标虚拟机组件的当前状态', '<span id="consoleExtensionSummary">宿主组件、授权、目标虚拟机组件与传输路径尚未证实</span>')}`;
    } else if (state.appSettingsSub === 'diagnostics') {
      bodyHtml = `
        ${rowCard('运行日志', '记录应用与 Core 的操作元数据及异常，本地滚动保存最多 12 MiB', c.switchHtml('settingsLoggingSwitch', state.loggingEnabled))}
        ${rowCard('详细日志', '额外记录常规监控采样数据，用于排查性能或采样异常', c.switchHtml('settingsVerboseSwitch', state.verboseDiagnostics, !state.loggingEnabled), state.loggingEnabled ? '' : 'is-disabled', 'settingsVerboseRow')}
        ${rowCard('日志管理', '日志保存于 %LocalAppData%\\ExHyperV\\Logs，导出时会自动打包为脱敏诊断包 (.zip)', c.diagBtnsCtrl)}
        <div class="isa-card uniform-1row">
          <div class="isa-card-row">
            <span class="setting-label-title">配置与数据目录</span>
            <div class="app-settings-row-ctrl">
              <span class="app-settings-about-kv-val">%LocalAppData%\\ExHyperV</span>
              <button type="button" class="winui-btn" id="settingsOpenDataDirBtn">打开目录</button>
            </div>
          </div>
        </div>`;
    } else {
      bodyHtml = c.aboutHtml;
    }

    root.innerHTML = `
      <div class="app-settings-shell">
        <div class="app-settings-nav-pane">
          <div class="settings-subnav">${navItemsHtml}</div>
        </div>
        <div class="app-settings-body">
          <div class="app-settings-stack">${bodyHtml}</div>
        </div>
      </div>`;

    root.querySelectorAll('[data-app-sub]').forEach((btn) => {
      btn.addEventListener('click', () => {
        state.appSettingsSub = btn.getAttribute('data-app-sub') || 'appearance';
        renderAppSettingsPage();
      });
    });

    const scroller = root.querySelector('.app-settings-body');
    if (scroller) scroller.scrollTop = prevScroll;

    bindAppSettingsEvents(root);
  }

  function bindAppSettingsEvents(root) {
    const themeCombo = root.querySelector('#settingsThemeCombo');
    const themeTrigger = root.querySelector('#settingsThemeTrigger');
    const themeFlyout = root.querySelector('#settingsThemeFlyout');
    const langCombo = root.querySelector('#settingsLangCombo');
    const langTrigger = root.querySelector('#settingsLangTrigger');
    const langFlyout = root.querySelector('#settingsLangFlyout');

    themeTrigger?.addEventListener('click', (e) => {
      e.stopPropagation();
      if (!themeFlyout || !themeCombo) return;
      if (langFlyout && langCombo && langTrigger) {
        langFlyout.hidden = true;
        langCombo.classList.remove('is-open');
        langTrigger.setAttribute('aria-expanded', 'false');
      }
      const willOpen = themeFlyout.hidden;
      themeFlyout.hidden = !willOpen;
      themeCombo.classList.toggle('is-open', willOpen);
      themeTrigger.setAttribute('aria-expanded', String(willOpen));
    });
    root.querySelectorAll('#settingsThemeFlyout [data-theme-val]').forEach((item) => {
      item.addEventListener('click', () => {
        const next = item.getAttribute('data-theme-val') || 'dark';
        if (themeFlyout && themeCombo && themeTrigger) {
          themeFlyout.hidden = true;
          themeCombo.classList.remove('is-open');
          themeTrigger.setAttribute('aria-expanded', 'false');
        }
        applyThemeFromMode(next);
      });
    });

    langTrigger?.addEventListener('click', (e) => {
      e.stopPropagation();
      if (!langFlyout || !langCombo) return;
      if (themeFlyout && themeCombo && themeTrigger) {
        themeFlyout.hidden = true;
        themeCombo.classList.remove('is-open');
        themeTrigger.setAttribute('aria-expanded', 'false');
      }
      const willOpen = langFlyout.hidden;
      langFlyout.hidden = !willOpen;
      langCombo.classList.toggle('is-open', willOpen);
      langTrigger.setAttribute('aria-expanded', String(willOpen));
    });
    root.querySelectorAll('#settingsLangFlyout [data-lang-val]').forEach((item) => {
      item.addEventListener('click', () => {
        const next = item.getAttribute('data-lang-val') || 'system';
        if (langFlyout && langCombo && langTrigger) {
          langFlyout.hidden = true;
          langCombo.classList.remove('is-open');
          langTrigger.setAttribute('aria-expanded', 'false');
        }
        if (state.language === next) return;
        state.language = next;
        renderAppSettingsPage();
        showNotice('界面语言已切换', 'success');
      });
    });

    root.querySelectorAll('#settingsAutoStartSeg [data-autostart-val]').forEach((btn) => {
      btn.addEventListener('click', () => {
        state.autoStartMode = btn.getAttribute('data-autostart-val') || 'off';
        renderAppSettingsPage();
      });
    });

    root.querySelectorAll('#settingsCloseBehaviorSeg [data-close-val]').forEach((btn) => {
      btn.addEventListener('click', () => {
        state.closeBehavior = btn.getAttribute('data-close-val') || 'exit';
        renderAppSettingsPage();
      });
    });

    const summary = root.querySelector('#consoleExtensionSummary');
    if (summary && serverAdapter.isLive && serverAdapter.client.serverInfo?.consoleProtocolVersions?.includes(2) && getVm()?.id) {
      serverAdapter.client.consoleBackends(getVm().id).then(({ backends }) => {
        const ext = backends.find(item => item.extension)?.extension;
        if (!ext) return;
        const states = ['未知', '尚未证实', '不可达', '未获授权', '未安装', '版本不兼容', '已就绪'];
        const rows = [['宿主组件', ext.host], ['授权', ext.authorization], ['目标虚拟机组件', ext.guest], ['当前传输路径', ext.transport]];
        const parts = rows.map(([label, stage]) => label + '：' + (states[stage?.rawCode] || '未知'));
        if (ext.transportKind === 1 || ext.transportKind === 2) parts.push(ext.transportKind === 1 ? 'CPU 共享' : 'GPU 共享');
        if (/^[a-z0-9-]{1,32}$/i.test(ext.diagnosticId || '')) parts.push(ext.diagnosticId);
        summary.textContent = parts.join(' / ');
      }).catch(() => { summary.textContent = '状态不可达'; });
    }
    root.querySelectorAll('#settingsDefaultConsoleSeg [data-console-val]').forEach((btn) => {
      btn.addEventListener('click', () => {
        state.defaultConsoleMode = btn.getAttribute('data-console-val') || 'RdpBasic';
        try { localStorage.setItem('exhyperv.console.defaultBackend', state.defaultConsoleMode); } catch {}
        renderAppSettingsPage();
        showNotice('已更新默认控制台会话', 'success');
      });
    });

    root.querySelector('#settingsAccentSwitch')?.addEventListener('click', () => {
      state.useThemeAccentForHeader = !state.useThemeAccentForHeader;
      applyAccentVariables();
      renderAppSettingsPage();
      if (state.activeTabKey === 'overview') renderOverview();
    });

    root.querySelectorAll('[data-swatch-hex]').forEach((btn) => {
      btn.addEventListener('click', () => {
        state.accentHex = btn.getAttribute('data-swatch-hex') || '#4CC2FF';
        applyAccentVariables();
        renderAppSettingsPage();
        if (state.activeTabKey === 'overview') renderOverview();
      });
    });

    root.querySelector('#settingsAccentChip')?.addEventListener('click', async () => {
      if (!state.useThemeAccentForHeader) return;
      try {
        await navigator.clipboard?.writeText(state.accentHex);
      } catch {}
      showNotice(`已复制颜色代码 ${state.accentHex}`, 'success');
    });

    root.querySelector('#settingsCustomColorBtn')?.addEventListener('click', (e) => {
      e.stopPropagation();
      const flyout = document.getElementById('settingsColorFlyout');
      if (!flyout) return;
      const willOpen = flyout.hidden;
      if (willOpen) {
        openColorPickerFlyout(e.currentTarget);
      } else {
        flyout.hidden = true;
      }
    });

    const bindToggle = (id, getVal, setVal) => {
      const el = root.querySelector(`#${id}`);
      if (!el) return;
      const toggle = () => {
        if (el.classList.contains('disabled')) return;
        setVal(!getVal());
        renderAppSettingsPage();
      };
      el.addEventListener('click', toggle);
      el.addEventListener('keydown', (e) => {
        if (e.key === ' ' || e.key === 'Enter') {
          e.preventDefault();
          toggle();
        }
      });
    };

    bindToggle('settingsAutoConsoleSwitch', () => state.autoOpenConsole, (v) => { state.autoOpenConsole = v; });
    bindToggle('settingsShowExtensionsSwitch', () => state.showExtensionEntries, (v) => {
      state.showExtensionEntries = v;
      try { localStorage.setItem('exhyperv.extensions.showEntries', v ? '1' : '0'); } catch {}
      if (!v && state.defaultConsoleMode === 'External') {
        state.defaultConsoleMode = 'RdpBasic';
        try { localStorage.setItem('exhyperv.console.defaultBackend', 'RdpBasic'); } catch {}
      }
      consoleCtrl?.syncVmState?.();
    });
    bindToggle('settingsAutoUpdateSwitch', () => state.autoCheckUpdate, (v) => { state.autoCheckUpdate = v; });
    bindToggle('settingsLoggingSwitch', () => state.loggingEnabled, (v) => {
      state.loggingEnabled = v;
      if (!v) state.verboseDiagnostics = false;
    });
    bindToggle('settingsVerboseSwitch', () => state.verboseDiagnostics, (v) => { state.verboseDiagnostics = v; });

    root.querySelector('#settingsExportLogsBtn')?.addEventListener('click', () => {
      showNotice('已导出脱敏诊断包 (ExHyperV-Diagnostics.zip)', 'success');
    });
    root.querySelector('#settingsOpenLogsBtn')?.addEventListener('click', () => {
      showNotice('已打开日志目录 %LocalAppData%\\ExHyperV\\Logs', 'success');
    });
    root.querySelector('#settingsOpenDataDirBtn')?.addEventListener('click', () => {
      showNotice('已打开配置目录 %LocalAppData%\\ExHyperV', 'success');
    });
    root.querySelector('#settingsCheckUpdateBtn')?.addEventListener('click', () => {
      showNotice('当前已是最新版本 2.0.0-Alpha', 'success');
    });
    root.querySelector('#settingsClearLogsBtn')?.addEventListener('click', () => {
      const modal = document.getElementById('settingsClearLogsModal');
      if (modal) modal.hidden = false;
    });
  }

  const clearLogsModal = document.getElementById('settingsClearLogsModal');
  const closeClearLogsModal = () => {
    if (clearLogsModal) clearLogsModal.hidden = true;
  };
  document.getElementById('settingsClearLogsCancelBtn')?.addEventListener('click', closeClearLogsModal);
  clearLogsModal?.addEventListener('click', (e) => {
    if (e.target === clearLogsModal) closeClearLogsModal();
  });
  document.getElementById('settingsClearLogsConfirmBtn')?.addEventListener('click', () => {
    closeClearLogsModal();
    showNotice('运行日志已清空', 'success');
  });

  const cpFlyout = document.getElementById('settingsColorFlyout');
  const cpSv = document.getElementById('settingsCpSv');
  const cpThumb = document.getElementById('settingsCpThumb');
  const cpHue = document.getElementById('settingsCpHue');
  const cpPreview = document.getElementById('settingsCpPreview');
  const cpHexInput = document.getElementById('settingsCpHexInput');
  let cpH = 200;
  let cpS = 0.7;
  let cpV = 1.0;

  const hexToHsv = (hex) => {
    const m = /^#?([0-9a-f]{6})$/i.exec(String(hex || '').trim());
    if (!m) return [200, 0.7, 1];
    const n = parseInt(m[1], 16);
    const r = ((n >> 16) & 255) / 255;
    const g = ((n >> 8) & 255) / 255;
    const b = (n & 255) / 255;
    const max = Math.max(r, g, b);
    const min = Math.min(r, g, b);
    const d = max - min;
    let h = 0;
    if (d !== 0) {
      if (max === r) h = ((g - b) / d + (g < b ? 6 : 0)) * 60;
      else if (max === g) h = ((b - r) / d + 2) * 60;
      else h = ((r - g) / d + 4) * 60;
    }
    return [Math.round(h), max === 0 ? 0 : d / max, max];
  };

  const hsvToHex = (h, s, v) => {
    const c = v * s;
    const x = c * (1 - Math.abs(((h / 60) % 2) - 1));
    const m = v - c;
    let r = 0, g = 0, b = 0;
    if (h < 60) [r, g, b] = [c, x, 0];
    else if (h < 120) [r, g, b] = [x, c, 0];
    else if (h < 180) [r, g, b] = [0, c, x];
    else if (h < 240) [r, g, b] = [0, x, c];
    else if (h < 300) [r, g, b] = [x, 0, c];
    else [r, g, b] = [c, 0, x];
    const toByte = (u) => Math.round((u + m) * 255).toString(16).padStart(2, '0').toUpperCase();
    return `#${toByte(r)}${toByte(g)}${toByte(b)}`;
  };

  const syncColorPickerUi = (updateHexField = true) => {
    const hex = hsvToHex(cpH, cpS, cpV);
    if (cpSv) cpSv.style.backgroundColor = `hsl(${cpH}, 100%, 50%)`;
    if (cpThumb) {
      cpThumb.style.left = `${cpS * 100}%`;
      cpThumb.style.top = `${(1 - cpV) * 100}%`;
    }
    if (cpHue) cpHue.value = String(cpH);
    if (cpPreview) cpPreview.style.background = hex;
    if (updateHexField && cpHexInput) cpHexInput.value = hex;
  };

  function openColorPickerFlyout(anchorEl) {
    if (!cpFlyout || !anchorEl) return;
    [cpH, cpS, cpV] = hexToHsv(state.accentHex);
    syncColorPickerUi(true);
    cpFlyout.hidden = false;
    const winRect = winEl.getBoundingClientRect();
    const btnRect = anchorEl.getBoundingClientRect();
    const flyW = cpFlyout.offsetWidth || 264;
    const flyH = cpFlyout.offsetHeight || 290;
    const left = Math.max(12, Math.min(winRect.width - flyW - 12, btnRect.left - winRect.left - 8));
    const spaceBelow = winRect.bottom - btnRect.bottom;
    const top = spaceBelow >= flyH + 16
      ? btnRect.bottom - winRect.top + 8
      : Math.max(12, btnRect.top - winRect.top - flyH - 8);
    cpFlyout.style.left = `${left}px`;
    cpFlyout.style.top = `${top}px`;
  }

  if (cpSv) {
    let draggingSv = false;
    const pickSv = (e) => {
      const rect = cpSv.getBoundingClientRect();
      if (!rect.width || !rect.height) return;
      cpS = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
      cpV = Math.max(0, Math.min(1, 1 - (e.clientY - rect.top) / rect.height));
      syncColorPickerUi(true);
    };
    cpSv.addEventListener('pointerdown', (e) => {
      draggingSv = true;
      cpSv.setPointerCapture(e.pointerId);
      pickSv(e);
    });
    cpSv.addEventListener('pointermove', (e) => {
      if (draggingSv) pickSv(e);
    });
    cpSv.addEventListener('pointerup', (e) => {
      if (!draggingSv) return;
      draggingSv = false;
      cpSv.releasePointerCapture(e.pointerId);
    });
  }

  cpHue?.addEventListener('input', (e) => {
    cpH = Number(e.target.value) || 0;
    syncColorPickerUi(true);
  });

  cpHexInput?.addEventListener('input', (e) => {
    const val = String(e.target.value || '').trim();
    if (/^#[0-9a-f]{6}$/i.test(val)) {
      [cpH, cpS, cpV] = hexToHsv(val);
      syncColorPickerUi(false);
    }
  });

  document.getElementById('settingsCpCancelBtn')?.addEventListener('click', () => {
    if (cpFlyout) cpFlyout.hidden = true;
  });

  document.getElementById('settingsCpApplyBtn')?.addEventListener('click', () => {
    const hex = hsvToHex(cpH, cpS, cpV);
    if (cpFlyout) cpFlyout.hidden = true;
    state.accentHex = hex;
    applyAccentVariables();
    renderAppSettingsPage();
    if (state.activeTabKey === 'overview') renderOverview();
  });

  winEl.addEventListener('click', (e) => {
    const themeCombo = document.getElementById('settingsThemeCombo');
    const themeTrigger = document.getElementById('settingsThemeTrigger');
    const themeFlyout = document.getElementById('settingsThemeFlyout');
    if (themeFlyout && !themeFlyout.hidden && !e.target.closest('#settingsThemeCombo')) {
      themeFlyout.hidden = true;
      themeCombo?.classList.remove('is-open');
      themeTrigger?.setAttribute('aria-expanded', 'false');
    }
    const langCombo = document.getElementById('settingsLangCombo');
    const langTrigger = document.getElementById('settingsLangTrigger');
    const langFlyout = document.getElementById('settingsLangFlyout');
    if (langFlyout && !langFlyout.hidden && !e.target.closest('#settingsLangCombo')) {
      langFlyout.hidden = true;
      langCombo?.classList.remove('is-open');
      langTrigger?.setAttribute('aria-expanded', 'false');
    }
    if (cpFlyout && !cpFlyout.hidden && !e.target.closest('#settingsColorFlyout') && !e.target.closest('#settingsCustomColorBtn')) {
      cpFlyout.hidden = true;
    }
  });

  renderAppSettingsPage();

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

  function focusOverviewDevice(kind = 'mem', idx = 0) {
    if (state.navTarget !== 'vm') switchNavigationTarget('vm');
    state.activeTabKey = 'overview';
    state.tcSelected = { kind, idx };
    renderPageTabs();
    renderActiveWorkspace();
  }

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

  function emitHostAppEvent(channel, payload) {
    try {
      if (window.__WOW_APP_CLIENT__ && typeof window.__WOW_APP_CLIENT__.emitAppEvent === 'function') {
        window.__WOW_APP_CLIENT__.emitAppEvent(channel, payload);
        return;
      }
      if (window.parent === window) return;
      window.parent.postMessage(
        {
          protocol: 'win11-app/v1',
          type: 'w11:app-event',
          channel,
          payload
        },
        window.location.origin
      );
    } catch {
      // ignore
    }
  }

  function notifyParentGpuState() {
    if (window.parent === window && !window.__WOW_APP_CLIENT__) return;
    const vm = getVm();
    const assigns = state.gpuAssignmentsByVm[vm.id] || [];
    let status = 'single';
    if (assigns.length === 0) status = 'empty';
    else if (assigns.length >= 2) status = 'dual';
    emitHostAppEvent('exhyperv:gpu-sync', {
      activeTabKey: state.activeTabKey,
      deviceSubKey: state.deviceSubKey,
      variant: state.gpuVariant || 'tiles',
      addPlacement: state.gpuAddPlacement || 'list-bottom-row',
      addBtnStyle: state.gpuAddBtnStyle || 'secondary-icon-text',
      iconStyle: state.gpuIconStyle || 'vector',
      driverMismatch: Boolean(state.gpuDriverMismatch),
      updateIcon: state.gpuUpdateIcon || 'upgrade-base',
      updateColor: state.gpuUpdateColor || 'white',
      status,
      mode: state.gpuMode || 'list'
    });
  }

  const hostCandidateMappings = (() => {
    try {
      const panel = document.getElementById('designCandidatesPanel');
      return JSON.parse(panel?.dataset?.dcpMappings || '[]');
    } catch {
      return [];
    }
  })();

  const VALID_STORAGE_ADD_VARIANTS = new Set(['pivot-2x3', 'pivot-3x2', 'matrix-card']);
  const VALID_DISK_CAP_LAYOUTS = new Set(['header-edge-6', 'header-edge-8', 'header-edge', 'bar-only', 'bar-pct']);
  const VALID_NIC_VARIANTS = new Set(['unified', 'inline-converged', 'pill-tabs', 'legacy']);
  const VALID_NIC_MODES = new Set(['list', 'add', 'policy']);
  const VALID_NIC_HOST_SWITCHES = new Set(['one', 'many', 'none']);
  const VALID_NIC_VM_ENVS = new Set(['gen1-off', 'gen2-off', 'gen1-on']);
  const VALID_NIC_SCENARIOS = new Set(['single', 'multi', 'empty']);
  const VALID_NIC_ADV_LABELS = new Set(['策略']);
  const VALID_NIC_ADV_ICONS = new Set(['E9E9', 'F8A6', 'E9D5', 'E71C', 'E90F', 'E713']);

  const VALID_CHECKPOINT_CANDIDATES = new Set([
    'a-vm-desktop',
    'a-console-shell',
    'c-brand-matte',
    'c-iso-chain'
  ]);

  function getHostPanelSyncState() {
    const vm = getVm();
    return {
      ...(hostMod ? hostMod.getHostOverviewCandidate() : DEFAULT_HOST_OVERVIEW_CANDIDATE),
      checkpointCandidate: state.checkpointCandidate || 'c-brand-matte',
      storageAddVariant: state.storageAddVariant || 'pivot-2x3',
      storageVmGen: String(Number(vm?.generation) === 1 ? 1 : 2),
      diskCapLayout: state.diskCapLayout || 'header-edge-6',
      nicVariant: state.nicVariant || 'unified',
      nicMode: state.nicMode || 'list',
      nicHostSwitches: state.nicHostSwitches || 'one',
      nicVmEnv: Number(vm?.generation) === 1 ? (vm?.running ? 'gen1-on' : 'gen1-off') : 'gen2-off',
      nicScenario: state.nicScenario || 'single',
      nicPower: vm?.running ? 'on' : 'off',
      nicAdvLabel: state.nicAdvLabel || '策略',
      nicAdvIcon: state.nicAdvIcon || 'E71C'
    };
  }

  function syncStandaloneHostRailPanel() {
    const cfg = getHostPanelSyncState();
    hostCandidateMappings.forEach(([attr, key]) => {
      document.querySelectorAll(`#designCandidatesPanel [${attr}]`).forEach((btn) => {
        const raw = btn.getAttribute(attr);
        btn.classList.toggle('active', raw === 'toggle' ? cfg[key] === 'on' : raw === cfg[key]);
      });
    });
  }

  function notifyParentHostState() {
    emitHostAppEvent('exhyperv:host-sync', getHostPanelSyncState());
  }

  function focusStorageAddTab() {
    if (state.navTarget !== 'vm') switchNavigationTarget('vm');
    state.activeTabKey = 'devices';
    state.deviceSubKey = 'storage';
    state.storageMode = 'add';
    renderPageTabs();
    renderDashboardSummary();
    renderActiveWorkspace();
  }

  function focusNetworkSettingsTab() {
    if (state.navTarget !== 'vm') switchNavigationTarget('vm');
    state.activeTabKey = 'devices';
    state.deviceSubKey = 'network';
    renderPageTabs();
    renderDashboardSummary();
    renderActiveWorkspace();
  }

  function focusCheckpointsTab() {
    if (state.navTarget !== 'vm') switchNavigationTarget('vm');
    state.activeTabKey = 'checkpoints';
    renderPageTabs();
    renderDashboardSummary();
    renderActiveWorkspace();
  }

  async function applyHostCandidatePayload(payload) {
    if (!payload || typeof payload !== 'object') return;
    const {
      checkpointCandidate,
      checkpointShowThumbs,
      storageAddVariant,
      storageVmGen,
      diskCapLayout,
      nicVariant,
      nicMode,
      nicHostSwitches,
      nicVmEnv,
      nicScenario,
      nicPower,
      nicAdvLabel,
      nicAdvIcon,
      ...hostPayload
    } = payload;

    let handledCheckpoint = false;
    if (typeof checkpointCandidate === 'string' && VALID_CHECKPOINT_CANDIDATES.has(checkpointCandidate)) {
      state.checkpointCandidate = checkpointCandidate;
      state.checkpointConfirmAction = null;
      handledCheckpoint = true;
    }
    if (checkpointShowThumbs === 'on' || checkpointShowThumbs === 'off') {
      state.checkpointShowThumbs = checkpointShowThumbs === 'on';
      handledCheckpoint = true;
    }

    let handledNic = false;
    const vm = getVm();
    const needsDevicesReset =
      vm &&
      ((nicMode === 'policy' && state.nicScenario === 'empty') ||
        (typeof nicScenario === 'string' && VALID_NIC_SCENARIOS.has(nicScenario)) ||
        (state.nicScenario === 'empty' &&
          ((typeof nicAdvLabel === 'string' && VALID_NIC_ADV_LABELS.has(nicAdvLabel)) ||
            (typeof nicAdvIcon === 'string' && VALID_NIC_ADV_ICONS.has(nicAdvIcon)))));
    const activeDevicesMod = needsDevicesReset ? devicesMod || (await loadDevicesMod()) : devicesMod;

    if (typeof nicVariant === 'string' && VALID_NIC_VARIANTS.has(nicVariant)) {
      state.nicVariant = nicVariant;
      if (nicVariant !== 'unified' && (state.nicMode === 'policy' || state.nicMode === 'add')) {
        state.nicMode = 'list';
      }
      handledNic = true;
    }
    if (typeof nicMode === 'string' && VALID_NIC_MODES.has(nicMode)) {
      if (nicMode === 'policy' && state.nicScenario === 'empty' && vm) {
        state.nicScenario = 'single';
        activeDevicesMod.resetNetworkAdaptersForScenario(state, vm, 'single');
      }
      if (nicMode === 'policy' || nicMode === 'add') {
        state.nicVariant = 'unified';
      }
      state.nicMode = nicMode;
      handledNic = true;
    }
    if (typeof nicHostSwitches === 'string' && VALID_NIC_HOST_SWITCHES.has(nicHostSwitches)) {
      state.nicHostSwitches = nicHostSwitches;
      state.nicMode = 'add';
      handledNic = true;
    }
    if (typeof nicVmEnv === 'string' && VALID_NIC_VM_ENVS.has(nicVmEnv)) {
      state.nicVmEnv = nicVmEnv;
      if (vm) {
        vm.generation = nicVmEnv === 'gen2-off' ? 2 : 1;
        vm.gen = vm.generation;
        vm.running = nicVmEnv === 'gen1-on';
      }
      renderVmList();
      handledNic = true;
    }
    if (typeof nicScenario === 'string' && VALID_NIC_SCENARIOS.has(nicScenario)) {
      state.nicScenario = nicScenario;
      if (vm) activeDevicesMod.resetNetworkAdaptersForScenario(state, vm, nicScenario);
      handledNic = true;
    }
    if (typeof nicPower === 'string' && (nicPower === 'on' || nicPower === 'off')) {
      if (vm) vm.running = nicPower === 'on';
      renderVmList();
      handledNic = true;
    }
    if (typeof nicAdvLabel === 'string' && VALID_NIC_ADV_LABELS.has(nicAdvLabel)) {
      state.nicAdvLabel = nicAdvLabel;
      if (state.nicScenario === 'empty' && vm) {
        state.nicScenario = 'single';
        activeDevicesMod.resetNetworkAdaptersForScenario(state, vm, 'single');
      }
      state.nicVariant = 'unified';
      state.nicMode = 'policy';
      state.nicPolicyTab = 'security';
      handledNic = true;
    }
    if (typeof nicAdvIcon === 'string' && VALID_NIC_ADV_ICONS.has(nicAdvIcon)) {
      state.nicAdvIcon = nicAdvIcon;
      if (state.nicScenario === 'empty' && vm) {
        state.nicScenario = 'single';
        activeDevicesMod.resetNetworkAdaptersForScenario(state, vm, 'single');
      }
      state.nicVariant = 'unified';
      state.nicMode = 'policy';
      state.nicPolicyTab = 'security';
      handledNic = true;
    }

    if (handledCheckpoint) {
      focusCheckpointsTab();
    } else if (handledNic) {
      focusNetworkSettingsTab();
    } else if (typeof storageAddVariant === 'string' && VALID_STORAGE_ADD_VARIANTS.has(storageAddVariant)) {
      state.storageAddVariant = storageAddVariant;
      focusStorageAddTab();
    } else if (storageVmGen === '1' || storageVmGen === '2') {
      if (vm) {
        vm.generation = Number(storageVmGen);
        vm.gen = vm.generation;
      }
      renderVmList();
      focusStorageAddTab();
    } else if (typeof diskCapLayout === 'string' && VALID_DISK_CAP_LAYOUTS.has(diskCapLayout)) {
      state.diskCapLayout = diskCapLayout;
      const curIdx = state.tcSelected?.kind === 'disk' ? state.tcSelected.idx : 0;
      focusOverviewDevice('disk', curIdx);
    }

    if (Object.keys(hostPayload).length > 0) {
      if (state.navTarget !== 'host') switchNavigationTarget('host');
      const activeHostMod = hostMod || (await loadHostMod());
      activeHostMod.setHostOverviewCandidate(hostPayload);
    }
    syncStandaloneHostRailPanel();
    notifyParentHostState();
  }

  hostCandidateMappings.forEach(([attr, key]) => {
    document.querySelectorAll(`#designCandidatesPanel [${attr}]`).forEach((btn) => {
      btn.addEventListener('click', () => {
        const val = btn.getAttribute(attr);
        if (!val) return;
        const nextVal = val === 'toggle' ? (btn.classList.contains('active') ? 'off' : 'on') : val;
        applyHostCandidatePayload({ [key]: nextVal });
      });
    });
  });

  function applyGpuCandidateData(data) {
    if (!data || typeof data !== 'object') return;
    const vm = getVm();
    if (state.navTarget !== 'vm') switchNavigationTarget('vm');
    state.activeTabKey = 'devices';
    state.deviceSubKey = 'gpu';

    if (data.variant) {
      state.gpuVariant = data.variant;
      if (!data.mode) state.gpuMode = 'list';
    }
    if (data.addPlacement) {
      state.gpuAddPlacement = data.addPlacement;
      if (!data.mode) state.gpuMode = 'list';
    }
    if (data.addBtnStyle) {
      state.gpuAddBtnStyle = data.addBtnStyle;
      if (!data.mode) state.gpuMode = 'list';
    }
    if (data.iconStyle) {
      state.gpuIconStyle = data.iconStyle;
    }
    if (typeof data.driverMismatch === 'boolean') {
      state.gpuDriverMismatch = data.driverMismatch;
    }
    if (data.updateIcon) {
      state.gpuUpdateIcon = data.updateIcon;
    }
    if (data.updateColor) {
      state.gpuUpdateColor = data.updateColor;
    }
    if (data.status) {
      const s = data.status;
      if (s === 'empty') {
        state.gpuAssignmentsByVm[vm.id] = [];
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
            expanded: false
          };
          state.gpuAssignmentsByVm[vm.id] = h2 === HOST_GPUS[0] && !HOST_GPUS[1] ? [g1] : [g1, g2];
        } else {
          state.gpuAssignmentsByVm[vm.id] = g1 ? [g1] : [];
        }
      } else {
        state.gpuAssignmentsByVm[vm.id] = structuredClone(INITIAL_GPU_ASSIGNMENTS);
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
  }

  function syncParentStateAll() {
    notifyParentGpuState();
    notifyParentHostState();
  }

  window.addEventListener('exhyperv:wow-init', syncParentStateAll);
  if (window.__WOW_APP_READY__) {
    syncParentStateAll();
  }

  window.addEventListener('exhyperv:wow-message', (event) => {
    const msg = event.detail;
    if (!msg || typeof msg !== 'object') return;
    if (msg.channel === 'exhyperv:host-candidate') {
      applyHostCandidatePayload(msg.payload);
    } else if (msg.channel === 'exhyperv:gpu-candidate') {
      applyGpuCandidateData(msg.payload);
    } else if (msg.channel === 'exhyperv:console-power' && msg.payload && typeof msg.payload === 'object') {
      const targetVm = activeVms.find((v) => v.id === msg.payload.id) || getVm();
      if (serverAdapter.isLive && msg.payload.action && targetVm && !targetVm._empty) {
        Promise.resolve(executePowerCommand(targetVm, msg.payload.action))
          .then(() => {
            if (msg.payload.requestId) {
              emitHostAppEvent('exhyperv:console-power-reply', {
                cid: msg.payload.cid,
                requestId: msg.payload.requestId,
                ok: true,
                running: Boolean(targetVm.running)
              });
            }
          })
          .catch((err) => {
            if (msg.payload.requestId) {
              emitHostAppEvent('exhyperv:console-power-reply', {
                cid: msg.payload.cid,
                requestId: msg.payload.requestId,
                ok: false,
                error: err?.message || '虚拟机电源操作失败'
              });
            }
          });
        return;
      }
      if (msg.payload.requestId) {
        emitHostAppEvent('exhyperv:console-power-reply', {
          cid: msg.payload.cid,
          requestId: msg.payload.requestId,
          ok: true,
          mode: 'demo'
        });
      }
      if (targetVm && typeof msg.payload.running === 'boolean') {
        targetVm.running = msg.payload.running;
        syncStandaloneHostRailPanel();
        notifyParentHostState();
        renderVmList();
        renderDashboardSummary();
        renderActiveWorkspace();
      }
    } else if (msg.channel === 'exhyperv:console-need-ticket' && msg.payload && typeof msg.payload === 'object') {
      if (serverAdapter.isLive) {
        serverAdapter
          .issueConsoleTicket(msg.payload.vmId, msg.payload.mode, msg.payload.watchOnly === true, msg.payload.ceiling)
          .then((conn) => {
            emitHostAppEvent('exhyperv:console-ticket-reply', {
              cid: msg.payload.cid,
              requestId: msg.payload.requestId,
              serverMode: 'live',
              serverUrl: conn?.serverUrl || '',
              protocol: conn?.protocol, rights: conn?.rights, backendId: conn?.backendId, extension: conn?.extension,
              wsTicket: conn?.ticket || ''
            });
          })
          .catch((err) => {
            emitHostAppEvent('exhyperv:console-ticket-reply', {
              cid: msg.payload.cid,
              requestId: msg.payload.requestId,
              serverMode: 'error',
              error: err?.message || '获取控制台连接票据失败'
            });
          });
      } else {
        emitHostAppEvent('exhyperv:console-ticket-reply', {
          cid: msg.payload.cid,
          requestId: msg.payload.requestId,
          serverMode: 'demo',
          serverUrl: '',
          wsTicket: ''
        });
      }
    }
  });

  window.addEventListener('message', (event) => {
    if (event.origin !== window.location.origin) return;
    const raw = event.data;
    if (!raw || typeof raw !== 'object') return;
    if (
      (raw.protocol === 'win11-app/v1' && raw.type === 'w11:init') ||
      raw.type === 'exhyperv-gpu-request-sync'
    ) {
      syncParentStateAll();
      return;
    }

    if (
      raw.protocol === 'win11-app/v1' &&
      raw.type === 'w11:app-message' &&
      raw.channel === 'exhyperv:host-candidate'
    ) {
      applyHostCandidatePayload(raw.payload);
      return;
    }

    if (
      raw.protocol === 'win11-app/v1' &&
      raw.type === 'w11:app-message' &&
      raw.channel === 'exhyperv:gpu-candidate'
    ) {
      applyGpuCandidateData(raw.payload);
    } else if (raw.type === 'exhyperv-gpu-candidate') {
      applyGpuCandidateData(raw);
    }
  });

  // Initial render
  applyAccentVariables();
  syncStandaloneHostRailPanel();
  renderVmList();
  renderDashboardSummary();
  renderPageTabs();
  renderActiveWorkspace();
}
