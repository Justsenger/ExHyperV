using CommunityToolkit.Mvvm.ComponentModel;
using System; // 必须引用 System 以使用 TimeSpan
using System.Collections.Generic;

namespace ExHyperV.Models
{
    public partial class VMInfo : ObservableObject
    {
        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private string _lowMMIO;

        [ObservableProperty]
        private string _highMMIO;

        [ObservableProperty]
        private string _guestControlled;

        [ObservableProperty]
        private Dictionary<string, string> _gPUs;

        [ObservableProperty]
        private int _generation;

        [ObservableProperty]
        private bool _isRunning;

        [ObservableProperty]
        private string _notes;

        [ObservableProperty]
        private string _status;

        [ObservableProperty]
        private int _cpuCount;

        [ObservableProperty]
        private double _memoryGb;

        [ObservableProperty]
        private string _diskSize;

        // 修改点 1：属性类型改为 TimeSpan
        [ObservableProperty]
        private TimeSpan _uptime;

        // 修改点 2：构造函数使用可选参数
        // 注意 uptime 参数改为 TimeSpan? 类型，且默认值为 null
        public VMInfo(
            string name,
            string low,
            string high,
            string guest,
            Dictionary<string, string> gpus,
            int generation = 0,
            bool isRunning = false,
            string notes = "",
            string status = "",
            int cpu = 0,
            double ram = 0,
            string disk = "0G",
            TimeSpan? uptime = null) // 允许不传时间
        {
            _name = name;
            _lowMMIO = low;
            _highMMIO = high;
            _guestControlled = guest;
            _gPUs = gpus;
            _generation = generation;
            _isRunning = isRunning;
            _notes = notes;
            _status = status;
            _cpuCount = cpu;
            _memoryGb = ram;
            _diskSize = disk;

            // 如果没传 uptime (为 null)，则默认为 TimeSpan.Zero (00:00:00)
            _uptime = uptime ?? TimeSpan.Zero;
        }
    }
}