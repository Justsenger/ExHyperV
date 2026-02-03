using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public enum VmDetailViewType { Dashboard, CpuSettings, CpuAffinity, MemorySettings, StorageSettings, AddStorage }

    public partial class VirtualMachinesPageViewModel : ObservableObject, IDisposable
    {
        private readonly VmQueryService _queryService;
        private readonly VmPowerService _powerService;
        private readonly VmProcessorService _vmProcessorService;
        private readonly CpuAffinityService _cpuAffinityService;
        private readonly VmMemoryService _vmMemoryService;
        private readonly VmStorageService _storageService;

        private CpuMonitorService _cpuService;
        private CancellationTokenSource _monitoringCts;
        private Task _cpuTask;
        private Task _stateTask;

        private const int MaxHistoryLength = 60;
        private readonly Dictionary<string, LinkedList<double>> _historyCache = new();
        private VmProcessorSettings _originalSettingsCache;
        private DispatcherTimer _uiTimer;

        [ObservableProperty] private bool _isLoading = true;
        [ObservableProperty] private bool _isLoadingSettings;
        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private ObservableCollection<VmInstanceInfo> _vmList = new();
        [ObservableProperty] private VmInstanceInfo _selectedVm;
        [ObservableProperty] private VmDetailViewType _currentViewType = VmDetailViewType.Dashboard;
        [ObservableProperty] private ObservableCollection<HostDiskInfo> _hostDisks = new();

        [ObservableProperty] private string _deviceType = "HardDisk";
        [ObservableProperty] private bool _isPhysicalSource = false;
        [ObservableProperty] private bool _autoAssign = true;
        [ObservableProperty] private string _filePath = string.Empty;
        [ObservableProperty] private bool _isNewDisk = false;
        [ObservableProperty] private int _newDiskSize = 128;
        [ObservableProperty] private string _selectedVhdType = "Dynamic";
        [ObservableProperty] private string _parentPath = string.Empty;
        [ObservableProperty] private string _sectorFormat = "Default";
        [ObservableProperty] private string _blockSize = "Default";
        [ObservableProperty] private string _isoSourceFolderPath = string.Empty;
        [ObservableProperty] private string _isoVolumeLabel = "NewISO";
        [ObservableProperty] private HostDiskInfo _selectedPhysicalDisk;
        [ObservableProperty] private string _selectedControllerType = "SCSI";
        [ObservableProperty] private int _selectedControllerNumber = 0;
        [ObservableProperty] private int _selectedLocation = 0;

        public List<string> AvailableControllerTypes { get; } = new() { "SCSI", "IDE" };
        public ObservableCollection<int> AvailableControllerNumbers { get; } = new() { 0, 1, 2, 3 };
        public ObservableCollection<int> AvailableLocations { get; } = new();
        public List<int> NewDiskSizePresets { get; } = new() { 32, 64, 128, 256, 512, 1024 };

        partial void OnDeviceTypeChanged(string value)
        {
            // 如果切换到光驱，自动取消“新建”勾选（通常不新建 ISO）
            if (value == "DvdDrive")
            {
                IsNewDisk = false;
            }

            // 清空当前路径，避免逻辑混乱
            FilePath = string.Empty;
        }

        // 定义路径输入框的占位符文字
        public string FilePathPlaceholder => DeviceType == "HardDisk"
            ? "选择或输入 .vhdx / .vhd 文件路径"
            : "选择或输入 .iso 镜像文件路径";

        public string BrowseButtonText => IsNewDisk ? "保存到..." : "浏览...";

        partial void OnIsNewDiskChanged(bool value)
        {
            OnPropertyChanged(nameof(BrowseButtonText));
            FilePath = string.Empty; // 切换状态时清空路径，防止混淆
        }

        partial void OnSelectedControllerTypeChanged(string value)
        {
            AvailableLocations.Clear();
            int maxLocation = (value == "IDE") ? 2 : 64; // IDE 每个通道只有 2 个位置，SCSI 有 64 个
            for (int i = 0; i < maxLocation; i++)
            {
                AvailableLocations.Add(i);
            }
            SelectedLocation = 0;
        }

        public List<string> AvailableOsTypes => Utils.SupportedOsTypes;
        public ObservableCollection<int> PossibleVCpuCounts { get; private set; }

        public VirtualMachinesPageViewModel(VmQueryService queryService, VmPowerService powerService)
        {
            _queryService = queryService;
            _powerService = powerService;
            _vmProcessorService = new VmProcessorService();
            _cpuAffinityService = new CpuAffinityService();
            _vmMemoryService = new VmMemoryService();
            _storageService = new VmStorageService();

            InitPossibleCpuCounts();

            for (int i = 0; i < 64; i++)
            {
                AvailableLocations.Add(i);
            }

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += (s, e) => { foreach (var vm in VmList) vm.TickUptime(); };
            _uiTimer.Start();

            Task.Run(async () => {
                await Task.Delay(300);
                Application.Current.Dispatcher.Invoke(() => LoadVmsCommand.Execute(null));
            });
        }

        private void InitPossibleCpuCounts()
        {
            var options = new HashSet<int>();
            int maxCores = Environment.ProcessorCount;
            int current = 1;
            while (current <= maxCores) { options.Add(current); current *= 2; }
            options.Add(maxCores);
            PossibleVCpuCounts = new ObservableCollection<int>(options.OrderBy(x => x));
        }

        private void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
        {
            Application.Current.Dispatcher.Invoke(() => {
                var presenter = Application.Current.MainWindow?.FindName("SnackbarPresenter") as SnackbarPresenter;
                if (presenter != null)
                {
                    var snack = new Snackbar(presenter) { Title = title, Content = message, Appearance = appearance, Icon = new SymbolIcon(icon), Timeout = TimeSpan.FromSeconds(3) };
                    snack.Show();
                }
            });
        }

        [RelayCommand]
        private async Task LoadVmsAsync()
        {
            if (IsLoading && VmList.Count > 0) return;
            IsLoading = true;
            try
            {
                var finalCollection = await Task.Run(async () => {
                    var vms = await _queryService.GetVmListAsync();
                    var sortedVms = vms.OrderBy(v => v.State == "已关机" ? 1 : 0).ThenBy(v => v.Name);
                    var list = new ObservableCollection<VmInstanceInfo>();
                    foreach (var vm in sortedVms)
                    {
                        var instance = new VmInstanceInfo(vm.Id, vm.Name)
                        {
                            OsType = vm.OsType,
                            CpuCount = vm.CpuCount,
                            MemoryGb = vm.MemoryGb,
                            Notes = vm.Notes,
                            Generation = vm.Generation,
                            Version = vm.Version,
                            GpuName = vm.GpuName
                        };

                        foreach (var disk in vm.Disks) instance.Disks.Add(disk);

                        instance.SyncBackendData(vm.State, vm.RawUptime);

                        instance.ControlCommand = new AsyncRelayCommand<string>(async (action) => {
                            instance.SetTransientState(GetOptimisticText(action));
                            try
                            {
                                await _powerService.ExecuteControlActionAsync(instance.Name, action);
                                await SyncSingleVmStateAsync(instance);
                            }
                            catch (Exception ex)
                            {
                                Application.Current.Dispatcher.Invoke(() => instance.ClearTransientState());
                                var realEx = ex;
                                while (realEx.InnerException != null) { realEx = realEx.InnerException; }
                                ShowSnackbar("操作失败", Utils.GetFriendlyErrorMessages(realEx.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                            }
                        });
                        list.Add(instance);
                    }
                    return list;
                });

                VmList = finalCollection;

                if (SelectedVm == null || !VmList.Any(x => x.Name == SelectedVm.Name))
                {
                    SelectedVm = VmList.FirstOrDefault();
                }

                StartMonitoring();
            }
            catch (Exception ex)
            {
                ShowSnackbar("加载失败", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private void GoToAddStorage()
        {
            FilePath = string.Empty;
            IsNewDisk = false;
            IsPhysicalSource = false;
            AutoAssign = true;
            IsoSourceFolderPath = string.Empty;
            CurrentViewType = VmDetailViewType.AddStorage;
        }

        [RelayCommand]
        private async Task ConfirmAddStorage()
        {
            if (SelectedVm == null) return;
            string target = IsPhysicalSource ? SelectedPhysicalDisk?.Number.ToString() : FilePath;
            if (string.IsNullOrEmpty(target) && !IsNewDisk) return;

            await AddDriveWrapperAsync(
                DeviceType,
                IsPhysicalSource,
                target,
                IsNewDisk,
                NewDiskSize,
                SelectedVhdType,
                ParentPath,
                IsoSourceFolderPath,
                IsoVolumeLabel);

            CurrentViewType = VmDetailViewType.StorageSettings;
        }

        [RelayCommand]
        private void CancelAddStorage() => CurrentViewType = VmDetailViewType.StorageSettings;

        [RelayCommand]
        private void BrowseFile()
        {
            // 如果是新建磁盘，使用 SaveFileDialog
            if (IsNewDisk && DeviceType == "HardDisk")
            {
                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "创建虚拟磁盘",
                    Filter = "虚拟磁盘 (*.vhdx)|*.vhdx|旧版虚拟磁盘 (*.vhd)|*.vhd",
                    DefaultExt = ".vhdx",
                    FileName = "NewVirtualDisk.vhdx" // 提供默认文件名
                };

                if (saveDialog.ShowDialog() == true)
                {
                    FilePath = saveDialog.FileName;
                }
            }
            // 如果是挂载现有文件，使用 OpenFileDialog
            else
            {
                var openDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = DeviceType == "HardDisk" ? "选择虚拟磁盘" : "选择 ISO 镜像",
                    Filter = DeviceType == "HardDisk" ?
                             "虚拟磁盘 (*.vhdx;*.vhd)|*.vhdx;*.vhd" :
                             "镜像文件 (*.iso)|*.iso"
                };

                if (openDialog.ShowDialog() == true)
                {
                    FilePath = openDialog.FileName;
                }
            }
        }

        [RelayCommand]
        private void BrowseFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if (dialog.ShowDialog() == true) IsoSourceFolderPath = dialog.FolderName;
        }

        [RelayCommand]
        private void BrowseParentFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "虚拟磁盘 (*.vhdx;*.vhd)|*.vhdx;*.vhd" };
            if (dialog.ShowDialog() == true) ParentPath = dialog.FileName;
        }

        [RelayCommand]
        private async Task ChangeOsType(string newType)
        {
            if (SelectedVm == null || SelectedVm.OsType == newType) return;
            string oldOsType = SelectedVm.OsType;
            string oldNotes = SelectedVm.Notes;
            SelectedVm.OsType = newType;
            SelectedVm.Notes = Utils.UpdateTagValue(SelectedVm.Notes, "OSType", newType);
            bool success = await _queryService.SetVmOsTypeAsync(SelectedVm.Name, newType);
            if (!success)
            {
                SelectedVm.OsType = oldOsType;
                SelectedVm.Notes = oldNotes;
                ShowSnackbar("修改失败", "无法保存标签，请检查权限", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }

        private string GetOptimisticText(string action) => action switch { "Start" => "正在启动", "Restart" => "正在重启", "Stop" => "正在关闭", "TurnOff" => "已关机", "Save" => "正在保存", "Suspend" => "正在暂停", _ => "处理中..." };

        private async Task SyncSingleVmStateAsync(VmInstanceInfo vm)
        {
            try
            {
                var allVms = await _queryService.GetVmListAsync();
                var freshData = allVms.FirstOrDefault(x => x.Name == vm.Name);
                if (freshData != null)
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        vm.SyncBackendData(freshData.State, freshData.RawUptime);
                        vm.Disks.Clear();
                        foreach (var disk in freshData.Disks) vm.Disks.Add(disk);
                        vm.Generation = freshData.Generation;
                        vm.Version = freshData.Version;
                        vm.GpuName = freshData.GpuName;
                    });
                }
            }
            catch { }
        }

        [RelayCommand]
        private async Task GoToCpuSettings()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.CpuSettings;
            IsLoadingSettings = true;
            try
            {
                var settings = await _vmProcessorService.GetVmProcessorAsync(SelectedVm.Name);
                if (settings != null)
                {
                    SelectedVm.Processor = settings;
                    _originalSettingsCache = settings.Clone();
                }
            }
            catch (Exception ex) { ShowSnackbar("加载失败", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally
            {
                await Task.Delay(200);
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task ApplyChangesAsync()
        {
            if (IsLoadingSettings || SelectedVm?.Processor == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await Task.Run(() => _vmProcessorService.SetVmProcessorAsync(SelectedVm.Name, SelectedVm.Processor));
                if (result.Success)
                    _originalSettingsCache = SelectedVm.Processor.Clone();
                else
                {
                    ShowSnackbar("应用失败", Utils.GetFriendlyErrorMessages(result.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    await GoToCpuSettings();
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar("系统异常", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                await GoToCpuSettings();
            }
            finally { IsLoadingSettings = false; }
        }

        [RelayCommand]
        private async Task GoToMemorySettings()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.MemorySettings;
            IsLoadingSettings = true;
            try
            {
                var settings = await _vmMemoryService.GetVmMemorySettingsAsync(SelectedVm.Name);
                if (settings != null)
                {
                    if (SelectedVm.MemorySettings != null)
                        SelectedVm.MemorySettings.PropertyChanged -= MemorySettings_PropertyChanged;
                    SelectedVm.MemorySettings = settings;
                    SelectedVm.MemorySettings.PropertyChanged += MemorySettings_PropertyChanged;
                }
            }
            catch (Exception ex) { ShowSnackbar("错误", $"加载失败: {Utils.GetFriendlyErrorMessages(ex.Message)}", ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally
            {
                await Task.Delay(200);
                IsLoadingSettings = false;
            }
        }

        private async void MemorySettings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var fastTrackProps = new[] { nameof(VmMemorySettings.BackingPageSize), nameof(VmMemorySettings.DynamicMemoryEnabled), nameof(VmMemorySettings.MemoryEncryptionPolicy) };
            if (fastTrackProps.Contains(e.PropertyName))
            {
                if (IsLoadingSettings || SelectedVm == null || SelectedVm.IsRunning || SelectedVm.MemorySettings == null)
                    return;

                IsLoadingSettings = true;
                try
                {
                    var result = await _vmMemoryService.SetVmMemorySettingsAsync(SelectedVm.Name, SelectedVm.MemorySettings);
                    if (!result.Success)
                    {
                        ShowSnackbar("自动应用失败", result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                        await GoToMemorySettings();
                    }
                    else OnPropertyChanged(nameof(SelectedVm));
                }
                finally
                {
                    await Task.Delay(200);
                    IsLoadingSettings = false;
                }
            }
        }

        [RelayCommand]
        private async Task ApplyMemorySettings()
        {
            if (SelectedVm?.MemorySettings == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await _vmMemoryService.SetVmMemorySettingsAsync(SelectedVm.Name, SelectedVm.MemorySettings);
                if (!result.Success) ShowSnackbar("保存失败", Utils.GetFriendlyErrorMessages(result.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                await GoToMemorySettings();
            }
            catch (Exception ex) { ShowSnackbar("异常", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoadingSettings = false; }
        }

        [RelayCommand]
        private async Task GoToStorageSettings()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.StorageSettings;

            // 关键改动：仅在存储列表为空时才加载数据
            if (SelectedVm.StorageItems.Count == 0)
            {
                IsLoadingSettings = true;
                try
                {
                    await _storageService.LoadVmStorageItemsAsync(SelectedVm);
                    await LoadHostDisksAsync();
                }
                catch (Exception ex) { ShowSnackbar("加载存储失败", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
                finally { IsLoadingSettings = false; }
            }
            // 如果数据已存在，则什么都不做，直接切换视图
        }
        private async Task LoadHostDisksAsync()
        {
            try
            {
                var disks = await _storageService.GetHostDisksAsync();
                Application.Current.Dispatcher.Invoke(() => HostDisks = new ObservableCollection<HostDiskInfo>(disks));
            }
            catch { }
        }

        [RelayCommand]
        private async Task RemoveStorageItem(VmStorageItem item)
        {
            if (SelectedVm == null || item == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await _storageService.RemoveDriveAsync(SelectedVm.Name, item);
                if (result.Success)
                {
                    ShowSnackbar("成功", result.Message == "Storage_Msg_Ejected" ? "光盘已弹出" : "设备已移除", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    await _storageService.LoadVmStorageItemsAsync(SelectedVm);
                }
                else ShowSnackbar("移除失败", result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            catch (Exception ex) { ShowSnackbar("错误", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoadingSettings = false; }
        }

        [RelayCommand]
        private async Task MountIso(VmStorageItem driveItem)
        {
            if (SelectedVm == null || driveItem == null || driveItem.DriveType != "DvdDrive") return;
            var openFileDialog = new Microsoft.Win32.OpenFileDialog { Filter = "Disk Images (*.iso)|*.iso|All files (*.*)|*.*" };
            if (openFileDialog.ShowDialog() == true)
            {
                IsLoadingSettings = true;
                try
                {
                    var result = await _storageService.ModifyDvdDrivePathAsync(SelectedVm.Name, driveItem.ControllerNumber, driveItem.ControllerLocation, openFileDialog.FileName);
                    if (result.Success)
                    {
                        ShowSnackbar("挂载成功", "ISO 已挂载", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                        await _storageService.LoadVmStorageItemsAsync(SelectedVm);
                    }
                    else ShowSnackbar("挂载失败", result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
                finally { IsLoadingSettings = false; }
            }
        }
        public async Task AddDriveWrapperAsync(string driveType, bool isPhysical, string pathOrNumber, bool isNew, int sizeGb = 128, string vhdType = "Dynamic", string parentPath = "", string isoSourcePath = null, string isoVolumeLabel = null)
        {
            if (SelectedVm == null) return;
            IsLoadingSettings = true;
            try
            {
                VmStorageSlot slot;
                if (AutoAssign)
                {
                    var (type, number, location) = await _storageService.GetNextAvailableSlotAsync(SelectedVm.Name, driveType);
                    slot = new VmStorageSlot { ControllerType = type, ControllerNumber = number, Location = location };
                }
                else
                {
                    slot = new VmStorageSlot { ControllerType = SelectedControllerType, ControllerNumber = SelectedControllerNumber, Location = SelectedLocation };
                }

                if (isPhysical && int.TryParse(pathOrNumber, out int diskNum))
                    await _storageService.SetDiskOfflineStatusAsync(diskNum, true);

                // --- 核心修改部分：补齐 SectorFormat 和 BlockSize ---
                var result = await _storageService.AddDriveAsync(
                    vmName: SelectedVm.Name,
                    controllerType: slot.ControllerType,
                    controllerNumber: slot.ControllerNumber,
                    location: slot.Location,
                    driveType: driveType,
                    pathOrNumber: pathOrNumber,
                    isPhysical: isPhysical,
                    isNew: isNew,
                    sizeGb: sizeGb,
                    vhdType: vhdType,
                    parentPath: parentPath,
                    sectorFormat: SectorFormat, // 必须传这个，否则后面的参数会错位
                    blockSize: BlockSize,       // 必须传这个，否则后面的参数会错位
                    isoSourcePath: isoSourcePath,
                    isoVolumeLabel: isoVolumeLabel
                );
                // ----------------------------------------------

                if (result.Success)
                {
                    ShowSnackbar("添加成功", $"设备已连接到 {result.ActualType} {result.ActualNumber}:{result.ActualLocation}", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    await _storageService.LoadVmStorageItemsAsync(SelectedVm);
                }
                else ShowSnackbar("添加失败", result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            catch (Exception ex) { ShowSnackbar("异常", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoadingSettings = false; }
        }
        [ObservableProperty] private ObservableCollection<VmCoreModel> _affinityHostCores;
        [ObservableProperty] private int _affinityColumns = 8;
        [ObservableProperty] private int _affinityRows = 1;

        [RelayCommand]
        private async Task GoToCpuAffinity()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.CpuAffinity; IsLoadingSettings = true;
            try
            {
                int totalCores = Environment.ProcessorCount;
                var currentAffinity = await _cpuAffinityService.GetCpuAffinityAsync(SelectedVm.Id);
                var coresList = new List<VmCoreModel>();
                for (int i = 0; i < totalCores; i++) coresList.Add(new VmCoreModel { CoreId = i, IsSelected = currentAffinity.Contains(i), CoreType = CpuMonitorService.GetCoreType(i) });
                AffinityHostCores = new ObservableCollection<VmCoreModel>(coresList);
                AffinityColumns = 8; AffinityRows = (int)Math.Ceiling((double)totalCores / AffinityColumns);
            }
            catch (Exception ex) { ShowSnackbar("加载亲和性失败", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoadingSettings = false; }
        }

        [RelayCommand]
        private async Task SaveAffinity()
        {
            if (SelectedVm == null || AffinityHostCores == null) return;
            IsLoadingSettings = true;
            try
            {
                var selectedIndices = AffinityHostCores.Where(c => c.IsSelected).Select(c => c.CoreId).ToList();
                if (await _cpuAffinityService.SetCpuAffinityAsync(SelectedVm.Id, selectedIndices)) GoToCpuSettings();
                else ShowSnackbar("保存失败", "无法应用亲和性设置，请检查 HCS权限", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            catch (Exception ex) { ShowSnackbar("错误", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoadingSettings = false; }
        }

        [RelayCommand] private void GoBackToDashboard() => CurrentViewType = VmDetailViewType.Dashboard;

        [RelayCommand]
        private void GoBack()
        {
            switch (CurrentViewType)
            {
                // 如果在“添加存储”界面，返回到“存储设置”列表
                case VmDetailViewType.AddStorage:
                    CurrentViewType = VmDetailViewType.StorageSettings;
                    break;

                // 如果在任何二级设置界面（CPU、内存、存储列表等），返回到“仪表盘”
                case VmDetailViewType.CpuSettings:
                case VmDetailViewType.CpuAffinity:
                case VmDetailViewType.MemorySettings:
                case VmDetailViewType.StorageSettings:
                    CurrentViewType = VmDetailViewType.Dashboard;
                    break;

                // 默认返回仪表盘
                default:
                    CurrentViewType = VmDetailViewType.Dashboard;
                    break;
            }
        }

        partial void OnSelectedVmChanged(VmInstanceInfo value) { CurrentViewType = VmDetailViewType.Dashboard; _originalSettingsCache = null; HostDisks.Clear(); }

        partial void OnSearchTextChanged(string value) { var view = CollectionViewSource.GetDefaultView(VmList); if (view != null) { view.Filter = item => (item is VmInstanceInfo vm) && (string.IsNullOrEmpty(value) || vm.Name.Contains(value, StringComparison.OrdinalIgnoreCase)); view.Refresh(); } }

        private void StartMonitoring() { if (_monitoringCts != null) return; _monitoringCts = new CancellationTokenSource(); _cpuTask = Task.Run(() => MonitorCpuLoop(_monitoringCts.Token)); _stateTask = Task.Run(() => MonitorStateLoop(_monitoringCts.Token)); }

        private async Task MonitorCpuLoop(CancellationToken token)
        {
            try { _cpuService = new CpuMonitorService(); } catch { return; }
            while (!token.IsCancellationRequested)
            {
                try { var rawData = _cpuService.GetCpuUsage(); Application.Current.Dispatcher.Invoke(() => ProcessAndApplyCpuUpdates(rawData));  await Task.Delay(1000, token); }
                catch (TaskCanceledException) { break; }
                catch { await Task.Delay(5000, token); }
            }
            _cpuService?.Dispose();
        }

        private async Task MonitorStateLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var updates = await _queryService.GetVmListAsync();
                    var memoryMap = await _queryService.GetVmRuntimeMemoryDataAsync();
                    Application.Current.Dispatcher.Invoke(() => {
                        foreach (var update in updates)
                        {
                            var vm = VmList.FirstOrDefault(v => v.Name == update.Name);
                            if (vm != null)
                            {
                                vm.SyncBackendData(update.State, update.RawUptime);
                                vm.Disks.Clear();
                                foreach (var disk in update.Disks) vm.Disks.Add(disk);
                                vm.GpuName = update.GpuName;
                                if (memoryMap.TryGetValue(vm.Id.ToString(), out var memData)) vm.UpdateMemoryStatus(memData.AssignedMb, memData.AvailablePercent);
                                else if (memoryMap.TryGetValue(vm.Id.ToString().ToUpper(), out var memDataUpper)) vm.UpdateMemoryStatus(memDataUpper.AssignedMb, memDataUpper.AvailablePercent);
                                else vm.UpdateMemoryStatus(0, 0);
                            }
                        }
                    });
                    await _queryService.UpdateDiskPerformanceAsync(VmList);
                    await Task.Delay(2000, token);
                }
                catch (TaskCanceledException) { break; }
                catch { await Task.Delay(3000, token); }
            }
        }
        private void ProcessAndApplyCpuUpdates(List<CpuCoreMetric> rawData) { var grouped = rawData.GroupBy(x => x.VmName); foreach (var group in grouped) { var vm = VmList.FirstOrDefault(v => v.Name == group.Key); if (vm == null) continue; vm.AverageUsage = vm.IsRunning ? group.Average(x => x.Usage) : 0; UpdateVmCores(vm, group.ToList()); } }
        private void UpdateVmCores(VmInstanceInfo vm, List<CpuCoreMetric> metrics) { var metricIds = metrics.Select(m => m.CoreId).ToHashSet(); vm.Cores.Where(c => !metricIds.Contains(c.CoreId)).ToList().ForEach(r => vm.Cores.Remove(r)); foreach (var metric in metrics) { var core = vm.Cores.FirstOrDefault(c => c.CoreId == metric.CoreId); if (core == null) { core = new VmCoreModel { CoreId = metric.CoreId }; int idx = 0; while (idx < vm.Cores.Count && vm.Cores[idx].CoreId < metric.CoreId) idx++; vm.Cores.Insert(idx, core); } core.Usage = metric.Usage; UpdateHistory(vm.Name, core); } vm.Columns = LayoutHelper.CalculateOptimalColumns(vm.Cores.Count); vm.Rows = (vm.Cores.Count > 0) ? (int)Math.Ceiling((double)vm.Cores.Count / vm.Columns) : 1; }
        private void UpdateHistory(string vmName, VmCoreModel core) { string key = $"{vmName}_{core.CoreId}"; if (!_historyCache.TryGetValue(key, out var history)) { history = new LinkedList<double>(); for (int k = 0; k < MaxHistoryLength; k++) history.AddLast(0); _historyCache[key] = history; } history.AddLast(core.Usage); if (history.Count > MaxHistoryLength) history.RemoveFirst(); core.HistoryPoints = CalculatePoints(history); }
        private PointCollection CalculatePoints(LinkedList<double> history) { double w = 100.0, h = 100.0, step = w / (MaxHistoryLength - 1); var points = new PointCollection(MaxHistoryLength + 2) { new Point(0, h) }; int i = 0; foreach (var val in history) points.Add(new Point(i++ * step, h - (val * h / 100.0))); points.Add(new Point(w, h)); points.Freeze(); return points; }
        public void Dispose() { _monitoringCts?.Cancel(); _cpuService?.Dispose(); _uiTimer?.Stop(); }
    }
}