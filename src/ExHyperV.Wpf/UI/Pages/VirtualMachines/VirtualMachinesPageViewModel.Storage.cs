using ExHyperV.Application;
using ExHyperV.UI.Shared.Interaction;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    public partial class VirtualMachinesPageViewModel
    {
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
        [ObservableProperty] private ObservableCollection<HostOpticalDriveInfo> _hostOpticals = new();
        [ObservableProperty] private HostOpticalDriveInfo _selectedPhysicalOptical;
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
        public Visibility OpticalTypeOptionVisibility => VmStorageService.GetSourceChoice(SelectedVm?.Generation ?? 0, IsPhysicalSource, DeviceType, IsNewDisk).SupportsOptical ? Visibility.Visible : Visibility.Collapsed;

        // 存储只读集合
        public ObservableCollection<string> AvailableControllerTypes { get; } = new();
        public ObservableCollection<int> AvailableControllerNumbers { get; } = new();
        public ObservableCollection<int> AvailableLocations { get; } = new();
        public List<int> NewDiskSizePresets { get; } = new() { 32, 64, 128, 256, 512, 1024 };



        [RelayCommand]
        private async Task GoToStorageSettingsAsync()
        {
            var vm = SelectedVm;
            if (vm == null) return;
            CurrentViewType = VmDetailViewType.StorageSettings;

            // 每次进入都重拉，同步外部(Hyper-V 管理器 / PowerShell 等)对该 VM 存储的改动——不再只在首次(Count==0)加载、留陈旧缓存
            IsLoadingSettings = true;
            try
            {
                await VmStoragePresentation.LoadVmStorageItemsAsync(vm);
                await LoadHostDisksAsync();
            }
            catch (Exception ex) { ShowError($"{Properties.Resources.Error_Storage_LoadFail}：{FriendlyError.CleanLines(ex.Message)}"); }
            finally { IsLoadingSettings = false; }
        }

        private async Task LoadHostDisksAsync()
        {
            HostDisks.Clear();
            var disks = (await HostDiskService.GetHostDisksAsync()).RequireData();
            HostDisks = new ObservableCollection<HostDiskInfo>(disks);
        }


        [RelayCommand]
        private async Task OptimizeStorageAsync(VmStorageItemViewModel item)
        {
            var vm = SelectedVm;
            if (item == null || vm == null || vm.IsRunning || item.IsOptimizing) return;

            item.IsOptimizing = true;

            try
            {
                // 虽然 await 会等待，但由于 vmms.exe 承载了 Job，即便 UI 崩溃，任务依然在后台跑
                var result = await VmStorageService.CompactDiskAsync(item.PathOrDiskNumber);

                if (result.Success)
                {
                    // 调用现有的存储服务，确保 UI 上的 GB 数值得到更新
                    await VmStoragePresentation.RefreshVirtualDiskSizesAsync(vm);

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
                item.IsOptimizing = false;
            }
        }

        [RelayCommand]
        private async Task RemoveStorageItemAsync(VmStorageItemViewModel item)
        {
            var vm = SelectedVm;
            if (vm == null || item == null || IsLoadingSettings) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmStorageService.RemoveDriveAsync(vm.Id, item.Model);
                if (result.Success)
                {
                    ShowSuccess(Properties.Resources.Msg_Storage_Removed);
                    await VmStoragePresentation.LoadVmStorageItemsAsync(vm);
                }
                else ShowError($"{Properties.Resources.Error_Storage_RemoveFail}：{result.Error}");
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
            finally { IsLoadingSettings = false; }
        }

        private bool CanEjectIso(VmStorageItemViewModel item) => item?.HasIsoMedia == true;

        [RelayCommand(CanExecute = nameof(CanEjectIso))]
        private async Task EjectIsoAsync(VmStorageItemViewModel item)
        {
            var vm = SelectedVm;
            if (vm == null || !CanEjectIso(item) || IsLoadingSettings) return;

            IsLoadingSettings = true;
            try
            {
                var result = await VmStorageService.ModifyDvdDrivePathAsync(
                    vm.Id, item.ControllerType, item.ControllerNumber, item.ControllerLocation, string.Empty);
                if (result.Success)
                {
                    ShowSuccess(Properties.Resources.Msg_Storage_Ejected);
                    await VmStoragePresentation.LoadVmStorageItemsAsync(vm);
                }
                else ShowError($"{Properties.Resources.Error_Storage_EjectFail}：{result.Error}");
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
            finally { IsLoadingSettings = false; }
        }

        // 判断是否可以编辑存储路径
        private bool CanEditStorage(VmStorageItemViewModel item)
        {
            return item != null && VmStorageService.CanEditStoragePath(item.Model);
        }

        // 修改存储路径（换盘/换ISO）
        [RelayCommand(CanExecute = nameof(CanEditStorage))]
        private async Task EditStoragePath(VmStorageItemViewModel driveItem)
        {
            var vm = SelectedVm;
            if (vm == null || driveItem == null || IsLoadingSettings) return;

            if (VmStorageService.GetEditStoragePathError(driveItem.Model, vm.IsRunning) is string editError)
            {
                ShowTip(editError);
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
                ApiResponse result;

                if (driveItem.DriveType == "DvdDrive")
                {
                    result = await VmStorageService.ModifyDvdDrivePathAsync(
                        vm.Id,
                        driveItem.ControllerType,
                        driveItem.ControllerNumber,
                        driveItem.ControllerLocation,
                        picked);
                }
                else
                {
                    result = await VmStorageService.ModifyHardDrivePathAsync(
                        vm.Id,
                        driveItem.ControllerType,
                        driveItem.ControllerNumber,
                        driveItem.ControllerLocation,
                        picked);
                }

                if (result.Success)
                {
                    ShowSuccess(Properties.Resources.Msg_Storage_PathUpdated);
                    await VmStoragePresentation.LoadVmStorageItemsAsync(vm);
                }
                else
                {
                    ShowError($"{Properties.Resources.Error_Common_ModFailShort}：{result.Error}");
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
        private async Task OpenFolderAsync(string path)
        {
            var reveal = await Shell.RevealAsync(path);
            if (!reveal.Success)
                ShowError($"{Properties.Resources.VmPage_OpenFail}：{reveal.Error}");
        }



        public int NewDiskSizeInt => VmStorageService.GetNewDiskSize(NewDiskSize);

        public string FilePathPlaceholder => DeviceType == "HardDisk"
            ? Properties.Resources.Placeholder_Vhd
            : Properties.Resources.Placeholder_Iso;

        public string BrowseButtonText => IsNewDisk ? Properties.Resources.Button_SaveTo : Properties.Resources.Button_Browse;

        partial void OnAutoAssignChanged(bool value)
        {
            if (value)
            {
                CalculateBestSlot();
            }
        }

        partial void OnNewDiskSizeChanged(string value)
        {
            if (int.TryParse(value, out int size) && size <= 0)
            {
                NewDiskSize = "128";
            }
        }

        partial void OnIsNewDiskChanged(bool value)
        {
            OnPropertyChanged(nameof(BrowseButtonText));
            FilePath = string.Empty;
        }

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

        partial void OnIsPhysicalSourceChanged(bool value)
        {
            var choice = VmStorageService.GetSourceChoice(SelectedVm?.Generation ?? 0, value, DeviceType, IsNewDisk);
            IsNewDisk = choice.IsNew;
            DeviceType = choice.DriveType;

            OnPropertyChanged(nameof(PhysicalDiskListVisibility));
            OnPropertyChanged(nameof(PhysicalOpticalListVisibility));
            OnPropertyChanged(nameof(OpticalTypeOptionVisibility));

            if (value && DeviceType == "DvdDrive") _ = LoadHostOpticalsAsync();
        }

        private async Task LoadHostOpticalsAsync()
        {
            HostOpticals.Clear();
            try
            {
                var opticals = (await HostDiskService.GetHostOpticalDrivesAsync()).RequireData();
                HostOpticals = new ObservableCollection<HostOpticalDriveInfo>(opticals);
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
        }

        partial void OnSelectedControllerTypeChanged(string value)
        {
            if (IsApplySuppressed || value == null) return;

            RefreshAvailableNumbers(value);

            // 切控制器类型会让"编号"和"位置"两个 ComboBox 的 ItemsSource 同时重建；若在此同步设值，
            // 会被容器的异步重建冲掉——表现为 1 代关自动分配、硬盘切光驱时位置丢空。
            // 延迟到 Loaded 优先级、等容器生成后再用跳变设值（与 SetSlot 同款保护）。
            WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                SelectedControllerNumber = -2;
                SelectedControllerNumber = AvailableControllerNumbers.FirstOrDefault();
                UpdateAvailableLocations();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        partial void OnSelectedControllerNumberChanged(int value)
        {
            // -2 是内部过渡值；锁定或过渡期间刷新位置列表会造成重复更新。
            if (value == -2 || IsApplySuppressed) return;

            UpdateAvailableLocations();
        }

        [RelayCommand]
        private async Task GoToAddStorageAsync()
        {
            var vm = SelectedVm;
            if (vm == null) return;

            IsLoadingSettings = true;
            try
            {
                await VmStoragePresentation.LoadVmStorageItemsAsync(vm);
                await LoadHostDisksAsync();   // 每次进添加界面都重拉主机物理盘列表，否则刚加过(已脱机)的盘还留在列表里被重复添加

                if (SelectedVm != vm) return;
                RefreshControllerOptions();

                if (AutoAssign) CalculateBestSlot();
                else UpdateAvailableLocations();

                CurrentViewType = VmDetailViewType.AddStorage;
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task ConfirmAddStorageAsync()
        {
            if (SelectedVm == null) return;

            var vm = SelectedVm;
            bool newDisk = !IsPhysicalSource && IsNewDisk && DeviceType == "HardDisk";
            bool newIso = !IsPhysicalSource && IsNewDisk && DeviceType == "DvdDrive";
            uint? blockSizeBytes = newDisk && BlockSize != "Default"
                ? uint.TryParse(BlockSize, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint parsedBlockSize) ? parsedBlockSize : 0u
                : null;
            var request = new VmStorageAddRequest
            {
                ControllerType = SelectedControllerType,
                ControllerNumber = SelectedControllerNumber,
                Location = SelectedLocation,
                DriveType = DeviceType,
                PathOrNumber = IsPhysicalSource
                    ? DeviceType == "DvdDrive" ? SelectedPhysicalOptical?.PnpDeviceId : SelectedPhysicalDisk?.Number.ToString()
                    : newIso ? IsoOutputPath : FilePath,
                IsPhysical = IsPhysicalSource,
                IsNew = IsNewDisk,
                VhdType = newDisk ? SelectedVhdType : "Dynamic",
                ParentPath = newDisk && SelectedVhdType == "Differencing" ? ParentPath : null,
                SizeGiB = newDisk && SelectedVhdType != "Differencing" ? NewDiskSizeInt : null,
                SectorFormat = newDisk ? SectorFormat : "Default",
                BlockSizeBytes = blockSizeBytes,
                IsoSourcePath = newIso ? IsoSourceFolderPath : null,
                IsoOutputPath = newIso ? IsoOutputPath : null,
                IsoVolumeLabel = newIso ? IsoVolumeLabel : null,
                PhysicalDisk = IsPhysicalSource && DeviceType == "HardDisk" ? SelectedPhysicalDisk : null
            };
            var prepared = VmStorageService.PrepareAddRequest(request,
                vm.Generation, vm.IsRunning, vm.StorageItems.Select(item => item.Model));
            if (!prepared.Success)
            {
                if (prepared.IsError) ShowError(prepared.Error!);
                else ShowTip(prepared.Error!);
                return;
            }
            await AddDriveWrapperAsync(vm, prepared.Request!);
            if (SelectedVm == vm) CurrentViewType = VmDetailViewType.StorageSettings;
        }

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
            if (picked != null)
            {
                IsoSourceFolderPath = picked;
                // 卷标签跟随文件夹名；制作器负责合法化。
                var folderName = Path.GetFileName(picked.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                IsoVolumeLabel = folderName;
            }
        }

        // 浏览父级磁盘
        [RelayCommand]
        private void BrowseParentFile()
        {
            var picked = Dialogs.PickOpenFile(null, Properties.Resources.Filter_VhdOnly,
                string.IsNullOrWhiteSpace(ParentPath) ? null : System.IO.Path.GetDirectoryName(ParentPath));
            if (picked != null) ParentPath = picked;
        }

        [RelayCommand]
        private void BrowseSaveIso()
        {
            var picked = Dialogs.PickSaveFile(Properties.Resources.Title_SaveIso, Properties.Resources.Filter_IsoExt, ".iso",
                string.IsNullOrWhiteSpace(IsoOutputPath) ? null : System.IO.Path.GetDirectoryName(IsoOutputPath),
                string.IsNullOrWhiteSpace(IsoOutputPath) ? $"{IsoVolumeLabel}.iso" : System.IO.Path.GetFileName(IsoOutputPath));
            if (picked != null) IsoOutputPath = picked;
        }

        private async Task AddDriveWrapperAsync(VmInstanceViewModel vm, VmStorageAddRequest request)
        {
            IsLoadingSettings = true;
            try
            {
                var result = await VmStorageService.AddDriveAsync(vm.Id, request);
                if (result.Success)
                {
                    var slot = result.RequireData();
                    ShowSuccess(string.Format(Properties.Resources.Msg_Storage_Connected, slot.ControllerType, slot.ControllerNumber, slot.Location));
                    await VmStoragePresentation.LoadVmStorageItemsAsync(vm);
                }
                else ShowError($"{Properties.Resources.Error_Storage_AddFail}：{result.Error}");
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
            finally { IsLoadingSettings = false; }
        }
        private void CalculateBestSlot()
        {
            if (SelectedVm == null) return;
            var plan = VmStorageService.GetBestSlot(SelectedVm.Generation, SelectedVm.IsRunning, DeviceType, SelectedVm.StorageItems.Select(item => item.Model));
            IsSlotValid = plan.Slot != null;
            SlotWarningMessage = plan.Error ?? string.Empty;
            if (plan.Slot is { } slot) SetSlot(slot.ControllerType, slot.ControllerNumber, slot.Location);
        }

        private void SetSlot(string type, int ctrlNum, int loc)
        {
            // 抑制跨 Dispatcher 回调：手动持有抑制域，待 Loaded 回调全部刷完再 Dispose（异常路径也 Dispose）。
            var suppression = SuppressApply();
            try
            {
                SelectedControllerType = type;
                RefreshAvailableNumbers(type);
                RefreshAvailableLocations(type, ctrlNum);

                // 使用 Loaded 优先级，这会等待 ComboBox 完成内部项的生成
                WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() => {

                    var targetNum = AvailableControllerNumbers.Contains(ctrlNum) ? ctrlNum : (AvailableControllerNumbers.Count > 0 ? AvailableControllerNumbers[0] : 0);

                    // 用 -2 强制触发 PropertyChanged，因为 -1 可能已经是当前 UI 的内部错误状态
                    SelectedControllerNumber = -2;
                    SelectedControllerNumber = targetNum;

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
            foreach (int number in VmStorageService.GetControllerNumbers(type))
                AvailableControllerNumbers.Add(number);
        }

        private void RefreshAvailableLocations(string type, int ctrlNum)
        {
            if (SelectedVm == null || type == null) return;

            AvailableLocations.Clear();
            foreach (int location in VmStorageService.GetAvailableLocations(type, ctrlNum, SelectedVm.StorageItems.Select(item => item.Model)))
                AvailableLocations.Add(location);
        }
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
                SlotWarningMessage = VmStorageService.GetControllerFullError(SelectedControllerType, SelectedControllerNumber, SelectedVm.StorageItems.Select(item => item.Model)) ?? string.Empty;
                return;
            }

            // 跳变 -2 再设目标，强制位置 ComboBox 刷新 SelectedItem——ItemsSource 刚 Clear+重填，
            // 目标若与当前值相同则直设不触发更新、ComboBox 会停在空选中态（位置显示丢空的另一半原因）。
            int target = AvailableLocations.Contains(SelectedLocation) ? SelectedLocation : AvailableLocations[0];
            SelectedLocation = -2;
            SelectedLocation = target;
        }
        private void RefreshControllerOptions()
        {
            if (SelectedVm == null) return;

            AvailableControllerTypes.Clear();
            foreach (string type in VmStorageService.GetControllerTypes(SelectedVm.Generation, SelectedVm.IsRunning, DeviceType))
                AvailableControllerTypes.Add(type);

            // 纠正当前选中项
            if (!AvailableControllerTypes.Contains(SelectedControllerType))
            {
                SelectedControllerType = AvailableControllerTypes.FirstOrDefault() ?? "SCSI";
            }
            else
            {
                OnSelectedControllerTypeChanged(SelectedControllerType);
            }
        }

    }
}
