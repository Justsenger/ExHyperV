using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
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

        // 物理光驱直通(仅第1代)
        [ObservableProperty] private ObservableCollection<HostOpticalInfo> _hostOpticals = new();
        [ObservableProperty] private HostOpticalInfo _selectedPhysicalOptical;
        [ObservableProperty] private string _selectedControllerType = "SCSI";
        [ObservableProperty] private int _selectedControllerNumber = 0;
        [ObservableProperty] private int _selectedLocation = 0;

        // 存储验证与提示
        [ObservableProperty] private string _slotWarningMessage = string.Empty;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(SlotWarningVisibility))] private bool _isSlotValid = true;
        public Visibility SlotWarningVisibility => IsSlotValid ? Visibility.Collapsed : Visibility.Visible;

        // 物理来源时:类型=硬盘→物理磁盘列表、类型=光盘→物理光驱列表;物理光驱仅第1代(第2代隐藏"光盘"类型项)
        public Visibility PhysicalDiskListVisibility => (IsPhysicalSource && DeviceType == "HardDisk") ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PhysicalOpticalListVisibility => (IsPhysicalSource && DeviceType == "DvdDrive") ? Visibility.Visible : Visibility.Collapsed;
        public Visibility OpticalTypeOptionVisibility => (!IsPhysicalSource || SelectedVm?.Generation == 1) ? Visibility.Visible : Visibility.Collapsed;

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

            // 每次进入都重拉，同步外部(Hyper-V 管理器 / PowerShell 等)对该 VM 存储的改动——不再只在首次(Count==0)加载、留陈旧缓存
            IsLoadingSettings = true;
            try
            {
                await VmStorageService.LoadVmStorageItemsAsync(SelectedVm.Model);
                await LoadHostDisksAsync();
            }
            catch (Exception ex) { ShowError($"{Properties.Resources.Error_Storage_LoadFail}：{FriendlyError.CleanLines(ex.Message)}"); }
            finally { IsLoadingSettings = false; }
        }

        // 加载宿主机物理磁盘列表
        private async Task LoadHostDisksAsync()
        {
            try
            {
                // 1. 获取 ApiResponse<List<HostDiskInfo>>
                var response = await HostDiskService.GetHostDisksAsync();

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

                    ShowSuccess(Properties.Resources.VmPage_OptimizeSuccessDesc);
                }
                else
                {
                    ShowError($"{Properties.Resources.VmPage_OptimizeFail}：{result.Error}");
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
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
                    ShowSuccess(result.Message);
                    await VmStorageService.LoadVmStorageItemsAsync(SelectedVm.Model);
                }
                else ShowError($"{Properties.Resources.Error_Storage_RemoveFail}：{result.Message}");
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
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
                ShowTip(Properties.Resources.Error_Storage_PhysicalMod);
                return;
            }

            if (driveItem.DriveType == "HardDisk" && SelectedVm.IsRunning && driveItem.ControllerType == "IDE")
            {
                ShowTip(Properties.Resources.Error_Storage_VhdRunning);
                return;
            }

            string filter = driveItem.DriveType == "DvdDrive"
                ? Properties.Resources.Filter_Iso
                : Properties.Resources.Filter_Vhd;
            string title = driveItem.DriveType == "DvdDrive" ? Properties.Resources.Title_SelectIso : Properties.Resources.Title_SelectVhd;

            var picked = Dialogs.PickOpenFile(title, filter);
            if (picked == null) return;

            IsLoadingSettings = true;
            try
            {
                (bool Success, string Message) result;

                if (driveItem.DriveType == "DvdDrive")
                {
                    result = await VmStorageService.ModifyDvdDrivePathAsync(
                        SelectedVm.Name,
                        driveItem.ControllerType,
                        driveItem.ControllerNumber,
                        driveItem.ControllerLocation,
                        picked);
                }
                else
                {
                    result = await VmStorageService.ModifyHardDrivePathAsync(
                        SelectedVm.Name,
                        driveItem.ControllerType,
                        driveItem.ControllerNumber,
                        driveItem.ControllerLocation,
                        picked);
                }

                if (result.Success)
                {
                    ShowSuccess(Properties.Resources.Msg_Storage_PathUpdated);
                    await VmStorageService.LoadVmStorageItemsAsync(SelectedVm.Model);
                }
                else
                {
                    ShowError($"{Properties.Resources.Error_Common_ModFailShort}：{result.Message}");
                }
            }
            catch (Exception ex)
            {
                ShowError(FriendlyError.CleanLines(ex.Message));
            }
            finally
            {
                IsLoadingSettings = false;
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

        // 在资源管理器中定位（文件高亮 / 目录打开，统一走 Shell 门面）
        [RelayCommand(CanExecute = nameof(CanOpenFolder))]
        private void OpenFolder(string path) => Shell.Reveal(path);


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
            OnPropertyChanged(nameof(PhysicalDiskListVisibility));
            OnPropertyChanged(nameof(PhysicalOpticalListVisibility));

            RefreshControllerOptions();

            if (AutoAssign) CalculateBestSlot();
            else UpdateAvailableLocations();

            if (IsPhysicalSource && value == "DvdDrive") _ = LoadHostOpticalsAsync();
        }

        // 属性变更监听 - 来源(虚拟文件/物理设备)
        partial void OnIsPhysicalSourceChanged(bool value)
        {
            if (value)
            {
                IsNewDisk = false;   // 物理来源不"新建"(新建开关也隐藏)，免得残留 true 影响后续校验
                // 第 2 代物理来源不支持物理光驱：把类型拉回硬盘(光盘类型项也会隐藏)
                if (SelectedVm?.Generation != 1 && DeviceType == "DvdDrive")
                    DeviceType = "HardDisk";
            }

            OnPropertyChanged(nameof(PhysicalDiskListVisibility));
            OnPropertyChanged(nameof(PhysicalOpticalListVisibility));
            OnPropertyChanged(nameof(OpticalTypeOptionVisibility));

            if (value && DeviceType == "DvdDrive") _ = LoadHostOpticalsAsync();
        }

        // 加载宿主物理光驱列表(物理来源 + 光盘,仅第1代)
        private async Task LoadHostOpticalsAsync()
        {
            try
            {
                var response = await HostDiskService.GetHostOpticalDrivesAsync();
                if (response.HasData)
                    Application.Current.Dispatcher.Invoke(
                        () => HostOpticals = new ObservableCollection<HostOpticalInfo>(response.Data!));
            }
            catch { }
        }

        // 属性变更监听 - 控制器类型
        partial void OnSelectedControllerTypeChanged(string value)
        {
            if (IsApplySuppressed || value == null) return;

            RefreshAvailableNumbers(value);

            // 切控制器类型会让"编号"和"位置"两个 ComboBox 的 ItemsSource 同时重建；若在此同步设值，
            // 会被容器的异步重建冲掉——表现为 1 代关自动分配、硬盘切光驱时位置丢空。
            // 延迟到 Loaded 优先级、等容器生成后再用跳变设值（与 SetSlot 同款保护）。
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                SelectedControllerNumber = -2;
                SelectedControllerNumber = AvailableControllerNumbers.FirstOrDefault();
                UpdateAvailableLocations();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // 属性变更监听 - 控制器编号
        partial void OnSelectedControllerNumberChanged(int value)
        {
            // 如果是内部设定的跳变值 -2，或者是锁定状态，绝对不要去刷新位置列表，否则会造成闪烁或死循环
            if (value == -2 || IsApplySuppressed) return;

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
                await LoadHostDisksAsync();   // 每次进添加界面都重拉宿主物理盘列表，否则刚加过(已脱机)的盘还留在列表里被重复添加

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
                ShowTip(Properties.Resources.Error_Storage_Occupied);
                return;
            }

            // 落地标识：物理光驱=宿主光驱 PNPDeviceID、物理硬盘=磁盘号、虚拟=文件路径
            bool isPhysicalOptical = IsPhysicalSource && DeviceType == "DvdDrive";
            string target = isPhysicalOptical
                ? SelectedPhysicalOptical?.PnpDeviceId
                : (IsPhysicalSource ? SelectedPhysicalDisk?.Number.ToString() : FilePath);

            // 只有"新建 ISO"用 IsoOutputPath 落地、FilePath 可空(下方单独校验)；其余(新建硬盘、挂载现有、物理盘)target 都是落地路径或盘号，必须非空。
            bool isNewIso = DeviceType == "DvdDrive" && IsNewDisk;
            if (string.IsNullOrEmpty(target) && !isNewIso)
            {
                ShowTip(Properties.Resources.Error_Storage_SelectTarget);
                return;
            }

            // 虚拟文件的路径预检查：挂载现有(.vhdx/.iso)文件须在、新建硬盘目标须不存在——否则底层只甩 0x80070002(找不到)/0x80070050(已存在) 原始码
            if (!IsPhysicalSource)
            {
                if (!IsNewDisk && !File.Exists(FilePath))
                {
                    ShowTip(Properties.Resources.Error_Storage_FileNotExist);
                    return;
                }
                if (IsNewDisk && DeviceType == "HardDisk" && File.Exists(FilePath))
                {
                    ShowTip(Properties.Resources.Error_Storage_FileExists);
                    return;
                }
            }

            // 新建差异磁盘必须有存在的父磁盘——否则要到 CreateVhd(type=4) 才抛底层原始码
            if (IsNewDisk && DeviceType == "HardDisk" && SelectedVhdType == "Differencing"
                && (string.IsNullOrWhiteSpace(ParentPath) || !File.Exists(ParentPath)))
            {
                ShowTip(Properties.Resources.Error_Storage_ParentRequired);
                return;
            }

            // 验证 ISO 创建参数
            if (DeviceType == "DvdDrive" && IsNewDisk)
            {
                if (string.IsNullOrWhiteSpace(IsoSourceFolderPath))
                {
                    ShowTip(Properties.Resources.Error_Storage_IsoSource);
                    return;
                }

                if (string.IsNullOrWhiteSpace(IsoOutputPath))
                {
                    ShowTip(Properties.Resources.Error_Storage_IsoPath);
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
                        ShowError(string.Format(Properties.Resources.Error_Storage_DirFail, ex.Message));
                        return;
                    }
                }

                if (!Directory.Exists(IsoSourceFolderPath))
                {
                    ShowTip(Properties.Resources.Error_Storage_SourceNoExist);
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
            string? picked;
            if (IsNewDisk && DeviceType == "HardDisk")
            {
                picked = Dialogs.PickSaveFile(Properties.Resources.Title_CreateVhd, Properties.Resources.Filter_VhdExt,
                    ".vhdx", GetDir(FilePath), GetFileName(FilePath, Properties.Resources.Default_VhdName));
            }
            else
            {
                picked = Dialogs.PickOpenFile(
                    DeviceType == "HardDisk" ? Properties.Resources.Title_OpenVhd : Properties.Resources.Title_SelectIso,
                    DeviceType == "HardDisk" ? Properties.Resources.Filter_VhdOnly : Properties.Resources.Filter_IsoOnly,
                    GetDir(FilePath));
            }
            if (picked != null) FilePath = picked;
        }

        // 浏览文件夹 (用于ISO制作)
        [RelayCommand]
        private void BrowseFolder()
        {
            var picked = Dialogs.PickFolder(initialDir: string.IsNullOrWhiteSpace(IsoSourceFolderPath) ? null : IsoSourceFolderPath);
            if (picked != null) IsoSourceFolderPath = picked;
        }

        // 浏览父级磁盘
        [RelayCommand]
        private void BrowseParentFile()
        {
            var picked = Dialogs.PickOpenFile(null, Properties.Resources.Filter_VhdOnly,
                string.IsNullOrWhiteSpace(ParentPath) ? null : System.IO.Path.GetDirectoryName(ParentPath));
            if (picked != null) ParentPath = picked;
        }

        // 浏览保存ISO路径
        [RelayCommand]
        private void BrowseSaveIso()
        {
            var picked = Dialogs.PickSaveFile(Properties.Resources.Title_SaveIso, Properties.Resources.Filter_IsoExt, ".iso",
                string.IsNullOrWhiteSpace(IsoOutputPath) ? null : System.IO.Path.GetDirectoryName(IsoOutputPath),
                string.IsNullOrWhiteSpace(IsoOutputPath) ? $"{IsoVolumeLabel}.iso" : System.IO.Path.GetFileName(IsoOutputPath));
            if (picked != null) IsoOutputPath = picked;
        }

        // 添加驱动器的包装函数
        public async Task AddDriveWrapperAsync(string driveType, bool isPhysical, string pathOrNumber, bool isNew, int sizeGb = 128, string vhdType = "Dynamic", string parentPath = "", string isoSourcePath = null, string isoVolumeLabel = null)
        {
            if (SelectedVm == null) return;
            IsLoadingSettings = true;
            try
            {
                // 直接读 UI 属性，不调后端 GetNextAvailableSlotAsync
                string targetType = SelectedControllerType;
                int targetNumber = SelectedControllerNumber;
                int targetLocation = SelectedLocation;


                int offlinedDisk = -1;   // 记录为添加而脱机的物理盘号，添加失败时还原上线
                if (isPhysical && int.TryParse(pathOrNumber, out int diskNum))
                {
                    var offlineResult = await HostDiskService.SetDiskOfflineStatusAsync(diskNum, true);
                    if (!offlineResult.Success)
                    {
                        // 脱机失败(盘正被宿主占用/有分区挂载)必须中止：否则后面在 Msvm_DiskDrive 查不到该盘、会误报"物理盘找不到"，掩盖真因。
                        ShowError($"{Properties.Resources.Error_Storage_OfflineFail}：{FriendlyError.LastSentence(offlineResult.Error)}");
                        return;
                    }
                    offlinedDisk = diskNum;
                }

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
                    ShowSuccess(string.Format(Properties.Resources.Msg_Storage_Connected, result.ActualType, result.ActualNumber, result.ActualLocation));
                    await VmStorageService.LoadVmStorageItemsAsync(SelectedVm.Model);
                }
                else
                {
                    if (offlinedDisk >= 0)   // 添加失败：把刚为此脱机的物理盘还原上线 + 清只读，别让主机的盘卡在脱机/只读
                    {
                        await HostDiskService.SetDiskOfflineStatusAsync(offlinedDisk, false);
                        await HostDiskService.SetDiskReadOnlyAsync(offlinedDisk, false);
                    }
                    ShowError($"{Properties.Resources.Error_Storage_AddFail}：{result.Message}");
                }
            }
            catch (Exception ex)
            {
                ShowError(FriendlyError.CleanLines(ex.Message));
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
                    SlotWarningMessage = Properties.Resources.Error_Storage_Gen1Dvd;
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
                SlotWarningMessage = Properties.Resources.Error_Storage_Gen1IdeFull;
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
                SlotWarningMessage = isRunning ? Properties.Resources.Error_Storage_NoScsiRunning : Properties.Resources.Error_Storage_NoScsi;
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
            SlotWarningMessage = Properties.Resources.Error_Storage_NoSlots;
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
            // 抑制跨 Dispatcher 回调：手动持有抑制域，待 Loaded 回调全部刷完再 Dispose（异常路径也 Dispose）。
            var suppression = SuppressApply();
            try
            {
                // 1. 设置接口类型并立即刷新列表数据源
                SelectedControllerType = type;
                RefreshAvailableNumbers(type);
                RefreshAvailableLocations(type, ctrlNum);

                // 2. 用 Dispatcher 等 UI 处理完 ItemsSource 变更通知再设值
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
                    suppression.Dispose();

                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception)
            {
                suppression.Dispose();
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
            if (IsApplySuppressed) return;
            if (SelectedVm == null || string.IsNullOrEmpty(SelectedControllerType)) return;

            IsSlotValid = true;
            SlotWarningMessage = string.Empty;
            RefreshAvailableLocations(SelectedControllerType, SelectedControllerNumber);

            if (AvailableLocations.Count == 0)
            {
                SelectedLocation = -1;
                IsSlotValid = false;
                SlotWarningMessage = string.Format(Properties.Resources.Error_Storage_CtrlFull, SelectedControllerType, SelectedControllerNumber);
                return;
            }

            // 跳变 -2 再设目标，强制位置 ComboBox 刷新 SelectedItem——ItemsSource 刚 Clear+重填，
            // 目标若与当前值相同则直设不触发更新、ComboBox 会停在空选中态（位置显示丢空的另一半原因）。
            int target = AvailableLocations.Contains(SelectedLocation) ? SelectedLocation : AvailableLocations[0];
            SelectedLocation = -2;
            SelectedLocation = target;
        }
        // 刷新控制器选项
        private void RefreshControllerOptions()
        {
            if (SelectedVm == null) return;

            bool isGen1 = SelectedVm.Generation == 1;
            bool isDvd = DeviceType == "DvdDrive";

            AvailableControllerTypes.Clear();

            // --- 物理约束逻辑 ---
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
