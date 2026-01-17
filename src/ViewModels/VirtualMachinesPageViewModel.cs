using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Properties;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels
{
    // 定义视图类型的枚举
    public enum VmDetailViewType
    {
        Dashboard,  // 仪表盘 (默认)
        CpuSettings, // CPU 设置
        MemorySettings // 内存设置 (未来)
    }

    public partial class VirtualMachinesPageViewModel : ObservableObject
    {
        private readonly InstancesService _instancesService;
        private readonly DispatcherTimer _localTimer;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private ObservableCollection<VmInstanceInfo> _vmList = new();

        [ObservableProperty]
        private VmInstanceInfo _selectedVm;

        // 控制右侧显示哪个视图
        [ObservableProperty]
        private VmDetailViewType _currentViewType = VmDetailViewType.Dashboard;

        public VirtualMachinesPageViewModel(InstancesService instancesService)
        {
            _instancesService = instancesService;

            _localTimer = new DispatcherTimer();
            _localTimer.Interval = TimeSpan.FromSeconds(1);
            _localTimer.Tick += LocalTimer_Tick;
            _localTimer.Start();

            _ = LoadVmsAsync();
        }

        // 切换到 CPU 设置页命令
        [RelayCommand]
        private void GoToCpuSettings()
        {
            CurrentViewType = VmDetailViewType.CpuSettings;
        }

        // 返回仪表盘命令
        [RelayCommand]
        private void GoBackToDashboard()
        {
            CurrentViewType = VmDetailViewType.Dashboard;
        }

        // 当切换左侧虚拟机时，强制重置回仪表盘
        partial void OnSelectedVmChanged(VmInstanceInfo value)
        {
            CurrentViewType = VmDetailViewType.Dashboard;
        }

        partial void OnSearchTextChanged(string value)
        {
            var view = CollectionViewSource.GetDefaultView(VmList);
            if (view != null)
            {
                view.Filter = item =>
                {
                    if (item is VmInstanceInfo vm)
                    {
                        return string.IsNullOrEmpty(value) || vm.Name.Contains(value, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                };
                view.Refresh();
            }
        }

        private void LocalTimer_Tick(object sender, EventArgs e)
        {
            if (VmList == null) return;
            foreach (var vm in VmList)
            {
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

            var sortedVms = vms.OrderBy(v => v.Status == "已关机" ? 1 : 0)
                               .ThenBy(v => v.Name);

            foreach (var vm in sortedVms)
            {
                string osType = "windows";
                if (!string.IsNullOrEmpty(vm.Notes))
                {
                    string notes = vm.Notes.ToLower();
                    if (notes.Contains("[ostype:linux]")) osType = "linux";
                    else if (notes.Contains("[ostype:android]")) osType = "android";
                    else if (notes.Contains("[ostype:macos]")) osType = "macos";
                    else if (notes.Contains("[ostype:freebsd]")) osType = "freebsd";
                    else if (notes.Contains("[ostype:openbsd]")) osType = "openbsd";
                    else if (notes.Contains("[ostype:openwrt]")) osType = "openwrt";
                    else if (notes.Contains("[ostype:fnos]")) osType = "fnos";
                    else if (notes.Contains("[ostype:chromeos]")) osType = "chromeos";
                    else if (notes.Contains("[ostype:fydeos]")) osType = "fydeos";
                }

                var instance = new VmInstanceInfo(
                    vm.Name,
                    vm.Status,
                    osType,
                    vm.CpuCount,
                    vm.MemoryGb,
                    vm.DiskSize,
                    vm.Uptime
                );

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
            SelectedVm = VmList.FirstOrDefault();

            if (!string.IsNullOrEmpty(SearchText))
            {
                OnSearchTextChanged(SearchText);
            }

            _ = StatusPollingLoop();
        }

        private async Task StatusPollingLoop()
        {
            while (true)
            {
                if (VmList.Count > 0)
                {
                    var checkList = VmList.ToList();
                    foreach (var vm in checkList)
                    {
                        var info = await _instancesService.GetVmDynamicInfoAsync(vm.Name);

                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            vm.State = info.State;
                            vm.RawUptime = info.Uptime;
                        });
                    }
                }
                await Task.Delay(3000);
            }
        }

        [RelayCommand]
        private async Task ControlAsync(string action)
        {
            if (SelectedVm == null) return;

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
                _ => true
            };

            if (!confirmed) return;

            await _instancesService.ExecuteControlActionAsync(SelectedVm.Name, action);

            // 操作后立即强制刷新一次状态，确保 UI 按钮状态及时更新
            var info = await _instancesService.GetVmDynamicInfoAsync(SelectedVm.Name);
            SelectedVm.State = info.State;
            SelectedVm.RawUptime = info.Uptime;
        }
    }

    public partial class VmInstanceInfo : ObservableObject
    {
        [ObservableProperty] private string _name;
        [ObservableProperty] private string _state;
        [ObservableProperty] private string _osType;
        [ObservableProperty] private string _configSummary;

        private TimeSpan _rawUptime;
        public TimeSpan RawUptime
        {
            get => _rawUptime;
            set
            {
                if (SetProperty(ref _rawUptime, value))
                {
                    OnPropertyChanged(nameof(Uptime));
                }
            }
        }

        public string Uptime => string.Format("{0:D2}:{1:D2}:{2:D2}:{3:D2}",
            _rawUptime.Days, _rawUptime.Hours, _rawUptime.Minutes, _rawUptime.Seconds);

        public bool IsRunning => State == "运行中" || State == "正在启动" || State == "正在关闭";

        public VmInstanceInfo(string name, string state, string osType, int cpu, double ram, string disk, TimeSpan uptime)
        {
            _name = name;
            _state = state;
            _osType = osType;
            _rawUptime = uptime;
            _configSummary = $"{cpu} Cores / {ram:F1}GB RAM / {disk}";
        }

        public void AddOneSecond()
        {
            RawUptime = RawUptime.Add(TimeSpan.FromSeconds(1));
        }

        partial void OnStateChanged(string value)
        {
            OnPropertyChanged(nameof(IsRunning));
            if (value == "已关机")
            {
                RawUptime = TimeSpan.Zero;
            }
        }
    }
}