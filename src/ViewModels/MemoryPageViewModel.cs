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

namespace ExHyperV.ViewModels
{
    public partial class MemoryPageViewModel : ObservableObject
    {
        private readonly IMemoryService _memoryService;
        [ObservableProperty]
        private bool _isLoading;

        public ObservableCollection<MemoryBankGroupViewModel> MemoryBankGroups { get; } = new();
        public ObservableCollection<VirtualMachineMemoryViewModel> VirtualMachinesMemory { get; } = new();

        public MemoryPageViewModel()
        {
            _memoryService = new MemoryService();
            // 页面加载时自动获取一次数据
            _ = LoadAllDataCommand.ExecuteAsync(null);
        }

        // 新命令，只刷新虚拟机内存
        [RelayCommand]
        private async Task RefreshVmDataAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            try
            {
                var vmMemoryModels = await _memoryService.GetVirtualMachinesMemoryAsync();
                var newVirtualMachinesMemory = vmMemoryModels.Select(vm => new VirtualMachineMemoryViewModel(vm)).ToList();

                if (Application.Current.Dispatcher.CheckAccess())
                {
                    VirtualMachinesMemory.Clear();
                    foreach (var vm in newVirtualMachinesMemory) VirtualMachinesMemory.Add(vm);
                }
                else
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        VirtualMachinesMemory.Clear();
                        foreach (var vm in newVirtualMachinesMemory) VirtualMachinesMemory.Add(vm);
                    });
                }
            }
            catch (Exception ex)
            {
                Utils.Show($"刷新虚拟机内存失败: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        // 初始加载命令
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
                var newVirtualMachinesMemory = vmMemoryModels.Select(vm => new VirtualMachineMemoryViewModel(vm)).ToList();

                if (Application.Current.Dispatcher.CheckAccess())
                {
                    UpdateCollections(newMemoryBankGroups, newVirtualMachinesMemory);
                }
                else
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => UpdateCollections(newMemoryBankGroups, newVirtualMachinesMemory));
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