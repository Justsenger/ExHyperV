using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>SMT（同时多线程）模式：继承宿主 / 单线程 / 多线程。</summary>
    public enum SmtMode { Inherit, SingleThread, MultiThread }

    /// <summary>
    /// VM 处理器设置（绑定 CPU Settings 页面）。
    /// 含 Clone/Restore 用于"取消编辑"还原。
    /// </summary>
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
        [ObservableProperty] private string? _cpuBrandString;

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
            _cpuBrandString = other.CpuBrandString;
        }
    }
}
