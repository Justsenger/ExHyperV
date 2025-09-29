// 文件路径: src/ViewModels/CpuPageViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ExHyperV.ViewModels
{
    public partial class CpuPageViewModel : ObservableObject
    {
        private readonly IMonitoringService _monitoringService;
        private readonly DispatcherTimer _timer;

        [ObservableProperty]
        private bool _isLoading = true;

        public ObservableCollection<object> PerformanceItems { get; } = new();

        [ObservableProperty]
        private object? _selectedItem;

        // 右侧的详细视图将直接绑定到 SelectedItem，不再需要这个属性
        // public object? SelectedDetailViewModel => SelectedItem;

        private readonly HostCpuViewModel _hostCpuViewModel = new();

        public CpuPageViewModel()
        {
            _monitoringService = new MonitoringService();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (s, e) => await UpdateDataAsync();
        }

        public async Task InitializeAsync()
        {
            try
            {
                // 先把宿主机加进去，这样即使更新失败，列表里也有东西
                PerformanceItems.Add(_hostCpuViewModel);
                await UpdateDataAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"!!!!!! ViewModel 初始化失败: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
                // ▼▼▼ 【核心修复】确保在加载完成后设置默认选中项 ▼▼▼
                if (SelectedItem == null && PerformanceItems.Any())
                {
                    SelectedItem = PerformanceItems.First();
                }
                // ▲▲▲ 【核心修复】结束 ▲▲▲
                _timer.Start();
            }
        }

        private async Task UpdateDataAsync()
        {
            var hostData = await _monitoringService.GetHostCpuUsageAsync();
            var vmDataList = await _monitoringService.GetVmCpuUsagesAsync();

            _hostCpuViewModel.UpdateData(hostData);

            var vmsToRemove = PerformanceItems.OfType<VmCpuViewModel>()
                .Where(vm => !vmDataList.Any(d => d.VmName == vm.VmName)).ToList();
            foreach (var vm in vmsToRemove)
            {
                PerformanceItems.Remove(vm);
            }

            foreach (var vmData in vmDataList)
            {
                var existingVm = PerformanceItems.OfType<VmCpuViewModel>()
                    .FirstOrDefault(vm => vm.VmName == vmData.VmName);
                if (existingVm != null)
                {
                    existingVm.UpdateData(vmData);
                }
                else
                {
                    PerformanceItems.Add(new VmCpuViewModel(vmData));
                }
            }
        }

        public void Cleanup()
        {
            _timer.Stop();
        }
    }
}