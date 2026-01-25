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
    // ↓↓↓ 内存设置模型 (已清理) ↓↓↓
    // ==========================================
    public partial class VmMemorySettings : ObservableObject
    {
        [ObservableProperty] private long _startup;
        [ObservableProperty] private bool _dynamicMemoryEnabled;
        [ObservableProperty] private long _minimum;
        [ObservableProperty] private long _maximum;
        [ObservableProperty] private int _buffer;
        [ObservableProperty] private int _priority;

        // 仅保留大页内存设置
        [ObservableProperty] private bool _hugePagesEnabled;
        [ObservableProperty] private bool _isHugePagesSupported = true;

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

            // 同步大页内存状态
            HugePagesEnabled = other.HugePagesEnabled;
            IsHugePagesSupported = other.IsHugePagesSupported;
        }
    }

    // ==========================================
    // ↓↓↓ 处理器设置模型 (已添加高级功能) ↓↓↓
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

        // --- 新增高级 CPU 功能 ---
        [ObservableProperty] private bool _disableSpeculationControls;       // 禁用推测执行保护
        [ObservableProperty] private bool _hideHypervisorPresent;            // 隐藏虚拟化标识
        [ObservableProperty] private bool _enablePerfmonArchPmu;             // 暴露硬件性能计数器
        [ObservableProperty] private bool _enablePerfmonIpt;                 // 暴露处理器追踪 (IPT)
        [ObservableProperty] private bool _allowAcountMcount;                // 允许访问 ACOUNT/MCOUNT

        public VmProcessorSettings Clone() => (VmProcessorSettings)this.MemberwiseClone();
        public void Restore(VmProcessorSettings other)
        {
            if (other == null) return;
            Count = other.Count;
            Reserve = other.Reserve;
            Maximum = other.Maximum;
            RelativeWeight = other.RelativeWeight;
            ExposeVirtualizationExtensions = other.ExposeVirtualizationExtensions;
            EnableHostResourceProtection = other.EnableHostResourceProtection;
            CompatibilityForMigrationEnabled = other.CompatibilityForMigrationEnabled;
            CompatibilityForOlderOperatingSystemsEnabled = other.CompatibilityForOlderOperatingSystemsEnabled;
            SmtMode = other.SmtMode;

            // 同步新增高级属性
            DisableSpeculationControls = other.DisableSpeculationControls;
            HideHypervisorPresent = other.HideHypervisorPresent;
            EnablePerfmonArchPmu = other.EnablePerfmonArchPmu;
            EnablePerfmonIpt = other.EnablePerfmonIpt;
            AllowAcountMcount = other.AllowAcountMcount;
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

        // 监控属性
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MemoryUsageString))]
        [NotifyPropertyChangedFor(nameof(MemoryLimitString))]
        private double _assignedMemoryGb;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MemoryDemandString))]
        private double _demandMemoryGb;

        [ObservableProperty] private int _availableMemoryPercent;
        [ObservableProperty] private int _memoryPressure;
        [ObservableProperty] private PointCollection _memoryHistoryPoints;

        private readonly LinkedList<double> _memoryUsageHistory = new();
        private const int MaxHistoryLength = 60;

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

        public void UpdateMemoryStatus(long assignedMb, int availablePercent)
        {
            if (!IsRunning || assignedMb == 0)
            {
                AssignedMemoryGb = 0; DemandMemoryGb = 0; AvailableMemoryPercent = 0; MemoryPressure = 0;
                UpdateHistoryPoints(0); return;
            }

            double newAssignedGb = assignedMb / 1024.0;
            double usedPercentage = (100 - availablePercent) / 100.0;
            double newDemandGb = newAssignedGb * usedPercentage;
            int pressure = 100 - availablePercent;

            AssignedMemoryGb = newAssignedGb;
            DemandMemoryGb = newDemandGb;
            AvailableMemoryPercent = availablePercent;
            MemoryPressure = pressure;

            UpdateHistoryPoints(pressure);
        }

        private void UpdateHistoryPoints(double pressurePercent)
        {
            pressurePercent = Math.Max(0, Math.Min(100, pressurePercent));
            _memoryUsageHistory.AddLast(pressurePercent);
            if (_memoryUsageHistory.Count > MaxHistoryLength) _memoryUsageHistory.RemoveFirst();

            var points = new PointCollection();
            int count = _memoryUsageHistory.Count;
            int offset = MaxHistoryLength - count;

            points.Add(new Point(offset, 100));
            int i = 0;
            foreach (var val in _memoryUsageHistory)
            {
                points.Add(new Point(offset + i, 100 - val));
                i++;
            }
            points.Add(new Point(MaxHistoryLength - 1, 100));
            points.Freeze();
            MemoryHistoryPoints = points;
        }

        partial void OnMemorySettingsChanged(VmMemorySettings value) => OnPropertyChanged(nameof(MemoryLimitString));

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

        public VmInstanceInfo(Guid id, string name) { _id = id; _name = name; }

        public void SetTransientState(string optimisticText) { _transientState = optimisticText; RefreshStateDisplay(); }
        public void ClearTransientState() { _transientState = null; RefreshStateDisplay(); }

        public void SyncBackendData(string realState, TimeSpan realUptime)
        {
            _backendState = realState;
            _anchorUptime = realUptime;
            _anchorLocalTime = DateTime.Now;
            if (_transientState != null && ShouldClearTransientState(realState)) _transientState = null;
            RefreshStateDisplay();
            TickUptime();
        }

        public void TickUptime()
        {
            if (!IsRunning) { Uptime = "00:00:00"; return; }
            var currentTotal = _anchorUptime + (DateTime.Now - _anchorLocalTime);
            Uptime = currentTotal.TotalDays >= 1
                ? $"{(int)currentTotal.TotalDays}.{currentTotal.Hours:D2}:{currentTotal.Minutes:D2}:{currentTotal.Seconds:D2}"
                : $"{currentTotal.Hours:D2}:{currentTotal.Minutes:D2}:{currentTotal.Seconds:D2}";
        }

        private void RefreshStateDisplay()
        {
            State = _transientState ?? _backendState;
            IsRunning = CheckIfRunning(State);
            if (!IsRunning) UpdateMemoryStatus(0, 0);
        }

        private bool CheckIfRunning(string s) => !string.IsNullOrEmpty(s) && s != "已关机" && s != "Off" && s != "已暂停" && s != "Paused" && s != "已保存" && s != "Saved";

        private bool ShouldClearTransientState(string backend)
        {
            if ((_transientState == "正在启动" || _transientState == "正在重启") && (backend == "运行中" || backend == "Running")) return true;
            if ((_transientState == "正在关闭" || _transientState == "正在保存") && (backend == "已关机" || backend == "Off" || backend == "已保存" || backend == "Saved" || backend == "已暂停" || backend == "Paused")) return true;
            return _transientState == "正在暂停" && (backend == "已暂停" || backend == "Paused" || backend == "已保存" || backend == "Saved");
        }
    }
}