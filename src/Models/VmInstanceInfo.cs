using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;       // 用于 Point
using System.Windows.Media; // 用于 PointCollection

namespace ExHyperV.Models
{
    public enum SmtMode { Inherit, SingleThread, MultiThread }
    public enum CoreType { Unknown, Performance, Efficient }

    public class CpuCoreMetric
    {
        public string VmName { get; set; }
        public int CoreId { get; set; }
        public float Usage { get; set; }
        public bool IsRunning { get; set; }
    }

    // ==========================================
    // ↓↓↓ 内存设置模型 ↓↓↓
    // ==========================================
    // ==========================================
    // ↓↓↓ 内存设置模型 ↓↓↓
    // ==========================================
    public partial class VmMemorySettings : ObservableObject
    {
        [ObservableProperty] private long _startup;
        [ObservableProperty] private bool _dynamicMemoryEnabled;
        [ObservableProperty] private long _minimum;
        [ObservableProperty] private long _maximum;
        [ObservableProperty] private int _buffer;
        [ObservableProperty] private int _priority;

        // 属性值
        [ObservableProperty] private bool _enableEpf;
        [ObservableProperty] private bool _hugePagesEnabled;
        [ObservableProperty] private bool _enableHotHint;
        [ObservableProperty] private bool _enableColdHint;

        // 支持位 (用于 UI 禁用/启用)
        [ObservableProperty] private bool _isEpfSupported = true;
        [ObservableProperty] private bool _isHugePagesSupported = true;
        [ObservableProperty] private bool _isHotHintSupported = true;
        [ObservableProperty] private bool _isColdHintSupported = true;

        public VmMemorySettings Clone() => (VmMemorySettings)this.MemberwiseClone();

        public void Restore(VmMemorySettings other)
        {
            if (other == null) return;
            Startup = other.Startup;
            DynamicMemoryEnabled = other.DynamicMemoryEnabled;
            Minimum = other.Minimum;
            Maximum = other.Maximum;
            Buffer = other.Buffer;
            Priority = other.Priority;
            EnableEpf = other.EnableEpf;
            HugePagesEnabled = other.HugePagesEnabled;
            EnableHotHint = other.EnableHotHint;
            EnableColdHint = other.EnableColdHint;

            // 同步支持位
            IsEpfSupported = other.IsEpfSupported;
            IsHugePagesSupported = other.IsHugePagesSupported;
            IsHotHintSupported = other.IsHotHintSupported;
            IsColdHintSupported = other.IsColdHintSupported;
        }
    }

    // ==========================================
    // ↓↓↓ 处理器设置模型 ↓↓↓
    // ==========================================
    public partial class VmProcessorSettings : ObservableObject
    {
        [ObservableProperty] private int _count;
        [ObservableProperty] private int _reserve;
        [ObservableProperty] private int _maximum;
        [ObservableProperty] private int _relativeWeight;
        [ObservableProperty] private bool _exposeVirtualizationExtensions;
        [ObservableProperty] private bool _enableHostResourceProtection;
        [ObservableProperty] private bool _compatibilityForMigrationEnabled;
        [ObservableProperty] private bool _compatibilityForOlderOperatingSystemsEnabled;
        [ObservableProperty] private SmtMode _smtMode;

        public VmProcessorSettings Clone() => (VmProcessorSettings)this.MemberwiseClone();
        public void Restore(VmProcessorSettings other)
        {
            if (other == null) return;
            Count = other.Count; Reserve = other.Reserve; Maximum = other.Maximum; RelativeWeight = other.RelativeWeight;
            ExposeVirtualizationExtensions = other.ExposeVirtualizationExtensions;
            EnableHostResourceProtection = other.EnableHostResourceProtection;
            CompatibilityForMigrationEnabled = other.CompatibilityForMigrationEnabled;
            CompatibilityForOlderOperatingSystemsEnabled = other.CompatibilityForOlderOperatingSystemsEnabled;
            SmtMode = other.SmtMode;
        }
    }

    // ==========================================
    // ↓↓↓ 核心 UI 模型 ↓↓↓
    // ==========================================
    public partial class VmCoreModel : ObservableObject
    {
        [ObservableProperty] private int _coreId;
        [ObservableProperty] private double _usage;
        [ObservableProperty] private PointCollection _historyPoints;
        [ObservableProperty] private CoreType _coreType = CoreType.Unknown;
        [ObservableProperty] private bool _isSelected;
    }

    // ==========================================
    // ↓↓↓ 虚拟机实例主模型 ↓↓↓
    // ==========================================
    public partial class VmInstanceInfo : ObservableObject
    {
        [ObservableProperty] private Guid _id;
        [ObservableProperty] private string _name;
        [ObservableProperty] private string _notes;
        [ObservableProperty] private int _generation;
        [ObservableProperty] private int _cpuCount;

        // 静态配置的内存 (用于列表初始化显示)
        [ObservableProperty] private double _memoryGb;

        [ObservableProperty] private string _diskSize;
        [ObservableProperty] private string _osType;
        [ObservableProperty] private string _lowMMIO;
        [ObservableProperty] private string _highMMIO;
        [ObservableProperty] private string _guestControlled;
        [ObservableProperty] private Dictionary<string, string> _gPUs = new();
        [ObservableProperty] private double _averageUsage;

        [ObservableProperty] private string _state;
        [ObservableProperty] private string _uptime = "00:00:00";
        [ObservableProperty] private bool _isRunning;

        [ObservableProperty] private VmProcessorSettings _processor;
        [ObservableProperty] private VmMemorySettings _memorySettings;

        // ==========================================
        // ↓↓↓ 实时内存监控属性与图表 ↓↓↓
        // ==========================================

        // 1. 已分配内存 (Assigned) - 对应 WMI MemoryUsage
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MemoryUsageString))]
        [NotifyPropertyChangedFor(nameof(MemoryLimitString))]
        private double _assignedMemoryGb;

        // 2. 内存需求 (Demand) - 计算得出: Assigned * (1 - Buffer%)
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MemoryDemandString))]
        private double _demandMemoryGb;

        // 3. 可用缓冲百分比 - 对应 WMI MemoryAvailable
        [ObservableProperty]
        private int _availableMemoryPercent;

        // 4. 【已修复】内存压力 (0-100)，用于显示 UI 上的大数字
        [ObservableProperty]
        private int _memoryPressure;

        // 5. 内存历史曲线数据 (绑定给 Polyline/Polygon)
        [ObservableProperty]
        private PointCollection _memoryHistoryPoints;

        // 内部历史记录队列
        private readonly LinkedList<double> _memoryUsageHistory = new();
        private const int MaxHistoryLength = 60; // 保持 60 个点

        // --- Dashboard 字符串绑定 ---

        public string MemoryUsageString => AssignedMemoryGb.ToString("N1");
        public string MemoryDemandString => DemandMemoryGb.ToString("N1");

        public string MemoryLimitString
        {
            get
            {
                if (MemorySettings != null)
                {
                    double limitMb = MemorySettings.DynamicMemoryEnabled ? MemorySettings.Maximum : MemorySettings.Startup;
                    return (limitMb / 1024.0).ToString("N1");
                }
                return MemoryGb > 0 ? MemoryGb.ToString("N1") : "0.0";
            }
        }

        /// <summary>
        /// 核心方法：根据 WMI 实时数据更新内存状态
        /// </summary>
        public void UpdateMemoryStatus(long assignedMb, int availablePercent)
        {
            // 0. 【关键修复】如果没开机，或者分配量为0，直接归零
            if (!IsRunning || assignedMb == 0)
            {
                AssignedMemoryGb = 0;
                DemandMemoryGb = 0;
                AvailableMemoryPercent = 0;
                MemoryPressure = 0;
                UpdateHistoryPoints(0); // 图表走直线 0
                return;
            }

            // 1. 更新已分配
            double newAssignedGb = assignedMb / 1024.0;

            // 2. 计算需求
            double usedPercentage = (100 - availablePercent) / 100.0;
            double newDemandGb = newAssignedGb * usedPercentage;

            // 3. 计算压力值
            int pressure = 100 - availablePercent;

            // 4. 赋值
            if (Math.Abs(AssignedMemoryGb - newAssignedGb) > 0.01 ||
                Math.Abs(DemandMemoryGb - newDemandGb) > 0.01 ||
                AvailableMemoryPercent != availablePercent)
            {
                AssignedMemoryGb = newAssignedGb;
                DemandMemoryGb = newDemandGb;
                AvailableMemoryPercent = availablePercent;
                MemoryPressure = pressure;
            }

            // 5. 更新历史曲线
            UpdateHistoryPoints(pressure);
        }
        private void UpdateHistoryPoints(double pressurePercent)
        {
            // 1. 限制范围 0-100
            pressurePercent = Math.Max(0, Math.Min(100, pressurePercent));

            // 2. 存入历史队列
            _memoryUsageHistory.AddLast(pressurePercent);
            if (_memoryUsageHistory.Count > MaxHistoryLength)
                _memoryUsageHistory.RemoveFirst();

            var points = new PointCollection();

            // =========================================================
            //  右对齐逻辑：数据不足60个时，紧贴右边显示
            // =========================================================

            int count = _memoryUsageHistory.Count;
            int offset = MaxHistoryLength - count; // 计算向右偏移量

            // 起始点
            points.Add(new Point(offset, 100));

            int i = 0;
            foreach (var val in _memoryUsageHistory)
            {
                // X 坐标 = 偏移量 + 当前索引
                double x = offset + i;
                // Y 坐标 = 100 - 百分比
                points.Add(new Point(x, 100 - val));
                i++;
            }

            // 结束点：右下角
            points.Add(new Point(MaxHistoryLength - 1, 100));

            points.Freeze();
            MemoryHistoryPoints = points;
        }

        partial void OnMemorySettingsChanged(VmMemorySettings value)
        {
            OnPropertyChanged(nameof(MemoryLimitString));
        }

        // ==========================================
        // ↓↓↓ UI 布局与状态逻辑 ↓↓↓
        // ==========================================

        [ObservableProperty] private int _columns = 2;
        [ObservableProperty] private int _rows = 1;

        public ObservableCollection<VmCoreModel> Cores { get; } = new();
        public IAsyncRelayCommand<string> ControlCommand { get; set; }
        public string ConfigSummary => $"{CpuCount} Cores / {MemoryGb:N1}GB RAM / {DiskSize}";

        private TimeSpan _anchorUptime;
        private DateTime _anchorLocalTime;
        private string _transientState;
        private string _backendState;

        public TimeSpan RawUptime => _anchorUptime;

        public VmInstanceInfo(Guid id, string name)
        {
            _id = id;
            _name = name;
        }

        public VmInstanceInfo(Guid id, string name, string state, string osType, int cpu, double ram, string disk, TimeSpan uptime)
        {
            _id = id; _name = name; _osType = osType;
            _cpuCount = cpu; _memoryGb = ram; _diskSize = disk;
            _assignedMemoryGb = ram;
            SyncBackendData(state, uptime);
        }

        public void SetTransientState(string optimisticText)
        {
            _transientState = optimisticText;
            RefreshStateDisplay();
        }

        public void ClearTransientState()
        {
            _transientState = null;
            RefreshStateDisplay();
        }

        public void SyncBackendData(string realState, TimeSpan realUptime)
        {
            _backendState = realState;
            _anchorUptime = realUptime;
            _anchorLocalTime = DateTime.Now;
            if (_transientState != null)
            {
                if (ShouldClearTransientState(realState))
                {
                    _transientState = null;
                }
            }
            RefreshStateDisplay();
            TickUptime();
        }

        public void TickUptime()
        {
            if (!IsRunning) { Uptime = "00:00:00"; return; }
            var delta = DateTime.Now - _anchorLocalTime;
            var currentTotal = _anchorUptime + delta;
            if (currentTotal.TotalDays >= 1)
                Uptime = $"{(int)currentTotal.TotalDays}.{currentTotal.Hours:D2}:{currentTotal.Minutes:D2}:{currentTotal.Seconds:D2}";
            else
                Uptime = $"{currentTotal.Hours:D2}:{currentTotal.Minutes:D2}:{currentTotal.Seconds:D2}";
        }

        private void RefreshStateDisplay()
        {
            State = _transientState ?? _backendState;
            IsRunning = CheckIfRunning(State);
            if (!IsRunning)
            {
                UpdateMemoryStatus(0, 0);
            }
        }

        private bool CheckIfRunning(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s != "已关机" && s != "Off" &&
                   s != "已暂停" && s != "Paused" &&
                   s != "已保存" && s != "Saved";
        }

        private bool ShouldClearTransientState(string backend)
        {
            if (_transientState == "正在启动" || _transientState == "正在重启")
            {
                if (backend == "运行中" || backend == "Running") return true;
                return false;
            }
            if (_transientState == "正在关闭" || _transientState == "正在保存")
            {
                if (backend == "已关机" || backend == "Off" || backend == "已保存" || backend == "Saved" || backend == "已暂停" || backend == "Paused") return true;
                return false;
            }
            if (_transientState == "正在暂停")
            {
                if (backend == "已暂停" || backend == "Paused" || backend == "已保存" || backend == "Saved") return true;
                return false;
            }
            return false;
        }
    }
}