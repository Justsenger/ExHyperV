using ExHyperV.Application;
using ExHyperV.UI.Shared.Interaction;
using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.UI.Pages.VirtualMachines;

public partial class VirtualMachinesPageViewModel
{
    [ObservableProperty] private Guid _exportVmId;
    [ObservableProperty] private string _exportVmName = string.Empty;
    [ObservableProperty] private string _exportDestinationPath = string.Empty;
    [ObservableProperty] private bool _exportIncludesVirtualHardDisks = true;
    [ObservableProperty] private bool _exportIncludesCheckpoints;
    [ObservableProperty] private VmExportCheckpointMode _exportCheckpointMode =
        VmExportCheckpointMode.All;
    [ObservableProperty] private VmExportCheckpointItemViewModel? _selectedExportCheckpoint;
    [ObservableProperty] private bool _exportIncludesRuntimeState;
    [ObservableProperty] private bool _exportCreatesPackage;
    [ObservableProperty] private VmExportPackageMode _exportPackageMode = VmExportPackageMode.Store;
    [ObservableProperty] private string _exportPackageFileName = string.Empty;
    [ObservableProperty] private bool _isLoadingExportOptions;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private bool _exportCompleted;
    [ObservableProperty] private int _exportProgress;
    [ObservableProperty] private string _exportStatusText = string.Empty;

    public ObservableCollection<VmExportDiskItemViewModel> ExportVirtualHardDisks { get; } = new();
    public ObservableCollection<VmExportCheckpointItemViewModel> ExportCheckpoints { get; } = new();

    private List<VmInstanceViewModel> _exportVms = new();
    private bool _hasExportCheckpoints;
    private bool _singleCheckpointRequirementsApplied;
    private bool _virtualHardDisksBeforeSingleCheckpoint;
    private bool _runtimeStateBeforeSingleCheckpoint;
    private Dictionary<string, bool>? _virtualHardDiskSelectionsBeforeCheckpoints;

    public bool CanConfigureExport =>
        !IsLoadingExportOptions && !IsExporting && !ExportCompleted;
    public IReadOnlyList<VmInstanceViewModel> ExportTargets => _exportVms;
    public bool HasExportCheckpoints => _hasExportCheckpoints;
    public bool IsMultiVmExport => _exportVms.Count > 1;
    public bool IsSingleCheckpointMode =>
        ExportCheckpointMode == VmExportCheckpointMode.Single;
    public bool IsSingleCheckpointExport =>
        ExportIncludesCheckpoints && ExportCheckpointMode == VmExportCheckpointMode.Single;
    public bool ShowExportVirtualHardDiskSelection =>
        ExportIncludesVirtualHardDisks && !IsSingleCheckpointExport;
    public bool CanConfigureExportVirtualHardDisks =>
        CanConfigureExport && !IsSingleCheckpointExport;
    public bool CanConfigureExportVirtualHardDiskSelection =>
        CanConfigureExport && !ExportIncludesCheckpoints;
    public bool CanConfigureExportRuntimeState =>
        CanConfigureExport && !IsSingleCheckpointExport;
    public bool ShowExportPackageOptions => ExportCreatesPackage;
    public bool ShowExportCheckpointModeSelector =>
        ExportIncludesCheckpoints && !IsMultiVmExport;
    public bool IsCompressedExportPackage => ExportPackageMode == VmExportPackageMode.Compress;
    public bool CanLeaveExport => !IsExporting;
    public bool ShowExportProgress => IsExporting || ExportCompleted;
    public bool CanStartExport => CanConfigureExport
        && !string.IsNullOrWhiteSpace(ExportDestinationPath)
        && (!ExportCreatesPackage || VmExportService.NormalizePackageFileName(ExportPackageFileName) != null)
        && (!IsSingleCheckpointExport || SelectedExportCheckpoint != null);

    partial void OnIsLoadingExportOptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfigureExport));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDisks));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(CanConfigureExportRuntimeState));
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnExportDestinationPathChanged(string value) =>
        OnPropertyChanged(nameof(CanStartExport));

    partial void OnExportIncludesVirtualHardDisksChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDiskSelection));
    }

    partial void OnExportCreatesPackageChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowExportPackageOptions));
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnExportPackageFileNameChanged(string value) =>
        OnPropertyChanged(nameof(CanStartExport));

    partial void OnExportPackageModeChanged(VmExportPackageMode value) =>
        OnPropertyChanged(nameof(IsCompressedExportPackage));

    partial void OnExportProgressChanged(int value)
    {
        if (IsExporting)
            TaskbarProgressService.Report(TaskbarProgressOperation.VmExport, value);
    }

    partial void OnIsExportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfigureExport));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDisks));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(CanConfigureExportRuntimeState));
        OnPropertyChanged(nameof(CanLeaveExport));
        OnPropertyChanged(nameof(ShowExportProgress));
        OnPropertyChanged(nameof(IsVmListEnabled));
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnExportCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfigureExport));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDisks));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(CanConfigureExportRuntimeState));
        OnPropertyChanged(nameof(ShowExportProgress));
        OnPropertyChanged(nameof(CanStartExport));
    }

    partial void OnExportIncludesCheckpointsChanged(bool value)
    {
        if (value)
        {
            _virtualHardDiskSelectionsBeforeCheckpoints = new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase);
            foreach (VmExportDiskItemViewModel disk in ExportVirtualHardDisks)
            {
                _virtualHardDiskSelectionsBeforeCheckpoints[disk.SelectionKey] = disk.IsIncluded;
                disk.IsIncluded = true;
            }
        }
        else if (_virtualHardDiskSelectionsBeforeCheckpoints is { } selections)
        {
            foreach (VmExportDiskItemViewModel disk in ExportVirtualHardDisks)
            {
                if (selections.TryGetValue(disk.SelectionKey, out bool isIncluded))
                    disk.IsIncluded = isIncluded;
            }

            _virtualHardDiskSelectionsBeforeCheckpoints = null;
        }

        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(ShowExportCheckpointModeSelector));
        UpdateSingleCheckpointRequirements();
    }

    partial void OnExportCheckpointModeChanged(VmExportCheckpointMode value)
    {
        OnPropertyChanged(nameof(IsSingleCheckpointMode));
        UpdateSingleCheckpointRequirements();
    }

    partial void OnSelectedExportCheckpointChanged(
        VmExportCheckpointItemViewModel? value) =>
        OnPropertyChanged(nameof(CanStartExport));

    [RelayCommand]
    private async Task GoToExportVmAsync(VmInstanceViewModel vm)
    {
        if (vm == null || IsExporting) return;

        var targets = (IsMultiSelect ? _selectedVms : new List<VmInstanceViewModel> { vm })
            .Where(item => item != null)
            .DistinctBy(item => item.Id)
            .ToList();
        if (targets.Count == 0) return;

        IsLoadingExportOptions = true;
        try
        {
            if (targets.Count == 1 && SelectedVm != vm)
            {
                CurrentViewType = VmDetailViewType.Dashboard;
                SelectedVm = vm;
            }

            _exportVms = targets;
            ExportVmId = targets[0].Id;
            ExportVmName = targets[0].Name;
            OnPropertyChanged(nameof(ExportTargets));
            OnPropertyChanged(nameof(IsMultiVmExport));
            OnPropertyChanged(nameof(ShowExportCheckpointModeSelector));
            ExportDestinationPath = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            ExportIncludesCheckpoints = false;
            ExportCheckpointMode = VmExportCheckpointMode.All;
            ExportIncludesVirtualHardDisks = true;
            ExportIncludesRuntimeState = false;
            ExportCreatesPackage = false;
            ExportPackageMode = VmExportPackageMode.Store;
            ExportPackageFileName = VmExportService.GetDefaultPackageFileName();
            ExportProgress = 0;
            ExportStatusText = string.Empty;
            ExportCompleted = false;
            CurrentViewType = VmDetailViewType.Export;

            ExportVirtualHardDisks.Clear();
            ExportCheckpoints.Clear();
            _hasExportCheckpoints = false;
            SelectedExportCheckpoint = null;
            OnPropertyChanged(nameof(HasExportCheckpoints));
            var exportDisks = new List<VmExportDiskItemViewModel>();
            foreach (VmInstanceViewModel target in targets)
            {
                await VmStoragePresentation.LoadVmStorageItemsAsync(target);
                var disksResult = await VmExportService.GetVirtualHardDisksAsync(target.Id);
                if (!disksResult.Success || !disksResult.HasData)
                {
                    ShowError(FriendlyError.CleanLines(disksResult.Error ?? Properties.Resources.Error_Common_LoadFail));
                    return;
                }

                foreach (VmExportService.VirtualHardDiskInfo diskInfo in
                         disksResult.Data ?? new List<VmExportService.VirtualHardDiskInfo>())
                {
                    string path = diskInfo.Path.Trim('"');
                    var disk = target.Disks.FirstOrDefault(item =>
                        string.Equals(
                            item.Path.Trim('"'),
                            path,
                            StringComparison.OrdinalIgnoreCase));

                    if (disk == null)
                    {
                        long size = 0;
                        try
                        {
                            if (File.Exists(path))
                                size = new FileInfo(path).Length;
                        }
                        catch { }

                        disk = new Models.VmDiskStatus
                        {
                            Name = Path.GetFileName(path),
                            Path = path,
                            CurrentSize = size,
                            MaxSize = size,
                            DiskType = "Virtual"
                        };
                    }

                    var storageItem = target.StorageItems.FirstOrDefault(item =>
                        item.DriveType == "HardDisk"
                        && item.DiskType == "Virtual"
                        && string.Equals(
                            item.PathOrDiskNumber.Trim('"'),
                            path,
                            StringComparison.OrdinalIgnoreCase));

                    exportDisks.Add(new VmExportDiskItemViewModel(
                        target.Id,
                        target.Name,
                        targets.Count > 1,
                        diskInfo,
                        disk,
                        storageItem));
                }

                var checkpointsResult = await VmExportService.GetCheckpointsAsync(target.Id);
                if (!checkpointsResult.Success || !checkpointsResult.HasData)
                {
                    ShowError(FriendlyError.CleanLines(checkpointsResult.Error ?? Properties.Resources.Error_Common_LoadFail));
                    return;
                }

                var checkpoints = checkpointsResult.Data
                    ?? new List<VmExportService.CheckpointInfo>();
                _hasExportCheckpoints |= checkpoints.Count > 0;
                if (targets.Count == 1)
                {
                    foreach (VmExportCheckpointItemViewModel checkpoint in
                             BuildExportCheckpointTree(checkpoints))
                        ExportCheckpoints.Add(checkpoint);
                }
            }

            foreach (VmExportDiskItemViewModel disk in exportDisks
                         .OrderBy(item => item.VmName)
                         .ThenBy(item => item.ControllerType)
                         .ThenBy(item => item.ControllerNumber)
                         .ThenBy(item => item.ControllerLocation))
                ExportVirtualHardDisks.Add(disk);

            SelectedExportCheckpoint = ExportCheckpoints.FirstOrDefault();
            OnPropertyChanged(nameof(HasExportCheckpoints));
        }
        finally
        {
            IsLoadingExportOptions = false;
        }
    }

    [RelayCommand]
    private void BrowseExportFolder()
    {
        if (!CanConfigureExport) return;

        string? selected = Dialogs.PickFolder(
            Properties.Resources.VmExport_SelectDestination,
            string.IsNullOrWhiteSpace(ExportDestinationPath)
                ? null
                : ExportDestinationPath);
        if (!string.IsNullOrWhiteSpace(selected))
            ExportDestinationPath = selected;
    }

    [RelayCommand]
    private void SelectAllExportCheckpoints() =>
        ExportCheckpointMode = VmExportCheckpointMode.All;

    [RelayCommand]
    private void SelectSingleExportCheckpoint()
    {
        if (!IsMultiVmExport)
            ExportCheckpointMode = VmExportCheckpointMode.Single;
    }

    [RelayCommand]
    private void SelectStoredExportPackage() =>
        ExportPackageMode = VmExportPackageMode.Store;

    [RelayCommand]
    private void SelectCompressedExportPackage() =>
        ExportPackageMode = VmExportPackageMode.Compress;

    [RelayCommand]
    private async Task StartExportAsync()
    {
        if (!CanStartExport) return;

        if (_exportVms.Count == 0) return;
        var request = new VmExportBatchRequest(
            _exportVms.Select(target => new VmExportTarget(target.Id, target.Name,
                ExportVirtualHardDisks.Where(disk => disk.VmId == target.Id && !disk.IsIncluded)
                    .Select(disk => disk.Model).ToArray())).ToArray(),
            ExportDestinationPath, ExportIncludesVirtualHardDisks, ExportIncludesCheckpoints,
            ExportCheckpointMode, SelectedExportCheckpoint?.Model, ExportIncludesRuntimeState,
            ExportCreatesPackage, ExportPackageMode, ExportPackageFileName);
        var planned = VmExportService.PlanBatchExport(request);
        if (!planned.Success || !planned.HasData)
        {
            ShowError(planned.Error ?? Properties.Resources.Error_Common_OpFail);
            return;
        }
        var plan = planned.Data!;
        if (plan.PackageFileName != null) ExportPackageFileName = plan.PackageFileName;

        IsExporting = true;
        TaskbarProgressService.Start(TaskbarProgressOperation.VmExport);
        ExportProgress = 0;
        ExportStatusText = Properties.Resources.VmExport_Preparing;
        bool taskbarCompleted = false;
        string? outputPathToReveal = null;

        try
        {
            var progress = new Progress<VmExportBatchProgress>(value =>
            {
                ExportProgress = value.Percentage;
                ExportStatusText = value.PackageStage is { } packageStage
                    ? GetPackageStageStatus(packageStage)
                    : string.Format(Properties.Resources.VmExport_Progress, value.Percentage);
            });
            var result = await VmExportService.ExportBatchAsync(plan, progress);
            if (!result.Success)
            {
                ExportStatusText = string.Format(
                    result.Stage == VmExportBatchStage.Packaging
                        ? Properties.Resources.VmExport_PackageFailed : Properties.Resources.VmExport_Failed,
                    DescribeOperationResult(result.Error, result.CleanupWarning,
                        result.Exports.SelectMany(item => item.RetainedPaths)
                            .Concat(string.IsNullOrWhiteSpace(result.OutputPath) ? [] : new[] { result.OutputPath }),
                        result.OperationStateUnknown));
                ShowError(ExportStatusText);
                return;
            }
            ExportProgress = 100;
            ExportCompleted = true;
            taskbarCompleted = true;
            if (result.CleanupWarning != null)
            {
                ExportStatusText = string.Format(Properties.Resources.VmExport_PackageCleanupWarning,
                    FriendlyError.CleanLines(result.CleanupWarning));
                ShowError(ExportStatusText);
            }
            else
            {
                ExportStatusText = Properties.Resources.VmExport_Completed;
                ShowSuccess(Properties.Resources.VmExport_Completed);
            }
            outputPathToReveal = result.OutputPath;
        }
        catch (Exception ex)
        {
            string error = FriendlyError.CleanLines(ex.Message);
            ExportStatusText = string.Format(Properties.Resources.VmExport_Failed, error);
            ShowError(ExportStatusText);
        }
        finally
        {
            IsExporting = false;
            if (taskbarCompleted)
                TaskbarProgressService.Complete(TaskbarProgressOperation.VmExport);
            else
                TaskbarProgressService.Fail(TaskbarProgressOperation.VmExport);
        }

        // 定位输出只是导出后的界面动作，不能把已完成的导出改报失败。
        if (!string.IsNullOrWhiteSpace(outputPathToReveal))
        {
            var reveal = await Shell.RevealAsync(outputPathToReveal);
            if (!reveal.Success)
                ShowTip($"{Properties.Resources.VmExport_Completed}\n{Properties.Resources.VmPage_OpenFail}：{reveal.Error}");
        }
    }

    [RelayCommand]
    private void CloseExport()
    {
        if (!CanLeaveExport) return;
        CurrentViewType = VmDetailViewType.Dashboard;
    }


    private string GetPackageStageStatus(VmExportPackageStage stage) => stage switch
    {
        VmExportPackageStage.CreatingArchive =>
            ExportPackageMode == VmExportPackageMode.Compress
                ? Properties.Resources.VmExport_PackageCompressing
                : Properties.Resources.VmExport_PackageStoring,
        VmExportPackageStage.ValidatingArchive =>
            Properties.Resources.VmExport_PackageValidating,
        _ => string.Format(Properties.Resources.VmExport_PackageProgress, ExportProgress)
    };

    private void UpdateSingleCheckpointRequirements()
    {
        bool shouldApply = IsSingleCheckpointExport;
        if (shouldApply && !_singleCheckpointRequirementsApplied)
        {
            _virtualHardDisksBeforeSingleCheckpoint = ExportIncludesVirtualHardDisks;
            _runtimeStateBeforeSingleCheckpoint = ExportIncludesRuntimeState;
            _singleCheckpointRequirementsApplied = true;

            ExportIncludesVirtualHardDisks = true;
            ExportIncludesRuntimeState = true;
            foreach (VmExportDiskItemViewModel disk in ExportVirtualHardDisks)
                disk.IsIncluded = true;

            SelectedExportCheckpoint ??= ExportCheckpoints.FirstOrDefault();
        }
        else if (!shouldApply && _singleCheckpointRequirementsApplied)
        {
            _singleCheckpointRequirementsApplied = false;
            ExportIncludesVirtualHardDisks = _virtualHardDisksBeforeSingleCheckpoint;
            ExportIncludesRuntimeState = _runtimeStateBeforeSingleCheckpoint;
        }

        OnPropertyChanged(nameof(IsSingleCheckpointExport));
        OnPropertyChanged(nameof(ShowExportVirtualHardDiskSelection));
        OnPropertyChanged(nameof(CanConfigureExportVirtualHardDisks));
        OnPropertyChanged(nameof(CanConfigureExportRuntimeState));
        OnPropertyChanged(nameof(CanStartExport));
    }

    private static IReadOnlyList<VmExportCheckpointItemViewModel> BuildExportCheckpointTree(
        IEnumerable<VmExportService.CheckpointInfo> checkpoints)
    {
        var items = checkpoints
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<VmExportService.CheckpointInfo>>(
            StringComparer.OrdinalIgnoreCase);
        var roots = new List<VmExportService.CheckpointInfo>();

        foreach (VmExportService.CheckpointInfo item in items.Values)
        {
            if (string.IsNullOrWhiteSpace(item.ParentId)
                || !items.ContainsKey(item.ParentId))
            {
                roots.Add(item);
                continue;
            }

            if (!children.TryGetValue(item.ParentId, out var siblings))
            {
                siblings = new List<VmExportService.CheckpointInfo>();
                children[item.ParentId] = siblings;
            }
            siblings.Add(item);
        }

        static IOrderedEnumerable<VmExportService.CheckpointInfo> Sort(
            IEnumerable<VmExportService.CheckpointInfo> source) =>
            source.OrderBy(item => item.CreatedDate).ThenBy(item => item.Name);

        var result = new List<VmExportCheckpointItemViewModel>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Append(
            VmExportService.CheckpointInfo item,
            string ancestorPrefix,
            bool isRoot)
        {
            if (!visited.Add(item.Id)) return;

            string treePrefix = isRoot
                ? string.Empty
                : ancestorPrefix + "└─ ";
            result.Add(new VmExportCheckpointItemViewModel(item, treePrefix));

            if (!children.TryGetValue(item.Id, out var childItems)) return;

            var orderedChildren = Sort(childItems).ToList();
            string nextPrefix = isRoot
                ? string.Empty
                : ancestorPrefix + "   ";
            for (int index = 0; index < orderedChildren.Count; index++)
                Append(
                    orderedChildren[index],
                    nextPrefix,
                    false);
        }

        var orderedRoots = Sort(roots).ToList();
        for (int index = 0; index < orderedRoots.Count; index++)
            Append(orderedRoots[index], string.Empty, true);

        foreach (VmExportService.CheckpointInfo unvisited in Sort(
                     items.Values.Where(item => !visited.Contains(item.Id))))
            Append(unvisited, string.Empty, true);

        return result;
    }
}
