using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging; // 引用 BitmapSource
using System.Windows.Threading;     // 引用 DispatcherTimer
using ExHyperV.Properties;
using ExHyperV.Tools;
using ExHyperV.Services;

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

    public class PageSizeItem
    {
        public string Description { get; set; } = string.Empty;
        public byte Value { get; set; }
    }

    public partial class VmDiskDetails : ObservableObject
    {
        [ObservableProperty] private string _name;
        [ObservableProperty] private string _path;
        [ObservableProperty] private string _diskType;
        [ObservableProperty] private long _currentSize;
        [ObservableProperty] private long _maxSize;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IoSpeedText))]
        private long _readSpeedBps; // 字节每秒

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IoSpeedText))]
        private long _writeSpeedBps; // 字节每秒

        public string IoSpeedText => $"↓ {FormatIoSpeed(_readSpeedBps)}   ↑ {FormatIoSpeed(_writeSpeedBps)}";

        public double UsagePercentage => _maxSize > 0 ? (double)_currentSize / _maxSize * 100 : 0;
        public string UsageText => $"{FormatBytes(_currentSize)} / {FormatBytes(_maxSize)}";


        private string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double dblSByte = bytes;
            while (dblSByte >= 1024 && i < suffixes.Length - 1)
            {
                dblSByte /= 1024;
                i++;
            }
            return $"{dblSByte:0.##} {suffixes[i]}";
        }
        private string FormatIoSpeed(long bps)
        {
            string[] suffixes = { "B/s", "KB/s", "MB/s", "GB/s" };
            int i = 0;
            double dblSpeed = bps;
            while (dblSpeed >= 1024 && i < suffixes.Length - 1)
            {
                dblSpeed /= 1024;
                i++;
            }
            return $"{dblSpeed:0.#} {suffixes[i]}";
        }
    }

    public class VmStorageSlot
    {
        public string ControllerType { get; set; } = "SCSI";
        public int ControllerNumber { get; set; } = 0;
        public int Location { get; set; } = 0;
    }

    public partial class VmStorageItem : ObservableObject
    {
        [ObservableProperty] private string _driveType;
        [ObservableProperty] private string _diskType;
        [ObservableProperty] private string _pathOrDiskNumber;
        [ObservableProperty] private int _controllerLocation;
        [ObservableProperty] private string _controllerType;
        [ObservableProperty] private int _controllerNumber;

        [ObservableProperty] private int _diskNumber;
        [ObservableProperty] private string _diskModel;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SizeDisplay))] // 当 DiskSizeGB 改变时通知 SizeDisplay 更新
        private double _diskSizeGB;

        [ObservableProperty] private string _serialNumber;

        public string DisplayName
        {
            get
            {
                if (_diskType == "Physical" && !string.IsNullOrEmpty(_diskModel))
                    return _diskModel;

                if (_diskType == "Virtual" && !string.IsNullOrEmpty(_pathOrDiskNumber))
                {
                    try { return Path.GetFileName(_pathOrDiskNumber); }
                    catch { return Resources.Model_Drive_VirtualDisk; }
                }

                return _driveType == "HardDisk" ? Resources.Model_Drive_VirtualHardDisk : Resources.Model_Drive_OpticalDrive;
            }
        }

        public string SourceTypeDisplayName => _diskType == "Physical" ? Resources.Model_Drive_SourcePhysical : Resources.Model_Drive_SourceVirtual;

        public string Icon => _driveType == "HardDisk" ? "\uEC59" : "\uE958";

        public string SizeDisplay
        {
            get
            {
                if (DiskSizeGB <= 0) return "unknown";
                if (DiskSizeGB < 1.0)
                {
                    double sizeMB = DiskSizeGB * 1024.0;
                    if (sizeMB < 1.0)
                    {
                        return $"{sizeMB * 1024.0:N0} KB";
                    }
                    return $"{sizeMB:N1} MB";
                }
                return $"{DiskSizeGB:N1} GB";
            }
        }
    }

    public partial class VmMemorySettings : ObservableObject
    {
        [ObservableProperty] private long _startup;
        [ObservableProperty] private bool _dynamicMemoryEnabled;
        [ObservableProperty] private long _minimum;
        [ObservableProperty] private long _maximum;
        [ObservableProperty] private int _buffer;
        [ObservableProperty] private int _priority;
        [ObservableProperty] private byte? _backingPageSize;

        public List<PageSizeItem> AvailablePageSizes { get; } = new List<PageSizeItem>
        {
            new PageSizeItem { Description = "标准 (4KB)", Value = 0 },
            new PageSizeItem { Description = "大页 (2MB)", Value = 1 },
            new PageSizeItem { Description = "巨页 (1GB)", Value = 2 }
        };

        [ObservableProperty] private byte? _memoryEncryptionPolicy;

        public VmMemorySettings Clone() => (VmMemorySettings)this.MemberwiseClone();

        public void Restore(VmMemorySettings other)
        {
            if (other == null) return;
            _startup = other.Startup;
            _dynamicMemoryEnabled = other.DynamicMemoryEnabled;
            _minimum = other.Minimum;
            _maximum = other.Maximum;
            _buffer = other.Buffer;
            _priority = other.Priority;
            _backingPageSize = other.BackingPageSize;
            _memoryEncryptionPolicy = other.MemoryEncryptionPolicy;
        }
    }

    public partial class VmProcessorSettings : ObservableObject
    {
        [ObservableProperty] private int _count;
        [ObservableProperty] private int _reserve;
        [ObservableProperty] private int _maximum;
        [ObservableProperty] private int _relativeWeight;
        [ObservableProperty] private bool? _exposeVirtualizationExtensions;
        [ObservableProperty] private bool? _enableHostResourceProtection;
        [ObservableProperty] private bool? _compatibilityForMigrationEnabled;
        [ObservableProperty] private bool? _compatibilityForOlderOperatingSystemsEnabled;
        [ObservableProperty] private SmtMode? _smtMode;
        [ObservableProperty] private bool? _disableSpeculationControls;
        [ObservableProperty] private bool? _hideHypervisorPresent;
        [ObservableProperty] private bool? _enablePerfmonArchPmu;
        [ObservableProperty] private bool? _allowAcountMcount;
        [ObservableProperty] private bool? _enableSocketTopology;

        public VmProcessorSettings Clone() => (VmProcessorSettings)this.MemberwiseClone();
        public void Restore(VmProcessorSettings other)
        {
            if (other == null) return;
            _count = other.Count;
            _reserve = other.Reserve;
            _maximum = other.Maximum;
            _relativeWeight = other.RelativeWeight;
            _exposeVirtualizationExtensions = other.ExposeVirtualizationExtensions;
            _enableHostResourceProtection = other.EnableHostResourceProtection;
            _compatibilityForMigrationEnabled = other.CompatibilityForMigrationEnabled;
            _compatibilityForOlderOperatingSystemsEnabled = other.CompatibilityForOlderOperatingSystemsEnabled;
            _smtMode = other.SmtMode;
            _disableSpeculationControls = other.DisableSpeculationControls;
            _hideHypervisorPresent = other.HideHypervisorPresent;
            _enablePerfmonArchPmu = other.EnablePerfmonArchPmu;
            _allowAcountMcount = other.AllowAcountMcount;
            _enableSocketTopology = other.EnableSocketTopology;
        }
    }

    public partial class VmCoreModel : ObservableObject
    {
        [ObservableProperty] private int _coreId;
        [ObservableProperty] private double _usage;
        [ObservableProperty] private PointCollection _historyPoints;
        [ObservableProperty] private CoreType _coreType = CoreType.Unknown;
        [ObservableProperty] private bool _isSelected;
    }

    public partial class VmInstanceInfo : ObservableObject
    {
        // ----------------------------------------------------------------------------------
        // 基础信息与状态 (已找回丢失的 OsType 和 RawUptime)
        // ----------------------------------------------------------------------------------
        [ObservableProperty] private Guid _id;
        [ObservableProperty] private string _name;
        [ObservableProperty] private string _notes;
        [ObservableProperty] private int _generation;
        [ObservableProperty] private string _version;
        [ObservableProperty] private string _osType;           // <-- 找回
        [ObservableProperty] private string _state;
        [ObservableProperty] private string _uptime = "00:00:00";
        [ObservableProperty] private bool _isRunning;
        [ObservableProperty] private BitmapSource? _thumbnail;

        private TimeSpan _anchorUptime;                         // 内部计时基准
        public TimeSpan RawUptime => _anchorUptime;             // <-- 找回逻辑，修复 CS1061

        // ----------------------------------------------------------------------------------
        // 处理器 (CPU) 与 内存
        // ----------------------------------------------------------------------------------
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigSummary))] // <--- 关键
        private int _cpuCount;
        [ObservableProperty] private double _averageUsage;
        [ObservableProperty] private int _columns = 2;
        [ObservableProperty] private int _rows = 1;
        public ObservableCollection<VmCoreModel> Cores { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigSummary))] // <--- 关键
        private double _memoryGb;

        [ObservableProperty] private VmProcessorSettings _processor;
        [ObservableProperty] private VmMemorySettings _memorySettings;

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

        // ----------------------------------------------------------------------------------
        // 存储 (Storage)
        // ----------------------------------------------------------------------------------
        public ObservableCollection<VmDiskDetails> Disks { get; } = new();
        public ObservableCollection<VmStorageItem> StorageItems { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigSummary))]
        private double _totalDiskSizeGb;

        // ----------------------------------------------------------------------------------
        // 显卡 (GPU) 分区与资源配置 (修复 XLS0432)
        // ----------------------------------------------------------------------------------

        [ObservableProperty] private string _gpuVendor;          // 制造商
        [ObservableProperty] private string _physicalGpuId;      // 宿主物理实例 ID
        [ObservableProperty] private string _hostDriverVersion;  // 宿主驱动版本

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasGpu))]
        [NotifyPropertyChangedFor(nameof(GpuDisplayLabel))] // ✅ 关键：当 GpuName 改变时，通知标签刷新
        private string _gpuName;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasGpu))]
        [NotifyPropertyChangedFor(nameof(GpuDisplayLabel))]
        private ObservableCollection<VmGpuAssignment> _assignedGpus = new();

        // ✅ 完美的智能标签：支持详细列表和摘要字符串的回退
        public string GpuDisplayLabel
        {
            get
            {
                // 情况 1：如果详细列表已经加载了，执行“工业感”逻辑
                if (AssignedGpus != null && AssignedGpus.Count > 0)
                {
                    var groups = AssignedGpus.GroupBy(g => g.Name).ToList();
                    var mainGroup = groups[0];
                    string mainName = mainGroup.Key;
                    int mainCount = mainGroup.Count();

                    string result = mainName;
                    if (mainCount > 1) result += $" *{mainCount}";
                    if (groups.Count > 1) result += " +";
                    return result;
                }

                // 情况 2：如果详细列表为空（初始状态），回退使用摘要字符串
                if (!string.IsNullOrEmpty(GpuName))
                {
                    return GpuName;
                }

                return "无";
            }
        }
        // ✅ 辅助：手动触发更新的方法（供 ViewModel 调用）
        public void RefreshGpuSummary()
        {
            OnPropertyChanged(nameof(GpuDisplayLabel));
        }


        public bool HasGpu => (AssignedGpus != null && AssignedGpus.Count > 0) || !string.IsNullOrEmpty(GpuName);
        partial void OnGpuNameChanged(string value) => OnPropertyChanged(nameof(HasGpu));

        // MMIO 地址空间配置
        [ObservableProperty] private string _lowMMIO;
        [ObservableProperty] private string _highMMIO;
        [ObservableProperty] private string _highMMIOBase;       // <-- 修复 XAML 报错
        [ObservableProperty] private string _guestControlled;

        // GPU 实时监控数据
        [ObservableProperty][NotifyPropertyChangedFor(nameof(GpuMaxUsage))] private double _gpu3dUsage;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(GpuMaxUsage))] private double _gpuCopyUsage;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(GpuMaxUsage))] private double _gpuEncodeUsage;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(GpuMaxUsage))] private double _gpuDecodeUsage;

        public double GpuMaxUsage => Math.Max(Math.Max(Gpu3dUsage, GpuCopyUsage), Math.Max(GpuEncodeUsage, GpuDecodeUsage));

        // GPU 波形图数据点
        [ObservableProperty] private PointCollection _gpu3dHistoryPoints;
        [ObservableProperty] private PointCollection _gpuCopyHistoryPoints;
        [ObservableProperty] private PointCollection _gpuEncodeHistoryPoints;
        [ObservableProperty] private PointCollection _gpuDecodeHistoryPoints;

        private readonly LinkedList<double> _gpu3dHistory = new();
        private readonly LinkedList<double> _gpuCopyHistory = new();
        private readonly LinkedList<double> _gpuEncodeHistory = new();
        private readonly LinkedList<double> _gpuDecodeHistory = new();

        // ----------------------------------------------------------------------------------
        // 构造函数与初始化
        // ----------------------------------------------------------------------------------
        public VmInstanceInfo(Guid id, string name)
        {
            _id = id;
            _name = name;

            InitializeGpuHistory(); // 初始化 GPU 队列实现平滑滚动

            Disks.CollectionChanged += (s, e) =>
            {
                TotalDiskSizeGb = Disks.Sum(d => d.MaxSize) / 1073741824.0;
                OnPropertyChanged(nameof(ConfigSummary));
            };
        }

        private void InitializeGpuHistory()
        {
            _gpu3dHistory.Clear(); _gpuCopyHistory.Clear();
            _gpuEncodeHistory.Clear(); _gpuDecodeHistory.Clear();

            for (int i = 0; i < MaxHistoryLength; i++)
            {
                _gpu3dHistory.AddLast(0.0);
                _gpuCopyHistory.AddLast(0.0);
                _gpuEncodeHistory.AddLast(0.0);
                _gpuDecodeHistory.AddLast(0.0);
            }
            RefreshGpuPoints();
        }

        // ----------------------------------------------------------------------------------
        // 业务逻辑方法 (内存、GPU、状态更新)
        // ----------------------------------------------------------------------------------
        public string MemoryUsageString => _assignedMemoryGb.ToString("N1");
        public string MemoryDemandString => _demandMemoryGb.ToString("N1");

        public string MemoryLimitString
        {
            get
            {
                if (_memorySettings != null)
                {
                    double limitMb = _memorySettings.DynamicMemoryEnabled ? _memorySettings.Maximum : _memorySettings.Startup;
                    return (limitMb / 1024.0).ToString("N1");
                }
                return _memoryGb > 0 ? _memoryGb.ToString("N1") : "0.0";
            }
        }

        public void UpdateMemoryStatus(long assignedMb, int availablePercent)
        {
            if (!_isRunning || assignedMb == 0)
            {
                AssignedMemoryGb = 0; DemandMemoryGb = 0; UpdateHistoryPoints(0); return;
            }
            AssignedMemoryGb = assignedMb / 1024.0;
            double usedPercentage = (100 - availablePercent) / 100.0;
            DemandMemoryGb = AssignedMemoryGb * usedPercentage;
            UpdateHistoryPoints(100 - availablePercent);
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
        public void UpdateGpuStats(VmQueryService.GpuUsageData data)
        {
            if (!IsRunning)
            {
                Gpu3dUsage = 0; GpuCopyUsage = 0; GpuEncodeUsage = 0; GpuDecodeUsage = 0;
            }
            else
            {
                Gpu3dUsage = data.Gpu3d; GpuCopyUsage = data.GpuCopy;
                GpuEncodeUsage = data.GpuEncode; GpuDecodeUsage = data.GpuDecode;
            }

            UpdateSingleGpuHistory(_gpu3dHistory, Gpu3dUsage);
            UpdateSingleGpuHistory(_gpuCopyHistory, GpuCopyUsage);
            UpdateSingleGpuHistory(_gpuEncodeHistory, GpuEncodeUsage);
            UpdateSingleGpuHistory(_gpuDecodeHistory, GpuDecodeUsage);

            RefreshGpuPoints();
            OnPropertyChanged(nameof(GpuMaxUsage));
        }

        private void UpdateSingleGpuHistory(LinkedList<double> history, double value)
        {
            history.AddLast(Math.Max(0, Math.Min(100, value)));
            if (history.Count > MaxHistoryLength) history.RemoveFirst();
        }

        private void RefreshGpuPoints()
        {
            Gpu3dHistoryPoints = CalculateGpuPoints(_gpu3dHistory);
            GpuCopyHistoryPoints = CalculateGpuPoints(_gpuCopyHistory);
            GpuEncodeHistoryPoints = CalculateGpuPoints(_gpuEncodeHistory);
            GpuDecodeHistoryPoints = CalculateGpuPoints(_gpuDecodeHistory);
        }

        private static PointCollection CalculateGpuPoints(LinkedList<double> history)
        {
            double w = 100.0, h = 100.0;
            double step = w / (MaxHistoryLength - 1);
            var points = new PointCollection(MaxHistoryLength + 2) { new Point(0, h) };
            int i = 0;
            foreach (var val in history) points.Add(new Point(i++ * step, h - val));
            points.Add(new Point(w, h));
            points.Freeze();
            return points;
        }

        public string ConfigSummary
        {
            get
            {
                string diskPart;
                if (Disks.Count == 0)
                {
                    diskPart = "无硬盘";
                }
                else
                {
                    diskPart = string.Join(" + ", Disks
                        .Select(d => d.MaxSize / 1073741824.0)
                        .OrderByDescending(g => g)
                        .Select(g => g >= 1 ? $"{g:0.#}G" : $"{g * 1024:0}M"));
                }
                return $"{_cpuCount} Cores / {_memoryGb:0.#}GB RAM / {diskPart}";
            }
        }
        public void SyncBackendData(string realState, TimeSpan realUptime)
        {
            _backendState = realState; _anchorUptime = realUptime; _anchorLocalTime = DateTime.Now;
            if (_transientState != null && ShouldClearTransientState(realState)) _transientState = null;
            RefreshStateDisplay();
            TickUptime();
        }

        public void TickUptime()
        {
            if (!_isRunning) { Uptime = "00:00:00"; return; }
            var currentTotal = _anchorUptime + (DateTime.Now - _anchorLocalTime);
            Uptime = currentTotal.TotalDays >= 1
                ? $"{(int)currentTotal.TotalDays}.{currentTotal.Hours:D2}:{currentTotal.Minutes:D2}:{currentTotal.Seconds:D2}"
                : $"{currentTotal.Hours:D2}:{currentTotal.Minutes:D2}:{currentTotal.Seconds:D2}";
        }

        private void RefreshStateDisplay()
        {
            State = _transientState ?? _backendState;
            IsRunning = !string.IsNullOrEmpty(State) && !new[] { "已关机", "Off", "已暂停", "Paused", "已保存", "Saved" }.Contains(State);
            if (!IsRunning) UpdateMemoryStatus(0, 0);
        }

        private bool ShouldClearTransientState(string backend)
        {
            if ((_transientState == "正在启动" || _transientState == "正在重启") && (backend == "运行中" || backend == "Running")) return true;
            if ((_transientState == "正在关闭" || _transientState == "正在保存") && (backend == "已关机" || backend == "Off" || backend == "已保存" || backend == "Saved" || backend == "已暂停" || backend == "Paused")) return true;
            if (_transientState == "正在暂停" && (backend == "已暂停" || backend == "Paused" || backend == "已保存" || backend == "Saved")) return true;
            return false;
        }

        public void SetTransientState(string text) { _transientState = text; RefreshStateDisplay(); }
        public void ClearTransientState() { _transientState = null; RefreshStateDisplay(); }

        private DateTime _anchorLocalTime;
        private string _transientState, _backendState;
        public IAsyncRelayCommand<string> ControlCommand { get; set; }
    }
}