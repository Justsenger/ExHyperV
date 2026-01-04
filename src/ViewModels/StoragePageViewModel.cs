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
using System.Text.RegularExpressions;

namespace ExHyperV.ViewModels
{
    public partial class StoragePageViewModel : ObservableObject
    {
        public static StoragePageViewModel Instance { get; } = new StoragePageViewModel();
        private readonly IStorageService _storageService;
        public ObservableCollection<VMInfo> VmList { get; } = new();
        [ObservableProperty] private ObservableCollection<UiDriveModel> _allDrives = new();
        [ObservableProperty] private VMInfo? _selectedVm;
        [ObservableProperty] private bool _isLoading;

        public StoragePageViewModel()
        {
            _storageService = new StorageService();
            _ = InitializeVmListAsync();
        }

        async partial void OnSelectedVmChanged(VMInfo? value)
        {
            if (value != null) await RefreshVmStorageAsync();
            else AllDrives.Clear();
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
                        )).OrderByDescending(vm => vm.IsRunning).ThenBy(vm => vm.Name).ToList();
                    }
                });
                VmList.Clear();
                foreach (var vm in vms) VmList.Add(vm);
                if (SelectedVm == null) SelectedVm = VmList.FirstOrDefault();
            }
            catch (Exception ex) { ShowSnackbar(Translate("Storage_Title_Error"), ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoading = false; }
        }

        [RelayCommand]
        public async Task RefreshVmStorageAsync()
        {
            if (SelectedVm == null) return;
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
            catch (Exception ex) { ShowSnackbar(Translate("Storage_Title_Error"), ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
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
                    string titleKey = message == "Storage_Msg_Ejected" ? "Storage_Title_EjectSuccess" : "Storage_Title_RemoveSuccess";
                    ShowSnackbar(Translate(titleKey), Translate(message), ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                }
                else
                {
                    ShowSnackbar(Translate("Storage_Title_Error"), Translate(message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex) { ShowSnackbar(Translate("Storage_Title_Error"), ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoading = false; }
        }

        [RelayCommand]
        public async Task AddStorageAsync()
        {
            if (SelectedVm == null) return;
            await Task.Delay(50);
            try
            {
                var currentStorage = ConvertUiDrivesToInfo(AllDrives);
                var dialog = new ChooseDiskWindow(SelectedVm.Name, SelectedVm.Generation, SelectedVm.IsRunning, currentStorage);
                if (Application.Current.MainWindow != null) dialog.Owner = Application.Current.MainWindow;

                if (dialog.ShowDialog() == true)
                {
                    var resultVm = dialog.ViewModel;
                    IsLoading = true;

                    // 物理磁盘处理逻辑保持不变
                    if (resultVm.IsPhysicalSource && resultVm.SelectedPhysicalDisk != null)
                        await _storageService.SetDiskOfflineStatusAsync(resultVm.SelectedPhysicalDisk.Number, true);

                    string pathOrNumber = resultVm.IsPhysicalSource ? resultVm.SelectedPhysicalDisk?.Number.ToString() : resultVm.FilePath;

                    // ==================== 修改开始 ====================
                    // 核心修改：在方法末尾追加了 ISO 相关的三个参数
                    var (success, message, actualType, actualNumber, actualLocation) = await _storageService.AddDriveAsync(
                        SelectedVm.Name,
                        resultVm.SelectedControllerType,
                        resultVm.SelectedControllerNumber,
                        resultVm.SelectedLocation,
                        resultVm.DeviceType,
                        pathOrNumber,
                        resultVm.IsPhysicalSource,
                        resultVm.IsNewDisk,
                        resultVm.NewDiskSize,
                        resultVm.SelectedVhdType,
                        resultVm.ParentPath,
                        resultVm.SectorFormat,
                        resultVm.BlockSize,
                        // 新增参数：从 Dialog ViewModel 中获取 ISO 配置
                        resultVm.IsoSourceFolderPath,  // 源文件夹路径
                        resultVm.IsoVolumeLabel,       // 卷标
                        resultVm.IsoFileSystem         // 文件系统枚举 (Udf/Iso9660)
                    );
                    // ==================== 修改结束 ====================

                    if (success)
                    {
                        await RefreshVmStorageAsync();
                        string detail = $"{Translate("Storage_Msg_MountedTo")} {actualType} {actualNumber} : {actualLocation}";
                        ShowSnackbar(Translate("Storage_Title_AddSuccess"), detail, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    }
                    else
                    {
                        ShowSnackbar(Translate("Storage_Title_Error"), Translate(message), ControlAppearance.Danger, SymbolRegular.DismissCircle24);
                    }
                }
            }
            catch (Exception ex) { ShowSnackbar(Translate("Storage_Title_Error"), ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoading = false; }
        }
        private List<VmStorageControllerInfo> ConvertUiDrivesToInfo(IEnumerable<UiDriveModel> uiDrives)
        {
            return uiDrives.GroupBy(d => new { d.ControllerType, d.ControllerNumber }).Select(g => new VmStorageControllerInfo
            {
                ControllerType = g.Key.ControllerType,
                ControllerNumber = g.Key.ControllerNumber,
                AttachedDrives = g.Select(d => new AttachedDriveInfo { ControllerLocation = d.ControllerLocation, DriveType = d.DriveType, DiskType = d.DiskType, PathOrDiskNumber = d.PathOrDiskNumber, DiskNumber = d.DiskNumber }).ToList()
            }).ToList();
        }

        [RelayCommand]
        public async Task ModifyDriveAsync(UiDriveModel drive)
        {
            if (drive == null || SelectedVm == null) return;
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = drive.DriveType == "HardDisk" ? "vhdx|*.vhdx;*.vhd" : "iso|*.iso";
            if (dialog.ShowDialog() == true)
            {
                IsLoading = true;
                try
                {
                    var (addSuccess, addMsg, _, _, _) = await _storageService.AddDriveAsync(SelectedVm.Name, drive.ControllerType, drive.ControllerNumber, drive.ControllerLocation, drive.DriveType, dialog.FileName, false);
                    if (addSuccess)
                    {
                        await RefreshVmStorageAsync();
                        ShowSnackbar(Translate("Storage_Title_ModifySuccess"), Translate("Storage_Msg_MediaUpdated"), ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    }
                    else { ShowSnackbar(Translate("Storage_Title_Error"), Translate(addMsg), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
                }
                catch (Exception ex) { ShowSnackbar(Translate("Storage_Title_Error"), ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
                finally { IsLoading = false; }
            }
        }

        private string Translate(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            try
            {
                var translated = ExHyperV.Properties.Resources.ResourceManager.GetString(key);
                return string.IsNullOrEmpty(translated) ? key : translated;
            }
            catch { return key; }
        }

        public void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon, double seconds = 4)
        {
            Application.Current.Dispatcher.Invoke(() => {
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null)
                {
                    var presenter = mainWindow.FindName("SnackbarPresenter") as SnackbarPresenter;
                    if (presenter != null)
                    {
                        var snackbar = new Snackbar(presenter) { Title = title, Content = message, Appearance = appearance, Icon = new SymbolIcon(icon) { FontSize = 20 }, Timeout = TimeSpan.FromSeconds(seconds) };
                        snackbar.Show();
                    }
                }
            });
        }
    }
}