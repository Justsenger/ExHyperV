// /Views/Pages/GPUPage.xaml.cs

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.ViewModels; // 保持这个 using，为了 HostGpuViewModel 等
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace ExHyperV.Views.Pages
{
    // 将 ViewModel 的特性直接应用到 Page 类上
    [ObservableObject]
    public partial class GPUPage : Page
    {
        // === ViewModel 属性 ===
        private readonly IGpuPartitionService _gpuService;

        [ObservableProperty]
        private bool _isLoading;

        public ObservableCollection<HostGpuViewModel> HostGpus { get; } = new();
        public ObservableCollection<VirtualMachineViewModel> VirtualMachines { get; } = new();

        // === 构造函数 ===
        public GPUPage()
        {
            InitializeComponent();

            // 将页面的 DataContext 设置为它自身
            this.DataContext = this;

            _gpuService = new GpuPartitionService();
            _ = LoadDataCommand.ExecuteAsync(null); // 页面加载时执行命令
        }

        // === ViewModel 命令 ===
        [RelayCommand]
        private async Task LoadDataAsync()
        {
            if (IsLoading) return; // 保留保护卫士

            IsLoading = true;
            try
            {
                await CoreLoadDataAsync(); // 调用核心逻辑
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

                            // 调用核心加载逻辑来强制刷新列表
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
            if (parameters[0] is not AssignedGpuViewModel gpuToRemove ||
                parameters[1] is not VirtualMachineViewModel vm)
            {
                return;
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
                // 1. 在后台获取所有新数据，并转换为临时的 ViewModel 列表。
                // 在这个阶段，界面上的数据完全不受影响。
                var hostGpuModels = await _gpuService.GetHostGpusAsync();
                var vmModels = await _gpuService.GetVirtualMachinesAsync();

                // 创建临时的 HostGpuViewModel 列表
                var newHostGpus = new List<HostGpuViewModel>();
                foreach (var gpuModel in hostGpuModels)
                {
                    newHostGpus.Add(new HostGpuViewModel(gpuModel));
                }

                // 创建临时的 VirtualMachineViewModel 列表
                var newVirtualMachines = new List<VirtualMachineViewModel>();
                foreach (var vmModel in vmModels)
                {
                    // 注意：这里传入的是新的 newHostGpus 列表
                    newVirtualMachines.Add(new VirtualMachineViewModel(vmModel, newHostGpus));
                }

                // 2. 确保操作在 UI 线程上执行
                if (System.Windows.Application.Current.Dispatcher.CheckAccess())
                {
                    // 当前已经是 UI 线程
                    UpdateCollections(newHostGpus, newVirtualMachines);
                }
                else
                {
                    // 从后台线程切换到 UI 线程
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

        // 这是一个新的辅助方法，用于在 UI 线程上安全地更新集合
        private void UpdateCollections(List<HostGpuViewModel> newGpus, List<VirtualMachineViewModel> newVms)
        {
            // 3. 开始一次性地、原子化地更新界面
            HostGpus.Clear();
            foreach (var gpu in newGpus)
            {
                HostGpus.Add(gpu);
            }

            VirtualMachines.Clear();
            foreach (var vm in newVms)
            {
                VirtualMachines.Add(vm);
            }
        }
    }
}