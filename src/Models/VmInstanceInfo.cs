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
        [ObservableProperty] private int _columns = 2;
        [ObservableProperty] private int _rows = 1;

        public ObservableCollection<VmCoreModel> Cores { get; } = new();
        public IAsyncRelayCommand<string> ControlCommand { get; set; }
        public string ConfigSummary => $"{CpuCount} Cores / {MemoryGb:N1}GB RAM / {DiskSize}";

        private TimeSpan _anchorUptime;
        private DateTime _anchorLocalTime;

        // --- 移除了 _transientLife 计数器 ---
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
            SyncBackendData(state, uptime);
        }

        public void SetTransientState(string optimisticText)
        {
            _transientState = optimisticText;
            // 不再设置倒计时，一直等到后端状态改变为止
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
                // 只有当后端状态真的达到了预期，或者变成了明确的终止态，才清除
                // 否则就一直保持 TransientState (例如保持 "正在关闭")
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
            if (!IsRunning)
            {
                Uptime = "00:00:00";
                return;
            }

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
            // 1. 启动类
            if (_transientState == "正在启动" || _transientState == "正在重启")
            {
                if (backend == "运行中" || backend == "Running") return true;
                // 如果是重启，变成了关机，可能还没起得来，先不清除，等它变成运行
                // 但如果重启变关机时间太久卡住... 
                // 实际上 Restart-VM 命令完成后通常已经起来了。
                // 如果是 WMI 捕获到了中间的“已关机”态，保持“正在重启”是合理的。
                return false;
            }

            // 2. 停止/保存类
            if (_transientState == "正在关闭" || _transientState == "正在保存")
            {
                if (backend == "已关机" || backend == "Off" ||
                    backend == "已保存" || backend == "Saved" ||
                    backend == "已暂停" || backend == "Paused")
                    return true;
                return false;
            }

            // 3. 暂停类
            if (_transientState == "正在暂停")
            {
                if (backend == "已暂停" || backend == "Paused" ||
                    backend == "已保存" || backend == "Saved")
                    return true;
                return false;
            }

            return false;
        }
    }
}