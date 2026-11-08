using ExHyperV.UI.Shared.Interaction;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
            {
                _isConfigPathManual = true;
                UpdateDiskPath();
            }
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

            if (VmEditService.GetNameError(vm.EditedName) != null || vm.EditedName == vm.Name) return;

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
                    ShowError($"{Properties.Resources.VmPage_RenameFail}：{result.Error}");
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
        public bool CanUseGen1 => VmCreateService.SupportsGeneration1;

        [ObservableProperty]
        private ObservableCollection<string> _supportedIsolationTypes = new() { "Disabled" };
        private bool _isDiskPathManual;
        private bool _isUpdatingDiskPath;
        private string _defaultVhdPath = VmCreateService.DefaultDiskDirectory;

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

            try
            {
                _isUpdatingDiskPath = true;
                NewVmNewDiskPath = VmCreateService.GetSuggestedDiskDirectory(
                    NewVmName, NewVmStoragePath, _defaultVhdPath, _isConfigPathManual);
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
            var defaults = VmCreateService.GetDefaultCreationParameters(VmList.Select(vm => vm.Name));
            _defaultVhdPath = defaults.DifferencingDiskRoot;
            NewVmGeneration = defaults.Generation;
            NewVmMemoryMb = defaults.MemoryMiB.ToString();
            NewVmProcessorCount = defaults.ProcessorCount.ToString();
            NewVmDiskMode = defaults.DiskMode;
            NewVmDiskSizeGb = defaults.DiskSizeGiB.ToString();
            NewVmQuantity = "1";
            NewVmDynamicMemory = defaults.EnableDynamicMemory;
            NewVmEnableSecureBoot = defaults.EnableSecureBoot;
            NewVmEnableTpm = defaults.EnableTpm;
            NewVmOpenHclIgvmPath = defaults.OpenHclIgvmPath;
            StartVmAfterCreation = defaults.StartAfterCreation;
            NewVmIsoPath = defaults.IsoPath;
            NewVmExistingDiskPath = defaults.VhdPath;
            SetDetectedConfigPath(defaults.Path);
            NewVmName = defaults.Name;
            UpdateDiskPath();

            try
            {
                var options = (await VmCreateService.GetCreationOptionsAsync()).RequireData();
                SetDetectedConfigPath(options.DefaultVmPath);
                _defaultVhdPath = options.DefaultVhdPath;
                UpdateDiskPath();
                SupportedVersions = new ObservableCollection<string>(options.Versions);
                SelectedVersion = options.DefaultVersion;
                IsIsolationSupported = options.IsolationSupported;
                SupportedIsolationTypes = new ObservableCollection<string>(options.IsolationTypes);
                NewVmIsolationType = "Disabled";
                AvailableSwitchNames = new ObservableCollection<string>(options.Switches);
                NewVmSelectedSwitch = options.DefaultSwitch;

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
            if (VmEditService.GetNameError(NewVmName) is string nameError)
            {
                ShowTip(nameError);
                return;
            }

            // The desktop still asks the user to finish its disk picker. Core also
            // supports callers that omit the directory and use the VM directory.
            if (NewVmDiskMode == 0)
            {
                if (string.IsNullOrWhiteSpace(NewVmNewDiskPath))
                {
                    ShowTip(Properties.Resources.VmPage_SelectVhdSave);
                    return;
                }
            }

            // Text parsing stays with the form; Core owns the admissible values
            // and error order, including parse failures represented by zero.
            int.TryParse(NewVmProcessorCount, out int cpuCount);
            long.TryParse(NewVmMemoryMb, out long memoryMb);
            int.TryParse(NewVmQuantity, out int quantity);
            long diskSizeGb = long.TryParse(NewVmDiskSizeGb, out long parsedDiskSize)
                ? parsedDiskSize : NewVmDiskMode == 0 ? 0 : 128;
            VmCreationParameters Build(string name) => new VmCreationParameters
            {
                Name = name,
                Path = NewVmStoragePath,
                Version = SelectedVersion,
                Generation = NewVmGeneration,
                ProcessorCount = cpuCount,
                MemoryMiB = memoryMb,
                EnableDynamicMemory = NewVmDynamicMemory,
                EnableSecureBoot = NewVmEnableSecureBoot,
                EnableTpm = NewVmEnableTpm,
                IsolationType = NewVmIsolationType,
                OpenHclIgvmPath = NewVmOpenHclIgvmPath,
                DiskMode = NewVmDiskMode,
                DiskSizeGiB = diskSizeGb,
                VhdPath = NewVmDiskMode == 0 ? NewVmNewDiskPath : NewVmExistingDiskPath,
                StoreNewDisksWithVm = VmCreateService.ShouldStoreNewDisksWithVm(
                    NewVmDiskMode, _isConfigPathManual, _isDiskPathManual),
                DifferencingDiskRoot = _defaultVhdPath,
                IsoPath = NewVmIsoPath,
                SwitchName = NewVmSelectedSwitch,
                StartAfterCreation = StartVmAfterCreation
            };

            if (VmCreateService.GetCreationError(Build(NewVmName), quantity) is string error)
            {
                ShowTip(error);
                return;
            }

            IsLoadingSettings = true;
            try
            {
                CreatingStatusText = quantity <= 1 ? Properties.Resources.VmPage_CreatingVm
                    : string.Format(Properties.Resources.VmPage_CreatingBatch, quantity);
                var result = await VmCreateService.CreateBatchAsync(Build(NewVmName), quantity,
                    async created =>
                    {
                        ShowSuccess(string.Format(Properties.Resources.VmPage_VmCreated, created.ActualName));
                        IsCreatingVm = false;
                        await LoadVmsCommand.ExecuteAsync(null);
                        var newVm = VmList.FirstOrDefault(v => v.Id == created.VmId);
                        if (newVm != null) SelectedVm = newVm;
                    },
                    () => CreatingStatusText = Properties.Resources.VmPage_StartingVm);
                if (result.ValidationError is string validationError)
                {
                    ShowTip(validationError);
                    return;
                }
                if (quantity <= 1)
                {
                    var item = result.Items[0];
                    if (!item.Creation.Success)
                    {
                        if (item.Creation.RegistrationState != VmRegistrationState.Unregistered)
                            await LoadVmsCommand.ExecuteAsync(null);
                        ShowError($"{Properties.Resources.VmPage_CreateFail}：{DescribeCreationResult(item.Creation)}");
                    }
                    else if (item.StartResult is { Success: false } startResult)
                        ShowError($"{Properties.Resources.VmPage_StartFail}：{FriendlyError.CleanLines(startResult.Error)}");
                    else if (item.Creation.ConfigurationIssues.Count > 0)
                        ShowTip(string.Join(Environment.NewLine,
                            item.Creation.ConfigurationIssues.Select(issue => FriendlyError.CleanLines(issue.Error))));
                }
                else
                {
                    IsCreatingVm = false;
                    await LoadVmsCommand.ExecuteAsync(null);
                    string tail = result.StartFailureCount > 0
                        ? string.Format(Properties.Resources.VmPage_BatchStartFail, result.StartFailureCount) : string.Empty;
                    string details = string.Join(Environment.NewLine + Environment.NewLine,
                        result.Items.Where(item => !item.Creation.Success || item.StartResult is { Success: false }
                                || item.Creation.ConfigurationIssues.Count > 0)
                            .Select(item => $"{item.Name}{Environment.NewLine}" +
                                (!item.Creation.Success ? DescribeCreationResult(item.Creation)
                                    : string.Join(Environment.NewLine,
                                        (item.StartResult is { Success: false } started
                                            ? new[] { FriendlyError.CleanLines(started.Error) } : Array.Empty<string>())
                                        .Concat(item.Creation.ConfigurationIssues.Select(issue => FriendlyError.CleanLines(issue.Error)))))));
                    if (result.CreatedCount == quantity && result.StartFailureCount == 0)
                    {
                        ShowSuccess(string.Format(Properties.Resources.VmPage_BatchAllOk, result.CreatedCount) + tail);
                        if (details.Length > 0) ShowTip(details);
                    }
                    else
                        ShowError(string.Format(Properties.Resources.VmPage_BatchPartial, result.CreatedCount, quantity - result.CreatedCount)
                            + tail + Environment.NewLine + details);
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

        private string GetNextAvailableName(string baseName) =>
            VmCreateService.GetNextAvailableName(baseName, VmList.Select(vm => vm.Name));




    }
}
