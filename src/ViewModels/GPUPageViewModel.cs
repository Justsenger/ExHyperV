using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Views;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public partial class GPUPageViewModel : ObservableObject
    {
        private readonly IGpuPartitionService _gpuService;

        [ObservableProperty]
        private bool _isLoading;

        public ObservableCollection<HostGpuViewModel> HostGpus { get; } = new();
        public ObservableCollection<VirtualMachineViewModel> VirtualMachines { get; } = new();

        public GPUPageViewModel()
        {
            _gpuService = new GpuPartitionService();
            _ = LoadDataCommand.ExecuteAsync(null);
        }

        [RelayCommand]
        private async Task LoadDataAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            try
            {
                await CoreLoadDataAsync();
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private async Task AddGpuAsync(VirtualMachineViewModel vm)
        {
            if (vm == null) return;
            var gpuInfoList = this.HostGpus.Select(h => h.Model).ToList();
            var chooseWindow = new ChooseGPUWindow(vm.Name, gpuInfoList);
            if (chooseWindow.ShowDialog() == true)
            {
                var selectedGpu = chooseWindow.SelectedGpu;
                if (selectedGpu != null)
                {
                    IsLoading = true;
                    try
                    {
                        string result = await _gpuService.AddGpuPartitionAsync(vm.Name, selectedGpu.Path, selectedGpu.Manu);
                        if (result == "OK")
                        {
                            string GPUname = selectedGpu.GPUname;
                            string VMname = vm.Name;
                            var ms = System.Windows.Application.Current.MainWindow as MainWindow;
                            if (ms != null)
                            {
                                var snackbarService = new SnackbarService();
                                snackbarService.SetSnackbarPresenter(ms.SnackbarPresenter);
                                snackbarService.Show(
                                    ExHyperV.Properties.Resources.success,
                                    GPUname + ExHyperV.Properties.Resources.already + VMname,
                                    ControlAppearance.Success,
                                    new SymbolIcon(SymbolRegular.CheckboxChecked24, 32),
                                    TimeSpan.FromSeconds(2)
                                );
                            }
                            await CoreLoadDataAsync();
                        }
                        else
                        {
                            Utils.Show($"挂载 GPU 失败: {result}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Utils.Show($"发生严重错误: {ex.Message}");
                    }
                    finally
                    {
                        IsLoading = false;
                    }
                }
            }
        }

        [RelayCommand]
        private async Task RemoveGpuAsync(object[] parameters)
        {
            if (parameters == null || parameters.Length < 2) return;
            if (parameters[0] is not VirtualMachineViewModel vm ||
                parameters[1] is not AssignedGpuViewModel gpuToRemove)
            {
                // 注意：这里我们根据 ElementName 绑定的顺序调整回来
                if (parameters[0] is AssignedGpuViewModel gpu && parameters[1] is VirtualMachineViewModel v)
                {
                    gpuToRemove = gpu;
                    vm = v;
                }
                else
                {
                    return;
                }
            }

            IsLoading = true;
            try
            {
                bool success = await _gpuService.RemoveGpuPartitionAsync(vm.Name, gpuToRemove.AdapterId);
                if (success)
                {
                    vm.AssignedGpus.Remove(gpuToRemove);
                }
                else
                {
                    Utils.Show("卸载 GPU 失败。");
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task CoreLoadDataAsync()
        {
            try
            {
                var hostGpuModels = await _gpuService.GetHostGpusAsync();
                var vmModels = await _gpuService.GetVirtualMachinesAsync();
                var newHostGpus = new List<HostGpuViewModel>();
                foreach (var gpuModel in hostGpuModels)
                {
                    newHostGpus.Add(new HostGpuViewModel(gpuModel));
                }
                var newVirtualMachines = new List<VirtualMachineViewModel>();
                foreach (var vmModel in vmModels)
                {
                    newVirtualMachines.Add(new VirtualMachineViewModel(vmModel, newHostGpus));
                }
                if (System.Windows.Application.Current.Dispatcher.CheckAccess())
                {
                    UpdateCollections(newHostGpus, newVirtualMachines);
                }
                else
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        UpdateCollections(newHostGpus, newVirtualMachines);
                    });
                }
            }
            catch (Exception ex)
            {
                Utils.Show($"加载数据时出错: {ex.Message}");
            }
        }

        private void UpdateCollections(List<HostGpuViewModel> newGpus, List<VirtualMachineViewModel> newVms)
        {
            HostGpus.Clear();
            foreach (var gpu in newGpus) { HostGpus.Add(gpu); }
            VirtualMachines.Clear();
            foreach (var vm in newVms) { VirtualMachines.Add(vm); }
        }
    }
}