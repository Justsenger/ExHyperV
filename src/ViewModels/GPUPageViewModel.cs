using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
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
        private readonly SshService _sshService;

        [ObservableProperty]
        private bool _isLoading;

        public ObservableCollection<HostGpuViewModel> HostGpus { get; } = new();
        public ObservableCollection<VirtualMachineViewModel> VirtualMachines { get; } = new();

        public GPUPageViewModel()
        {
            _gpuService = new GpuPartitionService();
            _sshService = new SshService();
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
            var chooseGpuWindow = new ChooseGPUWindow(vm.Name, gpuInfoList);
            if (chooseGpuWindow.ShowDialog() != true) return;

            var selectedGpu = chooseGpuWindow.SelectedGpu;
            bool isSimpleMode = chooseGpuWindow.IsSimpleMode;

            PartitionInfo userSelectedPartition = null;

            if (!isSimpleMode)
            {
                string vmState = await _gpuService.GetVmStateAsync(vm.Name);
                if (vmState != "Off")
                {
                    ShutdownChoice choice = ShutdownChoice.Cancel;
                    await Application.Current.Dispatcher.InvokeAsync(() => {
                        var dialog = new ShutdownConfirmationDialog(vm.Name);
                        dialog.ShowDialog();
                        choice = dialog.UserChoice;
                    });

                    if (choice == ShutdownChoice.ShutdownAndContinue)
                    {
                        IsLoading = true;
                        await _gpuService.ShutdownVmAsync(vm.Name);
                        IsLoading = false;
                    }
                    else return;
                }

                IsLoading = true;
                try
                {
                    var partitions = await _gpuService.GetPartitionsFromVmAsync(vm.Name);
                    var selectablePartitions = partitions
                        .Where(p => p.OsType == OperatingSystemType.Windows || p.OsType == OperatingSystemType.Linux)
                        .ToList();

                    if (!selectablePartitions.Any())
                    {
                        Utils.Show(ExHyperV.Properties.Resources.Error_NoRecognizedPartitionFound);
                        return;
                    }

                    await Application.Current.Dispatcher.InvokeAsync(async () => {
                        userSelectedPartition = await ShowPartitionSelectionDialog(selectablePartitions);
                    });

                    if (userSelectedPartition == null) return;
                }
                catch (Exception ex)
                {
                    Utils.Show(string.Format(Properties.Resources.Error_FatalError, ex.Message));
                    return;
                }
                finally { IsLoading = false; }
            }

            IsLoading = true;
            try
            {
                string result = await _gpuService.AddGpuPartitionAsync(vm.Name, selectedGpu.Path, selectedGpu.Manu, userSelectedPartition, selectedGpu.Id);

                if (result == "OK")
                {
                    string GPUname = selectedGpu.GPUname;
                    string VMname = vm.Name;
                    var ms = Application.Current.MainWindow as MainWindow;
                    if (ms != null)
                    {
                        var snackbarService = new SnackbarService();
                        snackbarService.SetSnackbarPresenter(ms.SnackbarPresenter);
                        snackbarService.Show(
                            Properties.Resources.success,
                            GPUname + Properties.Resources.already + VMname,
                            ControlAppearance.Success,
                            new FontIcon
                            {
                                Glyph = "\uF16C",
                                FontSize = 32,
                                FontFamily = Application.Current.FindResource("SegoeFluentIcons") as FontFamily,
                            },
                            System.TimeSpan.FromSeconds(2)
                        );
                    }
                }
                else
                {
                    Utils.Show(string.Format(Properties.Resources.GpuPartition_Error_MountGpuFailed, result));
                }
            }
            catch (System.Exception ex)
            {
                Utils.Show(string.Format(Properties.Resources.Error_FatalError, ex.Message));
            }
            finally
            {
                await CoreLoadDataAsync();
                IsLoading = false;
            }
        }
        [RelayCommand]
        private async Task RemoveGpuAsync(object[] parameters)
        {
            if (parameters == null || parameters.Length < 2) return;
            if (parameters[0] is not VirtualMachineViewModel vm ||
                parameters[1] is not AssignedGpuViewModel gpuToRemove)
            {
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
                    Utils.Show(Properties.Resources.Error_UnmountGpuFailed);
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
                var isHyperVInstalled = await _gpuService.IsHyperVModuleAvailableAsync();
                var hostGpuModels = await _gpuService.GetHostGpusAsync();
                var vmModels = await _gpuService.GetVirtualMachinesAsync();
                var newHostGpus = new List<HostGpuViewModel>();
                foreach (var gpuModel in hostGpuModels)
                {
                    newHostGpus.Add(new HostGpuViewModel(gpuModel, isHyperVInstalled));
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
                Utils.Show(string.Format(Properties.Resources.Error_LoadDataFailed, ex.Message));
            }
        }

        private void UpdateCollections(List<HostGpuViewModel> newGpus, List<VirtualMachineViewModel> newVms)
        {
            HostGpus.Clear();
            foreach (var gpu in newGpus) { HostGpus.Add(gpu); }
            VirtualMachines.Clear();
            foreach (var vm in newVms) { VirtualMachines.Add(vm); }
        }

        private async Task<PartitionInfo> ShowPartitionSelectionDialog(List<PartitionInfo> partitions)
        {
            var dialog = new ChoosePartitionWindow(partitions);
            bool? result = dialog.ShowDialog();
            if (result == true)
            {
                return dialog.SelectedPartition;
            }
            return null;
        }

    }
}