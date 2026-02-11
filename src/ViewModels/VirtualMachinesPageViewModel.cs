using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public enum VmDetailViewType
    {
        Dashboard, CpuSettings, CpuAffinity, MemorySettings, StorageSettings, AddStorage,
        GpuSettings,
        AddGpuSelect,  
        AddGpuProgress, NetworkSettings
    }
    public partial class VirtualMachinesPageViewModel : ObservableObject, IDisposable
    {
        // ----------------------------------------------------------------------------------
        // 服务依赖与私有字段
        // ----------------------------------------------------------------------------------
        private readonly VmQueryService _queryService;
        private readonly VmPowerService _powerService;
        private readonly VmProcessorService _vmProcessorService;
        private readonly CpuAffinityService _cpuAffinityService;
        private readonly VmMemoryService _vmMemoryService;
        private readonly VmStorageService _storageService;
        private readonly VmGPUService _vmGpuService;

        private CpuMonitorService _cpuService;
        private CancellationTokenSource _monitoringCts;
        private Task _cpuTask;
        private Task _stateTask;

        private const int MaxHistoryLength = 60;
        private readonly Dictionary<string, LinkedList<double>> _historyCache = new();
        private VmProcessorSettings _originalSettingsCache;
        private DispatcherTimer _uiTimer;
        private DispatcherTimer? _thumbnailTimer;

        private Snackbar? _activeSnackbar; // 用于追踪当前显示的通知

        // ----------------------------------------------------------------------------------
        // 构造函数与初始化
        // ----------------------------------------------------------------------------------
        public VirtualMachinesPageViewModel(VmQueryService queryService, VmPowerService powerService)
        {
            _queryService = queryService;
            _powerService = powerService;
            _vmProcessorService = new VmProcessorService();
            _cpuAffinityService = new CpuAffinityService();
            _vmMemoryService = new VmMemoryService();
            _storageService = new VmStorageService();
            _vmGpuService = new VmGPUService(); // 初始化 GPU 服务

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

        public void Dispose()
        {
            _monitoringCts?.Cancel();
            _cpuService?.Dispose();
            _uiTimer?.Stop();
        }

        // ----------------------------------------------------------------------------------
        // 页面视图状态与导航
        // ----------------------------------------------------------------------------------
        [ObservableProperty] private bool _isLoading = true;
        [ObservableProperty] private bool _isLoadingSettings;
        [ObservableProperty] private VmDetailViewType _currentViewType = VmDetailViewType.Dashboard;
        [ObservableProperty] private string _searchText = string.Empty;

        partial void OnSearchTextChanged(string value)
        {
            var view = CollectionViewSource.GetDefaultView(VmList);
            if (view != null)
            {
                view.Filter = item => (item is VmInstanceInfo vm) && (string.IsNullOrEmpty(value) || vm.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
                view.Refresh();
            }
        }

        [RelayCommand] private void GoBackToDashboard() => CurrentViewType = VmDetailViewType.Dashboard;

        [RelayCommand]
        private void GoBack()
        {
            switch (CurrentViewType)
            {
                case VmDetailViewType.AddStorage:
                    CurrentViewType = VmDetailViewType.StorageSettings;
                    break;
                case VmDetailViewType.GpuSettings:
                case VmDetailViewType.CpuSettings:
                case VmDetailViewType.CpuAffinity:
                case VmDetailViewType.MemorySettings:
                case VmDetailViewType.StorageSettings:
                case VmDetailViewType.NetworkSettings:
                    CurrentViewType = VmDetailViewType.Dashboard;
                    break;
                default:
                    CurrentViewType = VmDetailViewType.Dashboard;
                    break;
            }
        }

        //虚拟机网络部分
        // ----------------------------------------------------------------------------------
        // 网络设置部分 (Network Settings)
        // ----------------------------------------------------------------------------------

        // 供界面下拉框绑定的虚拟交换机列表
        [ObservableProperty] private ObservableCollection<string> _availableSwitchNames = new();

        [RelayCommand]
        private async Task GoToNetworkSettings()
        {
            if (SelectedVm == null) return;

            CurrentViewType = VmDetailViewType.NetworkSettings;
            IsLoadingSettings = true;

            try
            {
                // =========================================================
                // MOCK DATA (模拟数据) - 用于预览 UI 效果
                // =========================================================

                // 1. 模拟宿主机的虚拟交换机列表
                AvailableSwitchNames.Clear();
                AvailableSwitchNames.Add("Default Switch");
                AvailableSwitchNames.Add("WSL (Hyper-V Firewall)");
                AvailableSwitchNames.Add("External Wi-Fi Bridge");
                AvailableSwitchNames.Add("Internal Private Network");

                // 2. 模拟当前虚拟机的网卡列表
                // 注意：先确保你在 VmInstanceInfo 中添加了 NetworkAdapters 属性 (上一步修复的)
                SelectedVm.NetworkAdapters.Clear();

                // --- 模拟网卡 1: 正常连接的业务网卡 ---
                SelectedVm.NetworkAdapters.Add(new VmNetworkAdapter
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "Network Adapter",
                    MacAddress = "00:15:5D:01:0A:03",
                    IsConnected = true,
                    SwitchName = "Default Switch",
                    IsStaticMac = false,

                    // 模拟 Guest OS 内部 IP
                    IpAddresses = new List<string> { "192.168.1.105", "fe80::215:5dff:fe01:a03" },

                    // 安全设置
                    MacSpoofingAllowed = true, // 模拟开启了 MAC 欺骗
                    DhcpGuardEnabled = false,

                    // 硬件加速
                    VmqEnabled = true,
                    SriovEnabled = false,

                    // 带宽
                    BandwidthLimit = 0, // 0 = 无限制
                    BandwidthReservation = 0,

                    VlanMode = VlanOperationMode.Access
                });

                // --- 模拟网卡 2: 用于隔离测试的网卡 (VLAN 20) ---
                SelectedVm.NetworkAdapters.Add(new VmNetworkAdapter
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "Isolation Adapter",
                    MacAddress = "00:15:5D:88:99:AA",
                    IsConnected = false, // 未连接状态
                    SwitchName = null,
                    IsStaticMac = true,

                    // 未连接自然没有 IP
                    IpAddresses = new List<string>(),

                    // 模拟配置了 VLAN
                    VlanMode = VlanOperationMode.Access,
                    AccessVlanId = 20,

                    // 模拟开启了一些高级功能
                    DeviceNamingEnabled = true,
                    TeamingAllowed = true
                });

                // 模拟加载耗时
                await Task.Delay(500);

                // =========================================================
                // END MOCK
                // =========================================================
            }
            catch (Exception ex)
            {
                ShowSnackbar("加载网络配置失败", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }
        [RelayCommand]
        private async Task AddNetworkAdapter()
        {
            if (SelectedVm == null) return;
            IsLoadingSettings = true;
            try
            {
                // 执行 PowerShell 添加网卡
                await Task.Run(() => Utils.Run($"Add-VMNetworkAdapter -VMName '{SelectedVm.Name}'"));

                ShowSnackbar("添加成功", "已添加新的网络适配器", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);

                // 稍作等待以让后台监控循环捕获到新网卡，或者手动触发刷新
                await Task.Delay(1000);
                // 重新进入以刷新界面
                await GoToNetworkSettings();
            }
            catch (Exception ex)
            {
                ShowSnackbar("添加失败", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task RemoveNetworkAdapter(string adapterId)
        {
            if (SelectedVm == null || string.IsNullOrEmpty(adapterId)) return;

            // 为了用户体验，先在 UI 上移除 (乐观更新)
            var adapterToRemove = SelectedVm.NetworkAdapters.FirstOrDefault(x => x.Id == adapterId);
            if (adapterToRemove != null)
            {
                // 注意：这里只是为了 UI 反应快，实际还需要后端删除
                // 如果你的 NetworkAdapters 是 ObservableCollection，可以直接 Remove
                // SelectedVm.NetworkAdapters.Remove(adapterToRemove); 
            }

            IsLoadingSettings = true;
            try
            {
                // 使用 ID 删除网卡 (需要处理 ID 格式，WMI ID 通常较长，这里假设 Utils.Run 能处理)
                // 更稳妥的方式是用 PowerShell 过滤器
                // Remove-VMNetworkAdapter -VMName 'VM' | Where-Object {$_.Id -eq 'ID'}

                string script = $"Get-VMNetworkAdapter -VMName '{SelectedVm.Name}' | Where-Object {{ $_.Id -eq '{adapterId}' }} | Remove-VMNetworkAdapter";
                await Task.Run(() => Utils.Run(script));

                ShowSnackbar("移除成功", "网络适配器已移除", ControlAppearance.Success, SymbolRegular.Delete24);

                await Task.Delay(1000);
                await GoToNetworkSettings();
            }
            catch (Exception ex)
            {
                ShowSnackbar("移除失败", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }


        // ----------------------------------------------------------------------------------
        // 虚拟机列表与核心操作 (加载/开关机/连接)
        // ----------------------------------------------------------------------------------
        [ObservableProperty] private ObservableCollection<VmInstanceInfo> _vmList = new();
        [ObservableProperty] private VmInstanceInfo _selectedVm;
        [ObservableProperty] private BitmapSource? _thumbnail;

        partial void OnSelectedVmChanged(VmInstanceInfo value)
        {
            CurrentViewType = VmDetailViewType.Dashboard;
            _originalSettingsCache = null;
            HostDisks.Clear();
        }

        public List<string> AvailableOsTypes => Utils.SupportedOsTypes;

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
                        if (string.IsNullOrWhiteSpace(vm.Name)) continue;

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
        private void OpenNativeConnect()
        {
            if (SelectedVm == null) return;
            try
            {
                System.Diagnostics.Process.Start("vmconnect.exe", $"localhost \"{SelectedVm.Name}\"");
            }
            catch (Exception ex)
            {
                ShowSnackbar("启动失败", "无法打开官方连接工具，请确保已安装 Hyper-V 管理组件。", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
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

        // ----------------------------------------------------------------------------------
        // CPU 设置部分
        // ----------------------------------------------------------------------------------
        public ObservableCollection<int> PossibleVCpuCounts { get; private set; }

        private void InitPossibleCpuCounts()
        {
            var options = new HashSet<int>();
            int maxCores = Environment.ProcessorCount;
            int current = 1;
            while (current <= maxCores) { options.Add(current); current *= 2; }
            options.Add(maxCores);
            PossibleVCpuCounts = new ObservableCollection<int>(options.OrderBy(x => x));
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

        // ----------------------------------------------------------------------------------
        // 内存设置部分
        // ----------------------------------------------------------------------------------
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

        // ----------------------------------------------------------------------------------
        // 存储管理 - 列表显示与编辑
        // ----------------------------------------------------------------------------------
        [ObservableProperty] private ObservableCollection<HostDiskInfo> _hostDisks = new();

        [RelayCommand]
        private async Task GoToStorageSettings()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.StorageSettings;

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

        private bool CanEditStorage(VmStorageItem item)
        {
            return item != null && item.DiskType != "Physical";
        }

        [RelayCommand(CanExecute = nameof(CanEditStorage))]
        private async Task EditStoragePath(VmStorageItem driveItem)
        {
            if (SelectedVm == null || driveItem == null) return;

            if (driveItem.DiskType == "Physical")
            {
                ShowSnackbar("操作受限", "物理直通磁盘无法修改路径，请移除后重新添加。", ControlAppearance.Danger, SymbolRegular.Warning24);
                return;
            }

            if (driveItem.DriveType == "HardDisk" && SelectedVm.IsRunning)
            {
                ShowSnackbar("操作受限", "无法在虚拟机运行时更换虚拟硬盘文件，请先关机。", ControlAppearance.Danger, SymbolRegular.Warning24);
                return;
            }

            string filter = driveItem.DriveType == "DvdDrive"
                ? "镜像文件 (*.iso)|*.iso|所有文件 (*.*)|*.*"
                : "虚拟磁盘 (*.vhdx;*.vhd)|*.vhdx;*.vhd|所有文件 (*.*)|*.*";

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = driveItem.DriveType == "DvdDrive" ? "选择 ISO 镜像" : "选择虚拟磁盘文件",
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
                        result = await _storageService.ModifyDvdDrivePathAsync(
                            SelectedVm.Name,
                            driveItem.ControllerNumber,
                            driveItem.ControllerLocation,
                            openFileDialog.FileName);
                    }
                    else
                    {
                        result = await _storageService.ModifyHardDrivePathAsync(
                            SelectedVm.Name,
                            driveItem.ControllerType,
                            driveItem.ControllerNumber,
                            driveItem.ControllerLocation,
                            openFileDialog.FileName);
                    }

                    if (result.Success)
                    {
                        ShowSnackbar("修改成功", "存储路径已更新", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                        await _storageService.LoadVmStorageItemsAsync(SelectedVm);
                    }
                    else
                    {
                        ShowSnackbar("修改失败", result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    }
                }
                catch (Exception ex)
                {
                    ShowSnackbar("错误", Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
                finally
                {
                    IsLoadingSettings = false;
                }
            }
        }

        private bool CanOpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (int.TryParse(path, out _)) return false;
            if (path.StartsWith("PhysicalDisk", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

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


        partial void OnAutoAssignChanged(bool value)
        {
            if (value)
            {
                CalculateBestSlot();
            }
        }

        // ----------------------------------------------------------------------------------
        // 存储管理 - 添加新设备 (向导相关属性与逻辑)
        // ----------------------------------------------------------------------------------
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

        // 选中的物理磁盘与控制器
        [ObservableProperty] private HostDiskInfo _selectedPhysicalDisk;
        [ObservableProperty] private string _selectedControllerType = "SCSI";
        [ObservableProperty] private int _selectedControllerNumber = 0;
        [ObservableProperty] private int _selectedLocation = 0;

        // 验证与提示
        [ObservableProperty] private string _slotWarningMessage = string.Empty;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SlotWarningVisibility))]
        private bool _isSlotValid = true;

        public Visibility SlotWarningVisibility => IsSlotValid ? Visibility.Collapsed : Visibility.Visible;

        // 只读集合
        public ObservableCollection<string> AvailableControllerTypes { get; } = new();
        public ObservableCollection<int> AvailableControllerNumbers { get; } = new();
        public ObservableCollection<int> AvailableLocations { get; } = new();
        public List<int> NewDiskSizePresets { get; } = new() { 32, 64, 128, 256, 512, 1024 };

        public int NewDiskSizeInt => int.TryParse(NewDiskSize, out int size) && size > 0 ? size : 128;

        public string FilePathPlaceholder => DeviceType == "HardDisk"
            ? "选择或输入 .vhdx / .vhd 文件路径"
            : "选择或输入 .iso 镜像文件路径";

        public string BrowseButtonText => IsNewDisk ? "保存到..." : "浏览...";

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

            RefreshControllerOptions();

            if (AutoAssign) CalculateBestSlot();
            else UpdateAvailableLocations();
        }

 
        partial void OnSelectedControllerTypeChanged(string value)
        {
            if (value == null) return;

            int currentNumber = SelectedControllerNumber;

            AvailableControllerNumbers.Clear();
            int maxCtrl = (value == "IDE") ? 2 : 4;
            for (int i = 0; i < maxCtrl; i++)
                AvailableControllerNumbers.Add(i);

            if (AvailableControllerNumbers.Contains(currentNumber))
            {
                SelectedControllerNumber = currentNumber;
            }
            else
            {
                SelectedControllerNumber = AvailableControllerNumbers.FirstOrDefault();
            }

            if (SelectedControllerNumber == currentNumber)
            {
                UpdateAvailableLocations();
            }
        }
        partial void OnSelectedControllerNumberChanged(int value)
        {
            UpdateAvailableLocations();
        }

        [RelayCommand]
        private async Task GoToAddStorage()
        {
            if (SelectedVm == null) return;

            IsLoadingSettings = true;
            try
            {
                await _storageService.LoadVmStorageItemsAsync(SelectedVm);

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

        [RelayCommand]
        private async Task ConfirmAddStorage()
        {
            if (SelectedVm == null) return;
            bool collision = SelectedVm.StorageItems.Any(i =>
                i.ControllerType == SelectedControllerType &&
                i.ControllerNumber == SelectedControllerNumber &&
                i.ControllerLocation == SelectedLocation);
            if (collision)
            {
                ShowSnackbar("位置冲突", "该插槽已被占用，请选择其他位置。", ControlAppearance.Danger, SymbolRegular.Warning24);
                return;
            }

            string target = IsPhysicalSource ? SelectedPhysicalDisk?.Number.ToString() : FilePath;
            if (string.IsNullOrEmpty(target) && !IsNewDisk) return;

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

        [RelayCommand]
        private void CancelAddStorage() => CurrentViewType = VmDetailViewType.StorageSettings;

        [RelayCommand]
        private void BrowseFile()
        {
            if (IsNewDisk && DeviceType == "HardDisk")
            {
                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "创建虚拟磁盘",
                    Filter = "虚拟磁盘 (*.vhdx)|*.vhdx|旧版虚拟磁盘 (*.vhd)|*.vhd",
                    DefaultExt = ".vhdx",
                    FileName = "NewVirtualDisk.vhdx"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    FilePath = saveDialog.FileName;
                }
            }
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
                    sectorFormat: SectorFormat,
                    blockSize: BlockSize,
                    isoSourcePath: isoSourcePath,
                    isoVolumeLabel: isoVolumeLabel
                );

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

        private void CalculateBestSlot()
        {
            if (SelectedVm == null) return;

            bool isGen1 = SelectedVm.Generation == 1;
            bool isDvd = DeviceType == "DvdDrive";

            if (isGen1)
            {
                for (int c = 0; c < 2; c++)
                {
                    for (int l = 0; l < 2; l++)
                    {
                        if (!IsSlotOccupied("IDE", c, l))
                        {
                            SetSlot("IDE", c, l);
                            return;
                        }
                    }
                }

                if (isDvd)
                {
                    IsSlotValid = false;
                    SlotWarningMessage = "第 1 代虚拟机的 IDE 控制器已满，无法添加光驱。";
                    return;
                }
            }

            for (int c = 0; c < 4; c++)
            {
                for (int l = 0; l < 64; l++)
                {
                    if (!IsSlotOccupied("SCSI", c, l))
                    {
                        SetSlot("SCSI", c, l);
                        return;
                    }
                }
            }

            IsSlotValid = false;
            SlotWarningMessage = "该虚拟机没有可用的存储插槽。";
        }

        private bool IsSlotOccupied(string type, int ctrlNum, int loc)
        {
            return SelectedVm.StorageItems.Any(i =>
                i.ControllerType == type &&
                i.ControllerNumber == ctrlNum &&
                i.ControllerLocation == loc);
        }

        private void SetSlot(string type, int ctrlNum, int loc)
        {
            SelectedControllerType = type;

            SelectedControllerNumber = ctrlNum;
            SelectedLocation = loc;

            IsSlotValid = true;
            SlotWarningMessage = string.Empty;
        }

        private void UpdateAvailableLocations()
        {
            if (SelectedVm == null || string.IsNullOrEmpty(SelectedControllerType)) return;

            int currentLocation = SelectedLocation;

            var usedLocations = SelectedVm.StorageItems
                .Where(i => i.ControllerType == SelectedControllerType &&
                            i.ControllerNumber == SelectedControllerNumber)
                .Select(i => i.ControllerLocation)
                .ToHashSet();

            int maxLoc = (SelectedControllerType == "IDE") ? 2 : 64;

            AvailableLocations.Clear();
            for (int i = 0; i < maxLoc; i++)
            {
                if (!usedLocations.Contains(i))
                {
                    AvailableLocations.Add(i);
                }
            }

            // 如果当前编号下没有可用位置，自动切换到下一个有位置的编号
            if (!AutoAssign && AvailableLocations.Count == 0)
            {
                foreach (int num in AvailableControllerNumbers)
                {
                    if (num == SelectedControllerNumber) continue;

                    var testUsedLocs = SelectedVm.StorageItems
                        .Where(i => i.ControllerType == SelectedControllerType &&
                                    i.ControllerNumber == num)
                        .Select(i => i.ControllerLocation)
                        .ToHashSet();

                    if (testUsedLocs.Count < maxLoc)
                    {
                        SelectedControllerNumber = num;
                        return;
                    }
                }
            }

            // 恢复位置选择
            if (AvailableLocations.Contains(currentLocation))
            {
                SelectedLocation = currentLocation;
            }
            else if (AvailableLocations.Count > 0)
            {
                SelectedLocation = AvailableLocations[0];
            }

            // 更新验证状态
            if (!AutoAssign)
            {
                if (AvailableLocations.Count == 0)
                {
                    IsSlotValid = false;
                    SlotWarningMessage = $"控制器 {SelectedControllerType} #{SelectedControllerNumber} 已满";
                }
                else
                {
                    IsSlotValid = true;
                    SlotWarningMessage = string.Empty;
                }
            }
            else
            {
                IsSlotValid = true;
                SlotWarningMessage = string.Empty;
            }
        }
        private void RefreshControllerOptions()
        {
            if (SelectedVm == null) return;

            // 只负责填充控制器类型列表
            AvailableControllerTypes.Clear();

            if (SelectedVm.Generation == 2)
            {
                AvailableControllerTypes.Add("SCSI");
            }
            else
            {
                AvailableControllerTypes.Add("IDE");
                if (DeviceType == "HardDisk")
                {
                    AvailableControllerTypes.Add("SCSI");
                }
            }

            // 选择合适的控制器类型（这会触发 OnSelectedControllerTypeChanged）
            if (!AvailableControllerTypes.Contains(SelectedControllerType))
            {
                SelectedControllerType = AvailableControllerTypes.FirstOrDefault() ?? "SCSI";
            }
            else
            {
                // ✅ 关键：即使类型没变，也要手动触发一次类型变更逻辑
                // 以确保编号和位置列表被正确初始化
                OnSelectedControllerTypeChanged(SelectedControllerType);
            }
        }        // ----------------------------------------------------------------------------------
        // CPU 亲和性部分
        // ----------------------------------------------------------------------------------
        [ObservableProperty] private ObservableCollection<VmCoreModel> _affinityHostCores;
        [ObservableProperty] private int _affinityColumns = 8;
        [ObservableProperty] private int _affinityRows = 1;

        [RelayCommand]
        private async Task GoToCpuAffinity()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.CpuAffinity;
            IsLoadingSettings = true;

            try
            {
                int totalCores = Environment.ProcessorCount;
                var currentAffinity = await _cpuAffinityService.GetCpuAffinityAsync(SelectedVm.Id);

                var coresList = new List<VmCoreModel>();
                for (int i = 0; i < totalCores; i++)
                {
                    coresList.Add(new VmCoreModel
                    {
                        CoreId = i,
                        IsSelected = currentAffinity.Contains(i),
                        CoreType = CpuMonitorService.GetCoreType(i)
                    });
                }
                AffinityHostCores = new ObservableCollection<VmCoreModel>(coresList);

                int bestCols = 4;
                if (totalCores <= 4)
                {
                    bestCols = totalCores;
                }
                else
                {
                    double minPenalty = double.MaxValue;
                    for (int c = 4; c <= 10; c++)
                    {
                        int r = (int)Math.Ceiling((double)totalCores / c);
                        int remainder = (c - (totalCores % c)) % c;
                        double wasteScore = (double)remainder / c;
                        double aspect = (double)c / r;
                        double aspectScore = Math.Abs(aspect - 1.5);
                        double totalPenalty = (wasteScore * 2.0) + aspectScore;

                        if (totalPenalty < minPenalty)
                        {
                            minPenalty = totalPenalty;
                            bestCols = c;
                        }
                    }
                }

                AffinityColumns = bestCols;
                AffinityRows = (int)Math.Ceiling((double)totalCores / AffinityColumns);
            }
            catch (Exception ex)
            {
                ShowSnackbar("加载亲和性失败", Utils.GetFriendlyErrorMessages(ex.Message),
                    ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
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

        // ----------------------------------------------------------------------------------
        // 后台监控循环与状态更新
        // ----------------------------------------------------------------------------------
        private void StartMonitoring() { if (_monitoringCts != null) return; _monitoringCts = new CancellationTokenSource(); _cpuTask = Task.Run(() => MonitorCpuLoop(_monitoringCts.Token)); _stateTask = Task.Run(() => MonitorStateLoop(_monitoringCts.Token)); }

        private async Task MonitorCpuLoop(CancellationToken token)
        {
            try { _cpuService = new CpuMonitorService(); } catch { return; }
            while (!token.IsCancellationRequested)
            {
                try { var rawData = _cpuService.GetCpuUsage(); Application.Current.Dispatcher.Invoke(() => ProcessAndApplyCpuUpdates(rawData)); await Task.Delay(1000, token); }
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

                    await _queryService.UpdateDiskPerformanceAsync(VmList);
                    var gpuUsageMap = await _queryService.GetGpuPerformanceAsync(VmList);


                    Application.Current.Dispatcher.Invoke(() => {
                        foreach (var update in updates)
                        {
                            var vm = VmList.FirstOrDefault(v => v.Name == update.Name);
                            if (vm != null)
                            {
                                vm.SyncBackendData(update.State, update.RawUptime);

                                vm.MacAddress = update.MacAddress;
                                if (vm.IsRunning)
                                {
                                    // IP 获取涉及 PowerShell，比较耗时，开启后台任务异步更新
                                    _ = Task.Run(async () => {
                                        try
                                        {
                                            var ip = await Utils.GetVmIpAddressAsync(vm.Name, vm.MacAddress);
                                            if (!string.IsNullOrEmpty(ip) && vm.IpAddress != ip)
                                            {
                                                Application.Current.Dispatcher.Invoke(() => vm.IpAddress = ip);
                                            }
                                        }
                                        catch { /* 忽略 IP 嗅探过程中的异常 */ }
                                    });
                                }
                                else
                                {
                                    vm.IpAddress = "---";
                                }


                                // --- 开始修复：增量更新磁盘列表，防止速率数据被 Clear 掉 ---
                                var updatePaths = update.Disks.Select(d => d.Path).ToHashSet();

                                // 1. 移除已不存在的磁盘
                                for (int i = vm.Disks.Count - 1; i >= 0; i--)
                                {
                                    if (!updatePaths.Contains(vm.Disks[i].Path))
                                        vm.Disks.RemoveAt(i);
                                }

                                // 2. 更新已有磁盘或添加新磁盘
                                foreach (var newDiskData in update.Disks)
                                {
                                    var existingDisk = vm.Disks.FirstOrDefault(d => d.Path == newDiskData.Path);
                                    if (existingDisk != null)
                                    {
                                        // 只同步元数据，不要覆盖 ReadSpeedBps 和 WriteSpeedBps
                                        existingDisk.Name = newDiskData.Name;
                                        existingDisk.CurrentSize = newDiskData.CurrentSize;
                                        existingDisk.MaxSize = newDiskData.MaxSize;
                                        existingDisk.DiskType = newDiskData.DiskType;
                                    }
                                    else
                                    {
                                        vm.Disks.Add(newDiskData);
                                    }
                                }
                                // --- 修复结束 ---

                                vm.GpuName = update.GpuName;

                                if (memoryMap.TryGetValue(vm.Id.ToString(), out var memData))
                                    vm.UpdateMemoryStatus(memData.AssignedMb, memData.AvailablePercent);
                                else if (memoryMap.TryGetValue(vm.Id.ToString().ToUpper(), out var memDataUpper))
                                    vm.UpdateMemoryStatus(memDataUpper.AssignedMb, memDataUpper.AvailablePercent);
                                else
                                    vm.UpdateMemoryStatus(0, 0);
                            }
                        }

                        if (gpuUsageMap.Count > 0)
                        {
                            foreach (var vm in VmList)
                            {
                                if (gpuUsageMap.TryGetValue(vm.Id, out var gpuData))
                                {
                                    vm.UpdateGpuStats(gpuData);
                                }
                                // 如果 VM 正在运行但 map 中没有它，其状态会在下次同步时被 IsRunning 属性清零
                            }
                        }

                    });

                    if (SelectedVm != null)
                    {
                        if (SelectedVm.IsRunning)
                        {
                            var img = await VmThumbnailProvider.GetThumbnailAsync(SelectedVm.Name, 320, 240);
                            if (img != null)
                            {
                                Application.Current.Dispatcher.Invoke(() => SelectedVm.Thumbnail = img);
                            }
                        }
                        else
                        {
                            if (SelectedVm.Thumbnail != null)
                            {
                                Application.Current.Dispatcher.Invoke(() => SelectedVm.Thumbnail = null);
                            }
                        }
                    }
                    await Task.Delay(2000, token);
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MonitorLoop Error] {ex.Message}");
                    await Task.Delay(3000, token);
                }
            }
        }
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

        private void ProcessAndApplyCpuUpdates(List<CpuCoreMetric> rawData) { var grouped = rawData.GroupBy(x => x.VmName); foreach (var group in grouped) { var vm = VmList.FirstOrDefault(v => v.Name == group.Key); if (vm == null) continue; vm.AverageUsage = vm.IsRunning ? group.Average(x => x.Usage) : 0; UpdateVmCores(vm, group.ToList()); } }
        private void UpdateVmCores(VmInstanceInfo vm, List<CpuCoreMetric> metrics) { var metricIds = metrics.Select(m => m.CoreId).ToHashSet(); vm.Cores.Where(c => !metricIds.Contains(c.CoreId)).ToList().ForEach(r => vm.Cores.Remove(r)); foreach (var metric in metrics) { var core = vm.Cores.FirstOrDefault(c => c.CoreId == metric.CoreId); if (core == null) { core = new VmCoreModel { CoreId = metric.CoreId }; int idx = 0; while (idx < vm.Cores.Count && vm.Cores[idx].CoreId < metric.CoreId) idx++; vm.Cores.Insert(idx, core); } core.Usage = metric.Usage; UpdateHistory(vm.Name, core); } vm.Columns = LayoutHelper.CalculateOptimalColumns(vm.Cores.Count); vm.Rows = (vm.Cores.Count > 0) ? (int)Math.Ceiling((double)vm.Cores.Count / vm.Columns) : 1; }
        private void UpdateHistory(string vmName, VmCoreModel core) { string key = $"{vmName}_{core.CoreId}"; if (!_historyCache.TryGetValue(key, out var history)) { history = new LinkedList<double>(); for (int k = 0; k < MaxHistoryLength; k++) history.AddLast(0); _historyCache[key] = history; } history.AddLast(core.Usage); if (history.Count > MaxHistoryLength) history.RemoveFirst(); core.HistoryPoints = CalculatePoints(history); }
        private PointCollection CalculatePoints(LinkedList<double> history) { double w = 100.0, h = 100.0, step = w / (MaxHistoryLength - 1); var points = new PointCollection(MaxHistoryLength + 2) { new Point(0, h) }; int i = 0; foreach (var val in history) points.Add(new Point(i++ * step, h - (val * h / 100.0))); points.Add(new Point(w, h)); points.Freeze(); return points; }

        // ----------------------------------------------------------------------------------
        // UI 辅助方法
        // ----------------------------------------------------------------------------------
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

        private string GetOptimisticText(string action) => action switch { "Start" => "正在启动", "Restart" => "正在重启", "Stop" => "正在关闭", "TurnOff" => "已关机", "Save" => "正在保存", "Suspend" => "正在暂停", _ => "处理中..." };


        //GPU部分
        // --- 添加显卡相关属性 ---
        [ObservableProperty] private ObservableCollection<GPUInfo> _hostGpus = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConfirmAddGpuCommand))] // 这里的 Command 必须存在
        private GPUInfo _selectedHostGpu;

        [ObservableProperty] private bool _autoInstallDrivers = true;
        [ObservableProperty] private ObservableCollection<TaskItem> _gpuTasks = new();
        [ObservableProperty] private bool _showPartitionSelector = false;
        [ObservableProperty] private ObservableCollection<PartitionInfo> _detectedPartitions = new();
        [ObservableProperty] private PartitionInfo _selectedPartition;
        [ObservableProperty] private bool _showSshForm = false;

        // --- Linux SSH 凭据绑定 (完整版) ---
        [ObservableProperty] private string _sshHost = "";         // 对应原 HostIP
        [ObservableProperty] private string _sshUsername = "root";
        [ObservableProperty] private string _sshPassword = "";
        [ObservableProperty] private int _sshPort = 22;
        [ObservableProperty] private bool _installGraphics = true;
        // 代理设置
        [ObservableProperty] private string _sshProxyHost = "";
        [ObservableProperty] private string _sshProxyPort = "";    // 字符串便于绑定，后台转换

        // --- 日志与控制台属性 ---
        [ObservableProperty] private string _gpuDeploymentLog = string.Empty;
        [ObservableProperty] private bool _showLogConsole = false;

        /// <summary>
        /// 向控制台追加一行带时间戳的日志
        /// </summary>
        private void AppendLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            // 确保在 UI 线程更新字符串，防止多线程竞争
            Application.Current.Dispatcher.Invoke(() => {
                GpuDeploymentLog += $"[{timestamp}] {message}{Environment.NewLine}";
            });
        }
        [RelayCommand]
        private void CopyLog()
        {
            if (!string.IsNullOrEmpty(GpuDeploymentLog))
            {
                Clipboard.SetText(GpuDeploymentLog);
                ShowSnackbar("复制成功", "配置日志已拷贝到剪贴板", ControlAppearance.Success, SymbolRegular.Copy24);
            }
        }




        [RelayCommand]
        private async Task GoToGpuSettings()
        {
            if (SelectedVm == null) return;

            CurrentViewType = VmDetailViewType.GpuSettings;
            IsLoadingSettings = true;
            try
            {
                // ✅ 1. 必须取消注释这一行，否则不会加载数据
                await RefreshCurrentVmGpuAssignments();
                // await Task.Delay(200); // 这一行可以留着也可以去掉
            }
            catch (Exception ex)
            {
                ShowSnackbar("加载失败", "无法读取 GPU 配置信息: " + ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }


        private async Task RefreshCurrentVmGpuAssignments()
        {
            if (SelectedVm == null) return;
            try
            {
                var vmAdapters = await _vmGpuService.GetVmGpuAdaptersAsync(SelectedVm.Name);
                var hostGpus = await _vmGpuService.GetHostGpusAsync();

                var tempList = new List<VmGpuAssignment>();

                foreach (var adapter in vmAdapters)
                {
                    var matchedHostGpu = hostGpus.FirstOrDefault(h =>
                        !string.IsNullOrEmpty(h.InstanceId) &&
                        !string.IsNullOrEmpty(adapter.InstancePath) &&
                        (adapter.InstancePath.Contains(h.InstanceId, StringComparison.OrdinalIgnoreCase) ||
                         NormalizeDeviceId(h.InstanceId) == NormalizeDeviceId(adapter.InstancePath)));

                    var assignment = new VmGpuAssignment { AdapterId = adapter.Id, InstanceId = adapter.InstancePath };

                    if (matchedHostGpu != null)
                    {
                        assignment.Name = matchedHostGpu.Name;
                        assignment.Manu = matchedHostGpu.Manu;
                        assignment.Vendor = matchedHostGpu.Vendor;
                        assignment.DriverVersion = matchedHostGpu.DriverVersion;
                        assignment.Ram = matchedHostGpu.Ram;
                        assignment.PName = matchedHostGpu.Pname;
                    }
                    else
                    {
                        assignment.Name = "Unknown Device";
                        assignment.Manu = "Default";
                    }
                    tempList.Add(assignment);
                }

                // ✅ 核心更新逻辑
                Application.Current.Dispatcher.Invoke(() => {
                    // 检查硬件 ID 序列是否一致
                    bool isHardwareSame = SelectedVm.AssignedGpus.Count == tempList.Count &&
                                         SelectedVm.AssignedGpus.Select(x => x.AdapterId)
                                                      .SequenceEqual(tempList.Select(x => x.AdapterId));

                    if (isHardwareSame)
                    {
                        // 1. 硬件没变，增量更新属性，UI 不会闪烁
                        for (int i = 0; i < tempList.Count; i++)
                        {
                            var target = SelectedVm.AssignedGpus[i];
                            var source = tempList[i];
                            target.Name = source.Name;
                            target.Manu = source.Manu;
                            target.Vendor = source.Vendor;
                            target.DriverVersion = source.DriverVersion;
                            target.Ram = source.Ram;
                            target.PName = source.PName;
                        }
                    }
                    else
                    {
                        // 2. 硬件数量或 ID 变了（添加或删除了卡），执行重建
                        SelectedVm.AssignedGpus.Clear();
                        foreach (var item in tempList) SelectedVm.AssignedGpus.Add(item);
                    }

                    // 3. 强制触发智能标签重新计算
                    SelectedVm.RefreshGpuSummary();
                });
            }
            catch (Exception ex)
            {
                ShowSnackbar("刷新显卡失败", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }
        /// <summary>
        /// 移除指定 ID 的 GPU 分区
        /// </summary>
        [RelayCommand]
        private async Task RemoveGpu(string adapterId)
        {
            if (SelectedVm == null || string.IsNullOrEmpty(adapterId)) return;

            // 1. 记录要移除的对象，用于后续 UI 乐观更新
            var itemToRemove = SelectedVm.AssignedGpus.FirstOrDefault(x => x.AdapterId == adapterId);
            if (itemToRemove == null) return;

            IsLoadingSettings = true;
            try
            {
                // 2. 调用服务执行物理移除
                bool success = await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, adapterId);

                if (success)
                {
                    // ✅ 3. 乐观 UI 更新：立即从本地集合中移除，不等待后端查询
                    // 这样用户点击按钮的瞬间，卡片就会消失，手感非常“解压”
                    Application.Current.Dispatcher.Invoke(() => {
                        SelectedVm.AssignedGpus.Remove(itemToRemove);

                        // 如果删光了，重置显卡名称显示
                        if (SelectedVm.AssignedGpus.Count == 0)
                        {
                            SelectedVm.GpuName = string.Empty;
                        }
                    });

                    ShowSnackbar("成功", "GPU 分区已从虚拟机移除。", ControlAppearance.Success, SymbolRegular.Checkmark24);

                    // 4. 后端强制同步（带一个延迟，避开 WMI 缓存抖动）
                    await Task.Delay(2000);
                    await RefreshCurrentVmGpuAssignments();
                }
                else
                {
                    ShowSnackbar("移除失败", "未能成功移除 GPU 分区，请检查权限。", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar("操作异常", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }
        /// <summary>
        /// 导航至添加新 GPU 的向导页面（预留）
        /// </summary>
        [RelayCommand]
        private async Task GoToAddGpu()
        {
            if (SelectedVm == null) return;

            IsLoadingSettings = true;
            try
            {
                var gpus = await _vmGpuService.GetHostGpusAsync();
                HostGpus = new ObservableCollection<GPUInfo>(gpus);

                // ✅ 修复：不再默认选中第一个，让列表初始为空
                SelectedHostGpu = null;

                CurrentViewType = VmDetailViewType.AddGpuSelect;
            }
            catch (Exception ex)
            {
                ShowSnackbar("错误", "无法加载宿主机显卡: " + ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task SelectPartitionAndContinue(PartitionInfo partition)
        {
            var driveTask = GpuTasks.FirstOrDefault(t => t.Name == "驱动安装");
            if (driveTask == null) return;

            if (partition.OsType == OperatingSystemType.Windows)
            {
                ShowPartitionSelector = false;
                driveTask.Status = ExHyperV.Models.TaskStatus.Running;
                driveTask.Description = $"正在同步驱动至分区 {partition.PartitionNumber}...";
                AppendLog(driveTask.Description);

                var result = await _vmGpuService.SyncWindowsDriversAsync(
                    SelectedVm.Name,
                    SelectedHostGpu.Pname,
                    SelectedHostGpu.Manu,
                    partition,
                    msg => {
                        driveTask.Description = msg;
                        AppendLog(msg);
                    });

                if (result.Success)
                {
                    driveTask.Status = ExHyperV.Models.TaskStatus.Success;
                    await FinishWorkflowAsync();
                }
                else
                {
                    driveTask.Status = ExHyperV.Models.TaskStatus.Failed;
                    driveTask.Description = result.Message;
                    AppendLog($"[错误] Windows 驱动同步失败: {result.Message}");
                }
            }
            else if (partition.OsType == OperatingSystemType.Linux)
            {
                SelectedPartition = partition;
                IsLoadingSettings = true;
                driveTask.Description = "正在分析 Linux 环境并获取网络地址...";
                AppendLog($">>> 用户选择了 Linux 分区: {partition.DisplayName}");

                try
                {
                    // 自动填充系统代理
                    var (pHost, pPort) = Utils.GetWindowsSystemProxy();
                    SshProxyHost = pHost;
                    SshProxyPort = pPort;
                    if (!string.IsNullOrEmpty(pHost)) AppendLog($"已自动识别系统代理: {pHost}:{pPort}");

                    // 自动开机
                    var status = await _vmGpuService.IsVmPoweredOffAsync(SelectedVm.Name);
                    if (status.IsOff)
                    {
                        driveTask.Description = "正在自动启动虚拟机以初始化网络...";
                        AppendLog(driveTask.Description);
                        await _powerService.ExecuteControlActionAsync(SelectedVm.Name, "Start");
                        await Task.Delay(3000);
                    }

                    // IP 嗅探
                    driveTask.Description = "正在嗅探虚拟机 IP 地址...";
                    AppendLog(driveTask.Description);

                    string vmIp = await Task.Run(async () =>
                    {
                        string getMacScript = $"(Get-VMNetworkAdapter -VMName '{SelectedVm.Name}').MacAddress | Select-Object -First 1";
                        var macResult = Utils.Run(getMacScript);

                        if (macResult != null && macResult.Count > 0)
                        {
                            string rawMac = macResult[0].ToString();
                            string formattedMac = System.Text.RegularExpressions.Regex.Replace(rawMac, "(.{2})", "$1:").TrimEnd(':');
                            AppendLog($"获取到 MAC 地址: {formattedMac}，正在匹配 IP...");

                            for (int i = 0; i < 3; i++)
                            {
                                var ip = await Utils.GetVmIpAddressAsync(SelectedVm.Name, formattedMac);
                                if (!string.IsNullOrEmpty(ip)) return ip;
                                await Task.Delay(2000);
                            }
                        }
                        return string.Empty;
                    });

                    if (!string.IsNullOrEmpty(vmIp))
                    {
                        SshHost = vmIp.Split(',')
                                     .Select(ip => ip.Trim())
                                     .FirstOrDefault(ip => System.Net.IPAddress.TryParse(ip, out var addr)
                                                     && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                     ?? string.Empty;
                        AppendLog($"成功获取 IP 地址: {SshHost}");
                    }
                    else
                    {
                        AppendLog("未能自动获取到 IP，请手动输入。");
                    }

                    ShowSshForm = true;
                    driveTask.Description = "请确认 SSH 凭据以开始部署...";
                }
                catch (Exception ex)
                {
                    ShowSnackbar("环境获取失败", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    AppendLog($"[警告] 获取环境异常: {ex.Message}");
                    ShowSshForm = true;
                }
                finally
                {
                    IsLoadingSettings = false;
                }
            }
        }

        [RelayCommand]
        private void CancelAddGpu()
        {
            CurrentViewType = VmDetailViewType.GpuSettings;
            GpuTasks.Clear();
        }

        // ----------------------------------------------------------------------------------
        // GPU 添加流程控制
        // ----------------------------------------------------------------------------------

        private bool CanConfirmAddGpu() => SelectedHostGpu != null;

        [RelayCommand(CanExecute = nameof(CanConfirmAddGpu))]
        private async Task ConfirmAddGpu()
        {
            if (SelectedHostGpu == null) return;

            CurrentViewType = VmDetailViewType.AddGpuProgress;
            ShowPartitionSelector = false;

            // 初始化日志状态
            GpuDeploymentLog = string.Empty;
            ShowLogConsole = true;

            AppendLog($">>> 开始为虚拟机 [{SelectedVm.Name}] 配置 GPU 分区");
            AppendLog($"选中物理显卡: {SelectedHostGpu.Name}");
            AppendLog($"设备路径: {SelectedHostGpu.Pname}");

            GpuTasks.Clear();
            GpuTasks.Add(new TaskItem { Name = "环境准备", Description = "正在准备宿主机环境...", Status = ExHyperV.Models.TaskStatus.Pending });
            GpuTasks.Add(new TaskItem { Name = "电源检查", Description = "检查虚拟机电源状态...", Status = ExHyperV.Models.TaskStatus.Pending });
            GpuTasks.Add(new TaskItem { Name = "系统优化", Description = "正在配置 MMIO 地址空间...", Status = ExHyperV.Models.TaskStatus.Pending });
            GpuTasks.Add(new TaskItem { Name = "分配显卡", Description = "正在创建 GPU 分区...", Status = ExHyperV.Models.TaskStatus.Pending });

            if (AutoInstallDrivers)
            {
                GpuTasks.Add(new TaskItem { Name = "驱动安装", Description = "等待扫描分区...", Status = ExHyperV.Models.TaskStatus.Pending });
            }

            // 执行真实工作流
            await RunRealGpuWorkflowAsync(0);
        }

        private string NormalizeDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return string.Empty;
            var normalizedId = deviceId.ToUpper();
            if (normalizedId.StartsWith(@"\\?\")) normalizedId = normalizedId.Substring(4);
            int suffixIndex = normalizedId.IndexOf("#{"); // 去掉 GUID 后缀
            if (suffixIndex != -1) normalizedId = normalizedId.Substring(0, suffixIndex);
            // 统一斜杠
            return normalizedId.Replace('\\', '#').Replace("#", "");
        }

        // 用于从 SSH 表单返回到分区列表界面
        [RelayCommand]
        private void GoBackToPartitionList()
        {
            ShowSshForm = false;

            // 恢复一下任务描述
            var driveTask = GpuTasks.FirstOrDefault(t => t.Name == "驱动安装");
            if (driveTask != null)
            {
                driveTask.Description = "请选择目标分区进行适配...";
            }
        }

        private async Task RunRealGpuWorkflowAsync(int startIndex)
        {
            var tasks = GpuTasks;

            for (int i = startIndex; i < tasks.Count; i++)
            {
                var task = tasks[i];
                task.Status = ExHyperV.Models.TaskStatus.Running;
                AppendLog($"正在执行: {task.Name}...");

                try
                {
                    switch (task.Name)
                    {
                        case "环境准备":
                            await _vmGpuService.PrepareHostEnvironmentAsync();
                            task.Description = "宿主机策略已成功应用。";
                            break;

                        case "电源检查":
                            var (isOff, state) = await _vmGpuService.IsVmPoweredOffAsync(SelectedVm.Name);
                            if (!isOff)
                            {
                                task.Description = $"当前状态: {state}。正在强制关闭虚拟机...";
                                AppendLog(task.Description);
                                await _powerService.ExecuteControlActionAsync(SelectedVm.Name, "TurnOff");
                                while (!(await _vmGpuService.IsVmPoweredOffAsync(SelectedVm.Name)).IsOff)
                                {
                                    await Task.Delay(1000);
                                }
                            }
                            task.Description = "虚拟机已就绪 (Off)。";
                            break;

                        case "系统优化":
                            bool optOk = await _vmGpuService.OptimizeVmForGpuAsync(SelectedVm.Name);
                            task.Description = optOk ? "MMIO 地址空间配置完成 (64GB High / 1GB Low)。" : "优化配置失败，将尝试继续。";
                            break;

                        case "分配显卡":
                            string targetPath = !string.IsNullOrEmpty(SelectedHostGpu.Pname)
                                                ? SelectedHostGpu.Pname
                                                : SelectedHostGpu.InstanceId;

                            var assignRes = await _vmGpuService.AssignGpuPartitionAsync(SelectedVm.Name, targetPath);
                            if (!assignRes.Success) throw new Exception(assignRes.Message);
                            task.Description = "GPU 分区已成功创建并绑定。";
                            break;

                        case "驱动安装":
                            task.Description = "正在分析虚拟机磁盘分区...";
                            AppendLog(task.Description);
                            var partitions = await _vmGpuService.GetPartitionsFromVmAsync(SelectedVm.Name);

                            if (partitions.Count == 1 && partitions[0].OsType == OperatingSystemType.Windows)
                            {
                                task.Description = "已识别到 Windows 主分区，正在同步驱动...";
                                var syncRes = await _vmGpuService.SyncWindowsDriversAsync(
                                    SelectedVm.Name,
                                    SelectedHostGpu.Pname,
                                    SelectedHostGpu.Manu,
                                    partitions[0],
                                    msg => {
                                        task.Description = msg;
                                        AppendLog(msg);
                                    });

                                if (!syncRes.Success) throw new Exception(syncRes.Message);
                                task.Description = "Windows 驱动安装成功。";
                            }
                            else
                            {
                                Application.Current.Dispatcher.Invoke(() => {
                                    DetectedPartitions = new ObservableCollection<PartitionInfo>(partitions);
                                    ShowPartitionSelector = true;
                                    ShowSshForm = false;
                                });
                                task.Description = "检测到多个环境或 Linux，等待用户选择注入目标...";
                                AppendLog(task.Description);
                                return;
                            }
                            break;
                    }

                    task.Status = ExHyperV.Models.TaskStatus.Success;
                    AppendLog($"[成功] {task.Name}: {task.Description}");
                    await Task.Delay(300);
                }
                catch (Exception ex)
                {
                    task.Status = ExHyperV.Models.TaskStatus.Failed;
                    task.Description = $"失败: {ex.Message}";
                    AppendLog($"[错误] {task.Name} 环节异常: {ex.Message}");
                    ShowSnackbar("操作失败", $"{task.Name} 环节出现异常", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    return;
                }
            }

            await FinishWorkflowAsync();
        }

        private async Task FinishWorkflowAsync()
        {
            await Task.Delay(1000);
            await RefreshCurrentVmGpuAssignments();
            CurrentViewType = VmDetailViewType.GpuSettings;
            ShowSnackbar("配置成功", $"{SelectedHostGpu.Name} 已准备就绪", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
        }

        [RelayCommand]
        private async Task StartLinuxDeploy()
        {
            var driveTask = GpuTasks.FirstOrDefault(t => t.Name == "驱动安装");
            if (driveTask == null) return;

            if (string.IsNullOrWhiteSpace(SshHost))
            {
                ShowSnackbar("验证失败", "主机 IP 地址不能为空", ControlAppearance.Danger, SymbolRegular.Warning24);
                return;
            }

            AppendLog($">>> 开始 Linux 部署流程");
            AppendLog($"连接参数 - Host: {SshHost}, Port: {SshPort}, User: {SshUsername}");
            if (!string.IsNullOrEmpty(SshProxyHost)) AppendLog($"使用代理: {SshProxyHost}:{SshProxyPort}");

            // ✅ 关键修正：隐藏整个配置卡片，露出底下的任务列表和日志控制台
            ShowPartitionSelector = false;
            ShowSshForm = false;

            driveTask.Status = ExHyperV.Models.TaskStatus.Running;

            var creds = new SshCredentials
            {
                Host = SshHost,
                Port = SshPort,
                Username = SshUsername,
                Password = SshPassword,
                ProxyHost = SshProxyHost,
                ProxyPort = int.TryParse(SshProxyPort, out int pp) ? pp : null,
                InstallGraphics = InstallGraphics
            };

            string result = await _vmGpuService.ProvisionLinuxGpuAsync(
                SelectedVm.Name,
                SelectedHostGpu.InstanceId,
                creds,
                msg => {
                    driveTask.Description = msg;
                    AppendLog(msg); // 实时追加每一行部署日志
                },
                CancellationToken.None
            );

            if (result == "OK")
            {
                driveTask.Status = ExHyperV.Models.TaskStatus.Success;
                AppendLog(">>> Linux 部署任务全部完成。");
                await FinishWorkflowAsync();
            }
            else
            {
                driveTask.Status = ExHyperV.Models.TaskStatus.Failed;
                driveTask.Description = result;
                AppendLog($">>> [严重错误] Linux 部署失败: {result}");
                // 如果失败了，可以考虑把面板再弹回来，或者让用户看日志
            }
        }


        [RelayCommand]
        private void CopyToClipboard(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text == "---" || text == "00-00-00-00-00-00") return;

            Clipboard.SetText(text);

            // 调用你已有的通知方法，给用户反馈
            ShowSnackbar("已复制", text, ControlAppearance.Success, SymbolRegular.Copy24);
        }
    }
}