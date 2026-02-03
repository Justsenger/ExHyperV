using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ExHyperV.Properties;
using ExHyperV.Tools;

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

        public string Icon => _driveType == "HardDisk" ? "\uEDA2" : "\uE958";

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
        [ObservableProperty] private Guid _id;
        [ObservableProperty] private string _name;
        [ObservableProperty] private string _notes;
        [ObservableProperty] private int _generation;
        [ObservableProperty] private string _version;
        [ObservableProperty] private int _cpuCount;

        public ObservableCollection<VmDiskDetails> Disks { get; } = new();
        public ObservableCollection<VmStorageItem> StorageItems { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigSummary))]
        private double _totalDiskSizeGb;

        [ObservableProperty] private double _memoryGb;
        [ObservableProperty] private string _osType;
        [ObservableProperty] private string _lowMMIO;
        [ObservableProperty] private string _highMMIO;
        [ObservableProperty] private string _guestControlled;
        [ObservableProperty] private string _gpuName;

        public bool HasGpu => !string.IsNullOrEmpty(_gpuName);
        partial void OnGpuNameChanged(string value) => OnPropertyChanged(nameof(HasGpu));

        [ObservableProperty] private Dictionary<string, string> _gPUs = new();
        [ObservableProperty] private double _averageUsage;
        [ObservableProperty] private string _state;
        [ObservableProperty] private string _uptime = "00:00:00";
        [ObservableProperty] private bool _isRunning;

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

        partial void OnCpuCountChanged(int value) => OnPropertyChanged(nameof(ConfigSummary));
        partial void OnMemoryGbChanged(double value) => OnPropertyChanged(nameof(ConfigSummary));

        private TimeSpan _anchorUptime;
        private DateTime _anchorLocalTime;
        private string _transientState;
        private string _backendState;

        public TimeSpan RawUptime => _anchorUptime;

        public VmInstanceInfo(Guid id, string name)
        {
            _id = id;
            _name = name;
            Disks.CollectionChanged += (s, e) =>
            {
                TotalDiskSizeGb = Disks.Sum(d => d.MaxSize) / 1073741824.0;
                OnPropertyChanged(nameof(ConfigSummary));
            };
        }

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
            if (!_isRunning) { Uptime = "00:00:00"; return; }
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