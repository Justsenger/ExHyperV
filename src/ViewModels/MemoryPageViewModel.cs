using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ExHyperV.ViewModels
{
    public partial class MemoryPageViewModel : ObservableObject
    {
        private readonly IMemoryService _memoryService;
        [ObservableProperty]
        private bool _isLoading;

        public ObservableCollection<MemoryBankGroupViewModel> MemoryBankGroups { get; } = new();
        public ObservableCollection<VirtualMachineMemoryViewModel> VirtualMachinesMemory { get; } = new();

        private readonly DispatcherTimer _liveDataTimer;

        public MemoryPageViewModel()
        {
            _memoryService = new MemoryService();

            _liveDataTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _liveDataTimer.Tick += OnLiveDataTimerTick;

            // 页面加载时只调用一次完整加载
            _ = LoadAllDataCommand.ExecuteAsync(null);
        }

        private async void OnLiveDataTimerTick(object sender, EventArgs e)
        {
            if (IsLoading) return;
            try
            {
                var liveDataList = await _memoryService.GetVirtualMachinesMemoryAsync();

                // --- 关键修正：完整的列表同步逻辑 ---

                // 1. 找出需要删除的VM (存在于旧列表，但不存在于新列表)
                var vmsToRemove = VirtualMachinesMemory
                    .Where(vm => !liveDataList.Any(ld => ld.VMName == vm.VMName))
                    .ToList();

                foreach (var vm in vmsToRemove)
                {
                    VirtualMachinesMemory.Remove(vm);
                }

                // 2. 找出需要更新和添加的VM
                foreach (var liveData in liveDataList)
                {
                    var targetVm = VirtualMachinesMemory.FirstOrDefault(vm => vm.VMName == liveData.VMName);
                    if (targetVm != null)
                    {
                        // 如果已存在，则更新其实时数据
                        targetVm.UpdateLiveData(liveData);
                    }
                    else
                    {
                        // 如果不存在，则是新添加的VM
                        VirtualMachinesMemory.Add(new VirtualMachineMemoryViewModel(liveData));
                    }
                }
            }
            catch { /* 在后台刷新时静默地忽略错误 */ }
        }
        public void Cleanup()
        {
            _liveDataTimer.Stop();
            _liveDataTimer.Tick -= OnLiveDataTimerTick;
        }

        // 这个命令现在是唯一的刷新按钮的入口
        [RelayCommand]
        private async Task RefreshVmDataAsync()
        {
            await LoadAllDataAsync();
        }

        // 初始和手动刷新都调用这个核心加载方法
        [RelayCommand]
        private async Task LoadAllDataAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            try
            {
                var hostMemoryTask = _memoryService.GetHostMemoryAsync();
                var vmMemoryTask = _memoryService.GetVirtualMachinesMemoryAsync();
                await Task.WhenAll(hostMemoryTask, vmMemoryTask);

                var hostMemoryModels = await hostMemoryTask;
                var vmMemoryModels = await vmMemoryTask;

                var allHostModules = hostMemoryModels.Select(m => new HostMemoryViewModel(m)).ToList();
                var newMemoryBankGroups = allHostModules.GroupBy(m => m.BankLabel).Select(g => new MemoryBankGroupViewModel(g.Key, g.ToList())).OrderBy(g => g.BankLabel).ToList();

                // 在创建新的ViewModel列表之前，先停止旧列表的定时器（如果适用）
                // 在我们的设计中，定时器是全局的，所以不需要这一步

                var newVirtualMachinesMemory = vmMemoryModels.Select(vm => new VirtualMachineMemoryViewModel(vm)).ToList();

                if (Application.Current.Dispatcher.CheckAccess())
                {
                    UpdateCollections(newMemoryBankGroups, newVirtualMachinesMemory);
                }
                else
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => UpdateCollections(newMemoryBankGroups, newVirtualMachinesMemory));
                }

                // 确保定时器只启动一次
                if (!_liveDataTimer.IsEnabled)
                {
                    _liveDataTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Utils.Show($"数据加载失败: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void UpdateCollections(List<MemoryBankGroupViewModel> newGroups, List<VirtualMachineMemoryViewModel> newVmsMemory)
        {
            MemoryBankGroups.Clear();
            foreach (var group in newGroups) MemoryBankGroups.Add(group);

            VirtualMachinesMemory.Clear();
            foreach (var vm in newVmsMemory) VirtualMachinesMemory.Add(vm);
        }
    }
}