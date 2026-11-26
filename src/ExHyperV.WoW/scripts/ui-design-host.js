const HOST_SYS = {
  os: 'Windows 11 专业工作站版 (Build 28000)',
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

const POWER_SUBGROUPS = [
  {
    id: '54533251-82be-4824-96c1-47b60b740d00',
    name: '处理器电源管理',
    icon: '&#xE950;',
    settings: [
      {
        id: '893dee8e-2bef-41e0-89c6-b55d0929964c',
        name: '最小处理器状态',
        desc: '指定处理器的最小性能状态（百分比）。',
        hidden: false,
        kind: 'range',
        min: 0,
        max: 100,
        step: 1,
        unit: '%',
        defaults: { balanced: [5, 5], high: [100, 100], ultimate: [100, 100], saver: [5, 5] }
      },
      {
        id: 'bc5038f7-23e0-4960-96da-33abaf5935ec',
        name: '最大处理器状态',
        desc: '指定处理器的最大性能状态（百分比）。',
        hidden: false,
        kind: 'range',
        min: 0,
        max: 100,
        step: 1,
        unit: '%',
        defaults: { balanced: [100, 100], high: [100, 100], ultimate: [100, 100], saver: [100, 70] }
      },
      {
        id: 'be337238-0d82-4146-a960-4f3749d470c7',
        name: '处理器性能提升模式',
        desc: '指定在当前操作情况允许选择高于最大频率的频率时，处理器如何选择目标频率（Turbo Boost）。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '已禁用' },
          { value: 1, label: '已启用' },
          { value: 2, label: '高性能' },
          { value: 3, label: '高效率' },
          { value: 4, label: '高性能高效率' },
          { value: 5, label: '积极且有保障' },
          { value: 6, label: '高效、积极且有保障' }
        ],
        defaults: { balanced: [2, 2], high: [2, 2], ultimate: [2, 2], saver: [0, 0] }
      },
      {
        id: '0cc5b647-c1df-4637-891a-dec35c318583',
        name: '处理器性能核心放置最小核心数量',
        desc: '指定核心放置机制保留处于活动状态的最小逻辑核心比例（%），设为 100% 可减少虚拟机线程唤醒延迟。',
        hidden: true,
        kind: 'range',
        min: 0,
        max: 100,
        step: 1,
        unit: '%',
        defaults: { balanced: [25, 10], high: [100, 50], ultimate: [100, 100], saver: [5, 5] }
      },
      {
        id: '93b8b6dc-0698-4d1c-9ee4-0644e900c85d',
        name: '异类线程调度策略',
        desc: '指定要在大小核异类处理器系统上使用哪种长期运行线程调度策略。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '所有处理器' },
          { value: 1, label: '高性能处理器' },
          { value: 2, label: '首选高性能处理器' },
          { value: 3, label: '高效处理器' },
          { value: 4, label: '首选高效处理器' },
          { value: 5, label: '自动' }
        ],
        defaults: { balanced: [2, 5], high: [2, 2], ultimate: [1, 1], saver: [4, 4] }
      },
      {
        id: 'bae08b81-2d5e-4688-ad6a-13243356654b',
        name: '异类短运行线程调度策略',
        desc: '指定短运行线程在异类处理器架构上的首选调度类别。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '所有处理器' },
          { value: 1, label: '高性能处理器' },
          { value: 2, label: '首选高性能处理器' },
          { value: 3, label: '高效处理器' },
          { value: 4, label: '首选高效处理器' },
          { value: 5, label: '自动' }
        ],
        defaults: { balanced: [2, 4], high: [2, 2], ultimate: [1, 1], saver: [4, 3] }
      },
      {
        id: '36687f9e-e3a5-4dbf-b1dc-15eb381c6864',
        name: '第 1 类处理器能源性能首选项策略',
        desc: '控制硬件 P-state / EPP 的能耗倾向（0 偏向极致性能，100 偏向最高节能）。',
        hidden: true,
        kind: 'range',
        min: 0,
        max: 100,
        step: 1,
        unit: '%',
        defaults: { balanced: [33, 50], high: [0, 15], ultimate: [0, 0], saver: [70, 85] }
      },
      {
        id: '5d76a2ca-e8c0-402f-a133-2158492d58ad',
        name: '处理器闲置禁用',
        desc: '决定处理器是否允许进入低功耗空闲状态（C-state）。禁用空闲可降低延迟但明显增加功耗与发热。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '启用闲置' },
          { value: 1, label: '禁用闲置' }
        ],
        defaults: { balanced: [0, 0], high: [0, 0], ultimate: [0, 0], saver: [0, 0] }
      },
      {
        id: '94d3a615-a899-4ac5-ae2b-e4d8f634367f',
        name: '系统散热方式',
        desc: '指定处理器升温时的散热优先顺序（主动先提高风扇转速，被动先降低处理器性能）。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '被动' },
          { value: 1, label: '主动' }
        ],
        defaults: { balanced: [1, 0], high: [1, 1], ultimate: [1, 1], saver: [0, 0] }
      },
      {
        id: '75b0ae3f-bce0-45a7-8c89-c9611c25e100',
        name: '处理器最大频率',
        desc: '限制处理器的最大工作频率（MHz），设为 0 表示不限制上限。',
        hidden: true,
        kind: 'range',
        min: 0,
        max: 4294967295,
        step: 100,
        unit: 'MHz',
        defaults: { balanced: [0, 0], high: [0, 0], ultimate: [0, 0], saver: [0, 2400] }
      }
    ]
  },
  {
    id: '501a4d13-42af-4429-9fd1-a8218c268e20',
    name: 'PCI Express',
    icon: '&#xE964;',
    settings: [
      {
        id: 'ee12f906-d277-404b-b6da-e5fa1a576df5',
        name: '链接状态电源管理',
        desc: '指定链接空闲时用于可用 PCIe 链接的活动状态电源管理 (ASPM) 策略，显卡直通与外置设备建议设为关闭。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '关闭' },
          { value: 1, label: '中等电源节省量' },
          { value: 2, label: '最大电源节省量' }
        ],
        defaults: { balanced: [1, 2], high: [0, 1], ultimate: [0, 0], saver: [2, 2] }
      }
    ]
  },
  {
    id: '0012ee47-9041-4b5d-9b77-535fba8b1442',
    name: '硬盘',
    icon: '&#xEDA2;',
    settings: [
      {
        id: '6738e2c4-e8a5-4a42-b16a-e040e769756e',
        name: '在此时间后关闭硬盘',
        desc: '指定硬盘在无读写活动多久后进入断电/休眠状态，设为 0 表示从不关闭（单位为秒）。',
        hidden: false,
        kind: 'range',
        min: 0,
        max: 4294967295,
        step: 30,
        unit: '秒',
        defaults: { balanced: [1200, 600], high: [0, 1200], ultimate: [0, 0], saver: [300, 120] }
      },
      {
        id: 'd639518a-e56d-4345-8af2-b9f32fb26109',
        name: 'Primary NVMe Idle Timeout',
        desc: '主 NVMe 固态硬盘进入自主功耗转换前的空闲等待时间（毫秒）。',
        hidden: true,
        kind: 'range',
        min: 0,
        max: 60000,
        step: 50,
        unit: 'ms',
        defaults: { balanced: [200, 100], high: [500, 200], ultimate: [0, 0], saver: [100, 50] }
      },
      {
        id: '0b2d69d7-a2a1-449c-9680-f91c70521c60',
        name: 'AHCI Link Power Management - HIPM/DIPM',
        desc: '配置 SATA/AHCI 控制器的主机与设备发起链接电源管理策略。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: 'Active' },
          { value: 1, label: 'HIPM' },
          { value: 2, label: 'HIPM+DIPM' },
          { value: 3, label: 'DIPM' },
          { value: 4, label: 'Lowest' }
        ],
        defaults: { balanced: [1, 2], high: [0, 1], ultimate: [0, 0], saver: [4, 4] }
      }
    ]
  },
  {
    id: '238c9fa8-0aad-41ed-83f4-97be242c8f20',
    name: '睡眠',
    icon: '&#xE708;',
    settings: [
      {
        id: '29f6c1db-86da-48c5-9fdb-f2b67b1f44da',
        name: '在此时间后睡眠',
        desc: '指定计算机在进入睡眠状态前处于非活动状态的时间，设为 0 表示从不睡眠（单位为秒）。',
        hidden: false,
        kind: 'range',
        min: 0,
        max: 4294967295,
        step: 60,
        unit: '秒',
        defaults: { balanced: [0, 1800], high: [0, 0], ultimate: [0, 0], saver: [900, 600] }
      },
      {
        id: '94ac6d29-73ce-41a6-809f-6363ba21b47e',
        name: '允许混合睡眠',
        desc: '启用后睡眠时同时把内存写入休眠文件，在 Hyper-V/S0 待机系统上通常不可用。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '关闭' },
          { value: 1, label: '启用' }
        ],
        defaults: { balanced: [0, 0], high: [0, 0], ultimate: [0, 0], saver: [0, 0] }
      },
      {
        id: '9d7815a6-7ee4-497e-8888-515a05f02364',
        name: '在此时间后休眠',
        desc: '指定计算机进入休眠状态前的非活动超时时间，设为 0 表示从不休眠（单位为秒）。',
        hidden: false,
        kind: 'range',
        min: 0,
        max: 4294967295,
        step: 300,
        unit: '秒',
        defaults: { balanced: [0, 604800], high: [0, 0], ultimate: [0, 0], saver: [10800, 3600] }
      },
      {
        id: 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d',
        name: '允许使用唤醒定时器',
        desc: '控制计划任务或其他唤醒定时器能否把系统从睡眠状态唤醒。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '禁用' },
          { value: 1, label: '启用' },
          { value: 2, label: '仅限重要的唤醒定时器' }
        ],
        defaults: { balanced: [2, 0], high: [1, 0], ultimate: [1, 1], saver: [0, 0] }
      },
      {
        id: '7bc4a2f9-d8fc-4469-b07b-33eb785aaca0',
        name: '无人参与系统睡眠超时',
        desc: '自动唤醒后无用户交互时重新进入睡眠状态的超时时间（秒）。',
        hidden: true,
        kind: 'range',
        min: 0,
        max: 4294967295,
        step: 30,
        unit: '秒',
        defaults: { balanced: [120, 120], high: [120, 120], ultimate: [0, 0], saver: [120, 60] }
      }
    ]
  },
  {
    id: '7516b95f-f776-4464-8c53-06167f40cc99',
    name: '显示',
    icon: '&#xE7F4;',
    settings: [
      {
        id: '3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e',
        name: '在此时间后关闭显示',
        desc: '指定关闭显示器前计算机处于非活动状态的时间，设为 0 表示从不关闭（单位为秒）。',
        hidden: false,
        kind: 'range',
        min: 0,
        max: 4294967295,
        step: 60,
        unit: '秒',
        defaults: { balanced: [900, 300], high: [0, 600], ultimate: [0, 0], saver: [300, 120] }
      },
      {
        id: 'aded5e82-b909-4619-9949-f5d71dac0bcb',
        name: '显示器亮度',
        desc: '指定内置显示器的默认亮度百分比。',
        hidden: false,
        kind: 'range',
        min: 0,
        max: 100,
        step: 5,
        unit: '%',
        defaults: { balanced: [70, 50], high: [100, 80], ultimate: [100, 100], saver: [50, 35] }
      },
      {
        id: 'f1fbfde2-a960-4165-9f88-50667911ce96',
        name: '显示器亮度变暗',
        desc: '在关闭屏幕前短暂变暗时的目标亮度百分比。',
        hidden: false,
        kind: 'range',
        min: 0,
        max: 100,
        step: 5,
        unit: '%',
        defaults: { balanced: [50, 40], high: [50, 40], ultimate: [50, 50], saver: [30, 20] }
      },
      {
        id: 'fbd9aa66-9553-4097-ba44-ed6e9d65eab8',
        name: '启用自适应亮度',
        desc: '根据环境光线传感器或内容变化自动调整显示器亮度。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '关闭' },
          { value: 1, label: '启用' }
        ],
        defaults: { balanced: [1, 1], high: [0, 1], ultimate: [0, 0], saver: [1, 1] }
      },
      {
        id: '8ec4b3a5-6868-48c2-be75-4f3044be88a7',
        name: '控制台锁定显示关闭超时',
        desc: '锁屏界面下处于无操作状态多长时间后关闭显示器（秒）。',
        hidden: true,
        kind: 'range',
        min: 0,
        max: 4294967295,
        step: 15,
        unit: '秒',
        defaults: { balanced: [60, 60], high: [60, 60], ultimate: [0, 0], saver: [30, 30] }
      }
    ]
  },
  {
    id: '2a737441-1930-4402-8d77-b2bebba308a3',
    name: 'USB 设置',
    icon: '&#xECF0;',
    settings: [
      {
        id: '48e6b7a6-50f5-4782-a5d4-53bb8f07e226',
        name: 'USB 选择性暂停设置',
        desc: '允许操作系统在单个 USB 设备空闲时将其挂起，外接加密狗或采集卡断连时建议禁用。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '已禁用' },
          { value: 1, label: '已启用' }
        ],
        defaults: { balanced: [1, 1], high: [0, 1], ultimate: [0, 0], saver: [1, 1] }
      },
      {
        id: 'd4e98f31-5ffe-4ce1-be31-1b38b384c009',
        name: 'USB 3 链接电源管理',
        desc: '控制 USB 3.x 主机控制器和设备链接何时进入 U1/U2 节能状态。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '关闭' },
          { value: 1, label: '中等电源节省量' },
          { value: 2, label: '最大电源节省量' }
        ],
        defaults: { balanced: [1, 2], high: [0, 1], ultimate: [0, 0], saver: [2, 2] }
      }
    ]
  },
  {
    id: '4f971e89-eebd-4455-a8de-9e59040e7347',
    name: '电源按钮和盖子',
    icon: '&#xE7E8;',
    settings: [
      {
        id: 'a7066653-8d6c-40a8-910e-a1f54b84c7e5',
        name: '「开始」菜单电源按钮',
        desc: '指定选择 Windows「开始」菜单默认电源命令时的行为。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '睡眠' },
          { value: 1, label: '休眠' },
          { value: 2, label: '关机' }
        ],
        defaults: { balanced: [0, 0], high: [0, 0], ultimate: [2, 2], saver: [0, 0] }
      },
      {
        id: '5ca83367-6e45-459f-a27b-476b1d01c936',
        name: '合上盖子操作',
        desc: '指定移动设备合上屏幕盖子时执行的电源动作。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '不采取任何操作' },
          { value: 1, label: '睡眠' },
          { value: 2, label: '休眠' },
          { value: 3, label: '关机' }
        ],
        defaults: { balanced: [1, 1], high: [0, 1], ultimate: [0, 1], saver: [1, 1] }
      }
    ]
  },
  {
    id: 'e73a048d-bf27-4f12-9731-8b2076e8891f',
    name: '电池',
    icon: '&#xE83F;',
    settings: [
      {
        id: '8183ba9a-e910-48da-8769-14ae6dc1170a',
        name: '电池电量水平低',
        desc: '触发低电量操作与提示通知的电池剩余百分比阈值。',
        hidden: false,
        kind: 'range',
        min: 0,
        max: 100,
        step: 1,
        unit: '%',
        defaults: { balanced: [10, 10], high: [10, 10], ultimate: [10, 10], saver: [15, 15] }
      },
      {
        id: 'd8742dcb-3e6a-4b3c-b3fe-374623cdcf06',
        name: '低电量操作',
        desc: '电池电量达到低电量水平时执行的操作。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '不采取任何操作' },
          { value: 1, label: '睡眠' },
          { value: 2, label: '休眠' },
          { value: 3, label: '关机' }
        ],
        defaults: { balanced: [0, 0], high: [0, 0], ultimate: [0, 0], saver: [0, 0] }
      },
      {
        id: '9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469',
        name: '关键电池电量水平',
        desc: '触发关键级别电池强制保护动作的电量百分比。',
        hidden: false,
        kind: 'range',
        min: 0,
        max: 100,
        step: 1,
        unit: '%',
        defaults: { balanced: [5, 5], high: [5, 5], ultimate: [5, 5], saver: [5, 5] }
      },
      {
        id: '637ea02f-bbcb-4015-8e2c-a1c7b9c0b546',
        name: '关键级别电池操作',
        desc: '电量跌至严重不足阈值时防止数据丢失的系统动作。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '不采取任何操作' },
          { value: 1, label: '睡眠' },
          { value: 2, label: '休眠' },
          { value: 3, label: '关机' }
        ],
        defaults: { balanced: [2, 2], high: [2, 2], ultimate: [2, 2], saver: [2, 2] }
      },
      {
        id: 'f3c5027d-cd16-4930-aa6b-90db844a8f00',
        name: '保留电池电量',
        desc: '在系统触发低电量保护前通知用户保留的电量水平。',
        hidden: false,
        kind: 'range',
        min: 0,
        max: 100,
        step: 1,
        unit: '%',
        defaults: { balanced: [7, 7], high: [7, 7], ultimate: [7, 7], saver: [7, 7] }
      }
    ]
  },
  {
    id: '19cbb8fa-5279-450e-9fac-8a3d5fedd0c1',
    name: '无线适配器设置',
    icon: '&#xE701;',
    settings: [
      {
        id: '12bbebe6-58d6-4636-95bb-3217ef867c1a',
        name: '节能模式',
        desc: '调整无线网卡 (Wi-Fi) 射频与信标侦听的功耗等级。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '最高性能' },
          { value: 1, label: '低节能' },
          { value: 2, label: '中等节能' },
          { value: 3, label: '最高节能' }
        ],
        defaults: { balanced: [0, 2], high: [0, 0], ultimate: [0, 0], saver: [2, 3] }
      }
    ]
  },
  {
    id: '9596fb26-9850-41fd-ac3e-f7c3c00afd4b',
    name: '多媒体设置',
    icon: '&#xE714;',
    settings: [
      {
        id: '03680956-93bc-4294-bba6-4e0f09bb717f',
        name: '共享媒体时',
        desc: '正在通过网络向局域网其他设备共享媒体文件时是否允许计算机睡眠。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '允许计算机睡眠' },
          { value: 1, label: '阻止计算机在一段时间不活动后进入睡眠状态' },
          { value: 2, label: '允许计算机进入离开模式' }
        ],
        defaults: { balanced: [1, 0], high: [1, 1], ultimate: [1, 1], saver: [0, 0] }
      },
      {
        id: '34c7b99f-9a6d-4b3c-8dc7-b6693b78cef4',
        name: '播放视频时',
        desc: '播放视频内容时偏好画质后处理还是降低功耗。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '优化视频质量' },
          { value: 1, label: '平衡' },
          { value: 2, label: '优化节能' }
        ],
        defaults: { balanced: [0, 1], high: [0, 0], ultimate: [0, 0], saver: [2, 2] }
      }
    ]
  },
  {
    id: '0d7dbae2-4294-402a-ba8e-26777e8488cd',
    name: '桌面背景设置',
    icon: '&#xE771;',
    settings: [
      {
        id: '309dce9b-bef4-4119-9921-a851fb12f0f4',
        name: '幻灯片放映',
        desc: '控制桌面壁纸幻灯片放映在当前供电模式下是否继续切换。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '可用' },
          { value: 1, label: '已暂停' }
        ],
        defaults: { balanced: [0, 1], high: [0, 0], ultimate: [0, 0], saver: [1, 1] }
      }
    ]
  },
  {
    id: '02f815b5-a5cf-4c84-bf20-649d1f75d3d8',
    name: 'Internet Explorer',
    icon: '&#xE774;',
    settings: [
      {
        id: '4c793e7d-a264-42e1-87d3-7a0d2f523ccd',
        name: 'JavaScript 计时器频率',
        desc: '传统浏览器计时器分辨率设置，节能优先会降低后台计时精度。',
        hidden: false,
        kind: 'choice',
        options: [
          { value: 0, label: '最大电源节省量' },
          { value: 1, label: '最高性能' }
        ],
        defaults: { balanced: [1, 0], high: [1, 1], ultimate: [1, 1], saver: [0, 0] }
      }
    ]
  },
  {
    id: '5fb4938d-1ee8-4b0f-9a3c-5036b0ab995c',
    name: '图形设置',
    icon: '&#xE7F4;',
    settings: [
      {
        id: 'dd848b2a-8a5d-4451-9ae2-39cd41658f6c',
        name: 'GPU 首选项策略',
        desc: '多显卡环境中混合图形驱动默认偏好的性能策略。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '节能' },
          { value: 1, label: '正常' },
          { value: 2, label: '高性能' }
        ],
        defaults: { balanced: [1, 0], high: [2, 1], ultimate: [2, 2], saver: [0, 0] }
      }
    ]
  },
  {
    id: 'fea3413e-7e05-4911-9a71-700331f1c294',
    name: '常规系统电源',
    icon: '&#xE713;',
    settings: [
      {
        id: '0e796bdb-100d-47d6-a2d5-f7d2daa51f51',
        name: '唤醒时需要密码',
        desc: '计算机从睡眠或待机恢复时是否要求重新输入登录密码。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '否' },
          { value: 1, label: '是' }
        ],
        defaults: { balanced: [1, 1], high: [1, 1], ultimate: [1, 1], saver: [1, 1] }
      },
      {
        id: 'f15576e8-98b7-4186-b944-eafa664402d9',
        name: '待机状态下的网络连接性',
        desc: '控制 S0 现代待机期间是否允许保持后台网络连接。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '禁用' },
          { value: 1, label: '启用' },
          { value: 2, label: '由 Windows 管理' }
        ],
        defaults: { balanced: [1, 1], high: [1, 1], ultimate: [1, 1], saver: [0, 0] }
      }
    ]
  },
  {
    id: 'de830923-a562-41af-a086-e3a2c6bad2da',
    name: '节能模式设置',
    icon: '&#xEC0A;',
    settings: [
      {
        id: '5c5bb349-ad29-4ee2-9d0b-2b25270f7a81',
        name: '节能模式策略',
        desc: '开启节电模式时采取适度节能或激进节能限制策略。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '常规节能' },
          { value: 1, label: '激进节能' }
        ],
        defaults: { balanced: [0, 0], high: [0, 0], ultimate: [0, 0], saver: [1, 1] }
      }
    ]
  },
  {
    id: '2e601130-5351-4d9d-8e04-252966bad054',
    name: '空闲复原',
    icon: '&#xE708;',
    settings: [
      {
        id: 'd502f7ee-1dc7-4efd-a55d-f04b6f5c0545',
        name: '已启用/禁用深度睡眠',
        desc: '控制平台处理器包与总线空闲复原策略（深度睡眠开关）。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '已禁用' },
          { value: 1, label: '已启用' }
        ],
        defaults: { balanced: [1, 1], high: [0, 1], ultimate: [0, 0], saver: [1, 1] }
      },
      {
        id: 'c36f0eb4-2988-4a70-8eee-0884fc2c2433',
        name: 'IO 合并超时时间',
        desc: '磁盘定时器合并唤醒窗口的延迟上限（毫秒）。',
        hidden: true,
        kind: 'range',
        min: 0,
        max: 4294967295,
        step: 50,
        unit: '毫秒',
        defaults: { balanced: [50, 50], high: [0, 50], ultimate: [0, 0], saver: [100, 100] }
      }
    ]
  },
  {
    id: '48672f38-7a9a-4bb2-8bf8-3d85be19de4e',
    name: '中断路由控制设置',
    icon: '&#xE964;',
    settings: [
      {
        id: '2bfc24f9-5ea2-4801-8213-3dbae01aa39d',
        name: '中断路由控制模式',
        desc: '控制硬件中断是否集中到特定活动处理器核心以减少唤醒抖动。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '默认' },
          { value: 1, label: '任何未休止处理器' },
          { value: 2, label: '锁定时处理器 0' },
          { value: 6, label: '由电源引擎自动均衡' }
        ],
        defaults: { balanced: [0, 0], high: [0, 0], ultimate: [0, 0], saver: [1, 1] }
      }
    ]
  },
  {
    id: '8619b916-e004-4dd8-9b66-dae86f806698',
    name: '状态感知电源操作',
    icon: '&#xE823;',
    settings: [
      {
        id: '82011705-fb95-4d46-8d35-4042b1d20def',
        name: '用户状态预测模式',
        desc: '在支持现代待机或存在传感器的设备上配置用户离开状态预测。',
        hidden: true,
        kind: 'choice',
        options: [
          { value: 0, label: '关闭' },
          { value: 1, label: '打开' }
        ],
        defaults: { balanced: [1, 1], high: [0, 0], ultimate: [0, 0], saver: [1, 1] }
      }
    ]
  }
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
    assignedVms: ['test', 'win11-arm64'],
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
  overviewResource: 'env',
  selectedLedVm: null,
  hoveredLedVm: null,
  hoveredLedLp: null,
  selectedMemKey: null,
  hoveredMemKey: null,
  hoveredMemSeg: null,
  selectedGpuKey: null,
  hoveredGpuKey: null,
  selectedStorageKey: null,
  hoveredStorageKey: null,
  selectedTopoNode: null,
  hoveredTopoNode: null,
  suppressedHoverTopoNode: null,
  topoPanX: 0,
  topoPanY: 0,
  topoZoom: 1,
  topoAutoFit: true,
  topoFullscreen: false,
  activeTab: 'overview',
  policySub: 'scheduler',
  powerComboOpen: false,
  powerOpenSettingCombo: null,
  powerShowHidden: false,
  powerSearch: '',
  powerHasBattery: true,
  powerExpandedGroups: new Set([
    '54533251-82be-4824-96c1-47b60b740d00',
    '501a4d13-42af-4429-9fd1-a8218c268e20',
    '0012ee47-9041-4b5d-9b77-535fba8b1442',
    '238c9fa8-0aad-41ed-83f4-97be242c8f20'
  ]),
  powerValues: {},
  selectedRail: { kind: 'cpu', idx: 0 }, // 'cpu' | 'mem' | 'nic' | 'switch' | 'gpu' | 'disk'

  hyperVServiceState: 'ready',
  hyperVEnabled: true,
  cpuVirtOk: true,
  gpuPvOk: true,
  iommuOk: true,
  isWindowsServer: false,
  isLive: false,
  liveVms: [],
  powerPlans: [],
  affinityProcs: [],

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
  vmGroups: structuredClone(INITIAL_VM_GROUPS),
  liveHostThreadReport: null
};

hostState.draft = structuredClone(hostState.saved);

const DEMO_HOST_SNAPSHOT = {
  sys: structuredClone(HOST_SYS),
  saved: structuredClone(hostState.saved),
  nics: structuredClone(INITIAL_NICS),
  switches: structuredClone(INITIAL_SWITCHES),
  gpus: structuredClone(INITIAL_GPUS),
  disks: structuredClone(INITIAL_DISKS),
  pcieDevices: structuredClone(INITIAL_PCIE),
  cpuGroups: structuredClone(INITIAL_CPU_GROUPS),
  vmGroups: structuredClone(INITIAL_VM_GROUPS),
  hyperVServiceState: 'ready',
  hyperVEnabled: true,
  cpuVirtOk: true,
  gpuPvOk: true,
  iommuOk: true,
  isWindowsServer: false
};

let hostActionHandler = null;

function dispatchHostMutation(action, payload, applyDemoFallback, successNotice) {
  if (typeof hostActionHandler === 'function') {
    Promise.resolve(hostActionHandler(action, payload))
      .then((handled) => {
        if (handled === false) {
          applyDemoFallback?.();
        } else if (successNotice) {
          hostState.notice = successNotice;
        }
        renderHostPage();
      })
      .catch((err) => {
        hostState.notice = {
          severity: 'error',
          text: err?.message || '宿主机操作失败'
        };
        renderHostPage();
      });
    return;
  }
  applyDemoFallback?.();
  if (successNotice) hostState.notice = successNotice;
  renderHostPage();
}

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
  const rawUsed = hostState.isLive ? Number(HOST_SYS.usedMemoryGb || 0) : Number(HOST_SYS.usedMemoryGb) || totalGb * 0.55;
  const inUseGb = Number(Math.min(totalGb - 0.1, Math.max(0.1, rawUsed)).toFixed(1));
  const freeGb = Number(Math.max(0.1, totalGb - inUseGb).toFixed(1));

  let sortedVms = [];
  let vmSumGb = 0;

  if (hostState.isLive) {
    const liveRunning = (hostState.liveVms || []).filter((v) => v.running);
    sortedVms = liveRunning
      .map((vm) => {
        const assignedGb = Number(Math.max(0.1, vm.memoryGb || 0).toFixed(1));
        const isDyn = Boolean(vm.memoryRaw?.DynamicMemoryEnabled);
        return {
          key: `vm:${vm.name}`,
          name: vm.name,
          modeText: vm.memoryRaw ? (isDyn ? '动态内存' : '固定内存') : '已分配内存',
          locked: !isDyn,
          dynamic: isDyn,
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
    vmSumGb = Number(sortedVms.reduce((s, item) => s + item.assignedGb, 0).toFixed(1));
  } else {
    const vmCount = Math.max(1, Number(hostState.mockVmCount) || HOST_SYS.runningVmCount || 2);
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
    sortedVms = weightTable
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
  }

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
// Host GPU Overview (Per-GPU Total vs Host & VM 4-Engine Breakdown)
// ============================================================================
function buildHostGpuBreakdown() {
  if (hostState.isLive) {
    const runningVms = (hostState.liveVms || []).filter((v) => v.running);
    const vmColorByName = new Map(
      runningVms.map((v, i) => [v.name, i < HOST_LED_VM_PALETTE.length ? HOST_LED_VM_PALETTE[i] : HOST_LED_OTHER_COLOR])
    );
    const cards = (hostState.gpus || []).map((gpu, gpuIdx) => {
      const totalD3 = Math.round(gpu.pts3d?.at(-1) ?? gpu.pct ?? 0);
      const totalCopy = Math.round(gpu.ptsCopy?.at(-1) ?? 0);
      const totalEnc = Math.round(gpu.ptsEnc?.at(-1) ?? 0);
      const totalDec = Math.round(gpu.ptsDec?.at(-1) ?? 0);
      const assignedList = Array.isArray(gpu.assignedVms) ? gpu.assignedVms : [];
      const consumers = [];
      let vmD3Sum = 0;
      let vmCopySum = 0;
      let vmEncSum = 0;
      let vmDecSum = 0;

      assignedList.forEach((vmName, idx) => {
        const v = (hostState.liveVms || []).find((x) => x.name === vmName);
        if (!v || !v.running) return;
        const tel = v.telemetry || {};
        const d3 = Math.round(tel.gpu3dPct || 0);
        const copy = Math.round(tel.gpuCopyPct || 0);
        const enc = Math.round(tel.gpuEncPct || 0);
        const dec = Math.round(tel.gpuDecPct || 0);
        vmD3Sum += d3;
        vmCopySum += copy;
        vmEncSum += enc;
        vmDecSum += dec;
        consumers.push({
          key: `vm:${vmName}`,
          name: vmName,
          kind: 'vm',
          roleTag: 'GPU-PV 分区',
          color: vmColorByName.get(vmName) || (idx < HOST_LED_VM_PALETTE.length ? HOST_LED_VM_PALETTE[idx] : HOST_LED_OTHER_COLOR),
          engines: { d3, copy, enc, dec },
          peakPct: Math.max(d3, copy, enc, dec),
          active: true
        });
      });

      const hostEngines = {
        d3: Math.max(0, totalD3 - vmD3Sum),
        copy: Math.max(0, totalCopy - vmCopySum),
        enc: Math.max(0, totalEnc - vmEncSum),
        dec: Math.max(0, totalDec - vmDecSum)
      };
      consumers.unshift({
        key: '__host__',
        name: '宿主',
        kind: 'host',
        roleTag: '宿主桌面与系统合成',
        color: HOST_LED_HOST_COLOR,
        engines: hostEngines,
        peakPct: Math.max(hostEngines.d3, hostEngines.copy, hostEngines.enc, hostEngines.dec),
        active: true
      });

      const engines = [
        { id: 'd3', label: '3D 渲染', total: Math.min(100, Math.max(totalD3, vmD3Sum)) },
        { id: 'copy', label: '数据复制', total: Math.min(100, Math.max(totalCopy, vmCopySum)) },
        { id: 'enc', label: '视频编码', total: Math.min(100, Math.max(totalEnc, vmEncSum)) },
        { id: 'dec', label: '视频解码', total: Math.min(100, Math.max(totalDec, vmDecSum)) }
      ].map((eng) => ({
        ...eng,
        slices: consumers
          .filter((c) => (c.engines[eng.id] || 0) > 0)
          .map((c) => ({ key: c.key, name: c.name, color: c.color, val: c.engines[eng.id] || 0 }))
      }));

      const totalPct = Math.max(...engines.map((e) => e.total), 0);
      const totalWeight = consumers.reduce((s, c) => s + c.peakPct, 0) || 1;
      const shareBar = consumers.map((c) => ({
        key: c.key,
        name: c.name,
        color: c.color,
        pctOfCard: c.peakPct,
        shareRatio: (c.peakPct / totalWeight) * totalPct
      }));

      return { gpuIdx, gpu, totalPct, consumers, engines, shareBar };
    });

    const primary = cards[0];
    const hostItem = primary
      ? primary.consumers.find((c) => c.key === '__host__')
      : { key: '__host__', name: '宿主', color: HOST_LED_HOST_COLOR, peakPct: 0 };
    const vmItems = primary ? primary.consumers.filter((c) => c.kind === 'vm') : [];
    return { cards, hostItem, vmItems, orderedItems: [hostItem, ...vmItems] };
  }

  const vmCount = Math.max(1, Number(hostState.mockVmCount) || HOST_SYS.runningVmCount || 2);
  const vms = Array.from({ length: vmCount }, (_, i) => ({
    key: `vm:${HOST_LED_MOCK_NAMES[i] || `vm-node-${i + 1}`}`,
    name: HOST_LED_MOCK_NAMES[i] || `vm-node-${i + 1}`,
    color: i < HOST_LED_VM_PALETTE.length ? HOST_LED_VM_PALETTE[i] : HOST_LED_OTHER_COLOR
  }));

  const cards = (hostState.gpus || []).map((gpu, gpuIdx) => {
    const hostEngines = { d3: 4, copy: 1, enc: 0, dec: 1 };
    const consumers = [
      {
        key: '__host__',
        name: '宿主',
        kind: 'host',
        roleTag: '宿主桌面与系统合成',
        color: HOST_LED_HOST_COLOR,
        engines: hostEngines,
        peakPct: Math.max(hostEngines.d3, hostEngines.copy, hostEngines.enc, hostEngines.dec),
        active: true
      }
    ];

    if (gpu.partitionable) {
      const activePool = vms.slice(0, Math.min(vms.length, Math.max(2, vmCount)));
      activePool.forEach((vm, idx) => {
        const d3 = idx === 0 ? 6 : idx === 1 ? 4 : Math.max(0, 3 - (idx % 3));
        const copy = idx === 0 ? 2 : idx === 1 ? 1 : idx % 2 === 0 ? 1 : 0;
        const enc = idx === 0 ? 2 : 0;
        const dec = idx === 0 ? 2 : idx === 1 ? 5 : idx % 2 === 1 ? 2 : 0;
        consumers.push({
          key: vm.key,
          name: vm.name,
          kind: 'vm',
          roleTag: 'GPU-PV 分区',
          color: vm.color,
          engines: { d3, copy, enc, dec },
          peakPct: Math.max(d3, copy, enc, dec),
          active: true
        });
      });
    }

    const sumEngine = (prop) => Math.min(100, consumers.reduce((s, c) => s + (c.engines[prop] || 0), 0));
    const engines = [
      { id: 'd3', label: '3D 渲染', total: sumEngine('d3') },
      { id: 'copy', label: '数据复制', total: sumEngine('copy') },
      { id: 'enc', label: '视频编码', total: sumEngine('enc') },
      { id: 'dec', label: '视频解码', total: sumEngine('dec') }
    ].map((eng) => ({
      ...eng,
      slices: consumers
        .filter((c) => (c.engines[eng.id] || 0) > 0)
        .map((c) => ({
          key: c.key,
          name: c.name,
          color: c.color,
          val: c.engines[eng.id] || 0
        }))
    }));

    const totalPct = Math.max(...engines.map((e) => e.total), 0);
    const totalWeight = consumers.reduce((s, c) => s + c.peakPct, 0) || 1;
    const shareBar = consumers.map((c) => ({
      key: c.key,
      name: c.name,
      color: c.color,
      pctOfCard: c.peakPct,
      shareRatio: (c.peakPct / totalWeight) * totalPct
    }));

    return {
      gpuIdx,
      gpu,
      totalPct,
      consumers,
      engines,
      shareBar
    };
  });

  const primary = cards[0];
  const hostItem = primary
    ? primary.consumers.find((c) => c.key === '__host__')
    : { key: '__host__', name: '宿主', color: HOST_LED_HOST_COLOR, peakPct: 0 };
  const vmItems = primary ? primary.consumers.filter((c) => c.kind === 'vm') : [];

  return {
    cards,
    hostItem,
    vmItems,
    orderedItems: [hostItem, ...vmItems]
  };
}

function renderHostGpuLegendSidebar(gpuData, focusKey) {
  const makeChip = (it) => `
    <button type="button"
      class="host-led-chip host-mem-side-chip ${hostState.selectedGpuKey === it.key ? 'is-selected' : ''} ${focusKey === it.key ? 'is-hovered' : ''}"
      data-gpu-key="${esc(it.key)}"
      title="${esc(it.name)} (峰值引擎 ${it.peakPct}%)"
      style="--led-color:${it.color};">
      <span class="host-led-swatch"></span>
      <span class="host-led-chip-name">${esc(it.name)}</span>
      <span class="host-mem-chip-gb">${it.peakPct}%</span>
    </button>
  `;

  return `
    <div class="host-rel-summary-box">
      <div class="host-led-pin-host">
        ${makeChip(gpuData.hostItem)}
      </div>
      <div class="host-led-legend host-led-vm-scroll">
        ${gpuData.vmItems.length ? gpuData.vmItems.map((v) => makeChip(v)).join('') : '<div class="host-mem-empty-hint">无已分配 GPU-PV 虚拟机</div>'}
      </div>
    </div>
  `;
}

function renderHostGpuOverview(gpuData, focusKey) {
  const focusItem = focusKey ? gpuData.orderedItems.find((it) => it.key === focusKey) : null;

  const cardsHtml = gpuData.cards
    .map((card) => {
      const focusedConsumer = focusKey ? card.consumers.find((c) => c.key === focusKey) : null;
      const headVal = focusedConsumer ? `${focusedConsumer.peakPct}%` : `${card.totalPct}%`;
      const headSub = focusedConsumer ? `${focusedConsumer.name} 峰值引擎` : '物理总负载';
      const headColor = focusedConsumer ? focusedConsumer.color : '';

      const composeSegs = card.shareBar
        .filter((s) => s.shareRatio > 0)
        .map((s) => {
          const isFocused = focusKey === s.key;
          const isDimmed = Boolean(focusKey) && !isFocused;
          return `<div class="host-share-seg ${isFocused ? 'is-focused' : ''} ${isDimmed ? 'is-dimmed' : ''}" data-gpu-key="${esc(s.key)}" style="width:${Math.max(1.5, s.shareRatio).toFixed(2)}%;background:${s.color};" title="${esc(s.name)} ${s.pctOfCard}%"></div>`;
        })
        .join('');

      const enginesHtml = card.engines
        .map((eng) => {
          const focusedSlice = focusKey ? eng.slices.find((s) => s.key === focusKey) : null;
          const engDisplayPct = focusKey ? (focusedSlice ? focusedSlice.val : 0) : eng.total;
          const engBars = eng.slices
            .map((s) => {
              const isFocused = focusKey === s.key;
              const isDimmed = Boolean(focusKey) && !isFocused;
              return `<div class="host-share-seg ${isFocused ? 'is-focused' : ''} ${isDimmed ? 'is-dimmed' : ''}" data-gpu-key="${esc(s.key)}" style="width:${Math.max(1.5, s.val).toFixed(2)}%;background:${s.color};" title="${esc(s.name)} ${s.val}%"></div>`;
            })
            .join('');
          const miniTags = eng.slices.length
            ? eng.slices
                .map((s) => {
                  const isFocused = focusKey === s.key;
                  const isDimmed = Boolean(focusKey) && !isFocused;
                  return `<span class="host-engine-tag ${isFocused ? 'is-focused' : ''} ${isDimmed ? 'is-dimmed' : ''}" data-gpu-key="${esc(s.key)}"><span class="host-engine-dot" style="background:${s.color};"></span><span>${esc(s.name)}</span><span class="host-engine-num">${s.val}%</span></span>`;
                })
                .join('')
            : '<span class="host-engine-empty">当前空闲 (0%)</span>';

          return `
            <div class="host-engine-tile">
              <div class="host-engine-head">
                <span class="host-engine-name">${esc(eng.label)}</span>
                <span class="host-engine-val" style="${focusedSlice ? `color:${focusedSlice.color};` : ''}">${engDisplayPct}%</span>
              </div>
              <div class="host-share-bar is-engine">${engBars}</div>
              <div class="host-engine-tags">${miniTags}</div>
            </div>
          `;
        })
        .join('');

      const consumerRows = card.consumers
        .map((c) => {
          const isFocused = focusKey === c.key;
          const isDimmed = Boolean(focusKey) && !isFocused;
          return `
            <button type="button" class="host-share-row ${isFocused ? 'is-focused' : ''} ${isDimmed ? 'is-dimmed' : ''}" data-gpu-key="${esc(c.key)}">
              <div class="host-share-row-left">
                <span class="host-share-bar-swatch" style="background:${c.color};"></span>
                <span class="host-share-row-name">${esc(c.name)}</span>
                <span class="vm-badge is-mini"><span class="vm-badge-text">${esc(c.roleTag)}</span></span>
              </div>
              <div class="host-share-row-metrics">
                <span class="host-io-pill">3D <b>${c.engines.d3}%</b></span>
                <span class="host-io-pill">复制 <b>${c.engines.copy}%</b></span>
                <span class="host-io-pill">编码 <b>${c.engines.enc}%</b></span>
                <span class="host-io-pill">解码 <b>${c.engines.dec}%</b></span>
              </div>
            </button>
          `;
        })
        .join('');

      return `
        <div class="host-share-card">
          <div class="host-share-card-head">
            <div class="host-share-card-title-wrap">
              <div class="host-share-icon-box"><span class="fi">&#xF211;</span></div>
              <div class="host-share-title-col">
                <div class="host-share-title-line">
                  <span class="host-share-title">GPU ${card.gpuIdx}</span>
                  <span class="host-share-model">${esc(card.gpu.name)}</span>
                </div>
                <div class="host-share-badges">
                  <span class="vm-badge is-mini"><span class="vm-badge-text">${card.gpu.partitionable ? 'GPU-PV 可分区' : '不支持分区'}</span></span>
                  <span class="vm-badge is-mini"><span class="vm-badge-text">专用显存 ${esc(card.gpu.vram)}</span></span>
                  ${card.gpu.partitionable ? `<span class="vm-badge is-mini"><span class="vm-badge-text">${card.consumers.length - 1} 台共享 VM</span></span>` : ''}
                </div>
              </div>
            </div>
            <div class="host-share-reading-col">
              <span class="host-share-reading" style="${headColor ? `color:${headColor};` : ''}">${headVal}</span>
              <span class="host-share-reading-sub">${esc(headSub)}</span>
            </div>
          </div>

          <div class="host-share-bar-section">
            <div class="host-share-bar-label">
              <span>物理显卡能力分摊（0 - 100%）</span>
              <span>${focusItem ? `${esc(focusItem.name)} 占用 ${focusedConsumer ? focusedConsumer.peakPct : 0}%` : `总占用 ${card.totalPct}%`}</span>
            </div>
            <div class="host-share-bar is-hero">${composeSegs}</div>
          </div>

          <div class="host-engine-grid">${enginesHtml}</div>

          <div class="host-share-rows">${consumerRows}</div>
        </div>
      `;
    })
    .join('');

  return `
    <div class="host-rel-wrap is-legend-right">
      <div class="host-share-stage">${cardsHtml}</div>
      ${renderHostGpuLegendSidebar(gpuData, focusKey)}
    </div>
  `;
}

// ============================================================================
// Host Storage Overview (Per-Physical-Disk Host vs VM Read / Write / IOPS / Q)
// ============================================================================
function parseStorageRateMb(rateStr) {
  const m = String(rateStr || '0 KB/s').trim().match(/^([\d.]+)\s*(KB\/s|MB\/s|GB\/s)$/i);
  if (!m) return 0;
  const val = parseFloat(m[1]) || 0;
  const unit = m[2].toUpperCase();
  if (unit === 'GB/S') return val * 1024;
  if (unit === 'KB/S') return val / 1024;
  return val;
}

function formatStorageRateMb(mb) {
  const val = Math.max(0, Number(mb) || 0);
  if (val <= 0.005) return '0 KB/s';
  if (val < 1) return `${Math.round(val * 1024)} KB/s`;
  return `${val.toFixed(1)} MB/s`;
}

function buildHostStorageBreakdown() {
  if (hostState.isLive) {
    const diskTotalsByKey = new Map();
    const addTotal = (key, meta, mb) => {
      const prev = diskTotalsByKey.get(key) || { ...meta, totalMb: 0 };
      prev.totalMb = Number((prev.totalMb + mb).toFixed(2));
      diskTotalsByKey.set(key, prev);
    };
    addTotal('__host__', { key: '__host__', name: '宿主', color: HOST_LED_HOST_COLOR }, 0);

    const cards = (hostState.disks || []).map((disk) => {
      const totalRead = parseStorageRateMb(disk.readRate);
      const totalWrite = parseStorageRateMb(disk.writeRate);
      const totalIo = Number((totalRead + totalWrite).toFixed(2));
      const consumers = [];
      if (!disk.offline) {
        consumers.push({
          key: '__host__',
          name: '宿主',
          kind: 'host',
          roleTag: '宿主存储吞吐',
          color: HOST_LED_HOST_COLOR,
          readMb: totalRead,
          writeMb: totalWrite,
          totalMb: totalIo,
          iops: 0,
          queue: 0
        });
        addTotal('__host__', { key: '__host__', name: '宿主', color: HOST_LED_HOST_COLOR }, totalIo);
      }
      return { disk, totalRead, totalWrite, totalIo, activeTotal: totalIo, consumers };
    });

    const hostItem = diskTotalsByKey.get('__host__');
    return { cards, hostItem, vmItems: [], orderedItems: [hostItem] };
  }

  const vmCount = Math.max(1, Number(hostState.mockVmCount) || HOST_SYS.runningVmCount || 2);
  const vms = Array.from({ length: vmCount }, (_, i) => ({
    key: `vm:${HOST_LED_MOCK_NAMES[i] || `vm-node-${i + 1}`}`,
    name: HOST_LED_MOCK_NAMES[i] || `vm-node-${i + 1}`,
    color: i < HOST_LED_VM_PALETTE.length ? HOST_LED_VM_PALETTE[i] : HOST_LED_OTHER_COLOR
  }));

  const diskTotalsByKey = new Map();
  const addTotal = (key, meta, mb) => {
    const prev = diskTotalsByKey.get(key) || { ...meta, totalMb: 0 };
    prev.totalMb = Number((prev.totalMb + mb).toFixed(2));
    diskTotalsByKey.set(key, prev);
  };
  addTotal('__host__', { key: '__host__', name: '宿主', color: HOST_LED_HOST_COLOR }, 0);
  vms.forEach((vm) => addTotal(vm.key, vm, 0));

  const cards = (hostState.disks || []).map((disk) => {
    const totalRead = parseStorageRateMb(disk.readRate);
    const totalWrite = parseStorageRateMb(disk.writeRate);
    const totalIo = Number((totalRead + totalWrite).toFixed(2));

    const consumers = [];
    if (!disk.offline && disk.number === 0) {
      const hostRead = Number((totalRead * 0.445).toFixed(1));
      const hostWrite = Number((totalWrite * 0.42).toFixed(1));
      const hostTotal = Number((hostRead + hostWrite).toFixed(1));
      consumers.push({
        key: '__host__',
        name: '宿主',
        kind: 'host',
        roleTag: '宿主系统与文件 IO',
        color: HOST_LED_HOST_COLOR,
        readMb: hostRead,
        writeMb: hostWrite,
        totalMb: hostTotal,
        iops: 420,
        queue: 1
      });
      addTotal('__host__', { key: '__host__', name: '宿主', color: HOST_LED_HOST_COLOR }, hostTotal);

      let remRead = Number((totalRead - hostRead).toFixed(1));
      let remWrite = Number((totalWrite - hostWrite).toFixed(1));
      vms.forEach((vm, idx) => {
        const isLast = idx === vms.length - 1;
        const factor = idx === 0 ? 0.66 : Math.max(0.15, 0.34 / Math.max(1, vms.length - 1));
        const r = isLast ? Math.max(0, Number(remRead.toFixed(1))) : Number((remRead * factor).toFixed(1));
        const w = isLast ? Math.max(0, Number(remWrite.toFixed(1))) : Number((remWrite * factor).toFixed(1));
        if (!isLast) {
          remRead = Math.max(0, Number((remRead - r).toFixed(1)));
          remWrite = Math.max(0, Number((remWrite - w).toFixed(1)));
        }
        const sum = Number((r + w).toFixed(1));
        consumers.push({
          key: vm.key,
          name: vm.name,
          kind: 'vm',
          roleTag: idx % 2 === 0 ? '动态 VHDX' : '固定 VHDX',
          color: vm.color,
          readMb: r,
          writeMb: w,
          totalMb: sum,
          iops: Math.max(35, Math.round(sum * 34)),
          queue: sum >= 6 ? 1 : 0
        });
        addTotal(vm.key, vm, sum);
      });
    }

    const activeTotal = consumers.reduce((s, c) => s + c.totalMb, 0);
    return {
      disk,
      totalRead,
      totalWrite,
      totalIo,
      activeTotal,
      consumers
    };
  });

  const hostItem = diskTotalsByKey.get('__host__');
  const vmItems = vms.map((v) => diskTotalsByKey.get(v.key)).filter(Boolean);

  return {
    cards,
    hostItem,
    vmItems,
    orderedItems: [hostItem, ...vmItems]
  };
}

function renderHostStorageLegendSidebar(storageData, focusKey) {
  const makeChip = (it) => `
    <button type="button"
      class="host-led-chip host-mem-side-chip ${hostState.selectedStorageKey === it.key ? 'is-selected' : ''} ${focusKey === it.key ? 'is-hovered' : ''}"
      data-storage-key="${esc(it.key)}"
      title="${esc(it.name)} (${formatStorageRateMb(it.totalMb)})"
      style="--led-color:${it.color};">
      <span class="host-led-swatch"></span>
      <span class="host-led-chip-name">${esc(it.name)}</span>
      <span class="host-mem-chip-gb">${it.totalMb > 0 ? `${it.totalMb.toFixed(1)}M` : '0'}</span>
    </button>
  `;

  return `
    <div class="host-rel-summary-box">
      <div class="host-led-pin-host">
        ${makeChip(storageData.hostItem)}
      </div>
      <div class="host-led-legend host-led-vm-scroll">
        ${storageData.vmItems.map((v) => makeChip(v)).join('')}
      </div>
    </div>
  `;
}

function renderHostStorageOverview(storageData, focusKey) {
  const cardsHtml = storageData.cards
    .map((card) => {
      const { disk, consumers, activeTotal } = card;
      const focusedConsumer = focusKey ? consumers.find((c) => c.key === focusKey) : null;
      const headReading = focusedConsumer
        ? `${formatStorageRateMb(focusedConsumer.readMb)} / ${formatStorageRateMb(focusedConsumer.writeMb)}`
        : `${formatStorageRateMb(card.totalRead)} / ${formatStorageRateMb(card.totalWrite)}`;
      const headSub = focusedConsumer
        ? `${focusedConsumer.name} 读 / 写`
        : disk.offline
          ? '脱机空闲'
          : '物理盘总 读 / 写';
      const headColor = focusedConsumer ? focusedConsumer.color : '';

      const composeHtml = consumers.length
        ? consumers
            .map((c) => {
              const pct = activeTotal > 0 ? (c.totalMb / activeTotal) * 100 : 0;
              const isFocused = focusKey === c.key;
              const isDimmed = Boolean(focusKey) && !isFocused;
              return `<div class="host-share-seg ${isFocused ? 'is-focused' : ''} ${isDimmed ? 'is-dimmed' : ''}" data-storage-key="${esc(c.key)}" style="width:${Math.max(2, pct).toFixed(2)}%;background:${c.color};" title="${esc(c.name)} ${pct.toFixed(0)}%"></div>`;
            })
            .join('')
        : '';

      const maxRowMb = Math.max(1, ...consumers.map((c) => c.totalMb));
      const rowsHtml = consumers.length
        ? consumers
            .map((c) => {
              const isFocused = focusKey === c.key;
              const isDimmed = Boolean(focusKey) && !isFocused;
              const readW = Math.round((c.readMb / maxRowMb) * 100);
              const writeW = Math.round((c.writeMb / maxRowMb) * 100);
              return `
                <button type="button" class="host-share-row is-storage ${isFocused ? 'is-focused' : ''} ${isDimmed ? 'is-dimmed' : ''}" data-storage-key="${esc(c.key)}">
                  <div class="host-share-row-left">
                    <span class="host-share-bar-swatch" style="background:${c.color};"></span>
                    <span class="host-share-row-name">${esc(c.name)}</span>
                    <span class="vm-badge is-mini"><span class="vm-badge-text">${esc(c.roleTag)}</span></span>
                  </div>
                  <div class="host-storage-bars">
                    <div class="host-rw-track" title="读 ${formatStorageRateMb(c.readMb)} / 写 ${formatStorageRateMb(c.writeMb)}">
                      <span class="host-rw-fill is-read" style="width:${Math.max(3, readW * 0.6)}%;background:${c.color};"></span>
                      <span class="host-rw-fill is-write" style="width:${Math.max(2, writeW * 0.4)}%;background:${c.color};opacity:0.55;"></span>
                    </div>
                  </div>
                  <div class="host-share-row-metrics">
                    <span class="host-io-pill">读 <b>${formatStorageRateMb(c.readMb)}</b></span>
                    <span class="host-io-pill">写 <b>${formatStorageRateMb(c.writeMb)}</b></span>
                    <span class="host-io-pill">IOPS <b>${c.iops}</b></span>
                    <span class="host-io-pill">队列 <b>${c.queue}</b></span>
                  </div>
                </button>
              `;
            })
            .join('')
        : `
          <div class="host-storage-idle-note">
            <span class="fi">&#xEDA2;</span>
            <span>${disk.canPassthrough ? '当前磁盘已脱机并处于可直通状态，尚未挂载到任何虚拟机。挂载后将由单台虚拟机独占访问，不与宿主共享。' : '当前无活动虚拟机挂载到此物理盘。'}</span>
          </div>
        `;

      const statusBadge = disk.offline
        ? '已脱机'
        : consumers.length > 1
          ? `宿主 + ${consumers.length - 1} 台 VM 共享`
          : '仅宿主使用';

      return `
        <div class="host-share-card">
          <div class="host-share-card-head">
            <div class="host-share-card-title-wrap">
              <div class="host-share-icon-box"><span class="fi">&#xEDA2;</span></div>
              <div class="host-share-title-col">
                <div class="host-share-title-line">
                  <span class="host-share-title">磁盘 ${disk.number}</span>
                  <span class="host-share-model">${esc(disk.name)}</span>
                </div>
                <div class="host-share-badges">
                  <span class="vm-badge is-mini"><span class="vm-badge-text">${esc(disk.sizeText)}</span></span>
                  <span class="vm-badge is-mini"><span class="vm-badge-text">${esc(statusBadge)}</span></span>
                  ${disk.canPassthrough ? '<span class="vm-badge is-mini"><span class="vm-badge-text">可直通独占</span></span>' : '<span class="vm-badge is-mini"><span class="vm-badge-text">系统盘</span></span>'}
                </div>
              </div>
            </div>
            <div class="host-share-reading-col">
              <span class="host-share-reading" style="${headColor ? `color:${headColor};` : ''}">${headReading}</span>
              <span class="host-share-reading-sub">${esc(headSub)}</span>
            </div>
          </div>

          ${
            composeHtml
              ? `
            <div class="host-share-bar-section">
              <div class="host-share-bar-label">
                <span>物理磁盘 IO 带宽分摊占比</span>
                <span>总吞吐 ${formatStorageRateMb(activeTotal)}</span>
              </div>
              <div class="host-share-bar is-hero">${composeHtml}</div>
            </div>
          `
              : ''
          }

          <div class="host-share-rows">${rowsHtml}</div>
        </div>
      `;
    })
    .join('');

  return `
    <div class="host-rel-wrap is-legend-right">
      <div class="host-share-stage">${cardsHtml}</div>
      ${renderHostStorageLegendSidebar(storageData, focusKey)}
    </div>
  `;
}

// ============================================================================
// Host Network Overview (Whole-Host Global Topology Diagram + Zoom/Pan/Full)
// ============================================================================
const VM_NET_PORT_MOCK_META = {
  'test::sw-default': { name: '以太网', tx: '140 Kbps', rx: '620 Kbps', vlan: '未标记' },
  'win11-arm64::sw-default': { name: '以太网', tx: '260 Kbps', rx: '980 Kbps', vlan: '未标记' },
  'win11-arm64::sw-iso': { name: '内网网卡', tx: '6.2 Mbps', rx: '6.4 Mbps', vlan: 'VLAN 10' },
  'ubuntu-dev::sw-ext': { name: 'ens33', tx: '0 Kbps', rx: '0 Kbps', vlan: '未标记' },
  'ubuntu-dev::sw-default': { name: 'ens34', tx: '0 Kbps', rx: '0 Kbps', vlan: '未标记' },
  'ubuntu-dev::sw-iso': { name: 'ens35', tx: '0 Kbps', rx: '0 Kbps', vlan: 'VLAN 10' }
};

function buildHostNetworkTopology() {
  const nics = (hostState.nics || []).filter((n) => n.linkUp);
  const switches = hostState.switches || [];

  const vmMetaByName = hostState.isLive
    ? new Map(
        (hostState.liveVms || []).map((v, i) => [
          v.name,
          {
            running: Boolean(v.running),
            color: i < HOST_LED_VM_PALETTE.length ? HOST_LED_VM_PALETTE[i] : HOST_LED_OTHER_COLOR,
            adapters: v.adapters || []
          }
        ])
      )
    : new Map(HOST_VM_CPU_ALLOCATIONS.map((v) => [v.name, v]));

  const vmPortsByName = new Map();
  switches.forEach((sw) => {
    (sw.endpoints || []).forEach((vmName) => {
      if (!vmPortsByName.has(vmName)) vmPortsByName.set(vmName, []);
      if (hostState.isLive) {
        const vmInfo = vmMetaByName.get(vmName) || { running: false, color: '#6CCB5F', adapters: [] };
        const matchedAdapters = (vmInfo.adapters || []).filter((a) => a.isConnected && a.subtitle === sw.name);
        const list = matchedAdapters.length > 0 ? matchedAdapters : [{ title: '网络适配器', sendRate: '0 Kbps', recvRate: '0 Kbps' }];
        list.forEach((a, i) => {
          vmPortsByName.get(vmName).push({
            id: `port:${vmName}:${sw.id}:${i}`,
            switchId: sw.id,
            switchName: sw.name,
            mode: sw.mode,
            name: a.title || a.name || '网络适配器',
            tx: vmInfo.running ? a.sendRate || '0 Kbps' : '0 Kbps',
            rx: vmInfo.running ? a.recvRate || '0 Kbps' : '0 Kbps',
            active: Boolean(vmInfo.running)
          });
        });
      } else {
        const meta = VM_NET_PORT_MOCK_META[`${vmName}::${sw.id}`] || {
          name: '虚拟网卡',
          tx: '0 Kbps',
          rx: '0 Kbps',
          vlan: '未标记'
        };
        const vmInfo = vmMetaByName.get(vmName) || { running: true, color: '#6CCB5F' };
        vmPortsByName.get(vmName).push({
          id: `port:${vmName}:${sw.id}`,
          switchId: sw.id,
          switchName: sw.name,
          mode: sw.mode,
          name: meta.name,
          tx: vmInfo.running ? meta.tx : '0 Kbps',
          rx: vmInfo.running ? meta.rx : '0 Kbps',
          active: Boolean(vmInfo.running)
        });
      }
    });
  });

  const colW = 252;
  const col1X = 32;
  const col2X = 440;
  const col3X = 848;
  const topPad = 24;
  const bottomPad = 24;

  const nicH = 88;
  const nicGap = 14;
  const swH = 92;
  const swGap = 18;
  const vmGap = 14;

  const hostPorts = switches
    .filter((sw) => sw.allowManagementOs)
    .map((sw) => ({
      id: `hostport:${sw.id}`,
      switchId: sw.id,
      switchName: sw.name,
      name: `vEthernet (${sw.name})`,
      ip: sw.ipv4 && sw.ipv4 !== '---' ? sw.ipv4 : '仅二层共享',
      active: true
    }));

  const rightEntries = [
    {
      nodeId: 'host-os',
      kind: 'host',
      name: '宿主 OS 端口',
      running: true,
      color: HOST_LED_HOST_COLOR,
      ports: hostPorts
    },
    ...Array.from(vmPortsByName.entries()).map(([vmName, ports]) => {
      const vmInfo = vmMetaByName.get(vmName) || { running: true, color: '#6CCB5F' };
      return {
        nodeId: `vm:${vmName}`,
        kind: 'vm',
        name: vmName,
        running: Boolean(vmInfo.running),
        color: vmInfo.color,
        ports
      };
    })
  ];

  rightEntries.forEach((entry) => {
    entry.height = 38 + Math.max(1, entry.ports.length) * 30 + 8;
  });

  const col1Total = nics.length * nicH + Math.max(0, nics.length - 1) * nicGap;
  const col2Total = switches.length * swH + Math.max(0, switches.length - 1) * swGap;
  const col3Total =
    rightEntries.reduce((s, r) => s + r.height, 0) + Math.max(0, rightEntries.length - 1) * vmGap;
  const stageSpan = Math.max(360, col1Total, col2Total, col3Total);
  const canvasW = 1132;
  const canvasH = topPad + stageSpan + bottomPad;

  const mediaIcons = {
    wired: '&#xE839;',
    wifi: '&#xE701;',
    cellular: '&#xEC3B;',
    tunnel: '&#xE705;'
  };
  const mediaLabels = {
    wired: '有线网卡',
    wifi: 'Wi-Fi',
    cellular: '蜂窝 5G',
    tunnel: '三层隧道'
  };

  let curY = topPad + Math.round((stageSpan - col1Total) / 2);
  const nicNodes = nics.map((nic) => {
    const node = {
      nodeId: `nic:${nic.id}`,
      nic,
      icon: mediaIcons[nic.media] || '&#xE774;',
      mediaLabel: mediaLabels[nic.media] || '网卡',
      x: col1X,
      y: curY,
      w: colW,
      h: nicH,
      portRightX: col1X + colW,
      portRightY: curY + Math.round(nicH / 2)
    };
    curY += nicH + nicGap;
    return node;
  });

  curY = topPad + Math.round((stageSpan - col2Total) / 2);
  const switchNodes = switches.map((sw) => {
    const modeColor = sw.mode === 'Bridge' ? '#3898EC' : sw.mode === 'NAT' ? '#00B7C3' : '#9D5CE6';
    const node = {
      nodeId: `sw:${sw.id}`,
      sw,
      modeLabel: sw.mode === 'Bridge' ? '外部桥接' : sw.mode === 'NAT' ? 'NAT (HCN)' : '仅内网隔离',
      modeColor,
      x: col2X,
      y: curY,
      w: colW,
      h: swH,
      portLeftX: col2X,
      portLeftY: curY + Math.round(swH / 2),
      portRightX: col2X + colW,
      portRightY: curY + Math.round(swH / 2)
    };
    curY += swH + swGap;
    return node;
  });

  curY = topPad + Math.round((stageSpan - col3Total) / 2);
  const rightNodes = rightEntries.map((entry) => {
    const ports = entry.ports.length
      ? entry.ports.map((p, idx) => ({
          ...p,
          anchorX: col3X,
          anchorY: curY + 38 + idx * 30 + 15
        }))
      : [];
    const node = {
      ...entry,
      x: col3X,
      y: curY,
      w: colW,
      h: entry.height,
      ports
    };
    curY += entry.height + vmGap;
    return node;
  });

  const edges = [];
  const primaryNic = nicNodes.find((n) => n.nic.media === 'wired') || nicNodes[0];

  // Upstream NIC -> Virtual Switch edges
  switchNodes.forEach((swNode) => {
    const { sw } = swNode;
    if (sw.mode === 'Bridge') {
      const matchedNic =
        nicNodes.find((n) => sw.upstream && sw.upstream.includes(n.nic.description)) || primaryNic;
      if (matchedNic) {
        edges.push({
          id: `edge:${matchedNic.nodeId}:${swNode.nodeId}`,
          fromId: matchedNic.nodeId,
          toId: swNode.nodeId,
          x1: matchedNic.portRightX,
          y1: matchedNic.portRightY,
          x2: swNode.portLeftX,
          y2: swNode.portLeftY,
          kind: 'bridge',
          color: swNode.modeColor,
          active: true,
          label: formatCompactRate(sw.sendRate, sw.recvRate, 'arrow-compact')
        });
      }
    } else if (sw.mode === 'NAT') {
      const natSources = nicNodes.filter((n) => n === primaryNic || n.nic.media === 'tunnel' || n.nic.media === 'cellular');
      natSources.forEach((nicNode, i) => {
        const yOffset = (i - (natSources.length - 1) / 2) * 14;
        edges.push({
          id: `edge:${nicNode.nodeId}:${swNode.nodeId}`,
          fromId: nicNode.nodeId,
          toId: swNode.nodeId,
          x1: nicNode.portRightX,
          y1: nicNode.portRightY,
          x2: swNode.portLeftX,
          y2: swNode.portLeftY + yOffset,
          kind: 'nat',
          color: swNode.modeColor,
          active: nicNode === primaryNic,
          label: nicNode === primaryNic ? 'NAT 默认出口' : nicNode.nic.media === 'tunnel' ? 'NAT 隧道路由' : 'NAT 备选'
        });
      });
    }
  });

  // Switch -> Right Host OS / VM Port edges (distribute switch right anchors smoothly)
  switchNodes.forEach((swNode) => {
    const targets = [];
    rightNodes.forEach((rNode) => {
      rNode.ports.forEach((p) => {
        if (p.switchId === swNode.sw.id) {
          targets.push({ rightNode: rNode, port: p });
        }
      });
    });

    targets.forEach((t, idx) => {
      const spread = targets.length > 1 ? ((idx / (targets.length - 1)) - 0.5) * 44 : 0;
      const isHostEdge = t.rightNode.kind === 'host';
      const isActive = isHostEdge || t.rightNode.running;
      edges.push({
        id: `edge:${swNode.nodeId}:${t.port.id}`,
        fromId: swNode.nodeId,
        toId: t.rightNode.nodeId,
        x1: swNode.portRightX,
        y1: Math.round(swNode.portRightY + spread),
        x2: t.port.anchorX,
        y2: t.port.anchorY,
        kind: isHostEdge ? 'host' : isActive ? 'vm-live' : 'vm-off',
        color: isHostEdge ? HOST_LED_HOST_COLOR : isActive ? swNode.modeColor : 'rgba(148, 163, 184, 0.45)',
        active: isActive,
        label: isHostEdge
          ? '宿主共享'
          : isActive
            ? formatCompactRate(t.port.tx, t.port.rx, 'arrow-compact')
            : '已关机'
      });
    });
  });

  const allBounds = [...nicNodes, ...switchNodes, ...rightNodes];
  const bounds = allBounds.length
    ? {
        minX: Math.min(...allBounds.map((n) => n.x)),
        maxX: Math.max(...allBounds.map((n) => n.x + n.w)),
        minY: Math.min(...allBounds.map((n) => n.y)),
        maxY: Math.max(...allBounds.map((n) => n.y + n.h))
      }
    : {
        minX: col1X,
        maxX: col3X + colW,
        minY: topPad,
        maxY: canvasH - bottomPad
      };

  return {
    canvasW,
    canvasH,
    bounds,
    col1X,
    col2X,
    col3X,
    colW,
    nicNodes,
    switchNodes,
    rightNodes,
    edges
  };
}

function computeHostTopoFocusSets(topo, focusId) {
  if (!focusId) return null;
  const activeNodes = new Set([focusId]);
  const activeEdges = new Set();

  if (focusId.startsWith('nic:')) {
    const adjSwitches = new Set();
    topo.edges.forEach((e) => {
      if (e.fromId === focusId) {
        activeEdges.add(e.id);
        activeNodes.add(e.toId);
        adjSwitches.add(e.toId);
      }
    });
    topo.edges.forEach((e) => {
      if (adjSwitches.has(e.fromId)) {
        activeEdges.add(e.id);
        activeNodes.add(e.toId);
      }
    });
  } else if (focusId.startsWith('sw:')) {
    topo.edges.forEach((e) => {
      if (e.fromId === focusId || e.toId === focusId) {
        activeEdges.add(e.id);
        activeNodes.add(e.fromId);
        activeNodes.add(e.toId);
      }
    });
  } else {
    const adjSwitches = new Set();
    topo.edges.forEach((e) => {
      if (e.toId === focusId) {
        activeEdges.add(e.id);
        activeNodes.add(e.fromId);
        adjSwitches.add(e.fromId);
      }
    });
    topo.edges.forEach((e) => {
      if (adjSwitches.has(e.toId)) {
        activeEdges.add(e.id);
        activeNodes.add(e.fromId);
      }
    });
  }

  return { activeNodes, activeEdges };
}

function getHostTopoFocusId() {
  const hoverId =
    hostState.hoveredTopoNode && hostState.hoveredTopoNode !== hostState.suppressedHoverTopoNode
      ? hostState.hoveredTopoNode
      : null;
  return hostState.selectedTopoNode || hoverId;
}

function applyHostTopoFocusDom(page) {
  const stage = page?.querySelector?.('#hostTopoStage');
  if (!stage) return;
  const focusId = getHostTopoFocusId();
  const topo = buildHostNetworkTopology();
  const focusSets = computeHostTopoFocusSets(topo, focusId);

  stage.querySelectorAll('[data-topo-node]').forEach((el) => {
    const id = el.getAttribute('data-topo-node');
    const isSelected = hostState.selectedTopoNode === id;
    const isFocused = Boolean(focusSets) && focusSets.activeNodes.has(id);
    const isDimmed = Boolean(focusSets) && !isFocused;
    el.classList.toggle('is-selected', isSelected);
    el.classList.toggle('is-focused', isFocused);
    el.classList.toggle('is-dimmed', isDimmed);
  });

  stage.querySelectorAll('[data-topo-edge]').forEach((el) => {
    const id = el.getAttribute('data-topo-edge');
    const isFocused = Boolean(focusSets) && focusSets.activeEdges.has(id);
    const isDimmed = Boolean(focusSets) && !isFocused;
    el.classList.toggle('is-focused', isFocused);
    el.classList.toggle('is-dimmed', isDimmed);
    if (isFocused && el.parentNode) {
      el.parentNode.appendChild(el);
    }
  });
}

function syncHostTopoViewport(page, forceAutoFit = false) {
  const stage = page?.querySelector?.('#hostTopoStage');
  const panLayer = page?.querySelector?.('#hostTopoPanLayer');
  const zoomBtn = page?.querySelector?.('#hostTopoZoomResetBtn');
  if (!stage || !panLayer) return;

  const canvasW = Number(panLayer.getAttribute('data-topo-w')) || 1132;
  const canvasH = Number(panLayer.getAttribute('data-topo-h')) || 420;
  const minX = Number(panLayer.getAttribute('data-topo-minx') ?? 32);
  const maxX = Number(panLayer.getAttribute('data-topo-maxx') ?? 1100);
  const minY = Number(panLayer.getAttribute('data-topo-miny') ?? 24);
  const maxY = Number(panLayer.getAttribute('data-topo-maxy') ?? Math.max(24, canvasH - 24));
  const sw = stage.clientWidth || 0;
  const sh = stage.clientHeight || 0;

  if ((hostState.topoAutoFit || forceAutoFit) && sw > 60 && sh > 60) {
    const boundsW = Math.max(1, maxX - minX);
    const boundsH = Math.max(1, maxY - minY);
    const fitZoom = Math.min((sw - 48) / boundsW, (sh - 48) / boundsH);
    const nextZoom = boundsW <= sw - 48 && boundsH <= sh - 48
      ? 1
      : Math.floor(Math.min(1, Math.max(0.45, fitZoom)) * 100) / 100;
    const centerX = (minX + maxX) / 2;
    const centerY = (minY + maxY) / 2;
    hostState.topoZoom = nextZoom;
    hostState.topoPanX = Math.round(sw / 2 - centerX * nextZoom);
    hostState.topoPanY = Math.round(sh / 2 - centerY * nextZoom);
    hostState.topoStageW = sw;
    hostState.topoStageH = sh;
  } else if (!hostState.topoAutoFit && sw > 0 && sh > 0) {
    if (hostState.topoStageW > 0 && hostState.topoStageH > 0) {
      hostState.topoPanX = (Number(hostState.topoPanX) || 0) + (sw - hostState.topoStageW) / 2;
      hostState.topoPanY = (Number(hostState.topoPanY) || 0) + (sh - hostState.topoStageH) / 2;
    }
    hostState.topoStageW = sw;
    hostState.topoStageH = sh;
  }

  const z = Number(hostState.topoZoom) || 1;
  const x = Number(hostState.topoPanX) || 0;
  const y = Number(hostState.topoPanY) || 0;
  panLayer.style.transform = `translate3d(${x}px, ${y}px, 0) scale(${z})`;
  if (zoomBtn) zoomBtn.textContent = `${Math.round(z * 100)}%`;
}

function renderHostNetworkOverview() {
  const topo = buildHostNetworkTopology();
  const focusId = getHostTopoFocusId();
  const focusSets = computeHostTopoFocusSets(topo, focusId);
  const isFullscreen = Boolean(hostState.topoFullscreen);

  const orderedEdges = [...topo.edges].sort((a, b) => {
    const af = Boolean(focusSets) && focusSets.activeEdges.has(a.id) ? 1 : 0;
    const bf = Boolean(focusSets) && focusSets.activeEdges.has(b.id) ? 1 : 0;
    return af - bf;
  });

  const wiresSvg = orderedEdges
    .map((e) => {
      const dx = Math.max(48, Math.abs(e.x2 - e.x1) * 0.48);
      const d = `M ${e.x1} ${e.y1} C ${e.x1 + dx} ${e.y1}, ${e.x2 - dx} ${e.y2}, ${e.x2} ${e.y2}`;
      const isFocused = Boolean(focusSets) && focusSets.activeEdges.has(e.id);
      const isDimmed = Boolean(focusSets) && !isFocused;
      const cls = ['host-topo-edge', `is-${e.kind}`, isFocused ? 'is-focused' : '', isDimmed ? 'is-dimmed' : '']
        .filter(Boolean)
        .join(' ');
      return `
        <g class="${cls}" data-topo-edge="${esc(e.id)}">
          <path class="host-topo-path" d="${d}" stroke="${e.color}" ${e.kind === 'nat' || e.kind === 'vm-off' ? 'stroke-dasharray="5 4"' : ''} />
          <circle class="host-topo-dot" cx="${e.x1}" cy="${e.y1}" r="3.5" fill="${e.color}" />
          <circle class="host-topo-dot" cx="${e.x2}" cy="${e.y2}" r="3.5" fill="${e.color}" />
        </g>
      `;
    })
    .join('');

  const pillsSvg = orderedEdges
    .filter((e) => (e.kind === 'bridge' || e.kind === 'vm-live') && e.label)
    .map((e) => {
      const mx = Math.round((e.x1 + e.x2) / 2);
      const my = Math.round((e.y1 + e.y2) / 2);
      const isFocused = Boolean(focusSets) && focusSets.activeEdges.has(e.id);
      const isDimmed = Boolean(focusSets) && !isFocused;
      const cls = ['host-topo-edge-pill', `is-${e.kind}`, isFocused ? 'is-focused' : '', isDimmed ? 'is-dimmed' : '']
        .filter(Boolean)
        .join(' ');
      const pillW = Math.min(114, Math.max(76, Math.round(e.label.length * 5.2 + 16)));
      const pillX = (-pillW / 2).toFixed(1);
      return `
        <g class="${cls}" data-topo-edge="${esc(e.id)}" transform="translate(${mx}, ${my})">
          <rect x="${pillX}" y="-10" width="${pillW}" height="20" rx="10" />
          <text x="0" y="3.5" text-anchor="middle">${esc(e.label)}</text>
        </g>
      `;
    })
    .join('');

  const pathsSvg = `
    <g class="host-topo-wires-layer">${wiresSvg}</g>
    <g class="host-topo-pills-layer">${pillsSvg}</g>
  `;

  const nicsHtml = topo.nicNodes
    .map((n) => {
      const { nic } = n;
      const isSelected = hostState.selectedTopoNode === n.nodeId;
      const isFocused = Boolean(focusSets) && focusSets.activeNodes.has(n.nodeId);
      const isDimmed = Boolean(focusSets) && !isFocused;
      const cls = ['host-topo-node', 'is-nic', isSelected ? 'is-selected' : '', isFocused ? 'is-focused' : '', isDimmed ? 'is-dimmed' : '']
        .filter(Boolean)
        .join(' ');
      return `
        <div class="${cls}" data-topo-node="${esc(n.nodeId)}" style="left:${n.x}px;top:${n.y}px;width:${n.w}px;height:${n.h}px;">
          <div class="host-topo-node-head">
            <span class="host-topo-node-icon"><span class="fi">${n.icon}</span></span>
            <span class="host-topo-node-title">${esc(nic.name)}</span>
            <span class="vm-badge is-mini"><span class="vm-badge-text">${esc(n.mediaLabel)}</span></span>
          </div>
          <div class="host-topo-node-sub">${esc(nic.description)}</div>
          <div class="host-topo-node-foot">
            <span class="host-topo-meta">${esc(nic.ipv4)}</span>
            <span class="host-topo-rate">${esc(formatCompactRate(nic.sendRate, nic.recvRate, 'arrow-compact'))}</span>
          </div>
        </div>
      `;
    })
    .join('');

  const switchesHtml = topo.switchNodes
    .map((s) => {
      const { sw } = s;
      const isSelected = hostState.selectedTopoNode === s.nodeId;
      const isFocused = Boolean(focusSets) && focusSets.activeNodes.has(s.nodeId);
      const isDimmed = Boolean(focusSets) && !isFocused;
      const cls = ['host-topo-node', 'is-switch', isSelected ? 'is-selected' : '', isFocused ? 'is-focused' : '', isDimmed ? 'is-dimmed' : '']
        .filter(Boolean)
        .join(' ');
      return `
        <div class="${cls}" data-topo-node="${esc(s.nodeId)}" style="left:${s.x}px;top:${s.y}px;width:${s.w}px;height:${s.h}px;--topo-accent:${s.modeColor};">
          <div class="host-topo-node-head">
            <span class="host-topo-node-icon is-switch"><span class="fi">&#xE774;</span></span>
            <span class="host-topo-node-title">${esc(sw.name)}</span>
            <span class="vm-badge is-mini"><span class="vm-badge-text">${esc(s.modeLabel)}</span></span>
          </div>
          <div class="host-topo-node-sub">${esc(sw.upstream)}</div>
          <div class="host-topo-node-foot">
            <span class="host-topo-meta">${sw.ipv4 && sw.ipv4 !== '---' ? esc(sw.ipv4) : '二层交换'}</span>
            <span class="host-topo-rate">${esc(formatCompactRate(sw.sendRate, sw.recvRate, 'arrow-compact'))}</span>
          </div>
        </div>
      `;
    })
    .join('');

  const rightHtml = topo.rightNodes
    .map((r) => {
      const isSelected = hostState.selectedTopoNode === r.nodeId;
      const isFocused = Boolean(focusSets) && focusSets.activeNodes.has(r.nodeId);
      const isDimmed = Boolean(focusSets) && !isFocused;
      const cls = ['host-topo-node', r.kind === 'host' ? 'is-host' : 'is-vm', !r.running ? 'is-off' : '', isSelected ? 'is-selected' : '', isFocused ? 'is-focused' : '', isDimmed ? 'is-dimmed' : '']
        .filter(Boolean)
        .join(' ');
      const portsHtml = r.ports.length
        ? r.ports
            .map(
              (p) => `
          <div class="host-topo-port-row ${p.active ? '' : 'is-off'}">
            <span class="host-topo-port-name">${esc(p.name)}</span>
            <span class="host-topo-port-rate">${p.active ? (p.ip ? esc(p.ip) : esc(formatCompactRate(p.tx, p.rx, 'arrow-compact'))) : '已关机'}</span>
          </div>
        `
            )
            .join('')
        : '<div class="host-topo-port-row is-off"><span class="host-topo-port-name">未开启宿主共享</span></div>';

      return `
        <div class="${cls}" data-topo-node="${esc(r.nodeId)}" style="left:${r.x}px;top:${r.y}px;width:${r.w}px;height:${r.h}px;--topo-accent:${r.color};">
          <div class="host-topo-node-head">
            <span class="host-topo-node-swatch" style="background:${r.color};"></span>
            <span class="host-topo-node-title">${esc(r.name)}</span>
            <span class="vm-badge is-mini"><span class="vm-badge-text">${r.kind === 'host' ? `${r.ports.length} 端口` : r.running ? '运行中' : '已关机'}</span></span>
          </div>
          <div class="host-topo-ports">${portsHtml}</div>
        </div>
      `;
    })
    .join('');

  const zoomPct = Math.round((Number(hostState.topoZoom) || 1) * 100);
  const panX = Number(hostState.topoPanX) || 0;
  const panY = Number(hostState.topoPanY) || 0;
  const zoomVal = Number(hostState.topoZoom) || 1;

  return `
    <div class="host-topo-stage" id="hostTopoStage">
      <div class="host-topo-pan-layer" id="hostTopoPanLayer" data-topo-w="${topo.canvasW}" data-topo-h="${topo.canvasH}" data-topo-minx="${topo.bounds.minX}" data-topo-maxx="${topo.bounds.maxX}" data-topo-miny="${topo.bounds.minY}" data-topo-maxy="${topo.bounds.maxY}" style="width:${topo.canvasW}px;height:${topo.canvasH}px;transform:translate3d(${panX}px, ${panY}px, 0) scale(${zoomVal});">
        <svg class="host-topo-svg" width="${topo.canvasW}" height="${topo.canvasH}">${pathsSvg}</svg>
        ${nicsHtml}
        ${switchesHtml}
        ${rightHtml}
      </div>
      <div class="host-topo-controls" data-topo-keep-open="1">
        <button type="button" class="host-topo-ctrl-btn" id="hostTopoZoomOutBtn" data-topo-keep-open="1" title="缩小" aria-label="缩小"><span class="fi">&#xE738;</span></button>
        <button type="button" class="host-topo-ctrl-btn is-readout" id="hostTopoZoomResetBtn" data-topo-keep-open="1" title="重置缩放并居中" aria-label="重置缩放">${zoomPct}%</button>
        <button type="button" class="host-topo-ctrl-btn" id="hostTopoZoomInBtn" data-topo-keep-open="1" title="放大" aria-label="放大"><span class="fi">&#xE710;</span></button>
        <div class="host-topo-ctrl-sep"></div>
        <button type="button" class="host-topo-ctrl-btn is-fullscreen" id="hostTopoFullscreenBtn" data-topo-keep-open="1" title="${isFullscreen ? '退出全屏' : '全屏'}" aria-label="${isFullscreen ? '退出全屏' : '全屏'}"><span class="fi">${isFullscreen ? '&#xE73F;' : '&#xE740;'}</span></button>
      </div>
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

  if (overview && sel.kind === 'env') {
    bodyHtml = renderEnvSectionHtml();
  } else if (sel.kind === 'cpu' || sel.kind === 'overview') {
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
        (() => {
          const hvMeta = getHyperVServiceStateMeta(hostState.hyperVServiceState);
          return renderTitleFirstKvItemHtml({
            title: 'Hyper-V 平台',
            stateText: hvMeta.kvLabel,
            caution: hvMeta.key !== 'ready'
          });
        })(),
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
      const liveReport = hostState.liveHostThreadReport;
      const liveThreads = Array.isArray(liveReport?.threads) ? liveReport.threads : null;
      const hostThreads = liveThreads && liveThreads.length > 0
        ? liveThreads.length
        : Math.max(1, Number(hostState.mockThreads) || HOST_SYS.threads || HOST_SYS.coreHistories.length || 12);
      const isClassicDegraded = hostState.cpuDetailStyle === 'led-classic';
      hwSubtitle = '';

      let runningVms;
      let lpRows;

      if (liveThreads && liveThreads.length > 0) {
        const vmColorMap = new Map();
        liveThreads.forEach((snap) => {
          (snap.consumers || []).forEach((c) => {
            if (!c.isHost && c.name && !vmColorMap.has(c.name)) {
              const color = vmColorMap.size < HOST_LED_VM_PALETTE.length ? HOST_LED_VM_PALETTE[vmColorMap.size] : HOST_LED_OTHER_COLOR;
              vmColorMap.set(c.name, { name: c.name, running: true, vcpu: 1, pct: 0, color });
            }
          });
        });
        runningVms = Array.from(vmColorMap.values());
        lpRows = liveThreads.map((snap, idx) => {
          const lp = typeof snap.logicalProcessorIndex === 'number' ? snap.logicalProcessorIndex : idx;
          const totalPct = Math.min(100, Math.max(0, Math.round(snap.totalUsagePercent || 0)));
          const slices = [];
          const hostPct = Math.round(snap.hostUsagePercent ?? snap.unattributedUsagePercent ?? 0);
          if (hostPct > 0 || (snap.consumers || []).length === 0) {
            slices.push({ vm: '__host__', label: '宿主', pct: Math.max(0, hostPct), color: HOST_LED_HOST_COLOR });
          }
          let vmUsed = 0;
          (snap.consumers || []).forEach((c) => {
            if (c.isHost) return;
            const p = Math.max(0, Math.round(c.usagePercent || 0));
            if (p <= 0) return;
            const meta = vmColorMap.get(c.name);
            slices.push({ vm: c.name, label: c.name, pct: p, color: meta?.color || HOST_LED_OTHER_COLOR });
            vmUsed += p;
          });
          const unattrGuest = Math.round(snap.unattributedGuestUsagePercent || 0);
          if (unattrGuest > 0) {
            slices.push({ vm: '__guest__', label: '未归属来宾', pct: unattrGuest, color: HOST_LED_GUEST_COLOR });
            vmUsed += unattrGuest;
          }
          const dominant = slices.slice().sort((a, b) => b.pct - a.pct)[0] || { vm: '__host__', label: '宿主', pct: totalPct, color: HOST_LED_HOST_COLOR };
          return { lp, totalPct, vmUsed, hostPct, slices, dominant };
        });
      } else if (hostState.isLive) {
        runningVms = (hostState.liveVms || [])
          .filter((v) => v.running)
          .map((v, i) => ({
            name: v.name,
            running: true,
            vcpu: v.cores || 1,
            pct: Math.round(v.telemetry?.cpuPct || 0),
            color: i < HOST_LED_VM_PALETTE.length ? HOST_LED_VM_PALETTE[i] : HOST_LED_OTHER_COLOR
          }));
        lpRows = Array.from({ length: hostThreads }, (_, lp) => {
          const hist = HOST_SYS.coreHistories[lp] || [0];
          const totalPct = Math.max(0, Math.min(100, Math.round(hist[hist.length - 1] ?? 0)));
          const slices = [{ vm: '__host__', label: '宿主', pct: totalPct, color: HOST_LED_HOST_COLOR }];
          return { lp, totalPct, vmUsed: 0, hostPct: totalPct, slices, dominant: slices[0] };
        });
      } else {
        runningVms = getRunningLedVms(hostThreads);
        lpRows = Array.from({ length: hostThreads }, (_, lp) => {
          const hist = HOST_SYS.coreHistories[lp] || HOST_SYS.coreHistories[lp % Math.max(1, HOST_SYS.coreHistories.length)] || [12];
          const rawTotal = Math.max(4, Math.min(96, Math.round((hist[hist.length - 1] ?? 12) + ((lp * 5) % 14))));
          const slices = [];
          let vmUsed = 0;

          const eligibleVms = runningVms.filter((v) => !v.allowedLps || v.allowedLps.includes(lp));
          const vmBudget = Math.max(0, rawTotal - 2);
          const vmSlices = [];
          eligibleVms.forEach((vm, idx) => {
            if (vmUsed >= vmBudget) return;
            const remain = vmBudget - vmUsed;
            const share = idx === eligibleVms.length - 1
              ? Math.min(remain, Math.max(1, Math.round(vmBudget / Math.max(1, eligibleVms.length))))
              : Math.min(remain, Math.max(1, Math.round((vmBudget / Math.max(1, eligibleVms.length)) * (((lp + idx) % 2 === 0) ? 1.15 : 0.78))));
            if (share > 0) {
              vmSlices.push({ vm: vm.name, label: vm.name, pct: share, color: vm.color });
              vmUsed += share;
            }
          });
          const hostPct = Math.max(2, rawTotal - vmUsed);
          slices.push({ vm: '__host__', label: '宿主', pct: hostPct, color: HOST_LED_HOST_COLOR }, ...vmSlices);
          const totalPct = Math.min(100, vmUsed + hostPct);
          const dominant = slices.slice().sort((a, b) => b.pct - a.pct)[0];
          return { lp, totalPct, vmUsed, hostPct, slices, dominant };
        });
      }

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
          const isHost = s.vm === '__host__';
          const isFocused = hovered && s.vm === hovered;
          if (isHost || isFocused || (keptArcs.length < 5 && s.pct >= 2)) {
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
        renderTitleFirstKvItemHtml({ title: '电源方案', stateText: (hostState.powerPlans?.length ? hostState.powerPlans : POWER_PLANS).find((p) => p.id === hostState.saved.powerPlanId)?.name || '平衡' })
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
    if (!nic) {
      icon = '&#xE774;';
      title = '物理网卡';
      hwSubtitle = '未检测到物理网卡';
      reading = '---';
      bodyHtml = `<div style="padding:18px 0;color:var(--text-tertiary);font-size:12.5px;">宿主机未上报可用物理网络适配器</div>`;
    } else {
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
    }
  } else if (sel.kind === 'switch') {
    const sw = hostState.switches[sel.idx] || hostState.switches[0];
    if (!sw) {
      icon = '&#xE774;';
      title = '虚拟交换机';
      hwSubtitle = '未检测到虚拟交换机';
      reading = '---';
      bodyHtml = `<div style="padding:18px 0;color:var(--text-tertiary);font-size:12.5px;">当前宿主机未配置 Hyper-V 虚拟交换机</div>`;
    } else {
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
    }
  } else if (sel.kind === 'gpu') {
    if (overview) {
      const gpuData = buildHostGpuBreakdown();
      const rawFocusKey = hostState.hoveredGpuKey || hostState.selectedGpuKey;
      const focusKey = rawFocusKey && gpuData.orderedItems.some((it) => it.key === rawFocusKey) ? rawFocusKey : null;
      bodyHtml = renderHostGpuOverview(gpuData, focusKey);
    } else {
      const g = hostState.gpus[sel.idx] || hostState.gpus[0];
      if (!g) {
        icon = '&#xF211;';
        title = 'GPU';
        hwSubtitle = '未检测到物理 GPU';
        reading = '---';
        bodyHtml = `<div style="padding:18px 0;color:var(--text-tertiary);font-size:12.5px;">宿主机未上报可用图形适配器</div>`;
      } else {
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
      }
    }
  } else if (overview && sel.kind === 'storage') {
    const storageData = buildHostStorageBreakdown();
    const rawFocusKey = hostState.hoveredStorageKey || hostState.selectedStorageKey;
    const focusKey = rawFocusKey && storageData.orderedItems.some((it) => it.key === rawFocusKey) ? rawFocusKey : null;
    bodyHtml = renderHostStorageOverview(storageData, focusKey);
  } else if (overview && sel.kind === 'net') {
    bodyHtml = renderHostNetworkOverview();
  } else if (sel.kind === 'disk') {
    const d = hostState.disks[sel.idx] || hostState.disks[0];
    if (!d) {
      icon = '&#xEDA2;';
      title = '物理磁盘';
      hwSubtitle = '未检测到物理磁盘';
      reading = '---';
      bodyHtml = `<div style="padding:18px 0;color:var(--text-tertiary);font-size:12.5px;">宿主机未上报物理磁盘设备</div>`;
    } else {
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
  }

  const identitySingleLine = !hwSubtitle;
  if (overview) {
    const activeOverviewKey = ['env', 'cpu', 'mem', 'gpu', 'storage', 'net'].includes(sel.kind) ? sel.kind : 'env';
    const isTopo = activeOverviewKey === 'net';
    return `
      <div class="vm-settings-area${isTopo ? ' is-host-topo-mode' : ''}">
        <div class="settings-subnav">${[['env', '环境'], ['cpu', 'CPU'], ['mem', '内存'], ['gpu', 'GPU'], ['storage', '存储'], ['net', '网络']].map(([key, label]) =>
          `<button type="button" class="subnav-btn ${activeOverviewKey === key ? 'selected' : ''}" aria-pressed="${activeOverviewKey === key}" data-overview-resource="${key}"><span>${label}</span></button>`
        ).join('')}</div>
        <div class="vm-settings-right">
          <div class="settings-host-scroll">${bodyHtml}</div>
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
// 环境要求自检（嵌入总览左侧第一项）
// ============================================================================
const HOST_ENV_HYPERV_STATES = [
  {
    key: 'ready',
    group: '运行与就绪',
    label: '正常就绪',
    kvLabel: '已启用',
    status: 'ok',
    hvActive: true,
    infoText: 'Hypervisor 监控程序、VMMS 服务与 WMI 管理接口均运行正常。',
    action: { label: '禁用', cls: '', disabled: false, op: 'disable' }
  },
  {
    key: 'pending-enable',
    group: '待重启过渡',
    label: '待重启启用',
    kvLabel: '待重启启用',
    status: 'warn',
    hvActive: false,
    infoText: 'Hyper-V 组件或引导启动项已启用，当前监控程序尚未接管，重启宿主后生效。',
    action: { label: '待重启', cls: '', disabled: true, op: 'none' }
  },
  {
    key: 'pending-disable',
    group: '待重启过渡',
    label: '待重启停用',
    kvLabel: '待重启停用',
    status: 'warn',
    hvActive: true,
    infoText: '已配置停用 Hypervisor 引导启动项，当前会话仍在运行，重启宿主后彻底关闭。',
    action: { label: '待重启', cls: '', disabled: true, op: 'none' }
  },
  {
    key: 'not-installed',
    group: '未启用或未安装',
    label: '组件未安装',
    kvLabel: '未启用',
    status: 'fail',
    hvActive: false,
    infoText: 'Hyper-V 组件尚未安装或启用，启用后需重启宿主完成配置。',
    action: { label: '启用', cls: 'accent', disabled: false, op: 'enable' }
  },
  {
    key: 'bcd-off',
    group: '未启用或未安装',
    label: 'BCD 启动项已关闭',
    kvLabel: '未启用',
    status: 'fail',
    hvActive: false,
    infoText: 'Hypervisor 引导启动项已关闭，开启后需重启宿主生效。',
    action: { label: '启用', cls: 'accent', disabled: false, op: 'enable' }
  },
  {
    key: 'vmms-stopped',
    group: '仅服务 / WMI 异常',
    label: 'VMMS 服务已停止',
    kvLabel: '服务未运行',
    status: 'fail',
    hvActive: true,
    infoText: 'VMMS 服务未运行，可直接尝试启动服务恢复。',
    action: { label: '启动服务', cls: 'accent', disabled: false, op: 'start-vmms' }
  },
  {
    key: 'vmms-disabled',
    group: '仅服务 / WMI 异常',
    label: 'VMMS 服务被禁用',
    kvLabel: '服务已禁用',
    status: 'fail',
    hvActive: true,
    infoText: 'VMMS 服务启动类型已被设为禁用，请恢复后启动。',
    action: null
  },
  {
    key: 'vmms-exe-missing',
    group: '仅服务 / WMI 异常',
    label: 'vmms 程序缺失',
    kvLabel: '组件文件缺失',
    status: 'fail',
    hvActive: true,
    infoText: '未找到 vmms 服务程序文件，请修复系统组件。',
    action: null
  },
  {
    key: 'wmi-error',
    group: '仅服务 / WMI 异常',
    label: 'WMI 接口异常',
    kvLabel: 'WMI 接口异常',
    status: 'fail',
    hvActive: true,
    infoText: 'VMMS 服务已运行，但 Hyper-V WMI 管理接口不可用。',
    action: null
  },
  {
    key: 'unsupported',
    group: '不支持与未知',
    label: '系统不支持',
    kvLabel: '系统不支持',
    status: 'fail',
    hvActive: false,
    infoText: '当前系统缺少可用的 Hyper-V 安装组件。',
    action: null
  },
  {
    key: 'unknown',
    group: '不支持与未知',
    label: '状态未知',
    kvLabel: '状态未知',
    status: 'unknown',
    hvActive: false,
    infoText: '无法确认当前状态（权限不足、查询超时或系统不支持探测）。',
    action: null
  }
];

function getHyperVServiceStateMeta(key) {
  return HOST_ENV_HYPERV_STATES.find((item) => item.key === key) || HOST_ENV_HYPERV_STATES[0];
}

function renderEnvSectionHtml() {
  const statusRowHtml = (status, label, { infoText = '', actionHtml = '' } = {}) => {
    const norm = status === true ? 'ok' : status === false ? 'fail' : status == null ? 'unknown' : status;
    const iconCls = norm === 'ok' ? 'is-ok' : norm === 'warn' ? 'is-warn' : norm === 'fail' ? 'is-fail' : 'is-muted';
    const iconHtml =
      norm === 'warn'
        ? `<span class="host-platform-status-icon ${iconCls}" aria-hidden="true"><span class="fi-warn-circle"><span class="fi fi-warn-circle-bg">&#xF136;</span><span class="fi fi-warn-circle-fg">&#xF13C;</span></span></span>`
        : `<span class="fi host-platform-status-icon ${iconCls}">${
            norm === 'ok' ? '&#xEC61;' : norm === 'fail' ? '&#xEB90;' : '&#xE9CE;'
          }</span>`;
    const infoBtnHtml = infoText
      ? `<button type="button" class="setting-info-btn" data-info-desc="${esc(infoText)}" title="${esc(infoText)}"><span class="fi" style="font-size:15px;">&#xE946;</span></button>`
      : '';
    return `
      <div class="isa-card">
        <div class="host-platform-row">
          <div class="host-platform-left">
            ${iconHtml}
            <div class="setting-label-grid">
              <span class="host-platform-label">${esc(label)}</span>
              ${infoBtnHtml}
            </div>
          </div>
          ${actionHtml}
        </div>
      </div>
    `;
  };
  const pendingServer = Boolean(hostState.saved.serverProductTypeSpoof) && !hostState.isWindowsServer;
  const hvMeta = getHyperVServiceStateMeta(hostState.hyperVServiceState);
  const cpuVirtInfo = hostState.cpuVirtOk
    ? '已在处理器或固件层面启用硬件虚拟化扩展，支持运行 Hyper-V 虚拟机。'
    : '未检测到可用的硬件虚拟化支持，请在 BIOS 或 UEFI 中开启 Intel VT-x、AMD SVM 或嵌套虚拟化。';
  const gpuPvInfo = hostState.gpuPvOk
    ? 'GPU-PV 需要 Windows 10 1803 (Build 17134) 及以上版本。'
    : '当前系统版本低于 Build 17134，不支持 GPU-PV 分区功能。';
  const iommuInfo = hostState.iommuOk
    ? '系统已启用硬件 DMA 保护与地址重映射隔离，满足设备直通安全前提。'
    : '未检测到硬件 IOMMU / SMMU DMA 保护，请在固件中启用相关虚拟化 IO 选项。';
  const serverInfo = hostState.isWindowsServer
    ? '当前宿主属于 Windows Server 产品类型，原生 PCIe 设备直通 (DDA) 已可用。'
    : pendingServer
      ? '工作站系统默认禁用原生 PCIe 设备直通 (DDA)，当前已配置切换为 ServerNT，重启宿主后生效。'
      : '仅 Windows Server 系统支持微软原生 PCIe 设备直通 (DDA)，当前处于工作站系统环境。';
  const hypervActionHtml = hvMeta.action
    ? `<button type="button" class="winui-btn ${hvMeta.action.cls || ''}" id="hostToggleHyperVBtn"${hvMeta.action.disabled ? ' disabled' : ''} style="flex-shrink:0;">${esc(hvMeta.action.label)}</button>`
    : '';
  return `
    <div class="proc-group-stack">
      <div class="host-platform-group">
        <div class="host-platform-group-title">基本要求</div>
        ${statusRowHtml(hostState.cpuVirtOk, 'CPU 虚拟化', { infoText: cpuVirtInfo })}
        ${statusRowHtml(hvMeta.status, 'Hyper-V 服务', {
          infoText: hvMeta.infoText,
          actionHtml: hypervActionHtml
        })}
      </div>
      <div class="host-platform-group">
        <div class="host-platform-group-title">GPU-PV 要求</div>
        ${statusRowHtml(hostState.gpuPvOk, `系统版本：${HOST_SYS.os}`, {
          infoText: gpuPvInfo
        })}
      </div>
      <div class="host-platform-group">
        <div class="host-platform-group-title">PCIe 直通要求</div>
        ${statusRowHtml(hostState.iommuOk, HOST_SYS.arch === 'ARM64' ? 'SMMU 状态' : 'IOMMU 状态', {
          infoText: iommuInfo
        })}
        ${statusRowHtml(hostState.isWindowsServer, '属于 Windows Server 系统', {
          infoText: serverInfo
        })}
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

function findPowerSettingDef(subId, setId) {
  const group = POWER_SUBGROUPS.find((g) => g.id === subId);
  const setting = group?.settings.find((s) => s.id === setId);
  return group && setting ? { group, setting } : null;
}

function getPowerSettingValue(planId, subId, setting, mode) {
  const planMap = hostState.powerValues[planId];
  const key = `${subId}::${setting.id}`;
  if (planMap && planMap[key] && typeof planMap[key][mode] === 'number') {
    return planMap[key][mode];
  }
  const pair = setting.defaults?.[planId] || setting.defaults?.balanced || [0, 0];
  return mode === 'dc' ? pair[1] : pair[0];
}

function setPowerSettingValue(planId, subId, setId, mode, val) {
  const found = findPowerSettingDef(subId, setId);
  if (!found) return false;
  const { setting } = found;
  let next = Number(val);
  if (!Number.isFinite(next)) return false;
  next = Math.trunc(next);
  if (setting.kind === 'range') {
    next = Math.max(setting.min, Math.min(setting.max, next));
  }
  const planMap = (hostState.powerValues[planId] ??= {});
  const key = `${subId}::${setId}`;
  const curAc = getPowerSettingValue(planId, subId, setting, 'ac');
  const curDc = getPowerSettingValue(planId, subId, setting, 'dc');
  planMap[key] = {
    ac: mode === 'ac' ? next : curAc,
    dc: mode === 'dc' ? next : curDc
  };
  return true;
}

function getFilteredPowerState() {
  const q = String(hostState.powerSearch || '').trim().toLowerCase();
  const showHidden = Boolean(hostState.powerShowHidden);
  let visibleCount = 0;
  let totalCount = 0;
  const groups = [];

  for (const group of POWER_SUBGROUPS) {
    const allowedInGroup = group.settings.filter((s) => showHidden || !s.hidden);
    totalCount += allowedInGroup.length;
    if (allowedInGroup.length === 0) continue;

    const groupMatch = q && group.name.toLowerCase().includes(q);
    const matchedSettings = q && !groupMatch
      ? allowedInGroup.filter(
          (s) =>
            s.name.toLowerCase().includes(q) ||
            (s.desc && s.desc.toLowerCase().includes(q)) ||
            (s.unit && s.unit.toLowerCase().includes(q))
        )
      : allowedInGroup;

    if (matchedSettings.length === 0) continue;
    visibleCount += matchedSettings.length;
    groups.push({ group, settings: matchedSettings, forcedOpen: Boolean(q) });
  }

  return { groups, visibleCount, totalCount };
}

function renderPowerEditorHtml(planId, subId, setting, mode) {
  const value = getPowerSettingValue(planId, subId, setting, mode);
  if (setting.kind === 'choice') {
    const options = setting.options || [];
    const selectedOpt = options.find((o) => o.value === value);
    const label = selectedOpt ? selectedOpt.label : `自定义 (${value})`;
    const comboKey = `${subId}::${setting.id}::${mode}`;
    const isOpen = hostState.powerOpenSettingCombo === comboKey;
    const optionItems = options
      .map(
        (o) => `
          <button type="button" class="winui-combo-item ${o.value === value ? 'is-selected' : ''}" data-host-power-option="${esc(comboKey)}::${o.value}" role="option" aria-selected="${o.value === value}">
            <span>${esc(o.label)}</span>
          </button>
        `
      )
      .join('');
    return `
      <div class="winui-combo ${isOpen ? 'is-open' : ''}" data-host-power-combo="${esc(comboKey)}">
        <button type="button" class="winui-combo-trigger" data-host-power-combo-trigger="${esc(comboKey)}" aria-haspopup="listbox" aria-expanded="${isOpen}" title="${esc(label)}">
          <span>${esc(label)}</span>
          <span class="fi winui-combo-chevron">&#xE70D;</span>
        </button>
        <div class="winui-combo-flyout" role="listbox" ${isOpen ? '' : 'hidden'}>
          ${optionItems}
        </div>
      </div>
    `;
  }

  const numKey = `${subId}::${setting.id}::${mode}`;
  const atMin = value <= setting.min;
  const atMax = value >= setting.max;
  return `
    <div class="host-power-num-box" data-host-power-numbox="${esc(numKey)}">
      <button type="button" class="host-power-num-step" data-host-power-step="${esc(numKey)}::-1" title="减少" ${atMin ? 'disabled' : ''}>
        <span class="fi">&#xE738;</span>
      </button>
      <input type="text" inputmode="numeric" class="host-power-num-input" data-host-power-num="${esc(numKey)}" value="${value}" aria-label="${esc(setting.name)} (${mode.toUpperCase()})" />
      <span class="host-power-num-unit">${esc(setting.unit || '')}</span>
      <button type="button" class="host-power-num-step" data-host-power-step="${esc(numKey)}::1" title="增加" ${atMax ? 'disabled' : ''}>
        <span class="fi">&#xE710;</span>
      </button>
    </div>
  `;
}

function renderPowerSubgroupsHtml() {
  const planId = hostState.draft.powerPlanId || 'balanced';
  const { groups } = getFilteredPowerState();
  const hasBattery = Boolean(hostState.powerHasBattery);

  if (groups.length === 0) {
    return `
      <div class="host-power-expander">
        <div class="host-power-empty">未找到匹配的电源设置，请调整关键词或开启「显示隐藏项」。</div>
      </div>
    `;
  }

  return groups
    .map(({ group, settings, forcedOpen }) => {
      const isOpen = forcedOpen || hostState.powerExpandedGroups.has(group.id);
      const rowsHtml = isOpen
        ? settings
            .map((setting) => {
              const hasOpenCombo =
                hostState.powerOpenSettingCombo === `${group.id}::${setting.id}::ac` ||
                hostState.powerOpenSettingCombo === `${group.id}::${setting.id}::dc`;
              const rangeNote =
                setting.kind === 'range'
                  ? `范围：${setting.min}–${setting.max}${setting.unit ? ' ' + setting.unit : ''}${setting.unit === '秒' ? '（0 为从不）' : ''}`
                  : '';
              const descText = [setting.desc, rangeNote].filter(Boolean).join(' ');
              const infoBtn = descText
                ? `<button type="button" class="setting-info-btn" data-info-desc="${esc(descText)}" title="${esc(descText)}"><span class="fi" style="font-size:15px;">&#xE946;</span></button>`
                : '';
              const hiddenTag = setting.hidden ? '<span class="host-power-hidden-tag">隐藏项</span>' : '';
              const controlsHtml = hasBattery
                ? `
                  <div class="host-power-dual-cols">
                    <div class="host-power-slot">
                      <span class="host-power-slot-mini"><span class="fi">&#xE83E;</span><span>接通电源 (AC)</span></span>
                      ${renderPowerEditorHtml(planId, group.id, setting, 'ac')}
                    </div>
                    <div class="host-power-slot">
                      <span class="host-power-slot-mini"><span class="fi">&#xE83F;</span><span>使用电池 (DC)</span></span>
                      ${renderPowerEditorHtml(planId, group.id, setting, 'dc')}
                    </div>
                  </div>
                `
                : `
                  <div class="host-power-single-col">
                    ${renderPowerEditorHtml(planId, group.id, setting, 'ac')}
                  </div>
                `;
              return `
                <div class="host-power-row ${hasOpenCombo ? 'has-open-combo' : ''}">
                  <div class="host-power-row-label">
                    <div class="setting-label-grid">
                      <span class="setting-label-title">${esc(setting.name)}</span>
                      ${hiddenTag}
                      ${infoBtn}
                    </div>
                    ${rangeNote ? `<div class="host-power-row-meta">${esc(rangeNote)}</div>` : ''}
                  </div>
                  ${controlsHtml}
                </div>
              `;
            })
            .join('')
        : '';

      return `
        <div class="host-power-expander ${isOpen ? 'is-open' : ''}">
          <button type="button" class="host-power-exp-header" data-host-power-group="${esc(group.id)}" aria-expanded="${isOpen}">
            <span class="host-power-exp-left">
              <span class="fi host-power-exp-icon">${group.icon}</span>
              <span class="host-power-exp-title">${esc(group.name)}</span>
              <span class="host-power-count-pill">${settings.length} 项</span>
            </span>
            <span class="fi host-power-exp-chevron">&#xE70D;</span>
          </button>
          ${
            isOpen
              ? `
                <div class="host-power-exp-body">
                  ${
                    hasBattery
                      ? `
                        <div class="host-power-col-head">
                          <span>设置项</span>
                          <div class="host-power-dual-cols">
                            <span class="host-power-col-badge"><span class="fi">&#xE83E;</span><span>接通电源 (AC)</span></span>
                            <span class="host-power-col-badge"><span class="fi">&#xE83F;</span><span>使用电池 (DC)</span></span>
                          </div>
                        </div>
                      `
                      : ''
                  }
                  ${rowsHtml}
                </div>
              `
              : ''
          }
        </div>
      `;
    })
    .join('');
}

function renderPowerPolicySectionHtml() {
  const plansList = hostState.powerPlans?.length ? hostState.powerPlans : POWER_PLANS;
  const activePlan = plansList.find((p) => p.id === hostState.draft.powerPlanId) || plansList[0];
  const comboItems = plansList.map(
    (p) => `
      <button type="button" class="winui-combo-item ${p.id === activePlan.id ? 'is-selected' : ''}" data-host-power-plan="${p.id}" role="option" aria-selected="${p.id === activePlan.id}">
        <span>${esc(p.name)}</span>
      </button>
    `
  ).join('');
  const { groups, visibleCount, totalCount } = getFilteredPowerState();
  const allExpanded = groups.length > 0 && groups.every((g) => g.forcedOpen || hostState.powerExpandedGroups.has(g.group.id));

  return `
    <div class="proc-group-stack">
      <div class="isa-grid-2x2">
        <div class="isa-card uniform-1row span-2">
          <div class="isa-card-row">
            ${labelHtml('电源方案', '枚举当前 Windows 电源计划并切换活动方案，在下方查看并修改所有子组电源参数。')}
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
        <div class="isa-card span-2 host-power-toolbar-card">
          <div class="host-power-toolbar">
            <div class="host-power-search-wrap">
              <span class="fi host-power-search-icon">&#xE721;</span>
              <input type="text" class="winui-textbox host-power-search-input" id="hostPowerSearchInput" placeholder="筛选电源设置、子组或说明..." value="${esc(hostState.powerSearch)}" />
              <button type="button" class="host-power-search-clear" id="hostPowerSearchClear" title="清空筛选" ${hostState.powerSearch ? '' : 'hidden'}>
                <span class="fi">&#xE711;</span>
              </button>
            </div>
            <div class="host-power-toolbar-right">
              <button type="button" class="winui-btn" id="hostPowerToggleAllBtn" ${groups.length === 0 ? 'disabled' : ''}>
                ${allExpanded ? '全部折叠' : '全部展开'}
              </button>
              <div class="host-power-hidden-switch" id="hostPowerShowHiddenWrap" title="开启后显示 Windows 默认隐藏的系统底层电源设置项">
                <span class="host-power-hidden-label">显示隐藏项</span>
                <span class="host-power-count-pill" id="hostPowerCountPill">${visibleCount} / ${totalCount}</span>
                <div class="winui-switch ${hostState.powerShowHidden ? 'on' : ''}" id="hostPowerShowHiddenSwitch" role="switch" aria-checked="${hostState.powerShowHidden}" tabindex="0">
                  <div class="winui-switch-track"><div class="winui-switch-thumb"></div></div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
      <div class="host-power-subgroups" id="hostPowerSubgroups">
        ${renderPowerSubgroupsHtml()}
      </div>
    </div>
  `;
}

function updatePowerSubgroupsDom(page) {
  const subgroupsEl = page.querySelector('#hostPowerSubgroups');
  if (!subgroupsEl) return;
  const { groups, visibleCount, totalCount } = getFilteredPowerState();
  subgroupsEl.innerHTML = renderPowerSubgroupsHtml();
  const pill = page.querySelector('#hostPowerCountPill');
  if (pill) pill.textContent = `${visibleCount} / ${totalCount}`;
  const clearBtn = page.querySelector('#hostPowerSearchClear');
  if (clearBtn) clearBtn.hidden = !hostState.powerSearch;
  const toggleAllBtn = page.querySelector('#hostPowerToggleAllBtn');
  if (toggleAllBtn) {
    const allExpanded = groups.length > 0 && groups.every((g) => g.forcedOpen || hostState.powerExpandedGroups.has(g.group.id));
    toggleAllBtn.textContent = allExpanded ? '全部折叠' : '全部展开';
    toggleAllBtn.disabled = groups.length === 0;
  }
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
    contentHtml = renderPowerPolicySectionHtml();
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

  const scrollSelectors = ['.tc-left-rail', '.tc-right-card', '.tc-canvas-body', '.vm-overview-scroll', '.settings-host-scroll', '.host-flat-scroll', '.host-led-legend', '.host-led-vm-scroll', '.host-led-tile-grid', '.host-share-stage'];
  const savedScrolls = scrollSelectors.map((sel) => {
    const el = page.querySelector?.(sel);
    return el ? { sel, top: el.scrollTop, left: el.scrollLeft } : null;
  });

  const isTopoActive = hostState.activeTab === 'overview' && hostState.overviewResource === 'net';
  const isTopoFullscreen = Boolean(hostState.topoFullscreen) && isTopoActive;
  const winEl = typeof document !== 'undefined' ? document.getElementById('winuiWindow') : null;
  if (winEl && typeof winEl.classList?.toggle === 'function') {
    winEl.classList.toggle('is-host-topo-fullscreen', isTopoFullscreen);
  }


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
      const iconMap = { warning: '&#xF13C;', info: '&#xE946;', error: '&#xE711;' };
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
  if (isTopoActive) {
    syncHostTopoViewport(page);
    if (typeof requestAnimationFrame === 'function') {
      requestAnimationFrame(() => syncHostTopoViewport(page));
    }
  }
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
  if (hostState.activeTab !== 'overview' || !['cpu', 'mem'].includes(hostState.overviewResource)) {
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

  let topoDragging = false;
  let topoJustDragged = false;
  let topoPointerId = null;
  let topoDragStartX = 0;
  let topoDragStartY = 0;
  let topoDragOrigX = 0;
  let topoDragOrigY = 0;

  page.addEventListener('pointerdown', (e) => {
    const stage = e.target.closest?.('#hostTopoStage');
    if (!stage) return;
    if (e.button !== 0 && e.button !== 1) return;
    if (
      e.target.closest('button') ||
      e.target.closest('input') ||
      e.target.closest('[data-topo-keep-open]') ||
      e.target.closest('[data-topo-node]')
    ) {
      return;
    }
    topoDragging = true;
    topoJustDragged = false;
    topoPointerId = e.pointerId;
    topoDragStartX = e.clientX;
    topoDragStartY = e.clientY;
    topoDragOrigX = Number(hostState.topoPanX) || 0;
    topoDragOrigY = Number(hostState.topoPanY) || 0;
  });

  page.addEventListener('pointermove', (e) => {
    if (!topoDragging) return;
    const stage = page.querySelector?.('#hostTopoStage');
    const panLayer = page.querySelector?.('#hostTopoPanLayer');
    if (!stage || !panLayer) return;
    const dx = e.clientX - topoDragStartX;
    const dy = e.clientY - topoDragStartY;
    if (!topoJustDragged && Math.hypot(dx, dy) > 4) {
      topoJustDragged = true;
      stage.classList.add('is-dragging');
      try {
        if (topoPointerId != null) stage.setPointerCapture(topoPointerId);
      } catch {}
    }
    if (topoJustDragged) {
      hostState.topoAutoFit = false;
      hostState.topoPanX = topoDragOrigX + dx;
      hostState.topoPanY = topoDragOrigY + dy;
      const z = Number(hostState.topoZoom) || 1;
      panLayer.style.transform = `translate3d(${hostState.topoPanX}px, ${hostState.topoPanY}px, 0) scale(${z})`;
    }
  });

  const endTopoDrag = (e) => {
    if (!topoDragging) return;
    topoDragging = false;
    topoPointerId = null;
    const stage = page.querySelector?.('#hostTopoStage');
    if (stage) {
      stage.classList.remove('is-dragging');
      try {
        stage.releasePointerCapture(e.pointerId);
      } catch {}
    }
  };
  page.addEventListener('pointerup', endTopoDrag);
  page.addEventListener('pointercancel', endTopoDrag);

  page.addEventListener(
    'wheel',
    (e) => {
      const stage = e.target.closest?.('#hostTopoStage');
      if (!stage) return;
      e.preventDefault();
      const rect = stage.getBoundingClientRect();
      const curZoom = Number(hostState.topoZoom) || 1;
      const step = e.deltaY < 0 ? 0.1 : -0.1;
      const nextZoom = Math.min(1.85, Math.max(0.45, Number((curZoom + step).toFixed(2))));
      if (nextZoom === curZoom) return;
      const mx = e.clientX - rect.left;
      const my = e.clientY - rect.top;
      const curX = Number(hostState.topoPanX) || 0;
      const curY = Number(hostState.topoPanY) || 0;
      const ratio = nextZoom / curZoom;
      hostState.topoAutoFit = false;
      hostState.topoPanX = mx - (mx - curX) * ratio;
      hostState.topoPanY = my - (my - curY) * ratio;
      hostState.topoZoom = nextZoom;
      syncHostTopoViewport(page);
    },
    { passive: false }
  );

  page.addEventListener('dblclick', (e) => {
    const stage = e.target.closest?.('#hostTopoStage');
    if (!stage) return;
    if (e.target.closest('[data-topo-node]') || e.target.closest('[data-topo-keep-open]')) return;
    hostState.topoAutoFit = true;
    syncHostTopoViewport(page, true);
  });

  page.addEventListener('mouseover', (e) => {
    if (topoDragging) return;
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
    const gpuTarget = e.target.closest?.('[data-gpu-key]');
    const nextGpu = gpuTarget ? gpuTarget.getAttribute('data-gpu-key') : null;
    if (hostState.hoveredGpuKey !== nextGpu) {
      hostState.hoveredGpuKey = nextGpu;
      renderHostPage();
    }
    const storageTarget = e.target.closest?.('[data-storage-key]');
    const nextStorage = storageTarget ? storageTarget.getAttribute('data-storage-key') : null;
    if (hostState.hoveredStorageKey !== nextStorage) {
      hostState.hoveredStorageKey = nextStorage;
      renderHostPage();
    }
    const topoTarget = e.target.closest?.('[data-topo-node]');
    const nextTopo = topoTarget ? topoTarget.getAttribute('data-topo-node') : null;
    if (hostState.suppressedHoverTopoNode && nextTopo !== hostState.suppressedHoverTopoNode) {
      hostState.suppressedHoverTopoNode = null;
    }
    if (hostState.hoveredTopoNode !== nextTopo) {
      hostState.hoveredTopoNode = nextTopo;
      applyHostTopoFocusDom(page);
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
    if (hostState.hoveredGpuKey) {
      hostState.hoveredGpuKey = null;
      changed = true;
    }
    if (hostState.hoveredStorageKey) {
      hostState.hoveredStorageKey = null;
      changed = true;
    }
    hostState.suppressedHoverTopoNode = null;
    if (hostState.hoveredTopoNode) {
      hostState.hoveredTopoNode = null;
      applyHostTopoFocusDom(page);
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

    if (e.target.closest('#hostTopoFullscreenBtn')) {
      e.stopPropagation();
      hostState.topoFullscreen = !hostState.topoFullscreen;
      hostState.topoAutoFit = true;
      renderHostPage();
      return;
    }

    if (e.target.closest('#hostTopoZoomInBtn') || e.target.closest('#hostTopoZoomOutBtn')) {
      e.stopPropagation();
      const stage = page.querySelector?.('#hostTopoStage');
      const curZoom = Number(hostState.topoZoom) || 1;
      const step = e.target.closest('#hostTopoZoomInBtn') ? 0.15 : -0.15;
      const nextZoom = Math.min(1.85, Math.max(0.45, Number((curZoom + step).toFixed(2))));
      if (stage && nextZoom !== curZoom) {
        const cx = (stage.clientWidth || 0) / 2;
        const cy = (stage.clientHeight || 0) / 2;
        const curX = Number(hostState.topoPanX) || 0;
        const curY = Number(hostState.topoPanY) || 0;
        const ratio = nextZoom / curZoom;
        hostState.topoAutoFit = false;
        hostState.topoPanX = cx - (cx - curX) * ratio;
        hostState.topoPanY = cy - (cy - curY) * ratio;
        hostState.topoZoom = nextZoom;
        syncHostTopoViewport(page);
      }
      return;
    }

    if (e.target.closest('#hostTopoZoomResetBtn')) {
      e.stopPropagation();
      hostState.topoAutoFit = true;
      syncHostTopoViewport(page, true);
      return;
    }

    const topoNode = e.target.closest?.('[data-topo-node]');
    if (topoNode) {
      if (topoJustDragged) {
        topoJustDragged = false;
        return;
      }
      e.stopPropagation();
      const nodeId = topoNode.getAttribute('data-topo-node');
      if (hostState.selectedTopoNode === nodeId) {
        hostState.selectedTopoNode = null;
        hostState.suppressedHoverTopoNode = nodeId;
      } else {
        hostState.selectedTopoNode = nodeId;
        hostState.suppressedHoverTopoNode = null;
      }
      applyHostTopoFocusDom(page);
      return;
    }

    if (e.target.closest('#hostTopoStage')) {
      if (topoJustDragged) {
        topoJustDragged = false;
        return;
      }
      if (hostState.selectedTopoNode || hostState.suppressedHoverTopoNode) {
        hostState.selectedTopoNode = null;
        hostState.suppressedHoverTopoNode = null;
        applyHostTopoFocusDom(page);
      }
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

    const gpuChip = e.target.closest('[data-gpu-key]');
    if (gpuChip) {
      const gpuKey = gpuChip.getAttribute('data-gpu-key');
      hostState.selectedGpuKey = hostState.selectedGpuKey === gpuKey ? null : gpuKey;
      renderHostPage();
      return;
    }

    const storageChip = e.target.closest('[data-storage-key]');
    if (storageChip) {
      const storageKey = storageChip.getAttribute('data-storage-key');
      hostState.selectedStorageKey = hostState.selectedStorageKey === storageKey ? null : storageKey;
      renderHostPage();
      return;
    }

    const tabBtn = e.target.closest('[data-host-tab]');
    if (tabBtn) {
      hostState.activeTab = tabBtn.getAttribute('data-host-tab') || 'state';
      if (hostState.activeTab !== 'overview') hostState.topoFullscreen = false;
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
      hostState.hoveredGpuKey = null;
      hostState.hoveredStorageKey = null;
      hostState.hoveredTopoNode = null;
      hostState.suppressedHoverTopoNode = null;
      if (hostState.overviewResource !== 'net') hostState.topoFullscreen = false;
      else hostState.topoAutoFit = true;
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
      const cur = getHyperVServiceStateMeta(hostState.hyperVServiceState);
      const op = cur.action?.op;
      if (op === 'disable') {
        dispatchHostMutation(
          'toggleHyperV',
          { enabled: false },
          () => {
            hostState.hyperVServiceState = 'pending-disable';
            hostState.hyperVEnabled = true;
          },
          { severity: 'warning', text: '已请求关闭 Hypervisor 引导启动项，重启宿主后生效。' }
        );
      } else if (op === 'enable') {
        const wasBcdOff = cur.key === 'bcd-off';
        dispatchHostMutation(
          'toggleHyperV',
          { enabled: true },
          () => {
            hostState.hyperVServiceState = 'pending-enable';
            hostState.hyperVEnabled = false;
          },
          {
            severity: 'warning',
            text: wasBcdOff
              ? '已重新开启 Hypervisor 引导启动项，重启宿主后生效。'
              : '已请求启用 Hyper-V 功能，重启宿主后生效。'
          }
        );
      } else if (op === 'start-vmms') {
        dispatchHostMutation(
          'toggleHyperV',
          { enabled: true },
          () => {
            hostState.hyperVServiceState = 'ready';
            hostState.hyperVEnabled = true;
          },
          { severity: 'success', text: '已启动 VMMS 服务，Hyper-V 服务现已就绪。' }
        );
      }
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
      const swName = `Custom Switch ${idx}`;
      dispatchHostMutation(
        'createSwitch',
        { name: swName, mode: 'Isolated' },
        () => {
          hostState.switches.push({
            id: `sw-custom-${idx}`,
            name: swName,
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
        },
        { severity: 'success', text: `已新建专用交换机 ${swName}` }
      );
      return;
    }

    const schedBtn = e.target.closest('[data-host-sched]');
    if (schedBtn) {
      const next = schedBtn.getAttribute('data-host-sched') || 'Core';
      if (hostState.saved.scheduler !== next) {
        dispatchHostMutation(
          'scheduler',
          { type: next },
          () => {
            hostState.draft.scheduler = next;
            hostState.saved.scheduler = next;
          },
          { severity: 'warning', text: `调度器已切换为「${getSchedulerLabel(next, true)}」，需重启宿主后生效。` }
        );
      } else {
        renderHostPage();
      }
      return;
    }

    if (e.target.closest('#hostPowerPlanTrigger')) {
      e.stopPropagation();
      hostState.powerOpenSettingCombo = null;
      hostState.powerComboOpen = !hostState.powerComboOpen;
      renderHostPage();
      return;
    }

    const powerPlanBtn = e.target.closest('[data-host-power-plan]');
    if (powerPlanBtn) {
      const next = powerPlanBtn.getAttribute('data-host-power-plan') || 'high';
      hostState.powerComboOpen = false;
      hostState.powerOpenSettingCombo = null;
      dispatchHostMutation(
        'powerPlan',
        { planId: next },
        () => {
          hostState.draft.powerPlanId = next;
          hostState.saved.powerPlanId = next;
        },
        { severity: 'success', text: '已应用' }
      );
      return;
    }

    if (e.target.closest('#hostPowerShowHiddenWrap')) {
      hostState.powerShowHidden = !hostState.powerShowHidden;
      hostState.powerOpenSettingCombo = null;
      renderHostPage();
      return;
    }

    if (e.target.closest('#hostPowerSearchClear')) {
      hostState.powerSearch = '';
      hostState.powerOpenSettingCombo = null;
      renderHostPage();
      page.querySelector('#hostPowerSearchInput')?.focus();
      return;
    }

    if (e.target.closest('#hostPowerToggleAllBtn')) {
      const { groups } = getFilteredPowerState();
      const allExpanded = groups.length > 0 && groups.every((g) => g.forcedOpen || hostState.powerExpandedGroups.has(g.group.id));
      if (allExpanded) {
        hostState.powerExpandedGroups.clear();
      } else {
        groups.forEach((g) => hostState.powerExpandedGroups.add(g.group.id));
      }
      hostState.powerOpenSettingCombo = null;
      renderHostPage();
      return;
    }

    const powerGroupBtn = e.target.closest('[data-host-power-group]');
    if (powerGroupBtn) {
      const gid = powerGroupBtn.getAttribute('data-host-power-group');
      if (gid) {
        if (hostState.powerExpandedGroups.has(gid)) {
          hostState.powerExpandedGroups.delete(gid);
        } else {
          hostState.powerExpandedGroups.add(gid);
        }
        hostState.powerOpenSettingCombo = null;
        renderHostPage();
      }
      return;
    }

    const powerComboTrigger = e.target.closest('[data-host-power-combo-trigger]');
    if (powerComboTrigger) {
      e.stopPropagation();
      const key = powerComboTrigger.getAttribute('data-host-power-combo-trigger');
      hostState.powerComboOpen = false;
      hostState.powerOpenSettingCombo = hostState.powerOpenSettingCombo === key ? null : key;
      renderHostPage();
      return;
    }

    const powerOptBtn = e.target.closest('[data-host-power-option]');
    if (powerOptBtn) {
      const raw = powerOptBtn.getAttribute('data-host-power-option') || '';
      const [subId, setId, mode, valStr] = raw.split('::');
      const planId = hostState.draft.powerPlanId || 'balanced';
      hostState.powerOpenSettingCombo = null;
      if (subId && setId && mode && setPowerSettingValue(planId, subId, setId, mode, Number(valStr))) {
        hostState.notice = { severity: 'success', text: '已应用' };
      }
      renderHostPage();
      return;
    }

    const powerStepBtn = e.target.closest('[data-host-power-step]');
    if (powerStepBtn && !powerStepBtn.disabled) {
      const raw = powerStepBtn.getAttribute('data-host-power-step') || '';
      const [subId, setId, mode, dirStr] = raw.split('::');
      const found = findPowerSettingDef(subId, setId);
      const planId = hostState.draft.powerPlanId || 'balanced';
      if (found) {
        const cur = getPowerSettingValue(planId, subId, found.setting, mode);
        const dir = Number(dirStr) || 0;
        const next = cur + dir * (found.setting.step || 1);
        if (setPowerSettingValue(planId, subId, setId, mode, next)) {
          hostState.notice = { severity: 'success', text: '已应用' };
          renderHostPage();
        }
      }
      return;
    }

    let closedAnyPowerCombo = false;
    if (hostState.powerComboOpen && !e.target.closest('#hostPowerPlanCombo')) {
      hostState.powerComboOpen = false;
      closedAnyPowerCombo = true;
    }
    if (hostState.powerOpenSettingCombo && !e.target.closest('[data-host-power-combo]')) {
      hostState.powerOpenSettingCombo = null;
      closedAnyPowerCombo = true;
    }
    if (closedAnyPowerCombo) {
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
        const notice =
          key === 'serverProductTypeSpoof' || key === 'nativeNvme'
            ? {
                severity: 'warning',
                text: `${key === 'nativeNvme' ? '主机原生 NVMe' : 'Server 系统标识'}已更新，需重启宿主后生效。`
              }
            : { severity: 'success', text: '已应用' };
        dispatchHostMutation(
          key,
          { enabled: next },
          () => {
            hostState.draft[key] = next;
            hostState.saved[key] = next;
          },
          notice
        );
      }
      return;
    }

    if (e.target.closest('#hostResetMacBtn')) {
      const minMac = '00-15-5D-01-00-00';
      const maxMac = '00-15-5D-01-FF-FF';
      dispatchHostMutation(
        'macRange',
        { minMac, maxMac },
        () => {
          hostState.draft.macMin = minMac;
          hostState.draft.macMax = maxMac;
          applyKeys(['macMin', 'macMax']);
        },
        { severity: 'success', text: '已恢复默认 MAC 地址池。' }
      );
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
      dispatchHostMutation(
        'macRange',
        { minMac, maxMac },
        () => {
          hostState.draft.macMin = minMac;
          hostState.draft.macMax = maxMac;
          applyKeys(['macMin', 'macMax']);
        },
        { severity: 'success', text: '全局动态 MAC 地址池已更新。' }
      );
      return;
    }
  });

  if (typeof document !== 'undefined') {
    document.addEventListener('click', (e) => {
      let dirty = false;
      if (hostState.powerComboOpen && !e.target.closest('#hostPowerPlanCombo')) {
        hostState.powerComboOpen = false;
        dirty = true;
      }
      if (hostState.powerOpenSettingCombo && !e.target.closest('[data-host-power-combo]')) {
        hostState.powerOpenSettingCombo = null;
        dirty = true;
      }
      if (dirty) renderHostPage();
    });
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && hostState.topoFullscreen) {
        hostState.topoFullscreen = false;
        hostState.topoAutoFit = true;
        renderHostPage();
      }
    });
  }

  if (typeof window !== 'undefined') {
    window.addEventListener('resize', () => {
      if (hostState.activeTab === 'overview' && hostState.overviewResource === 'net') {
        syncHostTopoViewport(page, Boolean(hostState.topoAutoFit));
      }
    });
  }

  let powerComposing = false;
  page.addEventListener('compositionstart', (e) => {
    if (e.target.id === 'hostPowerSearchInput') powerComposing = true;
  });
  page.addEventListener('compositionend', (e) => {
    if (e.target.id === 'hostPowerSearchInput') {
      powerComposing = false;
      hostState.powerSearch = e.target.value;
      updatePowerSubgroupsDom(page);
    }
  });

  page.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && e.target.matches('[data-host-power-num]')) {
      e.preventDefault();
      e.target.blur();
      return;
    }
    if ((e.key === ' ' || e.key === 'Enter') && e.target.id === 'hostPowerShowHiddenSwitch') {
      e.preventDefault();
      hostState.powerShowHidden = !hostState.powerShowHidden;
      hostState.powerOpenSettingCombo = null;
      renderHostPage();
    }
  });

  page.addEventListener('change', (e) => {
    if (e.target.matches('[data-host-power-num]')) {
      const rawKey = e.target.getAttribute('data-host-power-num') || '';
      const [subId, setId, mode] = rawKey.split('::');
      const planId = hostState.draft.powerPlanId || 'balanced';
      const parsed = Number(String(e.target.value || '').trim());
      if (!Number.isFinite(parsed) || parsed < 0) {
        hostState.notice = { severity: 'error', text: '请输入有效的非负整数数值。' };
        renderHostPage();
        return;
      }
      if (setPowerSettingValue(planId, subId, setId, mode, parsed)) {
        hostState.notice = { severity: 'success', text: '已应用' };
        renderHostPage();
      }
      return;
    }
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
    if (e.target.id === 'hostPowerSearchInput') {
      if (powerComposing) return;
      hostState.powerSearch = e.target.value;
      updatePowerSubgroupsDom(page);
      return;
    }
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

export function setHostActionHandler(fn) {
  hostActionHandler = typeof fn === 'function' ? fn : null;
}

export function applyLiveHostSnapshot(snapshot) {
  if (!snapshot) return;
  hostState.isLive = true;
  if (snapshot.sys) Object.assign(HOST_SYS, snapshot.sys);
  if (snapshot.saved) {
    Object.assign(hostState.saved, snapshot.saved);
    hostState.draft = structuredClone(hostState.saved);
  }
  if (typeof snapshot.hyperVServiceState === 'string') hostState.hyperVServiceState = snapshot.hyperVServiceState;
  if (typeof snapshot.hyperVEnabled === 'boolean') hostState.hyperVEnabled = snapshot.hyperVEnabled;
  if (typeof snapshot.cpuVirtOk === 'boolean') hostState.cpuVirtOk = snapshot.cpuVirtOk;
  if (typeof snapshot.gpuPvOk === 'boolean') hostState.gpuPvOk = snapshot.gpuPvOk;
  if (typeof snapshot.iommuOk === 'boolean') hostState.iommuOk = snapshot.iommuOk;
  if (typeof snapshot.isWindowsServer === 'boolean') hostState.isWindowsServer = snapshot.isWindowsServer;
  if (typeof snapshot.powerHasBattery === 'boolean') hostState.powerHasBattery = snapshot.powerHasBattery;
  if (Array.isArray(snapshot.powerPlans)) hostState.powerPlans = snapshot.powerPlans;
  if (Array.isArray(snapshot.nics)) hostState.nics = snapshot.nics;
  if (Array.isArray(snapshot.switches)) hostState.switches = snapshot.switches;
  if (Array.isArray(snapshot.gpus)) hostState.gpus = snapshot.gpus;
  if (Array.isArray(snapshot.disks)) hostState.disks = snapshot.disks;
  if (Array.isArray(snapshot.pcieDevices)) hostState.pcieDevices = snapshot.pcieDevices;
  if (Array.isArray(snapshot.vmGroups)) hostState.vmGroups = snapshot.vmGroups;
  if (Array.isArray(snapshot.affinityProcs)) hostState.affinityProcs = snapshot.affinityProcs;
  if (Array.isArray(snapshot.cpuGroups)) hostState.cpuGroups = snapshot.cpuGroups;
  if (Array.isArray(snapshot.liveVms)) hostState.liveVms = snapshot.liveVms;
  hostState.liveHostThreadReport = snapshot.liveHostThreadReport || null;
  if (snapshot.sys?.threads) hostState.mockThreads = String(snapshot.sys.threads);

  const page = typeof document !== 'undefined' ? document.getElementById('pageHost') : null;
  if (page && !page.hidden) renderHostPage();
}

export function applyLiveHostTelemetry(snapshot) {
  if (!snapshot) return;
  if (snapshot.sys) Object.assign(HOST_SYS, snapshot.sys);
  if (Array.isArray(snapshot.nics)) hostState.nics = snapshot.nics;
  if (Array.isArray(snapshot.gpus)) hostState.gpus = snapshot.gpus;
  if (Array.isArray(snapshot.disks)) hostState.disks = snapshot.disks;
  if (Array.isArray(snapshot.liveVms)) hostState.liveVms = snapshot.liveVms;
  if (snapshot.liveHostThreadReport) hostState.liveHostThreadReport = snapshot.liveHostThreadReport;
  const page = typeof document !== 'undefined' ? document.getElementById('pageHost') : null;
  if (page && !page.hidden) renderHostPage();
}

export function resetHostToDemo() {
  hostActionHandler = null;
  hostState.isLive = false;
  hostState.liveVms = [];
  hostState.powerPlans = [];
  hostState.affinityProcs = [];
  Object.assign(HOST_SYS, structuredClone(DEMO_HOST_SNAPSHOT.sys));
  hostState.saved = structuredClone(DEMO_HOST_SNAPSHOT.saved);
  hostState.draft = structuredClone(hostState.saved);
  hostState.nics = structuredClone(DEMO_HOST_SNAPSHOT.nics);
  hostState.switches = structuredClone(DEMO_HOST_SNAPSHOT.switches);
  hostState.gpus = structuredClone(DEMO_HOST_SNAPSHOT.gpus);
  hostState.disks = structuredClone(DEMO_HOST_SNAPSHOT.disks);
  hostState.pcieDevices = structuredClone(DEMO_HOST_SNAPSHOT.pcieDevices);
  hostState.cpuGroups = structuredClone(DEMO_HOST_SNAPSHOT.cpuGroups);
  hostState.vmGroups = structuredClone(DEMO_HOST_SNAPSHOT.vmGroups);
  hostState.hyperVServiceState = DEMO_HOST_SNAPSHOT.hyperVServiceState;
  hostState.hyperVEnabled = DEMO_HOST_SNAPSHOT.hyperVEnabled;
  hostState.cpuVirtOk = DEMO_HOST_SNAPSHOT.cpuVirtOk;
  hostState.gpuPvOk = DEMO_HOST_SNAPSHOT.gpuPvOk;
  hostState.iommuOk = DEMO_HOST_SNAPSHOT.iommuOk;
  hostState.isWindowsServer = DEMO_HOST_SNAPSHOT.isWindowsServer;
  hostState.liveHostThreadReport = null;
  hostState.mockThreads = '12';
  hostState.mockVmCount = '2';
  hostState.notice = null;

  const page = typeof document !== 'undefined' ? document.getElementById('pageHost') : null;
  if (page && !page.hidden) renderHostPage();
}
