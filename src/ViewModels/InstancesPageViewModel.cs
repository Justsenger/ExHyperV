using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading; // 必须引用：用于定时器
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Properties;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels
{
    public partial class InstancesPageViewModel : ObservableObject
    {
        private readonly InstancesService _instancesService;
        private readonly DispatcherTimer _localTimer;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private ObservableCollection<VmInstanceInfo> _vmList = new();

        [ObservableProperty]
        private VmInstanceInfo _selectedVm;

        public InstancesPageViewModel(InstancesService instancesService)
        {
            _instancesService = instancesService;

            // 初始化本地 1秒 定时器 (用于 UI 秒表跳动)
            _localTimer = new DispatcherTimer();
            _localTimer.Interval = TimeSpan.FromSeconds(1);
            _localTimer.Tick += LocalTimer_Tick;
            _localTimer.Start();

            _ = LoadVmsAsync();
        }

        // 每秒触发：纯内存操作，界面极其流畅
        private void LocalTimer_Tick(object sender, EventArgs e)
        {
            if (VmList == null) return;
            foreach (var vm in VmList)
            {
                // 只有处于“运行中”状态才自增时间
                if (vm.IsRunning)
                {
                    vm.AddOneSecond();
                }
            }
        }

        [RelayCommand]
        private async Task LoadVmsAsync()
        {
            IsLoading = true;
            VmList.Clear();
            var vms = await _instancesService.GetVmListAsync();

            // 排序：正在运行/启动中等非关机状态排在前面，然后按名称排序
            var sortedVms = vms.OrderBy(v => v.Status == "已关机" ? 1 : 0)
                               .ThenBy(v => v.Name);

            foreach (var vm in sortedVms)
            {
                // 解析 Note 中的 OS 类型标记
                string osType = "windows"; // 默认为 windows
                if (!string.IsNullOrEmpty(vm.Notes))
                {
                    string notes = vm.Notes.ToLower();
                    if (notes.Contains("[ostype:linux]")) osType = "linux";
                    else if (notes.Contains("[ostype:android]")) osType = "android";
                    else if (notes.Contains("[ostype:macos]")) osType = "macos";
                    else if (notes.Contains("[ostype:freebsd]")) osType = "freebsd"; // 新增
                    else if (notes.Contains("[ostype:openbsd]")) osType = "openbsd"; // 新增
                    else if (notes.Contains("[ostype:openwrt]")) osType = "openwrt";
                    else if (notes.Contains("[ostype:fnos]")) osType = "fnos";
                    else if (notes.Contains("[ostype:chromeos]")) osType = "chromeos";
                    else if (notes.Contains("[ostype:fydeos]")) osType = "fydeos";
                }

                // 传递原始 TimeSpan
                var instance = new VmInstanceInfo(
                    vm.Name,
                    vm.Status,
                    osType,
                    vm.CpuCount,
                    vm.MemoryGb,
                    vm.DiskSize,
                    vm.Uptime
                );

                // 监听系统类型改变并自动写入 Hyper-V Notes
                instance.PropertyChanged += async (s, e) =>
                {
                    if (e.PropertyName == nameof(VmInstanceInfo.OsType))
                    {
                        await _instancesService.UpdateOsTypeNoteAsync(instance.Name, instance.OsType);
                    }
                };

                VmList.Add(instance);
            }
            IsLoading = false;

            // 启动后台轮询
            _ = StatusPollingLoop();
        }

        // 后台校准循环：每3秒一次，修正时间误差，获取真实状态
        private async Task StatusPollingLoop()
        {
            while (true)
            {
                if (VmList.Count > 0)
                {
                    // 创建副本列表以防集合修改
                    var checkList = VmList.ToList();
                    foreach (var vm in checkList)
                    {
                        var info = await _instancesService.GetVmDynamicInfoAsync(vm.Name);

                        // 更新 UI 线程上的属性
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            vm.State = info.State; // 会自动更新 IsRunning
                            // 覆盖时间（校准）
                            vm.RawUptime = info.Uptime;
                        });
                    }
                }
                // 每3秒进行一次后台校准
                await Task.Delay(3000);
            }
        }

        [RelayCommand]
        private async Task ControlAsync(string action)
        {
            if (SelectedVm == null) return;

            // 对危险操作进行二次确认
            bool confirmed = action switch
            {
                "Restart" => await DialogManager.ShowConfirmAsync(
                    Resources.Confirm_Restart_Title,
                    string.Format(Resources.Confirm_Restart_Message, SelectedVm.Name),
                    isDanger: false),
                "Stop" => await DialogManager.ShowConfirmAsync(
                    Resources.Confirm_Shutdown_Title,
                    string.Format(Resources.Confirm_Shutdown_Message, SelectedVm.Name),
                    isDanger: false),
                "TurnOff" => await DialogManager.ShowConfirmAsync(
                    Resources.Confirm_TurnOff_Title,
                    string.Format(Resources.Confirm_TurnOff_Message, SelectedVm.Name),
                    isDanger: true),
                _ => true // 启动、暂停、保存等操作无需确认
            };

            if (!confirmed) return;

            await _instancesService.ExecuteControlActionAsync(SelectedVm.Name, action);

            // 操作后立即校准一次状态，提升交互响应感
            var info = await _instancesService.GetVmDynamicInfoAsync(SelectedVm.Name);
            SelectedVm.State = info.State;
            SelectedVm.RawUptime = info.Uptime;
        }
    }

    // ==========================================
    // UI 绑定的包装类
    // ==========================================
    public partial class VmInstanceInfo : ObservableObject
    {
        [ObservableProperty] private string _name;
        [ObservableProperty] private string _state; // 如 "运行中", "已关机"
        [ObservableProperty] private string _osType;

        // 配置摘要 (用于 UI 显示: "16 Cores / 4.0GB RAM / 64G")
        [ObservableProperty] private string _configSummary;

        // 原始时间数据
        private TimeSpan _rawUptime;
        public TimeSpan RawUptime
        {
            get => _rawUptime;
            set
            {
                if (SetProperty(ref _rawUptime, value))
                {
                    // 当原始时间改变时，通知 Uptime 字符串更新
                    OnPropertyChanged(nameof(Uptime));
                }
            }
        }

        // 给 UI 绑定的格式化时间字符串
        public string Uptime => string.Format("{0:D2}:{1:D2}:{2:D2}:{3:D2}",
            _rawUptime.Days, _rawUptime.Hours, _rawUptime.Minutes, _rawUptime.Seconds);

        // 判断是否应该计时
        public bool IsRunning => State == "运行中" || State == "正在启动" || State == "正在关闭";

        public VmInstanceInfo(string name, string state, string osType, int cpu, double ram, string disk, TimeSpan uptime)
        {
            _name = name;
            _state = state;
            _osType = osType;
            _rawUptime = uptime;
            _configSummary = $"{cpu} Cores / {ram:F1}GB RAM / {disk}";
        }

        // 手动增加一秒 (由定时器调用)
        public void AddOneSecond()
        {
            RawUptime = RawUptime.Add(TimeSpan.FromSeconds(1));
        }

        // 当状态改变时，更新 IsRunning 属性
        partial void OnStateChanged(string value)
        {
            OnPropertyChanged(nameof(IsRunning));
            // 如果关机了，重置时间为 0
            if (value == "已关机")
            {
                RawUptime = TimeSpan.Zero;
            }
        }
    }
}