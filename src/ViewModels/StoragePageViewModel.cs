using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Views;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;
using System.Diagnostics;

namespace ExHyperV.ViewModels
{
    public partial class StoragePageViewModel : ObservableObject
    {
        public static StoragePageViewModel Instance { get; } = new StoragePageViewModel();

        private readonly IStorageService _storageService;

        public ObservableCollection<VMInfo> VmList { get; } = new();

        [ObservableProperty]
        private ObservableCollection<UiDriveModel> _allDrives = new();

        [ObservableProperty]
        private VMInfo? _selectedVm;

        [ObservableProperty]
        private bool _isLoading;

        public StoragePageViewModel()
        {
            _storageService = new StorageService();
            _ = InitializeVmListAsync();
        }

        async partial void OnSelectedVmChanged(VMInfo? value)
        {
            if (value != null)
            {
                await RefreshVmStorageAsync();
            }
            else
            {
                AllDrives.Clear();
            }
        }

        private async Task InitializeVmListAsync()
        {
            IsLoading = true;
            try
            {
                var vms = await Task.Run(() =>
                {
                    using (var ps = System.Management.Automation.PowerShell.Create())
                    {
                        ps.AddScript("Get-VM | Select-Object Name, State, Generation");
                        var results = ps.Invoke();
                        return results.Select(r => new VMInfo(
                            r.Properties["Name"].Value?.ToString() ?? "",
                            "", "", "", new Dictionary<string, string>(),
                            r.Properties["Generation"].Value != null ? Convert.ToInt32(r.Properties["Generation"].Value) : 0,
                            r.Properties["State"].Value?.ToString() == "Running"
                        ))
                        .OrderByDescending(vm => vm.IsRunning)
                        .ThenBy(vm => vm.Name)
                        .ToList();
                    }
                });

                VmList.Clear();
                foreach (var vm in vms) VmList.Add(vm);

                if (SelectedVm == null) SelectedVm = VmList.FirstOrDefault();
            }
            catch (Exception ex)
            {
                ShowSnackbar("初始化失败", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task RefreshVmStorageAsync()
        {
            if (SelectedVm == null) return;
            IsLoading = true;
            try
            {
                var storageInfo = await _storageService.GetVmStorageInfoAsync(SelectedVm.Name);
                Application.Current.Dispatcher.Invoke(() => {
                    AllDrives.Clear();
                    foreach (var ctrl in storageInfo)
                    {
                        if (ctrl.AttachedDrives == null) continue;
                        foreach (var drive in ctrl.AttachedDrives)
                        {
                            AllDrives.Add(new UiDriveModel
                            {
                                DriveType = drive.DriveType,
                                DiskType = drive.DiskType,
                                PathOrDiskNumber = drive.PathOrDiskNumber?.ToString() ?? "",
                                ControllerLocation = drive.ControllerLocation,
                                ControllerType = ctrl.ControllerType,
                                ControllerNumber = ctrl.ControllerNumber,
                                DiskNumber = drive.DiskNumber,
                                DiskModel = drive.DiskModel,
                                DiskSizeGB = drive.DiskSizeGB,
                                SerialNumber = drive.SerialNumber
                            });
                        }
                    }
                });
            }
            catch (Exception ex) { ShowSnackbar("查询失败", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoading = false; }
        }

        [RelayCommand]
        public async Task RemoveDriveAsync(UiDriveModel drive)
        {
            if (drive == null || SelectedVm == null) return;

            IsLoading = true;
            try
            {
                var (success, message) = await _storageService.RemoveDriveAsync(SelectedVm.Name, drive);

                if (success)
                {
                    await RefreshVmStorageAsync();
                    ShowSnackbar("移除成功", "存储设备已安全断开", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                }
                else
                {
                    ShowSnackbar("操作失败", message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar("移除异常", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task AddStorageAsync()
        {
            if (SelectedVm == null) return;

            IsLoading = true;
            try
            {
                var currentStorage = await _storageService.GetVmStorageInfoAsync(SelectedVm.Name);
                // 核心修复：传入 SelectedVm.IsRunning
                var dialog = new ChooseDiskWindow(SelectedVm.Name, SelectedVm.Generation, SelectedVm.IsRunning, currentStorage);

                if (Application.Current.MainWindow != null) dialog.Owner = Application.Current.MainWindow;

                IsLoading = false;
                if (dialog.ShowDialog() == true)
                {
                    var resultVm = dialog.ViewModel;
                    IsLoading = true;

                    if (resultVm.IsPhysicalSource && resultVm.SelectedPhysicalDisk != null)
                    {
                        await _storageService.SetDiskOfflineStatusAsync(resultVm.SelectedPhysicalDisk.Number, true);
                    }

                    string pathOrNumber = resultVm.IsPhysicalSource ? resultVm.SelectedPhysicalDisk?.Number.ToString() : resultVm.FilePath;

                    var (success, message, actualType, actualNumber, actualLocation) = await _storageService.AddDriveAsync(
                        SelectedVm.Name, resultVm.SelectedControllerType, resultVm.SelectedControllerNumber, resultVm.SelectedLocation,
                        resultVm.DeviceType, pathOrNumber, resultVm.IsPhysicalSource, resultVm.IsNewDisk, resultVm.NewDiskSize,
                        resultVm.SelectedVhdType, resultVm.ParentPath, resultVm.SectorFormat, resultVm.BlockSize);

                    if (success)
                    {
                        await RefreshVmStorageAsync();
                        ShowSnackbar("添加成功", $"设备已挂载至 {actualType} 控制器 {actualNumber} 插槽 {actualLocation}", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    }
                    else
                    {
                        ShowSnackbar("添加失败", message, ControlAppearance.Danger, SymbolRegular.DismissCircle24);
                    }
                }
            }
            catch (Exception ex) { ShowSnackbar("操作异常", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoading = false; }
        }

        [RelayCommand]
        public async Task ModifyDriveAsync(UiDriveModel drive)
        {
            if (drive == null || SelectedVm == null) return;

            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = drive.DriveType == "HardDisk" ? "虚拟硬盘|*.vhdx;*.vhd" : "光盘镜像|*.iso";

            if (dialog.ShowDialog() == true)
            {
                IsLoading = true;
                try
                {
                    // === 彻底修复逻辑：不要先调 RemoveDriveAsync！ ===
                    // 直接调用 AddDriveAsync。
                    // 内部 PowerShell 脚本会自动判断：
                    // 1. 如果该位置已有光驱/硬盘硬件，执行 Set 指令（换碟/热挂载），不会报错。
                    // 2. 如果该位置为空，执行 Add 指令（物理添加设备）。
                    // 3. 这种原子化操作解决了开机状态下禁止删除光驱的问题，也解决了关机状态下的 WMI 延迟冲突。

                    var (addSuccess, addMsg, _, _, _) = await _storageService.AddDriveAsync(
                        SelectedVm.Name,
                        drive.ControllerType,
                        drive.ControllerNumber,
                        drive.ControllerLocation,
                        drive.DriveType,
                        dialog.FileName,
                        false // isPhysical
                    );

                    if (addSuccess)
                    {
                        await RefreshVmStorageAsync();
                        ShowSnackbar("修改成功", "驱动器媒介已热更新", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    }
                    else
                    {
                        ShowSnackbar("操作失败", addMsg, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    }
                }
                catch (Exception ex)
                {
                    ShowSnackbar("异常", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
                finally
                {
                    IsLoading = false;
                }
            }
        }
        public void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon, double seconds = 4)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null)
                {
                    var presenter = mainWindow.FindName("SnackbarPresenter") as SnackbarPresenter;
                    if (presenter != null)
                    {
                        var snackbar = new Snackbar(presenter)
                        {
                            Title = title,
                            Content = message,
                            Appearance = appearance,
                            Icon = new SymbolIcon(icon) { FontSize = 20 },
                            Timeout = TimeSpan.FromSeconds(seconds)
                        };
                        snackbar.Show();
                    }
                }
            });
        }
    }
}