using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public partial class VirtualMachinesPageViewModel
    {
        // ===== 视图模型属性 - 存储管理 =====
        [ObservableProperty] private ObservableCollection<HostDiskInfo> _hostDisks = new();

        // 存储向导属性
        [ObservableProperty] private string _deviceType = "HardDisk";
        [ObservableProperty] private bool _isPhysicalSource = false;
        [ObservableProperty] private bool _autoAssign = true;
        [ObservableProperty] private string _filePath = string.Empty;
        [ObservableProperty] private bool _isNewDisk = false;
        [ObservableProperty] private string _newDiskSize = "128";

        // ISO与高级选项
        [ObservableProperty] private string _selectedVhdType = "Dynamic";
        [ObservableProperty] private string _parentPath = string.Empty;
        [ObservableProperty] private string _sectorFormat = "Default";
        [ObservableProperty] private string _blockSize = "Default";
        [ObservableProperty] private string _isoSourceFolderPath = string.Empty;
        [ObservableProperty] private string _isoVolumeLabel = "NewISO";
        [ObservableProperty] private string _isoOutputPath = string.Empty;

        // 选中的物理磁盘与控制器
        [ObservableProperty] private HostDiskInfo _selectedPhysicalDisk;
        [ObservableProperty] private string _selectedControllerType = "SCSI";
        [ObservableProperty] private int _selectedControllerNumber = 0;
        [ObservableProperty] private int _selectedLocation = 0;

        // 存储验证与提示
        [ObservableProperty] private string _slotWarningMessage = string.Empty;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(SlotWarningVisibility))] private bool _isSlotValid = true;
        public Visibility SlotWarningVisibility => IsSlotValid ? Visibility.Collapsed : Visibility.Visible;

        // 存储只读集合
        public ObservableCollection<string> AvailableControllerTypes { get; } = new();
        public ObservableCollection<int> AvailableControllerNumbers { get; } = new();
        public ObservableCollection<int> AvailableLocations { get; } = new();
        public List<int> NewDiskSizePresets { get; } = new() { 32, 64, 128, 256, 512, 1024 };


        // ===== 存储管理模块 - 列表与基础操作 =====

        // 导航至存储设置页面
        [RelayCommand]
        private async Task GoToStorageSettingsAsync()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.StorageSettings;

            if (SelectedVm.StorageItems.Count == 0)
            {
                IsLoadingSettings = true;
                try
                {
                    await VmStorageService.LoadVmStorageItemsAsync(SelectedVm.Model);
                    await LoadHostDisksAsync();
                }
                catch (Exception ex) { ShowSnackbar(Properties.Resources.Error_Storage_LoadFail, FriendlyError.CleanLines(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
                finally { IsLoadingSettings = false; }
            }
        }

        // 加载宿主机物理磁盘列表
        private async Task LoadHostDisksAsync()
        {
            try
            {
                // 1. 获取 ApiResponse<List<HostDiskInfo>>
                var response = await VmStorageService.GetHostDisksAsync();

                // 2. 判断是否成功且存在数据
                if (response.HasData)
                {
                    // 3. 将 response.Data 传递给 ObservableCollection
                    Application.Current.Dispatcher.Invoke(() => HostDisks = new ObservableCollection<HostDiskInfo>(response.Data!));
                }
            }
            catch { }
        }


        // 优化磁盘
        [RelayCommand]
        private async Task OptimizeStorageAsync(VmStorageItem item)
        {
            // 空值、正在运行、或已经在优化中的磁盘不处理
            if (item == null || SelectedVm == null || SelectedVm.IsRunning || item.IsOptimizing) return;

            // 进入优化状态
            item.IsOptimizing = true;

            try
            {
                // 1. 发起 WMI 压缩指令
                // 虽然 await 会等待，但由于 vmms.exe 承载了 Job，即便 UI 崩溃，任务依然在后台跑
                var result = await VmStorageService.CompactDiskAsync(item.PathOrDiskNumber);

                if (result.Success)
                {
                    // 2. 刷新磁盘物理大小 (FileSize)
                    // 调用现有的存储服务，确保 UI 上的 GB 数值得到更新
                    await VmStorageService.RefreshVirtualDiskSizesAsync(SelectedVm.Model);

                    ShowSnackbar(Properties.Resources.VmPage_OptimizeSuccess, Properties.Resources.VmPage_OptimizeSuccessDesc, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                }
                else
                {
                    ShowSnackbar(Properties.Resources.VmPage_OptimizeFail, result.Error, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.VmPage_SysExp, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                // 3. 释放优化状态
                item.IsOptimizing = false;
            }
        }

        // 移除存储设备
        [RelayCommand]
        private async Task RemoveStorageItemAsync(VmStorageItem item)
        {
            if (SelectedVm == null || item == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmStorageService.RemoveDriveAsync(SelectedVm.Name, item);
                if (result.Success)
                {
                    ShowSnackbar(Properties.Resources.Common_Success, result.Message == "Storage_Msg_Ejected" ? Properties.Resources.Msg_Storage_Ejected : Properties.Resources.Msg_Storage_Removed, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    await VmStorageService.LoadVmStorageItemsAsync(SelectedVm.Model);
                }
                else ShowSnackbar(Properties.Resources.Error_Storage_RemoveFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            catch (Exception ex) { ShowSnackbar(Properties.Resources.Common_Error, FriendlyError.CleanLines(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoadingSettings = false; }
        }

        // 判断是否可以编辑存储路径
        private bool CanEditStorage(VmStorageItem item)
        {
            return item != null && item.DiskType != "Physical";
        }

        // 修改存储路径（换盘/换ISO）
        [RelayCommand(CanExecute = nameof(CanEditStorage))]
        private async Task EditStoragePath(VmStorageItem driveItem)
        {
            if (SelectedVm == null || driveItem == null) return;

            if (driveItem.DiskType == "Physical")
            {
                ShowSnackbar(Properties.Resources.Common_Restricted, Properties.Resources.Error_Storage_PhysicalMod, ControlAppearance.Danger, SymbolRegular.Warning24);
                return;
            }

            if (driveItem.DriveType == "HardDisk" && SelectedVm.IsRunning && driveItem.ControllerType == "IDE")
            {
                ShowSnackbar(Properties.Resources.Common_Restricted, Properties.Resources.Error_Storage_VhdRunning, ControlAppearance.Danger, SymbolRegular.Warning24);
                return;
            }

            string filter = driveItem.DriveType == "DvdDrive"
                ? Properties.Resources.Filter_Iso
                : Properties.Resources.Filter_Vhd;

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = driveItem.DriveType == "DvdDrive" ? Properties.Resources.Title_SelectIso : Properties.Resources.Title_SelectVhd,
                Filter = filter
            };

            if (openFileDialog.ShowDialog() == true)
            {
                IsLoadingSettings = true;
                try
                {
                    (bool Success, string Message) result;

                    if (driveItem.DriveType == "DvdDrive")
                    {
                        result = await VmStorageService.ModifyDvdDrivePathAsync(
                            SelectedVm.Name,
                            driveItem.ControllerNumber,
                            driveItem.ControllerLocation,
                            openFileDialog.FileName);
                    }
                    else
                    {
                        result = await VmStorageService.ModifyHardDrivePathAsync(
                            SelectedVm.Name,
                            driveItem.ControllerType,
                            driveItem.ControllerNumber,
                            driveItem.ControllerLocation,
                            openFileDialog.FileName);
                    }

                    if (result.Success)
                    {
                        ShowSnackbar(Properties.Resources.Msg_Common_ModSuccess, Properties.Resources.Msg_Storage_PathUpdated, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                        await VmStorageService.LoadVmStorageItemsAsync(SelectedVm.Model);
                    }
                    else
                    {
                        ShowSnackbar(Properties.Resources.Error_Common_ModFailShort, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    }
                }
                catch (Exception ex)
                {
                    ShowSnackbar(Properties.Resources.Common_Error, FriendlyError.CleanLines(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
                finally
                {
                    IsLoadingSettings = false;
                }
            }
        }

        // 判断路径是否可打开文件夹
        private bool CanOpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (int.TryParse(path, out _)) return false;
            if (path.StartsWith("PhysicalDisk", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        // 在资源管理器中打开所在文件夹
        [RelayCommand(CanExecute = nameof(CanOpenFolder))]
        private void OpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (int.TryParse(path, out _) || path.StartsWith("PhysicalDisk", StringComparison.OrdinalIgnoreCase)) return;

                if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
                else
                {
                    string directory = System.IO.Path.GetDirectoryName(path);
                    if (System.IO.Directory.Exists(directory))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", directory);
                    }
                }
            }
            catch (Exception) { }
        }


        // ===== 存储管理模块 - 添加设备向导 =====

        public int NewDiskSizeInt => int.TryParse(NewDiskSize, out int size) && size > 0 ? size : 128;

        public string FilePathPlaceholder => DeviceType == "HardDisk"
            ? Properties.Resources.Placeholder_Vhd
            : Properties.Resources.Placeholder_Iso;

        public string BrowseButtonText => IsNewDisk ? Properties.Resources.Button_SaveTo : Properties.Resources.Button_Browse;

        // 属性变更监听 - 自动分配插槽
        partial void OnAutoAssignChanged(bool value)
        {
            if (value)
            {
                CalculateBestSlot();
            }
        }

        // 属性变更监听 - 磁盘大小
        partial void OnNewDiskSizeChanged(string value)
        {
            if (int.TryParse(value, out int size) && size <= 0)
            {
                NewDiskSize = "128";
            }
        }

        // 属性变更监听 - 是否新建磁盘
        partial void OnIsNewDiskChanged(bool value)
        {
            OnPropertyChanged(nameof(BrowseButtonText));
            FilePath = string.Empty;
        }

        // 属性变更监听 - 设备类型
        partial void OnDeviceTypeChanged(string value)
        {
            FilePath = string.Empty;
            IsoOutputPath = string.Empty;

            OnPropertyChanged(nameof(FilePathPlaceholder));
            OnPropertyChanged(nameof(BrowseButtonText));

            RefreshControllerOptions();

            if (AutoAssign) CalculateBestSlot();
            else UpdateAvailableLocations();
        }

        // 属性变更监听 - 控制器类型
        partial void OnSelectedControllerTypeChanged(string value)
        {
            if (_isInternalUpdating || value == null) return;

            RefreshAvailableNumbers(value);

            // 手动切换时也使用跳变技巧，确保 UI 同步
            SelectedControllerNumber = -2;
            SelectedControllerNumber = AvailableControllerNumbers.FirstOrDefault();

            UpdateAvailableLocations();
        }

        // 属性变更监听 - 控制器编号
        partial void OnSelectedControllerNumberChanged(int value)
        {
            // 如果是内部设定的跳变值 -2，或者是锁定状态，绝对不要去刷新位置列表，否则会造成闪烁或死循环
            if (value == -2 || _isInternalUpdating) return;

            UpdateAvailableLocations();
        }

        // 导航至添加存储向导
        [RelayCommand]
        private async Task GoToAddStorageAsync()
        {
            if (SelectedVm == null) return;

            IsLoadingSettings = true;
            try
            {
                await VmStorageService.LoadVmStorageItemsAsync(SelectedVm.Model);

                RefreshControllerOptions();

                if (AutoAssign) CalculateBestSlot();
                else UpdateAvailableLocations();

                CurrentViewType = VmDetailViewType.AddStorage;
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 确认添加存储设备
        [RelayCommand]
        private async Task ConfirmAddStorageAsync()
        {
            if (SelectedVm == null) return;

            // 检查插槽冲突
            bool collision = SelectedVm.StorageItems.Any(i =>
                i.ControllerType == SelectedControllerType &&
                i.ControllerNumber == SelectedControllerNumber &&
                i.ControllerLocation == SelectedLocation);

            if (collision)
            {
                ShowSnackbar(Properties.Resources.Error_Storage_Collision, Properties.Resources.Error_Storage_Occupied, ControlAppearance.Danger, SymbolRegular.Warning24);
                return;
            }

            // 根据设备类型和新建标志验证路径
            string target = IsPhysicalSource ? SelectedPhysicalDisk?.Number.ToString() : FilePath;

            if (string.IsNullOrEmpty(target) && !IsNewDisk)
            {
                ShowSnackbar(Properties.Resources.Error_Common_Args, Properties.Resources.Error_Storage_SelectTarget, ControlAppearance.Caution, SymbolRegular.Warning24);
                return;
            }

            // 验证 ISO 创建参数
            if (DeviceType == "DvdDrive" && IsNewDisk)
            {
                if (string.IsNullOrWhiteSpace(IsoSourceFolderPath))
                {
                    ShowSnackbar(Properties.Resources.Error_Common_Args, Properties.Resources.Error_Storage_IsoSource, ControlAppearance.Caution, SymbolRegular.Warning24);
                    return;
                }

                if (string.IsNullOrWhiteSpace(IsoOutputPath))
                {
                    ShowSnackbar(Properties.Resources.Error_Common_Args, Properties.Resources.Error_Storage_IsoPath, ControlAppearance.Caution, SymbolRegular.Warning24);
                    return;
                }

                target = IsoOutputPath;

                var outputDir = Path.GetDirectoryName(IsoOutputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    try
                    {
                        Directory.CreateDirectory(outputDir);
                    }
                    catch (Exception ex)
                    {
                        ShowSnackbar(Properties.Resources.Common_Error, string.Format(Properties.Resources.Error_Storage_DirFail, ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                        return;
                    }
                }

                if (!Directory.Exists(IsoSourceFolderPath))
                {
                    ShowSnackbar(Properties.Resources.Common_Error, Properties.Resources.Error_Storage_SourceNoExist, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    return;
                }
            }

            await AddDriveWrapperAsync(
                DeviceType,
                IsPhysicalSource,
                target,
                IsNewDisk,
                NewDiskSizeInt,
                SelectedVhdType,
                ParentPath,
                IsoSourceFolderPath,
                IsoVolumeLabel);

            CurrentViewType = VmDetailViewType.StorageSettings;
        }

        // 取消添加存储
        [RelayCommand]
        private void CancelAddStorage() => CurrentViewType = VmDetailViewType.StorageSettings;

        // 浏览文件
        [RelayCommand]
        private void BrowseFile()
        {
            if (IsNewDisk && DeviceType == "HardDisk")
            {
                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = Properties.Resources.Title_CreateVhd,
                    Filter = Properties.Resources.Filter_VhdExt,
                    DefaultExt = ".vhdx",
                    InitialDirectory = GetDir(FilePath),
                    FileName = GetFileName(FilePath, Properties.Resources.Default_VhdName)
                };
                if (saveDialog.ShowDialog() == true) FilePath = saveDialog.FileName;
            }
            else
            {
                var openDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = DeviceType == "HardDisk" ? Properties.Resources.Title_OpenVhd : Properties.Resources.Title_SelectIso,
                    Filter = DeviceType == "HardDisk" ? Properties.Resources.Filter_VhdOnly : Properties.Resources.Filter_IsoOnly,
                    InitialDirectory = GetDir(FilePath)
                };
                if (openDialog.ShowDialog() == true) FilePath = openDialog.FileName;
            }
        }

        // 浏览文件夹 (用于ISO制作)
        [RelayCommand]
        private void BrowseFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                InitialDirectory = string.IsNullOrWhiteSpace(IsoSourceFolderPath) ? string.Empty : IsoSourceFolderPath
            };
            if (dialog.ShowDialog() == true) IsoSourceFolderPath = dialog.FolderName;
        }

        // 浏览父级磁盘
        [RelayCommand]
        private void BrowseParentFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = Properties.Resources.Filter_VhdOnly,
                InitialDirectory = string.IsNullOrWhiteSpace(ParentPath) ? string.Empty : System.IO.Path.GetDirectoryName(ParentPath)
            };
            if (dialog.ShowDialog() == true) ParentPath = dialog.FileName;
        }

        // 浏览保存ISO路径
        [RelayCommand]
        private void BrowseSaveIso()
        {
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Properties.Resources.Title_SaveIso,
                Filter = Properties.Resources.Filter_IsoExt,
                DefaultExt = ".iso",
                InitialDirectory = string.IsNullOrWhiteSpace(IsoOutputPath) ? string.Empty : System.IO.Path.GetDirectoryName(IsoOutputPath),
                FileName = string.IsNullOrWhiteSpace(IsoOutputPath) ? $"{IsoVolumeLabel}.iso" : System.IO.Path.GetFileName(IsoOutputPath)
            };

            if (saveDialog.ShowDialog() == true)
            {
                IsoOutputPath = saveDialog.FileName;
            }
        }

        // 添加驱动器的包装函数
        public async Task AddDriveWrapperAsync(string driveType, bool isPhysical, string pathOrNumber, bool isNew, int sizeGb = 128, string vhdType = "Dynamic", string parentPath = "", string isoSourcePath = null, string isoVolumeLabel = null)
        {
            if (SelectedVm == null) return;
            IsLoadingSettings = true;
            try
            {
                // --- 核心修复：直接读取 UI 属性，不再调用后端 GetNextAvailableSlotAsync ---
                string targetType = SelectedControllerType;
                int targetNumber = SelectedControllerNumber;
                int targetLocation = SelectedLocation;


                if (isPhysical && int.TryParse(pathOrNumber, out int diskNum))
                    await VmStorageService.SetDiskOfflineStatusAsync(diskNum, true);

                var result = await VmStorageService.AddDriveAsync(
                    vmName: SelectedVm.Name,
                    controllerType: targetType,   // 传递界面显示的值
                    controllerNumber: targetNumber, // 传递界面显示的值
                    location: targetLocation,       // 传递界面显示的值
                    driveType: driveType,
                    pathOrNumber: pathOrNumber,
                    isPhysical: isPhysical,
                    isNew: isNew,
                    sizeGb: sizeGb,
                    vhdType: vhdType,
                    parentPath: parentPath,
                    sectorFormat: SectorFormat,
                    blockSize: BlockSize,
                    isoSourcePath: isoSourcePath,
                    isoVolumeLabel: isoVolumeLabel
                );

                if (result.Success)
                {
                    ShowSnackbar(Properties.Resources.Msg_Common_AddSuccess, string.Format(Properties.Resources.Msg_Storage_Connected, result.ActualType, result.ActualNumber, result.ActualLocation), ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    await VmStorageService.LoadVmStorageItemsAsync(SelectedVm.Model);
                }
                else
                {
                    ShowSnackbar(Properties.Resources.Error_Storage_AddFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Common_ExceptionLabel, FriendlyError.CleanLines(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }
        // 计算最佳可用插槽
        private void CalculateBestSlot()
        {
            if (SelectedVm == null) return;
            bool isRunning = SelectedVm.IsRunning;
            bool isGen1 = SelectedVm.Generation == 1;
            bool isDvd = DeviceType == "DvdDrive";

            IsSlotValid = true;
            SlotWarningMessage = string.Empty;

            if (isGen1 && isDvd)
            {
                if (isRunning)
                {
                    IsSlotValid = false;
                    SlotWarningMessage = Properties.Resources.Error_Storage_Gen1Dvd; // 修复点
                    return;
                }
                for (int c = 0; c < 2; c++)
                {
                    for (int l = 0; l < 2; l++)
                    {
                        if (!IsSlotOccupied("IDE", c, l)) { SetSlot("IDE", c, l); return; }
                    }
                }
                IsSlotValid = false;
                SlotWarningMessage = Properties.Resources.Error_Storage_Gen1IdeFull; // 修复点
                return;
            }

            if (isRunning || !isGen1)
            {
                for (int c = 0; c < 4; c++)
                {
                    for (int l = 0; l < 64; l++)
                    {
                        if (!IsSlotOccupied("SCSI", c, l)) { SetSlot("SCSI", c, l); return; }
                    }
                }
                IsSlotValid = false;
                SlotWarningMessage = isRunning ? Properties.Resources.Error_Storage_NoScsiRunning : Properties.Resources.Error_Storage_NoScsi; // 修复点
                return;
            }

            if (isGen1)
            {
                for (int c = 0; c < 2; c++)
                {
                    for (int l = 0; l < 2; l++)
                    {
                        if (!IsSlotOccupied("IDE", c, l)) { SetSlot("IDE", c, l); return; }
                    }
                }
            }

            for (int c = 0; c < 4; c++)
            {
                for (int l = 0; l < 64; l++)
                {
                    if (!IsSlotOccupied("SCSI", c, l)) { SetSlot("SCSI", c, l); return; }
                }
            }

            IsSlotValid = false;
            SlotWarningMessage = Properties.Resources.Error_Storage_NoSlots; // 修复点
        }
        // 检查插槽是否被占用
        private bool IsSlotOccupied(string type, int ctrlNum, int loc)
        {
            return SelectedVm.StorageItems.Any(i =>
                i.ControllerType == type &&
                i.ControllerNumber == ctrlNum &&
                i.ControllerLocation == loc);
        }

        // 设置当前选中的插槽
        private void SetSlot(string type, int ctrlNum, int loc)
        {

            _isInternalUpdating = true; // 锁定拦截器
            try
            {
                // 1. 设置接口类型并立即刷新列表数据源
                SelectedControllerType = type;
                RefreshAvailableNumbers(type);
                RefreshAvailableLocations(type, ctrlNum);

                // 2. 关键步骤：使用 Dispatcher 确保 UI 已处理完 ItemsSource 的变更通知
                // 使用 Loaded 优先级，这会等待 ComboBox 完成内部项的生成
                Application.Current.Dispatcher.BeginInvoke(new Action(() => {

                    // --- 强刷 [编号] ---
                    var targetNum = AvailableControllerNumbers.Contains(ctrlNum) ? ctrlNum : (AvailableControllerNumbers.Count > 0 ? AvailableControllerNumbers[0] : 0);

                    // 用 -2 强制触发 PropertyChanged，因为 -1 可能已经是当前 UI 的内部错误状态
                    SelectedControllerNumber = -2;
                    SelectedControllerNumber = targetNum;

                    // --- 强刷 [位置] ---
                    SelectedLocation = -2;
                    if (AvailableLocations.Contains(loc))
                    {
                        SelectedLocation = loc;
                    }
                    else if (AvailableLocations.Count > 0)
                    {
                        SelectedLocation = AvailableLocations[0];
                    }

                    IsSlotValid = true;
                    SlotWarningMessage = string.Empty;

                    // 全部完成后解锁
                    _isInternalUpdating = false;

                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception)
            {
                _isInternalUpdating = false;
            }
        }

        private void RefreshAvailableNumbers(string type)
        {
            AvailableControllerNumbers.Clear();
            int maxCtrl = (type == "IDE") ? 2 : 4;
            for (int i = 0; i < maxCtrl; i++)
                AvailableControllerNumbers.Add(i);
        }

        private void RefreshAvailableLocations(string type, int ctrlNum)
        {
            if (SelectedVm == null || type == null) return;

            var usedLocations = SelectedVm.StorageItems
                .Where(i => i.ControllerType == type && i.ControllerNumber == ctrlNum)
                .Select(i => i.ControllerLocation)
                .ToHashSet();

            int maxLoc = (type == "IDE") ? 2 : 64;
            AvailableLocations.Clear();
            for (int i = 0; i < maxLoc; i++)
            {
                if (!usedLocations.Contains(i)) AvailableLocations.Add(i);
            }
        }
        // 更新可用的位置列表
        private void UpdateAvailableLocations()
        {
            if (_isInternalUpdating) return;
            if (SelectedVm == null || string.IsNullOrEmpty(SelectedControllerType)) return;

            IsSlotValid = true;
            SlotWarningMessage = string.Empty;
            RefreshAvailableLocations(SelectedControllerType, SelectedControllerNumber);

            if (AvailableLocations.Count == 0)
            {
                SelectedLocation = -1;
                IsSlotValid = false;
                // 修复点：使用格式化资源
                SlotWarningMessage = string.Format(Properties.Resources.Error_Storage_CtrlFull, SelectedControllerType, SelectedControllerNumber);
                return;
            }

            // 如果当前位置不在新列表中，重置为第一个可用位置
            if (!AvailableLocations.Contains(SelectedLocation))
            {
                SelectedLocation = AvailableLocations[0];
            }
        }
        // 刷新控制器选项
        private void RefreshControllerOptions()
        {
            if (SelectedVm == null) return;

            bool isGen1 = SelectedVm.Generation == 1;
            bool isDvd = DeviceType == "DvdDrive";

            AvailableControllerTypes.Clear();

            // --- 核心物理约束逻辑 ---
            if (isGen1)
            {
                if (isDvd)
                {
                    // 法则 1：1 代机光驱必须在 IDE 上
                    AvailableControllerTypes.Add("IDE");
                }
                else
                {
                    // 1 代机硬盘
                    if (SelectedVm.IsRunning)
                    {
                        // 法则 2：运行中只能热插拔 SCSI
                        AvailableControllerTypes.Add("SCSI");
                    }
                    else
                    {
                        // 关机状态，IDE 和 SCSI 都可以
                        AvailableControllerTypes.Add("IDE");
                        AvailableControllerTypes.Add("SCSI");
                    }
                }
            }
            else
            {
                // 法则 3：2 代机永远只有 SCSI
                AvailableControllerTypes.Add("SCSI");
            }

            // 纠正当前选中项
            if (!AvailableControllerTypes.Contains(SelectedControllerType))
            {
                SelectedControllerType = AvailableControllerTypes.FirstOrDefault() ?? "SCSI";
            }
            else
            {
                // 强制刷新一次编号列表
                OnSelectedControllerTypeChanged(SelectedControllerType);
            }
        }

    }
}
