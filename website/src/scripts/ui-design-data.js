export const OS_TYPES = [
  'Windows', 'Ubuntu', 'ArchLinux', 'Debian', 'CentOS', 'Kali', 'Linux', 'Android', 'ChromeOS', 'FydeOS',
  'MacOS', 'FreeBSD', 'OpenWrt', 'FnOS', 'iStoreOS', 'TrueNAS', 'Unraid', 'NixOS', 'Manjaro', 'LinuxMint',
  'Fedora', 'Deepin', 'openEuler', 'Kylin', 'openSUSE', 'RockyLinux', 'AlmaLinux', 'RHEL', 'Alpine'
];

export const PAGE_TABS = [
  { key: 'overview', title: '状态', titleEn: 'Status', titleEnShort: 'Status', glyph: '\uE9D9', fluent: false, rotate: 0, file: 'VmDashboardView.xaml + Monitoring.cs + Devices.cs' },
  { key: 'processor', title: '处理器', titleEn: 'Processor', titleEnShort: 'CPU', glyph: '\uF58E', fluent: false, rotate: 90, file: 'Settings/VmProcessorSettingsView.cs + VmCpuAffinitySettingsView.cs' },
  { key: 'memory', title: '内存', titleEn: 'Memory', titleEnShort: 'RAM', glyph: '\uEEA0', fluent: false, rotate: 0, file: 'Settings/VmMemorySettingsView.cs' },
  { key: 'devices', title: '设备', titleEn: 'Devices', titleEnShort: 'Devices', glyph: '\uF0E2', fluent: false, rotate: 0, file: 'Settings/VmGpuSettingsView.cs + Storage + Network' },
  { key: 'spacetime', title: '检查点', titleEn: 'Checkpoints', titleEnShort: 'Snapshots', glyph: '\uE1DF', fluent: true, rotate: 0, file: 'Settings/VmSpacetimeSettingsView.cs' },
  { key: 'advanced', title: '高级', titleEn: 'Advanced', titleEnShort: 'Adv', glyph: '\uEC7A', fluent: false, rotate: 0, file: 'Settings/VmBootSettingsView.cs + Security + Console + Power + StoragePolicy' }
];

export const MERGED_SUBNAV_GROUPS = {
  devices: [
    { key: 'gpu', title: '显卡', titleEn: 'Graphics', titleEnShort: 'GPU', glyph: '\uF211', file: 'Settings/VmGpuSettingsView.cs + Workflow.cs + Decisions.cs' },
    { key: 'storage', title: '存储', titleEn: 'Storage', titleEnShort: 'Storage', glyph: '\uEDA2', file: 'Settings/VmStorageSettingsView.cs + Add.cs' },
    { key: 'network', title: '网卡', titleEn: 'Adapter', titleEnShort: 'NIC', glyph: '\uE774', file: 'Settings/VmNetworkSettingsView.cs' },
    { key: 'serial', title: '串口', titleEn: 'Serial', titleEnShort: 'COM', glyph: '\uE950', file: 'Settings/VmSerialPortSettingsView.cs' },
    { key: 'pcie', title: 'PCIe', titleEn: 'PCIe', titleEnShort: 'PCIe', glyph: '\uF0F5', fluent: true, dividerBefore: true, file: 'Settings/VmPcieSettingsView.cs' }
  ],
  advanced: [
    { key: 'boot', title: '引导', titleEn: 'Boot', titleEnShort: 'Boot', file: 'Settings/VmBootSettingsView.cs' },
    { key: 'security', title: '安全', titleEn: 'Security', titleEnShort: 'Security', file: 'Settings/VmSecuritySettingsView.cs' },
    { key: 'console', title: '控制台', titleEn: 'Console', titleEnShort: 'Console', file: 'Settings/VmAdvancedSettingsView.cs' },
    { key: 'power', title: '电源', titleEn: 'Power', titleEnShort: 'Power', file: 'Settings/VmAdvancedSettingsView.cs' },
    { key: 'storage-policy', title: '存储', titleEn: 'Storage', titleEnShort: 'Storage', file: 'Settings/VmAdvancedSettingsView.cs' }
  ]
};

export const VM_ITEMS = [
  {
    id: 'vm-test',
    name: 'test',
    osImage: '/ui-design/os/Windows.svg',
    running: false,
    stateText: '已关机',
    uptime: '00:00:00',
    liveUptime: '00:18:42',
    cores: 12,
    memoryGb: 2.0,
    usedMemoryGb: 1.4,
    configText: '12 核 / 2 GB / 128 GB + 128 GB',
    generationText: '',
    versionText: '',
    osText: 'Windows',
    gpuText: 'NVIDIA GeForce RTX 4090',
    ipWhenRunning: '172.24.118.94',
    ipv6WhenRunning: 'fe80::215:5dff:fe01:2a0b',
    gpus: [
      {
        key: 'gpu-0',
        title: 'NVIDIA GeForce RTX 4090',
        vendor: 'NVIDIA',
        memoryText: '24564 MB',
        driverVersion: '32.0.15.6636',
        instanceId: 'PCI\\VEN_10DE&DEV_2684&SUBSYS_409010DE&REV_A1\\4&2C8B14A&0&0008'
      }
    ],
    disks: [
      {
        key: 'disk-0',
        title: '新建虚拟磁盘.vhdx',
        subtitle: '动态磁盘',
        controllerLoc: 'SCSI 0:0',
        vhdFormat: '动态 VHDX',
        glyph: '\uEC59',
        fileSizeText: '196 MB',
        capacityText: '128 GB',
        ratio: 0.002,
        readRate: '1.2 MB/s',
        writeRate: '420 KB/s',
        readIops: 142,
        writeIops: 38,
        queueLength: 1,
        path: 'C:\\Users\\saniye\\Downloads\\usbip\\新建虚拟磁盘.vhdx',
        physicalInfo: {
          model: 'WDC SN850X 1000GB',
          diskNumber: 2,
          serialNumber: 'WD-WCC6Y4K8921A',
          capacityText: '1.0 TB',
          readRate: '24.6 MB/s',
          writeRate: '8.4 MB/s',
          readIops: 410,
          writeIops: 96,
          queueLength: 2
        }
      },
      {
        key: 'disk-1',
        title: 'test.vhdx',
        subtitle: '动态磁盘',
        controllerLoc: 'SCSI 0:1',
        vhdFormat: '固定 VHDX',
        glyph: '\uEC59',
        fileSizeText: '52.5 GB',
        capacityText: '128 GB',
        ratio: 0.41,
        readRate: '3.8 MB/s',
        writeRate: '1.1 MB/s',
        readIops: 86,
        writeIops: 24,
        queueLength: 0,
        path: 'D:\\Hyper-V\\Virtual Hard Disks\\test.vhdx'
      }
    ],
    adapters: [
      {
        key: 'net-0',
        title: '网络适配器',
        subtitle: 'Default Switch',
        mac: '00155D012A0B'
      }
    ]
  },
  {
    id: 'vm-win11-arm64',
    name: 'Windows11-ARM64',
    osImage: '/ui-design/os/Windows.svg',
    running: false,
    stateText: '已关机',
    uptime: '00:00:00',
    liveUptime: '02:14:38',
    cores: 8,
    memoryGb: 8.0,
    usedMemoryGb: 4.6,
    configText: '8 核 / 8 GB / 128 GB',
    generationText: '',
    versionText: '',
    osText: 'Windows',
    gpuText: 'NVIDIA GeForce RTX 4090',
    ipWhenRunning: '172.24.118.108',
    ipv6WhenRunning: 'fe80::215:5dff:fe2a:9f0c',
    gpus: [
      {
        key: 'gpu-0',
        title: 'NVIDIA GeForce RTX 4090',
        vendor: 'NVIDIA',
        memoryText: '24564 MB',
        driverVersion: '32.0.15.6636',
        instanceId: 'PCI\\VEN_10DE&DEV_2684&SUBSYS_409010DE&REV_A1\\4&2C8B14A&0&0008'
      }
    ],
    disks: [
      {
        key: 'disk-arm-0',
        title: 'Windows11-ARM64.vhdx',
        subtitle: '动态磁盘',
        glyph: '\uEC59',
        fileSizeText: '36.8 GB',
        capacityText: '128 GB',
        ratio: 0.287,
        path: 'D:\\Hyper-V\\Virtual Hard Disks\\Windows11-ARM64.vhdx'
      }
    ],
    adapters: [
      {
        key: 'net-arm-0',
        title: '网络适配器',
        subtitle: 'Default Switch',
        mac: '00155D2A9F0C'
      }
    ]
  }
];

/* 1:1 VmProcessorSettingsView.cs (9 sections including VmCpuAffinitySettingsView) */
export const PROCESSOR_SECTIONS = [
  {
    title: '计算资源',
    titleEn: 'Compute',
    titleEnShort: 'Compute',
    kind: 'fields',
    fields: [
      { id: 'Count', title: '核心数 (vCPU)', kind: 'stepper', value: '12', min: 1, max: 32, desc: '虚拟机的逻辑处理器数量，只能在虚拟机关机后修改。' },
      { id: 'Reservation', title: '预留 (%)', kind: 'stepper', value: '0', min: 0, max: 100, desc: '为虚拟机设定的最低处理器资源比例。' },
      { id: 'Limit', title: '限制 (%)', kind: 'stepper', value: '100', min: 0, max: 100, desc: '为虚拟机设定的最高处理器资源比例。' },
      { id: 'RelativeWeight', title: '权重', kind: 'stepper', value: '100', min: 1, max: 10000, desc: '多台虚拟机争抢处理器资源时的分配优先级。' }
    ]
  },
  {
    title: '核心绑定',
    titleEn: 'CPU Affinity',
    titleEnShort: 'Affinity',
    kind: 'affinity',
    cpuModel: 'AMD Ryzen 9 9950X 16-Core Processor',
    desc: '按住左键拖动可多选。全部取消选择表示解除绑定。'
  },
  {
    title: '指令集能力',
    titleEn: 'Instruction Set',
    titleEnShort: 'Features',
    kind: 'fields',
    fields: [
      { id: 'ExposeVirtualizationExtensions', title: '嵌套虚拟化', kind: 'toggle', value: true, desc: '开启后透传虚拟化指令集，支持在虚拟机内运行虚拟机' },
      { id: 'CompatibilityForMigrationEnabled', title: '迁移兼容性', kind: 'toggle', value: false, desc: '屏蔽 CPU 指令集，以便在不同版本的主机间迁移' },
      { id: 'CompatibilityForMigrationMode', title: '迁移功能集', kind: 'choice', value: 'MinimumFeatureSet', options: [['MinimumFeatureSet', '最小功能集'], ['CommonClusterFeatureSet', '群集共同功能集']], desc: '选择固定的最小特征集，或群集节点共同支持的特征集' },
      { id: 'CompatibilityForOlderOperatingSystemsEnabled', title: '旧操作系统兼容', kind: 'toggle', value: false, desc: '屏蔽 CPU 指令集，提高对老旧操作系统的兼容性' },
      { id: 'CpuBrandString', title: '自定义 CPU 名称', kind: 'string', value: 'AMD Ryzen 9 9950X 16-Core Processor', desc: '修改向来宾公开的 CPU 名称' }
    ]
  },
  {
    title: '处理器拓扑',
    titleEn: 'Topology',
    titleEnShort: 'Topology',
    kind: 'fields',
    fields: [
      { id: 'SmtMode', title: '超线程', kind: 'choice', value: 'Inherit', options: [['Inherit', '自动'], ['SingleThread', '关闭'], ['MultiThread', '开启']], desc: '自动：跟随宿主拓扑。\n关闭：每个虚拟核心 1 个线程。\n开启：每 2 个 vCPU 组成 1 个双线程核心。' },
      { id: 'MaxProcessorsPerNumaNode', title: '单节点最大核心数', kind: 'stepper', value: '16', min: 1, max: 240, desc: '每个虚拟 NUMA 节点最多容纳的 vCPU 数量。数值越小，拆出的 NUMA 节点越多。' },
      { id: 'MaxNumaNodesPerSocket', title: '单插槽最大节点数', kind: 'stepper', value: '1', min: 1, max: 64, desc: '每个虚拟 CPU 插槽最多容纳的 NUMA 节点数。数值越小，拆出的 CPU 插槽越多。' },
      { id: 'MaxProcessorCountPerL3', title: '单 L3 最大核心数', kind: 'stepper', value: '', placeholder: '自动', min: 1, max: 240, desc: '每个虚拟 L3 缓存域最多容纳的 vCPU 数量，留空为自动。' },
      { id: 'MaxClusterCountPerSocket', title: '单插槽最大簇数', kind: 'stepper', value: '', placeholder: '自动', min: 1, max: 8, desc: '每个虚拟 CPU 插槽最多包含的处理器簇数（1～8），留空为自动。' },
      { id: 'L3DistributionPolicy', title: 'L3 处理器分布', kind: 'choice', value: 'SmallToLarge', options: [['SmallToLarge', '由小到大'], ['LargeToSmall', '由大到小'], ['EvenSmallToLarge', '均匀小到大'], ['EvenLargeToSmall', '均匀大到小']], desc: '当 vCPU 除不尽单 L3 最大核心数时（如 22 核按每 L3 最多 6 核分为 4 组）：\n由小到大：4, 6, 6, 6\n由大到小：6, 6, 6, 4\n均匀小到大：5, 5, 6, 6\n均匀大到小：6, 6, 5, 5' },
      { id: 'L3CacheWays', title: 'L3 缓存路数', kind: 'stepper', value: '0', min: 0, max: 1024, desc: '设置虚拟机读取到的 L3 缓存相联路数，0 表示默认（仅改变报告值，不分配物理缓存）。' }
    ]
  },
  {
    title: '中断控制',
    titleEn: 'Interrupts',
    titleEnShort: 'Interrupts',
    kind: 'fields',
    fields: [
      { id: 'ApicMode', title: '中断控制器模式', kind: 'choice', value: 'Default', options: [['Default', '默认'], ['Legacy', '传统'], ['Apic', 'xAPIC'], ['X2Apic', 'x2APIC']], desc: '切换虚拟机的 APIC 中断控制器模式（仅 x64 支持）。' },
      { id: 'LpiMode', title: '局部外设中断', kind: 'choice', value: 'Default', options: [['Default', '默认'], ['Disabled', '禁用'], ['Enabled', '启用']], desc: '设置虚拟机接收局部外设中断的方式（仅 ARM64 支持）。', disabled: true, disabledTip: '当前宿主架构不支持' },
      { id: 'EnableHostResourceProtection', title: '主机资源保护', kind: 'toggle', value: false, desc: '当虚拟机持续占用过多宿主时间时自动节流，保护宿主与其他虚拟机。' }
    ]
  },
  {
    title: '性能监控',
    titleEn: 'Performance',
    titleEnShort: 'PerfMon',
    kind: 'fields',
    fields: [
      { id: 'AllowAcountMcount', title: '真实频率计数器', kind: 'toggle', value: true, desc: '允许虚拟机读取实际与基准频率比值以正确显示主频（仅 x64 支持）。' },
      { id: 'EnablePerfmonPmu', title: '硬件性能计数器（PMU）', kind: 'toggle', value: false, desc: '允许虚拟机内的分析工具读取硬件指令数、缓存命中等指标（仅 Intel 支持）。' },
      { id: 'EnablePerfmonLbr', title: '分支跳转记录（LBR）', kind: 'toggle', value: false, desc: '透传最近分支跳转历史栈，用于低开销采样分析（仅 Intel 支持）。' },
      { id: 'EnablePerfmonPebs', title: '精确事件采样（PEBS）', kind: 'toggle', value: false, desc: '计数器溢出时记录精确指令地址与寄存器快照（仅 Intel 支持，需先开启 PMU）。' },
      { id: 'EnablePerfmonIpt', title: '指令流跟踪（IPT）', kind: 'toggle', value: false, desc: '硬件级连续记录指令执行流，用于深度追踪与调试（仅 Intel 支持，需先开启 PMU）。' }
    ]
  },
  {
    title: '频率调节',
    titleEn: 'Frequency',
    titleEnShort: 'Freq',
    kind: 'fields',
    fields: [
      { id: 'PerfCpuFreqMinMhz', title: '最低频率（MHz）', kind: 'stepper', value: '3000', min: 0, max: 9000, desc: '限制处理器的最低运行频率，避免空闲降频过低导致唤醒卡顿。' },
      { id: 'PerfCpuFreqCapMhz', title: '最高频率（MHz）', kind: 'stepper', value: '5700', min: 0, max: 9000, desc: '限制处理器的最高运行频率，可用于控温或限制功耗。' },
      { id: 'PerfCpuFreqDesiredMhz', title: '目标频率（MHz）', kind: 'stepper', value: '', placeholder: '自动', min: 0, max: 9000, desc: '固定频率模式下希望尽量维持的目标主频。' },
      { id: 'PerfCpuEnergyPerformancePreference', title: '性能倾向', kind: 'stepper', value: '', placeholder: '自动', min: 0, max: 255, desc: '智能调频时的调度倾向（对应底层 EPP：性能优先=0，均衡=128，节能优先=255）。' },
      { id: 'PerfCpuAutonomousActivityWindow', title: '负载采样窗口', kind: 'stepper', value: '', placeholder: '自动', min: 0, max: 10000, desc: '硬件评估负载变化的时间窗口原始值（一般保持自动即可）。' },
      { id: 'PerfCpuIgnoreHostMaxFrequency', title: '忽略宿主频率限制', kind: 'toggle', value: false, desc: '宿主机开启节能模式限频时，允许该虚拟机不受宿主电源计划封顶（仍受物理极限约束）。' }
    ]
  },
  {
    title: '寻址与分页',
    titleEn: 'Addressing',
    titleEnShort: 'Paging',
    kind: 'fields',
    fields: [
      { id: 'PageShatterMode', title: '大页拆分缓解', kind: 'choice', value: 'Default', options: [['Default', '默认'], ['AlwaysEnabled', '启用'], ['AlwaysDisabled', '禁用']], desc: '针对部分老款 Intel CPU 取指缺陷（CVE-2018-12207）的安全缓解，防止来宾异常导致宿主崩溃。保持“默认”即可由系统按 CPU 型号自动决定。' },
      { id: 'PhysicalAddressWidth', title: '物理地址位宽', kind: 'stepper', value: '', placeholder: '自动', min: 36, max: 52, desc: '限制虚拟机可见的物理地址总线位宽（决定内存与直通设备 MMIO 的寻址上限，如 40 位约 1 TB、48 位约 256 TB）。日常留空为自动即可。' }
    ]
  },
  {
    title: '安全隔离',
    titleEn: 'Isolation',
    titleEnShort: 'Isolation',
    kind: 'fields',
    fields: [
      { id: 'DisableSpeculationControls', title: '禁用推测执行控制', kind: 'toggle', value: false, desc: '向虚拟机隐藏推测执行侧信道漏洞缓解控制能力。' },
      { id: 'HardwareIsolationExtensionsEnabled', title: '硬件隔离扩展', kind: 'toggle', value: false, desc: '允许在虚拟机内嵌套运行硬件隔离子虚拟机（如 AMD SEV-SNP，需宿主硬件支持）。' },
      { id: 'MaxHwIsolatedGuests', title: '硬件隔离来宾上限', kind: 'stepper', value: '0', min: 0, max: 64, desc: '允许创建的硬件隔离来宾最大数量。' }
    ]
  }
];

/* 1:1 VmMemorySettingsView.cs (6 balanced sections) */
export const MEMORY_SECTIONS = [
  {
    title: '计算资源',
    titleEn: 'Allocation',
    titleEnShort: 'Memory',
    fields: [
      { id: 'Startup', title: '启动内存', kind: 'text', value: '2048', unit: 'MiB', desc: '为虚拟机分配的初始运行内存大小。' },
      { id: 'Weight', title: '内存权重', kind: 'text', value: '5000', unit: '', desc: '多台虚拟机争抢内存时的分配优先级；数值越大优先级越高。' },
      { id: 'DynamicMemoryEnabled', title: '动态内存', kind: 'toggle', value: false, desc: '允许 Hyper-V 在虚拟机运行时按实际负载动态增减内存。' },
      { id: 'Minimum', title: '最小内存', kind: 'text', value: '512', unit: 'MiB', desc: '动态内存缩减时允许保留的最低内存下限。' },
      { id: 'Maximum', title: '最大内存', kind: 'text', value: '16384', unit: 'MiB', desc: '动态内存扩展时允许分配的最高内存上限。' },
      { id: 'Buffer', title: '内存缓冲', kind: 'text', value: '20', unit: '%', span2: true, desc: '在当前内存需求之上额外预留的缓冲比例，用于应对突发负载。' }
    ]
  },
  {
    title: 'MMIO 空间',
    titleEn: 'MMIO Space',
    titleEnShort: 'MMIO',
    fields: [
      { id: 'LowSizeMiB', title: '32 位空间大小', kind: 'text', value: '512', unit: 'MiB', desc: '4 GB 以下低位 MMIO 空间大小（MiB），供传统 32 位 PCI 设备映射使用；一般保持默认即可，过大可能挤占低地址可用内存。' },
      { id: 'HighSizeMiB', title: '64 位空间大小', kind: 'text', value: '32768', unit: 'MiB', desc: '4 GB 以上高位 MMIO 空间大小（MiB），供 64 位 PCIe 设备使用；GPU 直通（DDA）等大显存设备资源不足时主要调大此项。' },
      { id: 'HighBaseMiB', title: '64 位空间起始地址', kind: 'text', value: '65536', unit: 'MiB', desc: '4 GB 以上高位 MMIO 空间的起始物理地址偏移（MiB，1024² 字节）；通常无需手动修改。' }
    ]
  },
  {
    title: '内存页管理',
    titleEn: 'Page Sizing',
    titleEnShort: 'Pages',
    fields: [
      { id: 'BackingPageSize', title: '后备页大小', kind: 'choice', value: '0', options: [['0', '默认 (4 KB)'], ['1', '大页 (2 MB)'], ['2', '巨大页 (1 GB)']], desc: '设置宿主机分配物理内存的页面大小；大页和巨页可降低地址转换开销，提升大内存负载性能。' },
      { id: 'DynMemOperationAlignment', title: '动态内存操作对齐', kind: 'choice', value: '1', options: [['0', '禁用'], ['1', '标准'], ['2', '大'], ['3', '巨大']], desc: '设置动态内存热增减时的边界对齐粒度，用于减少内存碎片，一般保持默认即可。' },
      { id: 'MaxMemoryBlocksPerNumaNode', title: '每个 NUMA 节点的内存块上限', kind: 'text', value: '131072', unit: 'MiB', span2: true, desc: '限制单个虚拟 NUMA 节点可分配的最大内存容量（MiB），用于配合虚拟 NUMA 拓扑减少跨节点访问。' }
    ]
  },
  {
    title: '内存映射',
    titleEn: 'Memory Mapping',
    titleEnShort: 'Mapping',
    fields: [
      { id: 'BackingType', title: '内存后备方式', kind: 'choice', value: '0', options: [['0', '物理后备'], ['1', '虚拟后备'], ['2', '混合（按节点配置）']], disabledOptions: ['2'], desc: '选择虚拟机内存由物理内存直接映射还是通过虚拟后备管理，会影响大页、冷热页提示及压缩存储。' },
      { id: 'EnableColdHint', title: '冷页提示', kind: 'toggle', value: false, desc: '允许向宿主机标记长时间未访问的内存页，便于优先回收或置换。' },
      { id: 'EnableHotHint', title: '热页提示', kind: 'toggle', value: false, desc: '允许向宿主机标记高频访问的内存页，尽量保留在高速物理内存中。' },
      { id: 'EnableEpf', title: '扩展页错误（EPF）', kind: 'toggle', value: false, desc: '启用扩展页错误（EPF）机制，辅助虚拟化环境下的内存访问与缺页调度。' },
      { id: 'EnablePrivateCompressionStore', title: '私有压缩存储', kind: 'toggle', value: false, desc: '为虚拟机启用独立的内存压缩存储区，降低内存紧张时的换出开销。' },
      { id: 'EnableGpaPinning', title: 'GPA 固定', kind: 'toggle', value: false, span2: true, desc: '将虚拟机物理地址（GPA）固定映射，防止被宿主机随意迁移或换出。' }
    ]
  },
  {
    title: '机密计算',
    titleEn: 'Confidential Compute',
    titleEnShort: 'Security',
    fields: [
      { id: 'MemoryEncryptionPolicy', title: '内存加密策略', kind: 'choice', value: '0', options: [['0', '禁用'], ['1', '支持时启用'], ['2', '始终启用']], desc: '控制是否对虚拟机内存启用硬件加密保护（需宿主机 CPU 支持）。' },
      { id: 'SgxEnabled', title: 'SGX', kind: 'toggle', value: false, span2: true, desc: '为虚拟机启用 Intel SGX 机密计算支持。' },
      { id: 'SgxLaunchControlMode', title: 'SGX 启动控制', kind: 'choice', value: '0', options: [['0', '拒绝访问'], ['1', '只读'], ['2', '读写']], desc: '控制来宾对 SGX 启动控制寄存器的访问权限。' },
      { id: 'SgxSize', title: 'SGX 内存大小', kind: 'text', value: '0', unit: 'MiB', span2: true, min: 2, step: 2, desc: '为 SGX 安全飞地（EPC）单独划拨的专用内存容量（最小 2 MiB，按 2 MiB 对齐）。' },
      { id: 'SgxLaunchControlDefault', title: 'SGX 默认启动控制值', kind: 'string', value: '', span2: true, placeholder: '自动（默认配置）', desc: '设置 SGX 默认启动控制密钥的初始配置文本。' }
    ]
  },
  {
    title: '高级扩展',
    titleEn: 'Extensions',
    titleEnShort: 'Extensions',
    fields: [
      { id: 'MemoryAccessTrackingState', title: '访问跟踪', kind: 'choice', value: '0', options: [['0', '禁用'], ['1', '启用'], ['2', '按 NUMA 节点配置']], disabledOptions: ['2'], desc: '开启后可追踪虚拟机内存页的读写活跃度，用于热页识别或实时迁移优化。' },
      { id: 'MemoryAccessTrackingPolicy', title: '跟踪粒度', kind: 'choice', value: '0', options: [['0', '自动'], ['1', '标准'], ['2', '大'], ['3', '巨大']], desc: '设置内存访问跟踪的统计粒度，粒度越粗开销越低。' },
      { id: 'CxlEnabled', title: 'CXL 内存扩展', kind: 'toggle', value: false, span2: true, desc: '实验性设置：部分 Windows 版本保存此项可能导致虚拟机管理服务崩溃；属性存在不代表硬件支持。' }
    ]
  }
];

/* 1:1 VmStorageSettingsView.cs attachments (Matches Screenshot 4 media_1794659693841_045a2033.png) */
export const INITIAL_STORAGE_ITEMS = [
  {
    id: 'st-1',
    driveType: 'HardDisk',
    diskType: 'Virtual',
    displayName: '新建虚拟磁盘.vhdx',
    sizeText: '196.0 MB',
    controllerType: 'SCSI',
    controllerNumber: 0,
    controllerLocation: 0,
    path: 'C:\\Users\\saniye\\Downloads\\usbip\\新建虚拟磁盘.vhdx',
    hasIsoMedia: false,
    isPassthroughStale: false,
    expanded: true
  },
  {
    id: 'st-2',
    driveType: 'HardDisk',
    diskType: 'Virtual',
    displayName: 'test.vhdx',
    sizeText: '52.5 GB',
    controllerType: 'SCSI',
    controllerNumber: 0,
    controllerLocation: 1,
    path: 'D:\\Hyper-V\\Virtual Hard Disks\\test.vhdx',
    hasIsoMedia: false,
    isPassthroughStale: false,
    expanded: false
  }
];

/* 1:1 VmGpuSettingsView.cs (GPU-PV) */
export const INITIAL_GPU_ASSIGNMENTS = [
  {
    id: 'gpu-1',
    name: 'NVIDIA GeForce RTX 4090',
    manu: 'NVIDIA',
    vendor: 'ASUS (华硕)',
    memoryText: '24564 MB',
    driverVersion: '32.0.15.6636',
    instanceId: 'PCI\\VEN_10DE&DEV_2684&SUBSYS_409010DE&REV_A1\\4&2C8B14A&0&0008',
    partitionPath: '\\\\?\\PCI#VEN_10DE&DEV_2684&SUBSYS_409010DE&REV_A1#4&2c8b14a&0&0008#{064092b3-625e-43bf-9eb5-dc845897dd95}\\GPUPARAV',
    expanded: true
  }
];

export const HOST_GPUS = [
  {
    id: 'host-gpu-1',
    name: 'NVIDIA GeForce RTX 4090',
    manu: 'NVIDIA',
    vendor: 'ASUS (华硕)',
    memoryText: '24564 MB',
    driverVersion: '32.0.15.6636',
    instanceId: 'PCI\\VEN_10DE&DEV_2684&SUBSYS_409010DE&REV_A1\\4&2C8B14A&0&0008',
    partitionPath: '\\\\?\\PCI#VEN_10DE&DEV_2684&SUBSYS_409010DE&REV_A1#4&2c8b14a&0&0008#{064092b3-625e-43bf-9eb5-dc845897dd95}\\GPUPARAV',
    partitionable: true
  },
  {
    id: 'host-gpu-2',
    name: 'AMD Radeon(TM) Graphics (iGPU)',
    manu: 'AMD',
    vendor: 'AMD',
    memoryText: '2048 MB',
    driverVersion: '31.0.24002.92',
    instanceId: 'PCI\\VEN_1002&DEV_164E&SUBSYS_88771043&REV_C1\\4&1A990C2&0&0041',
    partitionPath: '\\\\?\\PCI#VEN_1002&DEV_164E&SUBSYS_88771043&REV_C1#4&1a990c2&0&0041#{064092b3-625e-43bf-9eb5-dc845897dd95}\\GPUPARAV',
    partitionable: true
  },
  {
    id: 'host-gpu-3',
    name: 'Intel(R) Arc(TM) A770 Graphics',
    manu: 'Intel',
    vendor: 'Intel',
    memoryText: '16384 MB',
    driverVersion: '32.0.101.6256',
    instanceId: 'PCI\\VEN_8086&DEV_56A0&SUBSYS_10298086&REV_08\\4&3F91C0B&0&0010',
    partitionPath: '',
    partitionable: false,
    unpartitionableReason: '未发现可用的 GPU-PV 分区资源'
  }
];

export const INITIAL_GPU_PARTITIONS = [
  {
    id: 'part-win11',
    diskLabel: '磁盘 0 · 分区 1',
    title: 'Windows 11 Pro (C:)',
    fs: 'NTFS',
    sizeText: '119.2 GB',
    osType: 'Windows',
    locked: false,
    desc: '识别到 \\Windows\\System32，可直接通过宿主 DriverStore 注入驱动'
  },
  {
    id: 'part-ubuntu',
    diskLabel: '磁盘 0 · 分区 2',
    title: 'Ubuntu 24.04.1 LTS (/)',
    fs: 'ext4',
    sizeText: '96.0 GB',
    osType: 'Linux',
    locked: false,
    desc: '识别到 /etc/os-release，可通过开机 SSH 自动部署 Mesa / CUDA 组件'
  },
  {
    id: 'part-bitlocker',
    diskLabel: '磁盘 1 · 分区 1',
    title: '工作数据区 (D:)',
    fs: 'BitLocker 已加密',
    sizeText: '240.0 GB',
    osType: 'BitLocker',
    locked: true,
    desc: '卷已启用 BitLocker 加密，无法离线读取系统目录'
  }
];

export const LINUX_GPU_SCRIPTS = [
  { id: 'ubuntu-cuda-mesa', label: 'Ubuntu / Debian — CUDA + Mesa D3D12 自动部署' },
  { id: 'arch-mesa', label: 'Arch Linux — Mesa Dozen Vulkan 驱动部署' },
  { id: 'generic-wslg', label: '通用发行版 — 仅同步 /usr/lib/wsl/lib 库文件' }
];

/* 1:1 VmBootSettingsView.cs */
export const INITIAL_BOOT_ENTRIES = [
  { id: 'boot-1', name: '固件文件', description: '\\EFI\\Microsoft\\Boot\\bootmgfw.efi', glyph: '\uE74C' },
  { id: 'boot-2', name: '硬盘驱动器', description: '新建虚拟磁盘.vhdx (SCSI 0:0)', glyph: '\uEDA2' },
  { id: 'boot-3', name: '硬盘驱动器', description: 'test.vhdx (SCSI 0:1)', glyph: '\uEDA2' },
  { id: 'boot-4', name: '网络适配器', description: '网络适配器 (Default Switch)', glyph: '\uE774' }
];

/* 1:1 VmSerialPortSettingsView.cs */
export const INITIAL_SERIAL_PORTS = [
  {
    portNumber: 1,
    name: 'COM 1',
    pipePath: '\\\\.\\pipe\\vm-test-com1',
    debuggerMode: false
  },
  {
    portNumber: 2,
    name: 'COM 2',
    pipePath: '',
    debuggerMode: false
  }
];

/* 1:1 VmPcieSettingsView.cs */
export const INITIAL_PCIE_DEVICES = [
  {
    id: 'pcie-1',
    name: 'NVIDIA GeForce RTX 4090 (vPCI)',
    classType: 'Display',
    glyph: '\uF211',
    guestMode: '0',
    instanceId: 'VPCI\\VEN_10DE&DEV_2684\\5&19A02B&0&0001',
    path: 'PCIROOT(0)#PCI(0101)#PCI(0000)',
    vendor: 'NVIDIA Corporation',
    expanded: true
  }
];

/* 1:1 VmSpacetimeSettingsView.cs */
export const SPACETIME_NODES = [
  { id: 'node-root', parentId: null, name: '初始干净系统 (24H2)', date: '2026-11-10 14:20:05', type: 'Snapshot', wormhole: false, x: 150, y: 210 },
  { id: 'node-2', parentId: 'node-root', name: '安装 CUDA 与 GPU-P 驱动前', date: '2026-11-12 19:45:12', type: 'Snapshot', wormhole: true, x: 430, y: 135 },
  { id: 'node-3', parentId: 'node-root', name: '测试内核调试分支', date: '2026-11-13 09:12:44', type: 'Snapshot', wormhole: false, x: 430, y: 295 },
  { id: 'node-current', parentId: 'node-2', name: '当前状态', date: '2026-11-14 19:35:00', type: 'Current', wormhole: false, x: 710, y: 135 }
];
