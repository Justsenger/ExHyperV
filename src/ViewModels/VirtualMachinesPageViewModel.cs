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
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using ExHyperV.Behaviors;

namespace ExHyperV.ViewModels
{
    public enum VmDetailViewType
    {
        Dashboard, CpuSettings, CpuAffinity, MemorySettings, StorageSettings, AddStorage,
        GpuSettings,
        AddGpuSelect,
        AddGpuProgress, NetworkSettings, BootSettings
    }
    public partial class VirtualMachinesPageViewModel : ObservableObject, IDisposable
    {
        // ----------------------------------------------------------------------------------
        // 私有服务字段与依赖注入
        // ----------------------------------------------------------------------------------
        private readonly VmQueryService _queryService;
        private readonly VmPowerService _powerService;
        private readonly VmProcessorService _vmProcessorService;
        private readonly CpuAffinityService _cpuAffinityService;
        private readonly VmMemoryService _vmMemoryService;
        private readonly VmStorageService _storageService;
        private readonly VmGPUService _vmGpuService;
        private readonly VmNetworkService _vmNetworkService;
        private readonly VmCreateService _vmCreateService = new();
        private readonly VmEditService _vmEditService = new();
        private readonly VmBootService _vmBootService = new();

        // ----------------------------------------------------------------------------------
        // 监控与后台任务字段
        // ----------------------------------------------------------------------------------
        private CpuMonitorService _cpuService;
        private CancellationTokenSource _monitoringCts;
        private Task _cpuTask;
        private Task _stateTask;
        private DispatcherTimer _uiTimer;

        private readonly Dictionary<Guid, (string NewName, DateTime Expiry)> _renameLockouts = new();

        // ----------------------------------------------------------------------------------
        // 缓存与状态字段
        // ----------------------------------------------------------------------------------
        private const int MaxHistoryLength = 60;
        private readonly Dictionary<string, LinkedList<double>> _historyCache = new();
        private VmProcessorSettings _originalSettingsCache;
        private bool _isInternalUpdating = false;
        private bool _isDiskPathManual = false; // 记录用户是否手动选择过磁盘路径

        // ----------------------------------------------------------------------------------
        // 视图模型属性 - 页面状态
        // ----------------------------------------------------------------------------------
        [ObservableProperty] private bool _isLoading = true;
        [ObservableProperty] private bool _isLoadingSettings;
        [ObservableProperty] private VmDetailViewType _currentViewType = VmDetailViewType.Dashboard;
        [ObservableProperty] private string _searchText = string.Empty;

        // ----------------------------------------------------------------------------------
        // 视图模型属性 - 虚拟机列表与选择
        // ----------------------------------------------------------------------------------
        [ObservableProperty] private ObservableCollection<VmInstanceInfo> _vmList = new();
        [ObservableProperty] private VmInstanceInfo _selectedVm;
        [ObservableProperty] private BitmapSource? _thumbnail;

        // ----------------------------------------------------------------------------------
        // 视图模型属性 - CPU 设置
        // ----------------------------------------------------------------------------------
        public ObservableCollection<int> PossibleVCpuCounts { get; private set; }
        [ObservableProperty] private ObservableCollection<VmCoreModel> _affinityHostCores;
        [ObservableProperty] private int _affinityColumns = 8;
        [ObservableProperty] private int _affinityRows = 1;

        // ----------------------------------------------------------------------------------
        // 视图模型属性 - 存储管理
        // ----------------------------------------------------------------------------------
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

        // ----------------------------------------------------------------------------------
        // 视图模型属性 - 网络设置
        // ----------------------------------------------------------------------------------
        [ObservableProperty] private ObservableCollection<string> _availableSwitchNames = new();

        // ----------------------------------------------------------------------------------
        // 视图模型属性 - GPU 管理
        // ----------------------------------------------------------------------------------
        [ObservableProperty] private ObservableCollection<GPUInfo> _hostGpus = new();
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ConfirmAddGpuCommand))] private GPUInfo _selectedHostGpu;
        [ObservableProperty] private bool _autoInstallDrivers = true;
        [ObservableProperty] private ObservableCollection<TaskItem> _gpuTasks = new();
        [ObservableProperty] private bool _showPartitionSelector = false;
        [ObservableProperty] private ObservableCollection<PartitionInfo> _detectedPartitions = new();
        [ObservableProperty] private PartitionInfo? _selectedPartition;
        [ObservableProperty] private bool _showSshForm = false;
        [ObservableProperty] private string? _currentProcessingGpuAdapterId;
        private bool _needConfig = false;

        // Linux SSH 凭据
        [ObservableProperty] private string _sshHost = "";
        [ObservableProperty] private string _sshUsername = "root";
        [ObservableProperty] private string _sshPassword = "";
        [ObservableProperty] private int _sshPort = 22;
        [ObservableProperty] private bool _installGraphics = true;
        [ObservableProperty] private bool _useSshProxy = false;
        [ObservableProperty] private string _sshProxyHost = "";
        [ObservableProperty] private string _sshProxyPort = "";
        private CancellationTokenSource? _gpuDeploymentCts;

        // 日志与控制台
        [ObservableProperty] private string _gpuDeploymentLog = string.Empty;
        [ObservableProperty] private bool _showLogConsole = false;

        // ----------------------------------------------------------------------------------
        // 构造函数与资源释放
        // ----------------------------------------------------------------------------------

        // Linux 部署字段

        [ObservableProperty] private ObservableCollection<LinuxScriptItem> _availableLinuxScripts = new();
        [ObservableProperty] private LinuxScriptItem _selectedLinuxScript;

        public VirtualMachinesPageViewModel(VmQueryService queryService, VmPowerService powerService)
        {
            _queryService = queryService;
            _powerService = powerService;
            _vmProcessorService = new VmProcessorService();
            _cpuAffinityService = new CpuAffinityService();
            _vmMemoryService = new VmMemoryService();
            _storageService = new VmStorageService();
            _vmGpuService = new VmGPUService();
            _vmNetworkService = new VmNetworkService();

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
        // 导航与页面状态控制
        // ----------------------------------------------------------------------------------

        // 搜索框文本变化时的过滤逻辑
        partial void OnSearchTextChanged(string value)
        {
            var view = CollectionViewSource.GetDefaultView(VmList);
            if (view != null)
            {
                view.Filter = item => (item is VmInstanceInfo vm) && (string.IsNullOrEmpty(value) || vm.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
                view.Refresh();
            }
        }

        // 返回仪表盘
        [RelayCommand]
        private void GoBackToDashboard() => CurrentViewType = VmDetailViewType.Dashboard;

        // 根据当前视图层级返回上一级
        [RelayCommand]
        private void GoBack()
        {
            switch (CurrentViewType)
            {
                case VmDetailViewType.AddStorage:
                    CurrentViewType = VmDetailViewType.StorageSettings;
                    break;
                case VmDetailViewType.BootSettings:
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

        // ----------------------------------------------------------------------------------
        // 视图模型属性 - 创建虚拟机表单
        // ----------------------------------------------------------------------------------

        // 控制右侧界面切换
        [ObservableProperty] private bool _isCreatingVm = false;

        // 当名称变化时，自动更新磁盘路径
        partial void OnNewVmNameChanged(string value)
        {
            // 如果这个变化不是在初始化过程中发生的，则标记为用户手动修改
            if (!IsLoadingSettings)
            {
                _isNameModifiedByUser = true;
            }
            UpdateDiskPath();
        }

        // 当基础路径变化时，自动更新磁盘路径
        partial void OnNewVmStoragePathChanged(string value)
        {
            UpdatePaths();
        }

        private void UpdatePaths()
        {
            if (string.IsNullOrWhiteSpace(NewVmName)) return;

            // 磁盘路径始终跟随：根目录 \ 虚拟机名 \ 虚拟机名.vhdx
            // 这样即使 NewVmStoragePath 是 "C:\Virtual Machines"，
            // 磁盘也会正确放在 "C:\Virtual Machines\test\test.vhdx"
            try
            {
                string basePath = string.IsNullOrWhiteSpace(NewVmStoragePath) ? @"C:\Virtual Machines" : NewVmStoragePath;
                NewVmNewDiskPath = Path.Combine(basePath, NewVmName, $"{NewVmName}.vhdx");
            }
            catch { }
        }


        // 重命名

        // 1. 触发重命名模式
        [RelayCommand]
        private void RenameVm(VmInstanceInfo vm)
        {
            if (vm == null) return;
            vm.StartEditing();
        }

        // 2. 取消重命名
        [RelayCommand]
        private void CancelRename(VmInstanceInfo vm)
        {
            if (vm == null) return;
            vm.IsEditing = false;
        }

        [RelayCommand]
        private async Task CommitRenameAsync(VmInstanceInfo vm)
        {
            if (vm == null || !vm.IsEditing) return;
            vm.IsEditing = false;

            if (string.IsNullOrWhiteSpace(vm.EditedName) || vm.EditedName == vm.Name) return;

            string oldName = vm.Name;
            string newName = vm.EditedName;
            Guid vmId = vm.Id; // 使用唯一 ID

            IsLoading = true;
            try
            {
                // --- 修复点：传入 vmId 而不是 oldName ---
                var result = await _vmEditService.RenameVmAsync(vmId, newName);

                if (result.Success)
                {
                    lock (_renameLockouts)
                    {
                        _renameLockouts[vmId] = (newName, DateTime.Now.AddSeconds(5));
                    }
                    vm.Name = newName;
                }
                else
                {
                    ShowSnackbar(Properties.Resources.VmPage_RenameFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.VmPage_SysExp, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoading = false;
            }
        }


        // --- 1. 常规设置 ---
        [ObservableProperty] private string _newVmName = "NewVM";
        [ObservableProperty] private string _newVmStoragePath = string.Empty;
        [ObservableProperty] private ObservableCollection<string> _supportedVersions = new() { "12.0", "11.0", "10.0", "9.0", "8.0" };
        [ObservableProperty] private string _selectedVersion = "8.0";

        // --- 2. 计算资源 ---
        [ObservableProperty] private string _newVmProcessorCount = "4"; // ComboBox IsEditable="True" 绑定 string
        [ObservableProperty] private string _newVmMemoryMb = "4096";    // ComboBox IsEditable="True" 绑定 string
        [ObservableProperty] private bool _newVmDynamicMemory = false;

        // 安全特性 (仅第 2 代)
        [ObservableProperty] private bool _newVmEnableSecureBoot = true;
        [ObservableProperty] private bool _newVmEnableTpm = true;
        [ObservableProperty] private string _newVmIsolationType = "Disabled"; // Disabled, TrustedLaunch, VBS, SNP, TDX

        // --- 3. 存储资源 ---
        [ObservableProperty] private int _newVmDiskMode = 0; // 0:新建磁盘, 1:现有磁盘, 2:稍后附加
        [ObservableProperty] private string _newVmDiskSizeGb = "128";
        [ObservableProperty] private string _newVmNewDiskPath = string.Empty;      // 模式0使用
        [ObservableProperty] private string _newVmExistingDiskPath = string.Empty; // 模式1使用

        // 安装介质 (ISO)
        [ObservableProperty] private string _newVmIsoPath = string.Empty;

        // --- 4. 网络与全局动作 ---
        [ObservableProperty] private string _newVmSelectedSwitch = string.Empty;
        [ObservableProperty] private bool _startVmAfterCreation = true;

        // 1. 探测结果：系统是否支持
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEnableIsolation))] // 当此值改变，通知 UI 刷新 CanEnableIsolation
        private bool _isIsolationSupported = false;

        // 2. 找到你原有的 NewVmGeneration 属性，添加通知
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEnableIsolation))] // 当代际改变，通知 UI 刷新 CanEnableIsolation
        private int _newVmGeneration = 2;

        // 3. 这是一个只读的计算属性，用于 UI 绑定
        public bool CanEnableIsolation => IsIsolationSupported && NewVmGeneration == 2;

        // 存储探测到的类型列表
        [ObservableProperty]
        private ObservableCollection<string> _supportedIsolationTypes = new() { "Disabled" };
        private bool _isNameModifiedByUser = false;


        // ----------------------------------------------------------------------------------
        // 创建虚拟机模块
        // ----------------------------------------------------------------------------------

        // 1. 点击左侧 "+" 按钮：进入创建模式
        private void UpdateDiskPath()
        {
            if (string.IsNullOrWhiteSpace(NewVmName) || _isDiskPathManual) return; // 如果手动选过，就不再自动更新

            string root = string.IsNullOrWhiteSpace(NewVmStoragePath) ? @"C:\ProgramData\Microsoft\Windows\Hyper-V" : NewVmStoragePath;
            try
            {
                NewVmNewDiskPath = Path.Combine(root, NewVmName, $"{NewVmName}.vhdx");
            }
            catch { }
        }

        [RelayCommand]
        private async Task CreateVm()
        {
            // --- 1. UI 状态与标志位重置 ---
            IsLoadingSettings = true;
            IsCreatingVm = true;
            SelectedVm = null;
            _isNameModifiedByUser = false; // 重置用户手动修改名称的标记

            // --- 2. 基础配置默认值初始化 ---
            NewVmGeneration = 2;
            NewVmMemoryMb = "4096";
            int hostCores = Environment.ProcessorCount;
            NewVmProcessorCount = (hostCores >= 4 ? 4 : hostCores).ToString();

            NewVmDiskMode = 0;
            NewVmDiskSizeGb = "128";
            NewVmDynamicMemory = false;
            NewVmEnableSecureBoot = true;
            NewVmEnableTpm = true;
            StartVmAfterCreation = true;
            NewVmIsoPath = string.Empty;
            NewVmExistingDiskPath = string.Empty;

            try
            {
                // --- 3. 动态探测宿主机默认路径 (核心：拒绝硬编码) ---
                // 调用 Service 通过 (Get-VMHost).VirtualMachinePath 获取真实路径
                var hostPaths = await _vmCreateService.GetHostDefaultPathsAsync();

                // 设置 UI 显示的根路径 (例如 C:\ProgramData\Microsoft\Windows\Hyper-V)
                NewVmStoragePath = hostPaths.DefaultVmPath;

                // --- 4. 初始化名称并触发路径联动 ---
                // 获取当前系统中不冲突的名称 (如 NewVM, NewVM (2))
                NewVmName = GetNextAvailableName("NewVM");

                // 执行路径联动逻辑，确保 NewVmNewDiskPath 此时已经指向：
                // [默认路径]\[NewVM]\[NewVM].vhdx
                UpdateDiskPath();

                // --- 5. 探测系统支持的配置版本 ---
                var allVersions = await _vmCreateService.GetSupportedVersionsAsync();
                SupportedVersions = new ObservableCollection<string>(allVersions);

                // 核心逻辑：在已降序排列的列表中，寻找第一个小于 200 的稳定版本作为默认值
                var defaultStable = allVersions.FirstOrDefault(v =>
                    double.TryParse(v, out double verNum) && verNum < 200);

                // 如果找到稳定版则选中，否则选列表第一个
                SelectedVersion = defaultStable ?? SupportedVersions.FirstOrDefault();

                // --- 6. 探测机密计算 (Isolation) 支持情况 ---
                var (supported, types) = await _vmCreateService.GetIsolationSupportAsync();
                IsIsolationSupported = supported;
                SupportedIsolationTypes = new ObservableCollection<string>(types);

                // 初始状态默认为 Disabled
                NewVmIsolationType = "Disabled";

                // --- 7. 加载虚拟交换机列表 ---
                var switches = await _vmNetworkService.GetAvailableSwitchesAsync();

                // 创建一个临时的列表，第一项放“未连接”
                string noneText = Properties.Resources.none; // “未连接”的文本
                var switchList = new List<string> { noneText };
                if (switches != null) switchList.AddRange(switches);

                AvailableSwitchNames = new ObservableCollection<string>(switchList);

                // --- 改进后的自动选择逻辑 ---

                // 1. 尝试寻找包含 "Default" 的交换机
                var defaultSwitch = AvailableSwitchNames.FirstOrDefault(s =>
                    s.Contains("Default", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains(Properties.Resources.VmPage_Default, StringComparison.OrdinalIgnoreCase));

                if (defaultSwitch != null)
                {
                    NewVmSelectedSwitch = defaultSwitch;
                }
                else
                {
                    // 2. 如果没找到 Default，尝试寻找第一个“非未连接”的真实交换机
                    var firstRealSwitch = AvailableSwitchNames.FirstOrDefault(s => s != noneText);

                    // 3. 如果找到了真实交换机就选它，否则（即列表里只有“未连接”）才选“未连接”
                    NewVmSelectedSwitch = firstRealSwitch ?? noneText;
                }

            }
            catch (Exception ex)
            {
                // 如果报错（比如宿主没装 Hyper-V 网络组件），至少保证有一个“未连接”可选
                AvailableSwitchNames = new ObservableCollection<string> { Properties.Resources.none };
                NewVmSelectedSwitch = AvailableSwitchNames[0];
                Debug.WriteLine($"[CREATE-VM-NET-ERROR] {ex.Message}");
            }

            finally
            {
                // 延迟一小会儿关闭加载状态，确保 UI 绑定完成
                await Task.Delay(100);
                IsLoadingSettings = false;
            }
        }
        // 2. 点击 Properties.Resources.VirtualMachinesPageViewModel_1 按钮：退出创建模式
        [RelayCommand]
        private void CancelCreate()
        {
            IsCreatingVm = false;
            // 恢复选中列表项提升体验
            if (SelectedVm == null && VmList.Count > 0)
            {
                SelectedVm = VmList.First();
            }
        }

        // --- 浏览文件系统相关命令 ---

        [RelayCommand]
        private void BrowseNewVmPath()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Properties.Resources.VmPage_SelectConfigDir,
                InitialDirectory = string.IsNullOrWhiteSpace(NewVmStoragePath) ? string.Empty : NewVmStoragePath
            };
            if (dialog.ShowDialog() == true)
            {
                NewVmStoragePath = dialog.FolderName;
            }
        }


        [RelayCommand]
        private void BrowseNewDiskLocation()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Properties.Resources.VmPage_SelectNewVhdPath,
                Filter = Properties.Resources.VmPage_VhdFilter,
                InitialDirectory = GetDir(NewVmNewDiskPath),
                FileName = GetFileName(NewVmNewDiskPath, $"{NewVmName}.vhdx")
            };
            if (dialog.ShowDialog() == true)
            {
                NewVmNewDiskPath = dialog.FileName;
                _isDiskPathManual = true; // 关键：标记用户已手动选择
            }
        }

        [RelayCommand]
        private void BrowseExistingDisk()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Properties.Resources.VmPage_SelectExistVhd,
                Filter = Properties.Resources.VmPage_VhdFilterBoth,
                InitialDirectory = GetDir(NewVmExistingDiskPath)
            };
            if (dialog.ShowDialog() == true) NewVmExistingDiskPath = dialog.FileName;
        }

        [RelayCommand]
        private void BrowseIsoImage()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Properties.Resources.VmPage_SelectIso,
                Filter = Properties.Resources.VmPage_IsoFilter,
                InitialDirectory = GetDir(NewVmIsoPath)
            };
            if (dialog.ShowDialog() == true) NewVmIsoPath = dialog.FileName;
        }
        
        [RelayCommand]
        private async Task ConfirmCreate()
        {
            // --- 1. 基础验证：名称 ---
            if (string.IsNullOrWhiteSpace(NewVmName))
            {
                ShowSnackbar(Properties.Resources.VmPage_CreateFail, Properties.Resources.VmPage_NameEmpty, ControlAppearance.Caution, SymbolRegular.Warning24);
                return;
            }

            // --- 2. 存储路径验证 ---
            string rootPath = string.IsNullOrWhiteSpace(NewVmStoragePath) ? @"C:\Virtual Machines" : NewVmStoragePath;
            string targetVmDir = Path.Combine(rootPath, NewVmName);

            // 检查：如果目标文件夹已存在且里面有文件，弹出警告（防止覆盖或混乱）
            if (Directory.Exists(targetVmDir))
            {
                if (Directory.EnumerateFileSystemEntries(targetVmDir).Any())
                {
                    // 这里可以根据需求决定是阻断还是仅提示。通常建议阻断以防止文件冲突。
                    ShowSnackbar(Properties.Resources.VmPage_CreateWarn, Properties.Resources.VmPage_TargetExist, ControlAppearance.Caution, SymbolRegular.Warning24);
                    return;
                }
            }

            // --- 3. 磁盘模式深度验证 ---
            if (NewVmDiskMode == 0) // 新建磁盘
            {
                if (string.IsNullOrWhiteSpace(NewVmNewDiskPath))
                {
                    ShowSnackbar(Properties.Resources.VmPage_CreateFail, Properties.Resources.VmPage_SelectVhdSave, ControlAppearance.Caution, SymbolRegular.Warning24);
                    return;
                }
            }
            else if (NewVmDiskMode == 1) // 现有磁盘 (修复点)
            {
                if (string.IsNullOrWhiteSpace(NewVmExistingDiskPath))
                {
                    ShowSnackbar(Properties.Resources.VmPage_CreateFail, Properties.Resources.VmPage_SelectExistVhdPath, ControlAppearance.Caution, SymbolRegular.Warning24);
                    return;
                }

                if (!File.Exists(NewVmExistingDiskPath))
                {
                    ShowSnackbar(Properties.Resources.VmPage_CreateFail, Properties.Resources.VmPage_ExistVhdNotFound, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    return;
                }
            }

            // --- 4. ISO 镜像验证 (如果有输入) ---
            if (!string.IsNullOrWhiteSpace(NewVmIsoPath) && !File.Exists(NewVmIsoPath))
            {
                ShowSnackbar(Properties.Resources.VmPage_CreateFail, Properties.Resources.VmPage_IsoNotFound, ControlAppearance.Caution, SymbolRegular.Warning24);
                return;
            }
            // --- 2. 组装专用 Model ---
            var request = new VmCreationParams
            {
                Name = NewVmName,
                IsManualName = _isNameModifiedByUser, // 告诉 Service 是否要加后缀
                Path = NewVmStoragePath,
                Version = SelectedVersion,
                Generation = NewVmGeneration,

                // 解析 UI 字符串 (ComboBox IsEditable=True)
                ProcessorCount = int.TryParse(NewVmProcessorCount, out var cpu) ? cpu : 4,
                MemoryMb = long.TryParse(NewVmMemoryMb, out var mem) ? mem : 4096,
                EnableDynamicMemory = NewVmDynamicMemory,

                // 安全设置
                EnableSecureBoot = NewVmEnableSecureBoot,
                EnableTpm = NewVmEnableTpm,
                IsolationType = NewVmIsolationType,

                // 存储设置
                DiskMode = NewVmDiskMode,
                DiskSizeGb = long.TryParse(NewVmDiskSizeGb, out var ds) ? ds : 128,
                VhdPath = NewVmDiskMode == 0 ? NewVmNewDiskPath : NewVmExistingDiskPath,
                IsoPath = NewVmIsoPath,

                // 网络与动作
                SwitchName = NewVmSelectedSwitch,
                StartAfterCreation = StartVmAfterCreation
            };

            // --- 3. 执行创建流程 ---
            IsLoading = true; // 显示全屏遮罩
            try
            {
                var result = await _vmCreateService.CreateVirtualMachineAsync(request);

                if (result.Success)
                {
                    string actualCreatedName = result.Message;
                    ShowSnackbar(
                         Properties.Resources.VmPage_CreateSuccess,
                         string.Format(Properties.Resources.VmPage_VmCreated, actualCreatedName), // 使用真实名称
                         ControlAppearance.Success,
                         SymbolRegular.CheckmarkCircle24);
                    // 退出创建模式
                    IsCreatingVm = false;

                    // 重新加载列表以显示新虚拟机
                    await LoadVmsCommand.ExecuteAsync(null);

                    // 尝试选中新创建的虚拟机
                    var newVm = VmList.FirstOrDefault(v => v.Name.Equals(actualCreatedName, StringComparison.OrdinalIgnoreCase));
                    if (newVm != null) SelectedVm = newVm;
                }
                else
                {
                    ShowSnackbar(Properties.Resources.VmPage_CreateFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.VmPage_SysExp, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoading = false;
            }
        }
        // --- 辅助私有方法 ---

        private string GetNextAvailableName(string baseName)
        {
            if (!VmList.Any(v => v.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)))
                return baseName;

            int i = 2;
            while (VmList.Any(v => v.Name.Equals($"{baseName} ({i})", StringComparison.OrdinalIgnoreCase)))
            {
                i++;
            }
            return $"{baseName} ({i})";
        }



        // ----------------------------------------------------------------------------------
        // 虚拟机列表管理与核心操作
        // ----------------------------------------------------------------------------------

        [RelayCommand]
        private async Task DeleteVmAsync(VmInstanceInfo vm)
        {
            if (vm == null) return;
            IsLoading = true;

            try
            {
                await Task.Run(() =>
                {
                    // 组合命令：
                    // 1. Stop-VM -TurnOff: 强制关闭电源（不管当前什么状态，报错也继续执行下一步）
                    // 2. Remove-VM -Force: 删除配置
                    string script = $"Stop-VM -Name '{vm.Name}' -TurnOff -ErrorAction SilentlyContinue; Remove-VM -Name '{vm.Name}' -Force";
                    Utils.Run(script);
                });

                VmList.Remove(vm);
                if (SelectedVm == vm) SelectedVm = VmList.FirstOrDefault();
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.VmPage_DeleteFail, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally { IsLoading = false; }
        }
        // 当选中的虚拟机发生变化时重置视图
        partial void OnSelectedVmChanged(VmInstanceInfo value)
        {
            if (value != null)
            {
                IsCreatingVm = false;
                _ = RefreshBootOrderForSelectedVmAsync(value);
            }
            CurrentViewType = VmDetailViewType.Dashboard;
            _originalSettingsCache = null;
            HostDisks.Clear();
        }

        private async Task RefreshBootOrderForSelectedVmAsync(VmInstanceInfo vm)
        {
            if (vm == null) return;
            try
            {
                var list = await _vmBootService.GetBootOrderAsync(vm.Name);

                Application.Current.Dispatcher.Invoke(() => {
                    vm.BootOrderItems.Clear();
                    foreach (var item in list)
                    {
                        vm.BootOrderItems.Add(item);
                    }
                    if (vm.BootOrderItems.Count > 0)
                    {
                        vm.BootOrderItems.Last().IsLast = true;
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BOOT-REFRESH-ERROR] {ex.Message}");
            }
        }

        private VmInstanceInfo CreateVmInstance(ExHyperV.Models.VmInstanceInfo vm)
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
            if (vm.NetworkAdapters != null)
            {
                foreach (var net in vm.NetworkAdapters) instance.NetworkAdapters.Add(net);
            }

            instance.SyncBackendData(vm.State, vm.RawUptime);
            instance.IpAddress = vm.IpAddress;

            // 绑定电源控制命令 (必须绑定，否则新发现的 VM 按钮无效)
            instance.ControlCommand = new AsyncRelayCommand<string>(async (action) => {
                instance.SetTransientState(GetOptimisticText(action));
                try
                {
                    await _powerService.ExecuteControlActionAsync(instance.Name, action);
                    await SyncSingleVmStateAsync(instance);
                    if (action == "Start" || action == "Restart")
                    {
                        TryApplyAffinityForRootScheduler(instance);
                    }
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() => instance.ClearTransientState());
                    var realEx = ex;
                    while (realEx.InnerException != null) { realEx = realEx.InnerException; }
                    ShowSnackbar(Properties.Resources.Error_Common_OpFail, Utils.GetFriendlyErrorMessages(realEx.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            });

            return instance;
        }

        public List<string> AvailableOsTypes => Utils.SupportedOsTypes;

        // 加载虚拟机列表
        [RelayCommand]
        private async Task LoadVmsAsync()
        {
            if (IsLoading && VmList.Count > 0) return;
            IsLoading = true;
            try
            {
                var finalCollection = await Task.Run(async () => {
                    var vms = await _queryService.GetVmListAsync();
                    var list = new ObservableCollection<VmInstanceInfo>();
                    foreach (var vm in vms)
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

                        // 绑定电源控制命令
                        instance.ControlCommand = new AsyncRelayCommand<string>(async (action) => {
                            instance.SetTransientState(GetOptimisticText(action));
                            try
                            {
                                await _powerService.ExecuteControlActionAsync(instance.Name, action);
                                await SyncSingleVmStateAsync(instance);
                                if (action == "Start" || action == "Restart")
                                {
                                    TryApplyAffinityForRootScheduler(instance);
                                }
                            }
                            catch (Exception ex)
                            {
                                Application.Current.Dispatcher.Invoke(() => instance.ClearTransientState());
                                var realEx = ex;
                                while (realEx.InnerException != null) { realEx = realEx.InnerException; }
                                ShowSnackbar(Properties.Resources.Error_Common_OpFail, Utils.GetFriendlyErrorMessages(realEx.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                            }
                        });

                        if (vm.NetworkAdapters != null)
                        {
                            foreach (var net in vm.NetworkAdapters) instance.NetworkAdapters.Add(net);
                        }

                        instance.IpAddress = vm.IpAddress;
                        list.Add(instance);
                    }
                    return list;
                });

                VmList = finalCollection;

                foreach (var vm in VmList.Where(v => v.IsRunning))
                {
                    TryApplyAffinityForRootScheduler(vm);
                }

                // 配置排序规则
                var view = CollectionViewSource.GetDefaultView(VmList);
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(nameof(VmInstanceInfo.IsRunning), ListSortDirection.Descending));
                view.SortDescriptions.Add(new SortDescription(nameof(VmInstanceInfo.Name), ListSortDirection.Ascending));

                // 开启实时排序
                if (view is System.ComponentModel.ICollectionViewLiveShaping liveView)
                {
                    liveView.IsLiveSorting = true;
                    liveView.LiveSortingProperties.Add(nameof(VmInstanceInfo.IsRunning));
                }

                if (SelectedVm == null || !VmList.Any(x => x.Name == SelectedVm.Name))
                {
                    SelectedVm = VmList.FirstOrDefault();
                }

                StartMonitoring();
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Common_LoadFail, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoading = false;
            }
            if (VmList.Count == 0)
            {
                SelectedVm = null;
            }
        }






        // 修改原本启动外部 vmconnect.exe 的逻辑
        [RelayCommand]
        private void OpenNativeConnect()
        {
            if (SelectedVm == null) return;

            try
            {
                // 1. 实例化我们自己的沉浸式控制台窗口
                // 传入当前选中虚拟机的 GUID (Id.ToString()) 和 名称
                var consoleWin = new ExHyperV.Views.ConsoleWindow(
                    SelectedVm.Id.ToString(),
                    SelectedVm.Name
                );

                // 2. 设置所有者为当前主窗口（这样主窗口关闭时，控制台也会跟着关，且弹出位置更准确）
                //consoleWin.Owner = Application.Current.MainWindow;

                // 3. 显示窗口
                consoleWin.Show();

                // 4. (可选) 给个小反馈
                Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_3, SelectedVm.Name));
            }
            catch (Exception ex)
            {
                ShowSnackbar(
                    Properties.Resources.Error_Vm_StartFail,
                    string.Format(Properties.Resources.VirtualMachinesPageViewModel_4, ex.Message),
                    ControlAppearance.Danger,
                    SymbolRegular.ErrorCircle24);
            }
        }

        // 修改操作系统标签
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
                ShowSnackbar(Properties.Resources.Error_Common_ModFailShort, Properties.Resources.Error_Common_NoPermission, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }

        // ----------------------------------------------------------------------------------
        // 后台监控循环与状态更新
        // ----------------------------------------------------------------------------------

        // 启动后台监控线程
        private void StartMonitoring()
        {
            if (_monitoringCts != null) return;
            _monitoringCts = new CancellationTokenSource();
            _cpuTask = Task.Run(() => MonitorCpuLoop(_monitoringCts.Token));
            _stateTask = Task.Run(() => MonitorStateLoop(_monitoringCts.Token));
            // 新增：独立的缩略图任务，避免阻塞状态同步
            _ = Task.Run(() => MonitorThumbnailLoop(_monitoringCts.Token));
        }

        // CPU 使用率监控循环
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

        // 虚拟机状态与性能数据同步循环
        private async Task MonitorStateLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 1. 获取后端最新原始数据
                    var updates = await _queryService.GetVmListAsync();
                    var memoryMap = await _queryService.GetVmRuntimeMemoryDataAsync();

                    await _queryService.UpdateDiskPerformanceAsync(VmList);
                    var gpuUsageMap = await _queryService.GetGpuPerformanceAsync(VmList);

                    Application.Current.Dispatcher.Invoke(() => {
                        bool needsResort = false;

                        // --- A. 监测删除：移除本地列表中 已经不存在于后端 的 VM ---
                        var updateIds = updates.Select(u => u.Id).ToHashSet();
                        for (int i = VmList.Count - 1; i >= 0; i--)
                        {
                            if (!updateIds.Contains(VmList[i].Id))
                            {
                                if (SelectedVm == VmList[i]) SelectedVm = null;
                                VmList.RemoveAt(i);
                                needsResort = true;
                            }
                        }

                        // --- B. 监测新建：添加后端存在但 本地列表没有 的 VM ---
                        var currentIds = VmList.Select(v => v.Id).ToHashSet();
                        foreach (var update in updates)
                        {
                            if (!currentIds.Contains(update.Id))
                            {
                                var newVm = CreateVmInstance(update);
                                VmList.Add(newVm);
                                needsResort = true;
                            }
                        }

                        // --- C. 更新属性：原有逻辑 ---
                        foreach (var update in updates)
                        {
                            // 使用 Id 匹配比 Name 更可靠，因为 VM 可能会被改名
                            var vm = VmList.FirstOrDefault(v => v.Id == update.Id);
                            if (vm != null)
                            {
                                // --- [新增] 重命名锁定保护拦截逻辑 ---
                                bool skipNameUpdate = false;
                                lock (_renameLockouts)
                                {
                                    if (_renameLockouts.TryGetValue(vm.Id, out var lockout))
                                    {
                                        // 检查：1. 后端数据是否已经同步为新名字？ 2. 是否已经超过了 5 秒保护期？
                                        if (update.Name.Equals(lockout.NewName, StringComparison.OrdinalIgnoreCase) ||
                                            DateTime.Now > lockout.Expiry)
                                        {
                                            // 满足上述任一条件，解除锁定
                                            _renameLockouts.Remove(vm.Id);
                                        }
                                        else
                                        {
                                            // 后端传回的依然是旧名字且在保护期内，拦截本次更新
                                            skipNameUpdate = true;
                                        }
                                    }
                                }

                                // 1. 如果名字变了且没有被锁定，更新名字
                                if (!skipNameUpdate)
                                {
                                    if (vm.Name != update.Name) vm.Name = update.Name;
                                }

                                // 2. 同步配置信息 (CPU 和 内存) - 这些不受名字锁定影响，正常同步
                                if (vm.CpuCount != update.CpuCount)
                                    vm.CpuCount = update.CpuCount;

                                if (vm.MemoryGb != update.MemoryGb)
                                    vm.MemoryGb = update.MemoryGb;

                                // 如果你的 ConfigSummary 还依赖代数或版本，建议也同步一下
                                if (vm.Generation != update.Generation)
                                    vm.Generation = update.Generation;

                                if (vm.Version != update.Version)
                                    vm.Version = update.Version;

                                bool wasRunning = vm.IsRunning;
                                vm.Notes = update.Notes;

                                vm.SyncBackendData(update.State, update.RawUptime);

                                // 如果状态从关机变开机（或反之），需要重新排序
                                if (wasRunning != vm.IsRunning) needsResort = true;

                                if (CurrentViewType != VmDetailViewType.NetworkSettings && !IsLoadingSettings)
                                {
                                    SyncNetworkAdaptersInternal(vm.NetworkAdapters, update.NetworkAdapters.ToList());
                                }

                                if (vm.IsRunning)
                                {
                                    var allIps = vm.NetworkAdapters.SelectMany(a => a.IpAddresses ?? new List<string>())
                                                   .Where(ip => !string.IsNullOrEmpty(ip) && !ip.Contains(":"))
                                                   .ToList();
                                    if (allIps.Count > 0) vm.IpAddress = allIps.First();

                                    foreach (var adapter in vm.NetworkAdapters)
                                    {
                                        if (!string.IsNullOrEmpty(adapter.MacAddress) && (adapter.IpAddresses == null || adapter.IpAddresses.Count == 0))
                                        {
                                            _ = Task.Run(async () => {
                                                try
                                                {
                                                    string arpIp = await Utils.GetVmIpAddressAsync(vm.Name, adapter.MacAddress);
                                                    if (!string.IsNullOrEmpty(arpIp))
                                                        Application.Current.Dispatcher.Invoke(() => {
                                                            adapter.IpAddresses = new List<string> { arpIp };
                                                            if (vm.IpAddress == "---" || string.IsNullOrWhiteSpace(vm.IpAddress)) vm.IpAddress = arpIp;
                                                        });
                                                }
                                                catch { }
                                            });
                                        }
                                    }
                                }
                                else
                                {
                                    vm.IpAddress = "---";
                                }

                                // --- 磁盘同步逻辑 ---
                                var updatePaths = update.Disks.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

                                for (int i = vm.Disks.Count - 1; i >= 0; i--)
                                {
                                    if (!updatePaths.Contains(vm.Disks[i].Path))
                                        vm.Disks.RemoveAt(i);
                                }

                                foreach (var newDiskData in update.Disks)
                                {
                                    var existingDisk = vm.Disks.FirstOrDefault(d => d.Path.Equals(newDiskData.Path, StringComparison.OrdinalIgnoreCase));
                                    if (existingDisk != null)
                                    {
                                        existingDisk.Name = newDiskData.Name;
                                        existingDisk.MaxSize = newDiskData.MaxSize;
                                        existingDisk.DiskType = newDiskData.DiskType;

                                        if (vm.IsRunning && existingDisk.DiskType != "Physical" && File.Exists(existingDisk.Path))
                                        {
                                            try
                                            {
                                                long realSizeBytes = new FileInfo(existingDisk.Path).Length;
                                                if (existingDisk.CurrentSize != realSizeBytes)
                                                    existingDisk.CurrentSize = realSizeBytes;
                                            }
                                            catch { }
                                        }
                                        else
                                        {
                                            existingDisk.CurrentSize = newDiskData.CurrentSize;
                                        }
                                    }
                                    else
                                    {
                                        vm.Disks.Add(newDiskData);
                                    }
                                }

                                vm.GpuName = update.GpuName;

                                if (memoryMap.TryGetValue(vm.Id.ToString(), out var memData))
                                    vm.UpdateMemoryStatus(memData.AssignedMb, memData.AvailablePercent);
                                else if (memoryMap.TryGetValue(vm.Id.ToString().ToUpper(), out var memDataUpper))
                                    vm.UpdateMemoryStatus(memDataUpper.AssignedMb, memDataUpper.AvailablePercent);
                                else
                                    vm.UpdateMemoryStatus(0, 0);
                            }
                        }
                        foreach (var vm in VmList)
                        {
                            if (gpuUsageMap.TryGetValue(vm.Id, out var gpuData))
                                vm.UpdateGpuStats(gpuData);
                            else
                                vm.UpdateGpuStats(new VmQueryService.GpuUsageData());
                        }

                        if (needsResort)
                        {
                            CollectionViewSource.GetDefaultView(VmList)?.Refresh();
                        }
                    });

                    if (SelectedVm != null && SelectedVm.IsRunning)
                    {
                        await _storageService.RefreshVirtualDiskSizesAsync(SelectedVm);
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
        }        // 同步单个虚拟机的最新状态
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

        // 处理 CPU 更新数据
        private void ProcessAndApplyCpuUpdates(List<CpuCoreMetric> rawData) { var grouped = rawData.GroupBy(x => x.VmName); foreach (var group in grouped) { var vm = VmList.FirstOrDefault(v => v.Name == group.Key); if (vm == null) continue; vm.AverageUsage = vm.IsRunning ? group.Average(x => x.Usage) : 0; UpdateVmCores(vm, group.ToList()); } }
        private void UpdateVmCores(VmInstanceInfo vm, List<CpuCoreMetric> metrics) { var metricIds = metrics.Select(m => m.CoreId).ToHashSet(); vm.Cores.Where(c => !metricIds.Contains(c.CoreId)).ToList().ForEach(r => vm.Cores.Remove(r)); foreach (var metric in metrics) { var core = vm.Cores.FirstOrDefault(c => c.CoreId == metric.CoreId); if (core == null) { core = new VmCoreModel { CoreId = metric.CoreId }; int idx = 0; while (idx < vm.Cores.Count && vm.Cores[idx].CoreId < metric.CoreId) idx++; vm.Cores.Insert(idx, core); } core.Usage = metric.Usage; UpdateHistory(vm.Name, core); } vm.Columns = LayoutHelper.CalculateOptimalColumns(vm.Cores.Count); vm.Rows = (vm.Cores.Count > 0) ? (int)Math.Ceiling((double)vm.Cores.Count / vm.Columns) : 1; }
        private void UpdateHistory(string vmName, VmCoreModel core) { string key = $"{vmName}_{core.CoreId}"; if (!_historyCache.TryGetValue(key, out var history)) { history = new LinkedList<double>(); for (int k = 0; k < MaxHistoryLength; k++) history.AddLast(0); _historyCache[key] = history; } history.AddLast(core.Usage); if (history.Count > MaxHistoryLength) history.RemoveFirst(); core.HistoryPoints = CalculatePoints(history); }
        private PointCollection CalculatePoints(LinkedList<double> history) { double w = 100.0, h = 100.0, step = w / (MaxHistoryLength - 1); var points = new PointCollection(MaxHistoryLength + 2) { new Point(0, h) }; int i = 0; foreach (var val in history) points.Add(new Point(i++ * step, h - (val * h / 100.0))); points.Add(new Point(w, h)); points.Freeze(); return points; }

        // ----------------------------------------------------------------------------------
        // CPU 设置与亲和性模块
        // ----------------------------------------------------------------------------------

        // 初始化可能的 vCPU 数量选项
        private void InitPossibleCpuCounts()
        {
            var options = new HashSet<int>();
            int maxCores = Environment.ProcessorCount;
            int current = 1;
            while (current <= maxCores) { options.Add(current); current *= 2; }
            options.Add(maxCores);
            PossibleVCpuCounts = new ObservableCollection<int>(options.OrderBy(x => x));
        }

        // 导航至 CPU 设置页面
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
            catch (Exception ex) { ShowSnackbar(Properties.Resources.Error_Common_LoadFail, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally
            {
                await Task.Delay(200);
                IsLoadingSettings = false;
            }
        }

        // 应用 CPU 设置更改
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
                    ShowSnackbar(Properties.Resources.Error_Common_ApplyFail, Utils.GetFriendlyErrorMessages(result.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    await GoToCpuSettings();
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Common_SysException, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                await GoToCpuSettings();
            }
            finally { IsLoadingSettings = false; }
        }

        // 导航至 CPU 亲和性页面
        [RelayCommand]
        private async Task GoToCpuAffinity()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.CpuAffinity;
            IsLoadingSettings = true;

            try
            {
                int totalCores = Environment.ProcessorCount;
                var currentAffinity = await _cpuAffinityService.GetCpuAffinityAsync(SelectedVm.Id, SelectedVm.Notes);

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
                ShowSnackbar(Properties.Resources.Error_Cpu_AffinityFail, Utils.GetFriendlyErrorMessages(ex.Message),
                    ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 保存亲和性设置
        [RelayCommand]
        private async Task SaveAffinity()
        {
            if (SelectedVm == null || AffinityHostCores == null) return;
            IsLoadingSettings = true;
            try
            {
                // 1. 获取用户选中的核心索引列表
                var selectedIndices = AffinityHostCores.Where(c => c.IsSelected).Select(c => c.CoreId).ToList();

                // 2. 调用服务应用设置 (内部会自动判断调度器类型)
                bool success = await _cpuAffinityService.SetCpuAffinityAsync(SelectedVm.Id, selectedIndices, SelectedVm.IsRunning);

                // 3. 无论当前是否应用成功，我们将配置持久化到 Notes
                string affinityStr = selectedIndices.Count > 0 ? string.Join(",", selectedIndices) : "";
                SelectedVm.Notes = Utils.UpdateTagValue(SelectedVm.Notes, "Affinity", affinityStr);

                await _queryService.SetVmNotesAsync(SelectedVm.Name, SelectedVm.Notes);

                if (success)
                {
                    ShowSnackbar(Properties.Resources.Msg_Common_SaveSuccess, Properties.Resources.Msg_Cpu_AffinityApplied, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    await GoToCpuSettings();
                }
                else
                {
                    // 如果是因为 Root 模式未开机导致无法实时应用
                    var scheduler = HyperVSchedulerService.GetSchedulerType();
                    if (scheduler == HyperVSchedulerType.Root && !SelectedVm.IsRunning)
                    {
                        ShowSnackbar(Properties.Resources.Msg_Cpu_AffinityQueued, Properties.Resources.Msg_Cpu_RootNotice, ControlAppearance.Info, SymbolRegular.Clock24);
                        await GoToCpuSettings();
                    }
                    else
                    {
                        ShowSnackbar(Properties.Resources.Error_Common_SaveFail, Properties.Resources.Error_Cpu_ApplyFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Common_Error, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 自动应用亲和性

        private void TryApplyAffinityForRootScheduler(VmInstanceInfo vm)
        {
            // 仅针对 Root 调度器且虚拟机正在运行的情况
            if (HyperVSchedulerService.GetSchedulerType() != HyperVSchedulerType.Root || !vm.IsRunning)
                return;

            string savedAffinity = Utils.GetTagValue(vm.Notes, "Affinity");
            if (string.IsNullOrEmpty(savedAffinity))
                return;

            // 异步执行，避免阻塞 UI
            _ = Task.Run(async () =>
            {
                try
                {
                    var coreIds = savedAffinity.Split(',')
                                             .Select(s => int.Parse(s.Trim()))
                                             .ToList();

                    // 尝试多次，因为 vmmem 进程可能启动较慢，或者为了确保应用成功
                    // 如果是软件刚启动检测到虚拟机已运行，通常一次就能成功，但保留重试机制更稳健
                    for (int i = 0; i < 5; i++)
                    {
                        // 如果是刚启动 VM，进程可能还没出来，等待一下；如果是已运行，这个等待不影响
                        if (i == 0) await Task.Delay(1000);
                        else await Task.Delay(2000);

                        // 再次检查是否还在运行，防止中途关机
                        if (!vm.IsRunning) break;

                        // 调用核心方法
                        bool success = ProcessAffinityManager.SetVmProcessAffinity(vm.Id, coreIds);
                        if (success)
                        {
                            Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_5, vm.Name));
                            break;
                        }
                        Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_6, i + 1, vm.Name));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_7, ex.Message));
                }
            });
        }

        // ----------------------------------------------------------------------------------
        // 内存设置模块
        // ----------------------------------------------------------------------------------

        // 导航至内存设置
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
            catch (Exception ex) { ShowSnackbar(Properties.Resources.Common_Error, string.Format(Properties.Resources.Error_Format_LoadFail, Utils.GetFriendlyErrorMessages(ex.Message)), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally
            {
                await Task.Delay(200);
                IsLoadingSettings = false;
            }
        }

        // 监听内存属性变更以实现部分自动应用
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
                    var result = await _vmMemoryService.SetVmMemorySettingsAsync(
    SelectedVm.Name,
    SelectedVm.MemorySettings,
    SelectedVm.IsRunning // 传入当前运行状态
);
                    if (!result.Success)
                    {
                        ShowSnackbar(Properties.Resources.Error_Memory_AutoApply, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
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

        // 手动应用内存设置
        [RelayCommand]
        private async Task ApplyMemorySettings()
        {
            if (SelectedVm?.MemorySettings == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await _vmMemoryService.SetVmMemorySettingsAsync(
                    SelectedVm.Name,
                    SelectedVm.MemorySettings,
                    SelectedVm.IsRunning // 传入当前运行状态
                ); if (!result.Success) ShowSnackbar(Properties.Resources.Error_Common_SaveFail, Utils.GetFriendlyErrorMessages(result.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                await GoToMemorySettings();
            }
            catch (Exception ex) { ShowSnackbar(Properties.Resources.Common_ExceptionLabel, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoadingSettings = false; }
        }

        // ----------------------------------------------------------------------------------
        // 存储管理模块 - 列表与基础操作
        // ----------------------------------------------------------------------------------

        // 导航至存储设置页面
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
                catch (Exception ex) { ShowSnackbar(Properties.Resources.Error_Storage_LoadFail, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
                finally { IsLoadingSettings = false; }
            }
        }

        // 加载宿主机物理磁盘列表
        private async Task LoadHostDisksAsync()
        {
            try
            {
                var disks = await _storageService.GetHostDisksAsync();
                Application.Current.Dispatcher.Invoke(() => HostDisks = new ObservableCollection<HostDiskInfo>(disks));
            }
            catch { }
        }


        // 优化磁盘
        [RelayCommand]
        private async Task OptimizeStorage(VmStorageItem item)
        {
            // 空值、正在运行、或已经在优化中的磁盘不处理
            if (item == null || SelectedVm == null || SelectedVm.IsRunning || item.IsOptimizing) return;

            // 进入优化状态
            item.IsOptimizing = true;

            try
            {
                // 1. 发起 WMI 压缩指令
                // 虽然 await 会等待，但由于 vmms.exe 承载了 Job，即便 UI 崩溃，任务依然在后台跑
                var result = await _storageService.CompactDiskAsync(item.PathOrDiskNumber);

                if (result.Success)
                {
                    // 2. 刷新磁盘物理大小 (FileSize)
                    // 调用现有的存储服务，确保 UI 上的 GB 数值得到更新
                    await _storageService.RefreshVirtualDiskSizesAsync(SelectedVm);

                    ShowSnackbar("优化完成", "虚拟磁盘空间已成功回收", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                }
                else
                {
                    ShowSnackbar("优化失败", result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar("系统异常", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                // 3. 释放优化状态
                item.IsOptimizing = false;
            }
        }

        // 移除存储设备
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
                    ShowSnackbar(Properties.Resources.Common_Success, result.Message == "Storage_Msg_Ejected" ? Properties.Resources.Msg_Storage_Ejected : Properties.Resources.Msg_Storage_Removed, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    await _storageService.LoadVmStorageItemsAsync(SelectedVm);
                }
                else ShowSnackbar(Properties.Resources.Error_Storage_RemoveFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            catch (Exception ex) { ShowSnackbar(Properties.Resources.Common_Error, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
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
                        ShowSnackbar(Properties.Resources.Msg_Common_ModSuccess, Properties.Resources.Msg_Storage_PathUpdated, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                        await _storageService.LoadVmStorageItemsAsync(SelectedVm);
                    }
                    else
                    {
                        ShowSnackbar(Properties.Resources.Error_Common_ModFailShort, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    }
                }
                catch (Exception ex)
                {
                    ShowSnackbar(Properties.Resources.Common_Error, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
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

        // ----------------------------------------------------------------------------------
        // 存储管理模块 - 添加设备向导
        // ----------------------------------------------------------------------------------

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

            Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_8, value));
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

            Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_9, value));
            UpdateAvailableLocations();
        }

        // 增加位置变更监听（用于观察是否有 UI 回传 null/默认值的情况）
        partial void OnSelectedLocationChanged(int value)
        {
            Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_10, value));
        }


        // 导航至添加存储向导
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

        // 确认添加存储设备
        [RelayCommand]
        private async Task ConfirmAddStorage()
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

                Debug.WriteLine($"[STORAGE-ACTION] 执行添加操作: {driveType} -> 控制器:{targetType} #{targetNumber} 位置:{targetLocation}");

                if (isPhysical && int.TryParse(pathOrNumber, out int diskNum))
                    await _storageService.SetDiskOfflineStatusAsync(diskNum, true);

                var result = await _storageService.AddDriveAsync(
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
                    await _storageService.LoadVmStorageItemsAsync(SelectedVm);
                }
                else
                {
                    ShowSnackbar(Properties.Resources.Error_Storage_AddFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Common_ExceptionLabel, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
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
            Debug.WriteLine($"[DEBUG-STORAGE] >>> 开始自动分配: {type} #{ctrlNum} Loc:{loc}");

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
                    Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_13, SelectedControllerNumber));

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
                    Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_14, SelectedLocation));

                    IsSlotValid = true;
                    SlotWarningMessage = string.Empty;

                    // 全部完成后解锁
                    _isInternalUpdating = false;
                    Debug.WriteLine(Properties.Resources.VirtualMachinesPageViewModel_15);

                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                _isInternalUpdating = false;
                Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_16, ex.Message));
            }
        }

        private void RefreshAvailableNumbers(string type)
        {
            Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_17, type));
            AvailableControllerNumbers.Clear();
            int maxCtrl = (type == "IDE") ? 2 : 4;
            for (int i = 0; i < maxCtrl; i++)
                AvailableControllerNumbers.Add(i);
        }

        private void RefreshAvailableLocations(string type, int ctrlNum)
        {
            if (SelectedVm == null || type == null) return;

            Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_18, type, ctrlNum));
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
                Debug.WriteLine(string.Format(Properties.Resources.VirtualMachinesPageViewModel_19, SelectedLocation));
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
        // ----------------------------------------------------------------------------------
        // 网络设置模块
        // ----------------------------------------------------------------------------------


        // ----------------------------------------------------------------------------------
        // 网络模式映射选项 (用于翻译)
        // ----------------------------------------------------------------------------------

        // 1. VLAN 主模式映射
        public List<object> VlanModeOptions { get; } = new()
{
    new { Value = VlanOperationMode.Access, Name = Properties.Resources.Net_Mode_Access },
    new { Value = VlanOperationMode.Trunk, Name = Properties.Resources.Net_Mode_Trunk },
    new { Value = VlanOperationMode.Private, Name = Properties.Resources.Net_Mode_Private }
};

        // 2. Private VLAN 类型 (角色) 映射
        public List<object> PvlanModeOptions { get; } = new()
{
    new { Value = PvlanMode.Isolated, Name = Properties.Resources.Net_Pvlan_Isolated },
    new { Value = PvlanMode.Community, Name = Properties.Resources.Net_Pvlan_Community },
    new { Value = PvlanMode.Promiscuous, Name = Properties.Resources.Net_Pvlan_Promiscuous }
};

        // 3. 端口镜像模式映射
        public List<object> PortMirroringOptions { get; } = new()
{
    new { Value = PortMonitorMode.None, Name = Properties.Resources.Common_Disabled },
    new { Value = PortMonitorMode.Source, Name = Properties.Resources.Net_Mirror_Source },
    new { Value = PortMonitorMode.Destination, Name = Properties.Resources.Net_Mirror_Dest }
};

        // 导航至网络设置
        [RelayCommand]
        private async Task GoToNetworkSettings()
        {
            if (SelectedVm == null) return;

            CurrentViewType = VmDetailViewType.NetworkSettings;
            IsLoadingSettings = true;

            try
            {
                var switchesTask = _vmNetworkService.GetAvailableSwitchesAsync();
                var adaptersTask = _vmNetworkService.GetNetworkAdaptersAsync(SelectedVm.Name);

                await Task.WhenAll(switchesTask, adaptersTask);

                if (!AvailableSwitchNames.SequenceEqual(switchesTask.Result))
                {
                    AvailableSwitchNames = new ObservableCollection<string>(switchesTask.Result);
                }

                var firstAdapter = adaptersTask.Result.FirstOrDefault();
                System.Diagnostics.Debug.WriteLine($"[DEBUG] GoToNetworkSettings is syncing. IsConnected = {firstAdapter?.IsConnected}");
                SyncNetworkAdaptersInternal(SelectedVm.NetworkAdapters, adaptersTask.Result);

                // IP 探测
                if (SelectedVm.IsRunning)
                {
                    _ = Task.Run(async () => {
                        await _vmNetworkService.FillDynamicIpsAsync(SelectedVm.Name, SelectedVm.NetworkAdapters);
                    });
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Common_LoadFail, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                await Task.Delay(300);
                IsLoadingSettings = false;
            }
        }

        // 智能同步网卡列表，避免 UI 闪烁
        private void SyncNetworkAdaptersInternal(ObservableCollection<VmNetworkAdapter> currentList, List<VmNetworkAdapter> newList)
        {
            if (newList == null) return;

            // 1. 移除已经不存在的网卡
            var toRemove = currentList.Where(c => !newList.Any(n => n.Id == c.Id)).ToList();
            foreach (var item in toRemove)
            {
                currentList.Remove(item);
            }

            // 2. 更新现有的 或 添加新的
            foreach (var newItem in newList)
            {
                var existingItem = currentList.FirstOrDefault(c => c.Id == newItem.Id);
                if (existingItem != null)
                {
                    // === 存在则更新属性 ===
                    existingItem.Name = newItem.Name;
                    existingItem.IsConnected = newItem.IsConnected;
                    existingItem.SwitchName = newItem.SwitchName;
                    existingItem.MacAddress = newItem.MacAddress;
                    existingItem.IsStaticMac = newItem.IsStaticMac;

                    if (newItem.IpAddresses != null && newItem.IpAddresses.Count > 0)
                    {
                        existingItem.IpAddresses = newItem.IpAddresses;
                    }

                    // VLAN 设置
                    existingItem.VlanMode = newItem.VlanMode;
                    existingItem.AccessVlanId = newItem.AccessVlanId;
                    existingItem.NativeVlanId = newItem.NativeVlanId;
                    existingItem.TrunkAllowedVlanIds = newItem.TrunkAllowedVlanIds;
                    existingItem.PvlanMode = newItem.PvlanMode;
                    existingItem.PvlanPrimaryId = newItem.PvlanPrimaryId;
                    existingItem.PvlanSecondaryId = newItem.PvlanSecondaryId;

                    // 带宽与安全
                    existingItem.BandwidthLimit = newItem.BandwidthLimit;
                    existingItem.BandwidthReservation = newItem.BandwidthReservation;
                    existingItem.MacSpoofingAllowed = newItem.MacSpoofingAllowed;
                    existingItem.DhcpGuardEnabled = newItem.DhcpGuardEnabled;
                    existingItem.RouterGuardEnabled = newItem.RouterGuardEnabled;
                    existingItem.MonitorMode = newItem.MonitorMode;
                    existingItem.StormLimit = newItem.StormLimit;
                    existingItem.TeamingAllowed = newItem.TeamingAllowed;

                    // 硬件卸载
                    existingItem.VmqEnabled = newItem.VmqEnabled;
                    existingItem.SriovEnabled = newItem.SriovEnabled;
                    existingItem.IpsecOffloadEnabled = newItem.IpsecOffloadEnabled;
                }
                else
                {
                    currentList.Add(newItem);
                }
            }
        }

        // 添加新的网络适配器
        [RelayCommand]
        private async Task AddNetworkAdapter()
        {
            if (SelectedVm == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await _vmNetworkService.AddNetworkAdapterAsync(SelectedVm.Name);

                if (result.Success)
                {
                    ShowSnackbar(Properties.Resources.Msg_Common_AddSuccess, Properties.Resources.Msg_Net_Added, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    await GoToNetworkSettings();
                }
                else
                {
                    ShowSnackbar(Properties.Resources.Error_Storage_AddFail, Utils.GetFriendlyErrorMessages(result.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Net_AddExc, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 移除网络适配器
        [RelayCommand]
        private async Task RemoveNetworkAdapter(string adapterId)
        {
            if (SelectedVm == null || string.IsNullOrEmpty(adapterId)) return;

            IsLoadingSettings = true;
            try
            {
                var result = await _vmNetworkService.RemoveNetworkAdapterAsync(SelectedVm.Name, adapterId);

                if (result.Success)
                {
                    ShowSnackbar(Properties.Resources.Msg_Net_Removed, Properties.Resources.Msg_Net_AdapterRemoved, ControlAppearance.Success, SymbolRegular.Delete24);
                    await GoToNetworkSettings();
                }
                else
                {
                    ShowSnackbar(Properties.Resources.Error_Storage_RemoveFail, Utils.GetFriendlyErrorMessages(result.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Net_RemoveExc, Utils.GetFriendlyErrorMessages(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 更新网卡连接状态
        [RelayCommand]
        private async Task UpdateAdapterConnection(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await _vmNetworkService.UpdateConnectionAsync(SelectedVm.Name, adapter);
                if (!result.Success)
                {
                    ShowSnackbar(Properties.Resources.Error_Common_OpFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    adapter.IsConnected = !adapter.IsConnected;
                }
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 应用 VLAN 设置
        [RelayCommand]
        private async Task ApplyVlanSettings(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await _vmNetworkService.ApplyVlanSettingsAsync(SelectedVm.Name, adapter);
                if (result.Success) ShowSnackbar(Properties.Resources.Common_Success, Properties.Resources.Msg_Net_VlanApplied, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                else ShowSnackbar(Properties.Resources.Common_Failed, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 应用 QoS 设置
        [RelayCommand]
        private async Task ApplyQosSettings(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await _vmNetworkService.ApplyBandwidthSettingsAsync(SelectedVm.Name, adapter);
                if (result.Success) ShowSnackbar(Properties.Resources.Common_Success, Properties.Resources.Msg_Net_QosApplied, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                else ShowSnackbar(Properties.Resources.Common_Failed, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 应用安全与监控设置
        [RelayCommand]
        private async Task ApplySecuritySettings(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var secResult = await _vmNetworkService.ApplySecuritySettingsAsync(SelectedVm.Name, adapter);
                if (!secResult.Success)
                {
                    ShowSnackbar(Properties.Resources.Common_Failed, string.Format(Properties.Resources.Error_Net_Security, secResult.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    return;
                }

                var offloadResult = await _vmNetworkService.ApplyOffloadSettingsAsync(SelectedVm.Name, adapter);
                if (!offloadResult.Success)
                {
                    ShowSnackbar(Properties.Resources.Common_Failed, string.Format(Properties.Resources.Error_Net_Offload, offloadResult.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    return;
                }

                ShowSnackbar(Properties.Resources.Common_Success, Properties.Resources.Msg_Common_Applied, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 切换硬件加速设置
        [RelayCommand]
        private async Task ToggleOffloadSetting(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            var result = await _vmNetworkService.ApplyOffloadSettingsAsync(SelectedVm.Name, adapter);
            if (!result.Success)
            {
                ShowSnackbar(Properties.Resources.Error_Net_ApplyFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }

        // 切换安全防护设置
        [RelayCommand]
        private async Task ToggleSecuritySetting(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            var result = await _vmNetworkService.ApplySecuritySettingsAsync(SelectedVm.Name, adapter);
            if (!result.Success)
            {
                ShowSnackbar(Properties.Resources.Error_Net_SecurityFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }

        // 引导顺序部分
        private readonly System.Threading.SemaphoreSlim _bootOrderLock = new(1, 1);
        private CancellationTokenSource? _bootSaveCts;

        [RelayCommand]
        private async Task GoToBootSettings()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.BootSettings;
            IsLoadingSettings = true;

            try
            {
                var list = await _vmBootService.GetBootOrderAsync(SelectedVm.Name);

                // 更新 UI 集合
                SelectedVm.BootOrderItems.Clear();
                foreach (var item in list) SelectedVm.BootOrderItems.Add(item);

                // 标记最后一个用于 UI 箭头显示
                if (SelectedVm.BootOrderItems.Count > 0)
                    SelectedVm.BootOrderItems.Last().IsLast = true;
            }
            finally { IsLoadingSettings = false; }
        }

        // 保存逻辑
        [RelayCommand]
        private async Task SaveBootOrder()
        {
            await SilentSaveBootOrderAsync();
        }

        public async Task SilentSaveBootOrderAsync()
        {
            if (SelectedVm == null || SelectedVm.BootOrderItems == null) return;

            try
            {
                // 关键检查：在保存前打印 UI 集合的名称顺序
                var currentOrder = SelectedVm.BootOrderItems.ToList();
                string names = string.Join(" -> ", currentOrder.Select(x => x.Name));
                Debug.WriteLine($"[VM-LOG] UI 触发保存，当前预览顺序: {names}");

                string vmName = SelectedVm.Name;
                bool success = await _vmBootService.SetBootOrderAsync(vmName, currentOrder);

                Debug.WriteLine($"[VM-LOG] 磁盘保存结果: {success}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VM-LOG] 保存过程出错: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ReorderBootItem(DragMoveArgs args)
        {
            if (SelectedVm == null || args == null) return;

            var list = SelectedVm.BootOrderItems;
            int oldIndex = list.IndexOf(args.Source);
            int newIndex = list.IndexOf(args.Target);

            if (oldIndex == -1 || newIndex == -1) return;

            // 之前的 1/3 阈值逻辑迁移到这里进行最终判定
            if (newIndex > oldIndex) // 向下拖
            {
                if (args.RelativeY < args.Threshold) return;
            }
            else // 向上拖
            {
                if (args.RelativeY > (args.Threshold * 2)) return; // 对应 targetItem.ActualHeight - threshold
            }

            // 执行移动
            list.Move(oldIndex, newIndex);

            // 更新 IsLast 标记以维护 UI 箭头显示（如果需要）
            foreach (var item in list) item.IsLast = false;
            list.Last().IsLast = true;
        }

        // ----------------------------------------------------------------------------------
        // GPU 管理模块 - 列表与基础操作
        // ----------------------------------------------------------------------------------

        // 导航至 GPU 管理页面
        [RelayCommand]
        private async Task GoToGpuSettings()
        {
            if (SelectedVm == null) return;

            CurrentViewType = VmDetailViewType.GpuSettings;
            IsLoadingSettings = true;
            try
            {
                await RefreshCurrentVmGpuAssignments();
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Common_LoadFail, Properties.Resources.Error_Gpu_ReadInfo + ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 刷新当前虚拟机的显卡分配情况
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

                Application.Current.Dispatcher.Invoke(() => {
                    bool isHardwareSame = SelectedVm.AssignedGpus.Count == tempList.Count &&
                                         SelectedVm.AssignedGpus.Select(x => x.AdapterId)
                                                      .SequenceEqual(tempList.Select(x => x.AdapterId));

                    if (isHardwareSame)
                    {
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
                        SelectedVm.AssignedGpus.Clear();
                        foreach (var item in tempList) SelectedVm.AssignedGpus.Add(item);
                    }

                    SelectedVm.RefreshGpuSummary();
                });
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Gpu_RefreshFail, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }

        // 移除 GPU 分区
        [RelayCommand]
        private async Task RemoveGpu(string adapterId)
        {
            if (SelectedVm == null || string.IsNullOrEmpty(adapterId)) return;

            var itemToRemove = SelectedVm.AssignedGpus.FirstOrDefault(x => x.AdapterId == adapterId);
            if (itemToRemove == null) return;

            IsLoadingSettings = true;
            try
            {
                bool success = await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, adapterId);

                if (success)
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        SelectedVm.AssignedGpus.Remove(itemToRemove);
                        if (SelectedVm.AssignedGpus.Count == 0)
                        {
                            SelectedVm.GpuName = string.Empty;
                        }
                    });

                    ShowSnackbar(Properties.Resources.Common_Success, Properties.Resources.Msg_Gpu_PartitionRemoved, ControlAppearance.Success, SymbolRegular.Checkmark24);

                    await Task.Delay(2000);
                    await RefreshCurrentVmGpuAssignments();
                }
                else
                {
                    ShowSnackbar(Properties.Resources.Error_Storage_RemoveFail, Properties.Resources.Error_Gpu_RemoveFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Common_OpException, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // ----------------------------------------------------------------------------------
        // GPU 管理模块 - 部署向导与自动化
        // ----------------------------------------------------------------------------------

        // 导航至添加 GPU 向导
        [RelayCommand]
        private async Task GoToAddGpu()
        {
            if (SelectedVm == null) return;

            IsLoadingSettings = true;
            try
            {
                // 1. 加载 GPU 列表
                var gpus = await _vmGpuService.GetHostGpusAsync();
                HostGpus = new ObservableCollection<GPUInfo>(gpus);
                SelectedHostGpu = null;

                // 2. 加载 Linux 脚本列表 (重写部分)
                var scripts = await _vmGpuService.GetAvailableScriptsAsync();
                AvailableLinuxScripts = new ObservableCollection<LinuxScriptItem>(scripts);
                SelectedLinuxScript = AvailableLinuxScripts.FirstOrDefault(); // 默认选中第一个（通常是本地脚本）

                CurrentViewType = VmDetailViewType.AddGpuSelect;
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Common_Error, "Failed to load GPU or Scripts: " + ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 取消添加 GPU
        [RelayCommand]
        private async Task CancelAddGpu() // 【修改为 async Task】
        {
            // 【新增：处理中途取消的回滚】
            if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId) && SelectedVm != null)
            {
                try
                {
                    await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                    _currentProcessingGpuAdapterId = null;
                }
                catch { } // 静默清理
            }

            CurrentViewType = VmDetailViewType.GpuSettings;
            GpuTasks.Clear();
        }

        partial void OnSelectedPartitionChanged(PartitionInfo? value)
        {
            if (value == null) return;
            _ = SelectPartitionAndContinueCommand.ExecuteAsync(value);
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                _selectedPartition = null;
                OnPropertyChanged(nameof(SelectedPartition));
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // 检查是否可以确认添加
        private bool CanConfirmAddGpu() => SelectedHostGpu != null;

        // 确认添加 GPU 并开始流程
        [RelayCommand(CanExecute = nameof(CanConfirmAddGpu))]
        private async Task ConfirmAddGpu()
        {
            if (SelectedHostGpu == null) return;

            CurrentViewType = VmDetailViewType.AddGpuProgress;
            ShowPartitionSelector = false;

            GpuDeploymentLog = string.Empty;
            ShowLogConsole = true;

            AppendLog(string.Format(Properties.Resources.Msg_Gpu_WorkStart, SelectedVm.Name));
            AppendLog(string.Format(Properties.Resources.Msg_Gpu_Selected, SelectedHostGpu.Name));
            AppendLog(string.Format(Properties.Resources.Msg_Gpu_Path, SelectedHostGpu.Pname));

            GpuTasks.Clear();

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.Prepare,
                Name = Properties.Resources.Task_Gpu_Prepare,
                Description = Properties.Resources.Msg_Gpu_PreparingHost,
                Status = ExHyperV.Models.TaskStatus.Pending  // 这里写全称
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.ConfigCheck,
                Name = Properties.Resources.Task_Gpu_Config,
                Description = Properties.Resources.Msg_Gpu_CheckingConfig,
                Status = ExHyperV.Models.TaskStatus.Pending  // 这里写全称
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.PowerCheck,
                Name = Properties.Resources.Task_Gpu_Power,
                Description = Properties.Resources.Msg_Gpu_CheckingPower,
                Status = ExHyperV.Models.TaskStatus.Pending  // 这里写全称
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.Optimization,
                Name = Properties.Resources.Task_Gpu_Opt,
                Description = Properties.Resources.Msg_Gpu_Mmio,
                Status = ExHyperV.Models.TaskStatus.Pending  // 这里写全称
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.Assign,
                Name = Properties.Resources.Task_Gpu_Assign,
                Description = Properties.Resources.Msg_Gpu_Creating,
                Status = ExHyperV.Models.TaskStatus.Pending  // 这里写全称
            });
            if (AutoInstallDrivers)
            {
                GpuTasks.Add(new TaskItem
                {
                    TaskType = GpuTaskType.Driver,
                    Name = Properties.Resources.Task_Gpu_Driver,
                    Description = Properties.Resources.Msg_Gpu_WaitingScan,
                    Status = ExHyperV.Models.TaskStatus.Pending
                });
            }

            await RunRealGpuWorkflowAsync(0);
        }

        // 执行 GPU 部署工作流
        private async Task RunRealGpuWorkflowAsync(int startIndex)
        {
            var tasks = GpuTasks;
            _currentProcessingGpuAdapterId = null;

            for (int i = startIndex; i < tasks.Count; i++)
            {
                if (CurrentViewType != VmDetailViewType.AddGpuProgress || SelectedHostGpu == null)
                {
                    Debug.WriteLine("GPU Workflow aborted: UI state or SelectedHostGpu has been reset.");
                    return;
                }

                var task = tasks[i];
                task.Status = ExHyperV.Models.TaskStatus.Running;
                AppendLog(string.Format(Properties.Resources.Msg_Gpu_ExecTask, task.Name));
                try
                {
                    switch (task.TaskType)
                    {
                        case GpuTaskType.Prepare:
                            await _vmGpuService.PrepareHostEnvironmentAsync();
                            task.Description = Properties.Resources.Msg_Gpu_Policy;
                            break;

                        case GpuTaskType.ConfigCheck:
                            _needConfig = !(await _vmGpuService.CheckVmForGpuAsync(SelectedVm.Name));
                            task.Description = _needConfig ? Properties.Resources.Msg_Gpu_ConfigNeeded : Properties.Resources.Msg_Gpu_ConfigOk;
                            break;

                        case GpuTaskType.PowerCheck:
                            if (_needConfig || AutoInstallDrivers)
                            {
                                var (isOff, state) = await _vmGpuService.IsVmPoweredOffAsync(SelectedVm.Name);
                                if (!isOff)
                                {
                                    task.Description = string.Format(Properties.Resources.Msg_Gpu_ForceOff, state);
                                    AppendLog(task.Description);
                                    await _powerService.ExecuteControlActionAsync(SelectedVm.Name, "TurnOff");
                                    while (!(await _vmGpuService.IsVmPoweredOffAsync(SelectedVm.Name)).IsOff)
                                    {
                                        await Task.Delay(100);
                                    }
                                }
                                task.Description = Properties.Resources.Msg_Gpu_Off;
                            }
                            else
                            {
                                task.Description = Properties.Resources.Msg_Skip;
                            }
                            break;

                        case GpuTaskType.Optimization:
                            if (_needConfig)
                            {
                                bool optOk = await _vmGpuService.OptimizeVmForGpuAsync(SelectedVm.Name);
                                task.Description = optOk ? Properties.Resources.Msg_Gpu_MmioOk : Properties.Resources.Error_Gpu_OptFail;
                            }
                            else
                            {
                                task.Description = Properties.Resources.Msg_Skip;
                            }
                            break;

                        case GpuTaskType.Assign:
                            string targetPath = !string.IsNullOrEmpty(SelectedHostGpu.Pname)
                                                ? SelectedHostGpu.Pname
                                                : SelectedHostGpu.InstanceId;

                            var assignRes = await _vmGpuService.AssignGpuPartitionAsync(SelectedVm.Name, targetPath);
                            if (!assignRes.Success) throw new Exception(assignRes.Message);
                            task.Description = Properties.Resources.Msg_Gpu_AssignOk;
                            await Task.Delay(100);
                            var currentAdapters = await _vmGpuService.GetVmGpuAdaptersAsync(SelectedVm.Name);
                            // 记录下来，以便后续步骤（如驱动安装）失败时删除
                            _currentProcessingGpuAdapterId = currentAdapters.LastOrDefault().Id;
                            break;

                        case GpuTaskType.Driver:
                            try
                            {
                                task.Description = Properties.Resources.Msg_Gpu_Scanning;
                                AppendLog(task.Description);

                                // 获取所有硬盘的所有分区
                                var allPartitions = await _vmGpuService.GetPartitionsFromVmAsync(SelectedVm.Name);

                                if (allPartitions == null || allPartitions.Count == 0)
                                {
                                    throw new Exception(Properties.Resources.Error_Gpu_NoPartFound);
                                }

                                // 计算涉及到的物理磁盘数量
                                var distinctDisks = allPartitions.Select(p => p.DiskPath).Distinct().Count();
                                if (distinctDisks == 1 && allPartitions.Count == 1)
                                {
                                    var singlePart = allPartitions[0];

                                    if (singlePart.OsType == OperatingSystemType.Windows)
                                    {
                                        // 1. 如果是 Windows 且单一，执行原有自动注入逻辑
                                        task.Description = Properties.Resources.Msg_Gpu_DetectWin;
                                        var syncRes = await _vmGpuService.SyncWindowsDriversAsync(
                                            SelectedVm.Name,
                                            SelectedHostGpu.Pname,
                                            SelectedHostGpu.Manu,
                                            singlePart,
                                            msg => { task.Description = msg; AppendLog(msg); });

                                        if (!syncRes.Success) throw new Exception(syncRes.Message);
                                        task.Description = Properties.Resources.Msg_Gpu_DriverOk;
                                    }
                                    else if (singlePart.OsType == OperatingSystemType.Linux)
                                    {
                                        // 2. [新增] 如果是 Linux 且单一，直接触发 SelectPartition 流程（嗅探 IP 并显示 SSH 表单）
                                        task.Description = "Detecting Linux system, jumping to SSH setup..."; // 建议放入资源文件
                                        AppendLog("Detected single Linux partition. Automating environment preparation...");

                                        // 异步启动 Linux 准备工作流（即你点击列表项时触发的逻辑）
                                        _ = Task.Run(async () => await SelectPartitionAndContinueCommand.ExecuteAsync(singlePart));

                                        return; // 退出当前循环，由 SelectPartitionAndContinue 接管后续逻辑
                                    }
                                }
                                else
                                {
                                    // 3. 多分区情况，保持现状，显示列表让用户选择
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        DetectedPartitions = new ObservableCollection<PartitionInfo>(allPartitions);
                                        ShowPartitionSelector = true;
                                        ShowSshForm = false;
                                    });
                                    task.Description = Properties.Resources.Msg_Gpu_ManualSelect;
                                    AppendLog(task.Description);
                                    return;
                                }
                            }
                            catch (Exception)
                            {
                                throw;
                            }
                            break;
                    }
                    task.Status = ExHyperV.Models.TaskStatus.Success;
                    AppendLog(string.Format(Properties.Resources.Msg_Gpu_TaskOk, task.Name, task.Description));
                }
                catch (Exception ex)
                {
                    task.Status = ExHyperV.Models.TaskStatus.Failed;
                    task.Description = string.Format(Properties.Resources.Error_Format_FailMsg, ex.Message);
                    AppendLog(string.Format(Properties.Resources.Error_Format_StageExc, task.Name, ex.Message));
                    if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
                    {
                        AppendLog(Properties.Resources.Error_Gpu_LinuxRollback); 
                        await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                        _currentProcessingGpuAdapterId = null;
                        AppendLog(Properties.Resources.Msg_Gpu_PartitionRemoved);
                    }

                    ShowSnackbar(Properties.Resources.Error_Common_OpFail, string.Format(Properties.Resources.Error_Format_StageError, task.Name), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    return;
                }
            }

            await FinishWorkflowAsync();
        }

        [RelayCommand]
        private async Task SelectPartitionAndContinue(PartitionInfo partition)
        {
            var driveTask = GpuTasks.FirstOrDefault(t => t.TaskType == GpuTaskType.Driver);
            if (driveTask == null) return;

            if (partition.OsType == OperatingSystemType.Windows)
            {
                ShowPartitionSelector = false;
                driveTask.Status = ExHyperV.Models.TaskStatus.Running;
                driveTask.Description = string.Format(Properties.Resources.Msg_Gpu_SyncingPart, partition.PartitionNumber);
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
                    _currentProcessingGpuAdapterId = null;
                    await FinishWorkflowAsync();
                }
                else
                {
                    if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
                    {
                        AppendLog(string.Format(Properties.Resources.Error_Gpu_Rollback, result.Message));
                        await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                        _currentProcessingGpuAdapterId = null;
                    }

                    driveTask.Status = ExHyperV.Models.TaskStatus.Failed;
                    driveTask.Description = result.Message;
                }
            }
            else if (partition.OsType == OperatingSystemType.Linux)
            {
                SelectedPartition = partition;
                IsLoadingSettings = true;

                // UI 状态转换：保持卡片开启，但切换到 SSH 表单 Grid
                ShowPartitionSelector = true;
                ShowSshForm = true;

                driveTask.Description = Properties.Resources.Msg_Gpu_LinuxVm;
                AppendLog(string.Format(Properties.Resources.Msg_Gpu_LinuxRemoteInit, partition.DisplayName));
                try
                {
                    // --- 自动探测宿主代理 (不修改全局变量) ---
                    UseSshProxy = false; // 默认关闭开关
                    try
                    {
                        var systemProxy = System.Net.WebRequest.DefaultWebProxy;
                        var proxyUri = systemProxy.GetProxy(new Uri("https://github.com"));
                        if (proxyUri != null && !proxyUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                        {
                            SshProxyHost = proxyUri.Host;
                            SshProxyPort = proxyUri.Port.ToString();
                        }
                    }
                    catch { /* 静默失败 */ }

                    // 检查虚拟机电源状态
                    var status = await _vmGpuService.IsVmPoweredOffAsync(SelectedVm.Name);
                    // 在 SelectPartitionAndContinue 方法内部：
                    if (status.IsOff)
                    {
                        driveTask.Description = Properties.Resources.Msg_Gpu_IpSniff;
                        AppendLog(driveTask.Description);

                        // 1. 执行开机
                        await _powerService.ExecuteControlActionAsync(SelectedVm.Name, "Start");

                        // 2. 【新增】立刻强制同步一次 UI 状态，不等后台循环
                        await SyncSingleVmStateAsync(SelectedVm);

                        await Task.Delay(3000); // 给系统一点反应时间
                    }

                    driveTask.Description = Properties.Resources.Msg_Gpu_IpScanning;
                    AppendLog(driveTask.Description);

                    // 扫描 IP
                    string vmIp = await Task.Run(async () =>
                    {
                        string getMacScript = $"(Get-VMNetworkAdapter -VMName '{SelectedVm.Name}').MacAddress | Select-Object -First 1";
                        var macResult = Utils.Run(getMacScript);
                        if (macResult != null && macResult.Count > 0)
                        {
                            string rawMac = macResult[0].ToString();
                            string formattedMac = System.Text.RegularExpressions.Regex.Replace(rawMac, "(.{2})", "$1:").TrimEnd(':');
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
                        SshHost = Utils.SelectBestIpv4Address(vmIp);
                        AppendLog(string.Format(Properties.Resources.Msg_Gpu_IpOk, SshHost));
                    }
                    else
                    {
                        AppendLog(Properties.Resources.Error_Gpu_IpManual);
                    }

                    driveTask.Description = Properties.Resources.Msg_Gpu_SshConfirm;
                }
                catch (Exception ex)
                {
                    ShowSnackbar(Properties.Resources.Error_Gpu_EnvFail, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    AppendLog(string.Format(Properties.Resources.Warn_Gpu_EnvExc, ex.Message));
                }
                finally
                {
                    IsLoadingSettings = false;
                }
            }
        }
        // 开始 Linux 部署
        // 开始 Linux 部署
        [RelayCommand] // 之前缺失这个特性，导致按钮无效
        private async Task StartLinuxDeploy()
        {
            _gpuDeploymentCts?.Cancel();
            _gpuDeploymentCts = new CancellationTokenSource();
            var token = _gpuDeploymentCts.Token;

            // 1. 定位驱动安装任务项
            var driveTask = GpuTasks.FirstOrDefault(t => t.TaskType == GpuTaskType.Driver);
            if (driveTask == null) return;

            // 2. 验证
            if (SelectedLinuxScript == null || string.IsNullOrWhiteSpace(SshHost))
            {
                ShowSnackbar(Properties.Resources.Error_Common_Verify, Properties.Resources.VirtualMachinesPageViewModel_20, ControlAppearance.Caution, SymbolRegular.Warning24);
                return;
            }

            // 3. 代理参数解析
            int? proxyPort = null;
            string proxyHost = string.Empty;
            if (UseSshProxy)
            {
                proxyHost = SshProxyHost?.Trim() ?? string.Empty;
                if (!int.TryParse(SshProxyPort, out int port) || string.IsNullOrWhiteSpace(proxyHost))
                {
                    ShowSnackbar(Properties.Resources.Error_Common_Verify, Properties.Resources.Validation_ProxyIpAndPortMismatch, ControlAppearance.Danger, SymbolRegular.Warning24);
                    return;
                }
                proxyPort = port;
            }

            // 4. UI 切换：隐藏卡片，显示控制台
            ShowPartitionSelector = false;
            ShowSshForm = false;
            ShowLogConsole = true;
            driveTask.Status = ExHyperV.Models.TaskStatus.Running;

            AppendLog(Properties.Resources.Msg_Gpu_DeployStart);
            AppendLog($"[Info] Selected Script: {SelectedLinuxScript.Name}");
            if (UseSshProxy) AppendLog(string.Format(Properties.Resources.Msg_Gpu_UsingProxy, proxyHost, proxyPort));

            // 5. 组装凭据 (强制 KeepGlobalProxySetting 为 false)
            var creds = new SshCredentials
            {
                Host = SshHost,
                Port = SshPort,
                Username = SshUsername,
                Password = SshPassword,
                UseProxy = this.UseSshProxy,
                ProxyHost = this.UseSshProxy ? proxyHost : null,
                ProxyPort = this.UseSshProxy ? proxyPort : null,
                InstallGraphics = InstallGraphics
            };

            // 6. 执行部署
            string result = await _vmGpuService.ProvisionLinuxGpuAsync(
                SelectedVm.Name,
                SelectedLinuxScript,
                creds,
                msg => {
                    if (msg.Contains("[STEP:"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(msg, @"\[STEP:\s*(.*?)\]");
                        if (match.Success)
                        {
                            Application.Current.Dispatcher.Invoke(() => {
                                driveTask.Description = match.Groups[1].Value;
                            });
                        }
                    }
                    AppendLog(msg);
                },
                token
            );

            // 7. 流程结束判定
            if (result == "OK" || (result.Contains("successfully") && result.Contains("signing")))
            {
                driveTask.Status = ExHyperV.Models.TaskStatus.Success;
                driveTask.Description = Properties.Resources.Msg_Gpu_LinuxDeployDone;
                _currentProcessingGpuAdapterId = null;
                AppendLog(Properties.Resources.Msg_Gpu_LinuxDeployDone);
                await FinishWorkflowAsync();
            }
            else
            {
                // 失败回滚
                if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
                {
                    AppendLog(Properties.Resources.Error_Gpu_LinuxRollback);
                    await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                    _currentProcessingGpuAdapterId = null;
                }

                driveTask.Status = ExHyperV.Models.TaskStatus.Failed;
                driveTask.Description = result;
                AppendLog(string.Format(Properties.Resources.Error_Gpu_DeployFatal, result));
            }
        }
        // 返回分区选择列表
        [RelayCommand]
        private void GoBackToPartitionList()
        {
            ShowSshForm = false;
            var driveTask = GpuTasks.FirstOrDefault(t => t.Name == Properties.Resources.Task_Gpu_Driver);
            if (driveTask != null)
            {
                driveTask.Description = Properties.Resources.Msg_Gpu_SelectPart;
            }
        }

        // 完成 GPU 部署工作流
        private async Task FinishWorkflowAsync()
        {
            await Task.Delay(1000);
            // 确保在 UI 线程刷新
            await RefreshCurrentVmGpuAssignments();

            // --- 核心修复：非空安全获取显卡名称 ---
            string gpuName = "GPU";
            if (SelectedHostGpu != null)
            {
                gpuName = SelectedHostGpu.Name;
            }
            else if (SelectedVm?.AssignedGpus?.Count > 0)
            {
                // 如果 SelectedHostGpu 已经被重置，尝试从已分配列表里拿名字
                gpuName = SelectedVm.AssignedGpus.Last().Name;
            }

            CurrentViewType = VmDetailViewType.GpuSettings;

            ShowSnackbar(
                Properties.Resources.Msg_Common_ConfigSuccess,
                string.Format(Properties.Resources.Msg_Gpu_Ready, gpuName),
                ControlAppearance.Success,
                SymbolRegular.CheckmarkCircle24);
        }

        // 设备 ID 格式化辅助
        private string NormalizeDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return string.Empty;
            var normalizedId = deviceId.ToUpper();
            if (normalizedId.StartsWith(@"\\?\")) normalizedId = normalizedId.Substring(4);
            int suffixIndex = normalizedId.IndexOf("#{");
            if (suffixIndex != -1) normalizedId = normalizedId.Substring(0, suffixIndex);
            return normalizedId.Replace('\\', '#').Replace("#", "");
        }

        // ----------------------------------------------------------------------------------
        // UI 辅助方法
        // ----------------------------------------------------------------------------------

        // 显示 Snackbar 通知
        private void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
        {
            Application.Current.Dispatcher.Invoke(() => {
                var presenter = Application.Current.MainWindow?.FindName("SnackbarPresenter") as SnackbarPresenter;
                if (presenter != null)
                {
                    // 根据类型决定显示时间
                    // 如果是 Danger (错误) 或 Caution (警告)，设置为 30 (不自动消失，直到手动点叉) 
                    TimeSpan timeout = (appearance == ControlAppearance.Danger || appearance == ControlAppearance.Caution)
                        ? TimeSpan.FromSeconds(30)
                        : TimeSpan.FromSeconds(2); // 成功信息 2 秒消失

                    var snack = new Snackbar(presenter)
                    {
                        Title = title,
                        Content = message,
                        Appearance = appearance,
                        Icon = new SymbolIcon(icon),
                        Timeout = timeout
                    };
                    snack.Show();
                }
            });

        }

        // 获取操作状态的乐观显示文本
        private string GetOptimisticText(string action) => action switch { "Start" => Properties.Resources.Status_Starting, "Restart" => Properties.Resources.Status_Restarting, "Stop" => Properties.Resources.Status_StoppingPresent, "TurnOff" => Properties.Resources.Status_Off, "Save" => Properties.Resources.Status_Saving, "Suspend" => Properties.Resources.Status_Suspending, _ => Properties.Resources.Status_Processing };

        // 追加日志到控制台
        private void AppendLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            Application.Current.Dispatcher.Invoke(() => {
                GpuDeploymentLog += $"[{timestamp}] {message}{Environment.NewLine}";
            });
        }

        // 复制日志
        [RelayCommand]
        private void CopyLog()
        {
            if (!string.IsNullOrEmpty(GpuDeploymentLog))
            {
                Clipboard.SetText(GpuDeploymentLog);
                ShowSnackbar(Properties.Resources.Msg_Common_CopyOk, Properties.Resources.Msg_Gpu_LogCopy, ControlAppearance.Success, SymbolRegular.Copy24);
            }
        }

        // 复制文本到剪贴板
        [RelayCommand]
        private void CopyToClipboard(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text == "---" || text == "00-00-00-00-00-00") return;
            Clipboard.SetText(text);
        }
        // ✅ 增加重置/重试命令逻辑

        [RelayCommand]
        private async Task ResetGpuDeployment()
        {
            _gpuDeploymentCts?.Cancel();
            _gpuDeploymentCts = new CancellationTokenSource();
            IsLoadingSettings = false;

            if (SelectedPartition != null)
            {
                // --- 场景 1: 软重置 ---
                var driveTask = GpuTasks.FirstOrDefault(t => t.TaskType == GpuTaskType.Driver);
                if (driveTask != null)
                {
                    driveTask.Status = ExHyperV.Models.TaskStatus.Pending;
                    driveTask.Description = SelectedPartition.OsType == OperatingSystemType.Linux
                        ? Properties.Resources.Msg_Gpu_SshConfirm
                        : Properties.Resources.Msg_Gpu_SelectPart;
                }

                if (SelectedPartition.OsType == OperatingSystemType.Linux)
                {
                    ShowPartitionSelector = true;
                    ShowSshForm = true;
                }
                else
                {
                    // Windows 流程重置
                    ShowPartitionSelector = true;
                    ShowSshForm = false;

                    // --- 关键改进：清空选中项，允许用户重新点击同一个分区 ---
                    SelectedPartition = null;
                }

                AppendLog($"--- {Properties.Resources.Label_Progress} ({Properties.Resources.VirtualMachinesPageViewModel_24}) ---");
                return;
            }
            // --- 场景 2: “硬重置”（彻底回滚，回到选显卡第一步） ---
            // 触发条件：还没有选定分区就挂了，或者用户在还没选分区时点击了重置

            // 1. 如果当前有正在处理的分区 ID（说明已经分配但未成功），执行物理回滚
            if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
            {
                AppendLog(Properties.Resources.VirtualMachinesPageViewModel_21); // "正在回滚 GPU 分配..."
                try
                {
                    await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                    _currentProcessingGpuAdapterId = null;
                    AppendLog(Properties.Resources.VirtualMachinesPageViewModel_22); // "回滚完成。"
                }
                catch (Exception ex)
                {
                    AppendLog(string.Format(Properties.Resources.VirtualMachinesPageViewModel_23, ex.Message)); // "回滚失败: {0}"
                }
            }

            // 2. 重置所有 UI 状态
            GpuTasks.Clear();
            GpuDeploymentLog = string.Empty;
            ShowPartitionSelector = false;
            ShowSshForm = false;
            ShowLogConsole = false;

            // 3. 彻底重来，重新初始化数据并跳转回选择界面
            await GoToAddGpu();

            // 4. 弹出全局重置提示
            ShowSnackbar(
                Properties.Resources.VirtualMachinesPageViewModel_24, // "重置"
                Properties.Resources.VirtualMachinesPageViewModel_25, // "流程已重置，请重新选择显卡。"
                Wpf.Ui.Controls.ControlAppearance.Info,
                Wpf.Ui.Controls.SymbolRegular.ArrowCounterclockwise24);
        }
        private async Task MonitorThumbnailLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // 只有当选中且运行时才更新
                if (SelectedVm != null && SelectedVm.IsRunning)
                {
                    var img = await VmThumbnailProvider.GetThumbnailAsync(SelectedVm.Name, 320, 240);
                    if (img != null)
                    {
                        Application.Current.Dispatcher.Invoke(() => SelectedVm.Thumbnail = img);
                    }
                }
                else if (SelectedVm != null && !SelectedVm.IsRunning && SelectedVm.Thumbnail != null)
                {
                    Application.Current.Dispatcher.Invoke(() => SelectedVm.Thumbnail = null);
                }

                // 缩略图不需要太高的刷新率，1.5秒或2秒一次即可，避免占用过多WMI资源
                await Task.Delay(1500, token);
            }
        }
        // 获取目录，用于 InitialDirectory
        private string GetDir(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        // 获取文件名，用于 SaveFileDialog 的 FileName
        private string GetFileName(string? path, string defaultNameWithExt)
        {
            if (string.IsNullOrWhiteSpace(path)) return defaultNameWithExt;
            try
            {
                return Path.GetFileName(path) ?? defaultNameWithExt;
            }
            catch { return defaultNameWithExt; }
        }

    }
}
