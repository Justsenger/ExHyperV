using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    public partial class VmInstanceInfo : ObservableObject
    {
        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private string _state;

        [ObservableProperty]
        private string _osType;

        [ObservableProperty]
        private int _cpuCount;

        [ObservableProperty]
        private double _memoryGb;

        [ObservableProperty]
        private string _diskSize;

        [ObservableProperty]
        private string _uptime;

        public string ConfigSummary => $"{CpuCount} Cores / {MemoryGb:N1}GB RAM / {DiskSize}";

        public VmInstanceInfo(string name, string state, string osType, int cpu, double ram, string disk, string uptime = "00:00:00")
        {
            _name = name;
            _state = state;
            _osType = osType;
            _cpuCount = cpu;
            _memoryGb = ram;
            _diskSize = disk;
            _uptime = uptime;
        }
    }
}