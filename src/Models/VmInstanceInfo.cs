using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;

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
        [ObservableProperty] private string _state;
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
        [ObservableProperty] private string _uptime;

        public IAsyncRelayCommand<string> ControlCommand { get; set; }

        private TimeSpan _rawUptime;
        public TimeSpan RawUptime
        {
            get => _rawUptime;
            set
            {
                if (SetProperty(ref _rawUptime, value))
                {
                    if (IsRunning)
                    {
                        BootTime = DateTime.Now - value;
                        UpdateUptimeDisplay();
                    }
                    else
                    {
                        Uptime = FormatUptime(value);
                    }
                }
            }
        }

        public DateTime BootTime { get; private set; }

        public ObservableCollection<VmCoreModel> Cores { get; } = new();
        [ObservableProperty] private int _columns = 2;
        [ObservableProperty] private int _rows = 1;
        [ObservableProperty] private VmProcessorSettings _processor;

        public string ConfigSummary => $"{CpuCount} Cores / {MemoryGb:N1}GB RAM / {DiskSize}";

        public bool IsRunning => State == "运行中" || State == "Running" || State == "正在启动" || State == "Starting" || State == "正在关闭" || State == "Stopping" || State == "正在重启" || State == "正在保存";

        public VmInstanceInfo(Guid id, string name)
        {
            _id = id; _name = name;
        }

        public VmInstanceInfo(Guid id, string name, string state, string osType, int cpu, double ram, string disk, TimeSpan uptime)
        {
            _id = id; _name = name; _state = state; _osType = osType;
            _cpuCount = cpu; _memoryGb = ram; _diskSize = disk;
            _rawUptime = uptime;

            if (IsRunning)
            {
                BootTime = DateTime.Now - uptime;
                UpdateUptimeDisplay();
            }
            else
            {
                _uptime = FormatUptime(uptime);
            }
        }

        public void UpdateUptimeDisplay()
        {
            if (IsRunning && BootTime != DateTime.MinValue)
            {
                var ts = DateTime.Now - BootTime;
                Uptime = FormatUptime(ts);
            }
            else
            {
                Uptime = "00:00:00";
            }
        }

        private string FormatUptime(TimeSpan ts)
        {
            if (ts <= TimeSpan.Zero || ts.TotalDays > 10000) return "00:00:00";
            if (ts.TotalDays >= 1)
            {
                return string.Format("{0}.{1:D2}:{2:D2}:{3:D2}", (int)ts.TotalDays, ts.Hours, ts.Minutes, ts.Seconds);
            }
            return string.Format("{0:D2}:{1:D2}:{2:D2}", ts.Hours, ts.Minutes, ts.Seconds);
        }

        partial void OnCpuCountChanged(int value) => OnPropertyChanged(nameof(ConfigSummary));
        partial void OnMemoryGbChanged(double value) => OnPropertyChanged(nameof(ConfigSummary));

        partial void OnStateChanged(string value)
        {
            OnPropertyChanged(nameof(IsRunning));
            if (!IsRunning)
            {
                RawUptime = TimeSpan.Zero;
                Uptime = "00:00:00";
                BootTime = DateTime.MinValue;
            }
        }
    }
}