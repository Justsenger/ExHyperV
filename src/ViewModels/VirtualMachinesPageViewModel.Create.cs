using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

        // 控制右侧界面切换
        [ObservableProperty] private bool _isCreatingVm = false;
        [ObservableProperty] private string _creatingStatusText = string.Empty;
        [ObservableProperty] private bool _isLoadingCreateOptions = false;

        partial void OnNewVmNameChanged(string value)
        {
            UpdateDiskPath();
        }

        // 用户直接键入配置目录时，不允许稍后完成的主机探测覆盖它。
        private bool _isUpdatingConfigPath;
        private bool _isConfigPathManual;
        partial void OnNewVmStoragePathChanged(string value)
        {
            if (!_isUpdatingConfigPath)
                _isConfigPathManual = true;
        }


        // 重命名

        [RelayCommand]
        private void RenameVm(VmInstanceViewModel vm)
        {
            if (vm == null) return;
            vm.StartEditing();
        }

        [RelayCommand]
        private void CancelRename(VmInstanceViewModel vm)
        {
            if (vm == null) return;
            vm.IsEditing = false;
        }

        [RelayCommand]
        private async Task CommitRenameAsync(VmInstanceViewModel vm)
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
                // 传 vmId 而非 oldName（VM 可能已改名）
                var result = await VmEditService.RenameVmAsync(vmId, newName);

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
                    ShowError($"{Properties.Resources.VmPage_RenameFail}：{result.Message}");
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }


        [ObservableProperty] private string _newVmName = "NewVM";
        [ObservableProperty] private string _newVmStoragePath = string.Empty;

        // 批量创建数量：>1 时名称按 base-NN 生成、各台并行建；=1 与原单建完全一致。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSingleCreate))]
        private string _newVmQuantity = "1";

        // 批量创建不能复用同一个现有磁盘。
        public bool IsSingleCreate => !(int.TryParse(NewVmQuantity, out var q) && q > 1);

        [ObservableProperty] private ObservableCollection<string> _supportedVersions = new() { "12.0", "11.0", "10.0", "9.0", "8.0" };
        [ObservableProperty] private string _selectedVersion = "8.0";

        [ObservableProperty] private string _newVmProcessorCount = "4"; // ComboBox IsEditable="True" 绑定 string
        [ObservableProperty] private string _newVmMemoryMb = "4096";    // ComboBox IsEditable="True" 绑定 string
        [ObservableProperty] private bool _newVmDynamicMemory = false;

        // 安全特性 (仅第 2 代)
        [ObservableProperty] private bool _newVmEnableSecureBoot = true;
        [ObservableProperty] private bool _newVmEnableTpm = true;
        [ObservableProperty] private string _newVmIsolationType = "Disabled"; // Disabled, TrustedLaunch, VBS, SNP, TDX, RME, OpenHCL
        [ObservableProperty] private string _newVmOpenHclIgvmPath = string.Empty;

        [ObservableProperty] private int _newVmDiskMode = 0; // 0:新建磁盘, 1:现有磁盘, 2:稍后附加
        [ObservableProperty] private string _newVmDiskSizeGb = "128";
        [ObservableProperty] private string _newVmNewDiskPath = string.Empty;      // 模式0使用
        [ObservableProperty] private string _newVmExistingDiskPath = string.Empty; // 模式1使用

        // 安装介质 (ISO)
        [ObservableProperty] private string _newVmIsoPath = string.Empty;

        [ObservableProperty] private string _newVmSelectedSwitch = string.Empty;
        [ObservableProperty] private bool _startVmAfterCreation = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEnableIsolation))] // 当此值改变，通知 UI 刷新 CanEnableIsolation
        private bool _isIsolationSupported = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEnableIsolation))] // 当代际改变，通知 UI 刷新 CanEnableIsolation
        private int _newVmGeneration = 2;

        public bool CanEnableIsolation => IsIsolationSupported && NewVmGeneration == 2;

        // ARM64 的 Hyper-V 不提供 IDE 控制器，无法承载第 1 代虚拟机（建机会卡在加盘步 Storage_Error_ControllerNotFound），
        // 据此禁用第 1 代选项。OS 架构运行期不变，故为只读计算属性、无需变更通知。
        public bool CanUseGen1 => RuntimeInformation.OSArchitecture != Architecture.Arm64;

        [ObservableProperty]
        private ObservableCollection<string> _supportedIsolationTypes = new() { "Disabled" };
        private bool _isDiskPathManual;
        private bool _isUpdatingDiskPath;
        private string _defaultVhdPath = @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks";

        partial void OnNewVmNewDiskPathChanged(string value)
        {
            if (!_isUpdatingDiskPath)
                _isDiskPathManual = true;
        }

        partial void OnNewVmGenerationChanged(int value)
        {
            if (value == 2) return;

            NewVmIsolationType = "Disabled";
            NewVmOpenHclIgvmPath = string.Empty;
        }




        private void UpdateDiskPath()
        {
            if (string.IsNullOrWhiteSpace(NewVmName) || _isDiskPathManual) return; // 如果手动选过，就不再自动更新

            string root = string.IsNullOrWhiteSpace(_defaultVhdPath)
                ? NewVmStoragePath
                : _defaultVhdPath;
            try
            {
                _isUpdatingDiskPath = true;
                NewVmNewDiskPath = Path.Combine(root, NewVmName);   // 只存文件夹，vhdx 文件名由服务按最终 VM 名派生
            }
            catch { }
            finally { _isUpdatingDiskPath = false; }
        }

        private void SetDetectedConfigPath(string path)
        {
            if (_isConfigPathManual) return;
            try
            {
                _isUpdatingConfigPath = true;
                NewVmStoragePath = path;
            }
            finally { _isUpdatingConfigPath = false; }
        }

        [RelayCommand]
        private async Task CreateVmAsync()
        {
            if (IsExecutingVmImport || IsPreparingVmImport) return;
            await DisposeVmImportSessionAsync();
            VmImportPreview = null;
            VmImportStep = 0;
            IsVmImportCompleted = false;
            IsVmImportViewVisible = false;
            IsCreatingVm = true;
            IsLoadingCreateOptions = true;
            SelectedVm = null;
            _isDiskPathManual = false;
            _isConfigPathManual = false;
            _defaultVhdPath = @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks";

            NewVmGeneration = 2;
            NewVmMemoryMb = "4096";
            int hostCores = Environment.ProcessorCount;
            NewVmProcessorCount = (hostCores >= 4 ? 4 : hostCores).ToString();

            NewVmDiskMode = 0;
            NewVmDiskSizeGb = "128";
            NewVmQuantity = "1";
            NewVmDynamicMemory = false;
            NewVmEnableSecureBoot = true;
            NewVmEnableTpm = true;
            NewVmOpenHclIgvmPath = string.Empty;
            StartVmAfterCreation = true;
            NewVmIsoPath = string.Empty;
            NewVmExistingDiskPath = string.Empty;

            // 先用可靠的回退值立即呈现表单；主机真实值随后在后台刷新。
            SetDetectedConfigPath(@"C:\ProgramData\Microsoft\Windows\Hyper-V");
            NewVmName = GetNextAvailableName("NewVM");
            UpdateDiskPath();

            try
            {
                // 四组主机信息彼此独立，并行探测，避免每次进入页面串行等待多轮 WMI。
                var hostPathsTask = VmCreateService.GetHostDefaultPathsAsync();
                var versionsTask = VmCreateService.GetSupportedVersionsAsync();
                var isolationTask = VmCreateService.GetIsolationSupportAsync();
                var switchesTask = VmNetworkService.GetAvailableSwitchesAsync();

                await Task.WhenAll(hostPathsTask, versionsTask, isolationTask, switchesTask);

                var hostPaths = await hostPathsTask;
                var allVersions = await versionsTask;
                var (supported, types) = await isolationTask;
                var switches = await switchesTask;

                // 配置文件与虚拟硬盘分别采用各自的主机默认位置。
                SetDetectedConfigPath(hostPaths.DefaultVmPath);
                _defaultVhdPath = string.IsNullOrWhiteSpace(hostPaths.DefaultVhdPath)
                    ? hostPaths.DefaultVmPath
                    : hostPaths.DefaultVhdPath;

                UpdateDiskPath();

                SupportedVersions = new ObservableCollection<string>(allVersions);

                // 在已降序的列表里取第一个小于 200 的稳定版本作默认值
                var defaultStable = allVersions.FirstOrDefault(v =>
                    double.TryParse(v, out double verNum) && verNum < 200);

                SelectedVersion = defaultStable ?? SupportedVersions.FirstOrDefault();

                IsIsolationSupported = supported;
                SupportedIsolationTypes = new ObservableCollection<string>(types);

                // 初始状态默认为 Disabled
                NewVmIsolationType = "Disabled";

                string noneText = Properties.Resources.Common_None; // “未连接”的文本
                var switchList = new List<string> { noneText };
                if (switches != null) switchList.AddRange(switches);

                AvailableSwitchNames = new ObservableCollection<string>(switchList);


                var defaultSwitch = AvailableSwitchNames.FirstOrDefault(s =>
                    s.Contains("Default", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains(Properties.Resources.VmPage_Default, StringComparison.OrdinalIgnoreCase));

                if (defaultSwitch != null)
                {
                    NewVmSelectedSwitch = defaultSwitch;
                }
                else
                {
                    var firstRealSwitch = AvailableSwitchNames.FirstOrDefault(s => s != noneText);

                    NewVmSelectedSwitch = firstRealSwitch ?? noneText;
                }

            }
            catch (Exception ex)
            {
                AvailableSwitchNames = new ObservableCollection<string> { Properties.Resources.Common_None };
                NewVmSelectedSwitch = AvailableSwitchNames[0];
                ShowError($"{Properties.Resources.VmPage_CreateOptionsLoadFail}：{FriendlyError.CleanLines(ex.Message)}");
                Debug.WriteLine($"[CREATE-VM-NET-ERROR] {ex.Message}");
            }

            finally
            {
                IsLoadingCreateOptions = false;
            }
        }
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


        [RelayCommand]
        private void BrowseNewVmPath()
        {
            var picked = Dialogs.PickFolder(Properties.Resources.VmPage_SelectConfigDir,
                string.IsNullOrWhiteSpace(NewVmStoragePath) ? null : NewVmStoragePath);
            if (picked != null) NewVmStoragePath = picked;
        }


        [RelayCommand]
        private void BrowseNewDiskLocation()
        {
            var picked = Dialogs.PickFolder(Properties.Resources.VmPage_SelectNewVhdPath,
                string.IsNullOrWhiteSpace(NewVmNewDiskPath) ? null : NewVmNewDiskPath);
            if (picked != null)
            {
                NewVmNewDiskPath = picked;   // 文件夹；vhdx 文件名由服务按 VM 名派生
                _isDiskPathManual = true; // 标记用户已手动选择
            }
        }

        [RelayCommand]
        private void BrowseExistingDisk()
        {
            var picked = Dialogs.PickOpenFile(Properties.Resources.VmPage_SelectExistVhd, Properties.Resources.VmPage_VhdFilterBoth, GetDir(NewVmExistingDiskPath));
            if (picked != null) NewVmExistingDiskPath = picked;
        }

        [RelayCommand]
        private void BrowseIsoImage()
        {
            var picked = Dialogs.PickOpenFile(Properties.Resources.VmPage_SelectIso, Properties.Resources.VmPage_IsoFilter, GetDir(NewVmIsoPath));
            if (picked != null) NewVmIsoPath = picked;
        }

        [RelayCommand]
        private void BrowseOpenHclIgvm()
        {
            var picked = Dialogs.PickOpenFile(
                Properties.Resources.VmPage_SelectOpenHclIgvm,
                Properties.Resources.VmPage_OpenHclIgvmFilter,
                GetDir(NewVmOpenHclIgvmPath));
            if (picked != null) NewVmOpenHclIgvmPath = picked;
        }

        [RelayCommand]
        private async Task ConfirmCreateAsync()
        {
            if (string.IsNullOrWhiteSpace(NewVmName))
            {
                ShowTip(Properties.Resources.VmPage_NameEmpty);
                return;
            }

            if (NewVmDiskMode == 0) // 新建磁盘
            {
                if (string.IsNullOrWhiteSpace(NewVmNewDiskPath))
                {
                    ShowTip(Properties.Resources.VmPage_SelectVhdSave);
                    return;
                }
            }
            else if (NewVmDiskMode == 1) // 现有磁盘
            {
                if (string.IsNullOrWhiteSpace(NewVmExistingDiskPath))
                {
                    ShowTip(Properties.Resources.VmPage_SelectExistVhdPath);
                    return;
                }

                if (!File.Exists(NewVmExistingDiskPath))
                {
                    ShowTip(Properties.Resources.VmPage_ExistVhdNotFound);
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(NewVmIsoPath) && !File.Exists(NewVmIsoPath))
            {
                ShowTip(Properties.Resources.VmPage_IsoNotFound);
                return;
            }

            if (NewVmIsolationType == "OpenHCL")
            {
                if (string.IsNullOrWhiteSpace(NewVmOpenHclIgvmPath) ||
                    !File.Exists(NewVmOpenHclIgvmPath))
                {
                    ShowTip(Properties.Resources.VmPage_OpenHclIgvmRequired);
                    return;
                }

                if (!Version.TryParse(SelectedVersion, out var openHclVersion) ||
                    openHclVersion < new Version(12, 0))
                {
                    ShowTip(Properties.Resources.VmPage_OpenHclRequiresV12);
                    return;
                }
            }

            if (!int.TryParse(NewVmProcessorCount, out var cpuCount) || cpuCount < 1)
            {
                ShowTip(Properties.Resources.VmPage_InvalidCpuCount);
                return;
            }
            if (!long.TryParse(NewVmMemoryMb, out var memoryMb) || memoryMb < 32)
            {
                ShowTip(Properties.Resources.VmPage_InvalidMemory);
                return;
            }
            if (NewVmDiskMode == 0 && (!long.TryParse(NewVmDiskSizeGb, out var diskSize) || diskSize < 1))
            {
                ShowTip(Properties.Resources.VmPage_InvalidDiskSize);
                return;
            }

            int quantity = int.TryParse(NewVmQuantity, out var qv) && qv >= 1 ? qv : 0;
            if (quantity < 1)
            {
                ShowTip(Properties.Resources.VmPage_InvalidQuantity);
                return;
            }
            // 组装参数（单台/批量共用；数值已校验，直接用解析结果）
            VmCreationParams Build(string name) => new VmCreationParams
            {
                Name = name,
                Path = NewVmStoragePath,
                Version = SelectedVersion,
                Generation = NewVmGeneration,
                ProcessorCount = cpuCount,
                MemoryMb = memoryMb,
                EnableDynamicMemory = NewVmDynamicMemory,
                EnableSecureBoot = NewVmEnableSecureBoot,
                EnableTpm = NewVmEnableTpm,
                IsolationType = NewVmIsolationType,
                OpenHclIgvmPath = NewVmOpenHclIgvmPath,
                DiskMode = NewVmDiskMode,
                DiskSizeGb = long.TryParse(NewVmDiskSizeGb, out var ds) ? ds : 128,
                VhdPath = NewVmDiskMode == 0 ? NewVmNewDiskPath : NewVmExistingDiskPath,
                CreateDifferencingDisk = quantity > 1 && NewVmDiskMode == 1,
                DifferencingDiskRoot = _defaultVhdPath,
                IsoPath = NewVmIsoPath,
                SwitchName = NewVmSelectedSwitch,
                StartAfterCreation = StartVmAfterCreation
            };

            IsLoadingSettings = true;
            try
            {
                if (quantity <= 1)
                {
                    CreatingStatusText = Properties.Resources.VmPage_CreatingVm;
                    var result = await VmCreateService.CreateVirtualMachineAsync(Build(NewVmName));
                    if (result.Success)
                    {
                        string actualCreatedName = result.Message;
                        ShowSuccess(string.Format(Properties.Resources.VmPage_VmCreated, actualCreatedName));
                        IsCreatingVm = false;
                        await LoadVmsCommand.ExecuteAsync(null);
                        var newVm = VmList.FirstOrDefault(v => v.Name.Equals(actualCreatedName, StringComparison.OrdinalIgnoreCase));
                        if (newVm != null) SelectedVm = newVm;
                        // 启动放此处：成功后单独启动并检查引擎返回，失败(如内存不足)弹原因而非静默吞掉。
                        if (StartVmAfterCreation)
                        {
                            CreatingStatusText = Properties.Resources.VmPage_StartingVm;
                            var startResult = await VmPowerService.ExecuteControlActionAsync(actualCreatedName, "Start");
                            if (!startResult.Success)
                                ShowError($"{Properties.Resources.VmPage_StartFail}：{FriendlyError.CleanLines(startResult.Error)}");
                        }
                    }
                    else
                    {
                        ShowError($"{Properties.Resources.VmPage_CreateFail}：{result.Message}");
                    }
                }
                else
                {
                    // 批量：base-NN 命名 → 各台并行建 → 聚合汇报 → 只重载一次。资源够不够交给用户。
                    CreatingStatusText = string.Format(Properties.Resources.VmPage_CreatingBatch, quantity);
                    var names = await VmCreateService.BuildBatchNamesAsync(NewVmName, NewVmStoragePath, quantity);
                    var results = await Task.WhenAll(names.Select(n => VmCreateService.CreateVirtualMachineAsync(Build(n))));
                    int okCount = results.Count(r => r.Success);

                    int startFail = 0;
                    if (StartVmAfterCreation)
                    {
                        var okNames = names.Where((n, i) => results[i].Success).ToList();
                        var starts = await Task.WhenAll(okNames.Select(n => VmPowerService.ExecuteControlActionAsync(n, "Start")));
                        startFail = starts.Count(s => !s.Success);
                    }

                    IsCreatingVm = false;
                    await LoadVmsCommand.ExecuteAsync(null);

                    string tail = startFail > 0 ? string.Format(Properties.Resources.VmPage_BatchStartFail, startFail) : string.Empty;
                    if (okCount == quantity)
                        ShowSuccess(string.Format(Properties.Resources.VmPage_BatchAllOk, okCount) + tail);
                    else
                        ShowError(string.Format(Properties.Resources.VmPage_BatchPartial, okCount, quantity - okCount) + tail);
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                IsLoadingSettings = false;
                CreatingStatusText = string.Empty;
            }
        }

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




    }
}
